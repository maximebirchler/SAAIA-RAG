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
using SAAIA.Backend.Shared;

namespace SAAIA.Backend.Endpoints;

internal sealed class SummaryEndpointsMarker { }

public static class SummaryEndpoints
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
        app.MapPost("/admin/jobs/cancel", CancelAdminJobAsync).RequireAdminKey();
        app.MapPost("/admin/jobs/resume", ResumeAdminJobAsync).RequireAdminKey();
        app.MapPost("/admin/jobs/delete_history", DeleteAdminJobHistoryAsync).RequireAdminKey();
        app.MapPost("/admin/jobs/purge", PurgeAdminJobHistoryAsync).RequireAdminKey();
        app.MapPost("/admin/ingestion/reindex", ReindexAsync).RequireAdminKey();

        app.Logger.LogInformation("Mapped summaries/admin tools endpoints");
    }

    public sealed record SummaryCommand(Guid? DocId, string? Level = "medium", Guid? JobId = null, string? DocLanguage = null, string? SourceHash = null, string? SummaryText = null, JsonElement? Meta = null, bool? Force = null, string? DocPath = null);
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


    private static async Task<IResult> CatalogSummariesAsync(
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

        categoryPath = NormalizeCategoryPathOrNull(categoryPath);
        categoryRef = NormalizeCategoryRefOrNull(categoryRef);
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
        categoryPath = await ResolveSummaryCategoryScopeAsync(conn, tenantId, categoryPath, categoryRef, ct);

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
  CASE
    WHEN s.source_hash IS NULL THEN 'missing'
    WHEN s.source_hash <> COALESCE(encode(d.content_hash, 'hex'), md5(COALESCE(d.doc_path,'') || '|' || COALESCE(d.file_size::text,'') || '|' || COALESCE(d.file_mtime::text,''))) THEN 'stale'
    ELSE 'fresh'
  END AS "SummaryState"
FROM documents d
LEFT JOIN document_summaries s
  ON s.tenant_id = d.tenant_id AND s.doc_id = d.doc_id AND s.level='medium'
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

        var value = rows.Select(row =>
        {
            var topLevel = row.DocPath?.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            var canonicalCategory = string.IsNullOrWhiteSpace(topLevel)
                ? row.Category
                : topLevel;

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
                row.SummaryState
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
        categoryPath = NormalizeCategoryPathOrNull(categoryPath);
        categoryRef = NormalizeCategoryRefOrNull(categoryRef);

        await using var conn = await ds.OpenConnectionAsync(ct);
        categoryPath = await ResolveSummaryCategoryScopeAsync(conn, tenantId, categoryPath, categoryRef, ct);

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
        categoryPath = NormalizeCategoryPathOrNull(categoryPath);
        categoryRef = NormalizeCategoryRefOrNull(categoryRef);

        await using var conn = await ds.OpenConnectionAsync(ct);
        categoryPath = await ResolveSummaryCategoryScopeAsync(conn, tenantId, categoryPath, categoryRef, ct);

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
        categoryPath = NormalizeCategoryPathOrNull(categoryPath);
        categoryRef = NormalizeCategoryRefOrNull(categoryRef);
        var lim = Math.Clamp(limit ?? 100, 1, 500);
        var off = Math.Max(offset ?? 0, 0);

        await using var conn = await ds.OpenConnectionAsync(ct);
        categoryPath = await ResolveSummaryCategoryScopeAsync(conn, tenantId, categoryPath, categoryRef, ct);

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

    private static async Task<IResult> MissingSummariesAsync(HttpContext ctx, NpgsqlDataSource ds, string? categoryPath, string? categoryRef, int? limit, int? offset)
    {
        AdminAuth.EnsureAdmin(ctx);
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;
        categoryPath = NormalizeCategoryPathOrNull(categoryPath);
        categoryRef = NormalizeCategoryRefOrNull(categoryRef);
        var lim = Math.Clamp(limit ?? 100, 1, 500);
        var off = Math.Max(offset ?? 0, 0);

        await using var conn = await ds.OpenConnectionAsync(ct);
        categoryPath = await ResolveSummaryCategoryScopeAsync(conn, tenantId, categoryPath, categoryRef, ct);

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
  CASE
    WHEN s.source_hash IS NULL THEN 'missing'
    WHEN s.source_hash <> COALESCE(encode(d.content_hash, 'hex'), md5(COALESCE(d.doc_path,'') || '|' || COALESCE(d.file_size::text,'') || '|' || COALESCE(d.file_mtime::text,''))) THEN 'stale'
    ELSE 'fresh'
  END AS "SummaryState"
FROM documents d
LEFT JOIN document_summaries s
  ON s.tenant_id = d.tenant_id AND s.doc_id = d.doc_id AND s.level='medium'
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

    private static async Task<IResult> GenerateSummaryAsync(HttpContext ctx, NpgsqlDataSource ds, SummaryCommand cmd)
    {
        AdminAuth.EnsureAdmin(ctx);
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;
        if (cmd.DocId is null || cmd.DocId == Guid.Empty)
            return Results.BadRequest(new { error = "doc_id_required" });

        var executionMode = IsBackofficeEnabled(ctx) ? "server_backoffice" : "client_admin";
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
            executionMode
        }, ct);

        return Results.Ok(new { jobId, status = "queued", executionMode, docId = cmd.DocId, level });
    }

    private static async Task<IResult> SubmitSummaryAsync(HttpContext ctx, NpgsqlDataSource ds, SummaryCommand cmd)
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
            docLanguage = string.IsNullOrWhiteSpace(cmd.DocLanguage) ? null : cmd.DocLanguage.Trim(),
            sourceHash,
            summaryText = cmd.SummaryText,
            summaryMeta = (object?)metaJson ?? DBNull.Value
        }, cancellationToken: ct));

        if (cmd.JobId is not null && cmd.JobId != Guid.Empty)
        {
            var jobSql = """
UPDATE admin_jobs
SET status='done', result=@result::jsonb, finished_at=now(), last_error=NULL
WHERE tenant_id=@tenant AND job_id=@jobId;
""";
            var result = JsonSerializer.Serialize(new { docId = cmd.DocId, level, stored = true });
            await conn.ExecuteAsync(new CommandDefinition(jobSql, new { tenant = tenantId, jobId = cmd.JobId, result }, cancellationToken: ct));
        }

        return Results.Ok(new { stored = true, docId = cmd.DocId, level, sourceHash });
    }

    private static async Task<IResult> SummaryStatusAsync(HttpContext ctx, NpgsqlDataSource ds, Guid jobId)
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
  level AS "Level",
  payload AS "Payload",
  result AS "Result",
  last_error AS "LastError",
  created_at AS "CreatedAt",
  started_at AS "StartedAt",
  finished_at AS "FinishedAt"
