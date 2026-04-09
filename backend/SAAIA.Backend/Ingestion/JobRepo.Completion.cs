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
        CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        const string lockSql = """
SELECT
    status AS "Status",
    COALESCE((payload #>> '{control,cancelRequested}')::boolean, false) AS "CancelRequested"
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
    ingestion_version = GREATEST(COALESCE(ingestion_version, 0), COALESCE(indexed_version, 0)),
    updated_at = now()
WHERE tenant_id=@tenant_id AND doc_path=@doc_path
  AND COALESCE(status, '') NOT IN ('missing','deleted');";
            await conn.ExecuteAsync(new CommandDefinition(stabilizeSql, new { tenant_id = tenantId, doc_path = docPath }, transaction: tx, cancellationToken: ct));
            await FreezeTerminalSnapshotAsync(conn, jobId, tx, ct);

            await tx.CommitAsync(ct);
            return false;
        }

        const string versionSql = @"SELECT
    ingestion_version AS ""IngestionVersion"",
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
            await tx.CommitAsync(ct);
            return false;
        }

        const string docSql = @"UPDATE documents
SET content_hash=@hash,
    file_size=@size,
    file_mtime=@mtime,
    status='indexed',
    indexed_version=@version,
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
    COALESCE((payload #>> '{control,cancelRequested}')::boolean, false) AS "CancelRequested"
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
    ingestion_version AS ""IngestionVersion"",
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
            await FreezeTerminalSnapshotAsync(conn, jobId, tx, ct);
            await tx.CommitAsync(ct);
            return false;
        }

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
        await FreezeTerminalSnapshotAsync(conn, jobId, tx, ct);

        await tx.CommitAsync(ct);
        return true;
    }

    internal static async Task FreezeTerminalSnapshotAsync(NpgsqlDataSource ds, Guid jobId, CancellationToken ct)
        => await IngestionJobSnapshotStore.FreezeTerminalSnapshotAsync(ds, jobId, ct);

    internal static async Task FreezeTerminalSnapshotAsync(NpgsqlConnection conn, Guid jobId, NpgsqlTransaction? tx, CancellationToken ct)
        => await IngestionJobSnapshotStore.FreezeTerminalSnapshotAsync(conn, jobId, tx, ct);

    public static async Task UpdateProgressAsync(NpgsqlDataSource ds, Guid jobId, string phase, int? current, int? total, CancellationToken ct)
        => await IngestionJobSnapshotStore.UpdateProgressAsync(ds, jobId, phase, current, total, ct);

    public static async Task StoreResumeCheckpointAsync(
        NpgsqlDataSource ds,
        Guid jobId,
        string sourceHash,
        long fileSize,
        int chunkTotal,
        CancellationToken ct)
        => await IngestionJobSnapshotStore.StoreResumeCheckpointAsync(ds, jobId, sourceHash, fileSize, chunkTotal, ct);

    public static async Task<ResumeCheckpointState?> GetResumeCheckpointAsync(NpgsqlDataSource ds, Guid jobId, CancellationToken ct)
        => await IngestionJobSnapshotStore.GetResumeCheckpointAsync(ds, jobId, ct);
}
