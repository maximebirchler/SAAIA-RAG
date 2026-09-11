using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static string? TryResolveLexicalRagCategoryScope(
        string effectiveUserMessage,
        IReadOnlyList<ToolMemory.CategorySnapshot> candidates)
    {
        var normalizedQuery = NormalizeLexicalLookup(effectiveUserMessage);
        if (candidates.Count == 0 || string.IsNullOrWhiteSpace(normalizedQuery))
            return null;

        var scored = candidates
            .Select(category => new
            {
                Category = category,
                Score = ComputeCategoryHintScore(category, normalizedQuery)
            })
            .Where(static item => item.Score > 0)
            .OrderByDescending(static item => item.Score)
            .ThenByDescending(static item => item.Category.TotalDocuments)
            .ThenBy(static item => item.Category.Ordinal)
            .Take(2)
            .ToArray();

        if (scored.Length == 0)
            return null;
        if (scored.Length > 1 && scored[0].Score == scored[1].Score)
            return null;

        var winner = scored[0].Category;
        return string.IsNullOrWhiteSpace(winner.CategoryPath) ? winner.DisplayName : winner.CategoryPath;
    }

    private string? ResolveLlmPlannedRagCategoryScope(string? plannedCategoryScope)
    {
        var normalizedScope = NormalizeLooseLookup(plannedCategoryScope);
        if (normalizedScope.Length < 3)
            return null;

        var candidates = (_mem.CatalogSnapshotCache?.Categories ?? new List<ToolMemory.CategorySnapshot>())
            .Concat(_mem.LastPresentedCategories ?? new List<ToolMemory.CategorySnapshot>())
            .GroupBy(static category => string.Join(
                    "|",
                    category.CategoryPath,
                    category.CategoryRef,
                    category.DisplayName),
                StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First());

        foreach (var category in candidates)
        {
            var names = new[]
                {
                    category.CategoryPath,
                    category.DisplayName,
                    category.CategoryRef
                }
                .Concat(category.Aliases ?? new List<string>())
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Select(NormalizeLooseLookup)
                .Where(static name => name.Length >= 3)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (names.Any(name =>
                    string.Equals(name, normalizedScope, StringComparison.Ordinal)
                    || normalizedScope.Contains(name, StringComparison.Ordinal)
                    || name.Contains(normalizedScope, StringComparison.Ordinal)))
            {
                return string.IsNullOrWhiteSpace(category.CategoryPath) ? category.DisplayName : category.CategoryPath;
            }
        }

        return null;
    }

    private static string? TryExtractExplicitRagCategoryScope(string? effectiveUserMessage)
    {
        var message = CollapseWhitespace(effectiveUserMessage ?? string.Empty);
        if (message.Length < 8)
            return null;

        foreach (Match match in Regex.Matches(
            message,
            @"(?i)\b(?:dans|in|within|categoria|catÃƒÂ©gorie|categorie|category|dossier|folder)\s+(?:la|le|les|l['\u2019]|the|un|une|des)?\s*(?<scope>[\p{L}\p{N}][\p{L}\p{N}'\u2019 \-_/&]{2,80})",
            RegexOptions.CultureInvariant))
        {
            var scope = CleanupExplicitRagCategoryScope(match.Groups["scope"].Value);
            if (!string.IsNullOrWhiteSpace(scope))
                return scope;
        }

        return null;
    }

    private static string? CleanupExplicitRagCategoryScope(string? value)
    {
        var scope = CollapseWhitespace(value ?? string.Empty)
            .Trim(' ', '.', ',', ';', ':', '!', '?', ')', ']', '}');
        if (string.IsNullOrWhiteSpace(scope))
            return null;

        scope = Regex.Replace(
            scope,
            @"(?i)\s+\b(?:pour|afin|avec|qui|que|dont|when|with|for|about|sobre)\b.*$",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();
        scope = scope.Trim(' ', '.', ',', ';', ':', '!', '?', ')', ']', '}');

        var normalized = NormalizeLooseLookup(scope);
        if (normalized.Length < 3)
            return null;
        if (Regex.IsMatch(
            normalized,
            @"^(?:documents?|docs?|pdf|sources?|corpus|base\s+de\s+connaissance|knowledge\s+base|documentation)$",
            RegexOptions.CultureInvariant))
        {
            return null;
        }

        return scope;
    }

    private static string? TryExtractTopLevelCategoryFromDocPath(string? docPath)
    {
        var normalized = (docPath ?? string.Empty).Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var slash = normalized.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0)
            return null;

        var category = normalized[..slash].Trim();
        return string.IsNullOrWhiteSpace(category) ? null : category;
    }
}