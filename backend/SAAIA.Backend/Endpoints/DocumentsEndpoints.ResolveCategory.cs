using Dapper;
using Npgsql;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Shared;
using SAAIA.Contracts;

namespace SAAIA.Backend.Endpoints;

public static partial class DocumentsEndpoints
{
    private static async Task<IResult> ResolveCategoryAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        ResolveCategoryRequest request)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;
        var path = DocumentsCategoryScopeResolver.NormalizeCategoryPathOrNull(request.Path ?? request.CategoryPath);
        var categoryRef = DocumentsCategoryScopeResolver.NormalizeCategoryRefOrNull(request.CategoryRef);

        await using var conn = await ds.OpenConnectionAsync(ct);
        var resolvedPath = await DocumentsCategoryScopeResolver.ResolveCategoryScopeAsync(conn, tenantId, path, categoryRef, ct);
        if (string.IsNullOrWhiteSpace(resolvedPath))
            return Results.Ok(new ResolveCategoryResponse());

        const string topSql = @"
SELECT
  path          AS ""Path"",
  name          AS ""Name"",
  display_order AS ""DisplayOrder"",
  doc_count     AS ""DocCount""
FROM documents_catalog_categories
WHERE tenant_id=@tenant AND path=@path
LIMIT 1;";

        const string nodeSql = @"
SELECT
  path AS ""Path"",
  name AS ""Name""
FROM documents_category_nodes
WHERE tenant_id=@tenant AND path=@path
LIMIT 1;";

        const string aliasesSql = @"
SELECT alias
FROM documents_catalog_category_aliases
WHERE tenant_id=@tenant AND path=@path
ORDER BY priority ASC, alias ASC;";

        var top = await conn.QueryFirstOrDefaultAsync<TopCategoryRow>(new CommandDefinition(
            topSql,
            new { tenant = tenantId, path = resolvedPath },
            cancellationToken: ct));

        var node = top is null
            ? await conn.QueryFirstOrDefaultAsync<CategoryNodeRow>(new CommandDefinition(
                nodeSql,
                new { tenant = tenantId, path = resolvedPath },
                cancellationToken: ct))
            : null;

        if (top is null && node is null)
            return Results.Ok(new ResolveCategoryResponse());

        var aliases = (await conn.QueryAsync<string>(new CommandDefinition(
            aliasesSql,
            new { tenant = tenantId, path = resolvedPath },
            cancellationToken: ct)))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var item = new ResolvedCategoryItem
        {
            CategoryRef = top is not null ? BuildCategoryRef(top.DisplayOrder) : resolvedPath,
            CategoryPath = resolvedPath,
            DisplayName = top?.Name ?? node?.Name ?? resolvedPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? resolvedPath,
            Ordinal = top?.DisplayOrder,
            TotalDocuments = top?.DocCount,
            Aliases = aliases
        };

        return Results.Ok(new ResolveCategoryResponse
        {
            Items = new List<ResolvedCategoryItem> { item }
        });
    }

    private sealed class TopCategoryRow
    {
        public string Path { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int DisplayOrder { get; set; }
        public int DocCount { get; set; }
    }

    private sealed class CategoryNodeRow
    {
        public string Path { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }
}
