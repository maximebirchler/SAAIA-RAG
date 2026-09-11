using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Localization;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static string[] BuildSourceBackedCandidateDiscoveryRetrievalQueries(string query)
    {
        var queries = new List<string>();
        var normalized = NormalizeLexicalLookup(NormalizeRagQueryForRetrieval(query));
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = NormalizeLexicalLookup(query);
        var planningCoverage = UsesSourceBackedPlanningCoverage(query);
        foreach (var retrievalQuery in BuildSoftChoiceOptionKindRetrievalQueries(query))
        {
            if (planningCoverage && LooksLikeNavigationDiscoveryProbeQuery(retrievalQuery))
                continue;
            AddDistinctQuery(queries, retrievalQuery);
        }

        var subjectTerms = ExtractQuerySignalTerms(normalized)
            .Concat(ExtractPlanningRetrievalTerms(normalized))
            .Where(static term => term.Length >= 4)
            .Where(static term => !IsSourceBackedActionRetrievalNoiseTerm(term))
            .Where(static term => !IsGenericPlanningCoverageTerm(term))
            .SelectMany(BuildRetrievalTermVariants)
            .Where(static term => term.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        var supportTerms = BuildPlanningExplorationSupportTermsForRetrieval(query)
            .Take(5)
            .ToArray();
        var candidateSuffixes = BuildCandidateExpansionSuffixes(query)
            .Where(suffix => !planningCoverage
                             || !LooksLikeDecorativeStructuredAxisPlannerQuery(NormalizeLexicalLookup(suffix)))
            .Take(4)
            .ToArray();
        var slotTerms = ExtractPlanningSlotRetrievalTerms(query)
            .Take(5)
            .ToArray();
        var constraintTerms = ExtractPlanningConstraintRetrievalTerms(normalized)
            .Take(4)
            .ToArray();

        if (planningCoverage)
        {
            foreach (var retrievalQuery in BuildStructuredPlanningCandidateDiscoveryRetrievalQueries(query).Take(14))
                AddDistinctQuery(queries, retrievalQuery);
        }

        if (!planningCoverage)
        {
            foreach (var retrievalQuery in BuildNavigationDiscoveryRetrievalQueries(query).Take(8))
                AddDistinctQuery(queries, retrievalQuery);
        }
        foreach (var retrievalQuery in BuildBroadSourceBackedDiscoveryRetrievalQueries(query).Take(14))
            AddDistinctQuery(queries, retrievalQuery);

        foreach (var subject in subjectTerms)
        {
            AddDistinctQuery(queries, subject);
            foreach (var suffix in candidateSuffixes)
                AddDistinctQuery(queries, $"{subject} {suffix}");
            foreach (var support in supportTerms.Take(3))
                AddDistinctQuery(queries, $"{subject} {support}");
            foreach (var constraint in constraintTerms)
                AddDistinctQuery(queries, $"{subject} {constraint}");
        }

        foreach (var slot in slotTerms)
        {
            AddDistinctQuery(queries, slot);
            foreach (var support in supportTerms.Take(3))
                AddDistinctQuery(queries, $"{slot} {support}");
            foreach (var subject in subjectTerms.Take(4))
                AddDistinctQuery(queries, $"{subject} {slot}");
        }

        foreach (var retrievalQuery in BuildSourceBackedActionRetrievalQueries(query).Take(10))
            AddDistinctQuery(queries, retrievalQuery);

        return queries
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(CollapseWhitespace)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(18)
            .ToArray();
    }

    private static IEnumerable<string> BuildStructuredPlanningInventoryRetrievalQueries(string query)
    {
        var language = DetectRetrievalExpansionLanguage(query);
        foreach (var queryVariant in BuildStructuredPlanningInventoryTermsForRetrieval(language, query))
        {
            yield return queryVariant;
        }

    }

    private static string[] BuildSourceBackedAnchorDiscoveryRetrievalQueries(string query)
    {
        var queries = new List<string>();
        var planningCoverage = UsesSourceBackedPlanningCoverage(query);
        var anchorTerms = ExtractPlanningCoverageAnchorTerms(query)
            .Concat(ExtractPairingTargetAnchorTerms(query))
            .Concat(ExtractPairingRequestedOptionKindTerms(query))
            .Concat(ExtractSoftChoiceRequestedOptionKindTerms(query))
            .SelectMany(BuildRetrievalTermVariants)
            .Where(static term => term.Length >= 4)
            .Where(static term => !IsSourceBackedActionRetrievalNoiseTerm(term))
            .Where(static term => !IsGenericPlanningCoverageTerm(term))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        var suffixes = BuildCandidateExpansionSuffixes(query)
            .Concat(BuildPlanningExpansionSuffixes(query))
            .Where(suffix => !planningCoverage
                             || !LooksLikeDecorativeStructuredAxisPlannerQuery(NormalizeLexicalLookup(suffix)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();
        var slotTerms = ExtractPlanningSlotRetrievalTerms(query)
            .Take(5)
            .ToArray();

        foreach (var anchor in anchorTerms)
        {
            AddDistinctQuery(queries, anchor);
            foreach (var suffix in suffixes)
                AddDistinctQuery(queries, $"{anchor} {suffix}");
            foreach (var slot in slotTerms.Take(4))
                AddDistinctQuery(queries, $"{anchor} {slot}");
        }

        foreach (var slot in slotTerms)
            AddDistinctQuery(queries, slot);

        return queries
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(CollapseWhitespace)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToArray();
    }

    private static string NormalizeSourceBackedLlmExplorationLabel(string? label)
    {
        var normalized = CollapseWhitespace(label ?? string.Empty)
            .ToLowerInvariant()
            .Replace('-', '_');
        normalized = Regex.Replace(normalized, @"[^a-z0-9_]+", "_", RegexOptions.CultureInvariant).Trim('_');
        return string.IsNullOrWhiteSpace(normalized) ? "llm_strategy" : normalized;
    }

    private static string[] ExtractSanitizedSourceBackedLlmExplorationQueries(
        JsonElement container,
        HashSet<string> emitted)
    {
        if (!container.TryGetProperty("queries", out var queriesElement) || queriesElement.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var queries = new List<string>();
        foreach (var queryElement in queriesElement.EnumerateArray())
        {
            if (queryElement.ValueKind != JsonValueKind.String)
                continue;

            var sanitized = SanitizeSourceBackedLlmExplorationQuery(queryElement.GetString());
            if (string.IsNullOrWhiteSpace(sanitized))
                continue;

            var key = NormalizeGeneratedSourceBackedExplorationQueryForDedup(sanitized);
            if (string.IsNullOrWhiteSpace(key) || !emitted.Add(key))
                continue;

            queries.Add(sanitized);
            if (queries.Count >= MaxSourceBackedLlmEvidenceExplorationQueries)
                break;
        }

        return queries.ToArray();
    }

    private static string? SanitizeSourceBackedLlmExplorationCategory(string? category)
    {
        var value = CollapseWhitespace(category ?? string.Empty)
            .Trim(' ', '.', ',', ';', ':', '-', '\u2022', '\u00b7');
        if (value.Length < 3 || value.Length > 120)
            return null;

        if (LooksLikeUnsafeGeneratedSourceBackedExplorationQuery(value))
            return null;

        if (LooksLikeNoisyGeneratedSourceBackedExplorationQuery(value))
            return null;

        if (Regex.Matches(value, @"[{}<>]").Count > 0)
            return null;

        return value;
    }

    private static string? SanitizeSourceBackedLlmExplorationQuery(string? query)
    {
        var value = CollapseWhitespace(query ?? string.Empty)
            .Trim(' ', '.', ',', ';', ':', '-', '\u2022', '\u00b7');
        if (value.Length < 3 || value.Length > 120)
            return null;

        if (LooksLikeUnsafeGeneratedSourceBackedExplorationQuery(value))
            return null;

        if (LooksLikeNoisyGeneratedSourceBackedExplorationQuery(value))
            return null;

        if (Regex.Matches(value, @"[{}<>]").Count > 0)
            return null;

        var tokenCount = Regex.Matches(value, @"[\p{L}\p{N}][\p{L}\p{N}'â€™/-]*", RegexOptions.CultureInvariant).Count;
        if (tokenCount is < 1 or > 14)
            return null;

        var normalized = NormalizeRagQueryForRetrieval(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        normalized = CollapseWhitespace(normalized)
            .Trim(' ', '.', ',', ';', ':', '-', '\u2022', '\u00b7');
        if (normalized.Length < 3 || normalized.Length > 120)
            return null;

        return normalized;
    }

    private static bool LooksLikeUnsafeGeneratedSourceBackedExplorationQuery(string value)
    {
        var normalized = NormalizeLexicalLookup(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        if (Regex.IsMatch(
                normalized,
                @"\b(?:ignore|override|bypass|system prompt|developer message|tool result|tool_results|assistant response|final answer|json schema|execute|delete|drop table|powershell|cmd\.exe|http://|https://)\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        return Regex.IsMatch(
            normalized,
            @"\b(?:reponds?|answer|write|redige|ecris|compose|summarize|resume)\b.+\b(?:directly|directement|finale?|utilisateur|user)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeNoisyGeneratedSourceBackedExplorationQuery(string value)
    {
        var raw = CollapseWhitespace(value ?? string.Empty);
        var normalized = NormalizeLexicalLookup(raw);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        var tokens = Regex.Matches(normalized, @"[\p{L}\p{N}]{3,}", RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(static match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (tokens.Length < 8)
            return false;

        if (Regex.IsMatch(raw, @"\p{L}{3,}-\p{L}{2,}", RegexOptions.CultureInvariant))
            return true;

        if (Regex.IsMatch(raw, @"[,;]", RegexOptions.CultureInvariant)
            && tokens.Length >= 10)
            return true;

        return Regex.IsMatch(
            normalized,
            @"\b(?:occasion|decouvrir|discover|autres?|others?|parfois|sometimes|souvent|often|lorsque|when|while|pendant|during|because|donne|gives|permet|allows)\b",
            RegexOptions.CultureInvariant);
    }

    private static string NormalizeGeneratedSourceBackedExplorationQueryForDedup(string? query)
    {
        var normalized = NormalizeRagQueryForRetrieval(query ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = CollapseWhitespace(query ?? string.Empty);

        return CollapseWhitespace(normalized).ToLowerInvariant();
    }

}
