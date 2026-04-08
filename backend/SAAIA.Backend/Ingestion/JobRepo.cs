using System.Text.Json;
using Dapper;
using Npgsql;

static class JobRepo
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
        return new IngestionJob(row.JobId, row.TenantId, row.Action, row.DocPath, row.Category, docId, version);
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
    locked_by=NULL,
    locked_at=NULL
WHERE job_id=@job_id AND status='running';";
        await conn.ExecuteAsync(new CommandDefinition(sql, new { job_id = jobId }, cancellationToken: ct));
    }

    public static async Task MarkFailedAsync(NpgsqlDataSource ds, Guid jobId, string error, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = @"
UPDATE ingestion_jobs
SET status = CASE
        WHEN COALESCE((payload #>> '{control,cancelRequested}')::boolean, false) THEN 'canceled'
        ELSE 'failed'
    END,
    finished_at=now(),
    last_error = CASE
        WHEN COALESCE((payload #>> '{control,cancelRequested}')::boolean, false)
            THEN COALESCE(last_error, @err, 'canceled_by_admin')
        ELSE @err
    END,
    locked_by=NULL,
    locked_at=NULL
WHERE job_id=@job_id AND status='running';";
        await conn.ExecuteAsync(new CommandDefinition(sql, new { job_id = jobId, err = error }, cancellationToken: ct));
    }

    public static async Task MarkCanceledAsync(NpgsqlDataSource ds, Guid jobId, string reason, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = @"
UPDATE ingestion_jobs
SET status='canceled', finished_at=COALESCE(finished_at, now()), last_error=COALESCE(@reason, last_error),
    locked_by=NULL, locked_at=NULL, available_at=now()
WHERE job_id=@job_id AND status IN ('queued','running','canceled');";
        await conn.ExecuteAsync(new CommandDefinition(sql, new { job_id = jobId, reason }, cancellationToken: ct));
    }

    public static async Task<int> RequeueStaleRunningAsync(NpgsqlDataSource ds, TimeSpan staleAfter, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);

        const string sql = @"
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
    END
WHERE status='running'
AND (
    locked_at IS NULL
    OR locked_at < now() - (@stale_seconds * interval '1 second')
);";

        return await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            stale_seconds = (int)staleAfter.TotalSeconds
        }, cancellationToken: ct));
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
    public static async Task StabilizeDocumentAfterCancelAsync(NpgsqlDataSource ds, Guid tenantId, string docPath, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = @"
UPDATE documents
SET auto_ingest_paused = true,
    auto_ingest_paused_at = COALESCE(auto_ingest_paused_at, now()),
    auto_ingest_pause_reason = COALESCE(auto_ingest_pause_reason, 'canceled_by_worker'),
    status = CASE
        WHEN COALESCE(indexed_version, 0) > 0 THEN 'indexed'
        ELSE status
    END,
    updated_at = now()
WHERE tenant_id = @tenant_id
  AND doc_path = @doc_path
  AND NOT COALESCE(auto_ingest_paused, false);";
        await conn.ExecuteAsync(new CommandDefinition(sql, new { tenant_id = tenantId, doc_path = docPath }, cancellationToken: ct));
    }

    public static async Task<bool> IsCanceledAsync(NpgsqlDataSource ds, Guid jobId, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
SELECT
    status AS "Status",
    COALESCE((payload #>> '{control,cancelRequested}')::boolean, false) AS "CancelRequested"
FROM ingestion_jobs
WHERE job_id=@job_id
LIMIT 1;
""";
        var row = await conn.QueryFirstOrDefaultAsync<JobCancelState>(new CommandDefinition(sql, new { job_id = jobId }, cancellationToken: ct));
        if (row is null)
            return false;

        return row.CancelRequested
               || string.Equals(row.Status, "canceled", StringComparison.OrdinalIgnoreCase)
               || string.Equals(row.Status, "cancelled", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<bool> IsCancellationRequestedAsync(NpgsqlDataSource ds, Guid tenantId, string docPath, Guid jobId, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
SELECT
    COALESCE(j.status, '') AS "JobStatus",
    COALESCE((j.payload #>> '{control,cancelRequested}')::boolean, false) AS "JobCancelRequested",
    COALESCE(d.auto_ingest_paused, false) AS "DocumentAutoIngestPaused",
    d.auto_ingest_pause_reason AS "DocumentAutoIngestPauseReason"
FROM ingestion_jobs j
LEFT JOIN documents d
  ON d.tenant_id=@tenant_id
 AND d.doc_path=@doc_path
WHERE j.job_id=@job_id
LIMIT 1;
""";
        var row = await conn.QueryFirstOrDefaultAsync<JobAndDocumentCancelState>(
            new CommandDefinition(sql, new { tenant_id = tenantId, doc_path = docPath, job_id = jobId }, cancellationToken: ct));
        if (row is null)
            return false;

        if (row.JobCancelRequested
            || string.Equals(row.JobStatus, "canceled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(row.JobStatus, "cancelled", StringComparison.OrdinalIgnoreCase))
            return true;

        return row.DocumentAutoIngestPaused
               && string.Equals(row.DocumentAutoIngestPauseReason, "admin_cancel", StringComparison.OrdinalIgnoreCase);
    }

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
SET status='canceled',
    finished_at=COALESCE(finished_at, now()),
    last_error=COALESCE(last_error, 'canceled_by_admin'),
    locked_by=NULL,
    locked_at=NULL
WHERE job_id=@job_id AND status='running';";
            await conn.ExecuteAsync(new CommandDefinition(cancelSql, new { job_id = jobId }, transaction: tx, cancellationToken: ct));

            // Stabilize document within the same transaction to prevent scanner race
            const string stabilizeSql = @"UPDATE documents
SET auto_ingest_paused = true,
    auto_ingest_paused_at = COALESCE(auto_ingest_paused_at, now()),
    auto_ingest_pause_reason = COALESCE(auto_ingest_pause_reason, 'canceled_at_commit'),
    status = CASE
        WHEN COALESCE(indexed_version, 0) > 0 THEN 'indexed'
        ELSE status
    END,
    updated_at = now()
WHERE tenant_id=@tenant_id AND doc_path=@doc_path
  AND NOT COALESCE(auto_ingest_paused, false);";
            await conn.ExecuteAsync(new CommandDefinition(stabilizeSql, new { tenant_id = tenantId, doc_path = docPath }, transaction: tx, cancellationToken: ct));

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
SET status='canceled',
    finished_at=COALESCE(finished_at, now()),
    last_error=COALESCE(last_error, 'canceled_by_admin_document'),
    locked_by=NULL,
    locked_at=NULL
WHERE job_id=@job_id AND status='running';";
            await conn.ExecuteAsync(new CommandDefinition(cancelSql, new { job_id = jobId }, transaction: tx, cancellationToken: ct));
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
    locked_by=NULL,
    locked_at=NULL
WHERE job_id=@job_id AND status='running';";
            await conn.ExecuteAsync(new CommandDefinition(cancelSql, new { job_id = jobId }, transaction: tx, cancellationToken: ct));
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
SET status='canceled',
    finished_at=COALESCE(finished_at, now()),
    last_error=COALESCE(last_error, 'canceled_by_admin_document'),
    locked_by=NULL,
    locked_at=NULL
WHERE job_id=@job_id AND status='running';";
            await conn.ExecuteAsync(new CommandDefinition(cancelSql, new { job_id = jobId }, transaction: tx, cancellationToken: ct));
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
SET status='deleted',
    updated_at=now(),
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

        await tx.CommitAsync(ct);
        return true;
    }

    public static async Task UpdateProgressAsync(NpgsqlDataSource ds, Guid jobId, string phase, int? current, int? total, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        int? percent = null;
        if (current.HasValue && total.HasValue && total.Value > 0)
            percent = Math.Clamp((int)Math.Round((current.Value * 100d) / total.Value, MidpointRounding.AwayFromZero), 0, 100);

        const string sql = @"
UPDATE ingestion_jobs
SET payload = jsonb_set(
        COALESCE(payload, '{}'::jsonb),
        '{progress}',
        jsonb_strip_nulls(jsonb_build_object(
            'phase', @phase,
            'current', @current,
            'total', @total,
            'percent', @percent
        )),
        true
    ),
    locked_at = CASE WHEN status='running' THEN now() ELSE locked_at END
WHERE job_id=@job_id;";

        await conn.ExecuteAsync(new CommandDefinition(sql, new { job_id = jobId, phase, current, total, percent }, cancellationToken: ct));
    }

    private sealed record JobCancelState(string? Status, bool CancelRequested);

    private sealed record JobAndDocumentCancelState(
        string? JobStatus,
        bool JobCancelRequested,
        bool DocumentAutoIngestPaused,
        string? DocumentAutoIngestPauseReason);

    private sealed record DocumentVersionState(int? IngestionVersion, bool AutoIngestPaused, string? AutoIngestPauseReason);

    private sealed record IngestionJobRow(Guid JobId, Guid TenantId, string Action, string DocPath, string? Category, string Payload)
    {
        public Guid? DocIdFromPayload
        {
            get
            {
                try
                {
                    using var d = JsonDocument.Parse(Payload);
                    if (d.RootElement.TryGetProperty("docId", out var el) && el.ValueKind == JsonValueKind.String)
                        return Guid.Parse(el.GetString()!);
                }
                catch { }
                return null;
            }
        }

        public int? VersionFromPayload
        {
            get
            {
                try
                {
                    using var d = JsonDocument.Parse(Payload);
                    if (d.RootElement.TryGetProperty("version", out var el) && el.ValueKind == JsonValueKind.Number)
                        return el.GetInt32();
                }
                catch { }
                return null;
            }
        }
    }
}
