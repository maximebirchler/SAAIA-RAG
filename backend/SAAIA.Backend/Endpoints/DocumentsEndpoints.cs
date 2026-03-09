using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using SAAIA.Backend.Audit;
using SAAIA.Backend.Auth;

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
        app.MapGet("/documents/tree", TreeAsync);
        app.MapGet("/documents/stats", StatsAsync);

        app.Logger.LogInformation("Mapped documents endpoints (catalog + count/tree/stats + legacy admin list)");
    }

    // -------------------------
    // User catalog (indexed only)
    // -------------------------

    private static async Task<IResult> CatalogAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        string? category,
        string? categoryPath,
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
        q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();

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
WHERE tenant_id=@tenant
  AND status='indexed'
  AND (@category IS NULL OR category=@category)
  AND (@categoryPath IS NULL OR doc_path LIKE (@categoryPath || '/%'))
  AND (@q IS NULL OR (doc_name ILIKE ('%' || @q || '%') OR doc_path ILIKE ('%' || @q || '%')))
ORDER BY category ASC, doc_name ASC
LIMIT @lim OFFSET @off;";

        var rows = await conn.QueryAsync(sql, new { tenant = tenantId, category, categoryPath, q, lim, off });

        await AuditWriter.WriteAsync(
            conn,
            tenantId,
            actorApiKeyId,
            actorIsAdmin,
            action: "documents.catalog",
            target: null,
            payload: new { category, categoryPath, q, limit = lim, offset = off },
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
        string? q)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        categoryPath = NormalizeCategoryPathOrNull(categoryPath);
        q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();

        await using var conn = await ds.OpenConnectionAsync(ct);

        // If q is provided, we must hit the documents table.
        if (!string.IsNullOrWhiteSpace(q))
        {
            const string sql = @"
SELECT COUNT(*)
FROM documents
WHERE tenant_id=@tenant
  AND status='indexed'
  AND (@categoryPath IS NULL OR doc_path LIKE (@categoryPath || '/%'))
  AND (doc_name ILIKE ('%' || @q || '%') OR doc_path ILIKE ('%' || @q || '%'));";

            var total = await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, new { tenant = tenantId, categoryPath, q }, cancellationToken: ct));
            return Results.Ok(new { total, source = "documents" });
        }

        // CategoryPath-only: use snapshot node count.
        if (!string.IsNullOrWhiteSpace(categoryPath))
        {
            const string nodeSql = @"
SELECT doc_count
FROM documents_category_nodes
WHERE tenant_id=@tenant AND path=@path
LIMIT 1;";

            var docCount = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(nodeSql, new { tenant = tenantId, path = categoryPath }, cancellationToken: ct));
            if (docCount is not null)
                return Results.Ok(new { total = docCount.Value, source = "snapshot" });

            // Fallback
            const string fallback = @"
SELECT COUNT(*)
FROM documents
WHERE tenant_id=@tenant AND status='indexed'
  AND doc_path LIKE (@path || '/%');";

            var totalFallback = await conn.ExecuteScalarAsync<long>(new CommandDefinition(fallback, new { tenant = tenantId, path = categoryPath }, cancellationToken: ct));
            return Results.Ok(new { total = totalFallback, source = "documents" });
        }

        // Global: use snapshot summary.
        const string summarySql = @"