FROM admin_jobs
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
        if (snapshot is not null)
        {
            try { snapshotTotal = (int)snapshot.TotalDocs; } catch { snapshotTotal = 0; }
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

    private static async Task<IResult> CatalogRescanAsync(HttpContext ctx, NpgsqlDataSource ds, ILogger<SummaryEndpointsMarker> logger, IOptions<CatalogSnapshotOptions> opt)
    {
        AdminAuth.EnsureAdmin(ctx);
        var result = await CatalogSnapshotBuilder.BuildAllActiveTenantsAsync(ds, opt.Value, logger, ctx.RequestAborted);
        return Results.Ok(new { refreshed = true, snapshot = result });
    }

    private static async Task<IResult> ListAdminJobsAsync(HttpContext ctx, NpgsqlDataSource ds, string? type, int? limit, int? offset)
    {
        AdminAuth.EnsureAdmin(ctx);
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;
        type = string.IsNullOrWhiteSpace(type) ? null : type.Trim().ToLowerInvariant();
        var lim = Math.Clamp(limit ?? 100, 1, 500);
        var off = Math.Max(offset ?? 0, 0);

        await using var conn = await ds.OpenConnectionAsync(ct);
        var sql = """
SELECT * FROM (
  SELECT
    job_id       AS "JobId",
    'summary'    AS "Type",
    job_type     AS "JobType",
    status       AS "Status",
    doc_id       AS "DocId",
    NULL::text   AS "DocPath",
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
    NULL::text   AS "EnqueueSource",
    NULL::text   AS "DocumentStatus",
    NULL::int    AS "DocumentIngestionVersion",
    NULL::int    AS "DocumentIndexedVersion",
    NULL::boolean AS "DocumentAutoIngestPaused",
    NULL::text   AS "DocumentAutoIngestPauseReason"
  FROM admin_jobs
  WHERE tenant_id=@tenant

  UNION ALL

  SELECT
    i.job_id       AS "JobId",
    'ingestion'    AS "Type",
    i.action       AS "JobType",
    CASE
      WHEN i.status='paused' THEN 'paused'
      WHEN i.status='canceled'
        AND i.action='upsert'
        AND COALESCE(d.auto_ingest_paused, false)
        AND COALESCE(d.indexed_version, 0) <= 0
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
    i.payload #>> '{progress,phase}' AS "ProgressPhase",
    CASE WHEN jsonb_typeof(i.payload->'progress'->'current')='number' THEN (i.payload->'progress'->>'current')::int ELSE NULL END AS "ProgressCurrent",
    CASE WHEN jsonb_typeof(i.payload->'progress'->'total')='number' THEN (i.payload->'progress'->>'total')::int ELSE NULL END AS "ProgressTotal",
    CASE WHEN jsonb_typeof(i.payload->'progress'->'percent')='number' THEN (i.payload->'progress'->>'percent')::int ELSE NULL END AS "ProgressPercent",
    COALESCE((i.payload #>> '{control,cancelRequested}')::boolean, false) AS "CancelRequested",
    i.payload ->> 'source' AS "EnqueueSource",
    d.status AS "DocumentStatus",
    d.ingestion_version AS "DocumentIngestionVersion",
    d.indexed_version AS "DocumentIndexedVersion",
    COALESCE(d.auto_ingest_paused, false) AS "DocumentAutoIngestPaused",
    d.auto_ingest_pause_reason AS "DocumentAutoIngestPauseReason"
  FROM ingestion_jobs i
  LEFT JOIN documents d
    ON d.tenant_id = i.tenant_id
   AND d.doc_path = i.doc_path
  WHERE i.tenant_id=@tenant
) j
WHERE (@type IS NULL OR lower("Type")=@type)
ORDER BY "CreatedAt" DESC
LIMIT @lim OFFSET @off;
""";

        var rows = await conn.QueryAsync(new CommandDefinition(sql, new { tenant = tenantId, type, lim, off }, cancellationToken: ct));
        return Results.Ok(new { items = rows, limit = lim, offset = off });
    }

    private static async Task<IResult> GetAdminJobAsync(HttpContext ctx, NpgsqlDataSource ds, Guid jobId)
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
    NULL::text   AS "DocPath",
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
    NULL::text   AS "EnqueueSource",
    NULL::text   AS "DocumentStatus",
    NULL::int    AS "DocumentIngestionVersion",
    NULL::int    AS "DocumentIndexedVersion",
    NULL::boolean AS "DocumentAutoIngestPaused",
    NULL::text   AS "DocumentAutoIngestPauseReason"
  FROM admin_jobs
  WHERE tenant_id=@tenant AND job_id=@jobId

  UNION ALL

  SELECT
    i.job_id       AS "JobId",
    'ingestion'    AS "Type",
    i.action       AS "JobType",
    CASE
      WHEN i.status='paused' THEN 'paused'
      WHEN i.status='canceled'
        AND i.action='upsert'
        AND COALESCE(d.auto_ingest_paused, false)
        AND COALESCE(d.indexed_version, 0) <= 0
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
    i.payload #>> '{progress,phase}' AS "ProgressPhase",
    CASE WHEN jsonb_typeof(i.payload->'progress'->'current')='number' THEN (i.payload->'progress'->>'current')::int ELSE NULL END AS "ProgressCurrent",
    CASE WHEN jsonb_typeof(i.payload->'progress'->'total')='number' THEN (i.payload->'progress'->>'total')::int ELSE NULL END AS "ProgressTotal",
    CASE WHEN jsonb_typeof(i.payload->'progress'->'percent')='number' THEN (i.payload->'progress'->>'percent')::int ELSE NULL END AS "ProgressPercent",
    COALESCE((i.payload #>> '{control,cancelRequested}')::boolean, false) AS "CancelRequested",
    i.payload ->> 'source' AS "EnqueueSource",
    d.status AS "DocumentStatus",
    d.ingestion_version AS "DocumentIngestionVersion",
    d.indexed_version AS "DocumentIndexedVersion",
    COALESCE(d.auto_ingest_paused, false) AS "DocumentAutoIngestPaused",
    d.auto_ingest_pause_reason AS "DocumentAutoIngestPauseReason"
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

    private static async Task<IResult> CancelAdminJobAsync(HttpContext ctx, NpgsqlDataSource ds, IngestionJobCancellationRegistry cancelRegistry, JobCancelCommand cmd)
    {
        AdminAuth.EnsureAdmin(ctx);
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;
        if (cmd.JobId == Guid.Empty)
            return Results.BadRequest(new { error = "job_id_required" });

        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var cancelAdminSql = """
UPDATE admin_jobs
SET status='canceled', canceled_at=now(), finished_at=now()
WHERE tenant_id=@tenant AND job_id=@jobId AND status IN ('queued','running');
""";
        var changed = await conn.ExecuteAsync(new CommandDefinition(cancelAdminSql, new { tenant = tenantId, jobId = cmd.JobId }, transaction: tx, cancellationToken: ct));
        if (changed > 0)
        {
            await tx.CommitAsync(ct);
            return Results.Ok(new { canceled = true, jobId = cmd.JobId, type = "summary", affected = changed, status = "canceled", result = "canceled" });
        }

        var ingestionRef = await conn.QueryFirstOrDefaultAsync<(Guid JobId, string DocPath, string Status, bool CancelRequested, string Action, int DocumentIndexedVersion)>(
            new CommandDefinition(
                """
SELECT
  job_id AS "JobId",
  doc_path AS "DocPath",
  status AS "Status",
  COALESCE((payload #>> '{control,cancelRequested}')::boolean, false) AS "CancelRequested",
  action AS "Action",
  COALESCE(d.indexed_version, 0) AS "DocumentIndexedVersion"
FROM ingestion_jobs
LEFT JOIN documents d
  ON d.tenant_id=ingestion_jobs.tenant_id
 AND d.doc_path=ingestion_jobs.doc_path
WHERE tenant_id=@tenant AND job_id=@jobId
LIMIT 1;
""",
                new { tenant = tenantId, jobId = cmd.JobId },
                transaction: tx,
                cancellationToken: ct));

        if (ingestionRef.JobId == Guid.Empty)
        {
            await tx.CommitAsync(ct);
            return Results.NotFound(new { error = "job_not_found", jobId = cmd.JobId });
        }

        var canceledQueued = await conn.ExecuteAsync(new CommandDefinition(
            """
UPDATE ingestion_jobs
SET status='canceled',
    finished_at=COALESCE(finished_at, now()),
    last_error=COALESCE(last_error,'canceled_by_admin'),
    locked_by=NULL,
    locked_at=NULL,
    available_at=now()
WHERE tenant_id=@tenant
  AND doc_path=@docPath
  AND action='upsert'
  AND status IN ('queued','paused');
""",
            new { tenant = tenantId, docPath = ingestionRef.DocPath },
            transaction: tx,
            cancellationToken: ct));

        var runningCancelRequested = await conn.ExecuteAsync(new CommandDefinition(
            """
UPDATE ingestion_jobs
SET payload = jsonb_set(COALESCE(payload, '{}'::jsonb), '{control,cancelRequested}', 'true'::jsonb, true)
WHERE tenant_id=@tenant
  AND doc_path=@docPath
  AND action='upsert'
  AND status='running';
""",
            new { tenant = tenantId, docPath = ingestionRef.DocPath },
            transaction: tx,
            cancellationToken: ct));

        var shouldPauseInitialIngestion = string.Equals(ingestionRef.Action, "upsert", StringComparison.OrdinalIgnoreCase)
            && ingestionRef.DocumentIndexedVersion <= 0;

        if ((canceledQueued > 0 || runningCancelRequested > 0) && shouldPauseInitialIngestion)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
UPDATE documents
SET auto_ingest_paused=true,
    auto_ingest_paused_at=now(),
    auto_ingest_pause_reason='admin_cancel',
    status = CASE
        WHEN COALESCE(indexed_version, 0) > 0 THEN 'indexed'
        ELSE status
    END,
    updated_at=now()
WHERE tenant_id=@tenant
  AND doc_path=@docPath
  AND COALESCE(indexed_version, 0) <= 0;
""",
                new { tenant = tenantId, docPath = ingestionRef.DocPath },
                transaction: tx,
                cancellationToken: ct));
        }

        var pausedQueued = 0;
        if (shouldPauseInitialIngestion && canceledQueued > 0)
        {
            pausedQueued = await conn.ExecuteAsync(new CommandDefinition(
                """
UPDATE ingestion_jobs
SET status='paused',
    started_at=NULL,
    finished_at=NULL,
    last_error=NULL,
    payload = (COALESCE(payload, '{}'::jsonb) #- '{control,cancelRequested}')
WHERE tenant_id=@tenant
  AND doc_path=@docPath
  AND action='upsert'
  AND status='canceled'
  AND COALESCE(last_error,'')='canceled_by_admin';
""",
                new { tenant = tenantId, docPath = ingestionRef.DocPath },
                transaction: tx,
                cancellationToken: ct));
        }

        var effectiveStatus = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            """
SELECT CASE
            WHEN status='paused' THEN 'paused'
            WHEN status='running' AND COALESCE((payload #>> '{control,cancelRequested}')::boolean, false) THEN 'cancel_requested'
            ELSE status
END
FROM ingestion_jobs
WHERE tenant_id=@tenant AND job_id=@jobId
LIMIT 1;
""",
            new { tenant = tenantId, jobId = cmd.JobId },
            transaction: tx,
            cancellationToken: ct));

        var normalizedPreviousStatus = string.Equals(ingestionRef.Status, "running", StringComparison.OrdinalIgnoreCase) && ingestionRef.CancelRequested
            ? "cancel_requested"
            : ingestionRef.Status;

        var result = effectiveStatus switch
        {
            "paused" => "paused",
            "canceled" or "cancelled" => "canceled",
            "cancel_requested" => "cancel_requested",
            "done" or "failed" => "already_finished",
            _ when canceledQueued > 0 || runningCancelRequested > 0 || pausedQueued > 0 => "accepted",
            _ => "nothing_changed"
        };

        await tx.CommitAsync(ct);

        var canceledInMemory = 0;
        if (canceledQueued > 0 || runningCancelRequested > 0)
        {
            canceledInMemory += cancelRegistry.CancelByDocPath(tenantId, ingestionRef.DocPath);
            if (runningCancelRequested == 0 && cancelRegistry.TryCancel(cmd.JobId))
                canceledInMemory++;
        }

        return Results.Ok(new
        {
            canceled = canceledQueued > 0 || runningCancelRequested > 0 || pausedQueued > 0 || string.Equals(effectiveStatus, "canceled", StringComparison.OrdinalIgnoreCase),
            jobId = cmd.JobId,
            type = "ingestion",
            requestedAction = shouldPauseInitialIngestion ? "pause" : "cancel",
            docPath = ingestionRef.DocPath,
            previousStatus = normalizedPreviousStatus,
            status = effectiveStatus ?? normalizedPreviousStatus,
            canceledQueued,
            runningCancelRequested,
            pausedQueued,
            canceledInMemory,
            result
        });
    }

    private static async Task<IResult> ResumeAdminJobAsync(HttpContext ctx, NpgsqlDataSource ds, IOptions<IngestionOptions> ingestOpt, JobResumeCommand cmd)
    {
        AdminAuth.EnsureAdmin(ctx);
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;
        if (cmd.JobId == Guid.Empty)
            return Results.BadRequest(new { error = "job_id_required" });

        await using var conn = await ds.OpenConnectionAsync(ct);

        var row = await conn.QueryFirstOrDefaultAsync<(Guid JobId, string DocPath, string Category, bool AutoIngestPaused, string Status, string Action)>(
            new CommandDefinition(
                """
SELECT
  i.job_id AS "JobId",
  i.doc_path AS "DocPath",
  COALESCE(d.category, i.category, 'general') AS "Category",
  COALESCE(d.auto_ingest_paused, false) AS "AutoIngestPaused",
  i.status AS "Status",
  i.action AS "Action"
FROM ingestion_jobs i
LEFT JOIN documents d
  ON d.tenant_id=i.tenant_id
 AND d.doc_path=i.doc_path
WHERE i.tenant_id=@tenant AND i.job_id=@jobId
LIMIT 1;
""",
                new { tenant = tenantId, jobId = cmd.JobId },
                cancellationToken: ct));

        if (row.JobId == Guid.Empty)
            return Results.NotFound(new { error = "job_not_found", jobId = cmd.JobId });

        if (!row.AutoIngestPaused)
            return Results.Ok(new { resumed = false, reason = "not_paused", jobId = cmd.JobId, docPath = row.DocPath });

        if (!string.Equals(row.Action, "upsert", StringComparison.OrdinalIgnoreCase))
            return Results.Conflict(new { error = "only_upsert_can_resume", jobId = cmd.JobId, docPath = row.DocPath });

        var active = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            """
SELECT 1
FROM ingestion_jobs
WHERE tenant_id=@tenant
  AND doc_path=@docPath
  AND status IN ('queued','running')
  AND job_id<>@jobId
LIMIT 1;
""",
            new { tenant = tenantId, docPath = row.DocPath, jobId = row.JobId },
            cancellationToken: ct));

        if (active.HasValue)
            return Results.Conflict(new { error = "active_job_exists", docPath = row.DocPath });

        var absPath = DocPathNormalizer.ToAbsoluteFromRelative(row.DocPath, ingestOpt.Value.DocumentsRoot);
        if (!File.Exists(absPath))
            return Results.Conflict(new { error = "document_file_not_found", docPath = row.DocPath });

        await conn.ExecuteAsync(new CommandDefinition(
            """
UPDATE documents
SET auto_ingest_paused=false,
    auto_ingest_paused_at=NULL,
    auto_ingest_pause_reason=NULL,
    updated_at=now()
WHERE tenant_id=@tenant AND doc_path=@docPath;
""",
            new { tenant = tenantId, docPath = row.DocPath },
            cancellationToken: ct));

        var resumedRows = await conn.ExecuteAsync(new CommandDefinition(
            """
UPDATE ingestion_jobs
SET status='queued',
    attempts=0,
    locked_by=NULL,
    locked_at=NULL,
    available_at=now(),
    started_at=NULL,
    finished_at=NULL,
    last_error=NULL,
    payload = (COALESCE(payload, '{}'::jsonb) #- '{control,cancelRequested}')
WHERE tenant_id=@tenant
  AND job_id=@jobId
  AND action='upsert'
  AND status IN ('paused','canceled');
""",
            new { tenant = tenantId, jobId = row.JobId },
            cancellationToken: ct));

        if (resumedRows <= 0)
            return Results.Conflict(new { error = "job_not_resumable", jobId = row.JobId, status = row.Status, docPath = row.DocPath });

        return Results.Ok(new { resumed = true, docPath = row.DocPath, jobId = row.JobId });
    }

    private static async Task<IResult> DeleteAdminJobHistoryAsync(HttpContext ctx, NpgsqlDataSource ds)
    {
        AdminAuth.EnsureAdmin(ctx);
        var tenantId = ctx.GetTenantId();
        var actorApiKeyId = ctx.GetApiKeyIdOrNull();
        var actorIsAdmin = ctx.IsAdmin();
        var ct = ctx.RequestAborted;

        static void CollectIds(JsonElement root, string propertyName, List<Guid> target)
        {
            if (!root.TryGetProperty(propertyName, out var prop))
                return;

            if (prop.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in prop.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out var parsed))
                        target.Add(parsed);
                }
                return;
            }

            if (prop.ValueKind == JsonValueKind.String && Guid.TryParse(prop.GetString(), out var single))
                target.Add(single);
        }

        List<Guid> parsedIds = new();
        try
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var body = await reader.ReadToEndAsync(ct);
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    CollectIds(root, "jobIds", parsedIds);
                    CollectIds(root, "ids", parsedIds);
                    CollectIds(root, "jobId", parsedIds);
                }
            }
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "invalid_json" });
        }

        var ids = parsedIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0)
            return Results.BadRequest(new { error = "job_ids_required" });

        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        const string deleteAdminSql = @"
DELETE FROM admin_jobs
WHERE tenant_id=@tenant
  AND job_id = ANY(@ids)
  AND COALESCE(lower(status),'') NOT IN ('queued','running','paused')
RETURNING job_id;";

        const string deleteIngestionSql = @"
DELETE FROM ingestion_jobs
WHERE tenant_id=@tenant
  AND job_id = ANY(@ids)
  AND COALESCE(lower(status),'') NOT IN ('queued','running','paused')
RETURNING job_id;";

        var deletedSummaryIds = (await conn.QueryAsync<Guid>(
            new CommandDefinition(deleteAdminSql, new { tenant = tenantId, ids }, transaction: tx, cancellationToken: ct))).ToArray();
        var deletedIngestionIds = (await conn.QueryAsync<Guid>(
            new CommandDefinition(deleteIngestionSql, new { tenant = tenantId, ids }, transaction: tx, cancellationToken: ct))).ToArray();

        await tx.CommitAsync(ct);

        var deletedIds = deletedSummaryIds.Concat(deletedIngestionIds)
            .Distinct()
            .ToArray();
        var deletedSummary = deletedSummaryIds.Length;
        var deletedIngestion = deletedIngestionIds.Length;
        var deleted = deletedIds.Length;
        var skippedIds = ids.Except(deletedIds).ToArray();
        var skipped = skippedIds.Length;

        try
        {
            await AuditWriter.WriteAsync(
                conn,
                tenantId,
                actorApiKeyId,
                actorIsAdmin,
                action: "admin.jobs.delete_history",
                target: "admin.jobs",
                payload: new { jobIds = ids, deletedIds, skippedIds, deletedSummary, deletedIngestion, deleted, skipped },
                ip: ctx.Connection.RemoteIpAddress?.ToString(),
                ct: ct);
        }
        catch
        {
        }

        return Results.Ok(new
        {
            requested = ids.Length,
            deleted,
            skipped,
            deletedSummary,
            deletedIngestion,
            deletedIds,
            skippedIds
        });
    }

    private static async Task<IResult> PurgeAdminJobHistoryAsync(HttpContext ctx, NpgsqlDataSource ds, JobPurgeCommand? cmd)
    {
        AdminAuth.EnsureAdmin(ctx);
        var tenantId = ctx.GetTenantId();
        var actorApiKeyId = ctx.GetApiKeyIdOrNull();
        var actorIsAdmin = ctx.IsAdmin();
        var ct = ctx.RequestAborted;

        var scope = (cmd?.Scope ?? "terminal").Trim().ToLowerInvariant();
        var type = string.IsNullOrWhiteSpace(cmd?.Type) ? null : cmd!.Type!.Trim().ToLowerInvariant();

        if (scope is not ("terminal" or "all" or "done" or "failed"))
            return Results.BadRequest(new { error = "invalid_scope", expected = new[] { "terminal", "all", "done", "failed" } });
        if (type is not null && type is not ("summary" or "ingestion"))
            return Results.BadRequest(new { error = "invalid_type", expected = new[] { "summary", "ingestion" } });

        static string BuildStatusFilter(string tableAlias, string scopeValue)
            => scopeValue switch
            {
                "done" => $"COALESCE(lower({tableAlias}.status),'') = 'done'",
                "failed" => $"COALESCE(lower({tableAlias}.status),'') IN ('failed','canceled','cancelled')",
                _ => $"COALESCE(lower({tableAlias}.status),'') NOT IN ('queued','running','paused')"
            };

        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var adminDeletedIds = Array.Empty<Guid>();
        var ingestionDeletedIds = Array.Empty<Guid>();

        if (type is null or "summary")
        {
            var adminSql = $"""
DELETE FROM admin_jobs a
WHERE a.tenant_id=@tenant
  AND {BuildStatusFilter("a", scope)}
RETURNING a.job_id;
""";
            adminDeletedIds = (await conn.QueryAsync<Guid>(
                new CommandDefinition(adminSql, new { tenant = tenantId }, transaction: tx, cancellationToken: ct))).ToArray();
        }

        if (type is null or "ingestion")
        {
            var ingestionSql = $"""
DELETE FROM ingestion_jobs i
WHERE i.tenant_id=@tenant
  AND {BuildStatusFilter("i", scope)}
RETURNING i.job_id;
""";
            ingestionDeletedIds = (await conn.QueryAsync<Guid>(
                new CommandDefinition(ingestionSql, new { tenant = tenantId }, transaction: tx, cancellationToken: ct))).ToArray();
        }

        await tx.CommitAsync(ct);

        var deletedIds = adminDeletedIds.Concat(ingestionDeletedIds).Distinct().ToArray();
        var deletedSummary = adminDeletedIds.Length;
        var deletedIngestion = ingestionDeletedIds.Length;
        var deleted = deletedIds.Length;

        try
        {
            await AuditWriter.WriteAsync(
                conn,
                tenantId,
                actorApiKeyId,
                actorIsAdmin,
                action: "admin.jobs.purge",
                target: "admin.jobs",
                payload: new { scope, type, deletedIds, deletedSummary, deletedIngestion, deleted },
                ip: ctx.Connection.RemoteIpAddress?.ToString(),
                ct: ct);
        }
        catch
        {
        }

        return Results.Ok(new
        {
            scope,
            type = type ?? "all",
            deleted,
            deletedSummary,
            deletedIngestion,
            deletedIds
        });
    }

    private static async Task<IResult> ReindexAsync(HttpContext ctx, NpgsqlDataSource ds, IOptions<IngestionOptions> ingestOpt, SummaryCommand cmd)
    {
        AdminAuth.EnsureAdmin(ctx);
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;
        await using var conn = await ds.OpenConnectionAsync(ct);

        DocumentRow? doc = null;
        if (cmd.DocId is not null && cmd.DocId != Guid.Empty)
            doc = await LoadDocumentAsync(conn, tenantId, cmd.DocId.Value, ct);
        else if (!string.IsNullOrWhiteSpace(cmd.DocPath))
            doc = await LoadDocumentByPathAsync(conn, tenantId, cmd.DocPath!, ct);

        if (doc is null)
            return Results.NotFound(new { error = "document_not_found" });

        FileInfo? fi = null;
        try
        {
            var documentsRoot = ingestOpt.Value.DocumentsRoot;
            var absPath = DocPathNormalizer.ToAbsoluteFromRelative(doc.DocPath, documentsRoot);
            if (!File.Exists(absPath))
                return Results.NotFound(new { error = "document_file_not_found", docPath = doc.DocPath });

            fi = new FileInfo(absPath);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = "invalid_document_path", detail = ex.Message, docPath = doc.DocPath });
        }

        var activeJob = await conn.QueryFirstOrDefaultAsync<(Guid JobId, string Status, bool CancelRequested)>(new CommandDefinition(
            """
SELECT
  job_id AS "JobId",
  status AS "Status",
  COALESCE((payload #>> '{control,cancelRequested}')::boolean, false) AS "CancelRequested"
FROM ingestion_jobs
WHERE tenant_id=@tenant
  AND doc_path=@docPath
  AND status IN ('queued','running','paused')
ORDER BY created_at DESC
LIMIT 1;
""",
            new { tenant = tenantId, docPath = doc.DocPath },
            cancellationToken: ct));

        if (activeJob.JobId != Guid.Empty)
        {
            var effectiveStatus = string.Equals(activeJob.Status, "running", StringComparison.OrdinalIgnoreCase) && activeJob.CancelRequested
                ? "cancel_requested"
                : activeJob.Status;
            return Results.Conflict(new
            {
                error = "active_job_exists",
                jobId = activeJob.JobId,
                status = effectiveStatus,
                docPath = doc.DocPath
            });
        }

        var enqueued = await IngestionEnqueue.EnqueueUpsertAsync(conn, tenantId, doc.DocPath, doc.Category, fi, ct: ct, enqueueSource: "admin");
        return Results.Ok(new { queued = true, docId = enqueued.DocId, jobId = enqueued.JobId, docPath = doc.DocPath });
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

    private static string? NormalizeCategoryPathOrNull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var normalized = path.Trim().Replace('\\', '/').Trim().Trim('/');
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? NormalizeCategoryRefOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    private static async Task<string?> ResolveSummaryCategoryScopeAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string? categoryPath,
        string? categoryRef,
        CancellationToken ct)
    {
        var normalizedPath = NormalizeCategoryPathOrNull(categoryPath);
        if (!string.IsNullOrWhiteSpace(normalizedPath))
        {
            var exactPath = await ResolveExistingSummaryCategoryPathAsync(conn, tenantId, normalizedPath!, ct);
            if (!string.IsNullOrWhiteSpace(exactPath))
                return exactPath;

            return await ResolveSummaryCategoryRefToPathAsync(conn, tenantId, categoryRef ?? normalizedPath!, ct);
        }

        if (string.IsNullOrWhiteSpace(categoryRef))
            return null;

        return await ResolveSummaryCategoryRefToPathAsync(conn, tenantId, categoryRef!, ct);
    }

    private static async Task<string?> ResolveExistingSummaryCategoryPathAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string categoryPath,
        CancellationToken ct)
    {
        var normalizedPath = NormalizeCategoryPathOrNull(categoryPath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
            return null;

        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            @"SELECT path FROM documents_category_nodes WHERE tenant_id=@tenant AND path=@path LIMIT 1;",
            new { tenant = tenantId, path = normalizedPath },
            cancellationToken: ct));
    }

    private static async Task<string?> ResolveSummaryCategoryRefToPathAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string categoryRef,
        CancellationToken ct)
    {
        var raw = NormalizeCategoryRefOrNull(categoryRef);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var normalizedPath = NormalizeCategoryPathOrNull(raw);
        if (!string.IsNullOrWhiteSpace(normalizedPath))
        {
            var exactPath = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                @"SELECT path FROM documents_category_nodes WHERE tenant_id=@tenant AND path=@path LIMIT 1;",
                new { tenant = tenantId, path = normalizedPath },
                cancellationToken: ct));
            if (!string.IsNullOrWhiteSpace(exactPath))
                return exactPath;
        }

        var ordinal = TryParseOrdinalCategoryRef(raw);
        if (ordinal is not null)
        {
            var byOrder = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                @"SELECT path FROM documents_catalog_categories WHERE tenant_id=@tenant AND display_order=@displayOrder LIMIT 1;",
                new { tenant = tenantId, displayOrder = ordinal.Value },
                cancellationToken: ct));
            if (!string.IsNullOrWhiteSpace(byOrder))
                return byOrder;
        }

        var probe = NormalizeCategoryComparableText(raw);
        if (!string.IsNullOrWhiteSpace(probe))
        {
            var byAlias = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                @"SELECT path
FROM documents_catalog_category_aliases
WHERE tenant_id=@tenant AND alias_key=@aliasKey
ORDER BY priority ASC, path ASC
LIMIT 1;",
                new { tenant = tenantId, aliasKey = probe },
                cancellationToken: ct));
            if (!string.IsNullOrWhiteSpace(byAlias))
                return byAlias;
        }

        var byName = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            @"SELECT path FROM documents_catalog_categories WHERE tenant_id=@tenant AND (LOWER(path)=LOWER(@value) OR LOWER(name)=LOWER(@value)) ORDER BY display_order ASC, name ASC LIMIT 1;",
            new { tenant = tenantId, value = raw.Trim() },
            cancellationToken: ct));
        if (!string.IsNullOrWhiteSpace(byName))
            return byName;

        if (!string.IsNullOrWhiteSpace(probe))
        {
            var candidates = (await conn.QueryAsync<TopCategorySnapshotRow>(new CommandDefinition(
                """
SELECT path AS "Path", name AS "Name", display_order AS "DisplayOrder"
FROM documents_catalog_categories
WHERE tenant_id=@tenant
ORDER BY display_order ASC, name ASC;
""",
                new { tenant = tenantId },
                cancellationToken: ct))).ToList();

            var matched = candidates.FirstOrDefault(candidate =>
            {
                var candidateName = NormalizeCategoryComparableText(candidate.Name);
                if (IsLooseCategoryComparableMatch(probe, candidateName))
                    return true;

                var candidatePath = NormalizeCategoryComparableText(candidate.Path);
                if (IsLooseCategoryComparableMatch(probe, candidatePath))
                    return true;

                var topLevelPath = NormalizeCategoryComparableText(candidate.Path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault());
                return IsLooseCategoryComparableMatch(probe, topLevelPath);
            });

            if (matched is not null && !string.IsNullOrWhiteSpace(matched.Path))
                return matched.Path;
        }

        return normalizedPath;
    }

    private static int? TryParseOrdinalCategoryRef(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var trimmed = raw.Trim();
        if (int.TryParse(trimmed, out var direct) && direct > 0)
            return direct;

        var compact = trimmed.Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);
        if (compact.StartsWith("cat", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(compact.Substring(3), out var catOrdinal)
            && catOrdinal > 0)
        {
            return catOrdinal;
        }

        var ordinalMatch = Regex.Match(trimmed, @"(?:^|\b)(?:cat(?:egory)?|cat[ée]gorie|categoria|kategorie)?\s*(?<n>\d{1,4})(?:st|nd|rd|th|er|e|eme|ème)?(?:\b|$)", RegexOptions.IgnoreCase);
        if (ordinalMatch.Success && int.TryParse(ordinalMatch.Groups["n"].Value, out var parsed) && parsed > 0)
            return parsed;

        return null;
    }

    private static string NormalizeCategoryComparableText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = RemoveDiacritics(value).ToLowerInvariant();
        normalized = normalized.Replace('&', ' ');
        normalized = Regex.Replace(normalized, @"[^a-z0-9]+", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        return normalized;
    }

    private static bool IsLooseCategoryComparableMatch(string probe, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(probe) || string.IsNullOrWhiteSpace(candidate))
            return false;

        if (string.Equals(probe, candidate, StringComparison.Ordinal))
            return true;

        if (candidate.StartsWith(probe, StringComparison.Ordinal) || probe.StartsWith(candidate, StringComparison.Ordinal))
            return true;

        var probeCompact = probe.Replace(" ", string.Empty, StringComparison.Ordinal);
        var candidateCompact = candidate.Replace(" ", string.Empty, StringComparison.Ordinal);

        if (string.Equals(probeCompact, candidateCompact, StringComparison.Ordinal))
            return true;

        if (candidateCompact.StartsWith(probeCompact, StringComparison.Ordinal) || probeCompact.StartsWith(candidateCompact, StringComparison.Ordinal))
            return true;

        var sharedPrefix = 0;
        var max = Math.Min(probeCompact.Length, candidateCompact.Length);
        while (sharedPrefix < max && probeCompact[sharedPrefix] == candidateCompact[sharedPrefix])
            sharedPrefix++;

        return sharedPrefix >= 7;
    }

    private static string RemoveDiacritics(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string NormalizeStoredSummaryLevel(string? level)
    {
        // Stored summaries are currently persisted only at the reusable medium level.
        // Accept looser inputs from callers and coerce them to the supported storage level
        // instead of letting the DB constraint fail at runtime.
        return "medium";
    }

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
    }

    private sealed class TopCategorySnapshotRow
    {
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public int DisplayOrder { get; set; }
    }

    private sealed class DocumentRow
    {
        public Guid DocId { get; set; }
        public string DocPath { get; set; } = "";
        public string DocName { get; set; } = "";
        public string Category { get; set; } = "";
    }
}
