using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using SAAIA.Backend.Audit;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Shared;
using SAAIA.Contracts;

namespace SAAIA.Backend.Endpoints;

public static class DocumentsEndpoints
{
    public static void Map(WebApplication app)
    {
        // Admin listing / details (legacy)
        app.MapGet("/documents", ListAsync).RequireAdminKey();
        app.MapGet("/documents/{docId:guid}", GetAsync).RequireAdminKey();

        // User-safe catalog (tenant scoped) for "liste des documents" (no chunks)
        app.MapGet("/documents/catalog", CatalogAsync);
        app.MapGet("/documents/catalog/{docId:guid}", CatalogGetAsync);

        // Inventory helpers (CDC v2.8.1)
        app.MapGet("/documents/count", CountAsync);
        app.MapGet("/documents/categories", CategoriesAsync);
        app.MapGet("/documents/tree", TreeAsync);
        app.MapGet("/documents/stats", StatsAsync);

        // Contract-aligned catalog surface (transition to snapshot + capabilities)
        app.MapGet("/catalog/snapshot", SnapshotAsync);
        app.MapGet("/catalog/categories", CatalogCategoriesAsync);
        app.MapGet("/catalog/documents", CatalogDocumentsAsync);
        app.MapGet("/catalog/stats", CatalogStatsAsync);

        // Admin/health filesystem diagnostics (kept out of nominal user inventory)
        app.MapGet("/admin/catalog/empty-folders/count", EmptyFoldersCountAsync).RequireAdminKey();
        app.MapGet("/admin/catalog/empty-folders", EmptyFoldersListAsync).RequireAdminKey();

        app.Logger.LogInformation("Mapped documents endpoints (catalog + count/tree/stats + legacy admin list + admin empty-folder diagnostics)");
    }

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
        categoryPath = NormalizeCategoryPathOrNull(categoryPath);
        categoryRef = NormalizeCategoryRefOrNull(categoryRef);
        q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();

        await using var conn = await ds.OpenConnectionAsync(ct);
        categoryPath = await ResolveCategoryScopeAsync(conn, tenantId, categoryPath, categoryRef, ct);

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
  AND (@q IS NULL OR (doc_name ILIKE ('%' || @q || '%') OR doc_path ILIKE ('%' || @q || '%')))
ORDER BY category ASC, doc_path ASC
LIMIT @lim OFFSET @off;";

        var rows = await conn.QueryAsync(sql, new { tenant = tenantId, category, categoryPath, q, lim, off });

