using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Dapper;
using Npgsql;
using SAAIA.Backend.Audit;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Shared;
using SAAIA.Contracts;

namespace SAAIA.Backend.Endpoints;

public static partial class DocumentsEndpoints
{
    public static void Map(WebApplication app)
    {
        // Unified user/admin documents surface:
        // - user callers receive the safe indexed catalog contract
        // - admin callers keep the richer legacy admin contract
        app.MapGet("/documents", UnifiedListAsync);
        app.MapGet("/documents/{docId:guid}", UnifiedGetAsync);

        // Transition alias kept for older clients until `/documents` convergence is complete everywhere.
        app.MapGet("/documents/catalog", CatalogAsync);
        app.MapGet("/documents/catalog/{docId:guid}", CatalogGetAsync);

    // Inventory helpers (CDC v3.1)
        app.MapGet("/documents/count", CountAsync);
        app.MapGet("/documents/categories", CategoriesAsync);
        app.MapGet("/documents/tree", TreeAsync);
        app.MapGet("/documents/stats", StatsAsync);
        app.MapPost("/documents/resolve-category", ResolveCategoryAsync);

        // Public read-only catalog context used by the client runtime; snapshot mutations stay admin-only.
        app.MapGet("/catalog/snapshot", SnapshotAsync);
        app.MapGet("/catalog/categories", CatalogCategoriesAsync);
        app.MapGet("/catalog/documents", CatalogDocumentsAsync);
        app.MapGet("/catalog/stats", CatalogStatsAsync);

        // Admin/health filesystem diagnostics (kept out of nominal user inventory)
        app.MapGet("/admin/catalog/empty-folders/count", EmptyFoldersCountAsync).RequireAdminKey();
        app.MapGet("/admin/catalog/empty-folders", EmptyFoldersListAsync).RequireAdminKey();
        app.MapGet("/admin/documents/extraction-quality", ExtractionQualityAsync).RequireAdminKey();

        app.Logger.LogInformation("Mapped documents endpoints (catalog + count/tree/stats + legacy admin list + admin diagnostics)");
    }

    private static Task<IResult> UnifiedListAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        string? category,
        string? status,
        string? categoryPath,
        string? categoryRef,
        string? q,
        DateTimeOffset? changedSince,
        int? limit,
        int? offset)
        => ctx.IsAdmin()
            ? ListAsync(ctx, ds, category, status, q, limit, offset)
            : CatalogAsync(ctx, ds, category, categoryPath, categoryRef, q, changedSince, limit, offset);

    private static Task<IResult> UnifiedGetAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        Guid docId)
        => ctx.IsAdmin()
            ? GetAsync(ctx, ds, docId)
            : CatalogGetAsync(ctx, ds, docId);

    // -------------------------
    // User catalog (indexed only)
    // -------------------------

    private static async Task<IResult> CatalogAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        string? category,
        string? categoryPath,
        string? categoryRef,
        string? q,
        DateTimeOffset? changedSince,
        int? limit,
        int? offset)
    {
        var tenantId = ctx.GetTenantId();
        var actorApiKeyId = ctx.GetApiKeyIdOrNull();
        var actorIsAdmin = ctx.IsAdmin();
        var ct = ctx.RequestAborted;

        var lim = Math.Clamp(limit ?? 200, 1, 2000);
        var off = Math.Max(offset ?? 0, 0);

        category = string.IsNullOrWhiteSpace(category) ? null : category.Trim().ToLowerInvariant();
        categoryPath = DocumentsCategoryScopeResolver.NormalizeCategoryPathOrNull(categoryPath);
        categoryRef = DocumentsCategoryScopeResolver.NormalizeCategoryRefOrNull(categoryRef);
        q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();

        await using var conn = await ds.OpenConnectionAsync(ct);
        categoryPath = await DocumentsCategoryScopeResolver.ResolveCategoryScopeAsync(conn, tenantId, categoryPath, categoryRef, ct);

        const string sql = @"
SELECT
  doc_id           AS ""DocId"",
  doc_path         AS ""DocPath"",
  doc_name         AS ""DocName"",
  category         AS ""Category"",
  CASE WHEN doc_path LIKE '%/%' THEN regexp_replace(doc_path, '/[^/]+$', '') ELSE '' END AS ""CategoryPath"",
  status           AS ""Status"",
  page_count       AS ""PageCount"",
  last_ingested_at AS ""LastIngestedAt"",
  updated_at       AS ""UpdatedAt""
FROM documents
WHERE tenant_id=@tenant
  AND status='indexed'
  AND (@category IS NULL OR category=@category)
  AND (@categoryPath IS NULL OR doc_path LIKE (@categoryPath || '/%'))
  AND (@changedSince IS NULL OR updated_at >= @changedSince)
  AND (@q IS NULL OR (doc_name ILIKE ('%' || @q || '%') OR doc_path ILIKE ('%' || @q || '%')))
ORDER BY category ASC, doc_path ASC
LIMIT @lim OFFSET @off;";

        var rows = await conn.QueryAsync(sql, new { tenant = tenantId, category, categoryPath, changedSince, q, lim, off });

        await AuditWriter.WriteAsync(
            conn,
            tenantId,
            actorApiKeyId,
            actorIsAdmin,
            action: "documents.catalog",
            target: null,
            payload: new { category, categoryPath, categoryRef, q, changedSince, limit = lim, offset = off },
            ip: ctx.Connection.RemoteIpAddress?.ToString(),
            ct: ct);

        return Results.Ok(new { items = rows, limit = lim, offset = off });
    }

    private static async Task<IResult> CatalogGetAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        Guid docId)
    {
        var tenantId = ctx.GetTenantId();
        var actorApiKeyId = ctx.GetApiKeyIdOrNull();
        var actorIsAdmin = ctx.IsAdmin();
        var ct = ctx.RequestAborted;

        await using var conn = await ds.OpenConnectionAsync(ct);

        const string sql = @"
SELECT
  doc_id           AS ""DocId"",
  doc_path         AS ""DocPath"",
  doc_name         AS ""DocName"",
  category         AS ""Category"",
  CASE WHEN doc_path LIKE '%/%' THEN regexp_replace(doc_path, '/[^/]+$', '') ELSE '' END AS ""CategoryPath"",
  status           AS ""Status"",
  page_count       AS ""PageCount"",
  last_ingested_at AS ""LastIngestedAt"",
  updated_at       AS ""UpdatedAt""
FROM documents
WHERE tenant_id=@tenant AND doc_id=@docId AND status='indexed'
LIMIT 1;";

        var row = await conn.QueryFirstOrDefaultAsync(sql, new { tenant = tenantId, docId });
        if (row is null) return Results.NotFound();

        await AuditWriter.WriteAsync(
            conn,
            tenantId,
            actorApiKeyId,
            actorIsAdmin,
            action: "documents.catalog.get",
            target: docId.ToString(),
            payload: null,
            ip: ctx.Connection.RemoteIpAddress?.ToString(),
            ct: ct);

        return Results.Ok(row);
    }

    // -------------------------
    // Inventory endpoints (CDC v3.1)
    // Snapshot-first with safe fallbacks.
    // -------------------------

    private static async Task<IResult> CountAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        string? categoryPath,
        string? categoryRef,
        string? q)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        categoryPath = DocumentsCategoryScopeResolver.NormalizeCategoryPathOrNull(categoryPath);
        categoryRef = DocumentsCategoryScopeResolver.NormalizeCategoryRefOrNull(categoryRef);
        q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();

        await using var conn = await ds.OpenConnectionAsync(ct);
        categoryPath = await DocumentsCategoryScopeResolver.ResolveCategoryScopeAsync(conn, tenantId, categoryPath, categoryRef, ct);

        const string sql = @"
