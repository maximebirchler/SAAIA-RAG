using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Dapper;
using Npgsql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Audit;
using SAAIA.Backend.CatalogSnapshot;
using SAAIA.Backend.Models;
using SAAIA.Backend.Shared;

namespace SAAIA.Backend.Endpoints;

internal sealed class SummaryEndpointsMarker { }

public static partial class SummaryEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/summaries/{docId:guid}", GetSummaryAsync);
        app.MapGet("/summaries/{docId:guid}/exists", SummaryExistsAsync);
        app.MapGet("/summaries/search", SearchSummariesAsync);

        // Contract-aligned catalog surface (admin summary status remains server-enforced)
        app.MapGet("/catalog/summaries", CatalogSummariesAsync).RequireAdminKey();

        app.MapGet("/admin/summaries/missing_count", MissingSummariesCountAsync).RequireAdminKey();
        app.MapGet("/admin/summaries/missing", MissingSummariesAsync).RequireAdminKey();
        app.MapGet("/admin/summaries/present_count", PresentSummariesCountAsync).RequireAdminKey();
        app.MapGet("/admin/summaries/present", PresentSummariesAsync).RequireAdminKey();
        app.MapPost("/admin/summaries/request", RequestSummaryAsync).RequireAdminKey();
        app.MapPost("/admin/summaries/generate", GenerateSummaryAsync).RequireAdminKey();
        app.MapPost("/admin/summaries/submit", SubmitSummaryAsync).RequireAdminKey();
        app.MapGet("/admin/summaries/status", SummaryStatusAsync).RequireAdminKey();
        app.MapDelete("/admin/summaries/{docId:guid}", DeleteSummaryAsync).RequireAdminKey();

        app.MapGet("/admin/catalog/health", CatalogHealthAsync).RequireAdminKey();
        app.MapPost("/admin/catalog/rescan", CatalogRescanAsync).RequireAdminKey();
        app.MapPost("/admin/catalog/rescan_now", CatalogRescanAsync).RequireAdminKey();

        app.MapGet("/admin/jobs", ListAdminJobsAsync).RequireAdminKey();
        app.MapGet("/admin/jobs/{jobId:guid}", GetAdminJobAsync).RequireAdminKey();
        app.MapPost("/admin/jobs/pause", PauseAdminJobAsync).RequireAdminKey();
        app.MapPost("/admin/jobs/cancel", CancelAdminJobAsync).RequireAdminKey();
        app.MapPost("/admin/jobs/resume", ResumeAdminJobAsync).RequireAdminKey();
        app.MapPost("/admin/jobs/delete_history", DeleteAdminJobHistoryAsync).RequireAdminKey();
        app.MapPost("/admin/jobs/purge", PurgeAdminJobHistoryAsync).RequireAdminKey();
        app.MapPost("/admin/ingestion/reindex", ReindexAsync).RequireAdminKey();

        app.Logger.LogInformation("Mapped summaries/admin tools endpoints");
    }

    public sealed record SummaryCommand(
        Guid? DocId,
        string? Level = "medium",
        Guid? JobId = null,
        string? DocLanguage = null,
        string? SourceHash = null,
        string? SummaryText = null,
        JsonElement? Meta = null,
        bool? Force = null,
        string? DocPath = null,
        string? CompletedBy = null,
        string? ExecutionLeaseToken = null);
    public sealed record JobPauseCommand(Guid JobId);
    public sealed record JobCancelCommand(Guid JobId);
    public sealed record JobResumeCommand(Guid JobId);
    public sealed record JobDeleteHistoryCommand(string[]? JobIds);
    public sealed record JobPurgeCommand(string? Scope, string? Type);

    private static async Task<IResult> GetSummaryAsync(HttpContext ctx, NpgsqlDataSource ds, Guid docId, string? level)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;
        level = NormalizeStoredSummaryLevel(level);

        await using var conn = await ds.OpenConnectionAsync(ct);
        var row = await LoadSummaryRowAsync(conn, tenantId, docId, level, ct);
        if (row is null)
            return Results.NotFound(new { error = "summary_not_found", docId, level });

        var currentHash = await ComputeDocumentSourceHashAsync(conn, tenantId, docId, ct);
        if (string.IsNullOrWhiteSpace(currentHash))
            return Results.NotFound(new { error = "document_not_found", docId, level });

        if (!string.Equals(row.SourceHash, currentHash, StringComparison.OrdinalIgnoreCase))
        {
            await DeleteSummaryCoreAsync(conn, tenantId, docId, level, ct);
            return Results.NotFound(new { error = "summary_stale", docId, level });
        }

        return Results.Ok(new
        {
            docId,
            row.DocPath,
            row.DocName,
            level,
            row.DocLanguage,
            sourceHash = row.SourceHash,
            summaryText = row.SummaryText,
            meta = ParseJsonOrNull(row.SummaryMeta),
            updatedAt = row.UpdatedAt,
            isFresh = true
        });
    }

    private static async Task<IResult> SummaryExistsAsync(HttpContext ctx, NpgsqlDataSource ds, Guid docId, string? level)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;
        level = NormalizeStoredSummaryLevel(level);

        await using var conn = await ds.OpenConnectionAsync(ct);
        var row = await LoadSummaryRowAsync(conn, tenantId, docId, level, ct);
        if (row is null)
            return Results.Ok(new { exists = false, isFresh = false, docId, level });

        var currentHash = await ComputeDocumentSourceHashAsync(conn, tenantId, docId, ct);
        var isFresh = !string.IsNullOrWhiteSpace(currentHash)
            && string.Equals(row.SourceHash, currentHash, StringComparison.OrdinalIgnoreCase);

        if (!isFresh)
            await DeleteSummaryCoreAsync(conn, tenantId, docId, level, ct);

        return Results.Ok(new
        {
            exists = isFresh,
            isFresh,
            docId,
            level,
            sourceHash = isFresh ? row.SourceHash : null,
            updatedAt = isFresh ? row.UpdatedAt : (DateTimeOffset?)null
        });
    }

    private static async Task<IResult> SearchSummariesAsync(HttpContext ctx, NpgsqlDataSource ds, string? q, int? limit, int? offset)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;
        q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        var lim = Math.Clamp(limit ?? 20, 1, 200);
        var off = Math.Max(offset ?? 0, 0);

        await using var conn = await ds.OpenConnectionAsync(ct);

        var sql = """
SELECT
  s.doc_id         AS "DocId",
  d.doc_path       AS "DocPath",
  d.doc_name       AS "DocName",
  s.level          AS "Level",
  s.doc_language   AS "DocLanguage",
  s.source_hash    AS "SourceHash",
  left(s.summary_text, 600) AS "SummaryText",
  s.summary_meta   AS "SummaryMeta",
  s.updated_at     AS "UpdatedAt"
FROM document_summaries s
JOIN documents d ON d.tenant_id = s.tenant_id AND d.doc_id = s.doc_id
WHERE s.tenant_id=@tenant
  AND (@q IS NULL OR d.doc_name ILIKE ('%' || @q || '%') OR d.doc_path ILIKE ('%' || @q || '%') OR s.summary_text ILIKE ('%' || @q || '%'))
ORDER BY s.updated_at DESC
LIMIT @lim OFFSET @off;
""";

        var rows = (await conn.QueryAsync<SummaryRow>(new CommandDefinition(sql, new { tenant = tenantId, q, lim, off }, cancellationToken: ct))).ToList();
        var items = new List<object>();
        foreach (var row in rows)
        {
            var currentHash = await ComputeDocumentSourceHashAsync(conn, tenantId, row.DocId, ct);
            if (!string.Equals(row.SourceHash, currentHash, StringComparison.OrdinalIgnoreCase))
            {
                await DeleteSummaryCoreAsync(conn, tenantId, row.DocId, row.Level, ct);
                continue;
            }

            items.Add(new
            {
                row.DocId,
                row.DocPath,
                row.DocName,
                row.Level,
                row.DocLanguage,
                row.SourceHash,
                summaryText = row.SummaryText,
                row.UpdatedAt
            });
        }

        return Results.Ok(new { items, limit = lim, offset = off });
    }


    internal static async Task<IResult> CatalogSummariesAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        string? categoryRef,
        string? categoryPath,
        int? pageSize,
        int? maxpagesize,
        string? cursor)
    {
        AdminAuth.EnsureAdmin(ctx);
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        categoryPath = SummaryCategoryScopeResolver.NormalizeCategoryPathOrNull(categoryPath);
        categoryRef = SummaryCategoryScopeResolver.NormalizeCategoryRefOrNull(categoryRef);
        var requestedPageSize = Math.Clamp(pageSize ?? maxpagesize ?? 100, 1, 500);

        CatalogSummariesCursor? cursorState = null;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!OpaqueCursor.TryDecode<CatalogSummariesCursor>(cursor, out cursorState) || cursorState is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "invalid_cursor", detail: "The supplied cursor is invalid.");
            }

            if (!CursorMatches(cursorState.CategoryPath, categoryPath)
                || !CursorMatches(cursorState.CategoryRef, categoryRef)
                || !CursorMatches(cursorState.PageSize, requestedPageSize))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "cursor_mismatch", detail: "The supplied cursor does not match the requested summaries filters.");
            }

            categoryPath = cursorState.CategoryPath;
            categoryRef = cursorState.CategoryRef;
            requestedPageSize = cursorState.PageSize;
        }

        var offset = cursorState?.Offset ?? 0;

        await using var conn = await ds.OpenConnectionAsync(ct);
        categoryPath = await SummaryCategoryScopeResolver.ResolveScopeAsync(conn, tenantId, categoryPath, categoryRef, ct);

        const string countSql = """
SELECT
  COUNT(*)::int AS "Total",
  COUNT(*) FILTER (WHERE s.source_hash IS NULL)::int AS "MissingStored",
  COUNT(*) FILTER (
    WHERE s.source_hash IS NOT NULL
      AND s.source_hash <> COALESCE(encode(d.content_hash, 'hex'), md5(COALESCE(d.doc_path,'') || '|' || COALESCE(d.file_size::text,'') || '|' || COALESCE(d.file_mtime::text,'')))
  )::int AS "StaleStored"
FROM documents d
LEFT JOIN document_summaries s
  ON s.tenant_id = d.tenant_id AND s.doc_id = d.doc_id AND s.level='medium'
WHERE d.tenant_id=@tenant
  AND d.status='indexed'
  AND (@categoryPath IS NULL OR d.doc_path LIKE (@categoryPath || '/%'))
  AND (
    s.source_hash IS NULL
    OR s.source_hash <> COALESCE(encode(d.content_hash, 'hex'), md5(COALESCE(d.doc_path,'') || '|' || COALESCE(d.file_size::text,'') || '|' || COALESCE(d.file_mtime::text,'')))
  );
""";

        const string sql = """
SELECT
  d.doc_id           AS "DocId",
  d.doc_path         AS "DocPath",
  d.doc_name         AS "DocName",
  d.category         AS "Category",
  d.page_count       AS "PageCount",
  d.last_ingested_at AS "LastIngestedAt",
  d.updated_at       AS "UpdatedAt",
  sj."ActiveSummaryJobId",
  sj."ActiveSummaryJobType",
  sj."ActiveSummaryJobStatus",
  sj."ActiveSummaryJobExecutionMode",
  sj."ActiveSummaryJobRuntimeCapabilityKey",
  sj."ActiveSummaryJobRuntimeCapabilityStatus",
  sj."ActiveSummaryJobEnqueueSource",
  sj."ActiveSummaryJobCampaignId",
  CASE
    WHEN s.source_hash IS NULL THEN 'missing'
    WHEN s.source_hash <> COALESCE(encode(d.content_hash, 'hex'), md5(COALESCE(d.doc_path,'') || '|' || COALESCE(d.file_size::text,'') || '|' || COALESCE(d.file_mtime::text,''))) THEN 'stale'
    ELSE 'fresh'
  END AS "SummaryState"
FROM documents d
LEFT JOIN document_summaries s
  ON s.tenant_id = d.tenant_id AND s.doc_id = d.doc_id AND s.level='medium'
LEFT JOIN LATERAL (
  SELECT
    aj.job_id AS "ActiveSummaryJobId",
    aj.job_type AS "ActiveSummaryJobType",
    aj.status AS "ActiveSummaryJobStatus",
    COALESCE(aj.payload ->> 'executionMode', 'client_admin') AS "ActiveSummaryJobExecutionMode",
    aj.payload ->> 'runtimeCapabilityKey' AS "ActiveSummaryJobRuntimeCapabilityKey",
    aj.payload ->> 'runtimeCapabilityStatus' AS "ActiveSummaryJobRuntimeCapabilityStatus",
    aj.payload ->> 'source' AS "ActiveSummaryJobEnqueueSource",
    CASE
      WHEN jsonb_typeof(aj.payload->'campaignId')='string' THEN (aj.payload->>'campaignId')::uuid
      ELSE NULL::uuid
    END AS "ActiveSummaryJobCampaignId"
  FROM admin_jobs aj
  WHERE aj.tenant_id = d.tenant_id
    AND aj.doc_id = d.doc_id
    AND aj.job_type IN ('summary.request', 'summary.generate')
    AND aj.status IN ('queued', 'running', 'paused')
  ORDER BY aj.created_at DESC
  LIMIT 1
) sj ON TRUE
WHERE d.tenant_id=@tenant
  AND d.status='indexed'
  AND (@categoryPath IS NULL OR d.doc_path LIKE (@categoryPath || '/%'))
  AND (
    s.source_hash IS NULL
    OR s.source_hash <> COALESCE(encode(d.content_hash, 'hex'), md5(COALESCE(d.doc_path,'') || '|' || COALESCE(d.file_size::text,'') || '|' || COALESCE(d.file_mtime::text,'')))
  )
ORDER BY d.updated_at DESC
LIMIT @lim OFFSET @off;
""";

        var counts = await conn.QueryFirstAsync<MissingSummaryCountRow>(new CommandDefinition(countSql, new { tenant = tenantId, categoryPath }, cancellationToken: ct));
        var rows = (await conn.QueryAsync<MissingSummaryRow>(new CommandDefinition(sql, new { tenant = tenantId, categoryPath, lim = requestedPageSize, off = offset }, cancellationToken: ct))).ToList();
        var capabilityBPolicyLookup = await LoadCapabilityBBackofficeCandidateLookupAsync(ctx, conn, tenantId, categoryPath, ct);

        var value = rows.Select(row =>
        {
            var topLevel = row.DocPath?.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            var canonicalCategory = string.IsNullOrWhiteSpace(topLevel)
                ? row.Category
                : topLevel;
            capabilityBPolicyLookup.TryGetValue(row.DocId, out var capabilityBCandidate);

            return new
            {
                documentRef = $"doc_{row.DocId:N}",
                row.DocId,
                row.DocPath,
                canonicalName = row.DocName,
                categoryCanonicalName = canonicalCategory,
                row.PageCount,
                row.LastIngestedAt,
                row.UpdatedAt,
                row.SummaryState,
                hasActiveSummaryJob = row.ActiveSummaryJobId.HasValue,
                row.ActiveSummaryJobId,
                row.ActiveSummaryJobType,
                row.ActiveSummaryJobStatus,
                row.ActiveSummaryJobExecutionMode,
                row.ActiveSummaryJobRuntimeCapabilityKey,
                row.ActiveSummaryJobRuntimeCapabilityStatus,
                row.ActiveSummaryJobEnqueueSource,
                row.ActiveSummaryJobCampaignId,
                CapabilityBReadyToEnqueue = capabilityBCandidate is not null && !capabilityBCandidate.PolicyBlocked,
                CapabilityBRecommendedAction = capabilityBCandidate?.RecommendedAction,
                CapabilityBPolicyBlocked = capabilityBCandidate?.PolicyBlocked ?? false,
                CapabilityBPolicyBlockReason = capabilityBCandidate?.PolicyBlockReason,
                CapabilityBPriorityScore = capabilityBCandidate?.PriorityScore ?? 0,
                CapabilityBLastJobStatus = capabilityBCandidate?.LastJobStatus,
                CapabilityBLastJobFinishedAt = capabilityBCandidate?.LastJobFinishedAt,
                CapabilityBLastJobError = capabilityBCandidate?.LastJobError,
                CapabilityBReasons = capabilityBCandidate?.Reasons ?? Array.Empty<string>()
            };
        }).ToList();

        string? nextLink = null;
        var nextOffset = offset + value.Count;
        if (nextOffset < counts.Total)
        {
            var nextCursor = OpaqueCursor.Encode(new CatalogSummariesCursor
            {
                CategoryPath = categoryPath,
                CategoryRef = categoryRef,
                PageSize = requestedPageSize,
                Offset = nextOffset
            });

            nextLink = BuildAbsoluteNextLink(ctx, new Dictionary<string, string?>
            {
                ["categoryPath"] = categoryPath,
                ["categoryRef"] = categoryRef,
                ["pageSize"] = requestedPageSize.ToString(),
                ["cursor"] = nextCursor
            });
        }

        return Results.Ok(new
        {
            value,
            nextLink,
            totals = new
            {
                total = counts.Total,
                missingStored = counts.MissingStored,
                staleStored = counts.StaleStored
            },
            scopePath = categoryPath,
            level = "medium"
        });
    }

    private static async Task<IResult> MissingSummariesCountAsync(HttpContext ctx, NpgsqlDataSource ds, string? categoryPath, string? categoryRef)
    {
        AdminAuth.EnsureAdmin(ctx);
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;
        categoryPath = SummaryCategoryScopeResolver.NormalizeCategoryPathOrNull(categoryPath);
        categoryRef = SummaryCategoryScopeResolver.NormalizeCategoryRefOrNull(categoryRef);

        await using var conn = await ds.OpenConnectionAsync(ct);
        categoryPath = await SummaryCategoryScopeResolver.ResolveScopeAsync(conn, tenantId, categoryPath, categoryRef, ct);

        const string sql = """
SELECT
  COUNT(*)::int AS "Total",
  COUNT(*) FILTER (WHERE s.source_hash IS NULL)::int AS "MissingStored",
  COUNT(*) FILTER (
    WHERE s.source_hash IS NOT NULL
      AND s.source_hash <> COALESCE(encode(d.content_hash, 'hex'), md5(COALESCE(d.doc_path,'') || '|' || COALESCE(d.file_size::text,'') || '|' || COALESCE(d.file_mtime::text,'')))
  )::int AS "StaleStored"
FROM documents d
LEFT JOIN document_summaries s
  ON s.tenant_id = d.tenant_id AND s.doc_id = d.doc_id AND s.level='medium'
WHERE d.tenant_id=@tenant
  AND d.status='indexed'
  AND (@categoryPath IS NULL OR d.doc_path LIKE (@categoryPath || '/%'))
  AND (
    s.source_hash IS NULL
    OR s.source_hash <> COALESCE(encode(d.content_hash, 'hex'), md5(COALESCE(d.doc_path,'') || '|' || COALESCE(d.file_size::text,'') || '|' || COALESCE(d.file_mtime::text,'')))
  );
""";

        var row = await conn.QueryFirstAsync<MissingSummaryCountRow>(new CommandDefinition(sql, new { tenant = tenantId, categoryPath }, cancellationToken: ct));
        return Results.Ok(new
        {
            total = row.Total,
            missingStored = row.MissingStored,
            staleStored = row.StaleStored,
            scopePath = categoryPath,
            level = "medium"
        });
    }

    private static async Task<IResult> PresentSummariesCountAsync(HttpContext ctx, NpgsqlDataSource ds, string? categoryPath, string? categoryRef)
    {
        AdminAuth.EnsureAdmin(ctx);
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;
        categoryPath = SummaryCategoryScopeResolver.NormalizeCategoryPathOrNull(categoryPath);
        categoryRef = SummaryCategoryScopeResolver.NormalizeCategoryRefOrNull(categoryRef);

        await using var conn = await ds.OpenConnectionAsync(ct);
        categoryPath = await SummaryCategoryScopeResolver.ResolveScopeAsync(conn, tenantId, categoryPath, categoryRef, ct);

        const string sql = """
SELECT
  COUNT(*)::int AS "Total"
FROM documents d
JOIN document_summaries s
  ON s.tenant_id = d.tenant_id AND s.doc_id = d.doc_id AND s.level='medium'
WHERE d.tenant_id=@tenant
  AND d.status='indexed'
  AND (@categoryPath IS NULL OR d.doc_path LIKE (@categoryPath || '/%'))
  AND s.source_hash IS NOT NULL
  AND s.source_hash = COALESCE(encode(d.content_hash, 'hex'), md5(COALESCE(d.doc_path,'') || '|' || COALESCE(d.file_size::text,'') || '|' || COALESCE(d.file_mtime::text,'')));
""";

        var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(sql, new { tenant = tenantId, categoryPath }, cancellationToken: ct));
        return Results.Ok(new
        {
            total,
            presentStored = total,
            scopePath = categoryPath,
            level = "medium",
            mode = "present"
        });
    }

    private static async Task<IResult> PresentSummariesAsync(HttpContext ctx, NpgsqlDataSource ds, string? categoryPath, string? categoryRef, int? limit, int? offset)
    {
        AdminAuth.EnsureAdmin(ctx);
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;
        categoryPath = SummaryCategoryScopeResolver.NormalizeCategoryPathOrNull(categoryPath);
        categoryRef = SummaryCategoryScopeResolver.NormalizeCategoryRefOrNull(categoryRef);
        var lim = Math.Clamp(limit ?? 100, 1, 500);
        var off = Math.Max(offset ?? 0, 0);

        await using var conn = await ds.OpenConnectionAsync(ct);
        categoryPath = await SummaryCategoryScopeResolver.ResolveScopeAsync(conn, tenantId, categoryPath, categoryRef, ct);

        const string countSql = """
SELECT
  COUNT(*)::int AS "Total"
FROM documents d
JOIN document_summaries s
  ON s.tenant_id = d.tenant_id AND s.doc_id = d.doc_id AND s.level='medium'
WHERE d.tenant_id=@tenant
  AND d.status='indexed'
  AND (@categoryPath IS NULL OR d.doc_path LIKE (@categoryPath || '/%'))
  AND s.source_hash IS NOT NULL
  AND s.source_hash = COALESCE(encode(d.content_hash, 'hex'), md5(COALESCE(d.doc_path,'') || '|' || COALESCE(d.file_size::text,'') || '|' || COALESCE(d.file_mtime::text,'')));
""";

        const string sql = """
SELECT
  d.doc_id           AS "DocId",
  d.doc_path         AS "DocPath",
  d.doc_name         AS "DocName",
  d.category         AS "Category",
  d.page_count       AS "PageCount",
  d.last_ingested_at AS "LastIngestedAt",
  d.updated_at       AS "UpdatedAt",
  s.source_hash      AS "StoredSourceHash",
  COALESCE(encode(d.content_hash, 'hex'), md5(COALESCE(d.doc_path,'') || '|' || COALESCE(d.file_size::text,'') || '|' || COALESCE(d.file_mtime::text,''))) AS "CurrentSourceHash",
  'fresh'            AS "SummaryState"
FROM documents d
JOIN document_summaries s
  ON s.tenant_id = d.tenant_id AND s.doc_id = d.doc_id AND s.level='medium'
WHERE d.tenant_id=@tenant
  AND d.status='indexed'
  AND (@categoryPath IS NULL OR d.doc_path LIKE (@categoryPath || '/%'))
  AND s.source_hash IS NOT NULL
  AND s.source_hash = COALESCE(encode(d.content_hash, 'hex'), md5(COALESCE(d.doc_path,'') || '|' || COALESCE(d.file_size::text,'') || '|' || COALESCE(d.file_mtime::text,'')))
ORDER BY d.updated_at DESC
LIMIT @lim OFFSET @off;
""";

        var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(countSql, new { tenant = tenantId, categoryPath }, cancellationToken: ct));
        var rows = await conn.QueryAsync<MissingSummaryRow>(new CommandDefinition(sql, new { tenant = tenantId, categoryPath, lim, off }, cancellationToken: ct));
        var items = rows.Select(row => new
        {
            row.DocId,
            row.DocPath,
            row.DocName,
            row.Category,
            row.PageCount,
            row.LastIngestedAt,
            row.UpdatedAt,
            row.SummaryState
        }).ToArray();

        return Results.Ok(new
        {
            total,
            presentStored = total,
            limit = lim,
            offset = off,
            endOfList = off + items.Length >= total,
            scopePath = categoryPath,
            level = "medium",
            mode = "present",
            items
        });
    }

    internal static async Task<IResult> MissingSummariesAsync(HttpContext ctx, NpgsqlDataSource ds, string? categoryPath, string? categoryRef, int? limit, int? offset)
    {
        AdminAuth.EnsureAdmin(ctx);
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;
        categoryPath = SummaryCategoryScopeResolver.NormalizeCategoryPathOrNull(categoryPath);
        categoryRef = SummaryCategoryScopeResolver.NormalizeCategoryRefOrNull(categoryRef);
        var lim = Math.Clamp(limit ?? 100, 1, 500);
        var off = Math.Max(offset ?? 0, 0);

        await using var conn = await ds.OpenConnectionAsync(ct);
        categoryPath = await SummaryCategoryScopeResolver.ResolveScopeAsync(conn, tenantId, categoryPath, categoryRef, ct);

        const string countSql = """
SELECT
  COUNT(*)::int AS "Total",
  COUNT(*) FILTER (WHERE s.source_hash IS NULL)::int AS "MissingStored",
  COUNT(*) FILTER (
    WHERE s.source_hash IS NOT NULL
      AND s.source_hash <> COALESCE(encode(d.content_hash, 'hex'), md5(COALESCE(d.doc_path,'') || '|' || COALESCE(d.file_size::text,'') || '|' || COALESCE(d.file_mtime::text,'')))
  )::int AS "StaleStored"
FROM documents d
LEFT JOIN document_summaries s
  ON s.tenant_id = d.tenant_id AND s.doc_id = d.doc_id AND s.level='medium'
WHERE d.tenant_id=@tenant
  AND d.status='indexed'
  AND (@categoryPath IS NULL OR d.doc_path LIKE (@categoryPath || '/%'))
  AND (
    s.source_hash IS NULL
    OR s.source_hash <> COALESCE(encode(d.content_hash, 'hex'), md5(COALESCE(d.doc_path,'') || '|' || COALESCE(d.file_size::text,'') || '|' || COALESCE(d.file_mtime::text,'')))
  );
""";

        const string sql = """
SELECT
  d.doc_id           AS "DocId",
  d.doc_path         AS "DocPath",
  d.doc_name         AS "DocName",
  d.category         AS "Category",
  d.page_count       AS "PageCount",
  d.last_ingested_at AS "LastIngestedAt",
  d.updated_at       AS "UpdatedAt",
  s.source_hash      AS "StoredSourceHash",
  COALESCE(encode(d.content_hash, 'hex'), md5(COALESCE(d.doc_path,'') || '|' || COALESCE(d.file_size::text,'') || '|' || COALESCE(d.file_mtime::text,''))) AS "CurrentSourceHash",
  sj."ActiveSummaryJobId",
  sj."ActiveSummaryJobType",
  sj."ActiveSummaryJobStatus",
  sj."ActiveSummaryJobExecutionMode",
  sj."ActiveSummaryJobRuntimeCapabilityKey",
  sj."ActiveSummaryJobRuntimeCapabilityStatus",
  sj."ActiveSummaryJobEnqueueSource",
  sj."ActiveSummaryJobCampaignId",
  CASE
    WHEN s.source_hash IS NULL THEN 'missing'
    WHEN s.source_hash <> COALESCE(encode(d.content_hash, 'hex'), md5(COALESCE(d.doc_path,'') || '|' || COALESCE(d.file_size::text,'') || '|' || COALESCE(d.file_mtime::text,''))) THEN 'stale'
    ELSE 'fresh'
  END AS "SummaryState"
FROM documents d
LEFT JOIN document_summaries s
  ON s.tenant_id = d.tenant_id AND s.doc_id = d.doc_id AND s.level='medium'
LEFT JOIN LATERAL (
  SELECT
    aj.job_id AS "ActiveSummaryJobId",
    aj.job_type AS "ActiveSummaryJobType",
    aj.status AS "ActiveSummaryJobStatus",
    COALESCE(aj.payload ->> 'executionMode', 'client_admin') AS "ActiveSummaryJobExecutionMode",
    aj.payload ->> 'runtimeCapabilityKey' AS "ActiveSummaryJobRuntimeCapabilityKey",
    aj.payload ->> 'runtimeCapabilityStatus' AS "ActiveSummaryJobRuntimeCapabilityStatus",
    aj.payload ->> 'source' AS "ActiveSummaryJobEnqueueSource",
    CASE
      WHEN jsonb_typeof(aj.payload->'campaignId')='string' THEN (aj.payload->>'campaignId')::uuid
      ELSE NULL::uuid
    END AS "ActiveSummaryJobCampaignId"
  FROM admin_jobs aj
  WHERE aj.tenant_id = d.tenant_id
    AND aj.doc_id = d.doc_id
    AND aj.job_type IN ('summary.request', 'summary.generate')
    AND aj.status IN ('queued', 'running', 'paused')
  ORDER BY aj.created_at DESC
  LIMIT 1
) sj ON TRUE
WHERE d.tenant_id=@tenant
  AND d.status='indexed'
  AND (@categoryPath IS NULL OR d.doc_path LIKE (@categoryPath || '/%'))
  AND (
    s.source_hash IS NULL
    OR s.source_hash <> COALESCE(encode(d.content_hash, 'hex'), md5(COALESCE(d.doc_path,'') || '|' || COALESCE(d.file_size::text,'') || '|' || COALESCE(d.file_mtime::text,'')))
  )
ORDER BY d.updated_at DESC
LIMIT @lim OFFSET @off;
""";

        var counts = await conn.QueryFirstAsync<MissingSummaryCountRow>(new CommandDefinition(countSql, new { tenant = tenantId, categoryPath }, cancellationToken: ct));
        var rows = await conn.QueryAsync<MissingSummaryRow>(new CommandDefinition(sql, new { tenant = tenantId, categoryPath, lim, off }, cancellationToken: ct));
        var capabilityBPolicyLookup = await LoadCapabilityBBackofficeCandidateLookupAsync(ctx, conn, tenantId, categoryPath, ct);
        var items = rows.Select(row => new
        {
            row.DocId,
            row.DocPath,
            row.DocName,
            row.Category,
            row.PageCount,
            row.LastIngestedAt,
            row.UpdatedAt,
            row.SummaryState,
            hasActiveSummaryJob = row.ActiveSummaryJobId.HasValue,
            row.ActiveSummaryJobId,
            row.ActiveSummaryJobType,
            row.ActiveSummaryJobStatus,
            row.ActiveSummaryJobExecutionMode,
            row.ActiveSummaryJobRuntimeCapabilityKey,
            row.ActiveSummaryJobRuntimeCapabilityStatus,
            row.ActiveSummaryJobEnqueueSource,
            row.ActiveSummaryJobCampaignId,
            CapabilityBReadyToEnqueue = capabilityBPolicyLookup.TryGetValue(row.DocId, out var capabilityBCandidate) && !capabilityBCandidate.PolicyBlocked,
            CapabilityBRecommendedAction = capabilityBCandidate?.RecommendedAction,
            CapabilityBPolicyBlocked = capabilityBCandidate?.PolicyBlocked ?? false,
            CapabilityBPolicyBlockReason = capabilityBCandidate?.PolicyBlockReason,
            CapabilityBPriorityScore = capabilityBCandidate?.PriorityScore ?? 0,
            CapabilityBLastJobStatus = capabilityBCandidate?.LastJobStatus,
            CapabilityBLastJobFinishedAt = capabilityBCandidate?.LastJobFinishedAt,
            CapabilityBLastJobError = capabilityBCandidate?.LastJobError,
            CapabilityBReasons = capabilityBCandidate?.Reasons ?? Array.Empty<string>()
        }).ToList();

        return Results.Ok(new
        {
            items,
            limit = lim,
            offset = off,
            total = counts.Total,
            missingStored = counts.MissingStored,
            staleStored = counts.StaleStored,
            scopePath = categoryPath,
            level = "medium"
        });
    }

    private static async Task<IResult> RequestSummaryAsync(HttpContext ctx, NpgsqlDataSource ds, SummaryCommand cmd)
    {
        AdminAuth.EnsureAdmin(ctx);
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;
        if (cmd.DocId is null || cmd.DocId == Guid.Empty)
            return Results.BadRequest(new { error = "doc_id_required" });

        await using var conn = await ds.OpenConnectionAsync(ct);
        var doc = await LoadDocumentAsync(conn, tenantId, cmd.DocId.Value, ct);
        if (doc is null) return Results.NotFound(new { error = "document_not_found", docId = cmd.DocId });

        var level = NormalizeStoredSummaryLevel(cmd.Level);
        var jobId = await InsertAdminJobAsync(conn, tenantId, cmd.DocId.Value, level, "summary.request", new
        {
            docId = cmd.DocId,
            docPath = doc.DocPath,
            level,
            executionMode = "client_admin"
        }, ct);

        return Results.Ok(new { jobId, status = "queued", executionMode = "client_admin", docId = cmd.DocId, level });
    }

    private static async Task<IResult> GenerateSummaryAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RuntimeGovernanceOptions> runtimeOptions,
        IOptions<RagOptions> ragOptions,
        IHostEnvironment env,
        SummaryCommand cmd)
    {
        AdminAuth.EnsureAdmin(ctx);
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;
        if (cmd.DocId is null || cmd.DocId == Guid.Empty)
            return Results.BadRequest(new { error = "doc_id_required" });

        var execution = await ResolveSummaryGenerationExecutionAsync(
            ctx,
            ds,
            runtimeOptions.Value,
            ragOptions.Value,
            env,
            ct);
        await using var conn = await ds.OpenConnectionAsync(ct);
        var doc = await LoadDocumentAsync(conn, tenantId, cmd.DocId.Value, ct);
        if (doc is null) return Results.NotFound(new { error = "document_not_found", docId = cmd.DocId });

        var level = NormalizeStoredSummaryLevel(cmd.Level);
        var jobId = await InsertAdminJobAsync(conn, tenantId, cmd.DocId.Value, level, "summary.generate", new
        {
            docId = cmd.DocId,
            docPath = doc.DocPath,
            level,
            force = cmd.Force ?? false,
            executionMode = execution.ExecutionMode,
            runtimeCapabilityKey = execution.CapabilityKey,
            runtimeCapabilityStatus = execution.Status,
            runtimeCapabilitySelected = execution.UsesCapabilityB
        }, ct);

        return Results.Ok(new
        {
            jobId,
            status = "queued",
            executionMode = execution.ExecutionMode,
            runtimeCapabilityKey = execution.CapabilityKey,
            runtimeCapabilityStatus = execution.Status,
            docId = cmd.DocId,
            level
        });
    }

    internal static async Task<IResult> SubmitSummaryAsync(HttpContext ctx, NpgsqlDataSource ds, SummaryCommand cmd)
    {
        AdminAuth.EnsureAdmin(ctx);
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;
        if (cmd.DocId is null || cmd.DocId == Guid.Empty)
            return Results.BadRequest(new { error = "doc_id_required" });
        if (string.IsNullOrWhiteSpace(cmd.SummaryText))
            return Results.BadRequest(new { error = "summary_text_required" });

        var level = NormalizeStoredSummaryLevel(cmd.Level);
        await using var conn = await ds.OpenConnectionAsync(ct);
        var doc = await LoadDocumentAsync(conn, tenantId, cmd.DocId.Value, ct);
        if (doc is null) return Results.NotFound(new { error = "document_not_found", docId = cmd.DocId });
        var jobContext = cmd.JobId is not null && cmd.JobId != Guid.Empty
            ? await LoadSummaryAdminJobContextAsync(conn, tenantId, cmd.JobId.Value, ct)
            : null;
        if (cmd.JobId is not null && cmd.JobId != Guid.Empty && jobContext is null)
            return Results.NotFound(new { error = "job_not_found", jobId = cmd.JobId });

        if (jobContext is not null
            && string.Equals(jobContext.RuntimeCapabilityKey, "capability_b.backoffice_generation", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(jobContext.ExecutionLeaseToken))
        {
            if (!string.Equals(jobContext.Status, "running", StringComparison.Ordinal))
                return Results.BadRequest(new { error = "capability_b_job_not_running", jobId = cmd.JobId });

            if (string.IsNullOrWhiteSpace(cmd.ExecutionLeaseToken))
                return Results.BadRequest(new { error = "capability_b_execution_lease_required", jobId = cmd.JobId });

            if (!string.Equals(jobContext.ExecutionLeaseToken, cmd.ExecutionLeaseToken, StringComparison.Ordinal))
                return Results.BadRequest(new { error = "capability_b_invalid_execution_lease", jobId = cmd.JobId });
        }

        var sourceHash = string.IsNullOrWhiteSpace(cmd.SourceHash)
            ? await ComputeDocumentSourceHashAsync(conn, tenantId, cmd.DocId.Value, ct)
            : cmd.SourceHash.Trim();
        if (string.IsNullOrWhiteSpace(sourceHash))
            return Results.BadRequest(new { error = "source_hash_unavailable" });

        var metaJson = cmd.Meta.HasValue ? cmd.Meta.Value.GetRawText() : null;

        var sql = """
INSERT INTO document_summaries(tenant_id, doc_id, level, doc_language, source_hash, summary_text, summary_meta, created_at, updated_at)
VALUES(@tenant, @docId, @level, @docLanguage, @sourceHash, @summaryText, @summaryMeta::jsonb, now(), now())
ON CONFLICT (tenant_id, doc_id, level)
DO UPDATE SET
  doc_language = EXCLUDED.doc_language,
  source_hash = EXCLUDED.source_hash,
  summary_text = EXCLUDED.summary_text,
  summary_meta = EXCLUDED.summary_meta,
  updated_at = now();
""";

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            tenant = tenantId,
            docId = cmd.DocId,
            level,
            docLanguage = NormalizeDocLanguage(cmd.DocLanguage),
            sourceHash,
            summaryText = cmd.SummaryText,
            summaryMeta = (object?)metaJson ?? DBNull.Value
        }, cancellationToken: ct));

        if (cmd.JobId is not null && cmd.JobId != Guid.Empty)
        {
            var completedBy = string.IsNullOrWhiteSpace(cmd.CompletedBy)
                ? "admin_submit_summary"
                : cmd.CompletedBy.Trim();
            var jobSql = """
UPDATE admin_jobs
SET status='done', result=@result::jsonb, finished_at=now(), last_error=NULL
WHERE tenant_id=@tenant AND job_id=@jobId;
""";
            var result = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["docId"] = cmd.DocId,
                ["docPath"] = doc.DocPath,
                ["level"] = level,
                ["stored"] = true,
                ["sourceHash"] = sourceHash,
                ["summaryLength"] = cmd.SummaryText?.Length ?? 0,
                ["completedBy"] = completedBy,
                ["executionMode"] = jobContext?.ExecutionMode,
                ["runtimeCapabilityKey"] = jobContext?.RuntimeCapabilityKey,
                ["runtimeCapabilityStatus"] = jobContext?.RuntimeCapabilityStatus,
                ["runtimeCapabilitySelected"] = jobContext?.RuntimeCapabilitySelected,
                ["runtimeProfileKey"] = jobContext?.RuntimeProfileKey,
                ["source"] = jobContext?.EnqueueSource,
                ["campaignId"] = jobContext?.CampaignId,
                ["executionLeaseToken"] = jobContext?.ExecutionLeaseToken
            });
            await conn.ExecuteAsync(new CommandDefinition(jobSql, new { tenant = tenantId, jobId = cmd.JobId, result }, cancellationToken: ct));

            if (string.Equals(jobContext?.RuntimeCapabilityKey, "capability_b.backoffice_generation", StringComparison.Ordinal))
            {
                await RuntimeCapabilityBExecutionCommandService.RecordCapabilityBSummaryCompletedAsync(
                    conn,
                    cmd.JobId.Value,
                    cmd.DocId.Value,
                    doc.DocPath,
                    level,
                    sourceHash,
                    cmd.SummaryText?.Length ?? 0,
                    jobContext?.RuntimeProfileKey,
                    jobContext?.CampaignId,
                    jobContext?.RuntimeCapabilityStatus,
                    ct);
            }
        }

        return Results.Ok(new { stored = true, docId = cmd.DocId, level, sourceHash });
    }

    internal static async Task<IResult> SummaryStatusAsync(HttpContext ctx, NpgsqlDataSource ds, Guid jobId)
    {
        AdminAuth.EnsureAdmin(ctx);
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;
        await using var conn = await ds.OpenConnectionAsync(ct);

        var sql = """
SELECT
  job_id AS "JobId",
  job_type AS "JobType",
  status AS "Status",
  doc_id AS "DocId",
  payload ->> 'docPath' AS "DocPath",
  level AS "Level",
  payload ->> 'source' AS "EnqueueSource",
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
  result ->> 'completedBy' AS "ResultCompletedBy",
  CASE
    WHEN s.doc_id IS NOT NULL THEN true
    ELSE false
  END AS "StoredSummaryExists",
  s.updated_at AS "StoredSummaryUpdatedAt",
  s.source_hash AS "StoredSummarySourceHash",
  CASE
    WHEN d.doc_id IS NULL THEN 'document_missing'
    WHEN s.doc_id IS NULL THEN 'missing'
    WHEN s.source_hash <> COALESCE(encode(d.content_hash, 'hex'), md5(COALESCE(d.doc_path,'') || '|' || COALESCE(d.file_size::text,'') || '|' || COALESCE(d.file_mtime::text,''))) THEN 'stale'
    ELSE 'fresh'
  END AS "StoredSummaryFreshness",
  payload AS "Payload",
  result AS "Result",
  last_error AS "LastError",
  created_at AS "CreatedAt",
  started_at AS "StartedAt",
  finished_at AS "FinishedAt"
FROM admin_jobs
LEFT JOIN documents d
  ON d.tenant_id = admin_jobs.tenant_id
 AND d.doc_id = admin_jobs.doc_id
LEFT JOIN document_summaries s
  ON s.tenant_id = admin_jobs.tenant_id
 AND s.doc_id = admin_jobs.doc_id
 AND s.level = admin_jobs.level
WHERE tenant_id=@tenant AND job_id=@jobId
LIMIT 1;
""";

        var row = await conn.QueryFirstOrDefaultAsync(new CommandDefinition(sql, new { tenant = tenantId, jobId }, cancellationToken: ct));
        return row is null ? Results.NotFound(new { error = "job_not_found", jobId }) : Results.Ok(row);
    }

    private static async Task<IResult> DeleteSummaryAsync(HttpContext ctx, NpgsqlDataSource ds, Guid docId, string? level)
    {
        AdminAuth.EnsureAdmin(ctx);
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;
        await using var conn = await ds.OpenConnectionAsync(ct);
        var deleted = await DeleteSummaryCoreAsync(conn, tenantId, docId, NormalizeStoredSummaryLevel(level), ct);
        return Results.Ok(new { deleted, docId, level = NormalizeStoredSummaryLevel(level) });
    }

    private static async Task<IResult> CatalogHealthAsync(HttpContext ctx, NpgsqlDataSource ds)
    {
        AdminAuth.EnsureAdmin(ctx);
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;
        await using var conn = await ds.OpenConnectionAsync(ct);

        var countsSql = """
SELECT
  COUNT(*) FILTER (WHERE status='indexed') AS indexed,
  COUNT(*) FILTER (WHERE status='pending') AS pending,
  COUNT(*) FILTER (WHERE status='missing') AS missing,
  COUNT(*) FILTER (WHERE status='error') AS failed,
  COUNT(*) FILTER (WHERE status='deleted') AS deleted
FROM documents
WHERE tenant_id=@tenant;
""";
        var counts = await conn.QueryFirstAsync(new CommandDefinition(countsSql, new { tenant = tenantId }, cancellationToken: ct));

        var dupSql = """
SELECT COUNT(*)
FROM (
  SELECT doc_path
  FROM documents
  WHERE tenant_id=@tenant
  GROUP BY doc_path
  HAVING COUNT(*) > 1
) d;
""";
        var duplicates = await conn.ExecuteScalarAsync<int>(new CommandDefinition(dupSql, new { tenant = tenantId }, cancellationToken: ct));

        var snapSql = """
SELECT computed_at AS "ComputedAt", total_docs AS "TotalDocs", max_depth AS "MaxDepth"
FROM documents_catalog_summary
WHERE tenant_id=@tenant
LIMIT 1;
""";
        var snapshot = await conn.QueryFirstOrDefaultAsync(new CommandDefinition(snapSql, new { tenant = tenantId }, cancellationToken: ct));

        int snapshotTotal = 0;
        if (snapshot is IDictionary<string, object> snapshotMap
            && snapshotMap.TryGetValue("TotalDocs", out var totalDocsRaw)
            && totalDocsRaw is not null)
        {
            snapshotTotal = totalDocsRaw switch
            {
                int value => value,
                long value => (int)value,
                short value => value,
                byte value => value,
                decimal value => (int)value,
                _ => 0
            };
        }

        return Results.Ok(new
        {
            indexed = (int)counts.indexed,
            pending = (int)counts.pending,
            missing = (int)counts.missing,
            failed = (int)counts.failed,
            deleted = (int)counts.deleted,
            duplicates,
            snapshot,
            snapshotMismatch = snapshotTotal != (int)counts.indexed
        });
    }




    private static async Task<DocumentRow?> LoadDocumentAsync(NpgsqlConnection conn, Guid tenantId, Guid docId, CancellationToken ct)
    {
        var sql = """
SELECT doc_id AS "DocId", doc_path AS "DocPath", doc_name AS "DocName", category AS "Category"
FROM documents
WHERE tenant_id=@tenant AND doc_id=@docId
LIMIT 1;
""";
        return await conn.QueryFirstOrDefaultAsync<DocumentRow>(new CommandDefinition(sql, new { tenant = tenantId, docId }, cancellationToken: ct));
    }

    private static async Task<DocumentRow?> LoadDocumentByPathAsync(NpgsqlConnection conn, Guid tenantId, string docPath, CancellationToken ct)
    {
        var sql = """
SELECT doc_id AS "DocId", doc_path AS "DocPath", doc_name AS "DocName", category AS "Category"
FROM documents
WHERE tenant_id=@tenant AND doc_path=@docPath
LIMIT 1;
""";
        return await conn.QueryFirstOrDefaultAsync<DocumentRow>(new CommandDefinition(sql, new { tenant = tenantId, docPath = NormalizePath(docPath) }, cancellationToken: ct));
    }

    private static async Task<SummaryAdminJobContext?> LoadSummaryAdminJobContextAsync(NpgsqlConnection conn, Guid tenantId, Guid jobId, CancellationToken ct)
    {
        const string sql = """
SELECT
  status AS "Status",
  payload ->> 'executionMode' AS "ExecutionMode",
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
  END AS "CampaignId"
FROM admin_jobs
WHERE tenant_id=@tenant AND job_id=@jobId
LIMIT 1;
""";

        return await conn.QueryFirstOrDefaultAsync<SummaryAdminJobContext>(new CommandDefinition(sql, new { tenant = tenantId, jobId }, cancellationToken: ct));
    }

    private static async Task<SummaryRow?> LoadSummaryRowAsync(NpgsqlConnection conn, Guid tenantId, Guid docId, string level, CancellationToken ct)
    {
        var sql = """
SELECT
  s.doc_id         AS "DocId",
  d.doc_path       AS "DocPath",
  d.doc_name       AS "DocName",
  s.level          AS "Level",
  s.doc_language   AS "DocLanguage",
  s.source_hash    AS "SourceHash",
  s.summary_text   AS "SummaryText",
  s.summary_meta   AS "SummaryMeta",
  s.updated_at     AS "UpdatedAt"
FROM document_summaries s
JOIN documents d ON d.tenant_id = s.tenant_id AND d.doc_id = s.doc_id
WHERE s.tenant_id=@tenant AND s.doc_id=@docId AND s.level=@level
LIMIT 1;
""";
        return await conn.QueryFirstOrDefaultAsync<SummaryRow>(new CommandDefinition(sql, new { tenant = tenantId, docId, level }, cancellationToken: ct));
    }

    private static async Task<string?> ComputeDocumentSourceHashAsync(NpgsqlConnection conn, Guid tenantId, Guid docId, CancellationToken ct)
    {
        var sql = """
SELECT COALESCE(encode(content_hash, 'hex'), md5(COALESCE(doc_path,'') || '|' || COALESCE(file_size::text,'') || '|' || COALESCE(file_mtime::text,'')))
FROM documents
WHERE tenant_id=@tenant AND doc_id=@docId
LIMIT 1;
""";
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(sql, new { tenant = tenantId, docId }, cancellationToken: ct));
    }

    private static async Task<bool> DeleteSummaryCoreAsync(NpgsqlConnection conn, Guid tenantId, Guid docId, string level, CancellationToken ct)
    {
        const string sql = "DELETE FROM document_summaries WHERE tenant_id=@tenant AND doc_id=@docId AND level=@level;";
        var changed = await conn.ExecuteAsync(new CommandDefinition(sql, new { tenant = tenantId, docId, level }, cancellationToken: ct));
        return changed > 0;
    }

    private static async Task<Guid> InsertAdminJobAsync(NpgsqlConnection conn, Guid tenantId, Guid docId, string level, string jobType, object payload, CancellationToken ct)
    {
        var jobId = Guid.NewGuid();
        var payloadJson = JsonSerializer.Serialize(payload);
        const string sql = "INSERT INTO admin_jobs(job_id, tenant_id, job_type, status, doc_id, level, payload, created_at) VALUES(@jobId, @tenant, @jobType, 'queued', @docId, @level, @payload::jsonb, now()) RETURNING job_id;";
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(sql, new { jobId, tenant = tenantId, jobType, docId, level, payload = payloadJson }, cancellationToken: ct));
    }
    private static string NormalizeStoredSummaryLevel(string? level)
    {
        // Stored summaries are currently persisted only at the reusable medium level.
        // Accept looser inputs from callers and coerce them to the supported storage level
        // instead of letting the DB constraint fail at runtime.
        return "medium";
    }

    private static string NormalizeDocLanguage(string? language)
        => string.IsNullOrWhiteSpace(language)
            ? "und"
            : language.Trim();

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var s = path.Trim().Replace('\\', '/').Trim('/');
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static bool IsBackofficeEnabled(HttpContext ctx)
    {
        var cfg = ctx.RequestServices.GetService(typeof(IConfiguration)) as IConfiguration;
        return string.Equals(cfg?["BACKOFFICE_LLM_ENABLED"], "true", StringComparison.OrdinalIgnoreCase);
    }

    private static RuntimeGovernanceOptions ResolveRuntimeGovernanceOptions(HttpContext ctx)
        => (ctx.RequestServices.GetService(typeof(IOptions<RuntimeGovernanceOptions>)) as IOptions<RuntimeGovernanceOptions>)?.Value
           ?? new RuntimeGovernanceOptions();

    private static Task<IReadOnlyDictionary<Guid, AdminRuntimeCapabilityBBackofficeCandidateDto>> LoadCapabilityBBackofficeCandidateLookupAsync(
        HttpContext ctx,
        NpgsqlConnection conn,
        Guid tenantId,
        string? categoryPath,
        CancellationToken ct)
        => RuntimeGovernanceService.LoadCapabilityBBackofficeCandidateLookupAsync(
            conn,
            tenantId,
            categoryPath,
            ResolveRuntimeGovernanceOptions(ctx),
            ct);

    internal static async Task<SummaryGenerationExecutionDecision> ResolveSummaryGenerationExecutionAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions runtimeOptions,
        RagOptions ragOptions,
        IHostEnvironment env,
        CancellationToken ct)
    {
        if (!IsBackofficeEnabled(ctx))
            return BuildSummaryExecutionDecision("client_admin", null, "backoffice_disabled", false);

        var capabilities = await RuntimeGovernanceReadService.GetCapabilitiesAsync(ds, runtimeOptions, ragOptions, env, ct);
        var capabilityB = capabilities.Items.FirstOrDefault(item => string.Equals(item.Key, "capability_b.backoffice_generation", StringComparison.Ordinal));
        if (capabilityB is not null && capabilityB.Selected && capabilityB.Authorized && capabilityB.Qualified && !capabilityB.Stale)
        {
            var runtimeReady = await IsCapabilityBExecutionRuntimeReadyAsync(ctx, ct);
            if (runtimeReady)
                return BuildSummaryExecutionDecision("server_backoffice", capabilityB.Key, "selected", true);

            return BuildSummaryExecutionDecision("client_admin", capabilityB.Key, "runtime_unavailable", false);
        }

        return BuildSummaryExecutionDecision(
            "client_admin",
            capabilityB?.Key,
            capabilityB?.Stale == true ? "stale" : capabilityB?.Qualified == true ? "qualified_not_selected" : "not_ready",
            false);
    }

    private static async Task<bool> IsCapabilityBExecutionRuntimeReadyAsync(HttpContext ctx, CancellationToken ct)
    {
        var httpFactory = ctx.RequestServices.GetService(typeof(IHttpClientFactory)) as IHttpClientFactory;
        return (await CapabilityBLiveRuntimeProbe.ProbeAsync(httpFactory, ct)).Available;
    }

    private static SummaryGenerationExecutionDecision BuildSummaryExecutionDecision(
        string executionMode,
        string? capabilityKey,
        string status,
        bool usesCapabilityB)
    {
        RuntimeGovernanceTelemetry.RecordCapabilityBExecutionDecision(executionMode, status, usesCapabilityB);
        return new SummaryGenerationExecutionDecision(executionMode, capabilityKey, status, usesCapabilityB);
    }

    private static object? ParseJsonOrNull(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
        catch
        {
            return raw;
        }
    }

    private sealed class SummaryRow
    {
        public Guid DocId { get; set; }
        public string DocPath { get; set; } = "";
        public string DocName { get; set; } = "";
        public string Level { get; set; } = "medium";
        public string? DocLanguage { get; set; }
        public string SourceHash { get; set; } = "";
        public string SummaryText { get; set; } = "";
        public string? SummaryMeta { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class SummaryAdminJobContext
    {
        public string? Status { get; set; }
        public string? ExecutionMode { get; set; }
        public string? RuntimeCapabilityKey { get; set; }
        public string? RuntimeCapabilityStatus { get; set; }
        public bool? RuntimeCapabilitySelected { get; set; }
        public string? RuntimeProfileKey { get; set; }
        public string? EnqueueSource { get; set; }
        public string? ExecutionLeaseToken { get; set; }
        public string? ExecutionClaimedBy { get; set; }
        public DateTimeOffset? ExecutionClaimedAt { get; set; }
        public Guid? CampaignId { get; set; }
    }


    private sealed class CatalogSummariesCursor
    {
        public string? CategoryPath { get; set; }
        public string? CategoryRef { get; set; }
        public int PageSize { get; set; }
        public int Offset { get; set; }
    }

    private static bool CursorMatches(string? cursorValue, string? requestValue)
        => string.IsNullOrWhiteSpace(requestValue) || string.Equals(cursorValue ?? string.Empty, requestValue ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static bool CursorMatches(int cursorValue, int requestValue)
        => requestValue <= 0 || cursorValue == requestValue;

    internal sealed record SummaryGenerationExecutionDecision(
        string ExecutionMode,
        string? CapabilityKey,
        string Status,
        bool UsesCapabilityB);

    private static string BuildAbsoluteNextLink(HttpContext ctx, IReadOnlyDictionary<string, string?> query)
    {
        var request = ctx.Request;
        var builder = new UriBuilder(request.Scheme, request.Host.Host, request.Host.Port ?? -1, "/catalog/summaries");
        var parts = new List<string>();
        foreach (var pair in query)
        {
            if (string.IsNullOrWhiteSpace(pair.Value))
                continue;
            parts.Add($"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}");
        }

        builder.Query = string.Join("&", parts);
        return builder.Uri.AbsoluteUri;
    }

    private sealed class MissingSummaryCountRow
    {
        public int Total { get; set; }
        public int MissingStored { get; set; }
        public int StaleStored { get; set; }
    }

    private sealed class MissingSummaryRow
    {
        public Guid DocId { get; set; }
        public string DocPath { get; set; } = "";
        public string DocName { get; set; } = "";
        public string Category { get; set; } = "";
        public int? PageCount { get; set; }
        public DateTimeOffset? LastIngestedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public string? StoredSourceHash { get; set; }
        public string? CurrentSourceHash { get; set; }
        public string SummaryState { get; set; } = "missing";
        public Guid? ActiveSummaryJobId { get; set; }
        public string? ActiveSummaryJobType { get; set; }
        public string? ActiveSummaryJobStatus { get; set; }
        public string? ActiveSummaryJobExecutionMode { get; set; }
        public string? ActiveSummaryJobRuntimeCapabilityKey { get; set; }
        public string? ActiveSummaryJobRuntimeCapabilityStatus { get; set; }
        public string? ActiveSummaryJobEnqueueSource { get; set; }
        public Guid? ActiveSummaryJobCampaignId { get; set; }
    }
    private sealed class DocumentRow
    {
        public Guid DocId { get; set; }
        public string DocPath { get; set; } = "";
        public string DocName { get; set; } = "";
        public string Category { get; set; } = "";
    }
}
