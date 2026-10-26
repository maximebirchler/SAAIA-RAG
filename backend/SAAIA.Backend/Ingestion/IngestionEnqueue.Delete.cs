using Dapper;
using Npgsql;

static partial class IngestionEnqueue
{
    public static async Task<(Guid DocId, int Version, Guid JobId)> EnqueueDeleteAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string docPath,
        CancellationToken ct,
        bool requiresSourceAbsence = false,
        string? enqueueSource = null)
    {
        docPath = PathUtil.NormalizeRelativePath(docPath);
        var fallbackDocId = IdUtil.DeterministicGuid($"{tenantId}:{docPath}");
        var docId = fallbackDocId;
        var docName = Path.GetFileName(docPath);
        var category = IngestionCategoryResolver.DeriveFromDocumentPath(docPath);

        const string docSql = """
INSERT INTO documents(
  tenant_id, doc_id, doc_path, doc_name, category,
  status, updated_at, missing_since, ingestion_version, indexed_version,
  auto_ingest_paused, auto_ingest_paused_at, auto_ingest_pause_reason
)
VALUES(
  @tenant_id, @doc_id, @doc_path, @doc_name, @category,
  'missing', now(), now(), @reserved_generation, 0,
  false, NULL, NULL
)
ON CONFLICT (tenant_id, doc_path)
DO UPDATE SET
  status = 'missing',
  updated_at = now(),
  missing_since = COALESCE(documents.missing_since, now()),
  ingestion_version = EXCLUDED.ingestion_version,
  auto_ingest_paused = false,
  auto_ingest_paused_at = NULL,
  auto_ingest_pause_reason = NULL
WHERE documents.doc_id=EXCLUDED.doc_id AND GREATEST(COALESCE(documents.ingestion_version,0),COALESCE(documents.indexed_version,0)) < EXCLUDED.ingestion_version
RETURNING doc_id, ingestion_version, COALESCE(indexed_version, 0) AS indexed_version;
""";

        // A slower reservation must not adopt another action's newer generation or document ID.
        (Guid doc_id, int ingestion_version, int indexed_version) returned = default;
        for (var admissionAttempt = 0; admissionAttempt < 32; admissionAttempt++)
        {
            docId = await IngestionGenerationRegistry.ResolveDocumentIdAsync(conn, tenantId, docPath, fallbackDocId, ct);
            var reservedGeneration = await IngestionGenerationRegistry.ReserveAsync(conn, tenantId, docId, ct);
            returned = await conn.QuerySingleOrDefaultAsync<(Guid doc_id, int ingestion_version, int indexed_version)>(
                new CommandDefinition(docSql, new
                {
                    tenant_id = tenantId,
                    doc_id = docId,
                    doc_path = docPath,
                    doc_name = docName,
                    category,
                    reserved_generation = reservedGeneration
                }, cancellationToken: ct)
            );
            if (returned.doc_id != Guid.Empty) break;
        }
        if (returned.doc_id == Guid.Empty) throw new InvalidOperationException("ingestion_admission_contention");

        const string cancelUpsert = """
UPDATE ingestion_jobs
SET status='failed',
    finished_at=now(),
    last_error='source_removed_during_ingestion',
    locked_by=NULL,
    locked_at=NULL,
    available_at=now(),
    payload = jsonb_set(
        COALESCE(payload, '{}'::jsonb),
        '{control}',
        COALESCE(payload->'control', '{}'::jsonb)
            || jsonb_build_object(
                'cancelRequested', true,
                'requestedAction', 'cancel'::text),
        true)
WHERE tenant_id=@tenant_id
  AND doc_path=@doc_path
  AND action='upsert'
  AND status IN ('queued','running','paused');
""";

        await conn.ExecuteAsync(new CommandDefinition(cancelUpsert, new
        {
            tenant_id = tenantId,
            doc_path = docPath
        }, cancellationToken: ct));

        var payload = IngestionJobPayloadJson.Serialize(
            returned.doc_id,
            returned.ingestion_version,
            source: enqueueSource,
            indexedVersionBefore: returned.indexed_version,
            requiresSourceAbsence: requiresSourceAbsence);
        var jobId = Guid.NewGuid();

        const string jobSql = """
INSERT INTO ingestion_jobs(job_id, tenant_id, action, doc_path, category, status, payload, available_at)
VALUES(@job_id, @tenant_id, 'delete', @doc_path, NULL, 'queued', @payload::jsonb, now())
ON CONFLICT (tenant_id, doc_path, action) WHERE status IN ('queued','running','paused')
DO UPDATE SET
  available_at = CASE
      WHEN ingestion_jobs.status='running' THEN ingestion_jobs.available_at
      ELSE now()
  END,
  payload = CASE
      WHEN ingestion_jobs.status='running'
          THEN jsonb_set(COALESCE(ingestion_jobs.payload,'{}'::jsonb),'{pendingDeleteIntent}',
              jsonb_build_object('version',EXCLUDED.payload->'version',
                  'requiresSourceAbsence',COALESCE(EXCLUDED.payload->'requiresSourceAbsence','false'::jsonb)),true)
      WHEN (LOWER(COALESCE(ingestion_jobs.payload #>> '{control,cancelRequested}', '')) = 'true')
          THEN jsonb_set(
              COALESCE(EXCLUDED.payload, '{}'::jsonb),
              '{control}',
              COALESCE(EXCLUDED.payload->'control', '{}'::jsonb)
                  || jsonb_build_object(
                      'cancelRequested', true,
                      'requestedAction', COALESCE(ingestion_jobs.payload #>> '{control,requestedAction}', 'cancel')::text),
              true)
      ELSE EXCLUDED.payload
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
                payload
            }, cancellationToken: ct)
        );

        return (returned.doc_id, returned.ingestion_version, effectiveJobId);
    }
}
