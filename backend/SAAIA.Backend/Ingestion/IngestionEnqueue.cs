using Dapper;
using Npgsql;
using System.Security.Cryptography;

static partial class IngestionEnqueue
{
    public enum ReturnedMissingIndexedDocumentOutcome
    {
        None = 0,
        RestoredWithoutReingestion = 1,
        ReactivatedForReindex = 2
    }

    public static async Task<(Guid DocId, int Version, Guid JobId)> EnqueueUpsertAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string docPath,
        string category,
        FileInfo? fi,
        CancellationToken ct,
        bool isAutomatic = false,
        string? enqueueSource = null,
        IngestionCapabilityAProfileSeed? capabilityAProfileSeed = null)
    {
        docPath = PathUtil.NormalizeRelativePath(docPath);
        category = IngestionCategoryResolver.Normalize(category);

        var docName = Path.GetFileName(docPath);
        var docId = IdUtil.DeterministicGuid($"{tenantId}:{docPath}");

        long? fileSize = fi is null ? null : fi.Length;
        DateTime? fileMtime = fi is null ? null : DateTime.SpecifyKind(fi.LastWriteTimeUtc, DateTimeKind.Utc);

        var docSql = isAutomatic ? """
INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category,
  status, updated_at, file_size, file_mtime,
  last_seen_at, missing_since, ingestion_version, indexed_version,
  auto_ingest_paused, auto_ingest_paused_at, auto_ingest_pause_reason
)
VALUES(
  @tenant_id, @doc_id, @doc_path, @doc_name, @category,
  'pending', now(), @file_size, @file_mtime,
  now(), NULL, 1, 0,
  false, NULL, NULL
)
ON CONFLICT (tenant_id, doc_path)
DO UPDATE SET
  doc_name = EXCLUDED.doc_name,
  category = EXCLUDED.category,
  status = CASE
      WHEN documents.status='indexed' AND COALESCE(documents.indexed_version, 0) > 0 THEN 'indexed'
      ELSE 'pending'
  END,
  updated_at = now(),
  file_size = EXCLUDED.file_size,
  file_mtime = EXCLUDED.file_mtime,
  last_seen_at = now(),
  missing_since = NULL,
  ingestion_version = GREATEST(COALESCE(documents.ingestion_version, 0), COALESCE(documents.indexed_version, 0)) + 1,
  auto_ingest_paused = CASE
      WHEN documents.auto_ingest_pause_reason = 'admin_cancel' THEN documents.auto_ingest_paused
      ELSE false
  END,
  auto_ingest_paused_at = CASE
      WHEN documents.auto_ingest_pause_reason = 'admin_cancel' THEN documents.auto_ingest_paused_at
      ELSE NULL
  END,
  auto_ingest_pause_reason = CASE
      WHEN documents.auto_ingest_pause_reason = 'admin_cancel' THEN documents.auto_ingest_pause_reason
      ELSE NULL
  END
RETURNING doc_id, ingestion_version;
""" : """
INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category,
  status, updated_at, file_size, file_mtime,
  last_seen_at, missing_since, ingestion_version, indexed_version,
  auto_ingest_paused, auto_ingest_paused_at, auto_ingest_pause_reason
)
VALUES(
  @tenant_id, @doc_id, @doc_path, @doc_name, @category,
  'pending', now(), @file_size, @file_mtime,
  now(), NULL, 1, 0,
  false, NULL, NULL
)
ON CONFLICT (tenant_id, doc_path)
DO UPDATE SET
  doc_name = EXCLUDED.doc_name,
  category = EXCLUDED.category,
  status = CASE
      WHEN documents.status='indexed' AND COALESCE(documents.indexed_version, 0) > 0 THEN 'indexed'
      ELSE 'pending'
  END,
  updated_at = now(),
  file_size = EXCLUDED.file_size,
  file_mtime = EXCLUDED.file_mtime,
  last_seen_at = now(),
  missing_since = NULL,
  ingestion_version = GREATEST(COALESCE(documents.ingestion_version, 0), COALESCE(documents.indexed_version, 0)) + 1,
  auto_ingest_paused = false,
  auto_ingest_paused_at = NULL,
  auto_ingest_pause_reason = NULL
RETURNING doc_id, ingestion_version, COALESCE(indexed_version, 0) AS indexed_version;
""";

        var returned = await conn.QuerySingleAsync<(Guid doc_id, int ingestion_version, int indexed_version)>(
            new CommandDefinition(docSql, new
            {
                tenant_id = tenantId,
                doc_id = docId,
                doc_path = docPath,
                doc_name = docName,
                category,
                file_size = fileSize,
                file_mtime = fileMtime
            }, cancellationToken: ct)
        );

        const string cancelDelete = """
UPDATE ingestion_jobs
SET status='canceled', finished_at=now(), last_error='coalesced_by_upsert'
WHERE tenant_id=@tenant_id AND doc_path=@doc_path
  AND action='delete' AND status='queued';
""";

        await conn.ExecuteAsync(new CommandDefinition(cancelDelete, new
        {
            tenant_id = tenantId,
            doc_path = docPath
        }, cancellationToken: ct));

        var payloadSource = IngestionJobPayloadJson.NormalizeSource(enqueueSource)
            ?? (isAutomatic ? "automatic" : "admin");
        var payload = IngestionJobPayloadJson.Serialize(
            returned.doc_id,
            returned.ingestion_version,
            payloadSource,
            returned.indexed_version,
            capabilityAProfileSeed);
        var jobId = Guid.NewGuid();

        const string jobSql = """
INSERT INTO ingestion_jobs(job_id, tenant_id, action, doc_path, category, status, payload, available_at)
VALUES(@job_id, @tenant_id, 'upsert', @doc_path, @category, 'queued', @payload::jsonb, now())
ON CONFLICT (tenant_id, doc_path, action) WHERE status IN ('queued','running','paused')
DO UPDATE SET
  available_at = CASE
      WHEN ingestion_jobs.status='running' THEN ingestion_jobs.available_at
      ELSE now()
  END,
  payload = CASE
      WHEN ingestion_jobs.status='running'
          THEN ingestion_jobs.payload
      WHEN COALESCE((ingestion_jobs.payload #>> '{control,cancelRequested}')::boolean, false)
          THEN jsonb_set(
              jsonb_set(EXCLUDED.payload, '{control,cancelRequested}', 'true'::jsonb, true),
              '{control,requestedAction}',
              to_jsonb(COALESCE(ingestion_jobs.payload #>> '{control,requestedAction}', 'cancel')::text),
              true)
      ELSE EXCLUDED.payload
  END,
  category = CASE
      WHEN ingestion_jobs.status='running' THEN ingestion_jobs.category
      ELSE EXCLUDED.category
  END,
  last_error = CASE
      WHEN ingestion_jobs.status='running' THEN ingestion_jobs.last_error
      ELSE NULL
  END
RETURNING job_id;
""";

        var effectiveJobId = await conn.ExecuteScalarAsync<Guid>(
            new CommandDefinition(jobSql, new
            {
                job_id = jobId,
                tenant_id = tenantId,
                doc_path = docPath,
                category,
                payload
            }, cancellationToken: ct)
        );

        return (returned.doc_id, returned.ingestion_version, effectiveJobId);
    }

    private sealed class RestorableDocumentState
    {
        public string Status { get; set; } = "";
        public int IndexedVersion { get; set; }
        public long? FileSize { get; set; }
        public DateTime? FileMtime { get; set; }
        public string? ContentHashHex { get; set; }
    }
}
