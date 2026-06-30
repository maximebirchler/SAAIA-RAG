using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Models;
using SAAIA.Client.WinUI.Services;
using SAAIA.Contracts;

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
    private static readonly TimeSpan DefaultRagMultiSearchSourceExplorationQueryTimeout = TimeSpan.FromSeconds(65);
    private static readonly TimeSpan RagMultiSearchCategoryProbeQueryTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RagMultiSearchCategoryCatalogProbeQueryTimeout = TimeSpan.FromSeconds(4);
#if DEBUG
    private static TimeSpan? RagMultiSearchSourceExplorationQueryTimeoutOverrideForTests;
#endif

    private static TimeSpan RagMultiSearchSourceExplorationQueryTimeout
    {
        get
        {
#if DEBUG
            return RagMultiSearchSourceExplorationQueryTimeoutOverrideForTests
                ?? DefaultRagMultiSearchSourceExplorationQueryTimeout;
#else
            return DefaultRagMultiSearchSourceExplorationQueryTimeout;
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

    private async Task<(bool ok, string answer, object? sourcesPayload)> TryReplayLastInventoryAnswerWithWriterAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        string language,
        CancellationToken ct,
        Action<string>? onPhase,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        _ = chatHistory;
        _ = userMessage;

        if (_mem.LastDeterministicRender is null
            || string.IsNullOrWhiteSpace(_mem.LastDeterministicRender.Kind)
            || string.IsNullOrWhiteSpace(_mem.LastDeterministicRender.DataJson)
            || !IsInventoryIntent(_mem.LastDeterministicRender.RouterIntent ?? _mem.LastRouterIntent))
        {
            return (false, string.Empty, null);
        }

        try
        {
            var answer = TryRenderLastDeterministicAnswer(language);
            if (string.IsNullOrWhiteSpace(answer))
                return (false, string.Empty, null);

            onPhase?.Invoke(DeterministicAgentText.PhaseWriting(language));
            onProgress?.Invoke(DeterministicAgentText.ProgressDraftFinalAnswer(language));
            await EmitDeterministicTextAsync(answer, onDelta, ct).ConfigureAwait(false);
            return (true, answer.Trim(), null);
        }
        catch
        {
            return (false, string.Empty, null);
        }
    }

    internal static string RenderDeterministicInventoryFromData(string kind, JsonElement data, string language)
    {
        return (kind ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "list" => RenderDocumentsListFromReplayData(data, language),
            "tree" => RenderDocumentsTreeFromReplayData(data, language),
            "stats" => BuildStatsFallbackAnswer(data, language),
            "categories" => RenderCategoriesFromReplayData(data, language),
            "count" => DeterministicAgentText.DocumentsCount(TryGetInt(data, "total") ?? 0, language),
            "empty_count" => DeterministicAgentText.EmptyFoldersCount(TryGetInt(data, "total") ?? 0, language),
            "empty_list" => RenderEmptyFoldersListFromReplayData(data, language),
            "summary_status_count" => RenderSummaryStatusCountFromReplayData(data, language),
            "summary_status_list" => RenderSummaryStatusListFromReplayData(data, language),
            "extraction_quality" => RenderExtractionQualityFromReplayData(data, language),
            "extraction_pages" => RenderExtractionPagesFromReplayData(data, language),
            "diagnostic_performance" => RenderDiagnosticPerformanceFromReplayData(data, language),
            _ => string.Empty
        };
    }

    private static string RenderCategoriesFromReplayData(JsonElement data, string language)
    {
        if (!data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return DeterministicAgentText.CategoriesCount(TryGetInt(data, "total") ?? 0, language);

        var rows = new List<string>();
        foreach (var entry in items.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var ordinal = TryGetInt(entry, "ordinal") ?? TryGetInt(entry, "displayOrder") ?? 0;
            var name = TryGetString(entry, "name") ?? string.Empty;
            var totalDocuments = TryGetInt(entry, "totalDocuments") ?? 0;
            rows.Add($"{ordinal}. {name} ({totalDocuments})");
        }

        if (rows.Count == 0)
            return DeterministicAgentText.CategoriesCount(TryGetInt(data, "total") ?? 0, language);

        var sb = new StringBuilder();
        sb.AppendLine(DeterministicAgentText.CategoriesListHeader(language));
        foreach (var row in rows)
            sb.AppendLine(row);

        return sb.ToString().TrimEnd();
    }

    private static string RenderExtractionQualityFromReplayData(JsonElement data, string language)
    {
        var summary = data.TryGetProperty("summary", out var summaryElement) && summaryElement.ValueKind == JsonValueKind.Object
            ? summaryElement
            : default;
        var totalDocuments = TryGetInt(summary, "totalDocuments") ?? 0;
        var sb = new StringBuilder();
        sb.AppendLine(DeterministicAgentText.ExtractionQualityHeader(language));
        var scopePath = TryGetString(data, "scopePath") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(scopePath))
            sb.AppendLine(DeterministicAgentText.ExtractionScope(scopePath, language));

        sb.AppendLine(DeterministicAgentText.ExtractionQualitySummary(
            totalDocuments,
            TryGetInt(summary, "okDocuments") ?? 0,
            TryGetInt(summary, "lowTextDocuments") ?? 0,
            TryGetInt(summary, "emptyTextDocuments") ?? 0,
            TryGetInt(summary, "unknownDocuments") ?? 0,
            TryGetInt(summary, "ocrRecommendedDocuments") ?? 0,
            TryGetInt(summary, "ocrAppliedDocuments") ?? 0,
            TryGetInt(summary, "manualReviewRecommendedDocuments") ?? 0,
            TryGetInt(summary, "pageWarningPages") ?? 0,
            language));

        var rejectedChunkDocs = TryGetInt(summary, "documentsWithRejectedChunks") ?? 0;
        var noSearchableChunkDocs = TryGetInt(summary, "documentsWithNoSearchableChunks") ?? 0;
        var retrievalReviewDocs = TryGetInt(summary, "documentsWithRetrievalReviewRecommended") ?? 0;
        if (rejectedChunkDocs > 0 || noSearchableChunkDocs > 0 || retrievalReviewDocs > 0)
        {
            sb.AppendLine(DeterministicAgentText.ExtractionRetrievalChunkSummary(
                rejectedChunkDocs,
                noSearchableChunkDocs,
                retrievalReviewDocs,
                language));
        }

        if (data.TryGetProperty("categories", out var categories) && categories.ValueKind == JsonValueKind.Array && categories.GetArrayLength() > 0)
        {
            sb.AppendLine(DeterministicAgentText.ExtractionCategoriesHeader(language));
            foreach (var category in categories.EnumerateArray().Where(static x => x.ValueKind == JsonValueKind.Object).Take(12))
            {
                var categoryPath = TryGetString(category, "categoryPath") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(categoryPath))
                    continue;

                sb.AppendLine("- " + DeterministicAgentText.ExtractionCategorySummary(
                    categoryPath,
                    TryGetInt(category, "totalDocuments") ?? 0,
                    TryGetInt(category, "lowTextDocuments") ?? 0,
                    TryGetInt(category, "emptyTextDocuments") ?? 0,
                    TryGetInt(category, "ocrRecommendedDocuments") ?? 0,
                    TryGetInt(category, "manualReviewRecommendedDocuments") ?? 0,
                    TryGetInt(category, "pageWarningPages") ?? 0,
                    language));
            }
        }

        if (!data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
        {
            sb.AppendLine(DeterministicAgentText.ExtractionNoDocuments(language));
            return sb.ToString().TrimEnd();
        }

        var index = 1;
        foreach (var entry in items.EnumerateArray().Where(static x => x.ValueKind == JsonValueKind.Object).Take(20))
        {
            var path = (TryGetString(entry, "docPath") ?? string.Empty).Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrWhiteSpace(path))
                continue;

            var label = SanitizeOpenTokenLabel(path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? path);
            var parts = new List<string>();
            AddNonEmptyPart(parts, DeterministicAgentText.ExtractionStatusLabel(TryGetString(entry, "qualityStatus"), language));
            AddNonEmptyPart(parts, DeterministicAgentText.ExtractionDocumentStatus(TryGetString(entry, "documentStatus"), language));
            AddNonEmptyPart(parts, DeterministicAgentText.ExtractionProcessingRunStatus(TryGetString(entry, "processingRunStatus"), language));
            if (TryGetBool(entry, "documentIndexable") == false)
                AddNonEmptyPart(parts, DeterministicAgentText.ExtractionDocumentNotIndexable(language));
            AddNonEmptyPart(parts, DeterministicAgentText.ExtractionFailureReason(TryGetString(entry, "failureReason"), language));
            AddNonEmptyPart(parts, DeterministicAgentText.ExtractionOcrFailureReason(TryGetString(entry, "ocrFailureReason"), language));
            AddNonEmptyPart(parts, FormatPercentPart(DeterministicAgentText.ExtractionConfidenceLabel(language), TryGetDouble(entry, "extractionConfidence")));
            AddNonEmptyPart(parts, FormatCodePart(DeterministicAgentText.ExtractionSourceLabel(language), TryGetString(entry, "extractionSource")));
            AddNonEmptyPart(parts, FormatPageRatioPart(TryGetInt(entry, "textPageCount"), TryGetInt(entry, "pageCount"), language));
            AddNonEmptyPart(parts, FormatCodePart(DeterministicAgentText.ExtractionNativeTextLabel(language), TryGetString(entry, "nativeTextStatus")));
            AddNonEmptyPart(parts, FormatCodePart(DeterministicAgentText.ExtractionTextStatusLabel(language), TryGetString(entry, "textStatus")));
            AddNonEmptyPart(parts, FormatCodePart(DeterministicAgentText.ExtractionOcrLanguagesLabel(language), TryGetString(entry, "ocrLanguages")));
            AddNonEmptyPart(parts, FormatDurationPart(DeterministicAgentText.ExtractionOcrDurationLabel(language), TryGetLong(entry, "ocrDurationMs")));

            if (TryGetBool(entry, "ocrApplied") == true)
                AddNonEmptyPart(parts, DeterministicAgentText.ExtractionOcrApplied(language));
            else if (TryGetBool(entry, "ocrRecommended") == true)
                AddNonEmptyPart(parts, DeterministicAgentText.ExtractionOcrRecommended(language));
            if (TryGetBool(entry, "manualReviewRecommended") == true)
                AddNonEmptyPart(parts, DeterministicAgentText.ExtractionReviewRecommended(language));

            var warningPages = TryGetInt(entry, "pageWarningCount") ?? 0;
            var reviewPages = TryGetInt(entry, "pageReviewRecommendedCount") ?? 0;
            if (warningPages > 0 || reviewPages > 0)
                AddNonEmptyPart(parts, DeterministicAgentText.ExtractionPageIssues(warningPages, reviewPages, language));
            AddNonEmptyPart(parts, FormatRetrievalChunkQualityPart(entry, language));
            AddNonEmptyPart(parts, FormatWordStatsPart(
                TryGetInt(entry, "totalWordCount"),
                TryGetDouble(entry, "averageWordsPerPage"),
                TryGetDouble(entry, "textPageRatio"),
                language));
            AddNonEmptyPart(parts, FormatArrayPart(DeterministicAgentText.ExtractionSignalsLabel(language), entry, "signals", 4, 72));

            var suffix = parts.Count == 0 ? string.Empty : $" - {string.Join("; ", parts)}";
            sb.AppendLine($"{index++}. [[open|{path}|1|{label}]]{suffix}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string? FormatRetrievalChunkQualityPart(JsonElement entry, string? language)
    {
        var quality = TryGetObject(entry, "retrievalChunkQuality")
                      ?? TryGetObject(entry, "RetrievalChunkQuality")
                      ?? TryGetObject(entry, "retrieval_chunk_quality");
        var total = quality.HasValue
            ? TryGetInt(quality.Value, "totalChunkCount") ?? TryGetInt(quality.Value, "TotalChunkCount")
            : TryGetInt(entry, "retrievalTotalChunkCount") ?? TryGetInt(entry, "RetrievalTotalChunkCount");
        var searchable = quality.HasValue
            ? TryGetInt(quality.Value, "searchableChunkCount") ?? TryGetInt(quality.Value, "SearchableChunkCount")
            : TryGetInt(entry, "retrievalSearchableChunkCount") ?? TryGetInt(entry, "RetrievalSearchableChunkCount");
        var rejected = quality.HasValue
            ? TryGetInt(quality.Value, "rejectedChunkCount") ?? TryGetInt(quality.Value, "RejectedChunkCount")
            : TryGetInt(entry, "retrievalRejectedChunkCount") ?? TryGetInt(entry, "RetrievalRejectedChunkCount");
        var review = (quality.HasValue
            ? TryGetBool(quality.Value, "manualReviewRecommended") ?? TryGetBool(quality.Value, "ManualReviewRecommended")
            : TryGetBool(entry, "retrievalManualReviewRecommended") ?? TryGetBool(entry, "RetrievalManualReviewRecommended")) == true;

        if (!total.HasValue && !searchable.HasValue && !rejected.HasValue && !review)
            return null;

        var reasonParts = new List<string>();
        if (quality.HasValue)
        {
            foreach (var pair in ExtractCompactIntMap(quality.Value, "rejectionReasons")
                         .Concat(ExtractCompactIntMap(quality.Value, "RejectionReasons"))
                         .GroupBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                         .Select(static group => new { Reason = group.Key, Count = group.Sum(static pair => pair.Value) })
                         .OrderByDescending(static pair => pair.Count)
                         .ThenBy(static pair => pair.Reason, StringComparer.OrdinalIgnoreCase)
                         .Take(3))
            {
                reasonParts.Add($"{ShortenInventoryValue(pair.Reason, 36)}={pair.Count}");
            }
        }

        return DeterministicAgentText.ExtractionRetrievalChunkQuality(
            total,
            searchable,
            rejected,
            review,
            reasonParts.Count == 0 ? null : string.Join(", ", reasonParts),
            language);
    }

    private static string RenderExtractionPagesFromReplayData(JsonElement data, string language)
    {
        var docPath = (TryGetString(data, "docPath") ?? string.Empty).Replace('\\', '/').TrimStart('/');
        var docLabel = string.IsNullOrWhiteSpace(docPath)
            ? TryGetString(data, "docId") ?? string.Empty
            : docPath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? docPath;
        var summary = data.TryGetProperty("summary", out var summaryElement) && summaryElement.ValueKind == JsonValueKind.Object
            ? summaryElement
            : default;

        var sb = new StringBuilder();
        sb.AppendLine(DeterministicAgentText.ExtractionPagesHeader(docLabel, language));
        sb.AppendLine(DeterministicAgentText.ExtractionPagesSummary(
            TryGetInt(summary, "pageCount") ?? 0,
            TryGetInt(summary, "manualReviewRecommendedPages") ?? 0,
            TryGetInt(summary, "probableOcrNoisePages") ?? 0,
            TryGetInt(summary, "emptyTextPages") ?? 0,
            TryGetInt(summary, "lowTextPages") ?? 0,
            TryGetInt(summary, "imagePages") ?? 0,
            language));
        var docParts = new List<string>();
        AddNonEmptyPart(docParts, DeterministicAgentText.ExtractionDocumentStatus(TryGetString(data, "documentStatus"), language));
        AddNonEmptyPart(docParts, DeterministicAgentText.ExtractionProcessingRunStatus(TryGetString(data, "processingRunStatus"), language));
        if (TryGetBool(data, "documentIndexable") == false)
            AddNonEmptyPart(docParts, DeterministicAgentText.ExtractionDocumentNotIndexable(language));
        AddNonEmptyPart(docParts, DeterministicAgentText.ExtractionFailureReason(TryGetString(data, "failureReason"), language));
        AddNonEmptyPart(docParts, DeterministicAgentText.ExtractionOcrFailureReason(TryGetString(data, "ocrFailureReason"), language));
        AddNonEmptyPart(docParts, DeterministicAgentText.ExtractionOcrAppliedReason(TryGetString(data, "ocrAppliedReason"), language));
        AddNonEmptyPart(docParts, DeterministicAgentText.ExtractionOcrMode(TryGetString(data, "ocrMode"), language));
        if (docParts.Count > 0)
            sb.AppendLine("- " + string.Join("; ", docParts));

        if (!data.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array || pages.GetArrayLength() == 0)
        {
            sb.AppendLine(DeterministicAgentText.ExtractionNoPages(language));
            return sb.ToString().TrimEnd();
        }

        var selectedPages = pages.EnumerateArray()
            .Where(static x => x.ValueKind == JsonValueKind.Object)
            .OrderByDescending(static page => TryGetBool(page, "manualReviewRecommended") == true)
            .ThenByDescending(static page => TryGetBool(page, "ocrCandidate") == true)
            .ThenByDescending(static page => TryGetInt(page, "suspiciousUnitCount") ?? 0)
            .ThenBy(static page => TryGetInt(page, "pageNumber") ?? int.MaxValue)
            .Take(25)
            .ToArray();

        foreach (var page in selectedPages)
        {
            var pageNumber = Math.Max(1, TryGetInt(page, "pageNumber") ?? 1);
            var pageLabel = $"{SourceBackedPagePrefix(language)}{pageNumber}";
            var label = SanitizeOpenTokenLabel(string.IsNullOrWhiteSpace(docLabel) ? pageLabel : $"{docLabel} {pageLabel}");
            var parts = new List<string>();
            AddNonEmptyPart(parts, DeterministicAgentText.ExtractionStatusLabel(TryGetString(page, "qualityStatus"), language));
            AddNonEmptyPart(parts, FormatPercentPart(DeterministicAgentText.ExtractionConfidenceLabel(language), TryGetDouble(page, "extractionConfidence")));
            AddNonEmptyPart(parts, DeterministicAgentText.ExtractionPageCounters(
                TryGetInt(page, "wordCount") ?? 0,
                TryGetInt(page, "charCount") ?? 0,
                TryGetInt(page, "imageCount") ?? 0,
                TryGetInt(page, "chunkCount") ?? 0,
                language));
            AddNonEmptyPart(parts, FormatCodePart(DeterministicAgentText.ExtractionTextStatusLabel(language), TryGetString(page, "textStatus")));
            AddNonEmptyPart(parts, FormatCodePart(DeterministicAgentText.ExtractionImageOcrStatusLabel(language), TryGetString(page, "imageOcrStatus")));
            AddNonEmptyPart(parts, FormatCodePart(DeterministicAgentText.ExtractionOcrReasonLabel(language), TryGetString(page, "imageOcrReason")));
            AddNonEmptyPart(parts, FormatCodePart(DeterministicAgentText.ExtractionOcrExitCodeLabel(language), TryGetInt(page, "imageOcrExitCode")?.ToString(CultureInfo.InvariantCulture)));
            AddNonEmptyPart(parts, FormatArrayPart(DeterministicAgentText.ExtractionSignalsLabel(language), page, "signals", 5, 80));
            AddNonEmptyPart(parts, FormatArrayPart(DeterministicAgentText.ExtractionPreviewLabel(language), page, "unitPreviews", 2, 96)
                ?? FormatArrayPart(DeterministicAgentText.ExtractionPreviewLabel(language), page, "chunkPreviews", 2, 96));

            if (TryGetBool(page, "imageOcrTimedOut") == true)
                AddNonEmptyPart(parts, DeterministicAgentText.ExtractionOcrTimedOut(language));
            if (TryGetBool(page, "ocrCandidate") == true)
                AddNonEmptyPart(parts, DeterministicAgentText.ExtractionOcrCandidate(language));
            if (TryGetBool(page, "manualReviewRecommended") == true)
                AddNonEmptyPart(parts, DeterministicAgentText.ExtractionReviewRecommended(language));

            var open = string.IsNullOrWhiteSpace(docPath)
                ? $"p.{pageNumber}"
                : $"[[open|{docPath}|{pageNumber}|{label}]]";
            sb.AppendLine($"- {open} - {string.Join("; ", parts.Where(static part => !string.IsNullOrWhiteSpace(part)))}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string RenderDocumentsTreeFromReplayData(JsonElement data, string language)
    {
        var markdown = TryGetString(data, "markdown") ?? string.Empty;
        return string.IsNullOrWhiteSpace(markdown) ? LocalizedStrings.NoDocumentsFound(language) : markdown.Trim();
    }

    private static string RenderDocumentsListFromReplayData(JsonElement data, string language)
    {
        if (!data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return LocalizedStrings.NoDocumentsFound(language);

        var lines = new List<string>();
        var i = 1;
        foreach (var entry in items.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var docPath = (TryGetString(entry, "docPath") ?? string.Empty).Replace('\\', '/').TrimStart('/');
            var docName = TryGetString(entry, "docName") ?? string.Empty;
            var categoryPath = TryGetString(entry, "categoryPath") ?? string.Empty;
            var mainCat = string.IsNullOrWhiteSpace(categoryPath) ? string.Empty : categoryPath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            var label = string.IsNullOrWhiteSpace(mainCat) ? docName : $"{docName} ({mainCat})";
            label = (label ?? string.Empty).Replace("|", " ").Replace("]", ")");

            if (!string.IsNullOrWhiteSpace(docPath) && !string.IsNullOrWhiteSpace(label))
                lines.Add($"{i++}. [[open|{docPath}|1|{label}]]");
        }

        var explicitScopePath = NormalizeCategoryPathArg(TryGetString(data, "scopePath"));
        var searchQuery = (TryGetString(data, "searchQuery") ?? string.Empty).Trim();
        var header = !string.IsNullOrWhiteSpace(explicitScopePath)
            ? DeterministicAgentText.DocumentsListHeader(language, explicitScopePath)
            : (!string.IsNullOrWhiteSpace(searchQuery)
                ? DeterministicAgentText.DocumentsSearchHeader(language, searchQuery)
                : DeterministicAgentText.DocumentsListHeader(language));

        return lines.Count == 0
            ? LocalizedStrings.NoDocumentsFound(language)
            : $"{header}{Environment.NewLine}{string.Join(Environment.NewLine, lines)}".TrimEnd();
    }

    private static string? TryInferDocumentsScopePath(JsonElement data)
    {
        var explicitScope = NormalizeCategoryPathArg(TryGetString(data, "scopePath"));
        if (!string.IsNullOrWhiteSpace(explicitScope))
            return explicitScope;

        if (!data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return null;

        string? unique = null;
        foreach (var entry in items.EnumerateArray())
        {
            var categoryPath = NormalizeCategoryPathArg(TryGetString(entry, "categoryPath"));
            if (string.IsNullOrWhiteSpace(categoryPath))
                return null;
            if (unique is null)
            {
                unique = categoryPath;
                continue;
            }

            if (!string.Equals(unique, categoryPath, StringComparison.OrdinalIgnoreCase))
                return null;
        }

        return unique;
    }

    private static string RenderEmptyFoldersListFromReplayData(JsonElement data, string language)
    {
        if (!data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return DeterministicAgentText.NoEmptyFoldersFound(language);

        var paths = new List<string>();
        foreach (var entry in items.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var path = TryGetString(entry, "path") ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(path))
                paths.Add(path);
        }

        if (paths.Count == 0)
            return DeterministicAgentText.NoEmptyFoldersFound(language);

        var sb = new StringBuilder();
        sb.AppendLine(DeterministicAgentText.EmptyFoldersHeader(language));
        for (var i = 0; i < paths.Count; i++)
            sb.AppendLine($"{i + 1}. {paths[i]}");

        return sb.ToString().TrimEnd();
    }

    private static string RenderSummaryStatusCountFromReplayData(JsonElement data, string language)
    {
        var totals = data.TryGetProperty("totals", out var totalsElement) && totalsElement.ValueKind == JsonValueKind.Object
            ? totalsElement
            : default;
        var total = TryGetInt(data, "total") ?? (totals.ValueKind == JsonValueKind.Object ? TryGetInt(totals, "total") : null) ?? 0;
        var profileMissing = TryGetInt(data, "profileMissing") ?? (totals.ValueKind == JsonValueKind.Object ? TryGetInt(totals, "profileMissing") : null) ?? 0;
        var mode = TryGetString(data, "mode") ?? "missing";
        if (string.Equals(mode, "present", StringComparison.OrdinalIgnoreCase))
        {
            return total <= 0
                ? DeterministicAgentText.NoStoredSummaries(language)
                : DeterministicAgentText.StoredSummariesCount(total, language);
        }

        if (profileMissing > 0 && total > 0)
            return DeterministicAgentText.MissingSummariesCount(total, language)
                + Environment.NewLine
                + DeterministicAgentText.BackofficeProfilesMissingCount(profileMissing, language);

        return total <= 0
            ? DeterministicAgentText.NoMissingSummaries(language)
            : DeterministicAgentText.MissingSummariesCount(total, language);
    }

    private static string RenderSummaryStatusListFromReplayData(JsonElement data, string language)
    {
        var hasItems = data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array;
        if (!hasItems)
            hasItems = data.TryGetProperty("value", out items) && items.ValueKind == JsonValueKind.Array;
        if (!hasItems)
            return string.Equals(TryGetString(data, "mode"), "present", StringComparison.OrdinalIgnoreCase)
                ? DeterministicAgentText.NoStoredSummaries(language)
                : DeterministicAgentText.NoMissingSummaries(language);

        var rows = new List<(string path, string label, string suffix)>();
        foreach (var entry in items.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var path = (TryGetString(entry, "docPath") ?? string.Empty).Replace('\\', '/').TrimStart('/');
            var state = TryGetString(entry, "summaryState") ?? string.Empty;
            var profileState = TryGetString(entry, "capabilityBProfileState") ?? TryGetString(entry, "CapabilityBProfileState") ?? string.Empty;
            var hasBackofficeProfile = TryGetBool(entry, "capabilityBHasBackofficeProfile") ?? TryGetBool(entry, "CapabilityBHasBackofficeProfile");
            var profileMissing = string.Equals(profileState, "missing", StringComparison.OrdinalIgnoreCase)
                || (hasBackofficeProfile.HasValue && !hasBackofficeProfile.Value && HasReason(entry, "profile_missing"));
            if (string.IsNullOrWhiteSpace(path))
                continue;

            var label = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? path;
            label = (label ?? string.Empty).Replace("|", " ").Replace("]", ")");
            var suffixes = new List<string>();
            if (state.Equals("stale", StringComparison.OrdinalIgnoreCase))
                suffixes.Add(DeterministicAgentText.SummaryStatusStaleSuffix(language));
            if (profileMissing)
                suffixes.Add(DeterministicAgentText.BackofficeProfileMissingSuffix(language));
            if (TryGetBool(entry, "hasActiveSummaryJob") == true || TryGetBool(entry, "HasActiveSummaryJob") == true)
                suffixes.Add(DeterministicAgentText.SummaryStatusActiveJobSuffix(
                    TryGetString(entry, "activeSummaryJobStatus") ?? TryGetString(entry, "ActiveSummaryJobStatus"),
                    language));
            if (TryGetBool(entry, "capabilityBPolicyBlocked") == true || TryGetBool(entry, "CapabilityBPolicyBlocked") == true)
                suffixes.Add(DeterministicAgentText.SummaryStatusPolicyBlockedSuffix(
                    TryGetString(entry, "capabilityBPolicyBlockReason") ?? TryGetString(entry, "CapabilityBPolicyBlockReason"),
                    language));
            else if (TryGetBool(entry, "capabilityBReadyToEnqueue") == true || TryGetBool(entry, "CapabilityBReadyToEnqueue") == true)
                suffixes.Add(DeterministicAgentText.SummaryStatusCapabilityActionSuffix(
                    TryGetString(entry, "capabilityBRecommendedAction") ?? TryGetString(entry, "CapabilityBRecommendedAction"),
                    language));

            var lastJobStatus = TryGetString(entry, "capabilityBLastJobStatus") ?? TryGetString(entry, "CapabilityBLastJobStatus");
            var lastJobError = TryGetString(entry, "capabilityBLastJobError") ?? TryGetString(entry, "CapabilityBLastJobError");
            if (!string.IsNullOrWhiteSpace(lastJobStatus)
                && (lastJobStatus.Contains("fail", StringComparison.OrdinalIgnoreCase)
                    || lastJobStatus.Contains("error", StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrWhiteSpace(lastJobError)))
            {
                suffixes.Add(DeterministicAgentText.SummaryStatusLastJobIssueSuffix(lastJobStatus, lastJobError, language));
            }

            var priority = TryGetDouble(entry, "capabilityBPriorityScore") ?? TryGetDouble(entry, "CapabilityBPriorityScore");
            if (priority is > 0)
                suffixes.Add(DeterministicAgentText.SummaryStatusPrioritySuffix(priority.Value, language));

            rows.Add((path, label, string.Join(" ", suffixes.Select(static suffix => $"[{suffix}]"))));
        }

        var mode = TryGetString(data, "mode") ?? "missing";
        if (rows.Count == 0)
            return string.Equals(mode, "present", StringComparison.OrdinalIgnoreCase)
                ? DeterministicAgentText.NoStoredSummaries(language)
                : DeterministicAgentText.NoMissingSummaries(language);

        var sb = new StringBuilder();
        sb.AppendLine(string.Equals(mode, "present", StringComparison.OrdinalIgnoreCase)
            ? DeterministicAgentText.StoredSummariesHeader(language)
            : DeterministicAgentText.MissingSummariesHeader(language));
        for (var i = 0; i < rows.Count; i++)
        {
            var suffix = string.IsNullOrWhiteSpace(rows[i].suffix) ? string.Empty : $" {rows[i].suffix}";
            sb.AppendLine($"{i + 1}. [[open|{rows[i].path}|1|{rows[i].label}]]{suffix}");
        }

        return sb.ToString().TrimEnd();
    }

    private static bool HasReason(JsonElement entry, string reason)
    {
        foreach (var propertyName in new[] { "capabilityBReasons", "CapabilityBReasons" })
        {
            if (!entry.TryGetProperty(propertyName, out var reasons) || reasons.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var item in reasons.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String
                    && string.Equals(item.GetString(), reason, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static void AddNonEmptyPart(List<string> parts, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            parts.Add(value.Trim());
    }

    private static string? FormatPercentPart(string label, double? value)
    {
        if (!value.HasValue)
            return null;

        var percent = value.Value <= 1.0
            ? value.Value * 100.0
            : value.Value;
        return $"{label} {Math.Round(percent)} %";
    }

    private static string? FormatCodePart(string label, string? value)
    {
        value = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(value) ? null : $"{label} {value}";
    }

    private static string? FormatPageRatioPart(int? textPages, int? pages, string? language)
    {
        if (!textPages.HasValue || !pages.HasValue || pages.Value <= 0)
            return null;

        return DeterministicAgentText.ExtractionTextPages(textPages.Value, pages.Value, language);
    }

    private static string? FormatDurationPart(string label, long? milliseconds)
    {
        if (!milliseconds.HasValue || milliseconds.Value <= 0)
            return null;

        var value = milliseconds.Value < 1000
            ? $"{milliseconds.Value.ToString(CultureInfo.InvariantCulture)}ms"
            : $"{(milliseconds.Value / 1000d).ToString(milliseconds.Value < 10000 ? "0.#" : "0", CultureInfo.InvariantCulture)}s";
        return $"{label} {value}";
    }

    private static string? FormatWordStatsPart(int? totalWords, double? averageWordsPerPage, double? textPageRatio, string? language)
    {
        if (!totalWords.HasValue || totalWords.Value <= 0)
            return null;

        return DeterministicAgentText.ExtractionWordStats(totalWords.Value, averageWordsPerPage, textPageRatio, language);
    }

    private static string? FormatArrayPart(string label, JsonElement entry, string propertyName, int maxItems, int maxChars)
    {
        if (entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty(propertyName, out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = array.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString()?.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxItems))
            .Select(value => ShortenInventoryValue(value!, maxChars))
            .ToArray();

        return values.Length == 0 ? null : $"{label} {string.Join(", ", values)}";
    }

    private static string ShortenInventoryValue(string value, int maxChars)
    {
        value = Regex.Replace(value.Trim(), @"\s+", " ");
        return value.Length <= maxChars
            ? value
            : value[..Math.Max(0, maxChars - 3)] + "...";
    }

    // ---------------- Tools exec helpers ----------------

    private async Task<JsonElement> ExecDocumentsListAsync(JsonElement args, CancellationToken ct)
    {
        var (categoryPath, categoryRef) = await ResolveCategoryScopeArgsAsync(args, ct).ConfigureAwait(false);
        var q = args.TryGetProperty("q", out var qj) && qj.ValueKind != JsonValueKind.Null ? qj.GetString() : null;
        var changedSince = ParseChangedSinceArg(GetStringArg(args, "changedSince"));
        var limit = args.TryGetProperty("limit", out var l) ? l.GetInt32() : 80;
        var offset = args.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;

        var res = await _api.DocumentsListAsync(categoryPath, categoryRef, q, changedSince, limit, offset, ct);
        res = ApplySpecificDocumentQueryGuard(res, q);

        _mem.LastListCategoryPath = categoryPath;
        _mem.LastListQuery = q;

        // Sanitize against filesystem + update PDFxx mapping (robust against moves/renames)
        var sanitized = DocumentListHelper.Sanitize(res, _mem);
        if (sanitized.docs.Count == 0)
        {
            _mem.LastFocusedDocument = null;
            _mem.LastRequestedDocumentRef = null;
        }

        return res;
    }

    private static DateTimeOffset? ParseChangedSinceArg(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return DateTimeOffset.TryParse(raw.Trim(), out var parsed)
            ? parsed
            : null;
    }

    private async Task<JsonElement> ExecDocumentsSearchAsync(JsonElement args, CancellationToken ct)
    {
        var q = args.GetProperty("q").GetString() ?? "";
        var (categoryPath, categoryRef) = await ResolveCategoryScopeArgsAsync(args, ct).ConfigureAwait(false);
        var limit = args.TryGetProperty("limit", out var l) ? l.GetInt32() : 80;
        var offset = args.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;

        var res = await _api.DocumentsSearchAsync(q, categoryPath, categoryRef, limit, offset, ct);
        res = ApplySpecificDocumentQueryGuard(res, q);

        _mem.LastListCategoryPath = categoryPath;
        _mem.LastListQuery = q;

        var sanitized = DocumentListHelper.Sanitize(res, _mem);
        if (sanitized.docs.Count == 0)
        {
            _mem.LastFocusedDocument = null;
            _mem.LastRequestedDocumentRef = null;
        }

        return res;
    }

    private async Task<JsonElement> ExecDocumentsGetAsync(JsonElement args, CancellationToken ct)
    {
        return await ExecDocumentsGetResolvedAsync(args, ct);
    }

    private async Task<JsonElement> ExecRagSearchAsync(JsonElement args, CancellationToken ct)
    {
        var query = ResolveRagSearchExecutionQuery(args.GetProperty("query").GetString() ?? "");
        var topK = args.TryGetProperty("topK", out var k) ? k.GetInt32() : 8;
        var categoryScope = GetRagCategoryScopeArg(args);
        var docId = GetRagDocIdArg(args);
        var docPath = GetRagDocPathArg(args);
        var maxPerDoc = GetRagMaxPerDocArg(args);
        var maxPerPage = GetRagMaxPerPageArg(args);
        var pageStart = GetRagPageStartArg(args);
        var pageEnd = GetRagPageEndArg(args);
        var researchMode = GetRagResearchModeArg(args);
        var includeResearchSurfaces = GetRagIncludeResearchSurfacesArg(args);
        var mode = args.TryGetProperty("mode", out var m) && m.ValueKind != JsonValueKind.Null ? m.GetString() : "balanced";
        var sw = Stopwatch.StartNew();
        EmitRagTrace(
            "rag.search.start",
            ("query", query),
            ("top_k", topK),
            ("mode", mode),
            ("category", categoryScope),
            ("doc_id", docId),
            ("doc_path", docPath),
            ("page_start", pageStart),
            ("page_end", pageEnd),
            ("research_mode", researchMode),
            ("include_research_surfaces", includeResearchSurfaces));

        if (LooksLikeComparativeDocumentaryRequest(query))
        {
            EmitRagTrace(
                "rag.search.redirect",
                ("reason", "comparative_documentary_request"),
                ("target", "rag.multi_search"),
                ("query", query));
            var multiArgs = CreateJsonArgs(new
            {
                queries = BuildComparativeRetrievalQueries(query),
                topK,
                categoryPath = categoryScope,
                docId,
                docPath,
                maxPerDoc,
                maxPerPage,
                pageStart,
                pageEnd,
                mode,
                researchMode,
                includeResearchSurfaces
            });
            return await ExecRagMultiSearchAsync(multiArgs, ct).ConfigureAwait(false);
        }

        JsonElement raw;
        try
        {
            raw = await _api.RagSearchToolAsync(
                query,
                topK,
                categoryScope,
                mode,
                ct,
                docId,
                docPath,
                maxPerDoc,
                maxPerPage,
                pageStart,
                pageEnd,
                researchMode,
                includeResearchSurfaces).ConfigureAwait(false);
        }
        catch (ApiClientBackendBusyException ex) when (!ct.IsCancellationRequested)
        {
            var busy = BuildRagSearchBusyPayload(new[] { query }, categoryScope, mode, ex);
            RememberLastRagDiagnostics(new[] { query }, busy);
            EmitRagTrace(
                "rag.search.end",
                ("query", query),
                ("hits", 0),
                ("busy", true),
                ("error", "rag_search_busy"),
                ("retry_after_seconds", ex.RetryAfterSeconds),
                ("ms", sw.ElapsedMilliseconds));
            return busy;
        }

        var normalized = NormalizeRagHits(raw);
        RememberLastRagDiagnostics(new[] { query }, normalized);
        EmitRagTrace(
            "rag.search.end",
            ("query", query),
            ("hits", CountRagHits(normalized)),
            ("busy", IsRagSearchBusyPayload(normalized)),
            ("error", TryGetString(normalized, "error")),
            ("ms", sw.ElapsedMilliseconds));
        return normalized;
    }

    private static string ResolveRagSearchExecutionQuery(string? rawQuery)
    {
        var raw = CollapseWhitespace(rawQuery ?? string.Empty);
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        if (TryExtractDelimitedUserDemandTopic(raw, out _))
            return raw;

        var normalized = NormalizeRagQueryForRetrieval(raw);
        return string.IsNullOrWhiteSpace(normalized) ? raw : normalized;
    }

    private async Task<JsonElement> ExecRagSearchRawAsync(string query, int topK, string? categoryScope, string? mode, CancellationToken ct)
    {
        query = CollapseWhitespace(query ?? string.Empty);
        if (string.IsNullOrWhiteSpace(query))
            return JsonDocument.Parse("{\"hits\":[]}").RootElement;

        var sw = Stopwatch.StartNew();
        EmitRagTrace(
            "rag.search.raw.start",
            ("query", query),
            ("top_k", topK),
            ("mode", mode),
            ("category", categoryScope));
        JsonElement raw;
        try
        {
            raw = await _api.RagSearchToolAsync(query, topK, categoryScope, mode, ct).ConfigureAwait(false);
        }
        catch (ApiClientBackendBusyException ex) when (!ct.IsCancellationRequested)
        {
            var busy = BuildRagSearchBusyPayload(new[] { query }, categoryScope, mode, ex);
            RememberLastRagDiagnostics(new[] { query }, busy);
            EmitRagTrace(
                "rag.search.raw.end",
                ("query", query),
                ("hits", 0),
                ("busy", true),
                ("error", "rag_search_busy"),
                ("retry_after_seconds", ex.RetryAfterSeconds),
                ("ms", sw.ElapsedMilliseconds));
            return busy;
        }

        var normalized = NormalizeRagHits(raw);
        RememberLastRagDiagnostics(new[] { query }, normalized);
        EmitRagTrace(
            "rag.search.raw.end",
            ("query", query),
            ("hits", CountRagHits(normalized)),
            ("busy", IsRagSearchBusyPayload(normalized)),
            ("error", TryGetString(normalized, "error")),
            ("ms", sw.ElapsedMilliseconds));
        return normalized;
    }

    private static JsonElement BuildRagSearchBusyPayload(
        IEnumerable<string> queries,
        string? categoryScope,
        string? mode,
        ApiClientBackendBusyException ex)
    {
        var payload = new
        {
            hits = Array.Empty<object>(),
            error = "rag_search_busy",
            busy = true,
            retryAfterSeconds = ex.RetryAfterSeconds,
            guidance = new
            {
                behavior = "retry_later",
                qualificationNote = "RAG search is temporarily busy; ask the user to retry shortly instead of claiming no documents were found."
            },
            meta = new
            {
                queries = queries.Where(static q => !string.IsNullOrWhiteSpace(q)).Take(8).ToArray(),
                mode = string.IsNullOrWhiteSpace(mode) ? "balanced" : mode,
                category = categoryScope,
                categoryPath = categoryScope,
                degradedRetrievers = new[] { "rag_search_busy" }
            }
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildRagSearchQueryTimeoutPayload(
        string query,
        string? categoryScope,
        string? mode,
        TimeSpan timeout)
    {
        var payload = new
        {
            hits = Array.Empty<object>(),
            error = "rag_search_query_timeout",
            busy = false,
            guidance = new
            {
                behavior = "continue_without_timed_out_query",
                qualificationNote = "One retrieval sub-query timed out; keep usable results from other queries and avoid treating the timeout as evidence absence."
            },
            meta = new
            {
                queries = new[] { query },
                mode = string.IsNullOrWhiteSpace(mode) ? "balanced" : mode,
                category = categoryScope,
                categoryPath = categoryScope,
                timeoutMs = (int)Math.Round(timeout.TotalMilliseconds),
                degradedRetrievers = new[] { "rag_search_query_timeout" }
            }
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        return doc.RootElement.Clone();
    }

    private static TimeSpan? ResolveRagMultiSearchPerQueryTimeout(
        string? researchMode,
        bool includeResearchSurfaces)
    {
        return IsRagSourceExploration(researchMode, includeResearchSurfaces)
            ? RagMultiSearchSourceExplorationQueryTimeout
            : null;
    }

    private static bool IsRagSourceExploration(string? researchMode, bool includeResearchSurfaces)
        => includeResearchSurfaces
           || string.Equals(researchMode, "source_exploration", StringComparison.OrdinalIgnoreCase);

    private static bool IsRagCategoryScopeSupportedByQueryTerms(string? categoryScope, IReadOnlyList<string> queries)
    {
        var normalizedScope = NormalizeLexicalLookup(categoryScope ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalizedScope))
            return true;

        var categoryTokens = Regex.Matches(normalizedScope, @"[\p{L}\p{Nd}]{3,}", RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(static match => match.Value)
            .Where(static token => !IsWeakCategoryScopeToken(token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (categoryTokens.Length == 0)
            return false;

        var normalizedQueries = NormalizeLexicalLookup(string.Join(' ', queries));
        if (string.IsNullOrWhiteSpace(normalizedQueries))
            return false;

        return categoryTokens.Any(token => Regex.IsMatch(
            normalizedQueries,
            $@"(?:^|\s){Regex.Escape(token)}(?:\s|$)",
            RegexOptions.CultureInvariant));
    }

    private static bool IsWeakCategoryScopeToken(string token)
        => token is "pdf" or "doc" or "docs" or "document" or "documents" or "general" or "generale" or "misc" or "miscellaneous";

    private static string? ResolveTrustedRagMultiSearchCategoryScope(
        string? categoryScope,
        IReadOnlyList<string> queries,
        string? docId,
        string? docPath,
        int? pageStart,
        int? pageEnd,
        string? researchMode,
        bool includeResearchSurfaces,
        bool trustCategoryScope,
        out string? rejectedCategoryScope,
        out string? rejectedCategoryScopeReason)
    {
        rejectedCategoryScope = null;
        rejectedCategoryScopeReason = null;

        if (string.IsNullOrWhiteSpace(categoryScope)
            || !IsRagSourceExploration(researchMode, includeResearchSurfaces)
            || !string.IsNullOrWhiteSpace(docId)
            || !string.IsNullOrWhiteSpace(docPath)
            || pageStart.HasValue
            || pageEnd.HasValue)
        {
            return categoryScope;
        }

        if (trustCategoryScope)
            return categoryScope;

        if (IsRagCategoryScopeSupportedByQueryTerms(categoryScope, queries))
            return categoryScope;

        rejectedCategoryScope = categoryScope;
        rejectedCategoryScopeReason = "source_exploration_scope_not_supported_by_query";
        return null;
    }

    private string? TryRefineRagMultiSearchCategoryScopeFromPreviousInference(
        string? categoryScope,
        string? rejectedCategoryScope,
        string? docId,
        string? docPath,
        int? pageStart,
        int? pageEnd,
        string? researchMode,
        bool includeResearchSurfaces,
        out string? refinedFrom,
        out string? reason)
    {
        refinedFrom = null;
        reason = null;

        if (!IsRagSourceExploration(researchMode, includeResearchSurfaces)
            || !string.IsNullOrWhiteSpace(docId)
            || !string.IsNullOrWhiteSpace(docPath)
            || pageStart.HasValue
            || pageEnd.HasValue)
        {
            return null;
        }

        var effectiveCategoryScope = NormalizeCategoryPathArg(categoryScope);
        var requestedCategoryScope = effectiveCategoryScope ?? NormalizeCategoryPathArg(rejectedCategoryScope);
        var inferredCategoryScope = NormalizeCategoryPathArg(_mem.Execution.LastRagInferredCategoryScope);
        if (string.IsNullOrWhiteSpace(requestedCategoryScope)
            || string.IsNullOrWhiteSpace(inferredCategoryScope))
        {
            return null;
        }

        if (string.Equals(effectiveCategoryScope, inferredCategoryScope, StringComparison.OrdinalIgnoreCase))
            return null;

        if (string.Equals(requestedCategoryScope, inferredCategoryScope, StringComparison.OrdinalIgnoreCase)
            || inferredCategoryScope.StartsWith(requestedCategoryScope + "/", StringComparison.OrdinalIgnoreCase))
        {
            refinedFrom = requestedCategoryScope;
            reason = _mem.Execution.LastRagInferredCategoryReason ?? "previous_inferred_category";
            return inferredCategoryScope;
        }

        return null;
    }

    private void RememberRagInferredCategoryScope(string? categoryScope, string reason)
    {
        var normalizedCategoryScope = NormalizeCategoryPathArg(categoryScope);
        if (string.IsNullOrWhiteSpace(normalizedCategoryScope))
            return;

        _mem.Execution.LastRagInferredCategoryScope = normalizedCategoryScope;
        _mem.Execution.LastRagInferredCategoryReason = reason;
        EmitRagTrace(
            "rag.multi_search.category_scope.remembered",
            ("category", normalizedCategoryScope),
            ("reason", reason));
    }

    private static string? TryGetRagMultiSearchResultCategoryPath(JsonElement result)
    {
        var meta = TryGetObject(result, "meta")
                   ?? TryGetObject(result, "Meta");
        if (!meta.HasValue)
            return null;

        return NormalizeCategoryPathArg(
            TryGetString(meta.Value, "categoryPath")
            ?? TryGetString(meta.Value, "CategoryPath")
            ?? TryGetString(meta.Value, "category")
            ?? TryGetString(meta.Value, "Category"));
    }

    private static string? ChooseCommittedSourceBackedCategoryScope(string? plannedScope, string? executedScope)
    {
        var planned = NormalizeCategoryPathArg(plannedScope);
        var executed = NormalizeCategoryPathArg(executedScope);
        if (string.IsNullOrWhiteSpace(executed))
            return planned;
        if (string.IsNullOrWhiteSpace(planned))
            return executed;
        if (string.Equals(planned, executed, StringComparison.OrdinalIgnoreCase)
            || executed.StartsWith(planned + "/", StringComparison.OrdinalIgnoreCase))
        {
            return executed;
        }

        return planned;
    }

    private async Task<JsonElement> TryExecRagSearchOrEmptyAsync(JsonElement args, CancellationToken ct)
    {
        try
        {
            return await ExecRagSearchAsync(args, ct).ConfigureAwait(false);
        }
        catch when (!ct.IsCancellationRequested)
        {
            return JsonDocument.Parse("{\"hits\":[]}").RootElement;
        }
    }

    private async Task<JsonElement> TryExecRagSearchRawOrEmptyAsync(string query, int topK, string? categoryScope, string? mode, CancellationToken ct)
    {
        try
        {
            return await ExecRagSearchRawAsync(query, topK, categoryScope, mode, ct).ConfigureAwait(false);
        }
        catch when (!ct.IsCancellationRequested)
        {
            return JsonDocument.Parse("{\"hits\":[]}").RootElement;
        }
    }

    private async Task<JsonElement> TryExecRagMultiSearchOrEmptyAsync(JsonElement args, CancellationToken ct)
    {
        try
        {
            return await ExecRagMultiSearchAsync(args, ct).ConfigureAwait(false);
        }
        catch when (!ct.IsCancellationRequested)
        {
            return JsonDocument.Parse("{\"hits\":[]}").RootElement;
        }
    }

    private async Task<JsonElement> ExecRagMultiSearchAsync(JsonElement args, CancellationToken ct)
    {
        var totalSw = Stopwatch.StartNew();
        // args: { queries: string[], topK: int, category: string|null, docId/docPath: string|null, mode: ... }
        var topK = args.TryGetProperty("topK", out var k) ? k.GetInt32() : 8;
        var categoryScope = GetRagCategoryScopeArg(args);
        var docId = GetRagDocIdArg(args);
        var docPath = GetRagDocPathArg(args);
        var maxPerDoc = GetRagMaxPerDocArg(args);
        var maxPerPage = GetRagMaxPerPageArg(args);
        var pageStart = GetRagPageStartArg(args);
        var pageEnd = GetRagPageEndArg(args);
        var researchMode = GetRagResearchModeArg(args);
        var includeResearchSurfaces = GetRagIncludeResearchSurfacesArg(args);
        var trustCategoryScope = GetRagTrustCategoryScopeArg(args);
        var mode = args.TryGetProperty("mode", out var m) && m.ValueKind != JsonValueKind.Null ? m.GetString() : "balanced";

        var queries = new List<string>();
        void AddQueryCandidates(string? rawValue)
        {
            var raw = CollapseWhitespace(rawValue ?? string.Empty);
            if (string.IsNullOrWhiteSpace(raw))
                return;

            if (LooksLikeQuotedLookupQuery(raw))
            {
                AddDistinctRagQuery(queries, raw);
                return;
            }

            AddDistinctRagQuery(queries, raw);

            var normalized = NormalizeRagQueryForRetrieval(raw);
            if (!string.IsNullOrWhiteSpace(normalized)
                && !string.Equals(raw, normalized, StringComparison.OrdinalIgnoreCase))
            {
                AddDistinctRagQuery(queries, normalized);
            }
        }

        if (args.TryGetProperty("queries", out var qArr) && qArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in qArr.EnumerateArray())
            {
                if (q.ValueKind != JsonValueKind.String) continue;
                AddQueryCandidates(q.GetString());
            }
        }

        // fallback: single query
        if (queries.Count == 0 && args.TryGetProperty("query", out var q1) && q1.ValueKind == JsonValueKind.String)
        {
            AddQueryCandidates(q1.GetString());
        }

        queries = queries.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var queryBudget = ResolveRagMultiSearchQueryBudget(
            topK,
            queries.Count,
            researchMode,
            includeResearchSurfaces == true);
        var requestedCategoryScope = categoryScope;
        categoryScope = ResolveTrustedRagMultiSearchCategoryScope(
            categoryScope,
            queries,
            docId,
            docPath,
            pageStart,
            pageEnd,
            researchMode,
            includeResearchSurfaces == true,
            trustCategoryScope,
            out var rejectedCategoryScope,
            out var rejectedCategoryScopeReason);
        var categoryScopeRefinedFromPreviousInference = false;
        var refinedCategoryScope = TryRefineRagMultiSearchCategoryScopeFromPreviousInference(
            categoryScope,
            rejectedCategoryScope,
            docId,
            docPath,
            pageStart,
            pageEnd,
            researchMode,
            includeResearchSurfaces == true,
            out var categoryScopeRefinedFrom,
            out var categoryScopeRefinementReason);
        if (!string.IsNullOrWhiteSpace(refinedCategoryScope))
        {
            ClientLog.Info(
                "ToolAgent rag.multi_search category scope refined: " +
                $"from={FormatRagTraceValue(categoryScopeRefinedFrom)}|refined={FormatRagTraceValue(refinedCategoryScope)}|" +
                $"reason={FormatRagTraceValue(categoryScopeRefinementReason)}");
            EmitRagTrace(
                "rag.multi_search.scope.refined",
                ("from", categoryScopeRefinedFrom),
                ("refined", refinedCategoryScope),
                ("reason", categoryScopeRefinementReason));
            categoryScope = refinedCategoryScope;
            categoryScopeRefinedFromPreviousInference = true;
            rejectedCategoryScope = null;
            rejectedCategoryScopeReason = null;
        }
        if (!string.IsNullOrWhiteSpace(rejectedCategoryScope))
        {
            ClientLog.Info(
                "ToolAgent rag.multi_search category scope rejected: " +
                $"requested={FormatRagTraceValue(rejectedCategoryScope)}|reason={FormatRagTraceValue(rejectedCategoryScopeReason)}|" +
                $"queries={TruncateForPrompt(string.Join(" || ", queries.Select(q => FormatRagTraceValue(q, 90))), 520)}");
            EmitRagTrace(
                "rag.multi_search.scope.rejected",
                ("requested_category", rejectedCategoryScope),
                ("reason", rejectedCategoryScopeReason),
                ("queries", queries.Take(queryBudget).ToArray()));
        }
        ClientLog.Info(
            "ToolAgent rag.multi_search begin: " +
            $"queries={queries.Count}|budget={queryBudget}|topK={topK}|mode={FormatRagTraceValue(mode)}|" +
            $"category={FormatRagTraceValue(categoryScope)}|requestedCategory={FormatRagTraceValue(requestedCategoryScope)}|" +
            $"rejectedCategory={FormatRagTraceValue(rejectedCategoryScope)}|docId={FormatRagTraceValue(docId)}|docPath={FormatRagTraceValue(docPath, 180)}|" +
            $"pageStart={pageStart?.ToString(CultureInfo.InvariantCulture) ?? "-"}|pageEnd={pageEnd?.ToString(CultureInfo.InvariantCulture) ?? "-"}|" +
            $"maxPerDoc={maxPerDoc?.ToString(CultureInfo.InvariantCulture) ?? "-"}|maxPerPage={maxPerPage?.ToString(CultureInfo.InvariantCulture) ?? "-"}|" +
            $"researchMode={FormatRagTraceValue(researchMode)}|includeResearchSurfaces={includeResearchSurfaces?.ToString() ?? "-"}|" +
            $"trustCategoryScope={trustCategoryScope}");
        EmitRagTrace(
            "rag.multi_search.start",
            ("queries", queries.Count),
            ("query_budget", queryBudget),
            ("top_k", topK),
            ("mode", mode),
            ("category", categoryScope),
            ("requested_category", requestedCategoryScope),
            ("rejected_category", rejectedCategoryScope),
            ("doc_id", docId),
            ("doc_path", docPath),
            ("page_start", pageStart),
            ("page_end", pageEnd),
            ("max_per_doc", maxPerDoc),
            ("max_per_page", maxPerPage),
            ("research_mode", researchMode),
            ("include_research_surfaces", includeResearchSurfaces),
            ("trust_category_scope", trustCategoryScope));

        if (queries.Count == 0)
        {
            ClientLog.Info(
                "ToolAgent rag.multi_search end: " +
                $"hits=0|reason=no_queries|ms={totalSw.ElapsedMilliseconds}");
            EmitRagTrace(
                "rag.multi_search.end",
                ("hits", 0),
                ("reason", "no_queries"),
                ("ms", totalSw.ElapsedMilliseconds));
            return JsonDocument.Parse("{\"hits\":[]}").RootElement;
        }

        var categoryInferredSupplementalQueries = new List<string>();

        async Task<string?> TryInferCategoryScopeFromCatalogProbeAsync()
        {
            if (!string.IsNullOrWhiteSpace(categoryScope)
                || !string.IsNullOrWhiteSpace(docId)
                || !string.IsNullOrWhiteSpace(docPath)
                || pageStart.HasValue
                || pageEnd.HasValue
                || !IsRagSourceExploration(researchMode, includeResearchSurfaces == true))
            {
                return null;
            }

            var probeQueries = queries
                .Take(queryBudget)
                .Where(static query => !string.IsNullOrWhiteSpace(query))
                .Select(static (query, index) => new
                {
                    Query = query,
                    Index = index,
                    Specificity = ComputeRagMultiSearchQuerySpecificity(query),
                    Key = NormalizeLexicalLookup(query)
                })
                .Where(static item => !string.IsNullOrWhiteSpace(item.Key))
                .GroupBy(static item => item.Key, StringComparer.Ordinal)
                .Select(static group => group
                    .OrderByDescending(item => item.Specificity)
                    .ThenBy(item => item.Index)
                    .First())
                .OrderByDescending(static item => item.Specificity)
                .ThenBy(static item => item.Index)
                .Take(RagMultiSearchCategoryProbeMaxQueries)
                .OrderBy(static item => item.Index)
                .Select(static item => item.Query)
                .ToArray();
            if (probeQueries.Length == 0)
                return null;

            var normalizedQueryText = NormalizeLexicalLookup(string.Join(' ', queries));
            var knownCategories = EnumerateKnownCategories()
                .Select(category => new
                {
                    Category = category,
                    Scope = string.IsNullOrWhiteSpace(category.CategoryPath) ? category.DisplayName : category.CategoryPath,
                    LexicalScore = ComputeCategoryHintScore(category, normalizedQueryText)
                })
                .Where(static item => !string.IsNullOrWhiteSpace(item.Scope) && item.Category.TotalDocuments > 0)
                .GroupBy(static item => item.Scope, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group
                    .OrderByDescending(item => item.LexicalScore)
                    .ThenByDescending(item => item.Category.TotalDocuments)
                    .ThenBy(item => item.Category.Ordinal)
                    .First())
                .ToArray();

            if (knownCategories.Length == 0)
            {
                EmitRagTrace(
                    "rag.multi_search.category_probe.skipped",
                    ("reason", "no_catalog_categories"),
                    ("queries", probeQueries));
                return null;
            }

            var catalogHitsByScope = new Dictionary<string, RagCategoryCatalogScopeEvidence>(StringComparer.OrdinalIgnoreCase);
            var catalogProbeTermQueryMatches = probeQueries
                .SelectMany(query => ExtractQuerySignalTerms(NormalizeLexicalLookup(query)))
                .GroupBy(static term => term, StringComparer.Ordinal)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.Count(),
                    StringComparer.Ordinal);
            var catalogProbeTerms = probeQueries
                .SelectMany(query => ExtractQuerySignalTerms(NormalizeLexicalLookup(query)))
                .Where(static term => term.Length >= 4)
                .Distinct(StringComparer.Ordinal)
                .Take(RagMultiSearchCategoryCatalogProbeMaxTerms)
                .ToArray();

            string? ResolveKnownCategoryScopeFromDocument(ToolMemory.DocumentItem document)
            {
                var documentScopes = new[]
                    {
                        document.CategoryPath,
                        document.Category,
                        GuessCategoryPath(document.DocPath),
                        TryExtractTopLevelCategoryFromDocPath(document.DocPath)
                    }
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!.Replace('\\', '/').Trim('/'))
                    .SelectMany(static value =>
                    {
                        var values = new List<string> { value };
                        var first = value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(first) && !string.Equals(first, value, StringComparison.OrdinalIgnoreCase))
                            values.Add(first);
                        return values;
                    })
                    .Select(NormalizeLooseLookup)
                    .Where(static value => value.Length >= 3)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                if (documentScopes.Length == 0)
                    return null;

                foreach (var knownCategory in knownCategories)
                {
                    var names = new[]
                        {
                            knownCategory.Scope,
                            knownCategory.Category.CategoryPath,
                            knownCategory.Category.DisplayName,
                            knownCategory.Category.CategoryRef
                        }
                        .Concat(knownCategory.Category.Aliases ?? new List<string>())
                        .Where(static value => !string.IsNullOrWhiteSpace(value))
                        .Select(value => value!.Replace('\\', '/').Trim('/'))
                        .SelectMany(static value =>
                        {
                            var values = new List<string> { value };
                            var first = value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
                            if (!string.IsNullOrWhiteSpace(first) && !string.Equals(first, value, StringComparison.OrdinalIgnoreCase))
                                values.Add(first);
                            return values;
                        })
                        .Select(NormalizeLooseLookup)
                        .Where(static value => value.Length >= 3)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();

                    if (names.Any(name => documentScopes.Contains(name, StringComparer.Ordinal)))
                        return knownCategory.Scope;
                }

                return null;
            }

            static string? ResolveCatalogDocumentSpecificScope(ToolMemory.DocumentItem document)
            {
                var categoryScope = CollapseWhitespace(document.CategoryPath)
                    .Replace('\\', '/')
                    .Trim('/');
                var docScope = CollapseWhitespace(GuessCategoryPath(document.DocPath))
                    .Replace('\\', '/')
                    .Trim('/');

                if (!string.IsNullOrWhiteSpace(categoryScope)
                    && !string.IsNullOrWhiteSpace(docScope)
                    && docScope.StartsWith(categoryScope + "/", StringComparison.OrdinalIgnoreCase))
                {
                    return docScope;
                }

                if (!string.IsNullOrWhiteSpace(docScope))
                    return docScope;

                return string.IsNullOrWhiteSpace(categoryScope) ? null : categoryScope;
            }

            static string? ResolveDominantCatalogSpecificScope(string fallbackScope, RagCategoryCatalogScopeEvidence? evidence)
            {
                if (evidence is null || evidence.SpecificScopes.Count == 0)
                    return null;

                var dominant = evidence.SpecificScopes
                    .OrderByDescending(static item => item.Value)
                    .ThenBy(static item => item.Key.Length)
                    .First();
                var requiredSupport = Math.Max(2, (int)Math.Ceiling(evidence.HitCount * 0.6));
                if (dominant.Value < requiredSupport)
                    return null;

                var normalizedFallback = fallbackScope.Replace('\\', '/').Trim('/');
                var normalizedDominant = dominant.Key.Replace('\\', '/').Trim('/');
                if (string.IsNullOrWhiteSpace(normalizedDominant)
                    || string.Equals(normalizedDominant, normalizedFallback, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return normalizedDominant.StartsWith(normalizedFallback + "/", StringComparison.OrdinalIgnoreCase)
                    ? normalizedDominant
                    : null;
            }

            static bool HasStrongRepeatedCatalogTermScopeEvidence(RagCategoryScopeProbeCandidate item)
                => item.CatalogHitCount >= 3
                   && item.CatalogDominantTermQueryMatches >= 2
                   && item.CatalogRepeatedQueryMatches >= 2;

            static string FormatCategoryProbeCandidate(RagCategoryScopeProbeCandidate item)
                => string.IsNullOrWhiteSpace(item.CatalogSuggestedScope)
                    ? $"{item.Scope}:catalog={item.CatalogHitCount},terms={item.CatalogQueryMatches},repeated={item.CatalogRepeatedQueryMatches},dominant={item.CatalogDominantTermQueryMatches},lex={item.LexicalScore}"
                    : $"{item.Scope}:catalog={item.CatalogHitCount},terms={item.CatalogQueryMatches},repeated={item.CatalogRepeatedQueryMatches},dominant={item.CatalogDominantTermQueryMatches},lex={item.LexicalScore},from={item.CatalogSuggestedScope}";

            async Task<RagCategoryScopeProbeCandidate[]> ExpandCategoryProbeCandidatesWithCatalogChildrenAsync(
                IReadOnlyList<RagCategoryScopeProbeCandidate> baseCategories)
            {
                var parentCandidates = baseCategories
                    .Where(static item => item.CatalogHitCount > 0)
                    .Where(static item => !string.IsNullOrWhiteSpace(item.Scope))
                    .Take(RagMultiSearchCategoryChildProbeMaxParents)
                    .ToArray();
                if (parentCandidates.Length == 0)
                    return baseCategories.ToArray();

                var childSw = Stopwatch.StartNew();
                var expanded = baseCategories.ToList();
                var seen = new HashSet<string>(
                    expanded.Select(static item => item.Scope),
                    StringComparer.OrdinalIgnoreCase);
                var errors = 0;
                var added = new List<string>();

                EmitRagTrace(
                    "rag.multi_search.category_child_probe.start",
                    ("parents", parentCandidates.Select(static item => item.Scope).ToArray()),
                    ("limit", RagMultiSearchCategoryChildProbeLimit));

                foreach (var parent in parentCandidates)
                {
                    try
                    {
                        using var childTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        childTimeoutCts.CancelAfter(RagMultiSearchCategoryCatalogProbeQueryTimeout);
                        var raw = await _api.DocumentsCategoriesAsync(
                            parent.Scope,
                            null,
                            RagMultiSearchCategoryChildProbeLimit,
                            0,
                            childTimeoutCts.Token).ConfigureAwait(false);
                        var children = ParseCategoriesFromCatalogJson(raw);

                        foreach (var child in children)
                        {
                            var childScope = CollapseWhitespace(
                                string.IsNullOrWhiteSpace(child.CategoryPath)
                                    ? child.DisplayName
                                    : child.CategoryPath);
                            childScope = childScope.Replace('\\', '/').Trim('/');
                            if (string.IsNullOrWhiteSpace(childScope)
                                || string.Equals(childScope, parent.Scope, StringComparison.OrdinalIgnoreCase)
                                || !childScope.StartsWith(parent.Scope.Trim('/').Replace('\\', '/') + "/", StringComparison.OrdinalIgnoreCase)
                                || !seen.Add(childScope))
                            {
                                continue;
                            }

                            expanded.Add(new RagCategoryScopeProbeCandidate(
                                child,
                                childScope,
                                ComputeCategoryHintScore(child, normalizedQueryText),
                                parent.CatalogHitCount,
                                parent.CatalogQueryMatches,
                                parent.CatalogRepeatedQueryMatches,
                                parent.CatalogDominantTermQueryMatches,
                                childScope));
                            added.Add(childScope);
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        errors++;
                    }
                    catch (Exception) when (!ct.IsCancellationRequested)
                    {
                        errors++;
                    }
                }

                EmitRagTrace(
                    "rag.multi_search.category_child_probe.end",
                    ("parents", parentCandidates.Length),
                    ("added", added.Take(12).ToArray()),
                    ("added_count", added.Count),
                    ("errors", errors),
                    ("ms", childSw.ElapsedMilliseconds));

                return expanded
                    .OrderByDescending(static item => item.CatalogQueryMatches)
                    .ThenByDescending(static item => item.CatalogRepeatedQueryMatches)
                    .ThenByDescending(static item => item.CatalogDominantTermQueryMatches)
                    .ThenByDescending(static item => item.CatalogHitCount)
                    .ThenByDescending(static item => item.LexicalScore)
                    .ThenByDescending(static item => item.Category.TotalDocuments)
                    .ThenBy(static item => item.Category.Ordinal)
                    .Take(RagMultiSearchCategoryProbeMaxScopes)
                    .ToArray();
            }

            if (catalogProbeTerms.Length > 0)
            {
                var catalogProbeSw = Stopwatch.StartNew();
                var catalogErrors = 0;
                EmitRagTrace(
                    "rag.multi_search.category_catalog_probe.start",
                    ("terms", catalogProbeTerms),
                    ("limit", RagMultiSearchCategoryCatalogProbeLimit));

                foreach (var term in catalogProbeTerms)
                {
                    try
                    {
                        using var catalogTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        catalogTimeoutCts.CancelAfter(RagMultiSearchCategoryCatalogProbeQueryTimeout);
                        var raw = await _api.DocumentsListAsync(
                            null,
                            null,
                            term,
                            RagMultiSearchCategoryCatalogProbeLimit,
                            0,
                            catalogTimeoutCts.Token).ConfigureAwait(false);
                        var documents = _api.ParseDocumentItems(raw);
                        _mem.PromoteDocumentsToWorkspace(documents);

                        foreach (var document in documents)
                        {
                            var scope = ResolveKnownCategoryScopeFromDocument(document);
                            if (string.IsNullOrWhiteSpace(scope))
                                continue;

                            if (!catalogHitsByScope.TryGetValue(scope, out var existing))
                            {
                                existing = new RagCategoryCatalogScopeEvidence();
                                catalogHitsByScope[scope] = existing;
                            }

                            existing.HitCount++;
                            if (existing.Terms.Add(term))
                            {
                                existing.TermQueryMatches[term] =
                                    catalogProbeTermQueryMatches.TryGetValue(term, out var queryMatches)
                                        ? Math.Max(1, queryMatches)
                                        : 1;
                            }

                            var specificScope = ResolveCatalogDocumentSpecificScope(document);
                            if (!string.IsNullOrWhiteSpace(specificScope))
                            {
                                existing.SpecificScopes.TryGetValue(specificScope, out var count);
                                existing.SpecificScopes[specificScope] = count + 1;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        catalogErrors++;
                    }
                    catch (Exception) when (!ct.IsCancellationRequested)
                    {
                        catalogErrors++;
                    }
                }

                var catalogSummary = catalogHitsByScope
                    .OrderByDescending(static item => item.Value.Terms.Count)
                    .ThenByDescending(static item => item.Value.HitCount)
                    .Take(8)
                    .Select(static item =>
                    {
                        var dominantScope = item.Value.SpecificScopes
                            .OrderByDescending(static scope => scope.Value)
                            .ThenBy(static scope => scope.Key.Length)
                            .Select(static scope => scope.Key)
                            .FirstOrDefault();
                        return string.IsNullOrWhiteSpace(dominantScope)
                            ? $"{item.Key}:docs={item.Value.HitCount},terms={item.Value.Terms.Count}"
                            : $"{item.Key}:docs={item.Value.HitCount},terms={item.Value.Terms.Count},specific={dominantScope}";
                    })
                    .ToArray();

                EmitRagTrace(
                    "rag.multi_search.category_catalog_probe.end",
                    ("terms", catalogProbeTerms.Length),
                    ("matched_categories", catalogHitsByScope.Count),
                    ("errors", catalogErrors),
                    ("results", catalogSummary),
                    ("ms", catalogProbeSw.ElapsedMilliseconds));
            }

            var matchedCatalogProbeTerms = catalogHitsByScope.Values
                .SelectMany(static evidence => evidence.Terms)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var effectiveProbeQueries = probeQueries
                .Concat(matchedCatalogProbeTerms)
                .Where(static query => !string.IsNullOrWhiteSpace(query))
                .GroupBy(NormalizeLexicalLookup, StringComparer.Ordinal)
                .Select(static group => group.First())
                .Take(RagMultiSearchCategoryProbeMaxEffectiveQueries)
                .ToArray();

            var categories = knownCategories
                .Select(item =>
                {
                    catalogHitsByScope.TryGetValue(item.Scope, out var catalog);
                    var suggestedScope = ResolveDominantCatalogSpecificScope(item.Scope, catalog);
                    return new RagCategoryScopeProbeCandidate(
                        item.Category,
                        suggestedScope ?? item.Scope,
                        item.LexicalScore,
                        catalog?.HitCount ?? 0,
                        catalog?.Terms.Count ?? 0,
                        catalog?.RepeatedQueryMatches ?? 0,
                        catalog?.DominantTermQueryMatches ?? 0,
                        suggestedScope);
                })
                .Where(static item => item.CatalogQueryMatches >= 2
                                      || item.LexicalScore > 0
                                      || HasStrongRepeatedCatalogTermScopeEvidence(item))
                .OrderByDescending(static item => item.CatalogQueryMatches)
                .ThenByDescending(static item => item.CatalogRepeatedQueryMatches)
                .ThenByDescending(static item => item.CatalogDominantTermQueryMatches)
                .ThenByDescending(static item => item.CatalogHitCount)
                .ThenByDescending(static item => item.LexicalScore)
                .ThenByDescending(static item => item.Category.TotalDocuments)
                .ThenBy(static item => item.Category.Ordinal)
                .Take(RagMultiSearchCategoryProbeMaxScopes)
                .ToArray();

            if (categories.Length == 0)
            {
                EmitRagTrace(
                    "rag.multi_search.category_probe.skipped",
                    ("reason", "weak_catalog_or_category_hints"),
                    ("queries", probeQueries),
                    ("catalog_terms", matchedCatalogProbeTerms),
                    ("catalog_results", catalogHitsByScope
                        .OrderByDescending(static item => item.Value.Terms.Count)
                        .ThenByDescending(static item => item.Value.HitCount)
                        .Take(8)
                        .Select(static item => $"{item.Key}:docs={item.Value.HitCount},terms={item.Value.Terms.Count}")
                        .ToArray()));
                return null;
            }

            categories = await ExpandCategoryProbeCandidatesWithCatalogChildrenAsync(categories).ConfigureAwait(false);

            categories = categories
                .OrderByDescending(static item => item.CatalogQueryMatches)
                .ThenByDescending(static item => item.CatalogRepeatedQueryMatches)
                .ThenByDescending(static item => item.CatalogDominantTermQueryMatches)
                .ThenByDescending(static item => item.CatalogHitCount)
                .ThenByDescending(static item => item.LexicalScore)
                .ThenByDescending(static item => item.Category.TotalDocuments)
                .ThenBy(static item => item.Category.Ordinal)
                .ToArray();

            var clearCatalogChildCandidates = categories
                .Where(static item => item.CatalogHitCount > 0)
                .Where(static item => item.CatalogQueryMatches >= 2
                                      || item.LexicalScore > 0
                                      || HasStrongRepeatedCatalogTermScopeEvidence(item))
                .Where(static item => !string.IsNullOrWhiteSpace(item.CatalogSuggestedScope))
                .Where(static item => item.Scope.Contains('/', StringComparison.Ordinal))
                .ToArray();
            if (clearCatalogChildCandidates.Length == 1)
            {
                var selected = clearCatalogChildCandidates[0];
                foreach (var supplementalQuery in matchedCatalogProbeTerms.Take(RagMultiSearchCategorySupplementalQueryLimit))
                    categoryInferredSupplementalQueries.Add(supplementalQuery);

                EmitRagTrace(
                    "rag.multi_search.category_probe.end",
                    ("decision", "catalog_child_applied"),
                    ("category", selected.Scope),
                    ("catalog_hits", selected.CatalogHitCount),
                    ("catalog_terms", selected.CatalogQueryMatches),
                    ("catalog_repeated_terms", selected.CatalogRepeatedQueryMatches),
                    ("catalog_dominant_term_queries", selected.CatalogDominantTermQueryMatches),
                    ("supplemental_queries", categoryInferredSupplementalQueries.ToArray()),
                    ("candidates", categories
                        .Select(FormatCategoryProbeCandidate)
                        .Take(12)
                        .ToArray()));
                return selected.Scope;
            }

            var repeatedCatalogTermCandidates = categories
                .Where(HasStrongRepeatedCatalogTermScopeEvidence)
                .ToArray();
            if (repeatedCatalogTermCandidates.Length == 1)
            {
                var selected = repeatedCatalogTermCandidates[0];
                foreach (var supplementalQuery in matchedCatalogProbeTerms.Take(RagMultiSearchCategorySupplementalQueryLimit))
                    categoryInferredSupplementalQueries.Add(supplementalQuery);

                EmitRagTrace(
                    "rag.multi_search.category_probe.end",
                    ("decision", "catalog_repeated_term_applied"),
                    ("category", selected.Scope),
                    ("catalog_hits", selected.CatalogHitCount),
                    ("catalog_terms", selected.CatalogQueryMatches),
                    ("catalog_repeated_terms", selected.CatalogRepeatedQueryMatches),
                    ("catalog_dominant_term_queries", selected.CatalogDominantTermQueryMatches),
                    ("supplemental_queries", categoryInferredSupplementalQueries.ToArray()),
                    ("candidates", categories
                        .Select(FormatCategoryProbeCandidate)
                        .Take(12)
                        .ToArray()));
                return selected.Scope;
            }

            if (categories.Length == 0)
            {
                EmitRagTrace(
                    "rag.multi_search.category_probe.skipped",
                    ("reason", "no_catalog_categories"),
                    ("queries", probeQueries));
                return null;
            }

            var probeSw = Stopwatch.StartNew();
            EmitRagTrace(
                "rag.multi_search.category_probe.start",
                ("categories", categories.Length),
                ("queries", effectiveProbeQueries),
                ("candidates", categories
                    .Select(FormatCategoryProbeCandidate)
                    .Take(12)
                    .ToArray()),
                ("top_k", RagMultiSearchCategoryProbeTopK));

            using var gate = new SemaphoreSlim(Math.Min(RagMultiSearchMaxParallelism, categories.Length));
            var results = await Task.WhenAll(categories.Select(async (item, categoryIndex) =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                var scopeSw = Stopwatch.StartNew();
                var hitCount = 0;
                var matchedQueryCount = 0;
                var errorCount = 0;
                var score = 0.0;
                ClientLog.Info(
                    "ToolAgent rag.multi_search category probe scope start: " +
                    $"scope={FormatRagTraceValue(item.Scope)}|index={categoryIndex + 1}/{categories.Length}|" +
                    $"queries={effectiveProbeQueries.Length}|catalogHits={item.CatalogHitCount}|catalogTerms={item.CatalogQueryMatches}|" +
                    $"lex={item.LexicalScore}");
                EmitRagTrace(
                    "rag.multi_search.category_probe.scope.start",
                    ("scope", item.Scope),
                    ("index", categoryIndex + 1),
                    ("total", categories.Length),
                    ("queries", effectiveProbeQueries.Length),
                    ("catalog_hits", item.CatalogHitCount),
                    ("catalog_terms", item.CatalogQueryMatches),
                    ("lexical_score", item.LexicalScore));
                try
                {
                    foreach (var probeQueryEntry in effectiveProbeQueries.Select(static (query, index) => new { Query = query, Index = index }))
                    {
                        var probeQuery = probeQueryEntry.Query;
                        var querySw = Stopwatch.StartNew();
                        var queryHitCount = 0;
                        string? queryError = null;
                        ClientLog.Info(
                            "ToolAgent rag.multi_search category probe query start: " +
                            $"scope={FormatRagTraceValue(item.Scope)}|index={probeQueryEntry.Index + 1}/{effectiveProbeQueries.Length}|" +
                            $"query={FormatRagTraceValue(probeQuery, 160)}");
                        EmitRagTrace(
                            "rag.multi_search.category_probe.query.start",
                            ("scope", item.Scope),
                            ("index", probeQueryEntry.Index + 1),
                            ("total", effectiveProbeQueries.Length),
                            ("query", probeQuery));
                        try
                        {
                            using var queryTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            queryTimeoutCts.CancelAfter(RagMultiSearchCategoryProbeQueryTimeout);
                            var raw = await _api.RagSearchToolAsync(
                                probeQuery,
                                RagMultiSearchCategoryProbeTopK,
                                item.Scope,
                                "balanced",
                                queryTimeoutCts.Token).ConfigureAwait(false);
                            var norm = NormalizeRagHits(raw);
                            queryHitCount = CountRagHits(norm);
                            hitCount += queryHitCount;
                            if (queryHitCount > 0)
                                matchedQueryCount++;
                            if (norm.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var hit in hits.EnumerateArray())
                                    score += Math.Max(0, ReadRagHitScore(hit));
                            }
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            errorCount++;
                            queryError = "rag_category_probe_query_timeout";
                        }
                        catch (ApiClientBackendBusyException) when (!ct.IsCancellationRequested)
                        {
                            errorCount++;
                            queryError = "rag_search_busy";
                        }
                        catch (Exception ex) when (!ct.IsCancellationRequested)
                        {
                            errorCount++;
                            queryError = ex.GetType().Name;
                        }
                        finally
                        {
                            querySw.Stop();
                            ClientLog.Info(
                                "ToolAgent rag.multi_search category probe query end: " +
                                $"scope={FormatRagTraceValue(item.Scope)}|index={probeQueryEntry.Index + 1}/{effectiveProbeQueries.Length}|" +
                                $"hits={queryHitCount}|error={FormatRagTraceValue(queryError, 160)}|ms={querySw.ElapsedMilliseconds}|" +
                                $"query={FormatRagTraceValue(probeQuery, 140)}");
                            EmitRagTrace(
                                "rag.multi_search.category_probe.query.end",
                                ("scope", item.Scope),
                                ("index", probeQueryEntry.Index + 1),
                                ("total", effectiveProbeQueries.Length),
                                ("query", probeQuery),
                                ("hits", queryHitCount),
                                ("error", queryError),
                                ("ms", querySw.ElapsedMilliseconds));
                        }
                    }
                }
                finally
                {
                    scopeSw.Stop();
                    ClientLog.Info(
                        "ToolAgent rag.multi_search category probe scope end: " +
                        $"scope={FormatRagTraceValue(item.Scope)}|index={categoryIndex + 1}/{categories.Length}|" +
                        $"hits={hitCount}|matchedQueries={matchedQueryCount}|errors={errorCount}|score={score.ToString("0.###", CultureInfo.InvariantCulture)}|" +
                        $"ms={scopeSw.ElapsedMilliseconds}");
                    EmitRagTrace(
                        "rag.multi_search.category_probe.scope.end",
                        ("scope", item.Scope),
                        ("index", categoryIndex + 1),
                        ("total", categories.Length),
                        ("hits", hitCount),
                        ("matched_queries", matchedQueryCount),
                        ("errors", errorCount),
                        ("score", score),
                        ("ms", scopeSw.ElapsedMilliseconds));
                    gate.Release();
                }

                return new RagCategoryScopeProbeResult(
                    item.Scope,
                    hitCount,
                    matchedQueryCount,
                    score,
                    errorCount,
                    scopeSw.ElapsedMilliseconds,
                    item.LexicalScore,
                    item.CatalogHitCount,
                    item.CatalogQueryMatches);
            })).ConfigureAwait(false);

            var ranked = results
                .Where(static result => result.HitCount > 0)
                .OrderByDescending(static result => result.MatchedQueryCount)
                .ThenByDescending(static result => result.CatalogQueryMatches)
                .ThenByDescending(static result => result.CatalogHitCount)
                .ThenByDescending(static result => result.Score)
                .ThenByDescending(static result => result.HitCount)
                .ThenBy(static result => result.ErrorCount)
                .ThenBy(static result => result.ElapsedMs)
                .ToArray();
            var summary = results
                .OrderByDescending(static result => result.MatchedQueryCount)
                .ThenByDescending(static result => result.CatalogQueryMatches)
                .ThenByDescending(static result => result.CatalogHitCount)
                .ThenByDescending(static result => result.HitCount)
                .ThenByDescending(static result => result.Score)
                .Take(8)
                .Select(static result => $"{result.Scope}:hits={result.HitCount},queries={result.MatchedQueryCount},catalog={result.CatalogHitCount}")
                .ToArray();

            if (ranked.Length == 0)
            {
                EmitRagTrace(
                    "rag.multi_search.category_probe.end",
                    ("decision", "no_hits"),
                    ("categories", categories.Length),
                    ("results", summary),
                    ("ms", probeSw.ElapsedMilliseconds));
                return null;
            }

            var best = ranked[0];
            var requiredMatchedQueries = Math.Min(2, effectiveProbeQueries.Length);
            if (best.LexicalScore == 0
                && (best.CatalogQueryMatches < 2 || best.MatchedQueryCount < requiredMatchedQueries))
            {
                EmitRagTrace(
                    "rag.multi_search.category_probe.end",
                    ("decision", "weak"),
                    ("best", best.Scope),
                    ("hits", best.HitCount),
                    ("matched_queries", best.MatchedQueryCount),
                    ("required_matched_queries", requiredMatchedQueries),
                    ("catalog_terms", best.CatalogQueryMatches),
                    ("queries", effectiveProbeQueries.Length),
                    ("results", summary),
                    ("ms", probeSw.ElapsedMilliseconds));
                return null;
            }

            if (ranked.Length > 1
                && best.MatchedQueryCount == ranked[1].MatchedQueryCount
                && best.CatalogQueryMatches == ranked[1].CatalogQueryMatches
                && best.CatalogHitCount == ranked[1].CatalogHitCount
                && best.HitCount == ranked[1].HitCount
                && Math.Abs(best.Score - ranked[1].Score) < 0.0001)
            {
                EmitRagTrace(
                    "rag.multi_search.category_probe.end",
                    ("decision", "ambiguous"),
                    ("best", best.Scope),
                    ("runner_up", ranked[1].Scope),
                    ("hits", best.HitCount),
                    ("matched_queries", best.MatchedQueryCount),
                    ("results", summary),
                    ("ms", probeSw.ElapsedMilliseconds));
                return null;
            }

            EmitRagTrace(
                "rag.multi_search.category_probe.end",
                ("decision", "applied"),
                ("category", best.Scope),
                ("hits", best.HitCount),
                ("matched_queries", best.MatchedQueryCount),
                ("catalog_hits", best.CatalogHitCount),
                ("catalog_terms", best.CatalogQueryMatches),
                ("score", best.Score),
                ("results", summary),
                ("ms", probeSw.ElapsedMilliseconds));
            if (best.CatalogHitCount > 0)
            {
                foreach (var supplementalQuery in matchedCatalogProbeTerms.Take(RagMultiSearchCategorySupplementalQueryLimit))
                    categoryInferredSupplementalQueries.Add(supplementalQuery);
            }
            return best.Scope;
        }

        async Task<JsonElement> RunMergedSearchAsync(string? scope, bool categoryInferred)
        {
            var scopeSw = Stopwatch.StartNew();
            var merged = new List<RagMultiSearchHitCandidate>();
            var queryRuns = new List<object?>();
            var degradedRetrievers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            object? selectedGuidance = null;
            string? selectedGuidanceBehavior = null;
            var selectedQueryList = queries.Take(queryBudget)
                .ToList();
            if (categoryInferred && categoryInferredSupplementalQueries.Count > 0)
            {
                foreach (var supplementalQuery in categoryInferredSupplementalQueries)
                {
                    if (selectedQueryList.Count >= queryBudget + RagMultiSearchCategorySupplementalQueryLimit)
                        break;

                    var normalizedSupplemental = NormalizeLexicalLookup(supplementalQuery);
                    if (string.IsNullOrWhiteSpace(normalizedSupplemental)
                        || selectedQueryList.Any(query => string.Equals(NormalizeLexicalLookup(query), normalizedSupplemental, StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    selectedQueryList.Add(supplementalQuery);
                }
            }

            var selectedQueries = selectedQueryList.ToArray();
            var querySpecificities = selectedQueries
                .Select(ComputeRagMultiSearchQuerySpecificity)
                .ToArray();
            var fanoutParallelism = Math.Min(RagMultiSearchMaxParallelism, Math.Max(1, selectedQueries.Length));
            ClientLog.Info(
                "ToolAgent rag.multi_search scope start: " +
                $"scope={FormatRagTraceValue(scope)}|categoryInferred={categoryInferred}|selectedQueries={selectedQueries.Length}|" +
                $"fanout={fanoutParallelism}|queries={TruncateForPrompt(string.Join(" || ", selectedQueries.Select(q => FormatRagTraceValue(q, 90))), 520)}");
            EmitRagTrace(
                "rag.multi_search.scope.start",
                ("scope", scope),
                ("category_inferred", categoryInferred),
                ("selected_queries", selectedQueries.Length),
                ("fanout", fanoutParallelism),
                ("queries", selectedQueries));
            using var gate = new SemaphoreSlim(fanoutParallelism);
            var runs = await Task.WhenAll(selectedQueries.Select(async (q, index) =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                var querySw = Stopwatch.StartNew();
                ClientLog.Info(
                    "ToolAgent rag.multi_search query start: " +
                    $"scope={FormatRagTraceValue(scope)}|index={index + 1}/{selectedQueries.Length}|query={FormatRagTraceValue(q, 180)}");
                EmitRagTrace(
                    "rag.multi_search.query.start",
                    ("scope", scope),
                    ("index", index + 1),
                    ("total", selectedQueries.Length),
                    ("query", q));
                try
                {
                    JsonElement norm;
                    var queryTimeout = ResolveRagMultiSearchPerQueryTimeout(
                        researchMode,
                        includeResearchSurfaces == true);
                    try
                    {
                        using var queryTimeoutCts = queryTimeout.HasValue
                            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                            : null;
                        if (queryTimeout.HasValue)
                            queryTimeoutCts!.CancelAfter(queryTimeout.Value);

                        var raw = await _api.RagSearchToolAsync(
                            q,
                            topK,
                            scope,
                            mode,
                            queryTimeoutCts?.Token ?? ct,
                            docId,
                            docPath,
                            maxPerDoc,
                            maxPerPage,
                            pageStart,
                            pageEnd,
                            researchMode,
                            includeResearchSurfaces).ConfigureAwait(false);
                        norm = NormalizeRagHits(raw);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested && queryTimeout.HasValue)
                    {
                        norm = BuildRagSearchQueryTimeoutPayload(q, scope, mode, queryTimeout.Value);
                    }
                    catch (ApiClientBackendBusyException ex) when (!ct.IsCancellationRequested)
                    {
                        norm = BuildRagSearchBusyPayload(new[] { q }, scope, mode, ex);
                    }
                    querySw.Stop();

                    var localDegradedRetrievers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    CollectRagDegradedRetrievers(norm, localDegradedRetrievers);
                    var guidance = DeserializePromptObject(norm, "guidance");
                    var guidanceBehavior = norm.TryGetProperty("guidance", out var guidanceEl) && guidanceEl.ValueKind == JsonValueKind.Object
                        ? TryGetString(guidanceEl, "behavior")
                        : null;
                    var busy = IsRagSearchBusyPayload(norm);
                    var hitCount = CountRagHits(norm);
                    var error = TryGetString(norm, "error");
                    ClientLog.Info(
                        "ToolAgent rag.multi_search query end: " +
                        $"scope={FormatRagTraceValue(scope)}|index={index + 1}/{selectedQueries.Length}|hits={hitCount}|" +
                        $"busy={busy}|error={FormatRagTraceValue(error, 180)}|degraded={localDegradedRetrievers.Count}|" +
                        $"ms={querySw.ElapsedMilliseconds}|query={FormatRagTraceValue(q, 140)}");
                    EmitRagTrace(
                        "rag.multi_search.query.end",
                        ("scope", scope),
                        ("index", index + 1),
                        ("total", selectedQueries.Length),
                        ("query", q),
                        ("hits", hitCount),
                        ("busy", busy),
                        ("error", error),
                        ("degraded", localDegradedRetrievers.ToArray()),
                        ("ms", querySw.ElapsedMilliseconds));
                    return new
                    {
                        Index = index,
                        Query = q,
                        Norm = norm.Clone(),
                        Busy = busy,
                        RetryAfterSeconds = TryGetInt(norm, "retryAfterSeconds") ?? 1,
                        ClientElapsedMs = querySw.ElapsedMilliseconds,
                        HitCount = hitCount,
                        Error = error,
                        Guidance = guidance,
                        GuidanceBehavior = guidanceBehavior,
                        Meta = DeserializePromptObject(norm, "meta"),
                        DegradedRetrievers = localDegradedRetrievers.ToArray()
                    };
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    querySw.Stop();
                    ClientLog.Info(
                        "ToolAgent rag.multi_search query end: " +
                        $"scope={FormatRagTraceValue(scope)}|index={index + 1}/{selectedQueries.Length}|ok=false|" +
                        $"error={TruncateForPrompt(ex.GetType().Name + ": " + ex.Message, 260)}|ms={querySw.ElapsedMilliseconds}|" +
                        $"query={FormatRagTraceValue(q, 140)}");
                    EmitRagTrace(
                        "rag.multi_search.query.end",
                        ("scope", scope),
                        ("index", index + 1),
                        ("total", selectedQueries.Length),
                        ("query", q),
                        ("ok", false),
                        ("error", ex.GetType().Name + ": " + ex.Message),
                        ("ms", querySw.ElapsedMilliseconds));
                    throw;
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(false);
            ClientLog.Info(
                "ToolAgent rag.multi_search scope gathered: " +
                $"scope={FormatRagTraceValue(scope)}|runs={runs.Length}|totalHits={runs.Sum(static run => run.HitCount)}|" +
                $"busyRuns={runs.Count(static run => run.Busy)}|errorRuns={runs.Count(static run => !string.IsNullOrWhiteSpace(run.Error))}|" +
                $"ms={scopeSw.ElapsedMilliseconds}");
            EmitRagTrace(
                "rag.multi_search.scope.gathered",
                ("scope", scope),
                ("runs", runs.Length),
                ("total_hits", runs.Sum(static run => run.HitCount)),
                ("busy_runs", runs.Count(static run => run.Busy)),
                ("error_runs", runs.Count(static run => !string.IsNullOrWhiteSpace(run.Error))),
                ("ms", scopeSw.ElapsedMilliseconds));

            foreach (var run in runs.OrderBy(static x => x.Index))
            {
                foreach (var degradedRetriever in run.DegradedRetrievers)
                    degradedRetrievers.Add(degradedRetriever);

                if (ShouldPreferMultiSearchGuidance(selectedGuidanceBehavior, run.GuidanceBehavior))
                {
                    selectedGuidance = run.Guidance;
                    selectedGuidanceBehavior = run.GuidanceBehavior;
                }

                queryRuns.Add(new
                {
                    query = run.Query,
                    clientElapsedMs = run.ClientElapsedMs,
                    hitCount = run.HitCount,
                    error = string.IsNullOrWhiteSpace(run.Error) ? null : run.Error,
                    busy = run.Busy ? true : (bool?)null,
                    retryAfterSeconds = run.Busy ? run.RetryAfterSeconds : (int?)null,
                    degradedRetrievers = run.DegradedRetrievers.Length == 0 ? null : run.DegradedRetrievers,
                    guidance = run.Guidance,
                    meta = run.Meta
                });

                if (run.Norm.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array)
                {
                    var hitRank = 0;
                    foreach (var h in hits.EnumerateArray())
                    {
                        merged.Add(new RagMultiSearchHitCandidate(
                            h.Clone(),
                            run.Index,
                            hitRank++,
                            querySpecificities.Length > run.Index ? querySpecificities[run.Index] : 0,
                            run.Query));
                    }
                }
            }

            var busyRuns = runs.Where(static run => run.Busy).ToArray();
            var allBusy = busyRuns.Length == runs.Length;
            var retryAfterSeconds = busyRuns.Length == 0
                ? (int?)null
                : Math.Clamp(busyRuns.Max(static run => run.RetryAfterSeconds), 1, 300);

            // Dedup by docPath + pageStart + pageEnd, keeping the richest metadata variant.
            var uniq = merged
                .GroupBy(static candidate => BuildRagHitDedupeKey(candidate.Hit), StringComparer.OrdinalIgnoreCase)
                .Select(static group =>
                {
                    var bestHit = group
                        .OrderByDescending(static candidate => ComputeRagHitMetadataRichness(candidate.Hit))
                        .ThenByDescending(static candidate => ReadRagHitScore(candidate.Hit))
                        .First()
                        .Hit;
                    var primaryOrigin = group
                        .Where(static candidate => candidate.QueryIndex == 0 && candidate.HitRank <= 1)
                        .OrderBy(static candidate => candidate.HitRank)
                        .ThenByDescending(static candidate => ReadRagHitScore(candidate.Hit))
                        .FirstOrDefault();
                    var bestOrigin = primaryOrigin ?? group
                        .OrderByDescending(static candidate => candidate.QuerySpecificity)
                        .ThenBy(static candidate => candidate.HitRank)
                        .ThenByDescending(static candidate => ReadRagHitScore(candidate.Hit))
                        .First();
                    return bestOrigin with { Hit = bestHit };
                })
                .ToList();

            var outputLimit = Math.Max(10, topK * 2);
            var selected = new List<RagMultiSearchHitCandidate>(outputLimit);
            var selectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void AddCandidate(RagMultiSearchHitCandidate candidate)
            {
                if (selected.Count >= outputLimit)
                    return;

                if (selectedKeys.Add(BuildRagHitDedupeKey(candidate.Hit)))
                    selected.Add(candidate);
            }

            foreach (var candidate in uniq
                         .Where(static candidate => candidate.QueryIndex == 0 && candidate.HitRank <= 1)
                         .OrderBy(static candidate => candidate.HitRank)
                         .ThenByDescending(static candidate => ReadRagHitScore(candidate.Hit)))
            {
                AddCandidate(candidate);
            }

            var perQueryKeep = Math.Clamp(topK / 2, 1, 3);
            foreach (var candidate in uniq
                         .GroupBy(static candidate => candidate.QueryIndex)
                         .SelectMany(group => group
                             .OrderBy(static candidate => candidate.HitRank)
                             .ThenByDescending(static candidate => ReadRagHitScore(candidate.Hit))
                             .Take(perQueryKeep))
                         .OrderByDescending(static candidate => candidate.QuerySpecificity)
                         .ThenBy(static candidate => candidate.HitRank)
                         .ThenBy(static candidate => candidate.QueryIndex)
                         .ThenByDescending(static candidate => ReadRagHitScore(candidate.Hit)))
            {
                AddCandidate(candidate);
            }

            foreach (var candidate in uniq
                         .OrderByDescending(static candidate => ReadRagHitScore(candidate.Hit))
                         .ThenByDescending(static candidate => candidate.QuerySpecificity)
                         .ThenBy(static candidate => candidate.HitRank)
                         .ThenBy(static candidate => candidate.QueryIndex))
            {
                AddCandidate(candidate);
            }

            var outputHits = selected
                .Select(static candidate => AnnotateRagMultiSearchHit(candidate))
                .ToList();
            var distinctDocs = outputHits
                .Select(static hit => TryGetString(hit, "docPath") ?? TryGetString(hit, "docName") ?? string.Empty)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var distinctPages = outputHits
                .Select(static hit =>
                {
                    var doc = TryGetString(hit, "docPath") ?? TryGetString(hit, "docName") ?? string.Empty;
                    return string.IsNullOrWhiteSpace(doc) ? string.Empty : $"{doc}#p{ReadRagHitPageStart(hit)}";
                })
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            ClientLog.Info(
                "ToolAgent rag.multi_search scope selected: " +
                $"scope={FormatRagTraceValue(scope)}|merged={merged.Count}|unique={uniq.Count}|selected={outputHits.Count}|" +
                $"distinctDocs={distinctDocs}|distinctPages={distinctPages}|busyRuns={busyRuns.Length}|" +
                $"degraded={degradedRetrievers.Count}|ms={scopeSw.ElapsedMilliseconds}");
            EmitRagTrace(
                "rag.multi_search.scope.selected",
                ("scope", scope),
                ("merged", merged.Count),
                ("unique", uniq.Count),
                ("selected", outputHits.Count),
                ("distinct_docs", distinctDocs),
                ("distinct_pages", distinctPages),
                ("busy_runs", busyRuns.Length),
                ("degraded", degradedRetrievers.ToArray()),
                ("ms", scopeSw.ElapsedMilliseconds));

            var payload = new
            {
                hits = outputHits,
                error = allBusy ? "rag_search_busy" : null,
                busy = allBusy ? true : (bool?)null,
                retryAfterSeconds = allBusy ? retryAfterSeconds : null,
                guidance = selectedGuidance,
                meta = new
                {
                    queries = selectedQueries,
                    mode = (mode ?? "balanced"),
                    category = scope,
                    categoryPath = scope,
                    requestedCategory = requestedCategoryScope,
                    rejectedCategoryScope,
                    rejectedCategoryScopeReason,
                    docId,
                    docPath,
                    pageStart,
                    pageEnd,
                    maxPerDoc,
                    maxPerPage,
                    researchMode,
                    includeResearchSurfaces,
                    categoryInferred,
                    fanoutParallelism,
                    busyQueries = busyRuns.Length == 0 ? null : busyRuns.Select(static run => run.Query).ToArray(),
                    degradedRetrievers = degradedRetrievers.Count == 0 ? null : degradedRetrievers.ToArray(),
                    queryRuns
                }
            };

            return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement.Clone();
        }

        var categoryScopeInferredByProbe = false;
        var probedCategoryScope = await TryInferCategoryScopeFromCatalogProbeAsync().ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(probedCategoryScope))
        {
            categoryScope = probedCategoryScope;
            categoryScopeInferredByProbe = true;
            RememberRagInferredCategoryScope(probedCategoryScope, "catalog_probe");
        }

        var result = await RunMergedSearchAsync(
            categoryScope,
            categoryScopeInferredByProbe || categoryScopeRefinedFromPreviousInference).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(categoryScope)
            && string.IsNullOrWhiteSpace(docId)
            && string.IsNullOrWhiteSpace(docPath)
            && HasRagHits(result)
            && !HasRagBusyQueries(result)
            && !IsRagSourceExploration(researchMode, includeResearchSurfaces == true))
        {
            var inferenceResults = new ToolResults();
            inferenceResults.Items.Add(new ToolResults.Item
            {
                ToolName = "rag.multi_search",
                Result = result
            });
            var inferredCategoryScope = TryInferDominantTopLevelCategoryScope(inferenceResults, string.Join(' ', queries));
            if (!string.IsNullOrWhiteSpace(inferredCategoryScope))
            {
                ClientLog.Info(
                    "ToolAgent rag.multi_search category inference: " +
                    $"inferred={FormatRagTraceValue(inferredCategoryScope)}|initialHits={CountRagHits(result)}");
                EmitRagTrace(
                    "rag.multi_search.category_inference",
                    ("decision", "candidate"),
                    ("inferred", inferredCategoryScope),
                    ("initial_hits", CountRagHits(result)));
                var scopedResult = await RunMergedSearchAsync(inferredCategoryScope, categoryInferred: true).ConfigureAwait(false);
                if (HasRagHits(scopedResult))
                {
                    ClientLog.Info(
                        "ToolAgent rag.multi_search category inference applied: " +
                        $"inferred={FormatRagTraceValue(inferredCategoryScope)}|scopedHits={CountRagHits(scopedResult)}");
                    EmitRagTrace(
                        "rag.multi_search.category_inference",
                        ("decision", "applied"),
                        ("inferred", inferredCategoryScope),
                        ("scoped_hits", CountRagHits(scopedResult)));
                    result = scopedResult;
                }
            }
        }
        else if (string.IsNullOrWhiteSpace(categoryScope)
                 && string.IsNullOrWhiteSpace(docId)
                 && string.IsNullOrWhiteSpace(docPath)
                 && HasRagHits(result)
                 && IsRagSourceExploration(researchMode, includeResearchSurfaces == true))
        {
            ClientLog.Info(
                "ToolAgent rag.multi_search category inference skipped: reason=source_exploration");
            EmitRagTrace(
                "rag.multi_search.category_inference",
                ("decision", "skipped"),
                ("reason", "source_exploration"));
        }

        RememberLastRagDiagnostics(
            queries.Take(queryBudget),
            result);
        ClientLog.Info(
            "ToolAgent rag.multi_search end: " +
            $"hits={CountRagHits(result)}|busy={HasRagBusyQueries(result)}|queries={queries.Count}|budget={queryBudget}|" +
            $"ms={totalSw.ElapsedMilliseconds}");
        EmitRagTrace(
            "rag.multi_search.end",
            ("hits", CountRagHits(result)),
            ("busy", HasRagBusyQueries(result)),
            ("queries", queries.Count),
            ("query_budget", queryBudget),
            ("ms", totalSw.ElapsedMilliseconds));
        return result;
    }

    private static bool HasRagBusyQueries(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("meta", out var meta)
            || meta.ValueKind != JsonValueKind.Object
            || !meta.TryGetProperty("busyQueries", out var busyQueries)
            || busyQueries.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return busyQueries.EnumerateArray().Any();
    }

    private static bool IsRagSearchBusyPayload(JsonElement value)
        => value.ValueKind == JsonValueKind.Object
           && ((value.TryGetProperty("busy", out var busy)
                    && busy.ValueKind is JsonValueKind.True)
               || (value.TryGetProperty("error", out var error)
                   && error.ValueKind == JsonValueKind.String
                   && string.Equals(error.GetString(), "rag_search_busy", StringComparison.OrdinalIgnoreCase)));

    private static void CollectRagDegradedRetrievers(JsonElement normalizedRagResult, ISet<string> degradedRetrievers)
    {
        if (degradedRetrievers is null)
            return;

        var meta = TryGetObject(normalizedRagResult, "meta")
                   ?? TryGetObject(normalizedRagResult, "Meta");
        if (!meta.HasValue)
            return;

        CollectRagDegradedRetrieversFromMeta(meta.Value, degradedRetrievers);

        var queryRuns = TryGetArray(meta.Value, "queryRuns")
                        ?? TryGetArray(meta.Value, "QueryRuns")
                        ?? TryGetArray(meta.Value, "query_runs");
        if (!queryRuns.HasValue)
            return;

        foreach (var run in queryRuns.Value.EnumerateArray())
        {
            if (run.ValueKind != JsonValueKind.Object)
                continue;

            var runMeta = TryGetObject(run, "meta")
                          ?? TryGetObject(run, "Meta");
            if (runMeta.HasValue)
                CollectRagDegradedRetrieversFromMeta(runMeta.Value, degradedRetrievers);
        }
    }

    private static void CollectRagDegradedRetrieversFromMeta(JsonElement meta, ISet<string> degradedRetrievers)
    {
        var directValues = TryGetArray(meta, "degradedRetrievers")
                           ?? TryGetArray(meta, "DegradedRetrievers")
                           ?? TryGetArray(meta, "degraded_retrievers");
        AddRagDegradedRetrieverValues(directValues, degradedRetrievers);

        var metrics = TryGetObject(meta, "metrics")
                      ?? TryGetObject(meta, "Metrics");
        if (!metrics.HasValue)
            return;

        var values = TryGetArray(metrics.Value, "degradedRetrievers")
                     ?? TryGetArray(metrics.Value, "DegradedRetrievers")
                     ?? TryGetArray(metrics.Value, "degraded_retrievers");
        AddRagDegradedRetrieverValues(values, degradedRetrievers);
    }

    private static void AddRagDegradedRetrieverValues(JsonElement? values, ISet<string> degradedRetrievers)
    {
        if (!values.HasValue || values.Value.ValueKind != JsonValueKind.Array)
            return;

        foreach (var value in values.Value.EnumerateArray())
        {
            var retriever = value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : value.ToString();
            if (!string.IsNullOrWhiteSpace(retriever))
                degradedRetrievers.Add(retriever.Trim());
        }
    }

    private static bool ShouldPreferMultiSearchGuidance(string? currentBehavior, string? candidateBehavior)
        => GuidancePriority(candidateBehavior) > GuidancePriority(currentBehavior);

    private static int GuidancePriority(string? behavior)
        => (behavior ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "ask_clarification" => 3,
            "answer_with_caveat" => 2,
            "" => 0,
            _ => 1
        };

    private void RememberLastRagDiagnostics(IEnumerable<string> queries, JsonElement result)
    {
        _mem.LastRagQueries = queries
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .Select(CollapseWhitespace)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        var degradedRetrievers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (result.ValueKind == JsonValueKind.Object)
            CollectRagDegradedRetrievers(result, degradedRetrievers);
        _mem.LastRagDegradedRetrievers = degradedRetrievers
            .Take(16)
            .ToList();

        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("hits", out var hits)
            || hits.ValueKind != JsonValueKind.Array)
        {
            _mem.LastRagHitLabels = new();
            return;
        }

        _mem.LastRagHitLabels = hits.EnumerateArray()
            .Where(static hit => hit.ValueKind == JsonValueKind.Object)
            .Take(30)
            .Select(FormatRagDiagnosticHitLabel)
            .Where(static label => !string.IsNullOrWhiteSpace(label))
            .ToList();
    }

    private static string FormatRagDiagnosticHitLabel(JsonElement hit)
    {
        var summary = BuildRagHitSummary(hit);
        var docName = string.IsNullOrWhiteSpace(summary.DocName)
            ? Path.GetFileName(summary.DocPath ?? string.Empty)
            : summary.DocName;
        var pageStart = summary.PageStart;
        var pageEnd = summary.PageEnd;
        var score = summary.Score;
        var scoreText = score.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var role = string.IsNullOrWhiteSpace(summary.SelectionHintRole) ? "unknown" : summary.SelectionHintRole;
        var cardCount = summary.MatchedContentCards?.Count ?? 0;
        var factCount = ExtractContentCardEvidenceFacts(new[] { summary }).Length;
        var rankText = summary.RetrievalHitRank.HasValue
            ? $" r={summary.RetrievalHitRank.Value}"
            : string.Empty;
        var queryText = summary.RetrievalQueryIndex.HasValue
            ? $" q={summary.RetrievalQueryIndex.Value}"
            : string.Empty;
        return $"{docName} {SourceBackedPagePrefix(string.Empty)}{pageStart}{(pageEnd != pageStart ? $"-{pageEnd}" : string.Empty)} score={scoreText} role={role} table={summary.HasTable.ToString().ToLowerInvariant()} cards={cardCount} facts={factCount}{queryText}{rankText}";
    }
private JsonElement ExecExportCreate(JsonElement args)
    {
        var format = args.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String ? (f.GetString() ?? "txt") : "txt";
        var fileName = args.TryGetProperty("fileName", out var n) && n.ValueKind == JsonValueKind.String ? (n.GetString() ?? "export") : args.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? (t.GetString() ?? "export") : "export";
        var content = args.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? (c.GetString() ?? "") : "";

        var path = ExportService.Create(format, fileName, content);
        var payload = new { savedPath = path };
        return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;
    }

    private async Task<JsonElement> ExecSupportBundleAsync(JsonElement args, CancellationToken ct)
    {
        if (_settings is null)
            return JsonDocument.Parse("{\"error\":\"missing_settings\"}").RootElement;

        var include = GetStringArrayArg(args, "include");
        var runtimeSnapshot = BuildAgentRuntimeSnapshot();
        var zip = await SupportBundleBuilder.BuildAsync(_settings, runtimeSnapshot, include).ConfigureAwait(false);
        var payload = new
        {
            zipPath = zip,
            included = include is { Count: > 0 } ? include.ToArray() : new[] { "diagnostics/agent-runtime" }
        };
        return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;
    }

    private async Task<JsonElement> ExecRagDebugScrollAsync(JsonElement args, CancellationToken ct)
    {
        var cursor = args.TryGetProperty("cursor", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var limit = args.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 100;
        string? docPath = null;
        if (args.TryGetProperty("docRef", out var dref) && dref.ValueKind == JsonValueKind.String)
        {
            var resolved = await ResolveDocRefAsync(dref.GetString() ?? string.Empty, ct).ConfigureAwait(false);
            docPath = resolved?.DocPath;
        }
        else if (args.TryGetProperty("docPath", out var dp) && dp.ValueKind == JsonValueKind.String)
        {
            docPath = dp.GetString();
        }

        try
        {
            var raw = await _api.RagDebugScrollAsync(cursor, limit, docPath, ct).ConfigureAwait(false);
            return raw;
        }
        catch
        {
            return JsonDocument.Parse("{\"items\":[],\"nextCursor\":null,\"error\":\"not_supported\"}").RootElement;
        }
    }
}
