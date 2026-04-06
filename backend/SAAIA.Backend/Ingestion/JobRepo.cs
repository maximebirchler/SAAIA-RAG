using System.Text.Json;
using Dapper;
using Npgsql;

static class JobRepo
{
    public static async Task<IngestionJob?> TryDequeueAsync(NpgsqlDataSource ds, string workerId, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        const string sql = """
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
    attempts=attempts+1,
    payload = jsonb_set(
        COALESCE(j.payload, '{}'::jsonb),
        '{control,cancelRequested}',
        'false'::jsonb,
        true
    )
FROM cte
WHERE j.job_id=cte.job_id
RETURNING
  j.job_id    AS JobId,
  j.tenant_id AS TenantId,
  j.action    AS Action,
  j.doc_path  AS DocPath,
  j.category  AS Category,
  j.payload   AS Payload;
""";

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
        const string sql = """
WITH j AS (
    SELECT
        job_id,
        tenant_id,
        CASE WHEN jsonb_typeof(payload->'docId')='string' THEN (payload->>'docId')::uuid ELSE NULL::uuid END AS doc_id,
        CASE WHEN jsonb_typeof(payload->'version')='number' THEN (payload->>'version')::int ELSE NULL::int END AS version,
        COALESCE((payload #>> '{control,cancelRequested}')::boolean, false) AS cancel_requested
    FROM ingestion_jobs
    WHERE job_id=@job_id
)
UPDATE ingestion_jobs t
SET status = CASE
        WHEN j.cancel_requested AND NOT EXISTS (
            SELECT 1
            FROM documents d
            WHERE d.tenant_id = j.tenant_id
              AND d.doc_id = j.doc_id
              AND d.indexed_version = j.version
              AND d.status = 'indexed'
        ) THEN 'canceled'
        ELSE 'done'
    END,
    finished_at=now(),
    last_error = CASE
        WHEN j.cancel_requested AND NOT EXISTS (
            SELECT 1
            FROM documents d
            WHERE d.tenant_id = j.tenant_id
              AND d.doc_id = j.doc_id
              AND d.indexed_version = j.version
              AND d.status = 'indexed'
        ) THEN COALESCE(t.last_error, 'canceled_by_admin')
        ELSE NULL
    END,
    locked_by=NULL,
    locked_at=NULL,
    available_at=now()
FROM j
WHERE t.job_id=j.job_id AND t.status='running';
""";
        await conn.ExecuteAsync(new CommandDefinition(sql, new { job_id = jobId }, cancellationToken: ct));
    }

    public static async Task MarkFailedAsync(NpgsqlDataSource ds, Guid jobId, string error, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
UPDATE ingestion_jobs
SET status = CASE
        WHEN COALESCE((payload #>> '{control,cancelRequested}')::boolean, false) THEN 'canceled'
        ELSE 'failed'
    END,
    finished_at=now(),
    last_error = CASE
        WHEN COALESCE((payload #>> '{control,cancelRequested}')::boolean, false)
            THEN COALESCE(last_error, COALESCE(@err, 'canceled_by_admin'))
        ELSE @err
    END,
    locked_by=NULL,
    locked_at=NULL,
    available_at=now()
WHERE job_id=@job_id AND status='running';
""";
        await conn.ExecuteAsync(new CommandDefinition(sql, new { job_id = jobId, err = error }, cancellationToken: ct));
    }

    public static async Task MarkCanceledAsync(NpgsqlDataSource ds, Guid jobId, string reason, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
UPDATE ingestion_jobs
SET status='canceled',
    finished_at=COALESCE(finished_at, now()),
    last_error=COALESCE(@reason, last_error),
    locked_by=NULL,
    locked_at=NULL,
    available_at=now(),
    payload = jsonb_set(
        COALESCE(payload, '{}'::jsonb),
        '{control,cancelRequested}',
        'true'::jsonb,
        true
    )
WHERE job_id=@job_id AND status IN ('queued','running','canceled','failed','done');
""";
        await conn.ExecuteAsync(new CommandDefinition(sql, new { job_id = jobId, reason }, cancellationToken: ct));
    }

    public static async Task<int> FinalizeStaleRunningAsync(NpgsqlDataSource ds, TimeSpan staleAfter, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);

        const string sql = """
UPDATE ingestion_jobs
SET status = CASE
        WHEN COALESCE((payload #>> '{control,cancelRequested}')::boolean, false) THEN 'canceled'
        ELSE 'failed'
    END,
    finished_at=now(),
    locked_at=NULL,
    locked_by=NULL,
    available_at=now(),
    last_error = CASE
        WHEN COALESCE((payload #>> '{control,cancelRequested}')::boolean, false)
            THEN COALESCE(last_error, 'canceled_stale_running')
        ELSE COALESCE(last_error, 'stale_running_timeout')
    END
WHERE status='running'
  AND (
      locked_at IS NULL
      OR locked_at < now() - (@stale_seconds * interval '1 second')
  );
""";

        return await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            stale_seconds = (int)staleAfter.TotalSeconds
        }, cancellationToken: ct));
    }

    public static async Task TouchAsync(NpgsqlDataSource ds, Guid jobId, string workerId, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);

        const string sql = """
UPDATE ingestion_jobs
SET locked_at=now()
WHERE job_id=@job_id
  AND status='running'
  AND locked_by=@worker;
""";

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            job_id = jobId,
            worker = workerId
        }, cancellationToken: ct));
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
        var row = await conn.QueryFirstOrDefaultAsync<JobCancelStateRow>(
            new CommandDefinition(sql, new { job_id = jobId }, cancellationToken: ct));

        if (row is null)
            return false;

        return string.Equals(row.Status, "canceled", StringComparison.OrdinalIgnoreCase)
               || string.Equals(row.Status, "cancelled", StringComparison.OrdinalIgnoreCase)
               || row.CancelRequested;
    }

    public static async Task UpdateProgressAsync(NpgsqlDataSource ds, Guid jobId, string phase, int? current, int? total, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        int? percent = null;
        if (current.HasValue && total.HasValue && total.Value > 0)
            percent = Math.Clamp((int)Math.Round((current.Value * 100d) / total.Value, MidpointRounding.AwayFromZero), 0, 100);

        const string sql = """
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
WHERE job_id=@job_id;
""";

        await conn.ExecuteAsync(new CommandDefinition(sql, new { job_id = jobId, phase, current, total, percent }, cancellationToken: ct));
    }

    private sealed record JobCancelStateRow(string Status, bool CancelRequested);

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
