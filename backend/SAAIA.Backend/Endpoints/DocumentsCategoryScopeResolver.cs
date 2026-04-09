using System.Text;
using Dapper;
using Npgsql;

namespace SAAIA.Backend.Endpoints;

internal static class DocumentsCategoryScopeResolver
{
    public static string? NormalizeCategoryPathOrNull(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var normalized = raw.Trim().Replace('\\', '/').Trim('/');
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    public static string? NormalizeCategoryRefOrNull(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var normalized = raw.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    public static async Task<Dictionary<string, string[]>> LoadTopCategoryAliasesAsync(
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
                      .Select(x => x!)
                      .Distinct(StringComparer.OrdinalIgnoreCase)
                      .ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    public static async Task<string?> ResolveCategoryScopeAsync(
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

        return null;
    }

    private static string NormalizeCategoryComparableText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var normalized = raw.Trim().Normalize(NormalizationForm.FormD);
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

    private sealed class CategoryAliasDto
    {
        public string? Path { get; set; }
        public string? Alias { get; set; }
        public int Priority { get; set; }
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
}
