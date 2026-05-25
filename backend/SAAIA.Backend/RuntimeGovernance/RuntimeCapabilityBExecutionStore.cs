using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Npgsql;
using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal static class RuntimeCapabilityBExecutionStore
{
    private const string CapabilityKey = "capability_b.backoffice_generation";
    private const string AdminRuntimeActor = "admin_runtime_endpoint";

    internal static async Task<Guid> InsertCapabilityBAdminJobAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        Guid docId,
        string docPath,
        bool force,
        Guid campaignId,
        string? profileKey,
        int priorityScore,
        CancellationToken ct)
    {
        var jobId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["docId"] = docId,
            ["docPath"] = docPath,
            ["level"] = "medium",
            ["force"] = force,
            ["executionMode"] = "server_backoffice",
            ["runtimeCapabilityKey"] = CapabilityKey,
            ["runtimeCapabilityStatus"] = "selected",
            ["runtimeCapabilitySelected"] = true,
            ["runtimeProfileKey"] = profileKey,
            ["source"] = "capability_b",
            ["priorityScore"] = priorityScore,
            ["campaignId"] = campaignId
        });

        const string sql = """
INSERT INTO admin_jobs(job_id, tenant_id, job_type, status, doc_id, doc_path, level, payload, created_at)
VALUES(@jobId, @tenant, 'summary.generate', 'queued', @docId, @docPath, 'medium', @payload::jsonb, now())
RETURNING job_id;
""";

        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(
            sql,
            new { jobId, tenant = tenantId, docId, docPath, payload },
            cancellationToken: ct));
    }

    internal static async Task<bool> TryClaimJobAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        Guid jobId,
        DateTimeOffset claimedAt,
        string payloadJson,
        CancellationToken ct)
    {
        const string sql = """
UPDATE admin_jobs
SET status='running',
    started_at=COALESCE(started_at, @claimedAt),
    payload=@payload::jsonb,
    last_error=NULL
WHERE tenant_id=@tenant AND job_id=@jobId AND status='queued';
""";

        var updated = await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                tenant = tenantId,
                jobId,
                claimedAt = claimedAt.UtcDateTime,
                payload = payloadJson
            },
            cancellationToken: ct));

        return updated > 0;
    }

    internal static async Task<int> RequeueStaleCapabilityBWorkerJobsAsync(
        NpgsqlConnection conn,
        DateTimeOffset staleBefore,
        string executorId,
        CancellationToken ct)
    {
        const string sql = """
UPDATE admin_jobs
SET status='queued',
    started_at=NULL,
    last_error=NULL,
    payload=(
      payload
       - 'executionLeaseToken'
       - 'executionClaimedAt'
       - 'executionHeartbeatAt'
       - 'executionClaimedBy'
       - 'executionClaimCapabilityStatus'
       - 'executionClaimProfileKey'
    ) || jsonb_build_object(
      'staleLeaseRequeuedAt', @requeuedAt,
      'staleLeasePreviousClaimedBy', payload ->> 'executionClaimedBy',
      'staleLeasePreviousClaimedAt', payload ->> 'executionClaimedAt',
      'staleLeasePreviousHeartbeatAt', payload ->> 'executionHeartbeatAt'
    )
WHERE job_type='summary.generate'
  AND status='running'
  AND payload ->> 'source' = 'capability_b'
  AND COALESCE(payload ->> 'executionClaimedBy', '') = @executorId
  AND COALESCE(
        CASE
          WHEN jsonb_typeof(payload -> 'executionHeartbeatAt') = 'string'
            THEN (payload ->> 'executionHeartbeatAt')::timestamptz
          ELSE NULL::timestamptz
        END,
        CASE
          WHEN jsonb_typeof(payload -> 'executionClaimedAt') = 'string'
            THEN (payload ->> 'executionClaimedAt')::timestamptz
          ELSE NULL::timestamptz
        END,
        started_at
      ) < @staleBefore;
""";

        return await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                staleBefore = staleBefore.UtcDateTime,
                requeuedAt = DateTimeOffset.UtcNow.UtcDateTime,
                executorId
            },
            cancellationToken: ct));
    }

    internal static async Task<bool> TryHeartbeatJobAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        Guid jobId,
        string leaseToken,
        DateTimeOffset heartbeatAt,
        CancellationToken ct)
    {
        const string sql = """
UPDATE admin_jobs
SET payload = payload || jsonb_build_object('executionHeartbeatAt', @heartbeatAt)
WHERE tenant_id=@tenant
  AND job_id=@jobId
  AND status='running'
  AND payload ->> 'executionLeaseToken' = @leaseToken;
""";

        var updated = await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                tenant = tenantId,
                jobId,
                leaseToken,
                heartbeatAt = heartbeatAt.UtcDateTime
            },
            cancellationToken: ct));

        return updated > 0;
    }

    internal static async Task<bool> TryCompleteJobAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        Guid jobId,
        string leaseToken,
        DateTimeOffset finishedAt,
        string resultJson,
        CancellationToken ct,
        NpgsqlTransaction? tx = null)
    {
        const string sql = """
UPDATE admin_jobs
SET status='done',
    result=@result::jsonb,
    finished_at=@finishedAt,
    last_error=NULL
WHERE tenant_id=@tenant
  AND job_id=@jobId
  AND status='running'
  AND payload ->> 'executionLeaseToken' = @leaseToken;
""";

        var updated = await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                tenant = tenantId,
                jobId,
                leaseToken,
                result = resultJson,
                finishedAt = finishedAt.UtcDateTime
            },
            transaction: tx,
            cancellationToken: ct));

        return updated > 0;
    }

    internal static async Task<bool> TryFailJobAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        Guid jobId,
        string leaseToken,
        DateTimeOffset failedAt,
        string resultJson,
        string lastError,
        CancellationToken ct)
    {
        const string sql = """
UPDATE admin_jobs
SET status='failed',
    result=@result::jsonb,
    finished_at=@failedAt,
    last_error=@lastError
WHERE tenant_id=@tenant
  AND job_id=@jobId
  AND status='running'
  AND payload ->> 'executionLeaseToken' = @leaseToken;
""";

        var updated = await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                tenant = tenantId,
                jobId,
                leaseToken,
                result = resultJson,
                failedAt = failedAt.UtcDateTime,
                lastError
            },
            cancellationToken: ct));

        return updated > 0;
    }

    internal static async Task<bool> TryCancelJobAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        Guid jobId,
        string leaseToken,
        DateTimeOffset canceledAt,
        string resultJson,
        string reason,
        CancellationToken ct)
    {
        const string sql = """
UPDATE admin_jobs
SET status='canceled',
    result=@result::jsonb,
    canceled_at=@canceledAt,
    finished_at=@canceledAt,
    last_error=@reason
WHERE tenant_id=@tenant
  AND job_id=@jobId
  AND status='running'
  AND payload ->> 'executionLeaseToken' = @leaseToken;
""";

        var updated = await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                tenant = tenantId,
                jobId,
                leaseToken,
                result = resultJson,
                canceledAt = canceledAt.UtcDateTime,
                reason
            },
            cancellationToken: ct));

        return updated > 0;
    }

    internal static async Task<CapabilityBExecutionJobRow?> LoadCapabilityBExecutionJobAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        Guid jobId,
        CancellationToken ct)
        => await conn.QueryFirstOrDefaultAsync<CapabilityBExecutionJobRow>(new CommandDefinition(
            """
SELECT
  job_id AS "JobId",
  doc_id AS "DocId",
  COALESCE(payload ->> 'docPath', doc_path) AS "DocPath",
  COALESCE(level, payload ->> 'level', 'medium') AS "Level",
  status AS "Status",
  COALESCE(payload ->> 'executionMode', 'server_backoffice') AS "ExecutionMode",
  payload ->> 'runtimeCapabilityKey' AS "RuntimeCapabilityKey",
  payload ->> 'runtimeCapabilityStatus' AS "RuntimeCapabilityStatus",
  CASE
    WHEN jsonb_typeof(payload->'runtimeCapabilitySelected')='boolean'
      THEN (payload->>'runtimeCapabilitySelected')::boolean
    ELSE NULL::boolean
  END AS "RuntimeCapabilitySelected",
  payload ->> 'runtimeProfileKey' AS "RuntimeProfileKey",
  payload ->> 'source' AS "EnqueueSource",
  payload ->> 'executionLeaseToken' AS "ExecutionLeaseToken",
  payload ->> 'executionClaimedBy' AS "ExecutionClaimedBy",
  CASE
    WHEN jsonb_typeof(payload->'executionClaimedAt')='string' THEN (payload->>'executionClaimedAt')::timestamptz
    ELSE NULL::timestamptz
  END AS "ExecutionClaimedAt",
  CASE
    WHEN jsonb_typeof(payload->'campaignId')='string' THEN (payload->>'campaignId')::uuid
    ELSE NULL::uuid
  END AS "CampaignId",
  CASE
    WHEN jsonb_typeof(payload->'priorityScore')='number' THEN (payload->>'priorityScore')::int
    ELSE NULL::int
  END AS "PriorityScore",
  payload::text AS "PayloadJson"
FROM admin_jobs
WHERE tenant_id=@tenant
  AND job_id=@jobId
  AND job_type='summary.generate'
  AND payload ->> 'source' = 'capability_b'
LIMIT 1;
""",
            new { tenant = tenantId, jobId },
            cancellationToken: ct));

    internal static async Task<CapabilityBExecutionJobRow?> LoadNextQueuedCapabilityBExecutionJobAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        CancellationToken ct)
        => await conn.QueryFirstOrDefaultAsync<CapabilityBExecutionJobRow>(new CommandDefinition(
            """
SELECT
  job_id AS "JobId",
  doc_id AS "DocId",
  COALESCE(payload ->> 'docPath', doc_path) AS "DocPath",
  COALESCE(level, payload ->> 'level', 'medium') AS "Level",
  status AS "Status",
  COALESCE(payload ->> 'executionMode', 'server_backoffice') AS "ExecutionMode",
  payload ->> 'runtimeCapabilityKey' AS "RuntimeCapabilityKey",
  payload ->> 'runtimeCapabilityStatus' AS "RuntimeCapabilityStatus",
  CASE
    WHEN jsonb_typeof(payload->'runtimeCapabilitySelected')='boolean'
      THEN (payload->>'runtimeCapabilitySelected')::boolean
    ELSE NULL::boolean
  END AS "RuntimeCapabilitySelected",
  payload ->> 'runtimeProfileKey' AS "RuntimeProfileKey",
  payload ->> 'source' AS "EnqueueSource",
  payload ->> 'executionLeaseToken' AS "ExecutionLeaseToken",
  payload ->> 'executionClaimedBy' AS "ExecutionClaimedBy",
  CASE
    WHEN jsonb_typeof(payload->'executionClaimedAt')='string' THEN (payload->>'executionClaimedAt')::timestamptz
    ELSE NULL::timestamptz
  END AS "ExecutionClaimedAt",
  CASE
    WHEN jsonb_typeof(payload->'campaignId')='string' THEN (payload->>'campaignId')::uuid
    ELSE NULL::uuid
  END AS "CampaignId",
  CASE
    WHEN jsonb_typeof(payload->'priorityScore')='number' THEN (payload->>'priorityScore')::int
    ELSE NULL::int
  END AS "PriorityScore",
  payload::text AS "PayloadJson"
FROM admin_jobs
WHERE tenant_id=@tenant
  AND job_type='summary.generate'
  AND status='queued'
  AND payload ->> 'source' = 'capability_b'
ORDER BY
  COALESCE(
    CASE
      WHEN jsonb_typeof(payload->'priorityScore')='number' THEN (payload->>'priorityScore')::int
      ELSE NULL::int
    END,
    0
  ) DESC,
  created_at ASC
LIMIT 1;
""",
            new { tenant = tenantId },
            cancellationToken: ct));

    internal static async Task<CapabilityBDocumentRow?> LoadCapabilityBDocumentAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        Guid docId,
        CancellationToken ct,
        NpgsqlTransaction? tx = null)
    {
        var row = await conn.QueryFirstOrDefaultAsync<CapabilityBDocumentStoreRow>(new CommandDefinition(
            """
SELECT
  d.doc_id AS "DocId",
  d.doc_path AS "DocPath",
  d.doc_name AS "DocName",
  d.category AS "Category",
  d.page_count AS "PageCount",
  COALESCE(d.indexed_version, 0) AS "IndexedVersion",
  d.auto_ingest_paused AS "AutoIngestPaused",
  d.auto_ingest_pause_reason AS "AutoIngestPauseReason",
  profile.language AS "ProfileLanguage",
  saaia_document_summary_source_hash(d.content_hash, d.doc_path, d.file_size, d.file_mtime, d.indexed_version) AS "SourceHash",
  revision.revision_id AS "RevisionId",
  run.payload ->> 'extractionSource' AS "ExtractionSource",
  run.payload #>> '{extractionQuality,textStatus}' AS "RunTextStatus",
  CASE
    WHEN LOWER(COALESCE(run.payload #>> '{extractionQuality,ocrRecommended}', '')) IN ('true', 'false')
      THEN (run.payload #>> '{extractionQuality,ocrRecommended}')::boolean
    ELSE NULL::boolean
  END AS "RunOcrRecommended",
  CASE
    WHEN COALESCE(run.payload #>> '{extractionQuality,pageCount}', '') ~ '^[0-9]+$'
      THEN (run.payload #>> '{extractionQuality,pageCount}')::int
    ELSE NULL::int
  END AS "RunPageCount",
  CASE
    WHEN COALESCE(run.payload #>> '{extractionQuality,textPageCount}', '') ~ '^[0-9]+$'
      THEN (run.payload #>> '{extractionQuality,textPageCount}')::int
    ELSE NULL::int
  END AS "RunTextPageCount",
  CASE
    WHEN COALESCE(run.payload #>> '{extractionQuality,emptyPageCount}', '') ~ '^[0-9]+$'
      THEN (run.payload #>> '{extractionQuality,emptyPageCount}')::int
    ELSE NULL::int
  END AS "RunEmptyPageCount",
  CASE
    WHEN COALESCE(run.payload #>> '{extractionQuality,sparsePageCount}', '') ~ '^[0-9]+$'
      THEN (run.payload #>> '{extractionQuality,sparsePageCount}')::int
    ELSE NULL::int
  END AS "RunSparsePageCount",
  CASE
    WHEN COALESCE(run.payload #>> '{extractionQuality,totalWordCount}', '') ~ '^[0-9]+$'
      THEN (run.payload #>> '{extractionQuality,totalWordCount}')::int
    ELSE NULL::int
  END AS "RunTotalWordCount",
  CASE
    WHEN COALESCE(run.payload #>> '{extractionQuality,totalCharCount}', '') ~ '^[0-9]+$'
      THEN (run.payload #>> '{extractionQuality,totalCharCount}')::int
    ELSE NULL::int
  END AS "RunTotalCharCount",
  CASE
    WHEN COALESCE(run.payload #>> '{extractionQuality,textPageRatio}', '') ~ '^[0-9]+(\.[0-9]+)?$'
      THEN (run.payload #>> '{extractionQuality,textPageRatio}')::double precision
    ELSE NULL::double precision
  END AS "RunTextPageRatio",
  run.payload #>> '{extractionQuality,signals}' AS "RunSignalsJson",
  CASE
    WHEN LOWER(COALESCE(run.payload ->> 'ocrAttempted', '')) IN ('true', 'false')
      THEN (run.payload ->> 'ocrAttempted')::boolean
    ELSE NULL::boolean
  END AS "OcrAttempted",
  CASE
    WHEN LOWER(COALESCE(run.payload ->> 'ocrApplied', '')) IN ('true', 'false')
      THEN (run.payload ->> 'ocrApplied')::boolean
    ELSE NULL::boolean
  END AS "OcrApplied",
  run.payload ->> 'ocrLanguages' AS "OcrLanguages",
  run.payload #>> '{ocrDiagnostics,failureReason}' AS "OcrFailureReason",
  run.payload #>> '{ocrDiagnostics,imagePageDiagnostics}' AS "OcrImagePageDiagnosticsJson",
  page_quality.page_count AS "PageQualityPageCount",
  page_quality.empty_page_count AS "PageQualityEmptyPageCount",
  page_quality.sparse_page_count AS "PageQualitySparsePageCount",
  page_quality.signals AS "PageQualitySignals"
FROM documents d
LEFT JOIN LATERAL (
  SELECT r.revision_id, r.published_at
  FROM document_revisions r
  WHERE r.tenant_id = d.tenant_id
    AND r.doc_id = d.doc_id
    AND r.indexed_version = COALESCE(d.indexed_version, 0)
  ORDER BY r.published_at DESC NULLS LAST
  LIMIT 1
) revision ON true
LEFT JOIN LATERAL (
  SELECT NULLIF(BTRIM(p.language), 'und') AS language
  FROM document_profiles p
  JOIN document_revisions r
    ON r.revision_id = p.revision_id
   AND r.tenant_id = p.tenant_id
   AND r.doc_id = p.doc_id
  WHERE p.tenant_id = d.tenant_id
    AND p.doc_id = d.doc_id
    AND r.indexed_version = COALESCE(d.indexed_version, 0)
  ORDER BY
    (NULLIF(BTRIM(p.language), 'und') IS NULL) ASC,
    r.published_at DESC NULLS LAST,
    p.profile_version ASC
  LIMIT 1
) profile ON true
LEFT JOIN LATERAL (
  SELECT pr.payload
  FROM document_processing_runs pr
  WHERE pr.tenant_id = d.tenant_id
    AND pr.doc_id = d.doc_id
    AND pr.action = 'upsert'
    AND pr.status = 'done'
    AND (
      pr.revision_id = revision.revision_id
      OR pr.indexed_version_after = COALESCE(d.indexed_version, 0)
    )
  ORDER BY
    (pr.revision_id = revision.revision_id) DESC,
    pr.finished_at DESC NULLS LAST,
    pr.started_at DESC NULLS LAST
  LIMIT 1
) run ON true
LEFT JOIN LATERAL (
  SELECT
    COUNT(DISTINCT pi.page_number)::int AS page_count,
    COUNT(DISTINCT pi.page_number) FILTER (WHERE pi.metadata #>> '{extractionQuality,textStatus}' = 'empty_text')::int AS empty_page_count,
    COUNT(DISTINCT pi.page_number) FILTER (WHERE pi.metadata #>> '{extractionQuality,textStatus}' = 'low_text')::int AS sparse_page_count,
    COALESCE(
      ARRAY_AGG(DISTINCT signal.signal) FILTER (WHERE signal.signal IS NOT NULL),
      ARRAY[]::text[]
    ) AS signals
  FROM document_page_index pi
  LEFT JOIN LATERAL jsonb_array_elements_text(COALESCE(pi.metadata #> '{extractionQuality,signals}', '[]'::jsonb)) signal(signal) ON true
  WHERE pi.revision_id = revision.revision_id
) page_quality ON true
WHERE d.tenant_id=@tenant
  AND d.doc_id=@docId
LIMIT 1;
""",
            new { tenant = tenantId, docId },
            transaction: tx,
            cancellationToken: ct));

        return row is null
            ? null
            : new CapabilityBDocumentRow(
                row.DocId,
                row.DocPath ?? string.Empty,
                row.DocName ?? string.Empty,
                row.Category,
                row.PageCount,
                row.IndexedVersion,
                row.ProfileLanguage,
                row.SourceHash,
                row.RevisionId,
                BuildExtractionQualitySnapshot(row));
    }

    internal static async Task<string?> ComputeCapabilityBDocumentSourceHashAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        Guid docId,
        CancellationToken ct,
        NpgsqlTransaction? tx = null)
        => await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            """
SELECT saaia_document_summary_source_hash(content_hash, doc_path, file_size, file_mtime, indexed_version)
FROM documents
WHERE tenant_id=@tenant
  AND doc_id=@docId
LIMIT 1;
""",
            new { tenant = tenantId, docId },
            transaction: tx,
            cancellationToken: ct));

    internal static async Task<string?> ComputeCapabilityBDocumentSourceHashForUpdateAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        Guid docId,
        CancellationToken ct,
        NpgsqlTransaction tx)
        => await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            """
SELECT saaia_document_summary_source_hash(content_hash, doc_path, file_size, file_mtime, indexed_version)
FROM documents
WHERE tenant_id=@tenant
  AND doc_id=@docId
LIMIT 1
FOR UPDATE;
""",
            new { tenant = tenantId, docId },
            transaction: tx,
            cancellationToken: ct));

    internal static async Task<bool> RevisionMatchesCurrentDocumentSnapshotAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        Guid docId,
        Guid revisionId,
        CancellationToken ct,
        NpgsqlTransaction tx)
        => await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