        await AuditWriter.WriteAsync(
            conn,
            tenantId,
            actorApiKeyId,
            actorIsAdmin,
            action: "documents.catalog",
            target: null,
            payload: new { category, categoryPath, categoryRef, q, limit = lim, offset = off },
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
    // Inventory endpoints (CDC v2.8.1)
    // Snapshot-first (M1.4) with safe fallbacks.
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

        categoryPath = NormalizeCategoryPathOrNull(categoryPath);
        categoryRef = NormalizeCategoryRefOrNull(categoryRef);
        q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();

        await using var conn = await ds.OpenConnectionAsync(ct);
        categoryPath = await ResolveCategoryScopeAsync(conn, tenantId, categoryPath, categoryRef, ct);

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
        path = NormalizeCategoryPathOrNull(path);
        categoryRef = NormalizeCategoryRefOrNull(categoryRef);
        var lim = Math.Clamp(limit ?? 100, 1, 500);
        var off = Math.Max(offset ?? 0, 0);

        await using var conn = await ds.OpenConnectionAsync(ct);
        path = await ResolveCategoryScopeAsync(conn, tenantId, path, categoryRef, ct);

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
            var aliasesByPath = await LoadTopCategoryAliasesAsync(conn, tenantId, rows.Select(x => x.Path).ToList(), ct);

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

        path = NormalizeCategoryPathOrNull(path);
        categoryRef = NormalizeCategoryRefOrNull(categoryRef);
        var maxDepth = depth is null ? (int?)null : Math.Clamp(depth.Value, 0, 20);
        format = string.IsNullOrWhiteSpace(format) ? "json" : format.Trim().ToLowerInvariant();

        await using var conn = await ds.OpenConnectionAsync(ct);
        path = await ResolveCategoryScopeAsync(conn, tenantId, path, categoryRef, ct);

        // Snapshot-first
        var snapTree = await TryLoadTreeFromSnapshotAsync(conn, tenantId, path, maxDepth, ct);
        if (snapTree is not null)
        {
            if (format is "markdown" or "md")
            {
                var sb = new StringBuilder();
                RenderMarkdownTree(snapTree, sb, indent: 0, maxDepth: maxDepth);
                return Results.Ok(new { path = snapTree.Path, markdown = sb.ToString(), source = "snapshot" });
            }

            var dto = ToDto(snapTree, maxDepth);
            return Results.Ok(new { path = snapTree.Path, tree = dto, source = "snapshot" });
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

        if (format is "markdown" or "md")
        {
            var sb = new StringBuilder();
            RenderMarkdownTree(node, sb, indent: 0, maxDepth: maxDepth);
            return Results.Ok(new { path = node.Path, markdown = sb.ToString(), source = "documents" });
        }

        var dto2 = ToDto(node, maxDepth);
        return Results.Ok(new { path = node.Path, tree = dto2, source = "documents" });
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

        var scopeRel = NormalizeCategoryPathOrNull(path) ?? string.Empty;
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
        path = NormalizeCategoryPathOrNull(path);
        categoryRef = NormalizeCategoryRefOrNull(categoryRef);

        await using var conn = await ds.OpenConnectionAsync(ct);
        path = await ResolveCategoryScopeAsync(conn, tenantId, path, categoryRef, ct);

        var snapshotPayload = await TryBuildStatsFromSnapshotAsync(conn, tenantId, path, ct);
        if (snapshotPayload is not null)
            return Results.Ok(snapshotPayload);

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

        return Results.Ok(new
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
        var aliasesByPath = await LoadTopCategoryAliasesAsync(conn, tenantId, categoryRows.Select(x => x.Path).ToList(), ct);

        var computedAt = summary?.ComputedAt ?? (categoryRows.Count > 0 ? categoryRows.Max(x => x.UpdatedAt) : DateTimeOffset.UtcNow);
        var snapshotId = BuildSnapshotId(computedAt, summary?.TotalDocs ?? categoryRows.Sum(x => x.DocCount));
        var etag = BuildSnapshotEtag(computedAt, summary?.TotalDocs ?? categoryRows.Sum(x => x.DocCount), categoryRows.Count);
        ctx.Response.Headers.ETag = etag;

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

        var requestedPath = NormalizeCategoryPathOrNull(path);
        var requestedCategoryRef = NormalizeCategoryRefOrNull(categoryRef);
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
        requestedPath = await ResolveCategoryScopeAsync(conn, tenantId, requestedPath, requestedCategoryRef, ct);

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
                var aliasesByPath = await LoadTopCategoryAliasesAsync(conn, tenantId, new[] { row.Path }, ct);
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
            var aliasesByPath = await LoadTopCategoryAliasesAsync(conn, tenantId, rows.Select(x => x.Path).ToList(), ct);

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

        return Results.Ok(new { value, nextLink });
    }

    private static async Task<IResult> CatalogDocumentsAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        string? categoryRef,
        string? q,
        string? orderby,
        int? pageSize,
        int? maxpagesize,
        string? cursor)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        var requestedCategoryRef = NormalizeCategoryRefOrNull(categoryRef);
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
                || !CursorMatches(cursorState.OrderBy, requestedOrderBy)
                || !CursorMatches(cursorState.PageSize, requestedPageSize))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "cursor_mismatch", detail: "The supplied cursor does not match the requested document filters.");
            }

            requestedCategoryRef = cursorState.CategoryRef;
            requestedQuery = cursorState.Query;
            requestedOrderBy = cursorState.OrderBy;
            requestedPageSize = cursorState.PageSize;
        }

        var offset = cursorState?.Offset ?? 0;

        await using var conn = await ds.OpenConnectionAsync(ct);
        var resolvedCategoryPath = await ResolveCategoryScopeAsync(conn, tenantId, categoryPath: null, categoryRef: requestedCategoryRef, ct: ct);

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
  AND (@q IS NULL OR (d.doc_name ILIKE ('%' || @q || '%') OR d.doc_path ILIKE ('%' || @q || '%')))
