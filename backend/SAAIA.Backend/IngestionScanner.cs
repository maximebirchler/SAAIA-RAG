using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            NpgsqlDataSource ds;

            using (var scope = _sp.CreateScope())
            {
                opt = scope.ServiceProvider.GetRequiredService<IOptions<IngestionOptions>>().Value;
                bootstrap = scope.ServiceProvider.GetRequiredService<IOptions<BootstrapOptions>>().Value;
                ds = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
            }

            if (!opt.ScannerEnabled)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                continue;
            }

            try
            {
                await ScanOnceAsync(ds, opt, bootstrap, ct);
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

    private async Task ScanOnceAsync(NpgsqlDataSource ds, IngestionOptions opt, BootstrapOptions bootstrap, CancellationToken ct)
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

        // 0) Si un job "running" est bloqué depuis trop longtemps, on le fail et on libère
        var staleRunningAfter = TimeSpan.FromMinutes(Math.Clamp(opt.StaleRunningMinutes, 5, 24 * 60));
        await MarkStaleRunningJobsFailedAsync(conn, tenantId, staleRunningAfter, ct);

        // 1) Si un job est EN COURS (running), on stoppe le scan (comme souhaité)
        if (await HasRunningJobAsync(conn, tenantId, ct))
        {
            _log.LogInformation("Scanner: running ingestion job detected => skipping scan this cycle");
            return;
        }

        // 2) Liste fichiers sur disque
        var now = DateTime.UtcNow;
        var missingGrace = TimeSpan.FromSeconds(Math.Clamp(opt.MissingGraceSeconds, 5, 24 * 3600));
        var maxFiles = Math.Clamp(opt.MaxFilesPerScan, 1, 200000);

        var files = Directory.EnumerateFiles(root, "*.pdf", SearchOption.AllDirectories)
            .Take(maxFiles)
            .ToList();

        // Anti wipe: delete seulement après 2 scans vides
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

        // 3) Charge docs connus
        const string loadSql = @"
SELECT
  doc_path      AS ""DocPath"",
  file_size     AS ""FileSize"",
  file_mtime    AS ""FileMtime"",
  status        AS ""Status"",
  missing_since AS ""MissingSince""
FROM documents
WHERE tenant_id = @tenant_id
  AND status <> 'deleted';";

        var existing = (await conn.QueryAsync<DocRow>(loadSql, new { tenant_id = tenantId }))
            .ToDictionary(x => x.DocPath, StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int enqUpsert = 0, enqDelete = 0, skippedTooFresh = 0, unchanged = 0;
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

            // IMPORTANT: on marque "seen" même si le fichier est trop frais (copy en cours)
            seen.Add(rel);

            // Evite de traiter un fichier encore en cours de copie
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
                await IngestionEnqueue.EnqueueUpsertAsync(conn, tenantId, rel, category, fi, ct);
                enqUpsert++;
                continue;
            }

            var changed =
                (row.FileSize ?? -1) != fi.Length ||
                !SameMtime(row.FileMtime, fi.LastWriteTimeUtc);

            // Re-index si: contenu changé OU doc était "missing" (il est revenu)
            var needsReindex =
                changed ||
                string.Equals(row.Status, "missing", StringComparison.OrdinalIgnoreCase);

            if (needsReindex)
            {
                await IngestionEnqueue.EnqueueUpsertAsync(conn, tenantId, rel, category, fi, ct);
                enqUpsert++;
            }
            else
            {
                unchanged++;
            }
        }

        // 5) Deletions
        // IMPORTANT: si on détecte des fichiers "too fresh" (copy/move en cours),
        // on NE FAIT PAS de deletions à ce cycle.
        if (!anyTooFresh)
        {
            foreach (var kv in existing)
            {
                // si dossier vide (streak>=2) => on supprime tout
                // sinon => on supprime ce qui n'est plus vu
                if (files.Count == 0 || !seen.Contains(kv.Key))
                {
                    // 1) Première détection "manquant" => on marque le doc missing et on attend MissingGrace
                    if (!string.Equals(kv.Value.Status, "missing", StringComparison.OrdinalIgnoreCase))
                    {
                        await IngestionEnqueue.MarkMissingAsync(conn, tenantId, kv.Key, ct);
                        kv.Value.Status = "missing";
                        kv.Value.MissingSince = now;
                        continue;
                    }

                    // 2) Si le doc est missing depuis suffisamment longtemps => enqueue delete
                    var missingSince = kv.Value.MissingSince;

                    // Legacy safety: si missing_since est NULL, on le fixe maintenant
                    if (missingSince is null)
                    {
                        await IngestionEnqueue.MarkMissingAsync(conn, tenantId, kv.Key, ct);
                        missingSince = now;
                    }

                    var missingSinceUtc = missingSince.Value.Kind == DateTimeKind.Utc
                        ? missingSince.Value
                        : missingSince.Value.ToUniversalTime();

                    if ((now - missingSinceUtc) >= missingGrace)
                    {
                        await IngestionEnqueue.EnqueueDeleteAsync(conn, tenantId, kv.Key, ct);
                        enqDelete++;
                    }
                }
            }
        }
        else
        {
            _log.LogWarning("Scanner: skipped deletions because some files are too fresh (copy/move in progress)");
        }

        _log.LogInformation(
            "Scanner: files={Files} unchanged={Unchanged} upsert_enqueued={Upserts} delete_enqueued={Deletes} skipped_too_fresh={TooFresh}",
            files.Count, unchanged, enqUpsert, enqDelete, skippedTooFresh
        );

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

    private static async Task<bool> HasRunningJobAsync(NpgsqlConnection conn, Guid tenantId, CancellationToken ct)
    {
        const string sql = @"
SELECT 1
FROM ingestion_jobs
WHERE tenant_id=@tenant_id
  AND status='running'
LIMIT 1;";

        var exists = await conn.ExecuteScalarAsync<int?>(
            new CommandDefinition(sql, new { tenant_id = tenantId }, cancellationToken: ct)
        );

        return exists.HasValue;
    }

    private static async Task MarkStaleRunningJobsFailedAsync(NpgsqlConnection conn, Guid tenantId, TimeSpan staleRunningAfter, CancellationToken ct)
    {
        const string sql = @"
UPDATE ingestion_jobs
SET status='failed',
    locked_at=NULL,
    locked_by=NULL,
    finished_at=now(),
    last_error=COALESCE(last_error,'stale_running_scanner')
WHERE tenant_id=@tenant_id
  AND status='running'
  AND locked_at IS NOT NULL
  AND locked_at < (now() - @stale);";

        await conn.ExecuteAsync(new CommandDefinition(sql, new
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

        // 1) Si un tenant est fourni via config, ne l'accepter QUE s'il existe réellement.
        //    (Sinon: FK violations sur documents/api_keys, et Qdrant peut paraître "vide" car filtré par tenant.)
        if (bootstrap.TenantId != Guid.Empty)
        {
            var exists = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT 1 FROM tenants WHERE tenant_id=@id LIMIT 1;",
                new { id = bootstrap.TenantId },
                cancellationToken: ct));

            if (exists.HasValue)
                return bootstrap.TenantId;
        }

        // 2) Fallback: premier tenant actif (profil single-tenant dev/prod).
        var tid = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT tenant_id FROM tenants WHERE is_active=true ORDER BY created_at ASC LIMIT 1;",
            cancellationToken: ct));

        if (tid is null || tid == Guid.Empty)
            throw new Exception("No tenant found. Enable Bootstrap or create a tenant in DB.");

        return tid.Value;
    }


    // Dapper: classe simple (évite les erreurs de ctor record)
    private sealed class DocRow
    {
        public string DocPath { get; set; } = "";
        public long? FileSize { get; set; }
        public DateTime? FileMtime { get; set; }
        public string Status { get; set; } = "";
        public DateTime? MissingSince { get; set; }
    }
}
