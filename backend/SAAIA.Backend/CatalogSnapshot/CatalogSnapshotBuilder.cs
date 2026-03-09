using Microsoft.Extensions.Logging;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace SAAIA.Backend.CatalogSnapshot;

public static class CatalogSnapshotBuilder
{
    /// <summary>
    /// Build the snapshot for all active tenants.
    /// </summary>
    public static async Task<BuildAllResult> BuildAllActiveTenantsAsync(
        NpgsqlDataSource ds,
        CatalogSnapshotOptions opt,
        ILogger logger,
        CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);

        var tenantIds = (await conn.QueryAsync<Guid>(new CommandDefinition(
            "SELECT tenant_id FROM tenants WHERE is_active=true ORDER BY created_at ASC;",
            cancellationToken: ct))).ToList();

        var results = new List<BuildTenantResult>();
        foreach (var tenantId in tenantIds)
        {
            var r = await BuildTenantAsync(ds, tenantId, opt, logger, ct);
            results.Add(r);
        }

        return new BuildAllResult(results);
    }

    /// <summary>
    /// Build the snapshot for a single tenant.
    /// Uses a Postgres advisory lock to avoid concurrent rebuilds.
    /// </summary>
    public static async Task<BuildTenantResult> BuildTenantAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        CatalogSnapshotOptions opt,
        ILogger logger,
        CancellationToken ct)
    {
        var lockId = ToAdvisoryLockId(tenantId);

        await using var conn = await ds.OpenConnectionAsync(ct);

        // Try to lock (non-blocking)
        var locked = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT pg_try_advisory_lock(@id);",
            new { id = lockId },
            cancellationToken: ct));

        if (!locked)
            return new BuildTenantResult(tenantId, Success: false, Message: "locked_by_other_instance");

        try
        {
            var now = DateTime.UtcNow;

            // 1) Load doc paths
            const string loadSql = @"
SELECT doc_path AS ""DocPath""
FROM documents
WHERE tenant_id=@tenant AND status='indexed'
ORDER BY doc_path ASC
LIMIT @lim;";

            var docPaths = (await conn.QueryAsync<string>(new CommandDefinition(
                loadSql,
                new { tenant = tenantId, lim = Math.Clamp(opt.MaxDocsPerTenant, 1, 5_000_000) },
                cancellationToken: ct)))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Replace('\\', '/').Trim())
                .ToList();

            // 2) Build tree
            var root = BuildTree(docPaths);

            // 3) Aggregate stats
            var nodesByDepth = new Dictionary<int, int>();
            var docsByDepth = new Dictionary<int, int>();
            var directDocsByDepth = new Dictionary<int, int>();

            var allNodes = new List<TreeNode>();
            Walk(root, allNodes);

            var maxDepth = allNodes.Count == 0 ? 0 : allNodes.Max(n => n.Depth);

            foreach (var n in allNodes)
            {
                if (n.Depth == 0) continue; // skip root

                nodesByDepth[n.Depth] = nodesByDepth.TryGetValue(n.Depth, out var c) ? c + 1 : 1;
                docsByDepth[n.Depth] = docsByDepth.TryGetValue(n.Depth, out var d) ? d + n.DocCount : n.DocCount;
                directDocsByDepth[n.Depth] = directDocsByDepth.TryGetValue(n.Depth, out var dd) ? dd + n.DirectDocCount : n.DirectDocCount;
            }

            var top = root.Children.Values
                .OrderByDescending(n => n.DocCount)
                .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .Select(n => new { path = n.Path, name = n.Name, docCount = n.DocCount, directDocCount = n.DirectDocCount })
                .Take(100)
                .ToList();

            // 4) Persist snapshot (transaction)
            await using var tx = await conn.BeginTransactionAsync(ct);

            // Replace nodes
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM documents_category_nodes WHERE tenant_id=@tenant;",
                new { tenant = tenantId },
                transaction: tx,
                cancellationToken: ct));

            const string insertNodeSql = @"
INSERT INTO documents_category_nodes(
  tenant_id, path, name, parent_path, depth, doc_count, direct_doc_count, updated_at
) VALUES (
  @TenantId, @Path, @Name, @ParentPath, @Depth, @DocCount, @DirectDocCount, @UpdatedAt
);";

            var nodeRows = allNodes
                .Select(n => new NodeRow
                {
                    TenantId = tenantId,
                    Path = n.Path,
                    Name = n.Name,
                    ParentPath = n.ParentPath,
                    Depth = n.Depth,
                    DocCount = n.DocCount,
                    DirectDocCount = n.DirectDocCount,
                    UpdatedAt = now
                })
                .ToList();

            // Batch inserts to keep command sizes reasonable
            const int batchSize = 2000;
            for (var i = 0; i < nodeRows.Count; i += batchSize)
            {
                var batch = nodeRows.Skip(i).Take(batchSize).ToList();
                await conn.ExecuteAsync(new CommandDefinition(
                    insertNodeSql,
                    batch,
                    transaction: tx,
                    cancellationToken: ct));
            }

            // Upsert summary
            var nodesByDepthJson = JsonSerializer.Serialize(nodesByDepth);
            var docsByDepthJson = JsonSerializer.Serialize(docsByDepth);
            var directDocsByDepthJson = JsonSerializer.Serialize(directDocsByDepth);
            var topJson = JsonSerializer.Serialize(top);

            const string upsertSummarySql = @"
