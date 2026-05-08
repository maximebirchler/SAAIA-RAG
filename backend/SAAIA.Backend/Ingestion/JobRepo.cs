using Dapper;
using Npgsql;

static partial class JobRepo
{
    public static async Task<IngestionJob?> TryDequeueAsync(NpgsqlDataSource ds, string workerId, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        const string sql = @"
WITH cte AS (
  SELECT q.job_id
  FROM ingestion_jobs q
  WHERE q.status='queued'
    AND q.available_at <= now()
    AND NOT EXISTS (
      SELECT 1 FROM ingestion_jobs r
      WHERE r.status='running'
        AND r.tenant_id=q.tenant_id
        AND r.doc_path=q.doc_path
    )
  ORDER BY q.priority ASC, q.created_at ASC
  FOR UPDATE SKIP LOCKED
  LIMIT 1
)
UPDATE ingestion_jobs j
SET status='running',
    locked_by=@worker,
    locked_at=now(),
    started_at=COALESCE(started_at, now()),
    attempts=attempts+1
FROM cte
WHERE j.job_id=cte.job_id
RETURNING
  j.job_id    AS JobId,
  j.tenant_id AS TenantId,
  j.action    AS Action,
  j.doc_path  AS DocPath,
  j.category  AS Category,
  j.payload   AS Payload;";

        var row = await conn.QueryFirstOrDefaultAsync<IngestionJobRow>(
            new CommandDefinition(sql, new { worker = workerId }, transaction: tx, cancellationToken: ct));

        if (row is null)
        {
            await tx.CommitAsync(ct);
            return null;
        }

        var docId = row.DocIdFromPayload ?? IdUtil.DeterministicGuid($"{row.TenantId}:{row.DocPath}");
        var version = row.VersionFromPayload ?? 0;

        await tx.CommitAsync(ct);
        return new IngestionJob(
            row.JobId,
            row.TenantId,
            row.Action,
            row.DocPath,
            row.Category,
            docId,
            version,
            row.CapabilityAProfileSeedFromPayload);
    }

