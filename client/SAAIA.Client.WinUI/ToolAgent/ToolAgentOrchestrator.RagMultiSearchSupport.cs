using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private const int RagMultiSearchMaxParallelism = 2;
    private const int RagMultiSearchCategoryProbeMaxScopes = 8;
    private const int RagMultiSearchCategoryProbeMaxQueries = 4;
    private const int RagMultiSearchCategoryProbeMaxEffectiveQueries = 6;
    private const int RagMultiSearchCategorySupplementalQueryLimit = 2;
    private const int RagMultiSearchCategoryProbeTopK = 3;
    private const int RagMultiSearchCategoryCatalogProbeMaxTerms = 6;
    private const int RagMultiSearchCategoryCatalogProbeLimit = 24;
    private const int RagMultiSearchCategoryChildProbeMaxParents = 4;
    private const int RagMultiSearchCategoryChildProbeLimit = 24;
    private static readonly TimeSpan DefaultRagSourceExplorationQueryTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RagMultiSearchCategoryProbeQueryTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RagMultiSearchCategoryCatalogProbeQueryTimeout = TimeSpan.FromSeconds(4);
#if DEBUG
    private static TimeSpan? RagSourceExplorationQueryTimeoutOverrideForTests;
#endif

    private static TimeSpan RagSourceExplorationQueryTimeout
    {
        get
        {
#if DEBUG
            return RagSourceExplorationQueryTimeoutOverrideForTests
                ?? DefaultRagSourceExplorationQueryTimeout;
#else
            return DefaultRagSourceExplorationQueryTimeout;
#endif
        }
    }

    private sealed record RagMultiSearchHitCandidate(JsonElement Hit, int QueryIndex, int HitRank, int QuerySpecificity, string Query);
    private sealed record RagCategoryScopeProbeCandidate(
        ToolMemory.CategorySnapshot Category,
        string Scope,
        int LexicalScore,
        int CatalogHitCount,
        int CatalogQueryMatches,
        int CatalogRepeatedQueryMatches,
        int CatalogDominantTermQueryMatches,
        string? CatalogSuggestedScope);

    private sealed class RagCategoryCatalogScopeEvidence
    {
        public int HitCount { get; set; }
        public HashSet<string> Terms { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> TermQueryMatches { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> SpecificScopes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int RepeatedQueryMatches => TermQueryMatches.Values.Sum();
        public int DominantTermQueryMatches => TermQueryMatches.Count == 0 ? 0 : TermQueryMatches.Values.Max();
    }

    private sealed record RagCategoryScopeProbeResult(
        string Scope,
        int HitCount,
        int MatchedQueryCount,
        double Score,
        int ErrorCount,
        long ElapsedMs,
        int LexicalScore,
        int CatalogHitCount,
        int CatalogQueryMatches);

    private static string FormatRagTraceValue(string? value, int maxChars = 120)
    {
        var collapsed = CollapseWhitespace(value ?? string.Empty);
        return string.IsNullOrWhiteSpace(collapsed)
            ? "-"
            : TruncateForPrompt(collapsed, maxChars);
    }

    private static int ResolveRagMultiSearchQueryBudget(
        int topK,
        int availableQueries,
        string? researchMode = null,
        bool includeResearchSurfaces = false)
    {
        if (availableQueries <= 0)
            return 0;

        var sourceExploration = includeResearchSurfaces
            || string.Equals(researchMode, "source_exploration", StringComparison.OrdinalIgnoreCase);
        if (sourceExploration)
        {
            var explorationBudget = topK >= 20
                ? 12
                : topK >= 18
                    ? 11
                    : topK >= 14
                        ? 10
                        : topK >= 12
                            ? 9
                            : Math.Max(10, topK);
            return Math.Clamp(Math.Min(availableQueries, explorationBudget), 1, 12);
        }

        var budget = topK >= 12
            ? 12
            : topK >= 10
                ? 10
                : 8;
        return Math.Clamp(Math.Min(availableQueries, budget), 1, 12);
    }

    private static string BuildRagHitDedupeKey(JsonElement hit)
    {
        var docPath =
            TryGetString(hit, "docPath")
            ?? TryGetString(hit, "doc_path")
            ?? string.Empty;
        var pageStart = ReadRagHitPageStart(hit);
        var pageEnd = ReadRagHitPageEnd(hit, pageStart);
        var chunkId =
            TryGetString(hit, "chunkId")
            ?? TryGetString(hit, "chunk_id")
            ?? TryGetString(hit, "ChunkId");
        var sourceHash =
            TryGetString(hit, "sourceHash")
            ?? TryGetString(hit, "source_hash")
            ?? TryGetString(hit, "SourceHash");
        var contentCardKeys = ExtractRagHitContentCardKeysForDedupe(hit);
        var discriminator = !string.IsNullOrWhiteSpace(chunkId)
            ? $"chunk:{chunkId.Trim()}"
            : contentCardKeys.Count > 0
                ? $"cards:{string.Join(",", contentCardKeys)}"
                : !string.IsNullOrWhiteSpace(sourceHash)
                    ? $"source:{sourceHash.Trim()}"
                    : "page";

        return $"{docPath}|{pageStart}|{pageEnd}|{discriminator}";
    }

    private static IReadOnlyList<string> ExtractRagHitContentCardKeysForDedupe(JsonElement hit)
    {
        var cards = TryGetArray(hit, "matchedContentCards")
                    ?? TryGetArray(hit, "matched_content_cards")
                    ?? TryGetArray(hit, "contentCards")
                    ?? TryGetArray(hit, "content_cards")
                    ?? TryGetArray(hit, "MatchedContentCards")
                    ?? TryGetArray(hit, "ContentCards");
        if (!cards.HasValue)
            return [];

        return cards.Value.EnumerateArray()
            .Where(static card => card.ValueKind == JsonValueKind.Object)
            .Select(BuildRagHitContentCardKeyForDedupe)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
    }

    private static string? BuildRagHitContentCardKeyForDedupe(JsonElement card)
    {
        var id = TryGetString(card, "contentCardId")
                 ?? TryGetString(card, "content_card_id")
                 ?? TryGetString(card, "ContentCardId");
        if (!string.IsNullOrWhiteSpace(id))
            return $"id:{id.Trim()}";

        var title = CollapseWhitespace(
            TryGetString(card, "title")
            ?? TryGetString(card, "Title")
            ?? string.Empty);
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var kind = CollapseWhitespace(
            TryGetString(card, "kind")
            ?? TryGetString(card, "Kind")
            ?? string.Empty);
        var pageStart = TryGetInt(card, "pageStart")
                        ?? TryGetInt(card, "page_start")
                        ?? TryGetInt(card, "PageStart");
        var pageEnd = TryGetInt(card, "pageEnd")
                      ?? TryGetInt(card, "page_end")
                      ?? TryGetInt(card, "PageEnd")
                      ?? pageStart;

        return $"shape:{title.ToLowerInvariant()}|{kind.ToLowerInvariant()}|{pageStart?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""}|{pageEnd?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""}";
    }

    private static double ReadRagHitScore(JsonElement hit)
        => hit.ValueKind == JsonValueKind.Object
           && hit.TryGetProperty("score", out var score)
           && score.ValueKind == JsonValueKind.Number
           && score.TryGetDouble(out var value)
            ? value
            : 0.0;

    private static int ComputeRagHitMetadataRichness(JsonElement hit)
    {
        if (hit.ValueKind != JsonValueKind.Object)
            return 0;

        var richness = 0;
        if (!string.IsNullOrWhiteSpace(TryGetString(hit, "sourceHash") ?? TryGetString(hit, "source_hash")))
            richness += 6;
        if (!string.IsNullOrWhiteSpace(TryGetDocumentLanguage(hit)))
            richness += 4;
        if (!string.IsNullOrWhiteSpace(TryGetString(hit, "profileLanguage") ?? TryGetString(hit, "profile_language")))
            richness += 4;
        if (!string.IsNullOrWhiteSpace(TryGetString(hit, "categoryRef") ?? TryGetString(hit, "category_ref")))
            richness += 3;
        if (!string.IsNullOrWhiteSpace(TryGetString(hit, "categoryPath") ?? TryGetString(hit, "category_path")))
            richness += 3;
        if (!string.IsNullOrWhiteSpace(TryGetString(hit, "chunkId") ?? TryGetString(hit, "chunk_id")))
            richness += 2;
        if (!string.IsNullOrWhiteSpace(TryGetString(hit, "contextualSnippet") ?? TryGetString(hit, "contextual_snippet")))
            richness += 2;

        var quality = TryGetObject(hit, "extractionQuality") ?? TryGetObject(hit, "extraction_quality") ?? TryGetObject(hit, "ExtractionQuality");
        if (quality.HasValue)
        {
            richness += 10;
            if (!string.IsNullOrWhiteSpace(TryGetString(quality.Value, "documentQualityStatus") ?? TryGetString(quality.Value, "document_quality_status")))
                richness += 2;
            if (!string.IsNullOrWhiteSpace(TryGetString(quality.Value, "pageQualityStatus") ?? TryGetString(quality.Value, "page_quality_status")))
                richness += 2;
            richness += Math.Min(8, CountArrayPropertyAny(quality.Value, "signals", "Signals"));
            var diagnostics =
                TryGetObject(quality.Value, "diagnosticSummary")
                ?? TryGetObject(quality.Value, "diagnostic_summary")
                ?? TryGetObject(quality.Value, "DiagnosticSummary");
            if (diagnostics.HasValue)
                richness += 10 + Math.Min(12, CountObjectScalarProperties(diagnostics.Value));
        }

        richness += CountArrayPropertyAny(hit, "matchedContentCards", "matched_content_cards", "contentCards", "content_cards") * 8;
        var hints = TryGetObject(hit, "selectionHints") ?? TryGetObject(hit, "selection_hints") ?? TryGetObject(hit, "SelectionHints");
        if (hints.HasValue)
            richness += 8 + Math.Min(10, CountObjectScalarProperties(hints.Value));

        return richness;
    }

    private static JsonElement AnnotateRagMultiSearchHit(RagMultiSearchHitCandidate candidate)
    {
        if (candidate.Hit.ValueKind != JsonValueKind.Object)
            return candidate.Hit.Clone();

        var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(candidate.Hit.GetRawText())
                      ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        payload["retrievalQuery"] = candidate.Query;
        payload["retrievalQueryIndex"] = candidate.QueryIndex;
        payload["retrievalHitRank"] = candidate.HitRank;
        payload["retrievalQuerySpecificity"] = candidate.QuerySpecificity;
        return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement.Clone();
    }

    private static int ComputeRagMultiSearchQuerySpecificity(string query)
    {
        var normalized = NormalizeLexicalLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return 0;

        var distinctTerms = Regex.Matches(normalized, @"[\p{L}\p{Nd}]{2,}", RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(static match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var score = Math.Min(80, distinctTerms * 5) + Math.Min(40, normalized.Length / 8);

        if (query.Contains('"', StringComparison.Ordinal))
            score += 8;
        if (Regex.IsMatch(normalized, @"\b(?:detail|details|procedure|quantite|quantites|quantity|quantities|timing|source|sources)\b", RegexOptions.CultureInvariant))
            score += 8;

        return score;
    }

    private static int CountArrayPropertyAny(JsonElement obj, params string[] propertyNames)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return 0;

        foreach (var propertyName in propertyNames)
        {
            if (obj.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Array)
                return value.GetArrayLength();
        }

        return 0;
    }

    private static int CountObjectScalarProperties(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return 0;

        var count = 0;
        foreach (var property in obj.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                count++;
        }

        return count;
    }
}
