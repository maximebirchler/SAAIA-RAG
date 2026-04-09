using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Bootstrap;

sealed class IngestionScanner : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<IngestionScanner> _log;

    // Anti “wipe transitoire”
    private int _emptyScanStreak = 0;

    public IngestionScanner(IServiceProvider sp, ILogger<IngestionScanner> log)
    {
        _sp = sp;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            IngestionOptions opt;
            BootstrapOptions bootstrap;
            RagOptions rag;
            NpgsqlDataSource ds;
            IHttpClientFactory httpFactory;

            using (var scope = _sp.CreateScope())
            {
                opt = scope.ServiceProvider.GetRequiredService<IOptions<IngestionOptions>>().Value;
                bootstrap = scope.ServiceProvider.GetRequiredService<IOptions<BootstrapOptions>>().Value;
                rag = scope.ServiceProvider.GetRequiredService<IOptions<RagOptions>>().Value;
                ds = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
                httpFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
            }

            if (!opt.ScannerEnabled)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                continue;
            }

            try
            {
                await ScanOnceAsync(ds, httpFactory, rag, opt, bootstrap, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "Scanner error");
            }

            var delay = TimeSpan.FromSeconds(Math.Clamp(opt.ScanIntervalSeconds, 2, 3600));
            await Task.Delay(delay, ct);
        }
    }

    private async Task ScanOnceAsync(
        NpgsqlDataSource ds,
        IHttpClientFactory httpFactory,
        RagOptions rag,
        IngestionOptions opt,
        BootstrapOptions bootstrap,
        CancellationToken ct)
    {
        var root = opt.DocumentsRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            _log.LogWarning("Scanner: DocumentsRoot is empty");
            return;
        }

        if (!Directory.Exists(root))
        {
            _log.LogWarning("Scanner: DocumentsRoot does not exist: {Root}", root);
            return;
        }

        var tenantId = await ResolveSingleTenantIdAsync(ds, bootstrap, ct);

        await using var conn = await ds.OpenConnectionAsync(ct);

        // 0) stale running -> failed
        var staleRunningAfter = TimeSpan.FromMinutes(Math.Clamp(opt.StaleRunningMinutes, 5, 24 * 60));
        await MarkStaleRunningJobsFailedAsync(conn, tenantId, staleRunningAfter, ct);

        // 1) list files
        var now = DateTime.UtcNow;
        var missingGrace = TimeSpan.FromSeconds(Math.Clamp(opt.MissingGraceSeconds, 5, 24 * 3600));
        var maxFiles = Math.Clamp(opt.MaxFilesPerScan, 1, 200000);

        var files = Directory.EnumerateFiles(root, "*.pdf", SearchOption.AllDirectories)
            .Take(maxFiles)
            .ToList();

        // Anti wipe: delete only after 2 empty scans
        if (files.Count == 0)
        {
            _emptyScanStreak++;
            _log.LogWarning("Scanner: 0 file found (streak={Streak})", _emptyScanStreak);
            if (_emptyScanStreak < 2)
                return;
        }
        else
        {
            _emptyScanStreak = 0;
        }

        // 3) load known docs
        const string loadSql = @"
SELECT
  doc_path                           AS ""DocPath"",
  file_size                          AS ""FileSize"",
  file_mtime                         AS ""FileMtime"",
  status                             AS ""Status"",
  missing_since                      AS ""MissingSince"",
  COALESCE(indexed_version, 0)       AS ""IndexedVersion"",
  COALESCE(auto_ingest_paused, false) AS ""AutoIngestPaused"",
  auto_ingest_pause_reason           AS ""AutoIngestPauseReason"",
  auto_ingest_paused_at              AS ""AutoIngestPausedAt""
