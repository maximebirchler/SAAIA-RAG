using Dapper;
using Npgsql;
using System.Security.Cryptography;

static partial class IngestionEnqueue
{
    public static async Task<ReturnedMissingIndexedDocumentOutcome> TryRestoreMissingIndexedDocumentAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string docPath,
        FileInfo fi,
        CancellationToken ct)
    {
        docPath = PathUtil.NormalizeRelativePath(docPath);
        if (!fi.Exists)
            return ReturnedMissingIndexedDocumentOutcome.None;

        const string stateSql = """
SELECT
  status AS "Status",
  COALESCE(indexed_version, 0) AS "IndexedVersion",
  file_size AS "FileSize",
  file_mtime AS "FileMtime",
  encode(content_hash, 'hex') AS "ContentHashHex"
FROM documents
WHERE tenant_id=@tenant_id
  AND doc_path=@doc_path
LIMIT 1;
""";

        var state = await conn.QueryFirstOrDefaultAsync<RestorableDocumentState>(
            new CommandDefinition(stateSql, new
            {
                tenant_id = tenantId,
                doc_path = docPath
            }, cancellationToken: ct));

        if (state is null
            || !string.Equals(state.Status, "missing", StringComparison.OrdinalIgnoreCase)
            || state.IndexedVersion <= 0)
        {
            return ReturnedMissingIndexedDocumentOutcome.None;
        }

        var sameFile = IngestionAutoUpsertGuard.SameFile(state.FileSize, state.FileMtime, fi.Length, fi.LastWriteTimeUtc);
        if (!sameFile && !string.IsNullOrWhiteSpace(state.ContentHashHex))
        {
            await using var fs = fi.OpenRead();
            var currentHash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
            sameFile = string.Equals(currentHash, state.ContentHashHex, StringComparison.OrdinalIgnoreCase);
        }

        if (sameFile)
        {
            const string cancelQueuedJobsSql = """
UPDATE ingestion_jobs
SET status='canceled',
    finished_at=COALESCE(finished_at, now()),
    last_error=CASE
        WHEN action='delete' THEN 'restored_before_delete'
        ELSE 'restored_before_upsert'
    END,
    locked_by=NULL,
    locked_at=NULL,
    available_at=now(),
    payload=((COALESCE(payload, '{}'::jsonb) #- '{control,cancelRequested}') #- '{control,requestedAction}')
WHERE tenant_id=@tenant_id
  AND doc_path=@doc_path
  AND action IN ('upsert','delete')
  AND status IN ('queued','paused');
""";

            await conn.ExecuteAsync(new CommandDefinition(cancelQueuedJobsSql, new
            {
                tenant_id = tenantId,
                doc_path = docPath
            }, cancellationToken: ct));

            const string requestCancelRunningJobsSql = """
UPDATE ingestion_jobs
SET payload = jsonb_set(
        jsonb_set(COALESCE(payload, '{}'::jsonb), '{control,cancelRequested}', 'true'::jsonb, true),
        '{control,requestedAction}',
        to_jsonb('cancel'::text),
        true)
WHERE tenant_id=@tenant_id
  AND doc_path=@doc_path
  AND action IN ('upsert','delete')
  AND status='running';
""";

            await conn.ExecuteAsync(new CommandDefinition(requestCancelRunningJobsSql, new
            {
                tenant_id = tenantId,
                doc_path = docPath
            }, cancellationToken: ct));

            const string restoreDocSql = """
UPDATE documents
SET status='indexed',
    updated_at=now(),
    last_seen_at=now(),
    missing_since=NULL,
    file_size=@file_size,
    file_mtime=@file_mtime,
    ingestion_version=GREATEST(COALESCE(ingestion_version, 0), COALESCE(indexed_version, 0)),
    auto_ingest_paused=false,
    auto_ingest_paused_at=NULL,
    auto_ingest_pause_reason=NULL
WHERE tenant_id=@tenant_id
  AND doc_path=@doc_path
  AND status='missing'
  AND COALESCE(indexed_version, 0) > 0;
""";

            var restored = await conn.ExecuteAsync(new CommandDefinition(restoreDocSql, new
            {
                tenant_id = tenantId,
                doc_path = docPath,
                file_size = fi.Length,
                file_mtime = DateTime.SpecifyKind(fi.LastWriteTimeUtc, DateTimeKind.Utc)
            }, cancellationToken: ct));

            return restored > 0
                ? ReturnedMissingIndexedDocumentOutcome.RestoredWithoutReingestion
                : ReturnedMissingIndexedDocumentOutcome.None;
        }

        const string cancelQueuedDeleteJobsSql = """
UPDATE ingestion_jobs
SET status='canceled',
    finished_at=COALESCE(finished_at, now()),
    last_error='restored_before_delete',
    locked_by=NULL,
    locked_at=NULL,
    available_at=now(),
    payload=((COALESCE(payload, '{}'::jsonb) #- '{control,cancelRequested}') #- '{control,requestedAction}')
WHERE tenant_id=@tenant_id
  AND doc_path=@doc_path
  AND action='delete'
  AND status IN ('queued','paused');
""";

        await conn.ExecuteAsync(new CommandDefinition(cancelQueuedDeleteJobsSql, new
        {
            tenant_id = tenantId,
            doc_path = docPath
        }, cancellationToken: ct));

        const string requestCancelRunningDeleteJobsSql = """
UPDATE ingestion_jobs
SET payload = jsonb_set(
        jsonb_set(COALESCE(payload, '{}'::jsonb), '{control,cancelRequested}', 'true'::jsonb, true),
        '{control,requestedAction}',
        to_jsonb('cancel'::text),
        true)
WHERE tenant_id=@tenant_id
  AND doc_path=@doc_path
  AND action='delete'
  AND status='running';
""";

        await conn.ExecuteAsync(new CommandDefinition(requestCancelRunningDeleteJobsSql, new
        {
            tenant_id = tenantId,
            doc_path = docPath
        }, cancellationToken: ct));

        const string reactivateDocSql = """
UPDATE documents
SET status='indexed',
    updated_at=now(),
    last_seen_at=now(),
    missing_since=NULL,
    ingestion_version=GREATEST(COALESCE(ingestion_version, 0), COALESCE(indexed_version, 0)),
    auto_ingest_paused=false,
    auto_ingest_paused_at=NULL,
    auto_ingest_pause_reason=NULL
WHERE tenant_id=@tenant_id
  AND doc_path=@doc_path
  AND status='missing'
  AND COALESCE(indexed_version, 0) > 0;
""";

        var reactivated = await conn.ExecuteAsync(new CommandDefinition(reactivateDocSql, new
        {
            tenant_id = tenantId,
            doc_path = docPath
        }, cancellationToken: ct));

        return reactivated > 0
            ? ReturnedMissingIndexedDocumentOutcome.ReactivatedForReindex
            : ReturnedMissingIndexedDocumentOutcome.None;
    }

    public static async Task<bool> MarkMissingAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string docPath,
        CancellationToken ct)
    {
        docPath = PathUtil.NormalizeRelativePath(docPath);
        var docId = IdUtil.DeterministicGuid($"{tenantId}:{docPath}");
        var docName = Path.GetFileName(docPath);

        const string docSql = """
INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category,
  status, updated_at, missing_since, ingestion_version, indexed_version,
  auto_ingest_paused, auto_ingest_paused_at, auto_ingest_pause_reason
)
VALUES(
  @tenant_id, @doc_id, @doc_path, @doc_name, 'general',
  'missing', now(), now(), 0, 0,
  false, NULL, NULL
)
ON CONFLICT (tenant_id, doc_path)
DO UPDATE SET
  status = CASE WHEN documents.status='deleted' THEN documents.status ELSE 'missing' END,
  updated_at = now(),
  missing_since = COALESCE(documents.missing_since, now())
  , auto_ingest_paused = false
  , auto_ingest_paused_at = NULL
  , auto_ingest_pause_reason = NULL
WHERE documents.status <> 'deleted'
  AND (documents.status <> 'missing' OR documents.missing_since IS NULL)
RETURNING doc_id;
""";

        var affected = await conn.ExecuteScalarAsync<Guid?>(
            new CommandDefinition(docSql, new
            {
                tenant_id = tenantId,
                doc_id = docId,
                doc_path = docPath,
                doc_name = docName
            }, cancellationToken: ct)
        );

        const string failQueuedOrPausedUpsert = """
UPDATE ingestion_jobs
SET status='failed',
    finished_at=now(),
    last_error='file_missing',
    locked_by=NULL,
    locked_at=NULL,
    available_at=now(),
    payload=((COALESCE(payload, '{}'::jsonb) #- '{control,cancelRequested}') #- '{control,requestedAction}')
WHERE tenant_id=@tenant_id AND doc_path=@doc_path
  AND action='upsert' AND status IN ('queued','paused');
""";

        await conn.ExecuteAsync(new CommandDefinition(failQueuedOrPausedUpsert, new
        {
            tenant_id = tenantId,
            doc_path = docPath
        }, cancellationToken: ct));

        const string failRunningUpsert = """
UPDATE ingestion_jobs
SET status='failed',
    finished_at=COALESCE(finished_at, now()),
    last_error=COALESCE(last_error, 'file_missing'),
    locked_by=NULL,
    locked_at=NULL,
    available_at=now(),
    payload = jsonb_set(
        jsonb_set(COALESCE(payload, '{}'::jsonb), '{control,cancelRequested}', 'true'::jsonb, true),
        '{control,requestedAction}',
        to_jsonb('cancel'::text),
        true)
WHERE tenant_id=@tenant_id
  AND doc_path=@doc_path
  AND action='upsert'
  AND status='running';
""";

        await conn.ExecuteAsync(new CommandDefinition(failRunningUpsert, new
        {
            tenant_id = tenantId,
            doc_path = docPath
        }, cancellationToken: ct));

        return affected.HasValue;
    }
}
