using Dapper;
using Npgsql;
using System.Text.Json;

static class IngestionEnqueue
{
    public static async Task<(Guid DocId, int Version, Guid JobId)> EnqueueUpsertAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string docPath,
        string category,
        FileInfo? fi,
        CancellationToken ct,
        bool isAutomatic = false,
        string? enqueueSource = null)
    {
        docPath = PathUtil.NormalizeRelativePath(docPath);
        category = string.IsNullOrWhiteSpace(category) ? "general" : category.Trim().ToLowerInvariant();

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
RETURNING doc_id, ingestion_version;
""";

        var returned = await conn.QuerySingleAsync<(Guid doc_id, int ingestion_version)>(
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

        var payload = JsonSerializer.Serialize(new
        {
            docId = returned.doc_id,
            version = returned.ingestion_version,
            source = string.IsNullOrWhiteSpace(enqueueSource) ? (isAutomatic ? "automatic" : "admin") : enqueueSource!.Trim().ToLowerInvariant()
        });
        var jobId = Guid.NewGuid();

        const string jobSql = """
INSERT INTO ingestion_jobs(job_id, tenant_id, action, doc_path, category, status, payload, available_at)
VALUES(@job_id, @tenant_id, 'upsert', @doc_path, @category, 'queued', @payload::jsonb, now())
ON CONFLICT (tenant_id, doc_path, action) WHERE status='queued'
DO UPDATE SET
  available_at = now(),
  payload = EXCLUDED.payload,
  category = EXCLUDED.category,
  last_error = NULL
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
  status, updated_at, missing_since, ingestion_version, indexed_version
)
VALUES(
  @tenant_id, @doc_id, @doc_path, @doc_name, 'general',
  'missing', now(), now(), 0, 0
)
ON CONFLICT (tenant_id, doc_path)
DO UPDATE SET
  status = CASE WHEN documents.status='deleted' THEN documents.status ELSE 'missing' END,
  updated_at = now(),
  missing_since = COALESCE(documents.missing_since, now())
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

        const string cancelUpsert = """
UPDATE ingestion_jobs
SET status='canceled', finished_at=now(), last_error='coalesced_by_missing'
WHERE tenant_id=@tenant_id AND doc_path=@doc_path
  AND action='upsert' AND status='queued';
""";

        await conn.ExecuteAsync(new CommandDefinition(cancelUpsert, new
        {
            tenant_id = tenantId,
            doc_path = docPath
        }, cancellationToken: ct));

        return affected.HasValue;
    }

    public static async Task<(Guid DocId, int Version, Guid JobId)> EnqueueDeleteAsync(
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
  status, updated_at, missing_since, ingestion_version, indexed_version
)
VALUES(
  @tenant_id, @doc_id, @doc_path, @doc_name, 'general',
  'missing', now(), now(), 1, 0
)
ON CONFLICT (tenant_id, doc_path)
DO UPDATE SET
  status = 'missing',
  updated_at = now(),
  missing_since = COALESCE(documents.missing_since, now()),
  ingestion_version = GREATEST(COALESCE(documents.ingestion_version, 0), COALESCE(documents.indexed_version, 0)) + 1
RETURNING doc_id, ingestion_version;
""";

        var returned = await conn.QuerySingleAsync<(Guid doc_id, int ingestion_version)>(
            new CommandDefinition(docSql, new
            {
                tenant_id = tenantId,
                doc_id = docId,
                doc_path = docPath,
                doc_name = docName
            }, cancellationToken: ct)
        );

        const string cancelUpsert = """
UPDATE ingestion_jobs
SET status='canceled', finished_at=now(), last_error='coalesced_by_delete'
WHERE tenant_id=@tenant_id AND doc_path=@doc_path
  AND action='upsert' AND status='queued';
""";

        await conn.ExecuteAsync(new CommandDefinition(cancelUpsert, new
        {
            tenant_id = tenantId,
            doc_path = docPath
        }, cancellationToken: ct));

        var payload = JsonSerializer.Serialize(new { docId = returned.doc_id, version = returned.ingestion_version });
        var jobId = Guid.NewGuid();

        const string jobSql = """
INSERT INTO ingestion_jobs(job_id, tenant_id, action, doc_path, category, status, payload, available_at)
VALUES(@job_id, @tenant_id, 'delete', @doc_path, NULL, 'queued', @payload::jsonb, now())
ON CONFLICT (tenant_id, doc_path, action) WHERE status='queued'
DO UPDATE SET
  available_at = now(),
  payload = EXCLUDED.payload,
  last_error = NULL
RETURNING job_id;
""";

        var effectiveJobId = await conn.ExecuteScalarAsync<Guid>(
            new CommandDefinition(jobSql, new
            {
                job_id = jobId,
                tenant_id = tenantId,
                doc_path = docPath,
                payload
            }, cancellationToken: ct)
        );

        return (returned.doc_id, returned.ingestion_version, effectiveJobId);
    }
}