INSERT INTO documents_catalog_summary(
  tenant_id, computed_at, total_docs, max_depth,
  nodes_by_depth, docs_by_depth, direct_docs_by_depth, top
) VALUES (
  @TenantId, @ComputedAt, @TotalDocs, @MaxDepth,
  @NodesByDepth::jsonb, @DocsByDepth::jsonb, @DirectDocsByDepth::jsonb, @Top::jsonb
)
ON CONFLICT (tenant_id) DO UPDATE SET
  computed_at=EXCLUDED.computed_at,
  total_docs=EXCLUDED.total_docs,
  max_depth=EXCLUDED.max_depth,
  nodes_by_depth=EXCLUDED.nodes_by_depth,
  docs_by_depth=EXCLUDED.docs_by_depth,
  direct_docs_by_depth=EXCLUDED.direct_docs_by_depth,
  top=EXCLUDED.top;";

            await conn.ExecuteAsync(new CommandDefinition(
                upsertSummarySql,
                new
                {
                    TenantId = tenantId,
                    ComputedAt = now,
                    TotalDocs = root.DocCount,
                    MaxDepth = maxDepth,
                    NodesByDepth = nodesByDepthJson,
                    DocsByDepth = docsByDepthJson,
                    DirectDocsByDepth = directDocsByDepthJson,
                    Top = topJson
                },
                transaction: tx,
                cancellationToken: ct));

            await tx.CommitAsync(ct);

            logger.LogInformation("Catalog snapshot built: tenant={TenantId} docs={Docs} nodes={Nodes} maxDepth={MaxDepth}", tenantId, root.DocCount, nodeRows.Count, maxDepth);

            return new BuildTenantResult(tenantId, Success: true, Message: "ok", Docs: root.DocCount, Nodes: nodeRows.Count, ComputedAtUtc: now);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Catalog snapshot build failed for tenant {TenantId}", tenantId);
            return new BuildTenantResult(tenantId, Success: false, Message: "error:" + ex.GetType().Name);
        }
        finally
        {
            try
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "SELECT pg_advisory_unlock(@id);",
                    new { id = lockId },
                    cancellationToken: ct));
            }
            catch
            {
                // ignore
            }
        }
    }

    // -------------------------
    // Tree build helpers
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

    private static TreeNode BuildTree(IEnumerable<string> docPaths)
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
            string? parent = null;

            for (var i = 0; i < segs.Length; i++)
            {
                var seg = segs[i];
                accum = string.IsNullOrWhiteSpace(accum) ? seg : (accum + "/" + seg);

                if (!cur.Children.TryGetValue(seg, out var child))
                {
                    child = new TreeNode
                    {
                        Name = seg,
                        Path = accum,
                        ParentPath = parent,
                        Depth = i + 1
                    };
                    cur.Children[seg] = child;
                }

                child.DocCount++;

                if (i == segs.Length - 1)
                    child.DirectDocCount++;

                parent = child.Path;
                cur = child;
            }
        }

        return root;
    }

    private static void Walk(TreeNode root, List<TreeNode> nodes)
    {
        nodes.Add(root);
        foreach (var c in root.Children.Values)
            Walk(c, nodes);
    }

    private static string GetCategoryPath(string docPath)
    {
        docPath = (docPath ?? "").Replace('\\', '/').Trim();
        if (string.IsNullOrWhiteSpace(docPath)) return "";

        var lastSlash = docPath.LastIndexOf('/');
        if (lastSlash <= 0) return "";

        return docPath.Substring(0, lastSlash);
    }

    private static long ToAdvisoryLockId(Guid tenantId)
    {
        // Stable, deterministic 64-bit value from GUID bytes.
        var bytes = tenantId.ToByteArray();
        return BitConverter.ToInt64(bytes, 0);
    }

    private sealed class NodeRow
    {
        public Guid TenantId { get; set; }
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public string? ParentPath { get; set; }
        public int Depth { get; set; }
        public int DocCount { get; set; }
        public int DirectDocCount { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}

public sealed record BuildTenantResult(Guid TenantId, bool Success, string Message, int Docs = 0, int Nodes = 0, DateTime? ComputedAtUtc = null);
public sealed record BuildAllResult(IReadOnlyList<BuildTenantResult> Tenants);
