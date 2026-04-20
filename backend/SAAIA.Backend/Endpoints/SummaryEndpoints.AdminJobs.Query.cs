using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Auth;
using SAAIA.Backend.CatalogSnapshot;

namespace SAAIA.Backend.Endpoints;

public static partial class SummaryEndpoints
{
    private static async Task<IResult> CatalogRescanAsync(HttpContext ctx, NpgsqlDataSource ds, ILogger<SummaryEndpointsMarker> logger, IOptions<CatalogSnapshotOptions> opt)
    {
        AdminAuth.EnsureAdmin(ctx);
        var result = await CatalogSnapshotBuilder.BuildAllActiveTenantsAsync(ds, opt.Value, logger, ctx.RequestAborted);
        return Results.Ok(new { refreshed = true, snapshot = result });
    }

    internal static async Task<IResult> ListAdminJobsAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        string? type,
        int? limit,
        int? offset,
        string? dateField,
        string? dateFrom,
        string? dateTo,
        string? sortDir)
    {
        AdminAuth.EnsureAdmin(ctx);
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;
        type = string.IsNullOrWhiteSpace(type) ? null : type.Trim().ToLowerInvariant();
        var lim = Math.Clamp(limit ?? 100, 1, 500);
        var off = Math.Max(offset ?? 0, 0);
        var normalizedDateField = AdminJobsQuerySupport.NormalizeDateField(dateField);
        var normalizedSortDirection = AdminJobsQuerySupport.NormalizeSortDirection(sortDir);
        var parsedDateFrom = AdminJobsQuerySupport.ParseDateParameter(dateFrom);
        var parsedDateTo = AdminJobsQuerySupport.ParseDateParameter(dateTo);
        if (parsedDateFrom.HasValue && parsedDateTo.HasValue && parsedDateFrom.Value >= parsedDateTo.Value)
        {
            return Results.Ok(new
            {
                items = Array.Empty<object>(),
                limit = lim,
                offset = off,
                dateField = normalizedDateField,
                dateFrom = parsedDateFrom,
                dateTo = parsedDateTo,
                sortDirection = normalizedSortDirection.ToLowerInvariant()
            });
        }

        var dateColumn = normalizedDateField switch
        {
            "created" => "COALESCE(\"CreatedAt\", \"StartedAt\", \"FinishedAt\")",
            "started" => "COALESCE(\"StartedAt\", \"CreatedAt\", \"FinishedAt\")",
            _ => "COALESCE(\"FinishedAt\", \"StartedAt\", \"CreatedAt\")"
        };
        var whereParts = new List<string> { "(@type IS NULL OR lower(\"Type\")=@type)" };
        if (parsedDateFrom.HasValue)
        {
            whereParts.Add($"{dateColumn} >= @dateFrom");
        }

        if (parsedDateTo.HasValue)
        {
            whereParts.Add($"{dateColumn} < @dateTo");
        }

        await using var conn = await ds.OpenConnectionAsync(ct);
        var sql = $$"""
SELECT * FROM (
  SELECT
    job_id       AS "JobId",
    'summary'    AS "Type",
    job_type     AS "JobType",
    status       AS "Status",
    doc_id       AS "DocId",
    payload ->> 'docPath' AS "DocPath",
    level        AS "Level",
    last_error   AS "LastError",
    created_at   AS "CreatedAt",
    started_at   AS "StartedAt",
    finished_at  AS "FinishedAt",
    NULL::text   AS "ProgressPhase",
    NULL::int    AS "ProgressCurrent",
    NULL::int    AS "ProgressTotal",
    NULL::int    AS "ProgressPercent",
    NULL::boolean AS "CancelRequested",
    payload ->> 'source' AS "EnqueueSource",
    NULL::text   AS "DocumentStatus",
    NULL::int    AS "DocumentIngestionVersion",
    NULL::int    AS "DocumentIndexedVersion",
    NULL::boolean AS "DocumentAutoIngestPaused",
    NULL::text   AS "DocumentAutoIngestPauseReason",
    COALESCE(payload ->> 'executionMode', 'client_admin') AS "ExecutionMode",
    payload ->> 'runtimeCapabilityKey' AS "RuntimeCapabilityKey",
    payload ->> 'runtimeCapabilityStatus' AS "RuntimeCapabilityStatus",
    CASE
      WHEN jsonb_typeof(payload->'runtimeCapabilitySelected')='boolean'
        THEN (payload->>'runtimeCapabilitySelected')::boolean
      ELSE NULL::boolean
    END AS "RuntimeCapabilitySelected",
    CASE
      WHEN jsonb_typeof(payload->'campaignId')='string' THEN (payload->>'campaignId')::uuid
      ELSE NULL::uuid
    END AS "CampaignId",
    CASE
      WHEN jsonb_typeof(payload->'force')='boolean' THEN (payload->>'force')::boolean
      ELSE NULL::boolean
    END AS "Force",
    CASE
      WHEN jsonb_typeof(result->'stored')='boolean' THEN (result->>'stored')::boolean
      ELSE NULL::boolean
    END AS "ResultStored",
    result ->> 'sourceHash' AS "ResultSourceHash",
    CASE
      WHEN jsonb_typeof(result->'summaryLength')='number' THEN (result->>'summaryLength')::int
      ELSE NULL::int
    END AS "ResultSummaryLength",
    result ->> 'completedBy' AS "ResultCompletedBy"
  FROM admin_jobs
  WHERE tenant_id=@tenant

  UNION ALL

  SELECT
    i.job_id       AS "JobId",
    'ingestion'    AS "Type",
    i.action       AS "JobType",
    CASE
      WHEN i.status='paused' THEN 'paused'
      WHEN i.status='running'
           AND COALESCE((i.payload #>> '{control,cancelRequested}')::boolean, false)
           AND COALESCE(d.status, '') NOT IN ('missing','deleted')
           AND (
             COALESCE(i.payload #>> '{control,requestedAction}', '') = 'pause'
             OR (
               i.action='upsert'
               AND COALESCE(d.indexed_version, 0) <= 0
               AND COALESCE(d.auto_ingest_paused, false)
               AND COALESCE(d.auto_ingest_pause_reason, '') = 'admin_cancel'
             )
           )
        THEN 'paused'
      WHEN i.status='running' AND COALESCE((i.payload #>> '{control,cancelRequested}')::boolean, false) THEN 'cancel_requested'
      ELSE i.status
    END            AS "Status",
    CASE WHEN jsonb_typeof(i.payload->'docId')='string' THEN (i.payload->>'docId')::uuid ELSE NULL::uuid END AS "DocId",
    i.doc_path     AS "DocPath",
    NULL::text   AS "Level",
    i.last_error   AS "LastError",
    i.created_at   AS "CreatedAt",
    i.started_at   AS "StartedAt",
    i.finished_at  AS "FinishedAt",
    CASE
      WHEN i.status IN ('done','failed','canceled','cancelled')
           AND COALESCE(i.payload #>> '{snapshot,progressPhase}', '') <> ''
        THEN i.payload #>> '{snapshot,progressPhase}'
      ELSE i.payload #>> '{progress,phase}'
    END AS "ProgressPhase",
    CASE
      WHEN i.status IN ('done','failed','canceled','cancelled')
           AND jsonb_typeof(i.payload #> '{snapshot,progressCurrent}')='number'
        THEN (i.payload #>> '{snapshot,progressCurrent}')::int
      WHEN jsonb_typeof(i.payload->'progress'->'current')='number'
        THEN (i.payload->'progress'->>'current')::int
      ELSE NULL
    END AS "ProgressCurrent",
    CASE
      WHEN i.status IN ('done','failed','canceled','cancelled')
           AND jsonb_typeof(i.payload #> '{snapshot,progressTotal}')='number'
        THEN (i.payload #>> '{snapshot,progressTotal}')::int
      WHEN jsonb_typeof(i.payload->'progress'->'total')='number'
        THEN (i.payload->'progress'->>'total')::int
      ELSE NULL
    END AS "ProgressTotal",
    CASE
      WHEN i.status IN ('done','failed','canceled','cancelled')
           AND jsonb_typeof(i.payload #> '{snapshot,progressPercent}')='number'
        THEN (i.payload #>> '{snapshot,progressPercent}')::int
      WHEN jsonb_typeof(i.payload->'progress'->'percent')='number'
        THEN (i.payload->'progress'->>'percent')::int
      ELSE NULL
    END AS "ProgressPercent",
    COALESCE((i.payload #>> '{control,cancelRequested}')::boolean, false) AS "CancelRequested",
    i.payload ->> 'source' AS "EnqueueSource",
    COALESCE(i.payload #>> '{snapshot,documentStatus}', d.status) AS "DocumentStatus",
    CASE
      WHEN jsonb_typeof(i.payload #> '{snapshot,documentIngestionVersion}')='number'
        THEN (i.payload #>> '{snapshot,documentIngestionVersion}')::int
      WHEN jsonb_typeof(i.payload->'version')='number'
        THEN (i.payload->>'version')::int
      ELSE d.ingestion_version
    END AS "DocumentIngestionVersion",
    CASE
      WHEN jsonb_typeof(i.payload #> '{snapshot,documentIndexedVersion}')='number'
        THEN (i.payload #>> '{snapshot,documentIndexedVersion}')::int
      WHEN i.status='done'
        THEN d.indexed_version
      WHEN jsonb_typeof(i.payload->'indexedVersionBefore')='number'
        THEN (i.payload->>'indexedVersionBefore')::int
      ELSE d.indexed_version
    END AS "DocumentIndexedVersion",
    CASE
      WHEN jsonb_typeof(i.payload #> '{snapshot,documentAutoIngestPaused}')='boolean'
        THEN (i.payload #>> '{snapshot,documentAutoIngestPaused}')::boolean
      ELSE COALESCE(d.auto_ingest_paused, false)
    END AS "DocumentAutoIngestPaused",
    COALESCE(i.payload #>> '{snapshot,documentAutoIngestPauseReason}', d.auto_ingest_pause_reason) AS "DocumentAutoIngestPauseReason",
    NULL::text AS "ExecutionMode",
    NULL::text AS "RuntimeCapabilityKey",
    NULL::text AS "RuntimeCapabilityStatus",
    NULL::boolean AS "RuntimeCapabilitySelected",
    NULL::uuid AS "CampaignId",
    NULL::boolean AS "Force",
    NULL::boolean AS "ResultStored",
    NULL::text AS "ResultSourceHash",
    NULL::int AS "ResultSummaryLength",
    NULL::text AS "ResultCompletedBy"
  FROM ingestion_jobs i
  LEFT JOIN documents d
    ON d.tenant_id = i.tenant_id
   AND d.doc_path = i.doc_path
  WHERE i.tenant_id=@tenant
) j
WHERE {{string.Join(" AND ", whereParts)}}
ORDER BY
  CASE WHEN {{dateColumn}} IS NULL THEN 1 ELSE 0 END,
  {{dateColumn}} {{normalizedSortDirection}},
  "FinishedAt" {{normalizedSortDirection}},
  "StartedAt" {{normalizedSortDirection}},
  "CreatedAt" {{normalizedSortDirection}}
LIMIT @lim OFFSET @off;
""";

        var rows = await conn.QueryAsync(new CommandDefinition(
            sql,
            new { tenant = tenantId, type, lim, off, dateFrom = parsedDateFrom, dateTo = parsedDateTo },
            cancellationToken: ct));
        return Results.Ok(new
        {
            items = rows,
            limit = lim,
            offset = off,
            dateField = normalizedDateField,
            dateFrom = parsedDateFrom,
            dateTo = parsedDateTo,
            sortDirection = normalizedSortDirection.ToLowerInvariant()
        });
    }

    internal static async Task<IResult> GetAdminJobAsync(HttpContext ctx, NpgsqlDataSource ds, Guid jobId)
    {
        AdminAuth.EnsureAdmin(ctx);
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        await using var conn = await ds.OpenConnectionAsync(ct);
        var sql = """
SELECT * FROM (
  SELECT
    job_id       AS "JobId",
    'summary'    AS "Type",
    job_type     AS "JobType",
    status       AS "Status",
    doc_id       AS "DocId",
    payload ->> 'docPath' AS "DocPath",
    level        AS "Level",
    last_error   AS "LastError",
    created_at   AS "CreatedAt",
    started_at   AS "StartedAt",
    finished_at  AS "FinishedAt",
    NULL::text   AS "ProgressPhase",
    NULL::int    AS "ProgressCurrent",
    NULL::int    AS "ProgressTotal",
    NULL::int    AS "ProgressPercent",
    NULL::boolean AS "CancelRequested",
    payload ->> 'source' AS "EnqueueSource",
    NULL::text   AS "DocumentStatus",
    NULL::int    AS "DocumentIngestionVersion",
    NULL::int    AS "DocumentIndexedVersion",
    NULL::boolean AS "DocumentAutoIngestPaused",
    NULL::text   AS "DocumentAutoIngestPauseReason",
    COALESCE(payload ->> 'executionMode', 'client_admin') AS "ExecutionMode",
    payload ->> 'runtimeCapabilityKey' AS "RuntimeCapabilityKey",
    payload ->> 'runtimeCapabilityStatus' AS "RuntimeCapabilityStatus",
    CASE
      WHEN jsonb_typeof(payload->'runtimeCapabilitySelected')='boolean'
        THEN (payload->>'runtimeCapabilitySelected')::boolean
      ELSE NULL::boolean
    END AS "RuntimeCapabilitySelected",
    CASE
      WHEN jsonb_typeof(payload->'campaignId')='string' THEN (payload->>'campaignId')::uuid
      ELSE NULL::uuid
    END AS "CampaignId",
    CASE
      WHEN jsonb_typeof(payload->'force')='boolean' THEN (payload->>'force')::boolean
      ELSE NULL::boolean
    END AS "Force",
    CASE
      WHEN jsonb_typeof(result->'stored')='boolean' THEN (result->>'stored')::boolean
      ELSE NULL::boolean
    END AS "ResultStored",
    result ->> 'sourceHash' AS "ResultSourceHash",
    CASE
      WHEN jsonb_typeof(result->'summaryLength')='number' THEN (result->>'summaryLength')::int
      ELSE NULL::int
    END AS "ResultSummaryLength",
    result ->> 'completedBy' AS "ResultCompletedBy"
  FROM admin_jobs
  WHERE tenant_id=@tenant AND job_id=@jobId

  UNION ALL

  SELECT
    i.job_id       AS "JobId",
    'ingestion'    AS "Type",
    i.action       AS "JobType",
    CASE
      WHEN i.status='paused' THEN 'paused'
      WHEN i.status='running'
           AND COALESCE((i.payload #>> '{control,cancelRequested}')::boolean, false)
           AND COALESCE(d.status, '') NOT IN ('missing','deleted')
           AND (
             COALESCE(i.payload #>> '{control,requestedAction}', '') = 'pause'
             OR (
               i.action='upsert'
               AND COALESCE(d.indexed_version, 0) <= 0
               AND COALESCE(d.auto_ingest_paused, false)
               AND COALESCE(d.auto_ingest_pause_reason, '') = 'admin_cancel'
             )
           )
        THEN 'paused'
      WHEN i.status='running' AND COALESCE((i.payload #>> '{control,cancelRequested}')::boolean, false) THEN 'cancel_requested'
      ELSE i.status
    END            AS "Status",
    CASE WHEN jsonb_typeof(i.payload->'docId')='string' THEN (i.payload->>'docId')::uuid ELSE NULL::uuid END AS "DocId",
    i.doc_path     AS "DocPath",
    NULL::text   AS "Level",
    i.last_error   AS "LastError",
    i.created_at   AS "CreatedAt",
    i.started_at   AS "StartedAt",
    i.finished_at  AS "FinishedAt",
    CASE
      WHEN i.status IN ('done','failed','canceled','cancelled')
           AND COALESCE(i.payload #>> '{snapshot,progressPhase}', '') <> ''
        THEN i.payload #>> '{snapshot,progressPhase}'
      ELSE i.payload #>> '{progress,phase}'
    END AS "ProgressPhase",
    CASE
      WHEN i.status IN ('done','failed','canceled','cancelled')
           AND jsonb_typeof(i.payload #> '{snapshot,progressCurrent}')='number'
        THEN (i.payload #>> '{snapshot,progressCurrent}')::int
      WHEN jsonb_typeof(i.payload->'progress'->'current')='number'
        THEN (i.payload->'progress'->>'current')::int
      ELSE NULL
    END AS "ProgressCurrent",
    CASE
      WHEN i.status IN ('done','failed','canceled','cancelled')
           AND jsonb_typeof(i.payload #> '{snapshot,progressTotal}')='number'
        THEN (i.payload #>> '{snapshot,progressTotal}')::int
      WHEN jsonb_typeof(i.payload->'progress'->'total')='number'
        THEN (i.payload->'progress'->>'total')::int
      ELSE NULL
    END AS "ProgressTotal",
    CASE
      WHEN i.status IN ('done','failed','canceled','cancelled')
           AND jsonb_typeof(i.payload #> '{snapshot,progressPercent}')='number'
        THEN (i.payload #>> '{snapshot,progressPercent}')::int
      WHEN jsonb_typeof(i.payload->'progress'->'percent')='number'
        THEN (i.payload->'progress'->>'percent')::int
      ELSE NULL
    END AS "ProgressPercent",
    COALESCE((i.payload #>> '{control,cancelRequested}')::boolean, false) AS "CancelRequested",
    i.payload ->> 'source' AS "EnqueueSource",
    COALESCE(i.payload #>> '{snapshot,documentStatus}', d.status) AS "DocumentStatus",
    CASE
      WHEN jsonb_typeof(i.payload #> '{snapshot,documentIngestionVersion}')='number'
        THEN (i.payload #>> '{snapshot,documentIngestionVersion}')::int
      WHEN jsonb_typeof(i.payload->'version')='number'
        THEN (i.payload->>'version')::int
      ELSE d.ingestion_version
    END AS "DocumentIngestionVersion",
    CASE
      WHEN jsonb_typeof(i.payload #> '{snapshot,documentIndexedVersion}')='number'
        THEN (i.payload #>> '{snapshot,documentIndexedVersion}')::int
      WHEN i.status='done'
        THEN d.indexed_version
      WHEN jsonb_typeof(i.payload->'indexedVersionBefore')='number'
        THEN (i.payload->>'indexedVersionBefore')::int
      ELSE d.indexed_version
    END AS "DocumentIndexedVersion",
    CASE
      WHEN jsonb_typeof(i.payload #> '{snapshot,documentAutoIngestPaused}')='boolean'
        THEN (i.payload #>> '{snapshot,documentAutoIngestPaused}')::boolean
      ELSE COALESCE(d.auto_ingest_paused, false)
    END AS "DocumentAutoIngestPaused",
    COALESCE(i.payload #>> '{snapshot,documentAutoIngestPauseReason}', d.auto_ingest_pause_reason) AS "DocumentAutoIngestPauseReason",
    NULL::text AS "ExecutionMode",
    NULL::text AS "RuntimeCapabilityKey",
    NULL::text AS "RuntimeCapabilityStatus",
    NULL::boolean AS "RuntimeCapabilitySelected",
    NULL::uuid AS "CampaignId",
    NULL::boolean AS "Force",
    NULL::boolean AS "ResultStored",
    NULL::text AS "ResultSourceHash",
    NULL::int AS "ResultSummaryLength",
    NULL::text AS "ResultCompletedBy"
  FROM ingestion_jobs i
  LEFT JOIN documents d
    ON d.tenant_id = i.tenant_id
   AND d.doc_path = i.doc_path
  WHERE i.tenant_id=@tenant AND i.job_id=@jobId
) j
LIMIT 1;
""";

        var row = await conn.QueryFirstOrDefaultAsync(new CommandDefinition(sql, new { tenant = tenantId, jobId }, cancellationToken: ct));
        return row is null ? Results.NotFound(new { error = "job_not_found", jobId }) : Results.Ok(row);
    }
}
