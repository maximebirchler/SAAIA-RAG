using Dapper;
using Npgsql;

static partial class JobRepo
{
    public static async Task StabilizeDocumentAfterCancelAsync(NpgsqlDataSource ds, Guid tenantId, string docPath, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = @"
UPDATE documents
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
        ELSE COALESCE(auto_ingest_pause_reason, 'canceled_by_worker')
    END,
    status = CASE
        WHEN COALESCE(indexed_version, 0) > 0 THEN 'indexed'
        ELSE status
    END,
    ingestion_version = GREATEST(COALESCE(ingestion_version, 0), COALESCE(indexed_version, 0)),
    updated_at = now()
WHERE tenant_id = @tenant_id
  AND doc_path = @doc_path
  AND COALESCE(status, '') NOT IN ('missing','deleted');";
        await conn.ExecuteAsync(new CommandDefinition(sql, new { tenant_id = tenantId, doc_path = docPath }, cancellationToken: ct));
    }

    public static async Task<bool> IsCanceledAsync(NpgsqlDataSource ds, Guid jobId, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
SELECT
    status AS "Status",
    COALESCE((payload #>> '{control,cancelRequested}')::boolean, false) AS "CancelRequested",
    payload #>> '{control,requestedAction}' AS "RequestedAction"
FROM ingestion_jobs
WHERE job_id=@job_id
LIMIT 1;
""";
        var row = await conn.QueryFirstOrDefaultAsync<JobCancelState>(new CommandDefinition(sql, new { job_id = jobId }, cancellationToken: ct));
        if (row is null)
            return false;

        return row.CancelRequested
               || IngestionAdminStatePolicies.IsPauseRequested(row.RequestedAction)
               || IngestionAdminStatePolicies.IsCancelRequested(row.RequestedAction)
               || string.Equals(row.Status, "canceled", StringComparison.OrdinalIgnoreCase)
               || string.Equals(row.Status, "cancelled", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<bool> IsCancellationRequestedAsync(NpgsqlDataSource ds, Guid tenantId, string docPath, Guid jobId, string action, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
SELECT
    COALESCE(j.status, '') AS "JobStatus",
    COALESCE((j.payload #>> '{control,cancelRequested}')::boolean, false) AS "JobCancelRequested",
    j.payload #>> '{control,requestedAction}' AS "RequestedAction",
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

        if (IngestionAdminStatePolicies.IsPauseRequested(row.RequestedAction))
            return true;

        return IngestionAdminStatePolicies.ShouldTreatDocumentPauseAsCancellation(
            action,
            row.DocumentAutoIngestPaused,
            row.DocumentAutoIngestPauseReason);
    }
}
