using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;

namespace SAAIA.Backend.Endpoints;

internal static class SummaryCategoryScopeResolver
{
    public static string? NormalizeCategoryPathOrNull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var normalized = path.Trim().Replace('\\', '/').Trim().Trim('/');
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    public static string? NormalizeCategoryRefOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    public static async Task<string?> ResolveScopeAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string? categoryPath,
        string? categoryRef,
        CancellationToken ct)
    {
        var normalizedPath = NormalizeCategoryPathOrNull(categoryPath);
        if (!string.IsNullOrWhiteSpace(normalizedPath))
        {
            var exactPath = await ResolveExistingPathAsync(conn, tenantId, normalizedPath!, ct);
            if (!string.IsNullOrWhiteSpace(exactPath))
                return exactPath;

            return await ResolveCategoryRefToPathAsync(conn, tenantId, categoryRef ?? normalizedPath!, ct);
        }

        if (string.IsNullOrWhiteSpace(categoryRef))
            return null;

        return await ResolveCategoryRefToPathAsync(conn, tenantId, categoryRef!, ct);
    }

    private static async Task<string?> ResolveExistingPathAsync(
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

    private sealed class TopCategorySnapshotRow
    {
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public int DisplayOrder { get; set; }
    }
}
