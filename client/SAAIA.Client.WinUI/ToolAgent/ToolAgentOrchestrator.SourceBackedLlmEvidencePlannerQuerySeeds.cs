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

    private string BuildSourceBackedLlmCategoryHintsForPrompt(
        string effectiveUserMessage,
        int maxCategories = 80,
        bool compact = false)
    {
        var categories = SelectSourceBackedLlmCategoryHints(
                effectiveUserMessage,
                maxCategories)
            .Select(x =>
            {
                var category = x.Category;
                var name = CollapseWhitespace(category.DisplayName);
                var path = CollapseWhitespace(category.CategoryPath);
                var categoryRef = CollapseWhitespace(category.CategoryRef);
                var scopeValue = string.IsNullOrWhiteSpace(path)
                    ? string.IsNullOrWhiteSpace(name) ? categoryRef : name
                    : path;
                var semanticLabel = string.IsNullOrWhiteSpace(path) || string.Equals(path, name, StringComparison.OrdinalIgnoreCase)
                    ? name
                    : $"{name} / {path}";
                if (compact)
                {
                    return TruncateForPrompt(
                        $"- {scopeValue} = {semanticLabel}",
                        140);
                }

                var aliases = category.Aliases is { Count: > 0 }
                    ? $" | aliases: {string.Join(", ", category.Aliases.Select(CollapseWhitespace).Where(static a => !string.IsNullOrWhiteSpace(a)).Take(2))}"
                    : string.Empty;
                var docs = category.TotalDocuments > 0 ? $" | docs: {category.TotalDocuments}" : string.Empty;
                var score = $" | lexicalScore: {x.Score}";
                var reference = string.IsNullOrWhiteSpace(categoryRef) ? string.Empty : $" | ref: {categoryRef}";
                var promptName = TruncateForPrompt(name, 54);
                var promptSemanticLabel = TruncateForPrompt(semanticLabel, 82);
                var scope = string.IsNullOrWhiteSpace(scopeValue)
                    ? string.Empty
                    : $" | scopeValue: {TruncateForPrompt(scopeValue, 72)}";
                return TruncateForPrompt(
                    $"- {promptName} | semanticLabel: {promptSemanticLabel}{scope}{reference}{docs}{aliases}{score}",
                    260);
            })
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        return categories.Length == 0 ? "none" : string.Join(Environment.NewLine, categories);
    }

    private IReadOnlyList<(ToolMemory.CategorySnapshot Category, int Score)>
        SelectSourceBackedLlmCategoryHints(
            string effectiveUserMessage,
            int maxCategories)
    {
        var query = NormalizeLexicalLookup(effectiveUserMessage);
        var maximum = Math.Max(1, maxCategories);
        var ranked = (_mem.LastPresentedCategories ?? new List<ToolMemory.CategorySnapshot>())
            .Concat(_mem.CatalogSnapshotCache?.Categories ?? new List<ToolMemory.CategorySnapshot>())
            .GroupBy(static category => CollapseWhitespace(
                string.IsNullOrWhiteSpace(category.CategoryRef)
                    ? string.IsNullOrWhiteSpace(category.CategoryPath) ? category.DisplayName : category.CategoryPath
                    : category.CategoryRef), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Select(category => (
                Category: category,
                Score: ComputeCategoryHintScore(category, query)))
            .OrderByDescending(static x => x.Score)
            .ThenBy(static x => x.Category.Ordinal)
            .ToArray();

        var supported = ranked
            .Where(static candidate => candidate.Score > 0)
            .ToArray();
        if (supported.Length <= maximum)
            return supported;

        var cutoffScore = supported[maximum - 1].Score;
        return supported
            .Where(candidate => candidate.Score > cutoffScore)
            .ToArray();
    }

    private static int ComputeCategoryHintScore(ToolMemory.CategorySnapshot category, string normalizedQuery)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return 0;

        var haystack = NormalizeLexicalLookup(string.Join(' ', new[]
        {
            category.DisplayName,
            category.CategoryPath,
            string.Join(' ', category.Aliases ?? new List<string>())
        }));
        if (string.IsNullOrWhiteSpace(haystack))
            return 0;

        return ExtractQuerySignalTerms(normalizedQuery)
            .Where(static term => term.Length >= 4)
            .Count(term => haystack.Contains(term, StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> BuildAlreadyTriedSourceBackedEvidenceExplorationQueries(
        ToolResults toolResults,
        string effectiveUserMessage,
        string language)
    {
        var queries = new List<string>();
        AddDistinctQuery(queries, NormalizeRagQueryForRetrieval(effectiveUserMessage));

        foreach (var query in BuildSourceBackedLlmEvidencePlannerDeterministicQuerySeeds(effectiveUserMessage, language))
            AddDistinctQuery(queries, query);

        foreach (var retrievalQuery in EnumerateRagHitSummaries(toolResults)
                     .Select(static hit => hit.RetrievalQuery)
                     .Where(static query => !string.IsNullOrWhiteSpace(query))
                     .Select(static query => query!))
        {
            AddDistinctQuery(queries, retrievalQuery);
        }

        return queries
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .Select(CollapseWhitespace)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(query => ScoreSourceBackedLlmPlannerPromptQuery(query, effectiveUserMessage))
            .ThenBy(static query => query.Length)
            .Take(MaxSourceBackedLlmEvidencePlannerAlreadyTriedQueries)
            .ToArray();
    }

    private static IReadOnlyList<string> BuildSourceBackedLlmEvidencePlannerDeterministicQuerySeeds(
        string effectiveUserMessage,
        string language)
    {
        var queries = new List<string>();
        var intentProbe = BuildInitialSourceBackedPlanningIntentProbeQuery(effectiveUserMessage);
        var intentProbeKey = NormalizeLexicalLookup(intentProbe);
        void Add(string? query)
        {
            if (!string.IsNullOrWhiteSpace(query))
                AddDistinctQuery(queries, CollapseWhitespace(query));
        }

        if (LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage))
        {
            Add(intentProbe);
            foreach (var query in BuildPlanningExplorationRetrievalQueries(effectiveUserMessage))
                Add(query);
            foreach (var query in BuildPlanningRetrievalQueries(effectiveUserMessage))
                Add(query);
        }
        else
        {
            foreach (var query in BuildSourceBackedActionRetrievalQueries(effectiveUserMessage))
                Add(query);
            foreach (var query in BuildSourceBackedEvidenceExpansionRetrievalQueries(effectiveUserMessage))
                Add(query);
            foreach (var query in BuildDocumentaryProbeRetrievalQueries(effectiveUserMessage))
                Add(query);
        }

        return queries
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(query => string.Equals(NormalizeLexicalLookup(query), intentProbeKey, StringComparison.Ordinal) ? -200 : ScoreSourceBackedLlmPlannerPromptQuery(query, effectiveUserMessage))
            .ThenBy(static query => query.Length)
            .Take(MaxSourceBackedLlmEvidencePlannerDeterministicSeeds)
            .ToArray();
    }

    private static int ScoreSourceBackedLlmPlannerPromptQuery(string query, string effectiveUserMessage)
    {
        var normalizedQuery = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return int.MaxValue;

        var score = query.Length > 120 ? 120 : query.Length > 90 ? 60 : query.Length > 70 ? 25 : 0;
        var normalizedUser = NormalizeLexicalLookup(NormalizeRagQueryForRetrieval(effectiveUserMessage));
        if (!string.IsNullOrWhiteSpace(normalizedUser)
            && string.Equals(normalizedQuery, normalizedUser, StringComparison.Ordinal)
            && query.Length > 70)
        {
            score += 140;
        }

        var tokenCount = Regex.Matches(normalizedQuery, @"[\p{L}\p{Nd}]{3,}", RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(static match => match.Value)
            .Where(static token => !IsWeakRouterRagQueryToken(token))
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (tokenCount > 10)
            score += 60;
        else if (tokenCount <= 1)
            score += 15;

        return score;
    }

    private static string FormatPromptList(IEnumerable<string> values, int maxItems = 40, int maxItemLength = 160)
    {
        var lines = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(value => $"- {TruncateForPrompt(CollapseWhitespace(value), maxItemLength)}")
            .Take(Math.Max(1, maxItems))
            .ToArray();
        return lines.Length == 0 ? "none" : string.Join(Environment.NewLine, lines);
    }

    private static string LimitPromptBlockLines(IEnumerable<string> values, int maxLines, int maxLineLength)
    {
        var lines = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(value => TruncateForPrompt(CollapseWhitespace(value), maxLineLength))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Take(Math.Max(1, maxLines))
            .ToArray();
        return lines.Length == 0 ? "none" : string.Join(Environment.NewLine, lines);
    }

    private static int CountRagRetrievalToolCalls(ToolResults toolResults)
        => toolResults.Items.Count(static item => item.ToolName is "rag.search" or "rag.multi_search");

    private static bool HasAttemptedBroadenedSourceBackedRetrieval(ToolResults toolResults)
        => CountRagRetrievalToolCalls(toolResults) > 1
           || toolResults.Items.Any(static item =>
               (item.ToolName is "documents.tree" or "documents.navigation" or "summary.search")
               && string.IsNullOrWhiteSpace(item.Error));
}