ORDER BY {orderClause}
LIMIT @lim OFFSET @off;";

        const string countSql = @"
SELECT COUNT(*)
FROM documents d
WHERE d.tenant_id=@tenant
  AND d.status='indexed'
  AND (@categoryPath IS NULL OR d.doc_path LIKE (@categoryPath || '/%'))
  AND (@q IS NULL OR (d.doc_name ILIKE ('%' || @q || '%') OR d.doc_path ILIKE ('%' || @q || '%')));";

        var rows = (await conn.QueryAsync<CatalogDocumentRow>(new CommandDefinition(sql, new { tenant = tenantId, categoryPath = resolvedCategoryPath, q = requestedQuery, lim = requestedPageSize, off = offset }, cancellationToken: ct))).ToList();
        var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(countSql, new { tenant = tenantId, categoryPath = resolvedCategoryPath, q = requestedQuery }, cancellationToken: ct));

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
                OrderBy = requestedOrderBy,
                PageSize = requestedPageSize,
                Offset = nextOffset
            });

            nextLink = BuildAbsoluteNextLink(ctx, "/catalog/documents", new Dictionary<string, string?>
            {
                ["categoryRef"] = requestedCategoryRef,
                ["q"] = requestedQuery,
                ["orderby"] = requestedOrderBy,
                ["pageSize"] = requestedPageSize.ToString(),
                ["cursor"] = nextCursor
            });
        }

        return Results.Ok(new CatalogDocumentListResponse
        {
            Value = value,
            NextLink = nextLink
        });
    }

    private static Task<IResult> CatalogStatsAsync(HttpContext ctx, NpgsqlDataSource ds, IOptions<IngestionOptions> ingestOpt, string? path, string? categoryRef)
        => StatsAsync(ctx, ds, ingestOpt, path, categoryRef);

    private static string BuildCategoryRef(int displayOrder)
        => $"cat_{displayOrder:000}";

    private static string BuildDocumentRef(Guid docId)
        => $"doc_{docId:N}";

    private static string BuildSnapshotId(DateTimeOffset computedAtUtc, long totalDocs)
        => $"snap_{computedAtUtc.ToUniversalTime():yyyy-MM-dd'T'HH:mm:ss'Z'}_{Math.Abs(HashCode.Combine(computedAtUtc.UtcTicks, totalDocs)):x8}";

    private static string BuildSnapshotEtag(DateTimeOffset computedAtUtc, long totalDocs, int categoryCount)
        => $"\"cat-{computedAtUtc.UtcTicks:x}-{totalDocs:x}-{categoryCount:x}\"";

    private static string BuildAbsoluteNextLink(HttpContext ctx, string path, IReadOnlyDictionary<string, string?> query)
    {
        var request = ctx.Request;
        var builder = new UriBuilder(request.Scheme, request.Host.Host, request.Host.Port ?? -1, path);
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

    private static string NormalizeCatalogOrderBy(string? orderBy)
    {
        if (string.IsNullOrWhiteSpace(orderBy))
            return "name_asc";

        return orderBy.Trim().ToLowerInvariant() switch
        {
            "name_desc" => "name_desc",
            "updatedat_desc" => "updatedAt_desc",
            "updatedat_asc" => "updatedAt_asc",
            _ => "name_asc"
        };
    }

    private static bool CursorMatches(string? cursorValue, string? requestValue)
        => string.IsNullOrWhiteSpace(requestValue) || string.Equals(cursorValue ?? string.Empty, requestValue ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static bool CursorMatches(int cursorValue, int requestValue)
        => requestValue <= 0 || cursorValue == requestValue;

    private static async Task<object?> TryBuildStatsFromSnapshotAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string? path,
        CancellationToken ct)
    {
        var scopedNode = await TryLoadTreeFromSnapshotAsync(conn, tenantId, path, maxDepth: null, ct: ct);
        if (scopedNode is null)
            return null;

        var scopedDepthBase = string.IsNullOrWhiteSpace(scopedNode.Path)
            ? 0
            : scopedNode.Path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

        var foldersByDepth = new Dictionary<int, int>();
        var documentsByDepth = new Dictionary<int, int>();
        var leafFoldersCount = 0;
        var totalFolders = 0;
        var topLevelFolderCount = scopedNode.Children.Count;

        void AddDocumentsAtDepth(int depth, int count)
        {
            if (count <= 0)
                return;

            documentsByDepth[depth] = documentsByDepth.TryGetValue(depth, out var existing)
                ? existing + count
                : count;
        }

        void Walk(TreeNode node, bool includeCurrent)
        {
            if (includeCurrent && !string.IsNullOrWhiteSpace(node.Path))
            {
                totalFolders++;
                var relativeDepth = Math.Max(0, node.Depth - scopedDepthBase);
                foldersByDepth[relativeDepth] = foldersByDepth.TryGetValue(relativeDepth, out var existingFolders)
                    ? existingFolders + 1
                    : 1;

                if (node.Children.Count == 0)
                    leafFoldersCount++;
            }

            var documentDepth = Math.Max(0, node.Depth - scopedDepthBase);
            AddDocumentsAtDepth(documentDepth, node.DirectDocCount);

            foreach (var child in node.Children.Values)
                Walk(child, includeCurrent: true);
        }

        if (string.IsNullOrWhiteSpace(scopedNode.Path))
        {
            AddDocumentsAtDepth(0, scopedNode.DirectDocCount);
            foreach (var child in scopedNode.Children.Values)
                Walk(child, includeCurrent: true);
        }
        else
        {
            Walk(scopedNode, includeCurrent: true);
        }

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

        return new
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
            includesEmptyFolders = false,
            emptyFoldersKnown = false,
            folderTree = ToDto(scopedNode, maxDepth: null)
        };
    }

    private static bool ShouldSkipDirectoryName(string? name)
    {
        var n = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(n))
            return false;
        if (n.StartsWith(".", StringComparison.Ordinal) || n.StartsWith("~", StringComparison.Ordinal))
            return true;
        return string.Equals(n, "__macosx", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> GetFolderPaths(string root, string scopeAbs)
    {
        var result = new List<string>();

        void Walk(string absDir, bool isScopeRoot)
        {
            foreach (var child in Directory.EnumerateDirectories(absDir, "*", SearchOption.TopDirectoryOnly).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (ShouldSkipDirectoryName(Path.GetFileName(child)))
                    continue;

                var rel = Path.GetRelativePath(root, child).Replace('\\', '/').Trim('/');
                if (!string.IsNullOrWhiteSpace(rel))
                    result.Add(rel);

                Walk(child, isScopeRoot: false);
            }
        }

        Walk(scopeAbs, isScopeRoot: true);
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static JsonElement ParseJsonOrEmptyObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            using var doc = JsonDocument.Parse("{}");
            return doc.RootElement.Clone();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                return doc.RootElement.Clone();
        }
        catch { }

        using (var doc = JsonDocument.Parse("{}"))
            return doc.RootElement.Clone();
    }

    private static JsonElement ParseJsonOrEmptyArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            using var doc = JsonDocument.Parse("[]");
            return doc.RootElement.Clone();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                return doc.RootElement.Clone();
        }
        catch { }

        using (var doc = JsonDocument.Parse("[]"))
            return doc.RootElement.Clone();
    }

    // -------------------------
    // Admin listing / details (legacy)
    // -------------------------

    private static async Task<IResult> ListAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        string? category,
        string? status,
        string? q,
        int? limit,
        int? offset)
    {
        AdminAuth.EnsureAdmin(ctx);

        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        var lim = Math.Clamp(limit ?? 200, 1, 2000);
        var off = Math.Max(offset ?? 0, 0);

        category = string.IsNullOrWhiteSpace(category) ? null : category.Trim().ToLowerInvariant();
        status = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToLowerInvariant();
        q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();

        await using var conn = await ds.OpenConnectionAsync(ct);

        const string sql = @"
SELECT
  doc_id            AS ""DocId"",
  doc_path          AS ""DocPath"",
  doc_name          AS ""DocName"",
  category          AS ""Category"",
  status            AS ""Status"",
  file_size         AS ""FileSize"",
  file_mtime        AS ""FileMtime"",
  page_count        AS ""PageCount"",
  last_ingested_at  AS ""LastIngestedAt"",
  ingestion_version AS ""IngestionVersion"",
  last_seen_at      AS ""LastSeenAt"",
  missing_since     AS ""MissingSince"",
  created_at        AS ""CreatedAt"",
  updated_at        AS ""UpdatedAt""
FROM documents
WHERE tenant_id=@tenant
  AND (@category IS NULL OR category=@category)
  AND (@status IS NULL OR status=@status)
  AND (@q IS NULL OR (doc_name ILIKE ('%' || @q || '%') OR doc_path ILIKE ('%' || @q || '%')))
ORDER BY updated_at DESC
LIMIT @lim OFFSET @off;";

        var rows = await conn.QueryAsync(sql, new { tenant = tenantId, category, status, q, lim, off });
        return Results.Ok(new { items = rows, limit = lim, offset = off });
    }

    private static async Task<IResult> GetAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        Guid docId)
    {
        AdminAuth.EnsureAdmin(ctx);

        var tenantId = ctx.GetTenantId();
        var actorApiKeyId = ctx.GetApiKeyIdOrNull();
        var actorIsAdmin = ctx.IsAdmin();
        var ct = ctx.RequestAborted;

        await using var conn = await ds.OpenConnectionAsync(ct);

        const string sql = @"
SELECT
  doc_id            AS ""DocId"",
  doc_path          AS ""DocPath"",
  doc_name          AS ""DocName"",
  category          AS ""Category"",
  status            AS ""Status"",
  content_hash      AS ""ContentHash"",
  file_size         AS ""FileSize"",
  file_mtime        AS ""FileMtime"",
  mime_type         AS ""MimeType"",
  page_count        AS ""PageCount"",
  last_ingested_at  AS ""LastIngestedAt"",
  ingestion_version AS ""IngestionVersion"",
  last_seen_at      AS ""LastSeenAt"",
  missing_since     AS ""MissingSince"",
  created_at        AS ""CreatedAt"",
  updated_at        AS ""UpdatedAt""
FROM documents
WHERE tenant_id=@tenant AND doc_id=@docId
LIMIT 1;";

        var row = await conn.QueryFirstOrDefaultAsync(sql, new { tenant = tenantId, docId });
        if (row is null) return Results.NotFound();

        await AuditWriter.WriteAsync(
            conn,
            tenantId,
            actorApiKeyId,
            actorIsAdmin,
            action: "documents.get",
            target: docId.ToString(),
            payload: null,
            ip: ctx.Connection.RemoteIpAddress?.ToString(),
            ct: ct);

        return Results.Ok(row);
    }

    // -------------------------
    // Snapshot tree loader
    // -------------------------

    private static async Task<TreeNode?> TryLoadTreeFromSnapshotAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string? basePath,
        int? maxDepth,
        CancellationToken ct)
    {
        basePath = NormalizeCategoryPathOrNull(basePath);

        // Determine base depth
        var baseDepth = string.IsNullOrWhiteSpace(basePath)
            ? 0
            : basePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

        int? absMaxDepth = maxDepth is null ? null : (baseDepth + maxDepth.Value);

        const string sql = @"
SELECT
  path             AS ""Path"",
  name             AS ""Name"",
  parent_path      AS ""ParentPath"",
  depth            AS ""Depth"",
  doc_count        AS ""DocCount"",
  direct_doc_count AS ""DirectDocCount""
FROM documents_category_nodes
WHERE tenant_id=@tenant
  AND (@base IS NULL OR path=@base OR path LIKE (@base || '/%'))
  AND (@absMaxDepth IS NULL OR depth <= @absMaxDepth)
ORDER BY depth ASC, path ASC;";

        var rows = (await conn.QueryAsync<NodeRow>(new CommandDefinition(
            sql,
            new { tenant = tenantId, @base = basePath, absMaxDepth },
            cancellationToken: ct)))
            .ToList();

        if (rows.Count == 0)
            return null;

        // Ensure base node exists
        var baseKey = basePath ?? "";
        if (!rows.Any(r => string.Equals(r.Path, baseKey, StringComparison.OrdinalIgnoreCase)))
            return null;

        var map = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            map[r.Path] = new TreeNode
            {
                Name = r.Name,
                Path = r.Path,
                ParentPath = r.ParentPath,
                Depth = r.Depth,
                DocCount = r.DocCount,
                DirectDocCount = r.DirectDocCount
            };
        }

        foreach (var n in map.Values)
        {
            // Do not attach the base node to anything (it is the root of the returned subtree).
            if (string.Equals(n.Path, baseKey, StringComparison.OrdinalIgnoreCase))
                continue;

            // In the snapshot, top-level nodes may have ParentPath = NULL. We attach those to the real root ("").
            var parentKey = n.ParentPath ?? "";

            if (map.TryGetValue(parentKey, out var parent))
                parent.Children[n.Name] = n;
        }