    public static async Task MarkDoneAsync(NpgsqlDataSource ds, Guid jobId, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = @"
UPDATE ingestion_jobs
SET status = CASE
        WHEN COALESCE((payload #>> '{control,cancelRequested}')::boolean, false) THEN 'canceled'
        ELSE 'done'
    END,
    finished_at=now(),
    last_error = CASE
        WHEN COALESCE((payload #>> '{control,cancelRequested}')::boolean, false)
            THEN COALESCE(last_error, 'canceled_by_admin')
        ELSE NULL
    END,
    payload = ((COALESCE(payload, '{}'::jsonb) #- '{control,cancelRequested}') #- '{control,requestedAction}'),
    locked_by=NULL,
    locked_at=NULL
WHERE job_id=@job_id AND status IN ('running','paused');";
        await conn.ExecuteAsync(new CommandDefinition(sql, new { job_id = jobId }, cancellationToken: ct));
    }

    public static async Task MarkFailedAsync(NpgsqlDataSource ds, Guid jobId, string error, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = @"
UPDATE ingestion_jobs j
SET status = CASE
        WHEN j.action='upsert'
             AND COALESCE(d.indexed_version, 0) <= 0
             AND COALESCE(d.status, '') NOT IN ('missing','deleted')
             AND (
                 COALESCE(j.payload #>> '{control,requestedAction}', '') = 'pause'
                 OR (
                     COALESCE(d.auto_ingest_paused, false)
                     AND COALESCE(d.auto_ingest_pause_reason, '') = 'admin_cancel'
                 )
             )
             AND COALESCE(@err, '') NOT IN ('source_removed_during_ingestion', 'file_missing')
            THEN 'paused'
        WHEN (
                 COALESCE((j.payload #>> '{control,cancelRequested}')::boolean, false)
                 OR COALESCE(j.payload #>> '{control,requestedAction}', '') = 'cancel'
             )
             AND COALESCE(@err, '') NOT IN ('source_removed_during_ingestion', 'file_missing')
            THEN 'canceled'
        ELSE 'failed'
    END,
    finished_at = CASE
        WHEN j.action='upsert'
             AND COALESCE(d.indexed_version, 0) <= 0
             AND COALESCE(d.status, '') NOT IN ('missing','deleted')
             AND (
                 COALESCE(j.payload #>> '{control,requestedAction}', '') = 'pause'
                 OR (
                     COALESCE(d.auto_ingest_paused, false)
                     AND COALESCE(d.auto_ingest_pause_reason, '') = 'admin_cancel'
                 )
             )
             AND COALESCE(@err, '') NOT IN ('source_removed_during_ingestion', 'file_missing')
            THEN NULL
        ELSE now()
    END,
    started_at = CASE
        WHEN j.action='upsert'
             AND COALESCE(d.indexed_version, 0) <= 0
             AND COALESCE(d.status, '') NOT IN ('missing','deleted')
             AND (
                 COALESCE(j.payload #>> '{control,requestedAction}', '') = 'pause'
                 OR (
                     COALESCE(d.auto_ingest_paused, false)
                     AND COALESCE(d.auto_ingest_pause_reason, '') = 'admin_cancel'
                 )
             )
             AND COALESCE(@err, '') NOT IN ('source_removed_during_ingestion', 'file_missing')
            THEN NULL
        ELSE j.started_at
    END,
    last_error = CASE
        WHEN j.action='upsert'
             AND COALESCE(d.indexed_version, 0) <= 0
             AND COALESCE(d.status, '') NOT IN ('missing','deleted')
             AND (
                 COALESCE(j.payload #>> '{control,requestedAction}', '') = 'pause'
                 OR (
                     COALESCE(d.auto_ingest_paused, false)
                     AND COALESCE(d.auto_ingest_pause_reason, '') = 'admin_cancel'
                 )
             )
             AND COALESCE(@err, '') NOT IN ('source_removed_during_ingestion', 'file_missing')
            THEN NULL
        WHEN (
                 COALESCE((j.payload #>> '{control,cancelRequested}')::boolean, false)
                 OR COALESCE(j.payload #>> '{control,requestedAction}', '') = 'cancel'
             )
             AND COALESCE(@err, '') NOT IN ('source_removed_during_ingestion', 'file_missing')
            THEN COALESCE(j.last_error, 'canceled_by_admin')
        ELSE @err
    END,
    payload = CASE
        WHEN j.action='upsert'
             AND COALESCE(d.indexed_version, 0) <= 0
             AND COALESCE(d.status, '') NOT IN ('missing','deleted')
             AND (
                 COALESCE(j.payload #>> '{control,requestedAction}', '') = 'pause'
                 OR (
                     COALESCE(d.auto_ingest_paused, false)
                     AND COALESCE(d.auto_ingest_pause_reason, '') = 'admin_cancel'
                 )
             )
             AND COALESCE(@err, '') NOT IN ('source_removed_during_ingestion', 'file_missing')
            THEN ((COALESCE(j.payload, '{}'::jsonb) #- '{control,cancelRequested}') #- '{control,requestedAction}')
        WHEN COALESCE(@err, '') IN ('source_removed_during_ingestion', 'file_missing')
            THEN ((COALESCE(j.payload, '{}'::jsonb) #- '{control,cancelRequested}') #- '{control,requestedAction}')
        WHEN (
                 COALESCE((j.payload #>> '{control,cancelRequested}')::boolean, false)
                 OR COALESCE(j.payload #>> '{control,requestedAction}', '') = 'cancel'
             )
             AND COALESCE(@err, '') NOT IN ('source_removed_during_ingestion', 'file_missing')
            THEN ((COALESCE(j.payload, '{}'::jsonb) #- '{control,cancelRequested}') #- '{control,requestedAction}')
        ELSE COALESCE(j.payload, '{}'::jsonb)
    END,
    locked_by=NULL,
    locked_at=NULL
FROM documents d
WHERE j.job_id=@job_id
  AND d.tenant_id=j.tenant_id
  AND d.doc_path=j.doc_path
  AND j.status IN ('running','paused');";
        var affected = await conn.ExecuteAsync(new CommandDefinition(sql, new { job_id = jobId, err = error }, cancellationToken: ct));

        if (affected > 0)
        {
            await FreezeTerminalSnapshotAsync(conn, jobId, null, ct);
            return;
        }

        const string fallbackSql = @"
UPDATE ingestion_jobs
SET status = CASE
        WHEN COALESCE((payload #>> '{control,cancelRequested}')::boolean, false)
             AND COALESCE(@err, '') NOT IN ('source_removed_during_ingestion', 'file_missing')
            THEN 'canceled'
        ELSE 'failed'
    END,
    finished_at=now(),
    last_error = CASE
        WHEN COALESCE((payload #>> '{control,cancelRequested}')::boolean, false)
             AND COALESCE(@err, '') NOT IN ('source_removed_during_ingestion', 'file_missing')
            THEN COALESCE(last_error, @err, 'canceled_by_admin')
        ELSE @err
    END,
    payload = CASE
        WHEN COALESCE(@err, '') IN ('source_removed_during_ingestion', 'file_missing')
            THEN ((COALESCE(payload, '{}'::jsonb) #- '{control,cancelRequested}') #- '{control,requestedAction}')
        WHEN COALESCE((payload #>> '{control,cancelRequested}')::boolean, false)
            THEN ((COALESCE(payload, '{}'::jsonb) #- '{control,cancelRequested}') #- '{control,requestedAction}')
        ELSE COALESCE(payload, '{}'::jsonb)
    END,
    locked_by=NULL,
    locked_at=NULL
WHERE job_id=@job_id AND status IN ('running','paused');";
        var fallbackAffected = await conn.ExecuteAsync(new CommandDefinition(fallbackSql, new { job_id = jobId, err = error }, cancellationToken: ct));
        if (fallbackAffected > 0)
            await FreezeTerminalSnapshotAsync(conn, jobId, null, ct);
    }

    public static async Task MarkCanceledAsync(NpgsqlDataSource ds, Guid jobId, string reason, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = @"
UPDATE ingestion_jobs j
SET status = CASE
        WHEN j.action='upsert'
             AND COALESCE(d.indexed_version, 0) <= 0
             AND COALESCE(d.status, '') NOT IN ('missing','deleted')
             AND (
                 COALESCE(j.payload #>> '{control,requestedAction}', '') = 'pause'
                 OR (
                     COALESCE(d.auto_ingest_paused, false)
                     AND COALESCE(d.auto_ingest_pause_reason, '') = 'admin_cancel'
                 )
             )
             AND COALESCE(@reason, '') LIKE 'canceled_by_admin%'
            THEN 'paused'
        ELSE 'canceled'
    END,
    finished_at = CASE
        WHEN j.action='upsert'
             AND COALESCE(d.indexed_version, 0) <= 0
             AND COALESCE(d.status, '') NOT IN ('missing','deleted')
             AND (
                 COALESCE(j.payload #>> '{control,requestedAction}', '') = 'pause'
                 OR (
                     COALESCE(d.auto_ingest_paused, false)
                     AND COALESCE(d.auto_ingest_pause_reason, '') = 'admin_cancel'
                 )
             )
             AND COALESCE(@reason, '') LIKE 'canceled_by_admin%'
            THEN NULL
        ELSE COALESCE(j.finished_at, now())
    END,
    started_at = CASE
        WHEN j.action='upsert'
             AND COALESCE(d.indexed_version, 0) <= 0
             AND COALESCE(d.status, '') NOT IN ('missing','deleted')
             AND (
                 COALESCE(j.payload #>> '{control,requestedAction}', '') = 'pause'
                 OR (
                     COALESCE(d.auto_ingest_paused, false)
                     AND COALESCE(d.auto_ingest_pause_reason, '') = 'admin_cancel'
                 )
             )
             AND COALESCE(@reason, '') LIKE 'canceled_by_admin%'
            THEN NULL
        ELSE j.started_at
    END,
    last_error = CASE
        WHEN j.action='upsert'
             AND COALESCE(d.indexed_version, 0) <= 0
             AND COALESCE(d.status, '') NOT IN ('missing','deleted')
             AND (
                 COALESCE(j.payload #>> '{control,requestedAction}', '') = 'pause'
                 OR (
                     COALESCE(d.auto_ingest_paused, false)
                     AND COALESCE(d.auto_ingest_pause_reason, '') = 'admin_cancel'
                 )
             )
             AND COALESCE(@reason, '') LIKE 'canceled_by_admin%'
            THEN NULL
        ELSE COALESCE(@reason, j.last_error)
    END,
    payload = CASE
        WHEN j.action='upsert'
             AND COALESCE(d.indexed_version, 0) <= 0
             AND (
                 COALESCE(j.payload #>> '{control,requestedAction}', '') = 'pause'
                 OR (
                     COALESCE(d.auto_ingest_paused, false)
                     AND COALESCE(d.auto_ingest_pause_reason, '') = 'admin_cancel'
                 )
             )
             AND COALESCE(@reason, '') LIKE 'canceled_by_admin%'
            THEN ((COALESCE(j.payload, '{}'::jsonb) #- '{control,cancelRequested}') #- '{control,requestedAction}')
        ELSE ((COALESCE(j.payload, '{}'::jsonb) #- '{control,cancelRequested}') #- '{control,requestedAction}')
    END,
    locked_by=NULL,
    locked_at=NULL,
    available_at=now()
FROM documents d
WHERE j.job_id=@job_id
  AND d.tenant_id=j.tenant_id
  AND d.doc_path=j.doc_path
  AND j.status IN ('queued','running','canceled','paused');";
        var affected = await conn.ExecuteAsync(new CommandDefinition(sql, new { job_id = jobId, reason }, cancellationToken: ct));

        const string fallbackSql = @"
UPDATE ingestion_jobs
SET status='canceled',
    finished_at=COALESCE(finished_at, now()),
    last_error=COALESCE(@reason, last_error),
    payload=((COALESCE(payload, '{}'::jsonb) #- '{control,cancelRequested}') #- '{control,requestedAction}'),
    locked_by=NULL,
    locked_at=NULL,
    available_at=now()
WHERE job_id=@job_id
  AND status IN ('queued','running','canceled','paused');";
        if (affected == 0)
        {
            var fallbackAffected = await conn.ExecuteAsync(new CommandDefinition(fallbackSql, new { job_id = jobId, reason }, cancellationToken: ct));
            if (fallbackAffected > 0)
                await FreezeTerminalSnapshotAsync(conn, jobId, null, ct);
        }
        else
        {
            await FreezeTerminalSnapshotAsync(conn, jobId, null, ct);
        }
    }

    public static async Task<int> RequeueStaleRunningAsync(NpgsqlDataSource ds, TimeSpan staleAfter, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);

        const string sql = @"
UPDATE ingestion_jobs j
SET status = CASE
        WHEN COALESCE((j.payload #>> '{control,cancelRequested}')::boolean, false)
             AND j.action='upsert'
             AND COALESCE(d.indexed_version, 0) <= 0
             AND COALESCE(d.status, '') NOT IN ('missing','deleted')
             AND (
                 COALESCE(j.payload #>> '{control,requestedAction}', '') = 'pause'
                 OR (
                     COALESCE(d.auto_ingest_paused, false)
                     AND COALESCE(d.auto_ingest_pause_reason, '') = 'admin_cancel'
                 )
             )
            THEN 'paused'
        WHEN COALESCE((j.payload #>> '{control,cancelRequested}')::boolean, false) THEN 'canceled'
        ELSE 'queued'
    END,
    finished_at = CASE
        WHEN COALESCE((j.payload #>> '{control,cancelRequested}')::boolean, false)
             AND j.action='upsert'
             AND COALESCE(d.indexed_version, 0) <= 0
             AND COALESCE(d.status, '') NOT IN ('missing','deleted')
             AND (
                 COALESCE(j.payload #>> '{control,requestedAction}', '') = 'pause'
                 OR (
                     COALESCE(d.auto_ingest_paused, false)
                     AND COALESCE(d.auto_ingest_pause_reason, '') = 'admin_cancel'
                 )
             )
            THEN NULL
        WHEN COALESCE((j.payload #>> '{control,cancelRequested}')::boolean, false)
            THEN COALESCE(j.finished_at, now())
        ELSE j.finished_at
    END,
    started_at = CASE
        WHEN COALESCE((j.payload #>> '{control,cancelRequested}')::boolean, false)
             AND j.action='upsert'
             AND COALESCE(d.indexed_version, 0) <= 0
             AND COALESCE(d.status, '') NOT IN ('missing','deleted')
             AND (
                 COALESCE(j.payload #>> '{control,requestedAction}', '') = 'pause'
                 OR (
                     COALESCE(d.auto_ingest_paused, false)
                     AND COALESCE(d.auto_ingest_pause_reason, '') = 'admin_cancel'
                 )
             )
            THEN NULL
        ELSE j.started_at
    END,
    locked_at=NULL,
    locked_by=NULL,
    available_at=now(),
    last_error = CASE
        WHEN COALESCE((j.payload #>> '{control,cancelRequested}')::boolean, false)
             AND j.action='upsert'
             AND COALESCE(d.indexed_version, 0) <= 0
             AND COALESCE(d.status, '') NOT IN ('missing','deleted')
             AND (
                 COALESCE(j.payload #>> '{control,requestedAction}', '') = 'pause'
                 OR (
                     COALESCE(d.auto_ingest_paused, false)
                     AND COALESCE(d.auto_ingest_pause_reason, '') = 'admin_cancel'
                 )
             )
            THEN NULL
        WHEN COALESCE((j.payload #>> '{control,cancelRequested}')::boolean, false)
            THEN COALESCE(j.last_error, 'canceled_stale_running')
        ELSE COALESCE(j.last_error, 'requeued_stale_running')
    END,
    payload = CASE
        WHEN COALESCE((j.payload #>> '{control,cancelRequested}')::boolean, false)
             AND j.action='upsert'
             AND COALESCE(d.indexed_version, 0) <= 0
             AND (
                 COALESCE(j.payload #>> '{control,requestedAction}', '') = 'pause'
                 OR (
                     COALESCE(d.auto_ingest_paused, false)
                     AND COALESCE(d.auto_ingest_pause_reason, '') = 'admin_cancel'
                 )
             )
            THEN ((COALESCE(j.payload, '{}'::jsonb) #- '{control,cancelRequested}') #- '{control,requestedAction}')
        WHEN COALESCE((j.payload #>> '{control,cancelRequested}')::boolean, false)
            THEN ((COALESCE(j.payload, '{}'::jsonb) #- '{control,cancelRequested}') #- '{control,requestedAction}')
        ELSE COALESCE(j.payload, '{}'::jsonb)
    END
FROM documents d
WHERE j.tenant_id=d.tenant_id
  AND j.doc_path=d.doc_path
  AND j.status='running'
  AND (
      j.locked_at IS NULL
      OR j.locked_at < now() - (@stale_seconds * interval '1 second')
  );";

        var affected = await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            stale_seconds = (int)staleAfter.TotalSeconds
        }, cancellationToken: ct));

        const string fallbackSql = @"
UPDATE ingestion_jobs
SET status = CASE
        WHEN COALESCE((payload #>> '{control,cancelRequested}')::boolean, false) THEN 'canceled'
        ELSE 'queued'
    END,
    finished_at = CASE
        WHEN COALESCE((payload #>> '{control,cancelRequested}')::boolean, false)
            THEN COALESCE(finished_at, now())
        ELSE finished_at
    END,
    locked_at=NULL,
    locked_by=NULL,
    available_at=now(),
    last_error = CASE
        WHEN COALESCE((payload #>> '{control,cancelRequested}')::boolean, false)
            THEN COALESCE(last_error, 'canceled_stale_running')
        ELSE COALESCE(last_error, 'requeued_stale_running')
    END,
    payload = CASE
        WHEN COALESCE((payload #>> '{control,cancelRequested}')::boolean, false)
            THEN ((COALESCE(payload, '{}'::jsonb) #- '{control,cancelRequested}') #- '{control,requestedAction}')
        ELSE COALESCE(payload, '{}'::jsonb)
    END
WHERE status='running'
  AND (
      locked_at IS NULL
      OR locked_at < now() - (@stale_seconds * interval '1 second')
  )
  AND NOT EXISTS (
      SELECT 1
      FROM documents d
      WHERE d.tenant_id=ingestion_jobs.tenant_id
        AND d.doc_path=ingestion_jobs.doc_path
  );";

        affected += await conn.ExecuteAsync(new CommandDefinition(fallbackSql, new
        {
            stale_seconds = (int)staleAfter.TotalSeconds
        }, cancellationToken: ct));

        return affected;
    }

    public static async Task TouchAsync(NpgsqlDataSource ds, Guid jobId, string workerId, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);

        const string sql = @"
    UPDATE ingestion_jobs
    SET locked_at=now()
    WHERE job_id=@job_id
    AND status='running'
    AND locked_by=@worker;";

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            job_id = jobId,
            worker = workerId
        }, cancellationToken: ct));
    }


    /// <summary>
    /// Safety net: ensures the document is stabilized after a job cancellation.
    /// Uses COALESCE to not overwrite existing pause info from the cancel endpoint.
    /// Only acts if the document is not already paused (idempotent).
    /// </summary>
    private sealed class JobCancelState
    {
        public string? Status { get; set; }
        public int? Priority { get; set; }
        public bool CancelRequested { get; set; }
        public string? RequestedAction { get; set; }
    }

    public sealed class ResumeCheckpointState
    {
        public int? ProgressCurrent { get; set; }
        public int? ProgressTotal { get; set; }
        public string? SourceHash { get; set; }
        public long? FileSize { get; set; }
        public int? ChunkTotal { get; set; }
    }

    private sealed record JobAndDocumentCancelState(
        string? JobStatus,
        bool JobCancelRequested,
        string? RequestedAction,
        bool DocumentAutoIngestPaused,
        string? DocumentAutoIngestPauseReason);

    private sealed record DocumentVersionState(
        Guid? DocId,
        string? Status,
        string? Category,
        int? IngestionVersion,
        int? IndexedVersion,
        bool AutoIngestPaused,
        string? AutoIngestPauseReason);

    private sealed record IngestionJobRow(Guid JobId, Guid TenantId, string Action, string DocPath, string? Category, string Payload)
    {
        private IngestionJobPayloadJson.IngestionJobPayloadData? _parsedPayload;

        private IngestionJobPayloadJson.IngestionJobPayloadData ParsedPayload
            => _parsedPayload ??= IngestionJobPayloadJson.Parse(Payload);

        public Guid? DocIdFromPayload => ParsedPayload.DocId;

        public int? VersionFromPayload => ParsedPayload.Version;

        public IngestionCapabilityAProfileSeed? CapabilityAProfileSeedFromPayload => ParsedPayload.CapabilityAProfileSeed;
    }
}
