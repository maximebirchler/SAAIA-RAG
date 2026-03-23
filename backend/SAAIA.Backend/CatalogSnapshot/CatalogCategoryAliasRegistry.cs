using System.Globalization;
using System.Text;

namespace SAAIA.Backend.CatalogSnapshot;

public static class CatalogCategoryAliasRegistry
{
    public sealed record CategoryAliasDefinition(string Alias, string AliasKey, string? Language, string Source, int Priority);

    private sealed record AliasSeed(string Alias, string? Language, string Source, int Priority);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<AliasSeed>> _explicitAliases =
        new Dictionary<string, IReadOnlyList<AliasSeed>>(StringComparer.Ordinal)
        {
            ["atex"] = new[]
            {
                new AliasSeed("ATEX", "fr", "builtin", 10)
            },
            ["general"] = new[]
            {
                new AliasSeed("General", "en", "builtin", 10),
                new AliasSeed("Général", "fr", "builtin", 20),
                new AliasSeed("Allgemein", "de", "builtin", 30),
                new AliasSeed("Generale", "it", "builtin", 40),
                new AliasSeed("Geral", "pt", "builtin", 50),
                new AliasSeed("Generalidades", "es", "builtin", 60)
            },
            ["programmation"] = new[]
            {
                new AliasSeed("Programmation", "fr", "builtin", 10),
                new AliasSeed("Programming", "en", "builtin", 20),
                new AliasSeed("Programación", "es", "builtin", 30),
                new AliasSeed("Programacao", "pt", "builtin", 40),
                new AliasSeed("Programação", "pt", "builtin", 41),
                new AliasSeed("Programmierung", "de", "builtin", 50),
                new AliasSeed("Programmazione", "it", "builtin", 60)
            }
        };

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

        var canonicalKey = NormalizeComparable(string.IsNullOrWhiteSpace(topLevel) ? name : topLevel);
        if (_explicitAliases.TryGetValue(canonicalKey, out var seeds))
        {
            foreach (var seed in seeds)
                Add(seed.Alias, seed.Language, seed.Source, seed.Priority);
        }

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
