using Dapper;
using Npgsql;

static partial class JobRepo
{
    public static async Task<bool> CompleteUpsertAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        Guid jobId,
        string docPath,
        byte[] hash,
        long size,
        DateTime mtimeUtc,
        int version,
        IReadOnlyList<ExtractedPdfPage> pages,
        IReadOnlyList<ExtractedDocumentSection> sections,
        IReadOnlyList<ExtractedDocumentUnit> units,
        IReadOnlyList<ProjectedRetrievalChunk> retrievalChunks,
        IReadOnlyList<ExtractedExactMatchEntry> exactMatchEntries,
        IReadOnlyList<ProjectedContextualTextEntry> contextualTextEntries,
        CancellationToken ct,
        string extractionSource = "pdf_text",
        bool ocrAttempted = false,
        bool ocrApplied = false,
        string? ocrLanguages = null,
        long? ocrDurationMs = null,
        PdfOcrDiagnostics? ocrDiagnostics = null,
        PdfExtractionQualitySummary? nativeExtractionQuality = null,
        IngestionCapabilityAProfileSeed? capabilityAProfileSeed = null)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        const string lockSql = """
SELECT
    status AS "Status",
    priority AS "Priority",
    (LOWER(COALESCE(payload #>> '{control,cancelRequested}', '')) = 'true') AS "CancelRequested"
FROM ingestion_jobs
WHERE job_id=@job_id
FOR UPDATE;
""";

        var row = await conn.QueryFirstOrDefaultAsync<JobCancelState>(new CommandDefinition(lockSql, new { job_id = jobId }, transaction: tx, cancellationToken: ct));
        if (row is null)
        {
            await tx.CommitAsync(ct);
            return false;
        }

        if (!string.Equals(row.Status, "running", StringComparison.OrdinalIgnoreCase) || row.CancelRequested)
        {
            const string cancelSql = @"UPDATE ingestion_jobs
SET status=CASE
      WHEN EXISTS (
          SELECT 1
          FROM documents d
          WHERE d.tenant_id=@tenant_id
            AND d.doc_path=@doc_path
            AND COALESCE(d.indexed_version, 0) <= 0
            AND COALESCE(d.status, '') NOT IN ('missing','deleted')
            AND (
                COALESCE(payload #>> '{control,requestedAction}', '') = 'pause'
                OR (
                    COALESCE(d.auto_ingest_paused, false)
                    AND COALESCE(d.auto_ingest_pause_reason, '') = 'admin_cancel'
                )
            )
      ) THEN 'paused'
      ELSE 'canceled'
    END,
    finished_at=CASE
      WHEN EXISTS (
          SELECT 1
          FROM documents d
          WHERE d.tenant_id=@tenant_id
            AND d.doc_path=@doc_path
            AND COALESCE(d.indexed_version, 0) <= 0
            AND COALESCE(d.status, '') NOT IN ('missing','deleted')
            AND (
                COALESCE(payload #>> '{control,requestedAction}', '') = 'pause'
                OR (
                    COALESCE(d.auto_ingest_paused, false)
                    AND COALESCE(d.auto_ingest_pause_reason, '') = 'admin_cancel'
                )
            )
      ) THEN NULL
      ELSE COALESCE(finished_at, now())
    END,
    last_error=CASE
      WHEN EXISTS (
          SELECT 1
          FROM documents d
          WHERE d.tenant_id=@tenant_id
            AND d.doc_path=@doc_path
            AND COALESCE(d.indexed_version, 0) <= 0
            AND COALESCE(d.status, '') NOT IN ('missing','deleted')
            AND (
                COALESCE(payload #>> '{control,requestedAction}', '') = 'pause'
                OR (
                    COALESCE(d.auto_ingest_paused, false)
                    AND COALESCE(d.auto_ingest_pause_reason, '') = 'admin_cancel'
                )
            )
      ) THEN NULL
      ELSE COALESCE(last_error, 'canceled_by_admin')
    END,
    payload=CASE
      WHEN EXISTS (
          SELECT 1
          FROM documents d
          WHERE d.tenant_id=@tenant_id
            AND d.doc_path=@doc_path
            AND COALESCE(d.indexed_version, 0) <= 0
            AND COALESCE(d.status, '') NOT IN ('missing','deleted')
            AND (
                COALESCE(payload #>> '{control,requestedAction}', '') = 'pause'
                OR (
                    COALESCE(d.auto_ingest_paused, false)
                    AND COALESCE(d.auto_ingest_pause_reason, '') = 'admin_cancel'
                )
            )
      ) THEN ((COALESCE(payload, '{}'::jsonb) #- '{control,cancelRequested}') #- '{control,requestedAction}')
      ELSE ((COALESCE(payload, '{}'::jsonb) #- '{control,cancelRequested}') #- '{control,requestedAction}')
    END,
    locked_by=NULL,
    locked_at=NULL
WHERE job_id=@job_id AND status IN ('running','paused');";
            await conn.ExecuteAsync(new CommandDefinition(cancelSql, new { job_id = jobId, tenant_id = tenantId, doc_path = docPath }, transaction: tx, cancellationToken: ct));

            const string stabilizeSql = @"UPDATE documents
SET auto_ingest_paused = CASE
        WHEN COALESCE(indexed_version, 0) > 0 THEN false
        ELSE true
    END,
    auto_ingest_paused_at = CASE
        WHEN COALESCE(indexed_version, 0) > 0 THEN NULL
        ELSE COALESCE(auto_ingest_paused_at, now())
    END,
    auto_ingest_pause_reason = CASE
        WHEN COALESCE(indexed_version, 0) > 0 THEN NULL
        ELSE COALESCE(auto_ingest_pause_reason, 'canceled_at_commit')
    END,
    status = CASE
        WHEN COALESCE(indexed_version, 0) > 0 THEN 'indexed'
        ELSE status
    END,
    ingestion_version = CASE
        WHEN COALESCE(indexed_version, 0) > 0 THEN COALESCE(indexed_version, 0)
        ELSE GREATEST(COALESCE(ingestion_version, 0), COALESCE(indexed_version, 0))
    END,
    updated_at = now()
WHERE tenant_id=@tenant_id AND doc_path=@doc_path
  AND COALESCE(status, '') NOT IN ('missing','deleted');";
            await conn.ExecuteAsync(new CommandDefinition(stabilizeSql, new { tenant_id = tenantId, doc_path = docPath }, transaction: tx, cancellationToken: ct));
            await FreezeTerminalSnapshotAsync(conn, jobId, tx, ct);

            await tx.CommitAsync(ct);
            return false;
        }

        const string versionSql = @"SELECT
    doc_id AS ""DocId"",
    status AS ""Status"",
    category AS ""Category"",
    ingestion_version AS ""IngestionVersion"",
    indexed_version AS ""IndexedVersion"",
    COALESCE(auto_ingest_paused, false) AS ""AutoIngestPaused"",
    auto_ingest_pause_reason AS ""AutoIngestPauseReason""
FROM documents
WHERE tenant_id=@tenant_id AND doc_path=@doc_path
FOR UPDATE;";
        var currentDocState = await conn.QueryFirstOrDefaultAsync<DocumentVersionState>(new CommandDefinition(versionSql, new
        {
            tenant_id = tenantId,
            doc_path = docPath
        }, transaction: tx, cancellationToken: ct));

        if (currentDocState is not null
            && currentDocState.AutoIngestPaused
            && string.Equals(currentDocState.AutoIngestPauseReason, "admin_cancel", StringComparison.OrdinalIgnoreCase))
        {
            const string cancelSql = @"UPDATE ingestion_jobs
SET status='paused',
    finished_at=NULL,
    last_error=NULL,
    payload=((COALESCE(payload, '{}'::jsonb) #- '{control,cancelRequested}') #- '{control,requestedAction}'),
    locked_by=NULL,
    locked_at=NULL
WHERE job_id=@job_id AND status IN ('running','paused');";
            await conn.ExecuteAsync(new CommandDefinition(cancelSql, new { job_id = jobId }, transaction: tx, cancellationToken: ct));
            await FreezeTerminalSnapshotAsync(conn, jobId, tx, ct);
            await tx.CommitAsync(ct);
            return false;
        }

        var currentVersion = currentDocState?.IngestionVersion;
        if (!currentVersion.HasValue || currentVersion.Value != version)
        {
            const string supersededSql = @"UPDATE ingestion_jobs
SET status='canceled',
    finished_at=COALESCE(finished_at, now()),
    last_error='superseded_at_commit',
    locked_by=NULL,
    locked_at=NULL
WHERE job_id=@job_id AND status='running';";
            await conn.ExecuteAsync(new CommandDefinition(supersededSql, new { job_id = jobId }, transaction: tx, cancellationToken: ct));
            await QueueCurrentVersionAfterSupersededAsync(conn, tx, tenantId, docPath, currentDocState, version, row.Priority ?? 100, ct);
            await FreezeTerminalSnapshotAsync(conn, jobId, tx, ct);
            await tx.CommitAsync(ct);
            return false;
        }

        var docId = currentDocState?.DocId ?? IdUtil.DeterministicGuid($"{tenantId}:{docPath}");
        var indexedVersionBefore = Math.Max(0, currentDocState?.IndexedVersion ?? 0);
        var nextIndexedVersion = version;
        var retrievalChunkQuality = IngestionWorker.BuildRetrievalChunkQualitySummary(retrievalChunks);
        if (retrievalChunkQuality.SearchableChunkCount == 0)
        {
            const string noSearchableReason = "manual_review_no_searchable_chunks";
            await DocumentFoundationRepo.PublishUnsearchableRetrievalDiagnosticsInTransactionAsync(
                conn,
                tx,
                tenantId,
                docId,
                jobId,
                docPath,
                hash,
                size,
                mtimeUtc,
                version,
                indexedVersionBefore,
                pages,
                units,
                retrievalChunks,
                retrievalChunkQuality,
                extractionSource,
                ocrAttempted,
                ocrApplied,
                ocrLanguages,
                ocrDurationMs,
                ocrDiagnostics,
                PdfExtractionQualitySummary.FromPages(pages),
                nativeExtractionQuality,
                noSearchableReason,
                ct);
            await tx.CommitAsync(ct);
            return false;
        }

        const string docSql = @"UPDATE documents
SET content_hash=@hash,
    file_size=@size,
    file_mtime=@mtime,
    page_count=@page_count,
    status='indexed',
    indexed_version=@indexed_version,
    last_ingested_at=now(),
    updated_at=now(),
    auto_ingest_paused=false,
    auto_ingest_paused_at=NULL,
    auto_ingest_pause_reason=NULL
WHERE tenant_id=@tenant_id AND doc_path=@doc_path;";
        await conn.ExecuteAsync(new CommandDefinition(docSql, new
        {
            tenant_id = tenantId,
            doc_path = docPath,
            hash,
            size,
            version,
            page_count = pages.Count,
            indexed_version = nextIndexedVersion,
            mtime = DateTime.SpecifyKind(mtimeUtc, DateTimeKind.Utc)
        }, transaction: tx, cancellationToken: ct));

        const string doneSql = @"UPDATE ingestion_jobs
SET status='done',
    finished_at=now(),
    last_error=NULL,
    locked_by=NULL,
    locked_at=NULL
WHERE job_id=@job_id AND status='running';";
        await conn.ExecuteAsync(new CommandDefinition(doneSql, new { job_id = jobId }, transaction: tx, cancellationToken: ct));
        await DocumentFoundationRepo.PublishUpsertCompletionAsync(
            conn,
            tx,
            tenantId,
            docId,
            jobId,
            docPath,
            hash,
            size,
            mtimeUtc,
            ingestionVersion: version,
            indexedVersionBefore,
            indexedVersionAfter: nextIndexedVersion,
            pages,
            sections,
            units,
            retrievalChunks,
            exactMatchEntries,
            contextualTextEntries,
            ct,
            extractionSource,
            ocrAttempted,
            ocrApplied,
            ocrLanguages,
            ocrDurationMs,
            ocrDiagnostics,
            nativeExtractionQuality,
            capabilityAProfileSeed);
        await FreezeTerminalSnapshotAsync(conn, jobId, tx, ct);

        await tx.CommitAsync(ct);
        return true;
    }

    public static async Task<bool> CompleteDeleteAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        Guid jobId,
        string docPath,
        int version,
        CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        const string lockSql = """
SELECT
    status AS "Status",
    priority AS "Priority",
    (LOWER(COALESCE(payload #>> '{control,cancelRequested}', '')) = 'true') AS "CancelRequested"
FROM ingestion_jobs
WHERE job_id=@job_id
FOR UPDATE;
""";

        var row = await conn.QueryFirstOrDefaultAsync<JobCancelState>(new CommandDefinition(lockSql, new { job_id = jobId }, transaction: tx, cancellationToken: ct));
        if (row is null)
        {
            await tx.CommitAsync(ct);
            return false;
        }

        if (!string.Equals(row.Status, "running", StringComparison.OrdinalIgnoreCase) || row.CancelRequested)
        {
            const string cancelSql = @"UPDATE ingestion_jobs
SET status='canceled',
    finished_at=COALESCE(finished_at, now()),
    last_error=COALESCE(last_error, 'canceled_by_admin'),
    payload=((COALESCE(payload, '{}'::jsonb) #- '{control,cancelRequested}') #- '{control,requestedAction}'),
    locked_by=NULL,
    locked_at=NULL
WHERE job_id=@job_id AND status='running';";
            await conn.ExecuteAsync(new CommandDefinition(cancelSql, new { job_id = jobId }, transaction: tx, cancellationToken: ct));
            await FreezeTerminalSnapshotAsync(conn, jobId, tx, ct);
            await tx.CommitAsync(ct);
            return false;
        }

        const string versionSql = @"SELECT
    doc_id AS ""DocId"",
    status AS ""Status"",
    category AS ""Category"",
    ingestion_version AS ""IngestionVersion"",
    indexed_version AS ""IndexedVersion"",
    COALESCE(auto_ingest_paused, false) AS ""AutoIngestPaused"",
    auto_ingest_pause_reason AS ""AutoIngestPauseReason""
FROM documents
WHERE tenant_id=@tenant_id AND doc_path=@doc_path
FOR UPDATE;";
        var currentDocState = await conn.QueryFirstOrDefaultAsync<DocumentVersionState>(new CommandDefinition(versionSql, new
        {
            tenant_id = tenantId,
            doc_path = docPath
        }, transaction: tx, cancellationToken: ct));

        var currentVersion = currentDocState?.IngestionVersion;
        if (!currentVersion.HasValue || currentVersion.Value != version)
        {
            const string supersededSql = @"UPDATE ingestion_jobs
SET status='canceled',
    finished_at=COALESCE(finished_at, now()),
    last_error='superseded_at_commit',
    locked_by=NULL,
    locked_at=NULL
WHERE job_id=@job_id AND status='running';";
            await conn.ExecuteAsync(new CommandDefinition(supersededSql, new { job_id = jobId }, transaction: tx, cancellationToken: ct));
            await QueueCurrentVersionAfterSupersededAsync(conn, tx, tenantId, docPath, currentDocState, version, row.Priority ?? 100, ct);
            await FreezeTerminalSnapshotAsync(conn, jobId, tx, ct);
            await tx.CommitAsync(ct);
            return false;
        }

        var docId = currentDocState?.DocId ?? IdUtil.DeterministicGuid($"{tenantId}:{docPath}");
        var indexedVersionBefore = Math.Max(0, currentDocState?.IndexedVersion ?? 0);

        const string docSql = @"UPDATE documents
SET status='deleted',
    updated_at=now(),
    content_hash=NULL,
    file_size=NULL,
    file_mtime=NULL,
    missing_since=NULL,
    ingestion_version=0,
    indexed_version=0,
    auto_ingest_paused=false,
    auto_ingest_paused_at=NULL,
    auto_ingest_pause_reason=NULL
WHERE tenant_id=@tenant_id AND doc_path=@doc_path;";
        await conn.ExecuteAsync(new CommandDefinition(docSql, new { tenant_id = tenantId, doc_path = docPath }, transaction: tx, cancellationToken: ct));

        const string doneSql = @"UPDATE ingestion_jobs
SET status='done',
    finished_at=now(),
    last_error=NULL,
    locked_by=NULL,
    locked_at=NULL
WHERE job_id=@job_id AND status='running';";
        await conn.ExecuteAsync(new CommandDefinition(doneSql, new { job_id = jobId }, transaction: tx, cancellationToken: ct));
        await DocumentFoundationRepo.PublishDeleteCompletionAsync(
            conn,
            tx,
            tenantId,
            docId,
            jobId,
            docPath,
            ingestionVersion: version,
            indexedVersionBefore,
            ct);
        await FreezeTerminalSnapshotAsync(conn, jobId, tx, ct);

        await tx.CommitAsync(ct);
        return true;
    }

    internal static async Task FreezeTerminalSnapshotAsync(NpgsqlDataSource ds, Guid jobId, CancellationToken ct)
        => await IngestionJobSnapshotStore.FreezeTerminalSnapshotAsync(ds, jobId, ct);

    internal static async Task FreezeTerminalSnapshotAsync(NpgsqlConnection conn, Guid jobId, NpgsqlTransaction? tx, CancellationToken ct)
        => await IngestionJobSnapshotStore.FreezeTerminalSnapshotAsync(conn, jobId, tx, ct);

    public static async Task<bool> MarkSupersededAndQueueCurrentAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        Guid jobId,
        string docPath,
        int supersededVersion,
        CancellationToken ct,
        string lastError = "superseded_version")
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        const string lockSql = """
SELECT
    status AS "Status",
    priority AS "Priority"
FROM ingestion_jobs
WHERE job_id=@job_id
FOR UPDATE;
""";

        var row = await conn.QueryFirstOrDefaultAsync<JobCancelState>(
            new CommandDefinition(lockSql, new { job_id = jobId }, transaction: tx, cancellationToken: ct));
        if (row is null)
        {
            await tx.CommitAsync(ct);
            return false;
        }

        const string cancelSql = """
UPDATE ingestion_jobs
SET status='canceled',
    finished_at=COALESCE(finished_at, now()),
    last_error=@last_error,
    locked_by=NULL,
    locked_at=NULL,
    available_at=now(),
    payload=((COALESCE(payload, '{}'::jsonb) #- '{control,cancelRequested}') #- '{control,requestedAction}')
WHERE job_id=@job_id
  AND status IN ('queued','running','paused');
""";

        var affected = await conn.ExecuteAsync(
            new CommandDefinition(cancelSql, new { job_id = jobId, last_error = lastError }, transaction: tx, cancellationToken: ct));
        if (affected == 0)
        {
            await tx.CommitAsync(ct);
            return false;
        }

        var currentDocState = await LoadCurrentDocumentVersionStateAsync(conn, tx, tenantId, docPath, ct);
        await QueueCurrentVersionAfterSupersededAsync(conn, tx, tenantId, docPath, currentDocState, supersededVersion, row.Priority ?? 100, ct);
        await FreezeTerminalSnapshotAsync(conn, jobId, tx, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private static async Task<DocumentVersionState?> LoadCurrentDocumentVersionStateAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid tenantId,
        string docPath,
        CancellationToken ct)
    {
        const string sql = """
SELECT
    doc_id AS "DocId",
    status AS "Status",
    category AS "Category",
    ingestion_version AS "IngestionVersion",
    indexed_version AS "IndexedVersion",
    COALESCE(auto_ingest_paused, false) AS "AutoIngestPaused",
    auto_ingest_pause_reason AS "AutoIngestPauseReason"
FROM documents
WHERE tenant_id=@tenant_id AND doc_path=@doc_path
FOR UPDATE;
""";

        return await conn.QueryFirstOrDefaultAsync<DocumentVersionState>(
            new CommandDefinition(sql, new { tenant_id = tenantId, doc_path = docPath }, transaction: tx, cancellationToken: ct));
    }

    private static async Task<Guid?> QueueCurrentVersionAfterSupersededAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid tenantId,
        string docPath,
        DocumentVersionState? currentDocState,
        int supersededVersion,
        int priority,
        CancellationToken ct)
    {
        if (currentDocState?.IngestionVersion is not int currentVersion
            || currentVersion <= 0
            || currentVersion <= supersededVersion)
            return null;

        var status = currentDocState.Status ?? string.Empty;
        var action = string.Equals(status, "missing", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "deleted", StringComparison.OrdinalIgnoreCase)
                ? "delete"
                : "upsert";
        var docId = currentDocState.DocId ?? IdUtil.DeterministicGuid($"{tenantId}:{docPath}");
        var indexedVersionBefore = Math.Max(0, currentDocState.IndexedVersion ?? 0);
        if (string.Equals(action, "upsert", StringComparison.OrdinalIgnoreCase)
            && string.Equals(status, "indexed", StringComparison.OrdinalIgnoreCase)
            && indexedVersionBefore >= currentVersion)
        {
            return null;
        }

        var payload = IngestionJobPayloadJson.Serialize(
            docId,
            currentVersion,
            source: "superseded",
            indexedVersionBefore: indexedVersionBefore);
        var nextJobId = Guid.NewGuid();
        var normalizedPriority = Math.Max(0, priority);
        var category = string.Equals(action, "upsert", StringComparison.OrdinalIgnoreCase)
            ? IngestionCategoryResolver.Normalize(
                string.IsNullOrWhiteSpace(currentDocState.Category)
                    ? IngestionCategoryResolver.DeriveFromDocumentPath(docPath)
                    : currentDocState.Category)
            : null;

        const string sql = """
INSERT INTO ingestion_jobs(job_id, tenant_id, action, doc_path, category, priority, status, payload, available_at)
VALUES(@job_id, @tenant_id, @action, @doc_path, @category, @priority, 'queued', @payload::jsonb, now())
ON CONFLICT (tenant_id, doc_path, action) WHERE status IN ('queued','running','paused')
DO UPDATE SET
  available_at = CASE
      WHEN ingestion_jobs.status='running' THEN ingestion_jobs.available_at
      ELSE now()
  END,
  payload = CASE
      WHEN ingestion_jobs.status='running' THEN ingestion_jobs.payload
      ELSE EXCLUDED.payload
  END,
  category = CASE
      WHEN ingestion_jobs.status='running' THEN ingestion_jobs.category
      ELSE EXCLUDED.category
  END,
  priority = CASE
      WHEN ingestion_jobs.status='running' THEN ingestion_jobs.priority
      ELSE LEAST(ingestion_jobs.priority, EXCLUDED.priority)
  END,
  last_error = CASE
      WHEN ingestion_jobs.status='running' THEN ingestion_jobs.last_error
      ELSE NULL
  END
RETURNING job_id;
""";

        return await conn.ExecuteScalarAsync<Guid?>(
            new CommandDefinition(sql, new
            {
                job_id = nextJobId,
                tenant_id = tenantId,
                action,
                doc_path = docPath,
                category,
                priority = normalizedPriority,
                payload
            }, transaction: tx, cancellationToken: ct));
    }

    public static async Task UpdateProgressAsync(NpgsqlDataSource ds, Guid jobId, string phase, int? current, int? total, CancellationToken ct)
        => await IngestionJobSnapshotStore.UpdateProgressAsync(ds, jobId, phase, current, total, ct);

    public static async Task UpdateProgressAsync(NpgsqlDataSource ds, Guid jobId, string phase, int? current, int? total, object? details, CancellationToken ct)
        => await IngestionJobSnapshotStore.UpdateProgressAsync(ds, jobId, phase, current, total, details, ct);

    public static async Task StoreResumeCheckpointAsync(
        NpgsqlDataSource ds,
        Guid jobId,
        string sourceHash,
        long fileSize,
        int chunkTotal,
        string embeddingModel,
        string embeddingInputFormat,
        CancellationToken ct)
        => await IngestionJobSnapshotStore.StoreResumeCheckpointAsync(ds, jobId, sourceHash, fileSize, chunkTotal, embeddingModel, embeddingInputFormat, ct);

    public static async Task<ResumeCheckpointState?> GetResumeCheckpointAsync(NpgsqlDataSource ds, Guid jobId, CancellationToken ct)
        => await IngestionJobSnapshotStore.GetResumeCheckpointAsync(ds, jobId, ct);
}