FROM documents
WHERE tenant_id = @tenant_id
  AND status <> 'deleted';";

        var existing = (await conn.QueryAsync<DocRow>(loadSql, new { tenant_id = tenantId }))
            .ToDictionary(x => x.DocPath, StringComparer.OrdinalIgnoreCase);

        // 3.1) Auto-heal: if Qdrant is empty for this tenant but DB has docs, force reindex.
        bool forceReindexAll = false;
        if (opt.ReindexIfQdrantEmpty && existing.Count > 0 && files.Count > 0)
        {
            var count = await TryGetTenantPointCountAsync(httpFactory, rag, tenantId, ct);

            // count == 0 => collection missing OR empty for this tenant
            if (count == 0)
            {
                forceReindexAll = true;
                _log.LogWarning(
                    "Scanner: Qdrant has 0 points for tenant {TenantId} while DB has {Docs} documents. Forcing reindex of all seen PDFs.",
                    tenantId, existing.Count
                );
            }
            else if (count is null)
            {
                // Qdrant unreachable / error => do NOT force reindex (avoid storms)
                _log.LogWarning("Scanner: cannot check Qdrant point count (skipping auto-heal this cycle).");
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int enqUpsert = 0, enqDelete = 0, skippedTooFresh = 0, unchanged = 0, suppressedAuto = 0;
        bool anyTooFresh = false;

        // 4) Upserts
        foreach (var file in files)
        {
            FileInfo fi;
            try { fi = new FileInfo(file); }
            catch { continue; }

            var rel = PathUtil.NormalizeRelativePath(
                Path.GetRelativePath(root, file).Replace('\\', '/')
            );

            if (IngestionPathFilter.ShouldIgnoreRel(rel))
                continue;

            // mark seen even if too fresh
            seen.Add(rel);

            // avoid in-progress copy
            var ageSeconds = (now - fi.LastWriteTimeUtc).TotalSeconds;
            if (ageSeconds < Math.Max(0, opt.MinFileAgeSeconds))
            {
                skippedTooFresh++;
                anyTooFresh = true;
                continue;
            }

            var category = DeriveCategory(rel, opt);

            if (!existing.TryGetValue(rel, out var row))
            {
                await IngestionEnqueue.EnqueueUpsertAsync(conn, tenantId, rel, category, fi, ct, isAutomatic: true, enqueueSource: "scanner");
                enqUpsert++;
                continue;
            }

            var changed =
                (row.FileSize ?? -1) != fi.Length ||
                !SameMtime(row.FileMtime, fi.LastWriteTimeUtc);

            var normalizedStatus = (row.Status ?? string.Empty).Trim().ToLowerInvariant();
            var hasActiveJob = await HasActiveJobForDocAsync(conn, tenantId, rel, ct);
            var autoSuppressed = !hasActiveJob
                && IngestionAutoUpsertGuard.ShouldSuppressAutoUpsert(row.ToAutoUpsertState(), fi.Length, fi.LastWriteTimeUtc);

            var pendingOrBrokenWithoutJob =
                (normalizedStatus is "pending" or "new" or "error")
                && !hasActiveJob
                && !autoSuppressed;

            var needsReindex =
                ((!hasActiveJob && forceReindexAll) ||
                 (!hasActiveJob && changed) ||
                 (!hasActiveJob && string.Equals(normalizedStatus, "missing", StringComparison.OrdinalIgnoreCase)) ||
                 pendingOrBrokenWithoutJob)
                && !autoSuppressed;

            if (autoSuppressed)
            {
                suppressedAuto++;
                _log.LogInformation(
                    "Scanner: auto-upsert suppressed after admin cancel for {DocPath} (status={Status}, indexed_version={IndexedVersion}, paused_at={PausedAt})",
                    rel, row.Status, row.IndexedVersion, row.AutoIngestPausedAt);
            }
            else if (needsReindex)
            {
                var failedBackoff = await GetRecentFailureBackoffStateAsync(conn, tenantId, rel, opt, ct);
                if (!changed && failedBackoff.ShouldBackoff)
                {
                    suppressedAuto++;
                    _log.LogWarning(
                        "Scanner: auto-upsert backoff for {DocPath} after {FailureStreak} consecutive failures (last_failed_at={LastFailedAt:u}, backoff={BackoffSeconds}s)",
                        rel,
                        failedBackoff.FailureStreak,
                        failedBackoff.LastFailedAtUtc,
                        failedBackoff.BackoffSeconds);
                    continue;
                }

                // Fresh re-check: close stale batch-load race where admin cancel
                // set auto_ingest_paused=true after the batch query ran
                if (await IsFreshAutoIngestPausedAsync(conn, tenantId, rel, ct))
                {
                    suppressedAuto++;
                    _log.LogInformation(
                        "Scanner: auto-upsert suppressed (fresh re-check after admin cancel) for {DocPath} (batch had paused={BatchPaused})",
                        rel, row.AutoIngestPaused);
                }
                else
                {
                    _log.LogInformation(
                        "Scanner: enqueue upsert for {DocPath} (status={Status}, changed={Changed}, hasActiveJob={HasActiveJob}, pendingOrBroken={PendingOrBroken}, forceReindex={Force})",
                        rel, row.Status, changed, hasActiveJob, pendingOrBrokenWithoutJob, forceReindexAll);
                    await IngestionEnqueue.EnqueueUpsertAsync(conn, tenantId, rel, category, fi, ct, isAutomatic: true, enqueueSource: "scanner");
                    enqUpsert++;
                }
            }
            else
            {
                unchanged++;
            }
        }

        // 5) Deletions
        // M1.2b: lors d'un copy/move, certains fichiers peuvent être "too fresh".
        // On doit quand même marquer les documents absents en "missing" pour éviter les doublons/fantômes
        // dans l'inventaire user. En revanche, on diffère l'enqueue des deletes définitifs tant que
        // l'on détecte un copy/move en cours (anyTooFresh=true).
        foreach (var kv in existing)
        {
            if (files.Count == 0 || !seen.Contains(kv.Key))
            {
                // 5.1) Marquer missing (même si anyTooFresh=true)
                if (!string.Equals(kv.Value.Status, "missing", StringComparison.OrdinalIgnoreCase))
                {
                    await IngestionEnqueue.MarkMissingAsync(conn, tenantId, kv.Key, ct);
                    kv.Value.Status = "missing";
                    kv.Value.MissingSince = now;
                    continue;
                }

                // 5.2) Robustesse legacy: missing mais missing_since null => fixer missing_since
                if (kv.Value.MissingSince is null)
                {
                    await IngestionEnqueue.MarkMissingAsync(conn, tenantId, kv.Key, ct);
                    kv.Value.MissingSince = now;
                }

                // 5.3) Delete définitif uniquement si aucun fichier trop frais (sinon on attend un prochain scan)
                if (anyTooFresh)
                    continue;

                var missingSinceUtc = kv.Value.MissingSince!.Value.Kind == DateTimeKind.Utc
                    ? kv.Value.MissingSince!.Value
                    : kv.Value.MissingSince!.Value.ToUniversalTime();

                if ((now - missingSinceUtc) >= missingGrace)
                {
                    await IngestionEnqueue.EnqueueDeleteAsync(conn, tenantId, kv.Key, ct);
                    enqDelete++;
                }
            }
        }

        if (anyTooFresh)
        {
            _log.LogWarning("Scanner: some files are too fresh (copy/move in progress) => missing is marked, but final deletes are deferred");
        }

        _log.LogInformation(
            "Scanner: files={Files} unchanged={Unchanged} upsert_enqueued={Upserts} delete_enqueued={Deletes} skipped_too_fresh={TooFresh} suppressed_auto={Suppressed} force_reindex={Force}",
            files.Count, unchanged, enqUpsert, enqDelete, skippedTooFresh, suppressedAuto, forceReindexAll
        );
    }

    private static async Task<int?> TryGetTenantPointCountAsync(
        IHttpClientFactory httpFactory,
        RagOptions rag,
        Guid tenantId,
        CancellationToken ct)
    {
        try
        {
            var qdrant = httpFactory.CreateClient("qdrant");
            qdrant.BaseAddress = new Uri(rag.QdrantBaseUrl);

            var body = new
            {
                filter = new
                {
                    must = new object[]
                    {
                        new { key = "tenant_id", match = new { value = tenantId.ToString() } }
                    }
                },
                exact = false
            };

            using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var resp = await qdrant.PostAsync($"/collections/{rag.QdrantCollection}/points/count", content, ct);

            if (resp.StatusCode == HttpStatusCode.NotFound)
                return 0;

            if (!resp.IsSuccessStatusCode)
                return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (doc.RootElement.TryGetProperty("result", out var result) &&
                result.ValueKind == JsonValueKind.Object &&
                result.TryGetProperty("count", out var countEl) &&
                countEl.ValueKind == JsonValueKind.Number)
            {
                return countEl.GetInt32();
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool SameMtime(DateTime? dbMtime, DateTime fsMtimeUtc)
    {
        if (dbMtime is null) return false;

        DateTime dbUtc;
        if (dbMtime.Value.Kind == DateTimeKind.Utc) dbUtc = dbMtime.Value;
        else if (dbMtime.Value.Kind == DateTimeKind.Local) dbUtc = dbMtime.Value.ToUniversalTime();
        else dbUtc = DateTime.SpecifyKind(dbMtime.Value, DateTimeKind.Utc);

        var fsUtc = DateTime.SpecifyKind(fsMtimeUtc, DateTimeKind.Utc);

        return Math.Abs((dbUtc - fsUtc).TotalSeconds) <= 2.0;
    }

    private static async Task<bool> IsFreshAutoIngestPausedAsync(NpgsqlConnection conn, Guid tenantId, string docPath, CancellationToken ct)
    {
        const string sql = @"
SELECT COALESCE(auto_ingest_paused, false)
FROM documents
WHERE tenant_id=@tenant_id AND doc_path=@doc_path
LIMIT 1;";

        return await conn.ExecuteScalarAsync<bool>(
            new CommandDefinition(sql, new { tenant_id = tenantId, doc_path = docPath }, cancellationToken: ct));
    }

    private static async Task<bool> HasActiveJobForDocAsync(NpgsqlConnection conn, Guid tenantId, string docPath, CancellationToken ct)
    {
        const string sql = @"
SELECT 1
FROM ingestion_jobs
WHERE tenant_id=@tenant_id
  AND doc_path=@doc_path
  AND status IN ('queued','running','paused')
LIMIT 1;";

        var exists = await conn.ExecuteScalarAsync<int?>(
            new CommandDefinition(sql, new { tenant_id = tenantId, doc_path = docPath }, cancellationToken: ct)
        );

        return exists.HasValue;
    }

    private static async Task<AutoRetryBackoffState> GetRecentFailureBackoffStateAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string docPath,
        IngestionOptions opt,
        CancellationToken ct)
    {
        var backoffSeconds = Math.Clamp(opt.AutoRetryBackoffSeconds, 0, 24 * 3600);
        var streak = Math.Clamp(opt.AutoRetryFailureStreak, 1, 20);

        const string sql = """
SELECT
  COUNT(*)::int AS "Total",
  COUNT(*) FILTER (WHERE lower(status)='failed')::int AS "Failed",
  MAX(finished_at) FILTER (WHERE lower(status)='failed') AS "LastFailedAtUtc"
FROM (
  SELECT status, finished_at
  FROM ingestion_jobs
  WHERE tenant_id=@tenant_id
    AND doc_path=@doc_path
    AND action='upsert'
    AND status IN ('failed','done','canceled','cancelled')
  ORDER BY created_at DESC
  LIMIT @streak
) x;
""";

        var row = await conn.QueryFirstOrDefaultAsync<AutoRetryBackoffStateRow>(
            new CommandDefinition(sql, new
            {
                tenant_id = tenantId,
                doc_path = docPath,
                streak
            }, cancellationToken: ct));

        if (row is null || row.Total < streak || row.Failed < streak || row.LastFailedAtUtc is null)
            return new AutoRetryBackoffState(false, row?.Failed ?? 0, null, backoffSeconds);

        var lastFailedUtc = DateTime.SpecifyKind(row.LastFailedAtUtc.Value, DateTimeKind.Utc);
        var elapsed = DateTime.UtcNow - lastFailedUtc;
        var shouldBackoff = backoffSeconds > 0 && elapsed.TotalSeconds < backoffSeconds;
        return new AutoRetryBackoffState(shouldBackoff, row.Failed, lastFailedUtc, backoffSeconds);
    }

    private static async Task MarkStaleRunningJobsFailedAsync(NpgsqlConnection conn, Guid tenantId, TimeSpan staleRunningAfter, CancellationToken ct)
    {
        const string sql = @"
UPDATE ingestion_jobs j
SET status = CASE
        WHEN COALESCE((j.payload #>> '{control,cancelRequested}')::boolean, false)
             AND j.action='upsert'
             AND COALESCE(d.indexed_version, 0) <= 0
             AND COALESCE(d.status, '') NOT IN ('missing','deleted')
             AND COALESCE(d.auto_ingest_paused, false)
             AND COALESCE(d.auto_ingest_pause_reason, '') = 'admin_cancel'
            THEN 'paused'
        WHEN COALESCE((j.payload #>> '{control,cancelRequested}')::boolean, false) THEN 'canceled'
        ELSE 'failed'
    END,
    locked_at=NULL,
    locked_by=NULL,
    finished_at=CASE
        WHEN COALESCE((j.payload #>> '{control,cancelRequested}')::boolean, false)
             AND j.action='upsert'
             AND COALESCE(d.indexed_version, 0) <= 0
             AND COALESCE(d.status, '') NOT IN ('missing','deleted')
             AND COALESCE(d.auto_ingest_paused, false)
             AND COALESCE(d.auto_ingest_pause_reason, '') = 'admin_cancel'
            THEN NULL
        ELSE now()
    END,
    started_at=CASE
        WHEN COALESCE((j.payload #>> '{control,cancelRequested}')::boolean, false)
             AND j.action='upsert'
             AND COALESCE(d.indexed_version, 0) <= 0
             AND COALESCE(d.status, '') NOT IN ('missing','deleted')
             AND COALESCE(d.auto_ingest_paused, false)
             AND COALESCE(d.auto_ingest_pause_reason, '') = 'admin_cancel'
            THEN NULL
        ELSE j.started_at
    END,
    last_error = CASE
        WHEN COALESCE((j.payload #>> '{control,cancelRequested}')::boolean, false)
             AND j.action='upsert'
             AND COALESCE(d.indexed_version, 0) <= 0
             AND COALESCE(d.auto_ingest_paused, false)
             AND COALESCE(d.auto_ingest_pause_reason, '') = 'admin_cancel'
            THEN NULL
        WHEN COALESCE((j.payload #>> '{control,cancelRequested}')::boolean, false)
            THEN COALESCE(j.last_error,'canceled_stale_scanner')
        ELSE COALESCE(j.last_error,'stale_running_scanner')
    END,
    payload = CASE
        WHEN COALESCE((j.payload #>> '{control,cancelRequested}')::boolean, false)
             AND j.action='upsert'
             AND COALESCE(d.indexed_version, 0) <= 0
             AND COALESCE(d.status, '') NOT IN ('missing','deleted')
             AND COALESCE(d.auto_ingest_paused, false)
             AND COALESCE(d.auto_ingest_pause_reason, '') = 'admin_cancel'
            THEN (COALESCE(j.payload, '{}'::jsonb) #- '{control,cancelRequested}')
        ELSE COALESCE(j.payload, '{}'::jsonb)
    END
FROM documents d
WHERE j.tenant_id=@tenant_id
  AND j.tenant_id=d.tenant_id
  AND j.doc_path=d.doc_path
  AND j.status='running'
  AND j.locked_at IS NOT NULL
  AND j.locked_at < (now() - @stale);";

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            tenant_id = tenantId,
            stale = staleRunningAfter
        }, cancellationToken: ct));

        const string fallbackSql = @"
UPDATE ingestion_jobs
SET status = CASE
        WHEN COALESCE((payload #>> '{control,cancelRequested}')::boolean, false) THEN 'canceled'
        ELSE 'failed'
    END,
    locked_at=NULL,
    locked_by=NULL,
    finished_at=now(),
    last_error = CASE
        WHEN COALESCE((payload #>> '{control,cancelRequested}')::boolean, false)
            THEN COALESCE(last_error,'canceled_stale_scanner')
        ELSE COALESCE(last_error,'stale_running_scanner')
    END
WHERE tenant_id=@tenant_id
  AND status='running'
  AND locked_at IS NOT NULL
  AND locked_at < (now() - @stale)
  AND NOT EXISTS (
      SELECT 1
      FROM documents d
      WHERE d.tenant_id=ingestion_jobs.tenant_id
        AND d.doc_path=ingestion_jobs.doc_path
  );";

        await conn.ExecuteAsync(new CommandDefinition(fallbackSql, new
        {
            tenant_id = tenantId,
            stale = staleRunningAfter
        }, cancellationToken: ct));
    }

    private static string DeriveCategory(string docPath, IngestionOptions opt)
    {
        if (!opt.CategoryFromFirstFolder)
            return (opt.DefaultCategory ?? "general").Trim().ToLowerInvariant();

        var parts = docPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
            return parts[0].Trim().ToLowerInvariant();

        return (opt.DefaultCategory ?? "general").Trim().ToLowerInvariant();
    }

    private static async Task<Guid> ResolveSingleTenantIdAsync(NpgsqlDataSource ds, BootstrapOptions bootstrap, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);

        if (bootstrap.TenantId != Guid.Empty)
        {
            var exists = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT 1 FROM tenants WHERE tenant_id=@id LIMIT 1;",
                new { id = bootstrap.TenantId },
                cancellationToken: ct));

            if (exists.HasValue)
                return bootstrap.TenantId;
        }

        var tid = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT tenant_id FROM tenants WHERE is_active=true ORDER BY created_at ASC LIMIT 1;",
            cancellationToken: ct));

        if (tid is null || tid == Guid.Empty)
            throw new Exception("No tenant found. Enable Bootstrap or create a tenant in DB.");

        return tid.Value;
    }

    private sealed class DocRow
    {
        public string DocPath { get; set; } = "";
        public long? FileSize { get; set; }
        public DateTime? FileMtime { get; set; }
        public string Status { get; set; } = "";
        public DateTime? MissingSince { get; set; }
        public int IndexedVersion { get; set; }
        public bool AutoIngestPaused { get; set; }
        public string? AutoIngestPauseReason { get; set; }
        public DateTime? AutoIngestPausedAt { get; set; }

        public IngestionAutoUpsertGuard.AutoUpsertState ToAutoUpsertState() => new()
        {
            DocPath = DocPath,
            FileSize = FileSize,
            FileMtime = FileMtime,
            Status = Status,
            IndexedVersion = IndexedVersion,
            AutoIngestPaused = AutoIngestPaused,
            AutoIngestPauseReason = AutoIngestPauseReason,
            AutoIngestPausedAt = AutoIngestPausedAt
        };
    }

    private sealed record AutoRetryBackoffStateRow(int Total, int Failed, DateTime? LastFailedAtUtc);
    private sealed record AutoRetryBackoffState(bool ShouldBackoff, int FailureStreak, DateTime? LastFailedAtUtc, int BackoffSeconds);
}
