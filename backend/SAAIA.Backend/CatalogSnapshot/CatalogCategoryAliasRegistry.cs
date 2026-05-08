using System.Globalization;
using System.Text;

namespace SAAIA.Backend.CatalogSnapshot;

public static class CatalogCategoryAliasRegistry
{
    public sealed record CategoryAliasDefinition(string Alias, string AliasKey, string? Language, string Source, int Priority);

    public static IReadOnlyList<CategoryAliasDefinition> BuildAliases(string path, string name)
    {
        var result = new List<CategoryAliasDefinition>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        static string FirstSegment(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        }

        void Add(string? alias, string? language, string source, int priority)
        {
            if (string.IsNullOrWhiteSpace(alias)) return;
            var trimmed = alias.Trim();
            var key = NormalizeComparable(trimmed);
            if (string.IsNullOrWhiteSpace(key)) return;
            if (!seen.Add(key)) return;
            result.Add(new CategoryAliasDefinition(trimmed, key, language, source, priority));
        }

        var topLevel = FirstSegment(path);
        Add(path, null, "canonical.path", 0);
        Add(name, null, "canonical.name", 0);
        Add(topLevel, null, "canonical.top", 0);

        return result
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.Alias, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string NormalizeComparable(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var normalized = raw.Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(ch))
                sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }
}