SELECT EXISTS (
  SELECT 1
  FROM documents d
  JOIN document_revisions r
    ON r.tenant_id = d.tenant_id
   AND r.doc_id = d.doc_id
   AND r.indexed_version = COALESCE(d.indexed_version, 0)
  WHERE d.tenant_id=@tenant
    AND d.doc_id=@docId
    AND r.revision_id=@revisionId
);
""",
            new { tenant = tenantId, docId, revisionId },
            transaction: tx,
            cancellationToken: ct));

    internal static Task UpsertDocumentSummaryAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        Guid docId,
        string level,
        string? docLanguage,
        string sourceHash,
        string summaryText,
        string? summaryMetaJson,
        CancellationToken ct,
        NpgsqlTransaction? tx = null)
    {
        var cleanLevel = PostgresTextSanitizer.Clean(level);
        var cleanDocLanguage = PostgresTextSanitizer.CleanOrNull(docLanguage);
        var cleanSourceHash = PostgresTextSanitizer.Clean(sourceHash);
        var cleanSummaryText = PostgresTextSanitizer.Clean(summaryText);
        var cleanSummaryMetaJson = PostgresTextSanitizer.CleanJson(summaryMetaJson);

        return conn.ExecuteAsync(new CommandDefinition(
            """
INSERT INTO document_summaries(tenant_id, doc_id, level, doc_language, source_hash, summary_text, summary_meta, created_at, updated_at)
VALUES(@tenant, @docId, @level, @docLanguage, @sourceHash, @summaryText, @summaryMeta::jsonb, now(), now())
ON CONFLICT (tenant_id, doc_id, level)
DO UPDATE SET
  doc_language = EXCLUDED.doc_language,
  source_hash = EXCLUDED.source_hash,
  summary_text = EXCLUDED.summary_text,
  summary_meta = EXCLUDED.summary_meta,
  updated_at = now();
