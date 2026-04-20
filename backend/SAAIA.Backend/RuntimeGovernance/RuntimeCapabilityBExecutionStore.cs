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
            ["campaignId"] = campaignId
        });

        const string sql = """
INSERT INTO admin_jobs(job_id, tenant_id, job_type, status, doc_id, level, payload, created_at)
VALUES(@jobId, @tenant, 'summary.generate', 'queued', @docId, 'medium', @payload::jsonb, now())
RETURNING job_id;
""";

        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(
            sql,
            new { jobId, tenant = tenantId, docId, payload },
            cancellationToken: ct));
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
  payload::text AS "PayloadJson"
FROM admin_jobs
WHERE tenant_id=@tenant
  AND job_type='summary.generate'
  AND status='queued'
  AND payload ->> 'source' = 'capability_b'
ORDER BY created_at ASC
LIMIT 1;
""",
            new { tenant = tenantId },
            cancellationToken: ct));

    internal static async Task<CapabilityBDocumentRow?> LoadCapabilityBDocumentAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        Guid docId,
        CancellationToken ct)
        => await conn.QueryFirstOrDefaultAsync<CapabilityBDocumentRow>(new CommandDefinition(
            """
SELECT
  doc_id AS "DocId",
  doc_path AS "DocPath",
  doc_name AS "DocName",
  category AS "Category",
  page_count AS "PageCount",
  COALESCE(indexed_version, 0) AS "IndexedVersion"
FROM documents
WHERE tenant_id=@tenant
  AND doc_id=@docId
LIMIT 1;
""",
            new { tenant = tenantId, docId },
            cancellationToken: ct));

    internal static async Task<string?> ComputeCapabilityBDocumentSourceHashAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        Guid docId,
        CancellationToken ct)
        => await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            """
SELECT COALESCE(encode(content_hash, 'hex'), md5(COALESCE(doc_path,'') || '|' || COALESCE(file_size::text,'') || '|' || COALESCE(file_mtime::text,'')))
FROM documents
WHERE tenant_id=@tenant
  AND doc_id=@docId
LIMIT 1;
""",
            new { tenant = tenantId, docId },
            cancellationToken: ct));

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
        CancellationToken ct)
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
            }, cancellationToken: ct));

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
}