return map[baseKey];
    }

    // -------------------------
    // Tree helpers
    // -------------------------

    private sealed class TreeNode
    {
        public string Name { get; init; } = "";
        public string Path { get; init; } = "";
        public string? ParentPath { get; init; }
        public int Depth { get; init; }
        public int DocCount { get; set; }
        public int DirectDocCount { get; set; }
        public Dictionary<string, TreeNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static TreeNode BuildTreeFromDocPaths(IEnumerable<string> docPaths)
    {
        var root = new TreeNode { Name = "", Path = "", ParentPath = null, Depth = 0 };

        foreach (var docPathRaw in docPaths)
        {
            var docPath = (docPathRaw ?? "").Replace('\\', '/').Trim();
            if (string.IsNullOrWhiteSpace(docPath)) continue;

            var catPath = GetCategoryPath(docPath);
            var segs = string.IsNullOrWhiteSpace(catPath)
                ? Array.Empty<string>()
                : catPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            root.DocCount++;

            if (segs.Length == 0)
            {
                root.DirectDocCount++;
                continue;
            }

            var cur = root;
            var accum = "";
            string? parentPath = null;
            for (var i = 0; i < segs.Length; i++)
            {
                var seg = segs[i];
                accum = string.IsNullOrWhiteSpace(accum) ? seg : (accum + "/" + seg);

                if (!cur.Children.TryGetValue(seg, out var child))
                {
                    child = new TreeNode { Name = seg, Path = accum, ParentPath = parentPath, Depth = i + 1 };
                    cur.Children[seg] = child;
                }

                child.DocCount++;

                if (i == segs.Length - 1)
                    child.DirectDocCount++;

                parentPath = child.Path;
                cur = child;
            }
        }

        return root;
    }

    private static TreeNode? FindNode(TreeNode root, string categoryPath)
    {
        categoryPath = NormalizeCategoryPathOrNull(categoryPath) ?? "";
        if (string.IsNullOrWhiteSpace(categoryPath)) return root;

        var segs = categoryPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cur = root;
        foreach (var s in segs)
        {
            if (!cur.Children.TryGetValue(s, out var child)) return null;
            cur = child;
        }
        return cur;
    }

    private static object ToDto(TreeNode node, int? maxDepth)
    {
        // maxDepth is relative to the provided node.
        if (maxDepth is not null && maxDepth.Value == 0)
            return new { name = node.Name, path = node.Path, docCount = node.DocCount, directDocCount = node.DirectDocCount, children = Array.Empty<object>() };

        var nextDepth = maxDepth is null ? (int?)null : Math.Max(maxDepth.Value - 1, 0);

        var children = node.Children.Values
            .OrderByDescending(n => n.DocCount)
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ch => ToDto(ch, nextDepth))
            .ToList();

        return new { name = node.Name, path = node.Path, docCount = node.DocCount, directDocCount = node.DirectDocCount, children };
    }

    private static void RenderMarkdownTree(TreeNode node, StringBuilder sb, int indent, int? maxDepth)
    {
        var prefix = new string(' ', indent * 2);
        if (!string.IsNullOrWhiteSpace(node.Path))
            sb.AppendLine($"{prefix}- {node.Name} ({node.DocCount})");

        if (maxDepth is not null && maxDepth.Value == 0)
            return;

        var nextDepth = maxDepth is null ? (int?)null : Math.Max(maxDepth.Value - 1, 0);

        foreach (var child in node.Children.Values
                     .OrderByDescending(n => n.DocCount)
                     .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
        {
            RenderMarkdownTree(child, sb, indent + (string.IsNullOrWhiteSpace(node.Path) ? 0 : 1), nextDepth);
        }
    }

    private static string GetCategoryPath(string docPath)
    {
        docPath = (docPath ?? "").Replace('\\', '/').Trim();
        if (string.IsNullOrWhiteSpace(docPath)) return "";

        var lastSlash = docPath.LastIndexOf('/');
        if (lastSlash <= 0) return "";

        return docPath.Substring(0, lastSlash);
    }

    private static string? NormalizeCategoryPathOrNull(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().Replace('\\', '/');
        s = s.Trim('/');
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static string? NormalizeCategoryRefOrNull(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static string NormalizeCategoryComparableText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var normalized = raw.Trim().Normalize(System.Text.NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == System.Globalization.UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(ch))
                sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }

    private static bool IsLooseCategoryComparableMatch(string probe, string candidate)
    {
        if (string.IsNullOrWhiteSpace(probe) || string.IsNullOrWhiteSpace(candidate))
            return false;

        if (string.Equals(probe, candidate, StringComparison.Ordinal))
            return true;

        if (probe.Length >= 5 && candidate.Length >= 5
            && (probe.StartsWith(candidate, StringComparison.Ordinal) || candidate.StartsWith(probe, StringComparison.Ordinal)))
        {
            return true;
        }

        var sharedPrefix = 0;
        var max = Math.Min(probe.Length, candidate.Length);
        while (sharedPrefix < max && probe[sharedPrefix] == candidate[sharedPrefix])
            sharedPrefix++;

        return sharedPrefix >= 7;
    }

    private static async Task<Dictionary<string, string[]>> LoadTopCategoryAliasesAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        IReadOnlyCollection<string> paths,
        CancellationToken ct)
    {
        if (paths is null || paths.Count == 0)
            return new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        var rows = (await conn.QueryAsync<CategoryAliasDto>(new CommandDefinition(
            @"SELECT
  path      AS ""Path"",
  alias     AS ""Alias"",
  priority  AS ""Priority""
FROM documents_catalog_category_aliases
WHERE tenant_id=@tenant
  AND path = ANY(@paths)
  AND source='builtin'
ORDER BY path ASC, priority ASC, alias ASC;",
            new { tenant = tenantId, paths = paths.ToArray() },
            cancellationToken: ct))).ToList();

        return rows
            .GroupBy(x => x.Path ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.Alias)
                      .Where(x => !string.IsNullOrWhiteSpace(x))
                      .Distinct(StringComparer.OrdinalIgnoreCase)
                      .ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<string?> ResolveCategoryScopeAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string? categoryPath,
        string? categoryRef,
        CancellationToken ct)
    {
        var normalizedPath = NormalizeCategoryPathOrNull(categoryPath);
        if (!string.IsNullOrWhiteSpace(normalizedPath))
        {
            var exactPath = await ResolveExistingCategoryPathAsync(conn, tenantId, normalizedPath!, ct);
            if (!string.IsNullOrWhiteSpace(exactPath))
                return exactPath;

            return await ResolveCategoryRefToPathAsync(conn, tenantId, categoryRef ?? normalizedPath!, ct);
        }

        if (string.IsNullOrWhiteSpace(categoryRef))
            return null;

        return await ResolveCategoryRefToPathAsync(conn, tenantId, categoryRef!, ct);
    }

    private static async Task<string?> ResolveExistingCategoryPathAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string categoryPath,
        CancellationToken ct)
    {
        var normalizedPath = NormalizeCategoryPathOrNull(categoryPath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
            return null;

        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            @"SELECT path
FROM documents_category_nodes
WHERE tenant_id=@tenant AND path=@path
LIMIT 1;",
            new { tenant = tenantId, path = normalizedPath },
            cancellationToken: ct));
    }

    private static async Task<string?> ResolveCategoryRefToPathAsync(
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
                @"SELECT path
FROM documents_category_nodes
WHERE tenant_id=@tenant AND path=@path
LIMIT 1;",
                new { tenant = tenantId, path = normalizedPath },
                cancellationToken: ct));
            if (!string.IsNullOrWhiteSpace(exactPath))
                return exactPath;
        }

        var ordinal = TryParseOrdinalCategoryRef(raw);
        if (ordinal is not null)
        {
            var byOrder = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                @"SELECT path
FROM documents_catalog_categories
WHERE tenant_id=@tenant AND display_order=@displayOrder
LIMIT 1;",
                new { tenant = tenantId, displayOrder = ordinal.Value },
                cancellationToken: ct));
            if (!string.IsNullOrWhiteSpace(byOrder))
                return byOrder;
        }

        var byName = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            @"SELECT path