""",
            new
            {
                tenant = tenantId,
                docId,
                level = cleanLevel,
                docLanguage = cleanDocLanguage,
                sourceHash = cleanSourceHash,
                summaryText = cleanSummaryText,
                summaryMeta = (object?)cleanSummaryMetaJson ?? DBNull.Value
            },
            transaction: tx,
            cancellationToken: ct));
    }

    internal static async Task RecordCapabilityBSummaryCompletedAsync(
        NpgsqlConnection conn,
        Guid jobId,
        Guid docId,
        string docPath,
        string level,
        string sourceHash,
        int summaryLength,
        string? profileKey,
        Guid? campaignId,
        string? runtimeCapabilityStatus,
        CancellationToken ct,
        NpgsqlTransaction? tx = null)
    {
        using var activity = RuntimeGovernanceTelemetry.StartCapabilityBOperationActivity("capability_b_summary_completed");
        var sw = Stopwatch.StartNew();
        try
        {
            const string sql = """
INSERT INTO runtime_capability_events(
  event_id,
  capability_key,
  profile_key,
  event_type,
  actor,
  reason,
  details,
  occurred_at)
VALUES(
  @event_id,
  @capability_key,
  @profile_key,
  @event_type,
  @actor,
  @reason,
  CAST(@details AS jsonb),
  @occurred_at);