SELECT COUNT(*)
FROM documents
WHERE tenant_id=@tenant
  AND status='indexed'
  AND (@categoryPath IS NULL OR doc_path LIKE (@categoryPath || '/%'))
  AND (@q IS NULL OR (doc_name ILIKE ('%' || @q || '%') OR doc_path ILIKE ('%' || @q || '%')));";

        var total = await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, new { tenant = tenantId, categoryPath, q }, cancellationToken: ct));
        return Results.Ok(new { total });
    }

    private static async Task<IResult> CategoriesAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        string? path,
        string? categoryRef,
        int? limit,
        int? offset)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;
        path = DocumentsCategoryScopeResolver.NormalizeCategoryPathOrNull(path);
        categoryRef = DocumentsCategoryScopeResolver.NormalizeCategoryRefOrNull(categoryRef);
        var lim = Math.Clamp(limit ?? 100, 1, 500);
        var off = Math.Max(offset ?? 0, 0);

        await using var conn = await ds.OpenConnectionAsync(ct);
        path = await DocumentsCategoryScopeResolver.ResolveCategoryScopeAsync(conn, tenantId, path, categoryRef, ct);

        if (string.IsNullOrWhiteSpace(path))
        {
            const string sql = @"
SELECT
  path             AS ""Path"",
  name             AS ""Name"",
  display_order    AS ""DisplayOrder"",
  doc_count        AS ""DocCount"",
  direct_doc_count AS ""DirectDocCount"",
  subfolder_count  AS ""SubfolderCount""
FROM documents_catalog_categories
WHERE tenant_id=@tenant
ORDER BY display_order ASC, name ASC
LIMIT @lim OFFSET @off;";

            const string totalSql = @"SELECT COUNT(*) FROM documents_catalog_categories WHERE tenant_id=@tenant;";

            var rows = (await conn.QueryAsync<TopCategoryDto>(new CommandDefinition(sql, new { tenant = tenantId, lim, off }, cancellationToken: ct))).ToList();
            var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(totalSql, new { tenant = tenantId }, cancellationToken: ct));
        var aliasesByPath = await DocumentsCategoryScopeResolver.LoadTopCategoryAliasesAsync(conn, tenantId, rows.Select(x => x.Path).ToList(), ct);

            var items = rows.Select(x => new
            {
                ordinal = x.DisplayOrder,
                displayOrder = x.DisplayOrder,
                path = x.Path,
                name = x.Name,
                depth = 1,
                totalDocuments = x.DocCount,
                directDocuments = x.DirectDocCount,
                subfolderCount = x.SubfolderCount,
                aliases = aliasesByPath.TryGetValue(x.Path, out var aliases) ? aliases : Array.Empty<string>()
            }).ToList();

            return Results.Ok(new { scopePath = (string?)null, total, limit = lim, offset = off, items });
        }

        const string scopedSql = @"
SELECT
  n.path             AS ""Path"",
  n.name             AS ""Name"",
  n.depth            AS ""Depth"",
  n.doc_count        AS ""DocCount"",
  n.direct_doc_count AS ""DirectDocCount"",
  COALESCE(c.child_count, 0) AS ""SubfolderCount""
FROM documents_category_nodes n
LEFT JOIN (
    SELECT tenant_id, parent_path, COUNT(*)::int AS child_count
    FROM documents_category_nodes
    WHERE tenant_id=@tenant
    GROUP BY tenant_id, parent_path
) c ON c.tenant_id = n.tenant_id AND c.parent_path = n.path
WHERE n.tenant_id=@tenant AND n.parent_path=@path
ORDER BY n.doc_count DESC, n.name ASC
LIMIT @lim OFFSET @off;";

        const string scopedTotalSql = @"
SELECT COUNT(*)
FROM documents_category_nodes
WHERE tenant_id=@tenant AND parent_path=@path;";

        var scopedRows = (await conn.QueryAsync<CategoryNodeDto>(new CommandDefinition(scopedSql, new { tenant = tenantId, path, lim, off }, cancellationToken: ct))).ToList();
        var scopedTotal = await conn.ExecuteScalarAsync<int>(new CommandDefinition(scopedTotalSql, new { tenant = tenantId, path }, cancellationToken: ct));

        var scopedItems = scopedRows.Select(x => new
        {
            path = x.Path,
            name = x.Name,
            depth = x.Depth,
            totalDocuments = x.DocCount,
            directDocuments = x.DirectDocCount,
            subfolderCount = x.SubfolderCount
        }).ToList();

        return Results.Ok(new { scopePath = path, total = scopedTotal, limit = lim, offset = off, items = scopedItems });
    }

    private static async Task<IResult> TreeAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        string? path,
        string? categoryRef,
        int? depth,
        string? format,
        int? limit,
        int? offset)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        path = DocumentsCategoryScopeResolver.NormalizeCategoryPathOrNull(path);
        categoryRef = DocumentsCategoryScopeResolver.NormalizeCategoryRefOrNull(categoryRef);
        var maxDepth = depth is null ? (int?)null : Math.Clamp(depth.Value, 0, 20);
        format = string.IsNullOrWhiteSpace(format) ? "json" : format.Trim().ToLowerInvariant();

        await using var conn = await ds.OpenConnectionAsync(ct);
        path = await DocumentsCategoryScopeResolver.ResolveCategoryScopeAsync(conn, tenantId, path, categoryRef, ct);

        // Snapshot-first
        var snapTree = await TryLoadTreeFromSnapshotAsync(conn, tenantId, path, maxDepth, ct);
        if (snapTree is not null)
        {
            return BuildTreeResponse(ctx, format, maxDepth, snapTree.Path, "snapshot", snapTree);
        }

        // Fallback (no snapshot yet)
        const string sql = @"
