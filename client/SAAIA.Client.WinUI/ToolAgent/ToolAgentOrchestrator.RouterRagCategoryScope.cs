using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static string? ResolveTrustedRouterRagCategoryScope(
        string? routerCategoryScope,
        string? fallbackCategoryScope,
        string effectiveUserMessage)
    {
        var candidate = NormalizeCategoryPathArg(routerCategoryScope);
        var fallback = NormalizeCategoryPathArg(fallbackCategoryScope);
        if (LooksLikeLeakedRouterRagCategoryScope(fallback, effectiveUserMessage))
            fallback = null;
        if (LooksLikeLeakedRouterRagCategoryScope(candidate, effectiveUserMessage))
            return fallback;

        return candidate ?? fallback;
    }

    private static bool LooksLikeLeakedRouterRagCategoryScope(string? categoryScope, string effectiveUserMessage)
    {
        var normalized = NormalizeLexicalLookup(categoryScope ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if ((categoryScope ?? string.Empty).Contains('/', StringComparison.Ordinal)
            || Regex.IsMatch(categoryScope ?? string.Empty, @"\bcat[_-]?\d+\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return false;
        }

        var tokens = Regex.Matches(normalized, @"[\p{L}\p{Nd}]{2,}", RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(static match => match.Value)
            .ToArray();
        if (tokens.Length < 5)
            return false;

        var containsPromptLeak = Regex.IsMatch(
            normalized,
            @"\b(?:documents?|sources?|fichiers?|cherche|recherche|trouve|mentionne|mentionnent|reponds?|answer|respond|avec|dans|pour|query|question)\b",
            RegexOptions.CultureInvariant);
        if (!containsPromptLeak)
            return false;

        return true;
    }
}