""";

            var evt = new AdminRuntimeCapabilityEventDto(
                EventId: Guid.NewGuid(),
                CapabilityKey: CapabilityKey,
                ProfileKey: profileKey,
                EventType: "capability_b_summary_completed",
                Actor: AdminRuntimeActor,
                Reason: "summary_stored",
                OccurredAt: DateTimeOffset.UtcNow,
                Details: new Dictionary<string, object?>
                {
                    ["jobId"] = jobId,
                    ["docId"] = docId,
                    ["docPath"] = docPath,
                    ["level"] = level,
                    ["sourceHash"] = sourceHash,
                    ["summaryLength"] = summaryLength,
                    ["campaignId"] = campaignId,
                    ["runtimeCapabilityStatus"] = runtimeCapabilityStatus
                });

            await conn.ExecuteAsync(new CommandDefinition(sql, new
            {
                event_id = evt.EventId,
                capability_key = evt.CapabilityKey,
                profile_key = evt.ProfileKey,
                event_type = evt.EventType,
                actor = evt.Actor,
                reason = evt.Reason,
                details = JsonSerializer.Serialize(evt.Details ?? new Dictionary<string, object?>()),
                occurred_at = evt.OccurredAt.UtcDateTime
            }, transaction: tx, cancellationToken: ct));

            RuntimeGovernanceTelemetry.RecordCapabilityEventWritten(evt.CapabilityKey, evt.EventType);

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                activity,
                "capability_b_summary_completed",
                success: true,
                durationMs: sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                activity,
                "capability_b_summary_completed",
                success: false,
                durationMs: sw.ElapsedMilliseconds,
                errorReason: ex.Message);
            throw;
        }
    }

    private static CapabilityBExtractionQualitySnapshot? BuildExtractionQualitySnapshot(CapabilityBDocumentStoreRow row)
    {
        var hasRunSnapshot = !string.IsNullOrWhiteSpace(row.RunTextStatus)
            || row.RunOcrRecommended.HasValue
            || row.OcrAttempted.HasValue
            || row.OcrApplied.HasValue
            || !string.IsNullOrWhiteSpace(row.RunSignalsJson)
            || !string.IsNullOrWhiteSpace(row.OcrFailureReason)
            || !string.IsNullOrWhiteSpace(row.OcrImagePageDiagnosticsJson);
        var hasPageSnapshot = row.PageQualityPageCount is > 0;
        var hasDocumentPause = row.AutoIngestPaused
            && !string.IsNullOrWhiteSpace(row.AutoIngestPauseReason);
        if (!hasRunSnapshot && !hasPageSnapshot && !hasDocumentPause)
            return null;

        var pageCount = row.RunPageCount ?? PositiveCount(row.PageQualityPageCount);
        var emptyPageCount = row.RunEmptyPageCount ?? CountWhenPagesExist(row.PageQualityEmptyPageCount, pageCount);
        var sparsePageCount = row.RunSparsePageCount ?? CountWhenPagesExist(row.PageQualitySparsePageCount, pageCount);
        var textPageCount = row.RunTextPageCount
            ?? (pageCount.HasValue && emptyPageCount.HasValue
                ? Math.Max(0, pageCount.Value - emptyPageCount.Value)
                : null);
        var textPageRatio = row.RunTextPageRatio
            ?? (pageCount is > 0 && textPageCount.HasValue
                ? Math.Round((double)textPageCount.Value / pageCount.Value, 4, MidpointRounding.AwayFromZero)
                : null);
        var textStatus = NormalizeQualityToken(row.RunTextStatus)
            ?? ResolveTextStatusFromPageCounts(pageCount, emptyPageCount, sparsePageCount)
            ?? "unknown";

        var signals = BuildExtractionQualitySignals(row);
        var ocrAttempted = row.OcrAttempted ?? false;
        var ocrApplied = row.OcrApplied ?? false;
        var ocrRecommended = row.RunOcrRecommended ?? IsLowQualityTextStatus(textStatus);
        var ocrFailureReason = NormalizeOptionalText(row.OcrFailureReason);
        var hasOcrFailure = HasOcrFailure(ocrFailureReason, row.OcrImagePageDiagnosticsJson, signals);
        if (hasOcrFailure && !signals.Contains("ocr_failed", StringComparer.OrdinalIgnoreCase))
            signals = signals.Concat(["ocr_failed"]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var status = ResolveExtractionQualityStatus(
            row,
            textStatus,
            ocrAttempted,
            ocrApplied,
            ocrRecommended,
            hasOcrFailure,
            signals);
        var manualReviewRecommended = RequiresManualReview(status, textStatus, hasOcrFailure, row);
        var confidence = ResolveExtractionConfidence(status, textStatus, ocrRecommended);

        return new CapabilityBExtractionQualitySnapshot(
            Status: status,
            TextStatus: textStatus,
            ExtractionConfidence: confidence,
            ManualReviewRecommended: manualReviewRecommended,
            OcrAttempted: ocrAttempted,
            OcrApplied: ocrApplied,
            OcrRecommended: ocrRecommended,
            OcrFailureReason: ocrFailureReason,
            PageCount: pageCount,
            TextPageCount: textPageCount,
            EmptyPageCount: emptyPageCount,
            SparsePageCount: sparsePageCount,
            TotalWordCount: row.RunTotalWordCount,
            TotalCharCount: row.RunTotalCharCount,
            TextPageRatio: textPageRatio,
            Signals: signals,
            Source: string.IsNullOrWhiteSpace(row.ExtractionSource)
                ? "document_revision"
                : row.ExtractionSource!.Trim());
    }

    private static int? PositiveCount(int? value)
        => value is > 0 ? value.Value : null;

    private static int? CountWhenPagesExist(int? value, int? pageCount)
        => pageCount.HasValue ? Math.Max(0, value ?? 0) : null;

    private static string[] BuildExtractionQualitySignals(CapabilityBDocumentStoreRow row)
    {
        var signals = new List<string>();
        signals.AddRange(ParseJsonStringArray(row.RunSignalsJson));
        if (row.PageQualitySignals is not null)
            signals.AddRange(row.PageQualitySignals);
        if (!string.IsNullOrWhiteSpace(row.AutoIngestPauseReason))
            signals.Add(row.AutoIngestPauseReason!);

        return signals
            .Select(NormalizeQualitySignal)
            .Where(static signal => !string.IsNullOrWhiteSpace(signal))
            .Select(static signal => signal!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToArray();
    }

    private static IReadOnlyList<string> ParseJsonStringArray(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        try
        {
            using var json = JsonDocument.Parse(value);
            if (json.RootElement.ValueKind == JsonValueKind.Array)
            {
                return json.RootElement
                    .EnumerateArray()
                    .Where(static item => item.ValueKind == JsonValueKind.String)
                    .Select(static item => item.GetString())
                    .Where(static item => !string.IsNullOrWhiteSpace(item))
                    .Select(static item => item!.Trim())
                    .ToArray();
            }

            if (json.RootElement.ValueKind == JsonValueKind.String)
                return [json.RootElement.GetString() ?? string.Empty];
        }
        catch (JsonException)
        {
            return [value.Trim()];
        }

        return [];
    }

    private static string? NormalizeQualitySignal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().Replace(' ', '_').ToLowerInvariant();
    }

    private static string? NormalizeQualityToken(string? value)
        => NormalizeQualitySignal(value);

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ResolveTextStatusFromPageCounts(int? pageCount, int? emptyPageCount, int? sparsePageCount)
    {
        if (pageCount is not > 0)
            return null;

        var empty = Math.Max(0, emptyPageCount ?? 0);
        var sparse = Math.Max(0, sparsePageCount ?? 0);
        var emptyRatio = (double)empty / pageCount.Value;
        var sparseRatio = (double)sparse / pageCount.Value;

        if (empty >= pageCount.Value)
            return "empty_text";
        if (emptyRatio >= 0.6d || sparseRatio >= 0.6d)
            return "low_text";

        return "ok";
    }

    private static bool IsLowQualityTextStatus(string? textStatus)
        => string.Equals(textStatus, "empty_text", StringComparison.OrdinalIgnoreCase)
           || string.Equals(textStatus, "low_text", StringComparison.OrdinalIgnoreCase);

    private static bool HasOcrFailure(string? failureReason, string? imageDiagnosticsJson, IReadOnlyList<string> signals)
    {
        if (ContainsQualityMarker(failureReason, "ocr_failed")
            || ContainsQualityMarker(failureReason, "failed"))
        {
            return true;
        }

        if (signals.Any(static signal =>
                signal.Contains("ocr_failed", StringComparison.OrdinalIgnoreCase)
                || signal.Contains("ocr_failure", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(imageDiagnosticsJson))
            return false;

        try
        {
            using var json = JsonDocument.Parse(imageDiagnosticsJson);
            if (json.RootElement.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var item in json.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                if (JsonPropertyContains(item, "status", "ocr_failed")
                    || JsonPropertyContains(item, "reason", "ocr_failed")
                    || JsonPropertyContains(item, "failureReason", "ocr_failed"))
                {
                    return true;
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return imageDiagnosticsJson.Contains("ocr_failed", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool JsonPropertyContains(JsonElement element, string propertyName, string marker)
        => element.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
           && ContainsQualityMarker(property.GetString(), marker);

    private static bool ContainsQualityMarker(string? value, string marker)
        => !string.IsNullOrWhiteSpace(value)
           && value.Contains(marker, StringComparison.OrdinalIgnoreCase);

    private static string ResolveExtractionQualityStatus(
        CapabilityBDocumentStoreRow row,
        string textStatus,
        bool ocrAttempted,
        bool ocrApplied,
        bool ocrRecommended,
        bool hasOcrFailure,
        IReadOnlyList<string> signals)
    {
        if (hasOcrFailure && (ocrAttempted || ocrRecommended || !ocrApplied || IsLowQualityTextStatus(textStatus)))
            return "ocr_failed_or_insufficient";
        if (string.Equals(textStatus, "empty_text", StringComparison.OrdinalIgnoreCase))
            return "manual_review_empty_text";
        if (string.Equals(textStatus, "low_text", StringComparison.OrdinalIgnoreCase))
            return "manual_review_low_text";

        var manualReviewSignal = signals.FirstOrDefault(static signal =>
            signal.Contains("manual_review", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(manualReviewSignal))
            return manualReviewSignal;

        var pauseReason = NormalizeQualityToken(row.AutoIngestPauseReason);
        if (row.AutoIngestPaused
            && !string.IsNullOrWhiteSpace(pauseReason)
            && pauseReason.Contains("manual_review", StringComparison.OrdinalIgnoreCase))
        {
            return pauseReason;
        }

        if (string.Equals(textStatus, "ok", StringComparison.OrdinalIgnoreCase))
            return "ok";

        return "unknown";
    }

    private static bool RequiresManualReview(
        string status,
        string textStatus,
        bool hasOcrFailure,
        CapabilityBDocumentStoreRow row)
        => hasOcrFailure
           || row.AutoIngestPaused && ContainsQualityMarker(row.AutoIngestPauseReason, "manual_review")
           || status.Contains("manual_review", StringComparison.OrdinalIgnoreCase)
           || status.Contains("ocr_failed", StringComparison.OrdinalIgnoreCase)
           || IsLowQualityTextStatus(textStatus);

    private static double ResolveExtractionConfidence(string status, string textStatus, bool ocrRecommended)
    {
        if (status.Contains("ocr_failed", StringComparison.OrdinalIgnoreCase))
            return 0.25d;
        if (status.Contains("manual_review_empty_text", StringComparison.OrdinalIgnoreCase))
            return 0.15d;
        if (status.Contains("manual_review_low_text", StringComparison.OrdinalIgnoreCase))
            return 0.35d;
        if (status.Contains("manual_review", StringComparison.OrdinalIgnoreCase))
            return 0.45d;
        if (string.Equals(textStatus, "ok", StringComparison.OrdinalIgnoreCase))
            return 1.0d;
        if (IsLowQualityTextStatus(textStatus))
            return string.Equals(textStatus, "empty_text", StringComparison.OrdinalIgnoreCase) ? 0.15d : 0.35d;

        return ocrRecommended ? 0.55d : 0.50d;
    }

    private sealed class CapabilityBDocumentStoreRow
    {
        public Guid DocId { get; set; }
        public string? DocPath { get; set; }
        public string? DocName { get; set; }
        public string? Category { get; set; }
        public int? PageCount { get; set; }
        public int IndexedVersion { get; set; }
        public bool AutoIngestPaused { get; set; }
        public string? AutoIngestPauseReason { get; set; }
        public string? ProfileLanguage { get; set; }
        public string? SourceHash { get; set; }
        public Guid? RevisionId { get; set; }
        public string? ExtractionSource { get; set; }
        public string? RunTextStatus { get; set; }
        public bool? RunOcrRecommended { get; set; }
        public int? RunPageCount { get; set; }
        public int? RunTextPageCount { get; set; }
        public int? RunEmptyPageCount { get; set; }
        public int? RunSparsePageCount { get; set; }
        public int? RunTotalWordCount { get; set; }
        public int? RunTotalCharCount { get; set; }
        public double? RunTextPageRatio { get; set; }
        public string? RunSignalsJson { get; set; }
        public bool? OcrAttempted { get; set; }
        public bool? OcrApplied { get; set; }
        public string? OcrLanguages { get; set; }
        public string? OcrFailureReason { get; set; }
        public string? OcrImagePageDiagnosticsJson { get; set; }
        public int? PageQualityPageCount { get; set; }
        public int? PageQualityEmptyPageCount { get; set; }
        public int? PageQualitySparsePageCount { get; set; }
        public string[]? PageQualitySignals { get; set; }
    }
}