SELECT doc_path AS ""DocPath""
FROM documents
WHERE tenant_id=@tenant AND status='indexed';";

        var docPaths = (await conn.QueryAsync<string>(new CommandDefinition(sql, new { tenant = tenantId }, cancellationToken: ct)))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        var root = BuildTreeFromDocPaths(docPaths);

        TreeNode node = root;
        if (!string.IsNullOrWhiteSpace(path))
        {
            var found = FindNode(root, path);
            if (found is null) return Results.NotFound(new { error = "path_not_found", path });
            node = found;
        }

        return BuildTreeResponse(ctx, format, maxDepth, node.Path, "documents", node);
    }

    private static IResult BuildTreeResponse(HttpContext ctx, string format, int? maxDepth, string path, string source, TreeNode node)
    {
        object payload;
        if (format is "markdown" or "md")
        {
            var sb = new StringBuilder();
            RenderMarkdownTree(node, sb, indent: 0, maxDepth: maxDepth);
            payload = new { path, markdown = sb.ToString(), source };
        }
        else
        {
            payload = new { path, tree = ToDto(node, maxDepth), source };
        }

        var json = JsonSerializer.Serialize(payload);
        var etag = BuildTreeEtag(json);
        ctx.Response.Headers.ETag = etag;

        if (RequestIfNoneMatchMatches(ctx, etag))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        return Results.Text(json, "application/json");
    }

    private static string BuildTreeEtag(string json)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return $"\"tree-{Convert.ToHexString(hash).ToLowerInvariant()}\"";
    }


    private static IResult EmptyFoldersCountAsync(HttpContext ctx, IOptions<IngestionOptions> ingestOpt, string? path)
    {
        AdminAuth.EnsureAdmin(ctx);

        var (ok, root, scopeAbs, scopeRel, errorResult) = TryResolveFolderScope(ingestOpt.Value, path);
        if (!ok)
            return errorResult!;

        var folders = GetEmptyFolderPaths(root!, scopeAbs!);
        return Results.Ok(new
        {
            scopePath = scopeRel,
            total = folders.Count
        });
    }

    private static IResult EmptyFoldersListAsync(HttpContext ctx, IOptions<IngestionOptions> ingestOpt, string? path, int? limit, int? offset)
    {
        AdminAuth.EnsureAdmin(ctx);

        var (ok, root, scopeAbs, scopeRel, errorResult) = TryResolveFolderScope(ingestOpt.Value, path);
        if (!ok)
            return errorResult!;

        var lim = Math.Clamp(limit ?? 200, 1, 2000);
        var off = Math.Max(offset ?? 0, 0);
        var folders = GetEmptyFolderPaths(root!, scopeAbs!);
        var page = folders
            .Skip(off)
            .Take(lim)
            .Select(x => new { path = x, name = Path.GetFileName(x) })
            .ToList();

        return Results.Ok(new
        {
            scopePath = scopeRel,
            total = folders.Count,
            limit = lim,
            offset = off,
            items = page
        });
    }

    private static (bool ok, string? root, string? scopeAbs, string? scopeRel, IResult? errorResult) TryResolveFolderScope(IngestionOptions ingest, string? path)
    {
        var root = (ingest.DocumentsRoot ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return (false, null, null, null, Results.BadRequest(new { error = "documents_root_not_found", root }));

        var scopeRel = DocumentsCategoryScopeResolver.NormalizeCategoryPathOrNull(path) ?? string.Empty;
        var scopeAbs = string.IsNullOrWhiteSpace(scopeRel)
            ? Path.GetFullPath(root)
            : Path.GetFullPath(Path.Combine(root, scopeRel.Replace('/', Path.DirectorySeparatorChar)));

        var rootFull = Path.GetFullPath(root);
        if (!scopeAbs.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            return (false, null, null, null, Results.BadRequest(new { error = "path_outside_root", path = scopeRel }));

        if (!Directory.Exists(scopeAbs))
            return (false, null, null, null, Results.NotFound(new { error = "path_not_found", path = scopeRel }));

        return (true, rootFull, scopeAbs, scopeRel, null);
    }

    private static List<string> GetEmptyFolderPaths(string root, string scopeAbs)
    {
        var result = new List<string>();

        bool ShouldSkipDir(string absDir)
        {
            var name = Path.GetFileName(absDir)?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                return false;
            if (name.StartsWith(".", StringComparison.Ordinal) || name.StartsWith("~", StringComparison.Ordinal))
                return true;
            return string.Equals(name, "__macosx", StringComparison.OrdinalIgnoreCase);
        }

        bool HasValidPdfDirectly(string absDir)
        {
            foreach (var file in Directory.EnumerateFiles(absDir, "*", SearchOption.TopDirectoryOnly))
            {
                var rel = IngestionPathFilter.SafeRelPath(root, file);
                if (!string.IsNullOrWhiteSpace(rel) && !IngestionPathFilter.ShouldIgnoreRel(rel))
                    return true;
            }
            return false;
        }

        bool Scan(string absDir, bool isScopeRoot)
        {
            var subtreeHasPdf = HasValidPdfDirectly(absDir);

            foreach (var child in Directory.EnumerateDirectories(absDir, "*", SearchOption.TopDirectoryOnly).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (ShouldSkipDir(child))
                    continue;

                if (Scan(child, isScopeRoot: false))
                    subtreeHasPdf = true;
            }

            if (!isScopeRoot && !subtreeHasPdf)
            {
                var relDir = Path.GetRelativePath(root, absDir).Replace('\\', '/').Trim('/');
                if (!string.IsNullOrWhiteSpace(relDir))
                    result.Add(relDir);
            }

            return subtreeHasPdf;
        }

        Scan(scopeAbs, isScopeRoot: true);
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static async Task<IResult> StatsAsync(HttpContext ctx, NpgsqlDataSource ds, IOptions<IngestionOptions> ingestOpt, string? path, string? categoryRef)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;
        path = DocumentsCategoryScopeResolver.NormalizeCategoryPathOrNull(path);
        categoryRef = DocumentsCategoryScopeResolver.NormalizeCategoryRefOrNull(categoryRef);

        await using var conn = await ds.OpenConnectionAsync(ct);
        path = await DocumentsCategoryScopeResolver.ResolveCategoryScopeAsync(conn, tenantId, path, categoryRef, ct);

        var snapshotPayload = await TryBuildStatsFromSnapshotAsync(conn, tenantId, path, ct);
        if (snapshotPayload is not null)
            return BuildCachedJsonResponse(ctx, "stats", snapshotPayload);

        // Fallback only when the snapshot is absent or incomplete.
        const string sql = @"
SELECT doc_path
FROM documents
WHERE tenant_id=@tenant AND status='indexed';";

        var docPaths = (await conn.QueryAsync<string>(new CommandDefinition(sql, new { tenant = tenantId }, cancellationToken: ct)))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        var root = BuildTreeFromDocPaths(docPaths);
        var scopedNode = string.IsNullOrWhiteSpace(path) ? root : FindNode(root, path!);
        if (scopedNode is null)
            return Results.NotFound(new { error = "path_not_found", path });

        var scopedDepthBase = string.IsNullOrWhiteSpace(scopedNode.Path)
            ? 0
            : scopedNode.Path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

        var foldersByDepth = new Dictionary<int, int>();
        var leafFoldersCount = 0;
        var totalFolders = 0;
        var topLevelFolderCount = scopedNode.Children.Count;
        var rootFolders = scopedNode.Children.Values
            .OrderByDescending(n => n.DocCount)
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .Select(n => new
            {
                path = n.Path,
                name = n.Name,
                totalDocuments = n.DocCount,
                directDocuments = n.DirectDocCount,
                subfolderCount = n.Children.Count
            })
            .ToList();
        var includesEmptyFolders = false;
        var emptyFoldersKnown = false;

        void Walk(TreeNode node)
        {
            if (!string.IsNullOrWhiteSpace(node.Path))
            {
                totalFolders++;
                var relativeDepth = Math.Max(0, node.Depth - scopedDepthBase);
                foldersByDepth[relativeDepth] = foldersByDepth.TryGetValue(relativeDepth, out var c) ? c + 1 : 1;
                if (node.Children.Count == 0)
                    leafFoldersCount++;
            }

            foreach (var child in node.Children.Values)
                Walk(child);
        }

        if (ReferenceEquals(scopedNode, root))
        {
            foreach (var child in root.Children.Values)
                Walk(child);
        }
        else
        {
            Walk(scopedNode);
        }

        var fsScope = TryResolveFolderScope(ingestOpt.Value, path);
        if (fsScope.ok)
        {
            var allFolders = GetFolderPaths(fsScope.root!, fsScope.scopeAbs!);
            if (allFolders.Count > 0 || Directory.EnumerateDirectories(fsScope.scopeAbs!, "*", SearchOption.TopDirectoryOnly).Any())
            {
                includesEmptyFolders = true;
                emptyFoldersKnown = true;
                totalFolders = allFolders.Count;
                foldersByDepth = allFolders
                    .GroupBy(x => string.IsNullOrWhiteSpace(x) ? 0 : x.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length - (string.IsNullOrWhiteSpace(fsScope.scopeRel) ? 0 : fsScope.scopeRel!.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length))
                    .ToDictionary(g => Math.Max(0, g.Key), g => g.Count());

                topLevelFolderCount = Directory.EnumerateDirectories(fsScope.scopeAbs!, "*", SearchOption.TopDirectoryOnly)
                    .Count(d => !ShouldSkipDirectoryName(Path.GetFileName(d)));

                leafFoldersCount = allFolders.Count(rel =>
                {
                    var abs = Path.GetFullPath(Path.Combine(fsScope.root!, rel.Replace('/', Path.DirectorySeparatorChar)));
                    return !Directory.EnumerateDirectories(abs, "*", SearchOption.TopDirectoryOnly)
                        .Any(d => !ShouldSkipDirectoryName(Path.GetFileName(d)));
                });

                rootFolders = Directory.EnumerateDirectories(fsScope.scopeAbs!, "*", SearchOption.TopDirectoryOnly)
                    .Where(d => !ShouldSkipDirectoryName(Path.GetFileName(d)))
                    .Select(d =>
                    {
                        var rel = Path.GetRelativePath(fsScope.root!, d).Replace('\\', '/').Trim('/');
                        var indexedNode = FindNode(root, rel);
                        var directSubfolders = Directory.EnumerateDirectories(d, "*", SearchOption.TopDirectoryOnly)
                            .Count(sd => !ShouldSkipDirectoryName(Path.GetFileName(sd)));
                        return new
                        {
                            path = rel,
                            name = Path.GetFileName(d),
                            totalDocuments = indexedNode?.DocCount ?? 0,
                            directDocuments = indexedNode?.DirectDocCount ?? 0,
                            subfolderCount = directSubfolders
                        };
                    })
                    .OrderByDescending(x => x.totalDocuments)
                    .ThenBy(x => x.name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        var documentsByDepth = new Dictionary<int, int>();
        foreach (var docPath in docPaths)
        {
            var categoryPath = GetCategoryPath(docPath);
            if (!string.IsNullOrWhiteSpace(path))
            {
                if (!(string.Equals(categoryPath, path, StringComparison.OrdinalIgnoreCase)
                      || categoryPath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
            }

            var depth = string.IsNullOrWhiteSpace(categoryPath)
                ? 0
                : categoryPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
            var relativeDepth = Math.Max(0, depth - scopedDepthBase);
            documentsByDepth[relativeDepth] = documentsByDepth.TryGetValue(relativeDepth, out var c) ? c + 1 : 1;
        }

        return BuildCachedJsonResponse(ctx, "stats", new
        {
            scopePath = scopedNode.Path,
            totalDocuments = scopedNode.DocCount,
            maxDepth = foldersByDepth.Keys.DefaultIfEmpty(0).Max(),
            totalNonEmptyFolders = totalFolders,
            topLevelFolderCount = topLevelFolderCount,
            leafFolderCount = leafFoldersCount,
            foldersByDepth = foldersByDepth
                .OrderBy(kv => kv.Key)
                .Select(kv => new { depth = kv.Key, folderCount = kv.Value })
                .ToList(),
            documentsByDepth = documentsByDepth
                .OrderBy(kv => kv.Key)
                .Select(kv => new { depth = kv.Key, documentCount = kv.Value })
                .ToList(),
            rootFolders,
            includesEmptyFolders,
            emptyFoldersKnown,
            folderTree = ToDto(scopedNode, maxDepth: null)
        });
    }


    private static async Task<IResult> ExtractionQualityAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        string? path,
        string? categoryRef,
        int? limit)
    {
        AdminAuth.EnsureAdmin(ctx);

        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;
        path = DocumentsCategoryScopeResolver.NormalizeCategoryPathOrNull(path);
        categoryRef = DocumentsCategoryScopeResolver.NormalizeCategoryRefOrNull(categoryRef);
        var lim = Math.Clamp(limit ?? 200, 1, 2000);

        await using var conn = await ds.OpenConnectionAsync(ct);
        path = await DocumentsCategoryScopeResolver.ResolveCategoryScopeAsync(conn, tenantId, path, categoryRef, ct);

        const string scopeWhere = @"
WHERE d.tenant_id=@tenant
  AND d.status='indexed'
  AND (@path IS NULL OR d.doc_path LIKE (@path || '/%'))";

        var qualityCte = $@"
WITH scoped_docs AS (
  SELECT
    d.doc_id,
    d.tenant_id,
    d.doc_path,
    d.indexed_version
  FROM documents d
  {scopeWhere}
),
latest_runs AS (
  SELECT
    sd.doc_id,
    r.payload
  FROM scoped_docs sd
  LEFT JOIN LATERAL (
    SELECT payload
    FROM document_processing_runs pr
    WHERE pr.tenant_id=sd.tenant_id
      AND pr.doc_id=sd.doc_id
      AND pr.action='upsert'
      AND pr.status='done'
    ORDER BY pr.finished_at DESC NULLS LAST, pr.started_at DESC NULLS LAST
    LIMIT 1
  ) r ON true
),
page_quality AS (
  SELECT
    sd.doc_id,
    COUNT(pi.page_number)::int AS page_count,
    COUNT(*) FILTER (WHERE pm.word_count > 0 AND COALESCE(pi.char_count, 0) > 0)::int AS text_page_count,
    COUNT(*) FILTER (WHERE pi.page_number IS NOT NULL AND (pm.word_count <= 0 OR COALESCE(pi.char_count, 0) <= 0))::int AS empty_page_count,
    COUNT(*) FILTER (
      WHERE pi.page_number IS NOT NULL
        AND pm.word_count > 0
        AND COALESCE(pi.char_count, 0) > 0
        AND (pm.word_count < 12 OR pi.char_count < 80)
    )::int AS sparse_page_count,
    COALESCE(SUM(pm.word_count), 0)::int AS total_word_count,
    COALESCE(SUM(pi.char_count), 0)::int AS total_char_count
  FROM scoped_docs sd
  LEFT JOIN document_revisions rev
    ON rev.tenant_id=sd.tenant_id
   AND rev.doc_id=sd.doc_id
   AND rev.indexed_version=sd.indexed_version
  LEFT JOIN document_page_index pi
    ON pi.tenant_id=sd.tenant_id
   AND pi.revision_id=rev.revision_id
  LEFT JOIN LATERAL (
    SELECT CASE
      WHEN COALESCE(pi.metadata ->> 'wordCount', '') ~ '^[0-9]+$' THEN (pi.metadata ->> 'wordCount')::int
      ELSE 0
    END AS word_count
  ) pm ON true
  GROUP BY sd.doc_id
),
doc_quality_base AS (
  SELECT
    sd.doc_id AS ""DocId"",
    sd.doc_path AS ""DocPath"",
    lr.payload #>> '{{extractionQuality,textStatus}}' AS ""RunTextStatus"",
    (lr.payload #>> '{{extractionQuality,ocrRecommended}}')::boolean AS ""RunOcrRecommended"",
    COALESCE((lr.payload #>> '{{extractionQuality,pageCount}}')::int, pq.page_count, 0) AS ""PageCount"",
    COALESCE((lr.payload #>> '{{extractionQuality,textPageCount}}')::int, pq.text_page_count, 0) AS ""TextPageCount"",
    COALESCE((lr.payload #>> '{{extractionQuality,emptyPageCount}}')::int, pq.empty_page_count, 0) AS ""EmptyPageCount"",
    COALESCE((lr.payload #>> '{{extractionQuality,sparsePageCount}}')::int, pq.sparse_page_count, 0) AS ""SparsePageCount"",
    COALESCE((lr.payload #>> '{{extractionQuality,totalWordCount}}')::int, pq.total_word_count, 0) AS ""TotalWordCount"",
    COALESCE((lr.payload #>> '{{extractionQuality,totalCharCount}}')::int, pq.total_char_count, 0) AS ""TotalCharCount"",
    COALESCE(
      (lr.payload #>> '{{extractionQuality,averageWordsPerPage}}')::double precision,
      ROUND((COALESCE(pq.total_word_count, 0)::numeric / GREATEST(COALESCE(pq.page_count, 0), 1)), 2)::double precision,
      0
    ) AS ""AverageWordsPerPage"",
    COALESCE(
      (lr.payload #>> '{{extractionQuality,textPageRatio}}')::double precision,
      ROUND((COALESCE(pq.text_page_count, 0)::numeric / GREATEST(COALESCE(pq.page_count, 0), 1)), 4)::double precision,
      0
    ) AS ""TextPageRatio"",
    CASE
      WHEN COALESCE(pq.page_count, 0) <= 0 THEN 'unknown'
      WHEN COALESCE(pq.total_word_count, 0) <= 0 THEN 'empty_text'
      WHEN (COALESCE(pq.empty_page_count, 0)::double precision / GREATEST(COALESCE(pq.page_count, 0), 1)) >= 0.6 THEN 'low_text'
      WHEN (COALESCE(pq.sparse_page_count, 0)::double precision / GREATEST(COALESCE(pq.page_count, 0), 1)) >= 0.6
           AND (COALESCE(pq.total_word_count, 0)::double precision / GREATEST(COALESCE(pq.page_count, 0), 1)) < 30 THEN 'low_text'
      WHEN (COALESCE(pq.total_word_count, 0)::double precision / GREATEST(COALESCE(pq.page_count, 0), 1)) < 10 THEN 'low_text'
      ELSE 'ok'
    END AS ""ComputedTextStatus"",
    lr.payload #>> '{{extractionQuality,signals}}' AS ""RunSignalsJson""
  FROM scoped_docs sd
  LEFT JOIN latest_runs lr ON lr.doc_id=sd.doc_id
  LEFT JOIN page_quality pq ON pq.doc_id=sd.doc_id
),
doc_quality AS (
  SELECT
    *,
    COALESCE(""RunTextStatus"", ""ComputedTextStatus"") AS ""TextStatus"",
    COALESCE(""RunOcrRecommended"", ""ComputedTextStatus"" IN ('empty_text', 'low_text')) AS ""OcrRecommended"",
    COALESCE(
      ""RunSignalsJson"",
      CASE ""ComputedTextStatus""
        WHEN 'empty_text' THEN '[""no_text_extracted"",""ocr_recommended""]'
        WHEN 'low_text' THEN '[""low_text_extraction"",""ocr_recommended""]'
        WHEN 'ok' THEN '[""text_extraction_ok""]'
        ELSE '[]'
      END
    ) AS ""SignalsJson""
  FROM doc_quality_base
)
";

        var summarySql = qualityCte + @"
SELECT
  COUNT(*)::int AS ""TotalDocuments"",
  COUNT(*) FILTER (WHERE ""OcrRecommended"")::int AS ""OcrRecommendedDocuments"",
  COUNT(*) FILTER (WHERE ""TextStatus""='empty_text')::int AS ""EmptyTextDocuments"",
  COUNT(*) FILTER (WHERE ""TextStatus""='low_text')::int AS ""LowTextDocuments"",
  COUNT(*) FILTER (WHERE ""TextStatus""='ok')::int AS ""OkDocuments"",
  COUNT(*) FILTER (WHERE ""TextStatus""='unknown')::int AS ""UnknownDocuments""
FROM doc_quality;";

        var itemsSql = qualityCte + @"
SELECT *
FROM doc_quality
ORDER BY
  ""OcrRecommended"" DESC,
  CASE ""TextStatus""
    WHEN 'empty_text' THEN 0
    WHEN 'low_text' THEN 1
    WHEN 'unknown' THEN 2
    ELSE 3
  END,
  ""DocPath"" ASC
LIMIT @lim;";

        var summary = await conn.QuerySingleAsync<ExtractionQualitySummaryRow>(new CommandDefinition(
            summarySql,
            new { tenant = tenantId, path },
            cancellationToken: ct));

        var rows = (await conn.QueryAsync<ExtractionQualityRow>(new CommandDefinition(
            itemsSql,
            new { tenant = tenantId, path, lim },
            cancellationToken: ct))).ToList();

        return Results.Ok(new
        {
            scopePath = path,
            summary = new
            {
                totalDocuments = summary.TotalDocuments,
                okDocuments = summary.OkDocuments,
                lowTextDocuments = summary.LowTextDocuments,
                emptyTextDocuments = summary.EmptyTextDocuments,
                unknownDocuments = summary.UnknownDocuments,
                ocrRecommendedDocuments = summary.OcrRecommendedDocuments
            },
            items = rows.Select(row => new
            {
                docId = row.DocId,
                docPath = row.DocPath,
                textStatus = row.TextStatus,
                ocrRecommended = row.OcrRecommended,
                pageCount = row.PageCount,
                textPageCount = row.TextPageCount,
                emptyPageCount = row.EmptyPageCount,
                sparsePageCount = row.SparsePageCount,
                totalWordCount = row.TotalWordCount,
                totalCharCount = row.TotalCharCount,
                averageWordsPerPage = row.AverageWordsPerPage,
                textPageRatio = row.TextPageRatio,
                signals = ParseJsonStringArray(row.SignalsJson)
            }),
            limit = lim
        });
    }

    private static async Task<IResult> SnapshotAsync(HttpContext ctx, NpgsqlDataSource ds)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        await using var conn = await ds.OpenConnectionAsync(ct);

        const string summarySql = @"
SELECT
  computed_at AS ""ComputedAt"",
  total_docs  AS ""TotalDocs""
FROM documents_catalog_summary
WHERE tenant_id=@tenant
LIMIT 1;";

        const string categoriesSql = @"
SELECT
  path          AS ""Path"",
  name          AS ""Name"",
  display_order AS ""DisplayOrder"",
  doc_count     AS ""DocCount"",
  updated_at    AS ""UpdatedAt""
FROM documents_catalog_categories
WHERE tenant_id=@tenant
ORDER BY display_order ASC, name ASC;";

        var summary = await conn.QueryFirstOrDefaultAsync<SnapshotSummaryRow>(new CommandDefinition(summarySql, new { tenant = tenantId }, cancellationToken: ct));
        var categoryRows = (await conn.QueryAsync<SnapshotCategoryRow>(new CommandDefinition(categoriesSql, new { tenant = tenantId }, cancellationToken: ct))).ToList();
        var aliasesByPath = await DocumentsCategoryScopeResolver.LoadTopCategoryAliasesAsync(conn, tenantId, categoryRows.Select(x => x.Path).ToList(), ct);

        var computedAt = summary?.ComputedAt ?? (categoryRows.Count > 0 ? categoryRows.Max(x => x.UpdatedAt) : DateTimeOffset.UtcNow);
        var snapshotId = BuildSnapshotId(computedAt, summary?.TotalDocs ?? categoryRows.Sum(x => x.DocCount));
        var etag = BuildSnapshotEtag(computedAt, summary?.TotalDocs ?? categoryRows.Sum(x => x.DocCount), categoryRows.Count);
        ctx.Response.Headers.ETag = etag;
        if (RequestIfNoneMatchMatches(ctx, etag))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        var payload = new CatalogSnapshotResponse
        {
            SnapshotId = snapshotId,
            CatalogVersion = computedAt.ToUniversalTime().ToString("O"),
            ETag = etag,
            Categories = categoryRows.Select(x => new CatalogSnapshotCategoryItem
            {
                CategoryRef = BuildCategoryRef(x.DisplayOrder),
                CategoryPath = x.Path,
                DisplayOrder = x.DisplayOrder,
                CanonicalName = x.Name,
                DocumentCount = x.DocCount,
                LastUpdatedUtc = x.UpdatedAt,
                Aliases = aliasesByPath.TryGetValue(x.Path, out var aliases) ? aliases.ToList() : new List<string>()
            }).ToList(),
            Totals = new CatalogSnapshotTotals
            {
                Documents = summary?.TotalDocs ?? categoryRows.Sum(x => (long)x.DocCount),
                Categories = categoryRows.Count
            }
        };

        return Results.Ok(payload);
    }

    private static async Task<IResult> CatalogCategoriesAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        string? path,
        string? categoryRef,
        int? pageSize,
        int? maxpagesize,
        string? cursor)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        var requestedPath = DocumentsCategoryScopeResolver.NormalizeCategoryPathOrNull(path);
        var requestedCategoryRef = DocumentsCategoryScopeResolver.NormalizeCategoryRefOrNull(categoryRef);
        var requestedPageSize = Math.Clamp(pageSize ?? maxpagesize ?? 100, 1, 500);

        CatalogCategoriesCursor? cursorState = null;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!OpaqueCursor.TryDecode<CatalogCategoriesCursor>(cursor, out cursorState) || cursorState is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "invalid_cursor", detail: "The supplied cursor is invalid.");
            }

            if (!CursorMatches(cursorState.Path, requestedPath)
                || !CursorMatches(cursorState.CategoryRef, requestedCategoryRef)
                || !CursorMatches(cursorState.PageSize, requestedPageSize))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "cursor_mismatch", detail: "The supplied cursor does not match the requested category filters.");
            }

            requestedPath = cursorState.Path;
            requestedCategoryRef = cursorState.CategoryRef;
            requestedPageSize = cursorState.PageSize;
        }

        var offset = cursorState?.Offset ?? 0;

        await using var conn = await ds.OpenConnectionAsync(ct);
        requestedPath = await DocumentsCategoryScopeResolver.ResolveCategoryScopeAsync(conn, tenantId, requestedPath, requestedCategoryRef, ct);

        List<object> value;
        var nextOffset = 0;
        var total = 0;

        if (!string.IsNullOrWhiteSpace(requestedCategoryRef) && !string.IsNullOrWhiteSpace(requestedPath))
        {
            const string exactSql = @"
SELECT
  path             AS ""Path"",
  name             AS ""Name"",
  display_order    AS ""DisplayOrder"",
  doc_count        AS ""DocCount"",
  direct_doc_count AS ""DirectDocCount"",
  subfolder_count  AS ""SubfolderCount"",
  updated_at       AS ""UpdatedAt""
FROM documents_catalog_categories
WHERE tenant_id=@tenant AND path=@path
LIMIT 1;";

            var row = await conn.QueryFirstOrDefaultAsync<SnapshotCategoryRow>(new CommandDefinition(exactSql, new { tenant = tenantId, path = requestedPath }, cancellationToken: ct));
            total = row is null ? 0 : 1;

            if (row is null || offset > 0)
            {
                value = new List<object>();
            }
            else
            {
        var aliasesByPath = await DocumentsCategoryScopeResolver.LoadTopCategoryAliasesAsync(conn, tenantId, new[] { row.Path }, ct);
                value = new List<object>
                {
                    new
                    {
                        categoryRef = BuildCategoryRef(row.DisplayOrder),
                        categoryPath = row.Path,
                        canonicalName = row.Name,
                        displayOrder = row.DisplayOrder,
                        documentCount = row.DocCount,
                        directDocumentCount = row.DirectDocCount,
                        subfolderCount = row.SubfolderCount,
                        lastUpdatedUtc = row.UpdatedAt,
                        aliases = aliasesByPath.TryGetValue(row.Path, out var aliases) ? aliases : Array.Empty<string>()
                    }
                };
            }
        }
        else if (string.IsNullOrWhiteSpace(requestedPath))
        {
            const string sql = @"
SELECT
  path             AS ""Path"",
  name             AS ""Name"",
  display_order    AS ""DisplayOrder"",
  doc_count        AS ""DocCount"",
  direct_doc_count AS ""DirectDocCount"",
  subfolder_count  AS ""SubfolderCount"",
  updated_at       AS ""UpdatedAt""
FROM documents_catalog_categories
WHERE tenant_id=@tenant
ORDER BY display_order ASC, name ASC
LIMIT @lim OFFSET @off;";

            const string totalSql = @"SELECT COUNT(*) FROM documents_catalog_categories WHERE tenant_id=@tenant;";

            var rows = (await conn.QueryAsync<SnapshotCategoryRow>(new CommandDefinition(sql, new { tenant = tenantId, lim = requestedPageSize, off = offset }, cancellationToken: ct))).ToList();
            total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(totalSql, new { tenant = tenantId }, cancellationToken: ct));
        var aliasesByPath = await DocumentsCategoryScopeResolver.LoadTopCategoryAliasesAsync(conn, tenantId, rows.Select(x => x.Path).ToList(), ct);

            value = rows.Select(x => (object)new
            {
                categoryRef = BuildCategoryRef(x.DisplayOrder),
                categoryPath = x.Path,
                canonicalName = x.Name,
                displayOrder = x.DisplayOrder,
                documentCount = x.DocCount,
                directDocumentCount = x.DirectDocCount,
                subfolderCount = x.SubfolderCount,
                lastUpdatedUtc = x.UpdatedAt,
                aliases = aliasesByPath.TryGetValue(x.Path, out var aliases) ? aliases : Array.Empty<string>()
            }).ToList();
        }
        else
        {
            const string scopedSql = @"
SELECT
  n.path             AS ""Path"",
  n.name             AS ""Name"",
  n.depth            AS ""Depth"",
  n.doc_count        AS ""DocCount"",
  n.direct_doc_count AS ""DirectDocCount"",
  COALESCE(c.child_count, 0) AS ""SubfolderCount"",
  n.updated_at       AS ""UpdatedAt""
FROM documents_category_nodes n
LEFT JOIN (
    SELECT tenant_id, parent_path, COUNT(*)::int AS child_count
    FROM documents_category_nodes
    WHERE tenant_id=@tenant
    GROUP BY tenant_id, parent_path
) c ON c.tenant_id = n.tenant_id AND c.parent_path = n.path
WHERE n.tenant_id=@tenant AND n.parent_path=@path
ORDER BY n.doc_count DESC, n.name ASC
LIMIT @lim OFFSET @off;";

            const string scopedTotalSql = @"
SELECT COUNT(*)
FROM documents_category_nodes
WHERE tenant_id=@tenant AND parent_path=@path;";

            var rows = (await conn.QueryAsync<ScopedSnapshotCategoryRow>(new CommandDefinition(scopedSql, new { tenant = tenantId, path = requestedPath, lim = requestedPageSize, off = offset }, cancellationToken: ct))).ToList();
            total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(scopedTotalSql, new { tenant = tenantId, path = requestedPath }, cancellationToken: ct));

            value = rows.Select(x => (object)new
            {
                categoryPath = x.Path,
                canonicalName = x.Name,
                depth = x.Depth,
                documentCount = x.DocCount,
                directDocumentCount = x.DirectDocCount,
                subfolderCount = x.SubfolderCount,
                lastUpdatedUtc = x.UpdatedAt
            }).ToList();
        }

        nextOffset = offset + value.Count;
        string? nextLink = null;
        if (nextOffset < total)
        {
            var nextCursor = OpaqueCursor.Encode(new CatalogCategoriesCursor
            {
                Path = requestedPath,
                CategoryRef = requestedCategoryRef,
                PageSize = requestedPageSize,
                Offset = nextOffset
            });

            nextLink = BuildAbsoluteNextLink(ctx, "/catalog/categories", new Dictionary<string, string?>
            {
                ["path"] = requestedPath,
                ["categoryRef"] = requestedCategoryRef,
                ["pageSize"] = requestedPageSize.ToString(),
                ["cursor"] = nextCursor
            });
        }

        return BuildCachedJsonResponse(ctx, "catalog-categories", new { value, nextLink });
    }

    private static async Task<IResult> CatalogDocumentsAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        string? categoryRef,
        string? q,
        DateTimeOffset? changedSince,
        string? orderby,
        int? pageSize,
        int? maxpagesize,
        string? cursor)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        var requestedCategoryRef = DocumentsCategoryScopeResolver.NormalizeCategoryRefOrNull(categoryRef);
        var requestedQuery = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        var requestedOrderBy = NormalizeCatalogOrderBy(orderby);
        var requestedPageSize = Math.Clamp(pageSize ?? maxpagesize ?? 50, 1, 200);

        CatalogDocumentsCursor? cursorState = null;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!OpaqueCursor.TryDecode<CatalogDocumentsCursor>(cursor, out cursorState) || cursorState is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "invalid_cursor", detail: "The supplied cursor is invalid.");
            }

            if (!CursorMatches(cursorState.CategoryRef, requestedCategoryRef)
                || !CursorMatches(cursorState.Query, requestedQuery)
                || !CursorMatches(cursorState.ChangedSince, changedSince)
                || !CursorMatches(cursorState.OrderBy, requestedOrderBy)
                || !CursorMatches(cursorState.PageSize, requestedPageSize))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "cursor_mismatch", detail: "The supplied cursor does not match the requested document filters.");
            }

            requestedCategoryRef = cursorState.CategoryRef;
            requestedQuery = cursorState.Query;
            changedSince = cursorState.ChangedSince;
            requestedOrderBy = cursorState.OrderBy;
            requestedPageSize = cursorState.PageSize;
        }

        var offset = cursorState?.Offset ?? 0;

        await using var conn = await ds.OpenConnectionAsync(ct);
        var resolvedCategoryPath = await DocumentsCategoryScopeResolver.ResolveCategoryScopeAsync(conn, tenantId, categoryPath: null, categoryRef: requestedCategoryRef, ct: ct);

        var orderClause = requestedOrderBy switch
        {
            "name_desc" => "d.doc_name DESC, d.doc_path DESC",
            "updatedAt_desc" => "d.updated_at DESC, d.doc_path ASC",
            "updatedAt_asc" => "d.updated_at ASC, d.doc_path ASC",
            _ => "d.doc_name ASC, d.doc_path ASC"
        };

        var sql = $@"
SELECT
  d.doc_id           AS ""DocId"",
  d.doc_path         AS ""DocPath"",
  d.doc_name         AS ""DocName"",
  d.updated_at       AS ""UpdatedAt"",
  CASE WHEN d.doc_path LIKE '%/%' THEN regexp_replace(d.doc_path, '/[^/]+$', '') ELSE '' END AS ""CategoryPath""
FROM documents d
WHERE d.tenant_id=@tenant
  AND d.status='indexed'
  AND (@categoryPath IS NULL OR d.doc_path LIKE (@categoryPath || '/%'))
  AND (@changedSince IS NULL OR d.updated_at >= @changedSince)
  AND (@q IS NULL OR (d.doc_name ILIKE ('%' || @q || '%') OR d.doc_path ILIKE ('%' || @q || '%')))
ORDER BY {orderClause}
LIMIT @lim OFFSET @off;";

        const string countSql = @"
SELECT COUNT(*)
FROM documents d
WHERE d.tenant_id=@tenant
  AND d.status='indexed'
  AND (@categoryPath IS NULL OR d.doc_path LIKE (@categoryPath || '/%'))
  AND (@changedSince IS NULL OR d.updated_at >= @changedSince)
  AND (@q IS NULL OR (d.doc_name ILIKE ('%' || @q || '%') OR d.doc_path ILIKE ('%' || @q || '%')));";

        var rows = (await conn.QueryAsync<CatalogDocumentRow>(new CommandDefinition(sql, new { tenant = tenantId, categoryPath = resolvedCategoryPath, changedSince, q = requestedQuery, lim = requestedPageSize, off = offset }, cancellationToken: ct))).ToList();
        var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(countSql, new { tenant = tenantId, categoryPath = resolvedCategoryPath, changedSince, q = requestedQuery }, cancellationToken: ct));

        var topCategories = (await conn.QueryAsync<SnapshotCategoryRow>(new CommandDefinition(
            @"SELECT path AS ""Path"", name AS ""Name"", display_order AS ""DisplayOrder"", doc_count AS ""DocCount"", direct_doc_count AS ""DirectDocCount"", subfolder_count AS ""SubfolderCount"", updated_at AS ""UpdatedAt"" FROM documents_catalog_categories WHERE tenant_id=@tenant ORDER BY display_order ASC, name ASC;",
            new { tenant = tenantId },
            cancellationToken: ct))).ToList();

        var topCategoryMap = topCategories.ToDictionary(x => x.Path, x => x, StringComparer.OrdinalIgnoreCase);

        var value = rows.Select(row =>
        {
            var topLevel = row.CategoryPath?.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            topCategoryMap.TryGetValue(topLevel ?? string.Empty, out var topCategory);
            return new CatalogDocumentItem
            {
                DocumentRef = BuildDocumentRef(row.DocId),
                DocId = row.DocId,
                DocPath = row.DocPath,
                CanonicalName = row.DocName,
                CategoryRef = topCategory is null ? null : BuildCategoryRef(topCategory.DisplayOrder),
                CategoryCanonicalName = topCategory?.Name ?? topLevel,
                CategoryPath = row.CategoryPath,
                LastModifiedUtc = row.UpdatedAt
            };
        }).ToList();

        string? nextLink = null;
        var nextOffset = offset + value.Count;
        if (nextOffset < total)
        {
            var nextCursor = OpaqueCursor.Encode(new CatalogDocumentsCursor
            {
                CategoryRef = requestedCategoryRef,
                Query = requestedQuery,
                ChangedSince = changedSince,
                OrderBy = requestedOrderBy,
                PageSize = requestedPageSize,
                Offset = nextOffset
            });

            nextLink = BuildAbsoluteNextLink(ctx, "/catalog/documents", new Dictionary<string, string?>
            {
                ["categoryRef"] = requestedCategoryRef,
                ["q"] = requestedQuery,
                ["changedSince"] = changedSince?.ToString("O"),
                ["orderby"] = requestedOrderBy,
                ["pageSize"] = requestedPageSize.ToString(),
                ["cursor"] = nextCursor
            });
        }

        return BuildCachedJsonResponse(ctx, "catalog-documents", new CatalogDocumentListResponse
        {
            Value = value,
            NextLink = nextLink
        });
    }

    private static string[] ParseJsonStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            return document.RootElement
                .EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed class CatalogCategoriesCursor
    {
        public string? Path { get; set; }
        public string? CategoryRef { get; set; }
        public int PageSize { get; set; }
        public int Offset { get; set; }
    }

    private sealed class CatalogDocumentsCursor
    {
        public string? CategoryRef { get; set; }
        public string? Query { get; set; }
        public DateTimeOffset? ChangedSince { get; set; }
        public string? OrderBy { get; set; }
        public int PageSize { get; set; }
        public int Offset { get; set; }
    }

    private sealed class SnapshotSummaryRow
    {
        public DateTimeOffset ComputedAt { get; set; }
        public long TotalDocs { get; set; }
    }

    private sealed class SnapshotCategoryRow
    {
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public int DisplayOrder { get; set; }
        public int DocCount { get; set; }
        public int DirectDocCount { get; set; }
        public int SubfolderCount { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class ScopedSnapshotCategoryRow
    {
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public int Depth { get; set; }
        public int DocCount { get; set; }
        public int DirectDocCount { get; set; }
        public int SubfolderCount { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class CatalogDocumentRow
    {
        public Guid DocId { get; set; }
        public string DocPath { get; set; } = "";
        public string DocName { get; set; } = "";
        public string CategoryPath { get; set; } = "";
        public DateTimeOffset? UpdatedAt { get; set; }
    }

    private sealed class ExtractionQualitySummaryRow
    {
        public int TotalDocuments { get; set; }
        public int OcrRecommendedDocuments { get; set; }
        public int EmptyTextDocuments { get; set; }
        public int LowTextDocuments { get; set; }
        public int OkDocuments { get; set; }
        public int UnknownDocuments { get; set; }
    }

    private sealed class ExtractionQualityRow
    {
        public Guid DocId { get; set; }
        public string DocPath { get; set; } = "";
        public string TextStatus { get; set; } = "";
        public bool OcrRecommended { get; set; }
        public int PageCount { get; set; }
        public int TextPageCount { get; set; }
        public int EmptyPageCount { get; set; }
        public int SparsePageCount { get; set; }
        public int TotalWordCount { get; set; }
        public int TotalCharCount { get; set; }
        public double AverageWordsPerPage { get; set; }
        public double TextPageRatio { get; set; }
        public string SignalsJson { get; set; } = "[]";
    }

    private sealed class SummaryRow
    {
        public DateTime ComputedAt { get; set; }
        public long TotalDocs { get; set; }
        public int MaxDepth { get; set; }
        public string NodesByDepth { get; set; } = "{}";
        public string DocsByDepth { get; set; } = "{}";
        public string DirectDocsByDepth { get; set; } = "{}";
        public string Top { get; set; } = "[]";
    }

    private sealed class TopCategoryDto
    {
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public int DisplayOrder { get; set; }
        public int DocCount { get; set; }
        public int DirectDocCount { get; set; }
        public int SubfolderCount { get; set; }
    }

    private sealed class CategoryNodeDto
    {
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public int Depth { get; set; }
        public int DocCount { get; set; }
        public int DirectDocCount { get; set; }
        public int SubfolderCount { get; set; }
    }
    private sealed class NodeRow
    {
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public string? ParentPath { get; set; }
        public int Depth { get; set; }
        public int DocCount { get; set; }
        public int DirectDocCount { get; set; }
    }
}
