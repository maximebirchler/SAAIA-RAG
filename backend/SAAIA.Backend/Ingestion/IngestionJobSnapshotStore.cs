using Dapper;
using Npgsql;

internal static class IngestionJobSnapshotStore
{
    public static async Task FreezeTerminalSnapshotAsync(NpgsqlDataSource ds, Guid jobId, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await FreezeTerminalSnapshotAsync(conn, jobId, null, ct);
    }

    public static async Task FreezeTerminalSnapshotAsync(NpgsqlConnection conn, Guid jobId, NpgsqlTransaction? tx, CancellationToken ct)
    {
        const string sql = """
UPDATE ingestion_jobs j
SET payload = jsonb_set(
        COALESCE(j.payload, '{}'::jsonb),
        '{snapshot}',
        jsonb_strip_nulls(jsonb_build_object(
            'documentStatus', (
                SELECT d.status
                FROM documents d
                WHERE d.tenant_id = j.tenant_id
                  AND d.doc_path = j.doc_path
                LIMIT 1
            ),
            'documentIngestionVersion', (
                CASE
                    WHEN jsonb_typeof(j.payload->'version') = 'number'
                        THEN (j.payload->>'version')::int
                    ELSE (
                        SELECT d.ingestion_version
                        FROM documents d
                        WHERE d.tenant_id = j.tenant_id
                          AND d.doc_path = j.doc_path
                        LIMIT 1
                    )
                END
            ),
            'documentIndexedVersion', (
                CASE
                    WHEN j.status = 'done' THEN (
                        SELECT d.indexed_version
                        FROM documents d
                        WHERE d.tenant_id = j.tenant_id
                          AND d.doc_path = j.doc_path
                        LIMIT 1
                    )
                    WHEN jsonb_typeof(j.payload->'indexedVersionBefore') = 'number'
                        THEN (j.payload->>'indexedVersionBefore')::int
                    ELSE (
                        SELECT d.indexed_version
                        FROM documents d
                        WHERE d.tenant_id = j.tenant_id
                          AND d.doc_path = j.doc_path
                        LIMIT 1
                    )
                END
            ),
            'documentAutoIngestPaused', (
                SELECT COALESCE(d.auto_ingest_paused, false)
                FROM documents d
                WHERE d.tenant_id = j.tenant_id
                  AND d.doc_path = j.doc_path
                LIMIT 1
            ),
            'documentAutoIngestPauseReason', (
                SELECT d.auto_ingest_pause_reason
                FROM documents d
                WHERE d.tenant_id = j.tenant_id
                  AND d.doc_path = j.doc_path
                LIMIT 1
            ),
            'progressPhase', j.payload #>> '{progress,phase}',
            'progressCurrent', CASE
                WHEN jsonb_typeof(j.payload->'progress'->'current') = 'number'
                    THEN (j.payload->'progress'->>'current')::int
                ELSE NULL
            END,
            'progressTotal', CASE
                WHEN jsonb_typeof(j.payload->'progress'->'total') = 'number'
                    THEN (j.payload->'progress'->>'total')::int
                ELSE NULL
            END,
            'progressPercent', CASE
                WHEN jsonb_typeof(j.payload->'progress'->'percent') = 'number'
                    THEN (j.payload->'progress'->>'percent')::int
                ELSE NULL
            END,
            'capturedAt', to_jsonb(now())
        )),
        true
    )
WHERE j.job_id = @job_id
  AND j.status IN ('done', 'failed', 'canceled');
""";

        await conn.ExecuteAsync(new CommandDefinition(sql, new { job_id = jobId }, transaction: tx, cancellationToken: ct));
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

    public static async Task StoreResumeCheckpointAsync(
        NpgsqlDataSource ds,
        Guid jobId,
        string sourceHash,
        long fileSize,
        int chunkTotal,
        string embeddingModel,
        string embeddingInputFormat,
        CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = @"
UPDATE ingestion_jobs
SET payload = jsonb_set(
        COALESCE(payload, '{}'::jsonb),
        '{resume}',
        jsonb_build_object(
            'sourceHash', @sourceHash,
            'fileSize', @fileSize,
            'chunkTotal', @chunkTotal,
            'embeddingModel', @embeddingModel,
            'embeddingInputFormat', @embeddingInputFormat
        ),
        true
    )
WHERE job_id=@job_id;";

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            job_id = jobId,
            sourceHash,
            fileSize,
            chunkTotal,
            embeddingModel,
            embeddingInputFormat
        }, cancellationToken: ct));
    }

    public static async Task<JobRepo.ResumeCheckpointState?> GetResumeCheckpointAsync(NpgsqlDataSource ds, Guid jobId, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
SELECT
    CASE WHEN jsonb_typeof(payload->'progress'->'current')='number' THEN (payload->'progress'->>'current')::int ELSE NULL END AS "ProgressCurrent",
    CASE WHEN jsonb_typeof(payload->'progress'->'total')='number' THEN (payload->'progress'->>'total')::int ELSE NULL END AS "ProgressTotal",
    payload #>> '{resume,sourceHash}' AS "SourceHash",
    CASE WHEN jsonb_typeof(payload->'resume'->'fileSize')='number' THEN (payload->'resume'->>'fileSize')::bigint ELSE NULL END AS "FileSize",
    CASE WHEN jsonb_typeof(payload->'resume'->'chunkTotal')='number' THEN (payload->'resume'->>'chunkTotal')::int ELSE NULL END AS "ChunkTotal",
    payload #>> '{resume,embeddingModel}' AS "EmbeddingModel",
    payload #>> '{resume,embeddingInputFormat}' AS "EmbeddingInputFormat"
FROM ingestion_jobs
WHERE job_id=@job_id
LIMIT 1;
""";

        return await conn.QueryFirstOrDefaultAsync<JobRepo.ResumeCheckpointState>(
            new CommandDefinition(sql, new { job_id = jobId }, cancellationToken: ct));
    }
}
