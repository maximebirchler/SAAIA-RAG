using System.Text;
using Dapper;
using Npgsql;

namespace SAAIA.Backend.Endpoints;

public static partial class DocumentsEndpoints
{
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

    private static async Task<TreeNode?> TryLoadTreeFromSnapshotAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string? basePath,
        int? maxDepth,
        CancellationToken ct)
    {
        basePath = DocumentsCategoryScopeResolver.NormalizeCategoryPathOrNull(basePath);

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
            if (string.Equals(n.Path, baseKey, StringComparison.OrdinalIgnoreCase))
                continue;

            var parentKey = n.ParentPath ?? "";

            if (map.TryGetValue(parentKey, out var parent))
                parent.Children[n.Name] = n;
        }

        return map[baseKey];
    }

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
        categoryPath = DocumentsCategoryScopeResolver.NormalizeCategoryPathOrNull(categoryPath) ?? "";
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
}