FROM documents_catalog_categories
WHERE tenant_id=@tenant
  AND (LOWER(path)=LOWER(@value) OR LOWER(name)=LOWER(@value))
ORDER BY display_order ASC, name ASC
LIMIT 1;",
            new { tenant = tenantId, value = raw.Trim() },
            cancellationToken: ct));
        if (!string.IsNullOrWhiteSpace(byName))
            return byName;

        var probe = NormalizeCategoryComparableText(raw);
        if (!string.IsNullOrWhiteSpace(probe))
        {
            var candidates = (await conn.QueryAsync<TopCategoryDto>(new CommandDefinition(
                @"SELECT
  path             AS ""Path"",
  name             AS ""Name"",
  display_order    AS ""DisplayOrder"",
  doc_count        AS ""DocCount"",
  direct_doc_count AS ""DirectDocCount"",
  subfolder_count  AS ""SubfolderCount""
FROM documents_catalog_categories
WHERE tenant_id=@tenant
ORDER BY display_order ASC, name ASC;",
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


        var match = System.Text.RegularExpressions.Regex.Match(
            trimmed,
            @"(?:^|\b)(?:category|categorie|catégorie|cat|rang)\s*(\d+)(?:er|eme|ème|nd|rd|th)?(?:\b|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var parsed) && parsed > 0)
            return parsed;

        match = System.Text.RegularExpressions.Regex.Match(
            trimmed,
            @"^(\d+)(?:er|eme|ème|nd|rd|th)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (match.Success && int.TryParse(match.Groups[1].Value, out parsed) && parsed > 0)
            return parsed;

        return null;
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

    private sealed class CategoryAliasDto
    {
        public string Path { get; set; } = "";
        public string Alias { get; set; } = "";
        public int Priority { get; set; }
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