SELECT total_docs
FROM documents_catalog_summary
WHERE tenant_id=@tenant
LIMIT 1;";

        var totalDocs = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(summarySql, new { tenant = tenantId }, cancellationToken: ct));
        if (totalDocs is not null)
            return Results.Ok(new { total = totalDocs.Value, source = "snapshot" });

        // Fallback
        const string countSql = @"SELECT COUNT(*) FROM documents WHERE tenant_id=@tenant AND status='indexed';";
        var total2 = await conn.ExecuteScalarAsync<long>(new CommandDefinition(countSql, new { tenant = tenantId }, cancellationToken: ct));
        return Results.Ok(new { total = total2, source = "documents" });
    }

    private static async Task<IResult> TreeAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        string? path,
        int? depth,
        string? format)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        path = NormalizeCategoryPathOrNull(path);
        var maxDepth = depth is null ? (int?)null : Math.Clamp(depth.Value, 0, 20);
        format = string.IsNullOrWhiteSpace(format) ? "json" : format.Trim().ToLowerInvariant();

        await using var conn = await ds.OpenConnectionAsync(ct);

        // Snapshot-first
        var snapTree = await TryLoadTreeFromSnapshotAsync(conn, tenantId, path, maxDepth, ct);
        if (snapTree is not null)
        {
            if (format is "markdown" or "md")
            {
                var sb = new StringBuilder();
                RenderMarkdownTree(snapTree, sb, indent: 0, maxDepth);
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
            RenderMarkdownTree(node, sb, indent: 0, maxDepth);
            return Results.Ok(new { path = node.Path, markdown = sb.ToString(), source = "documents" });
        }

        var dto2 = ToDto(node, maxDepth);
        return Results.Ok(new { path = node.Path, tree = dto2, source = "documents" });
    }

    private static async Task<IResult> StatsAsync(HttpContext ctx, NpgsqlDataSource ds)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        await using var conn = await ds.OpenConnectionAsync(ct);

        // Snapshot-first
        const string summarySql = @"
SELECT
  computed_at            AS ""ComputedAt"",
  total_docs             AS ""TotalDocs"",
  max_depth              AS ""MaxDepth"",
  nodes_by_depth::text   AS ""NodesByDepth"",
  docs_by_depth::text    AS ""DocsByDepth"",
  direct_docs_by_depth::text AS ""DirectDocsByDepth"",
  top::text              AS ""Top""
FROM documents_catalog_summary
WHERE tenant_id=@tenant
LIMIT 1;";

        var summary = await conn.QueryFirstOrDefaultAsync<SummaryRow>(new CommandDefinition(summarySql, new { tenant = tenantId }, cancellationToken: ct));
        if (summary is not null)
        {
            var nodesByDepth = ParseJsonOrEmptyObject(summary.NodesByDepth);
            var docsByDepth = ParseJsonOrEmptyObject(summary.DocsByDepth);
            var directDocsByDepth = ParseJsonOrEmptyObject(summary.DirectDocsByDepth);
            var top = ParseJsonOrEmptyArray(summary.Top);

            return Results.Ok(new
            {
                computedAtUtc = summary.ComputedAt,
                totalDocs = summary.TotalDocs,
                maxDepth = summary.MaxDepth,
                nodesByDepth,
                docsByDepth,
                directDocsByDepth,
                top,
                source = "snapshot"
            });
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

        var nodesByDepth2 = new Dictionary<int, int>();
        var docsByDepth2 = new Dictionary<int, int>();
        var directDocsByDepth2 = new Dictionary<int, int>();

        var top2 = root.Children.Values
            .OrderByDescending(n => n.DocCount)
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .Select(n => new { path = n.Path, name = n.Name, docCount = n.DocCount, directDocCount = n.DirectDocCount })
            .Take(50)
            .ToList();

        Walk(root, depth: 0);

        return Results.Ok(new
        {
            totalDocs = root.DocCount,
            maxDepth = nodesByDepth2.Keys.DefaultIfEmpty(0).Max(),
            nodesByDepth = nodesByDepth2,
            docsByDepth = docsByDepth2,
            directDocsByDepth = directDocsByDepth2,
            top = top2,
            source = "documents"
        });

        void Walk(TreeNode n, int depth)
        {
            if (depth > 0)
            {
                nodesByDepth2[depth] = nodesByDepth2.TryGetValue(depth, out var c) ? c + 1 : 1;
                docsByDepth2[depth] = docsByDepth2.TryGetValue(depth, out var d) ? d + n.DocCount : n.DocCount;
                directDocsByDepth2[depth] = directDocsByDepth2.TryGetValue(depth, out var dd) ? dd + n.DirectDocCount : n.DirectDocCount;
            }

            foreach (var child in n.Children.Values)
                Walk(child, depth + 1);
        }
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
        {
            var direct = node.DirectDocCount > 0 ? $" (direct {node.DirectDocCount})" : "";
            sb.AppendLine($"{prefix}- {node.Name} ({node.DocCount}){direct}");
        }

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
