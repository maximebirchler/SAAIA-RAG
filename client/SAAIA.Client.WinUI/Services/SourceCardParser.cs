using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using SAAIA.Client.WinUI.Models;

namespace SAAIA.Client.WinUI.Services;

public static class SourceCardParser
{
    public static List<SourceCard> Parse(string? sourcesJson)
    {
        var result = new List<SourceCard>();
        if (string.IsNullOrWhiteSpace(sourcesJson)) return result;

        try
        {
            using var doc = JsonDocument.Parse(sourcesJson);
            var root = doc.RootElement;

            // ✅ Priorité : notre payload client contient "merged" (voir RagChatAgent.cs)
            var arr =
                TryGetArray(root, "merged", "Merged") ??
                TryGetArray(root, "items", "Items") ??
                TryGetArray(root, "sources", "Sources") ??
                TryGetArray(root, "hits", "Hits") ??
                TryGetArray(root, "matches", "Matches") ??
                (root.ValueKind == JsonValueKind.Array ? root : (JsonElement?)null);

            if (arr is not null)
            {
                AddFromArray(result, arr.Value);
            }
            else if (TryGetObjectAny(root, "source", "Source") is { } source)
            {
                AddFromObject(result, source);
            }
            else
            {
                // ✅ Fallback : certains payloads peuvent être { searches: [ { items: [...] }, ... ] }
                if (root.ValueKind == JsonValueKind.Object &&
                    (root.TryGetProperty("searches", out var searches) || root.TryGetProperty("Searches", out searches)) &&
                    searches.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in searches.EnumerateArray())
                    {
                        var items = TryGetArray(s, "items", "Items", "hits", "Hits", "matches", "Matches");
                        if (items is not null)
                            AddFromArray(result, items.Value);
                    }
                }
            }
        }
        catch
        {
            return new List<SourceCard>();
        }

        // micro-dedup
        return result
            .Where(s => !string.IsNullOrWhiteSpace(s.DocName) || !string.IsNullOrWhiteSpace(s.DocPath))
            .GroupBy(s => $"{s.DocPath}::{s.DocName}::{s.PageStart}::{s.PageEnd}::{s.Snippet}")
            .Select(MergeSourceCardGroup)
            .ToList();
    }

    private static SourceCard MergeSourceCardGroup(IEnumerable<SourceCard> group)
    {
        var sources = group
            .OrderByDescending(SourceCardRichnessScore)
            .ToArray();
        var primary = sources[0];

        return new SourceCard
        {
            DocId = PickString(sources, static s => s.DocId),
            DocPath = PickString(sources, static s => s.DocPath) ?? primary.DocPath,
            DocName = PickString(sources, static s => s.DocName) ?? primary.DocName,
            PageStart = PickInt(sources, static s => s.PageStart),
            PageEnd = PickInt(sources, static s => s.PageEnd),
            Snippet = PickString(sources, static s => s.Snippet) ?? primary.Snippet,
            Score = PickDouble(sources, static s => s.Score),
            SourceHash = PickString(sources, static s => s.SourceHash),
            DocLanguage = PickString(sources, static s => s.DocLanguage),
            ProfileLanguage = PickString(sources, static s => s.ProfileLanguage),
            Category = PickString(sources, static s => s.Category),
            CategoryRef = PickString(sources, static s => s.CategoryRef),
            CategoryPath = PickString(sources, static s => s.CategoryPath),
            ChunkId = PickString(sources, static s => s.ChunkId),
            SectionTitle = PickString(sources, static s => s.SectionTitle),
            HeadingPath = PickString(sources, static s => s.HeadingPath),
            PrevChunkId = PickString(sources, static s => s.PrevChunkId),
            NextChunkId = PickString(sources, static s => s.NextChunkId),
            SameSectionChunkId = PickString(sources, static s => s.SameSectionChunkId),
            OriginalChunkType = PickString(sources, static s => s.OriginalChunkType),
            OffsetStart = PickInt(sources, static s => s.OffsetStart),
            OffsetEnd = PickInt(sources, static s => s.OffsetEnd),
            ExtractionSource = PickString(sources, static s => s.ExtractionSource),
            DocumentQualityStatus = PickString(sources, static s => s.DocumentQualityStatus),
            PageQualityStatus = PickString(sources, static s => s.PageQualityStatus),
            TextStatus = PickString(sources, static s => s.TextStatus),
            QualityStatus = PickString(sources, static s => s.QualityStatus),
            ExtractionConfidence = PickDouble(sources, static s => s.ExtractionConfidence),
            DocumentExtractionConfidence = PickDouble(sources, static s => s.DocumentExtractionConfidence),
            PageExtractionConfidence = PickDouble(sources, static s => s.PageExtractionConfidence),
            ManualReviewRecommended = sources.Any(static s => s.ManualReviewRecommended),
            DocumentManualReviewRecommended = sources.Any(static s => s.DocumentManualReviewRecommended),
            PageManualReviewRecommended = sources.Any(static s => s.PageManualReviewRecommended),
            OcrAttempted = sources.Any(static s => s.OcrAttempted),
            OcrApplied = sources.Any(static s => s.OcrApplied),
            OcrRecommended = sources.Any(static s => s.OcrRecommended),
            ExtractionDiagnosticSummary = MergeDiagnosticSummaries(sources),
            QualitySignals = MergeStringLists(sources.Select(static s => s.QualitySignals), 8),
            MatchedContentCards = MergeContentCards(sources),
            ProfileSignals = MergeProfileSignals(sources),
            SelectionHintEvidenceRole = PickString(sources, static s => s.SelectionHintEvidenceRole),
            SelectionHintActionabilityScore = PickInt(sources, static s => s.SelectionHintActionabilityScore),
            SelectionHintSupportScore = PickInt(sources, static s => s.SelectionHintSupportScore),
            SelectionHintFragmentScore = PickInt(sources, static s => s.SelectionHintFragmentScore),
            SelectionHintNavigationScore = PickInt(sources, static s => s.SelectionHintNavigationScore),
            SelectionHintQualityPenalty = PickInt(sources, static s => s.SelectionHintQualityPenalty),
            ContentRole = PickString(sources, static s => s.ContentRole),
            NavigationReason = PickString(sources, static s => s.NavigationReason),
            RetrievalNavigationScore = PickDouble(sources, static s => s.RetrievalNavigationScore),
            ContentDensityScore = PickDouble(sources, static s => s.ContentDensityScore),
            PagesLabel = PickString(sources, static s => s.PagesLabel) ?? primary.PagesLabel,
            ScoreLabel = PickString(sources, static s => s.ScoreLabel) ?? primary.ScoreLabel,
            MetadataLabel = PickString(sources, static s => s.MetadataLabel) ?? primary.MetadataLabel
        };
    }

    private static int SourceCardRichnessScore(SourceCard source)
    {
        var score = 0;
        score += HasValue(source.SourceHash) * 10;
        score += HasValue(source.DocLanguage) * 4;
        score += HasValue(source.ProfileLanguage) * 4;
        score += HasValue(source.Category) * 2;
        score += HasValue(source.CategoryRef) * 2;
        score += HasValue(source.CategoryPath) * 2;
        score += HasValue(source.ChunkId) * 2;
        score += HasValue(source.SectionTitle) * 1;
        score += HasValue(source.HeadingPath) * 1;
        score += HasValue(source.PrevChunkId) * 1;
        score += HasValue(source.NextChunkId) * 1;
        score += HasValue(source.SameSectionChunkId) * 1;
        score += HasValue(source.OriginalChunkType) * 1;
        score += source.OffsetStart is null ? 0 : 1;
        score += source.OffsetEnd is null ? 0 : 1;
        score += HasValue(source.ExtractionSource) * 4;
        score += HasValue(source.DocumentQualityStatus) * 3;
        score += HasValue(source.PageQualityStatus) * 3;
        score += HasValue(source.TextStatus) * 2;
        score += HasValue(source.QualityStatus) * 2;
        score += source.ExtractionConfidence is null ? 0 : 2;
        score += source.DocumentExtractionConfidence is null ? 0 : 1;
        score += source.PageExtractionConfidence is null ? 0 : 1;
        score += source.ManualReviewRecommended ? 1 : 0;
        score += source.DocumentManualReviewRecommended ? 1 : 0;
        score += source.PageManualReviewRecommended ? 1 : 0;
        score += source.OcrAttempted ? 1 : 0;
        score += source.OcrApplied ? 2 : 0;
        score += source.OcrRecommended ? 2 : 0;
        score += SourceDiagnosticRichnessScore(source.ExtractionDiagnosticSummary);
        score += Math.Min(5, source.QualitySignals?.Count ?? 0);
        score += Math.Min(25, (source.MatchedContentCards?.Count ?? 0) * 5);
        score += SourceProfileSignalsRichnessScore(source.ProfileSignals);
        score += HasValue(source.SelectionHintEvidenceRole) * 3;
        score += source.SelectionHintActionabilityScore is null ? 0 : 1;
        score += source.SelectionHintSupportScore is null ? 0 : 1;
        score += source.SelectionHintFragmentScore is null ? 0 : 1;
        score += source.SelectionHintNavigationScore is null ? 0 : 1;
        score += source.SelectionHintQualityPenalty is null ? 0 : 1;
        score += HasValue(source.ContentRole) * 3;
        score += HasValue(source.NavigationReason) * 2;
        score += source.RetrievalNavigationScore is null ? 0 : 1;
        score += source.ContentDensityScore is null ? 0 : 1;
        return score;
    }

    private static string? PickString(IEnumerable<SourceCard> sources, Func<SourceCard, string?> selector)
        => sources
            .Select(selector)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?
            .Trim();

    private static int? PickInt(IEnumerable<SourceCard> sources, Func<SourceCard, int?> selector)
        => sources
            .Select(selector)
            .FirstOrDefault(static value => value.HasValue);

    private static double? PickDouble(IEnumerable<SourceCard> sources, Func<SourceCard, double?> selector)
        => sources
            .Select(selector)
            .FirstOrDefault(static value => value.HasValue);

    private static List<string> MergeStringLists(IEnumerable<IEnumerable<string>> lists, int maxItems)
        => lists
            .SelectMany(static list => list)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maxItems, 1, 24))
            .ToList();

    private static List<SourceContentCard> MergeContentCards(IEnumerable<SourceCard> sources)
        => sources
            .SelectMany(static source => source.MatchedContentCards)
            .Where(static card => !string.IsNullOrWhiteSpace(card.Title))
            .GroupBy(BuildContentCardMergeKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group =>
            {
                var cards = group.OrderByDescending(ContentCardRichnessScore).ToArray();
                var primary = cards[0];
                return new SourceContentCard
                {
                    Title = primary.Title,
                    ContentCardId = PickContentCardString(cards, static card => card.ContentCardId),
                    PageStart = cards.Select(static card => card.PageStart).FirstOrDefault(static value => value.HasValue),
                    PageEnd = cards.Select(static card => card.PageEnd).FirstOrDefault(static value => value.HasValue),
                    Kind = PickContentCardString(cards, static card => card.Kind),
                    Signals = MergeStringLists(cards.Select(static card => card.Signals), 8),
                    Evidence = cards.Select(static card => card.Evidence).FirstOrDefault(static evidence => evidence.HasValue)?.Clone()
                };
            })
            .OrderByDescending(ContentCardRichnessScore)
            .Take(8)
            .ToList();

    private static string BuildContentCardMergeKey(SourceContentCard card)
        => !string.IsNullOrWhiteSpace(card.ContentCardId)
            ? $"id:{card.ContentCardId.Trim()}"
            : string.Join(
                "|",
                "shape",
                (card.Title ?? string.Empty).Trim().ToLowerInvariant(),
                (card.Kind ?? string.Empty).Trim().ToLowerInvariant(),
                card.PageStart?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                card.PageEnd?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);

    private static int ContentCardRichnessScore(SourceContentCard card)
        => HasValue(card.ContentCardId) * 8
           + (card.Evidence.HasValue ? 8 : 0)
           + Math.Min(8, card.Signals.Count)
           + (card.PageStart.HasValue ? 1 : 0)
           + (card.PageEnd.HasValue ? 1 : 0);

    private static string? PickContentCardString(IEnumerable<SourceContentCard> cards, Func<SourceContentCard, string?> selector)
        => cards
            .Select(selector)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?
            .Trim();

    private static SourceProfileSignals? MergeProfileSignals(IEnumerable<SourceCard> sources)
    {
        var profiles = sources
            .Select(static source => source.ProfileSignals)
            .Where(static profile => profile is not null)
            .Select(static profile => profile!)
            .OrderByDescending(SourceProfileSignalsRichnessScore)
            .ToArray();
        if (profiles.Length == 0)
            return null;

        var primary = profiles[0];
        var merged = new SourceProfileSignals
        {
            ProfileVersion = PickProfileString(profiles, static profile => profile.ProfileVersion),
            Language = PickProfileString(profiles, static profile => profile.Language),
            Keywords = MergeStringLists(profiles.Select(static profile => profile.Keywords), 8),
            Entities = MergeStringLists(profiles.Select(static profile => profile.Entities), 8),
            Topics = MergeStringLists(profiles.Select(static profile => profile.Topics), 8),
            HypotheticalQuestions = MergeStringLists(profiles.Select(static profile => profile.HypotheticalQuestions), 4),
            Limits = MergeStringLists(profiles.Select(static profile => profile.Limits), 4),
            MatchedTerms = MergeStringLists(profiles.Select(static profile => profile.MatchedTerms), 12),
            MatchCount = profiles
                .Select(static profile => profile.MatchCount)
                .FirstOrDefault(static value => value.HasValue) ?? primary.MatchCount
        };

        return SourceProfileSignalsRichnessScore(merged) == 0 ? null : merged;
    }

    private static string? PickProfileString(IEnumerable<SourceProfileSignals> profiles, Func<SourceProfileSignals, string?> selector)
        => profiles
            .Select(selector)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?
            .Trim();

    private static SourceExtractionDiagnosticSummary? MergeDiagnosticSummaries(IEnumerable<SourceCard> sources)
    {
        var diagnostics = sources
            .Select(static source => source.ExtractionDiagnosticSummary)
            .Where(static summary => summary is not null)
            .Select(static summary => summary!)
            .ToArray();
        if (diagnostics.Length == 0)
            return null;

        return new SourceExtractionDiagnosticSummary
        {
            NativeTextStatus = PickDiagnosticString(diagnostics, static summary => summary.NativeTextStatus),
            NativeOcrRecommended = diagnostics.Select(static summary => summary.NativeOcrRecommended).FirstOrDefault(static value => value.HasValue),
            OcrMode = PickDiagnosticString(diagnostics, static summary => summary.OcrMode),
            OcrLanguages = PickDiagnosticString(diagnostics, static summary => summary.OcrLanguages),
            OcrDurationMs = diagnostics.Select(static summary => summary.OcrDurationMs).FirstOrDefault(static value => value.HasValue),
            OcrFailureReason = PickDiagnosticString(diagnostics, static summary => summary.OcrFailureReason),
            OcrAppliedReason = PickDiagnosticString(diagnostics, static summary => summary.OcrAppliedReason),
            OcrTimedOut = diagnostics.Select(static summary => summary.OcrTimedOut).FirstOrDefault(static value => value.HasValue),
            OcrAttemptedPageCount = diagnostics.Select(static summary => summary.OcrAttemptedPageCount).FirstOrDefault(static value => value.HasValue),
            OcrSkippedPageCount = diagnostics.Select(static summary => summary.OcrSkippedPageCount).FirstOrDefault(static value => value.HasValue),
            OcrPagesWithNovelTextCount = diagnostics.Select(static summary => summary.OcrPagesWithNovelTextCount).FirstOrDefault(static value => value.HasValue),
            PageCount = diagnostics.Select(static summary => summary.PageCount).FirstOrDefault(static value => value.HasValue),
            TextPageCount = diagnostics.Select(static summary => summary.TextPageCount).FirstOrDefault(static value => value.HasValue),
            EmptyPageCount = diagnostics.Select(static summary => summary.EmptyPageCount).FirstOrDefault(static value => value.HasValue),
            SparsePageCount = diagnostics.Select(static summary => summary.SparsePageCount).FirstOrDefault(static value => value.HasValue),
            ImagePageCount = diagnostics.Select(static summary => summary.ImagePageCount).FirstOrDefault(static value => value.HasValue),
            PageWarningCount = diagnostics.Select(static summary => summary.PageWarningCount).FirstOrDefault(static value => value.HasValue),
            PageReviewRecommendedCount = diagnostics.Select(static summary => summary.PageReviewRecommendedCount).FirstOrDefault(static value => value.HasValue)
        };
    }

    private static string? PickDiagnosticString(IEnumerable<SourceExtractionDiagnosticSummary> diagnostics, Func<SourceExtractionDiagnosticSummary, string?> selector)
        => diagnostics
            .Select(selector)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?
            .Trim();

    private static int SourceProfileSignalsRichnessScore(SourceProfileSignals? profile)
    {
        if (profile is null)
            return 0;

        var score = 0;
        score += HasValue(profile.ProfileVersion) * 2;
        score += HasValue(profile.Language) * 2;
        score += Math.Min(8, profile.Keywords.Count);
        score += Math.Min(8, profile.Entities.Count);
        score += Math.Min(8, profile.Topics.Count);
        score += Math.Min(4, profile.HypotheticalQuestions.Count);
        score += Math.Min(4, profile.Limits.Count);
        score += Math.Min(6, profile.MatchedTerms.Count);
        score += profile.MatchCount.GetValueOrDefault() > 0 ? 2 : 0;
        return score;
    }

    private static int SourceDiagnosticRichnessScore(SourceExtractionDiagnosticSummary? diagnostics)
    {
        if (diagnostics is null)
            return 0;

        var score = 0;
        score += HasValue(diagnostics.NativeTextStatus) * 2;
        score += diagnostics.NativeOcrRecommended is null ? 0 : 1;
        score += HasValue(diagnostics.OcrMode) * 2;
        score += HasValue(diagnostics.OcrLanguages) * 1;
        score += diagnostics.OcrDurationMs is null ? 0 : 1;
        score += HasValue(diagnostics.OcrFailureReason) * 4;
        score += HasValue(diagnostics.OcrAppliedReason) * 2;
        score += diagnostics.OcrTimedOut == true ? 4 : 0;
        score += diagnostics.OcrAttemptedPageCount is null ? 0 : 1;
        score += diagnostics.OcrPagesWithNovelTextCount is null ? 0 : 2;
        score += diagnostics.PageWarningCount is null ? 0 : 2;
        score += diagnostics.PageReviewRecommendedCount is null ? 0 : 3;
        score += diagnostics.ImagePageCount is null ? 0 : 1;
        return score;
    }

    private static int HasValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? 0 : 1;

    private static void AddFromArray(List<SourceCard> result, JsonElement arr)
    {
        if (arr.ValueKind != JsonValueKind.Array) return;

        foreach (var el in arr.EnumerateArray())
        {
            AddFromObject(result, el);
        }
    }

    private static void AddFromObject(List<SourceCard> result, JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return;

        var docPath = GetStringAny(el, "doc_path", "docPath", "DocPath", "doc", "Doc", "path", "Path", "file", "File", "source", "Source") ?? "";
        var docName = GetStringAny(el, "doc_name", "docName", "DocName", "label", "Label", "title", "Title", "name", "Name") ?? SafeFileName(docPath);

        var page = GetIntAny(el, "page", "Page", "p", "P");
        var pageStart = GetIntAny(el, "page_start", "pageStart", "PageStart", "fromPage", "FromPage", "page_from") ?? page;
        var pageEnd = GetIntAny(el, "page_end", "pageEnd", "PageEnd", "toPage", "ToPage", "page_to") ?? page;

        var snippet = GetStringAny(el, "excerpt", "Excerpt", "snippet", "Snippet", "contextualSnippet", "ContextualSnippet", "contextual_snippet", "text", "Text", "chunk", "Chunk", "content", "Content") ?? "";
        var score = GetDoubleAny(el, "score", "Score", "similarity", "Similarity", "rerankScore", "RerankScore");
        var extractionQuality = TryGetObjectAny(el, "extractionQuality", "extraction_quality", "ExtractionQuality");
        var selectionHints = TryGetObjectAny(el, "selectionHints", "selection_hints", "SelectionHints");
        var profileSignals = TryGetObjectAny(el, "profileSignals", "profile_signals", "ProfileSignals");
        var contentSignals = TryGetObjectAny(el, "contentSignals", "content_signals", "ContentSignals")
                             ?? TryGetObjectAny(el, "context", "Context");
        var provenanceInfo = TryGetObjectAny(el, "provenanceInfo", "provenance_info", "ProvenanceInfo");
        var documentExtractionConfidence = GetDoubleFromQualityOrRoot(
            el,
            extractionQuality,
            "documentExtractionConfidence", "document_extraction_confidence", "DocumentExtractionConfidence");
        var pageExtractionConfidence = GetDoubleFromQualityOrRoot(
            el,
            extractionQuality,
            "pageExtractionConfidence", "page_extraction_confidence", "PageExtractionConfidence");
        var genericExtractionConfidence = GetDoubleFromQualityOrRoot(
            el,
            extractionQuality,
            "extractionConfidence", "extraction_confidence", "ExtractionConfidence",
            "confidence", "Confidence");
        var documentManualReviewRecommended = GetBoolFromQualityOrRoot(
            el,
            extractionQuality,
            "documentManualReviewRecommended", "document_manual_review_recommended", "DocumentManualReviewRecommended");
        var pageManualReviewRecommended = GetBoolFromQualityOrRoot(
            el,
            extractionQuality,
            "pageManualReviewRecommended", "page_manual_review_recommended", "PageManualReviewRecommended");
        var genericManualReviewRecommended = GetBoolFromQualityOrRoot(
            el,
            extractionQuality,
            "manualReviewRecommended", "manual_review_recommended", "ManualReviewRecommended",
            "manualReview", "manual_review", "ManualReview",
            "reviewRecommended", "review_recommended", "ReviewRecommended",
            "manualReviewRequired", "manual_review_required", "ManualReviewRequired",
            "requiresManualReview", "requires_manual_review", "RequiresManualReview");
        var diagnosticSummary = ParseExtractionDiagnosticSummary(el, extractionQuality);

        result.Add(new SourceCard
        {
            DocId = GetStringAny(el, "docId", "doc_id", "DocId"),
            DocPath = docPath,
            DocName = docName,
            PageStart = pageStart,
            PageEnd = pageEnd,
            Snippet = snippet,
            Score = score,
            SourceHash = GetStringAny(el, "sourceHash", "source_hash", "SourceHash"),
            DocLanguage = GetStringAny(
                el,
                "docLanguage", "doc_language", "DocLanguage",
                "documentLanguage", "document_language", "DocumentLanguage",
                "sourceLanguage", "source_language", "SourceLanguage"),
            ProfileLanguage = GetStringAny(el, "profileLanguage", "profile_language", "ProfileLanguage"),
            Category = GetStringAny(el, "category", "Category"),
            CategoryRef = GetStringAny(el, "categoryRef", "category_ref", "CategoryRef"),
            CategoryPath = GetStringAny(el, "categoryPath", "category_path", "CategoryPath"),
            ChunkId = GetStringAny(el, "chunkId", "chunk_id", "ChunkId"),
            SectionTitle = GetStringFromContextHintsOrRoot(
                el,
                contentSignals,
                selectionHints,
                "sectionTitle", "section_title", "SectionTitle"),
            HeadingPath = GetStringFromContextHintsOrRoot(
                el,
                contentSignals,
                selectionHints,
                "headingPath", "heading_path", "HeadingPath"),
            PrevChunkId = GetStringFromContextHintsOrRoot(
                el,
                contentSignals,
                selectionHints,
                "prevChunkId", "prev_chunk_id", "PrevChunkId"),
            NextChunkId = GetStringFromContextHintsOrRoot(
                el,
                contentSignals,
                selectionHints,
                "nextChunkId", "next_chunk_id", "NextChunkId"),
            SameSectionChunkId = GetStringFromContextHintsOrRoot(
                el,
                contentSignals,
                selectionHints,
                "sameSectionChunkId", "same_section_chunk_id", "SameSectionChunkId"),
            OriginalChunkType = GetStringFromContextHintsOrRoot(
                el,
                contentSignals,
                selectionHints,
                "originalChunkType", "original_chunk_type", "OriginalChunkType"),
            OffsetStart = GetIntFromObjectOrRoot(el, provenanceInfo, "offsetStart", "offset_start", "OffsetStart"),
            OffsetEnd = GetIntFromObjectOrRoot(el, provenanceInfo, "offsetEnd", "offset_end", "OffsetEnd"),
            ExtractionSource = GetStringFromQualityOrRoot(
                el,
                extractionQuality,
                "extractionSource", "extraction_source", "ExtractionSource"),
            DocumentQualityStatus = GetStringFromQualityOrRoot(
                el,
                extractionQuality,
                "documentQualityStatus", "document_quality_status", "DocumentQualityStatus"),
            PageQualityStatus = GetStringFromQualityOrRoot(
                el,
                extractionQuality,
                "pageQualityStatus", "page_quality_status", "PageQualityStatus"),
            TextStatus = GetStringFromQualityOrRoot(
                el,
                extractionQuality,
                "textStatus", "text_status", "TextStatus"),
            QualityStatus = GetStringFromQualityOrRoot(
                el,
                extractionQuality,
                "qualityStatus", "quality_status", "QualityStatus",
                "quality", "Quality", "status", "Status",
                "pageQualityStatus", "page_quality_status", "PageQualityStatus",
                "documentQualityStatus", "document_quality_status", "DocumentQualityStatus"),
            ExtractionConfidence = genericExtractionConfidence ?? pageExtractionConfidence ?? documentExtractionConfidence,
            DocumentExtractionConfidence = documentExtractionConfidence,
            PageExtractionConfidence = pageExtractionConfidence,
            ManualReviewRecommended = genericManualReviewRecommended
                ?? pageManualReviewRecommended
                ?? documentManualReviewRecommended
                ?? false,
            DocumentManualReviewRecommended = documentManualReviewRecommended ?? false,
            PageManualReviewRecommended = pageManualReviewRecommended ?? false,
            OcrAttempted = GetBoolFromQualityOrRoot(
                el,
                extractionQuality,
                "ocrAttempted", "ocr_attempted", "OcrAttempted", "OCRAttempted") ?? false,
            OcrApplied = GetBoolFromQualityOrRoot(
                el,
                extractionQuality,
                "ocrApplied", "ocr_applied", "OcrApplied", "OCRApplied") ?? false,
            OcrRecommended = GetBoolFromQualityOrRoot(
                el,
                extractionQuality,
                "ocrRecommended", "ocr_recommended", "OcrRecommended", "OCRRecommended") ?? false,
            ExtractionDiagnosticSummary = diagnosticSummary,
            QualitySignals = (extractionQuality is null
                    ? ExtractSignals(el)
                    : ExtractSignals(extractionQuality.Value).Concat(ExtractSignals(el)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            MatchedContentCards = ExtractMatchedContentCards(el),
            ProfileSignals = ParseProfileSignals(profileSignals),
            SelectionHintEvidenceRole = selectionHints is null
                ? GetStringAny(el, "selectionHintEvidenceRole", "selection_hint_evidence_role", "evidenceRole", "EvidenceRole")
                : GetStringAny(selectionHints.Value, "evidenceRole", "evidence_role", "EvidenceRole"),
            SelectionHintActionabilityScore = selectionHints is null
                ? GetIntAny(el, "selectionHintActionabilityScore", "selection_hint_actionability_score", "actionabilityScore", "ActionabilityScore")
                : GetIntAny(selectionHints.Value, "actionabilityScore", "actionability_score", "ActionabilityScore"),
            SelectionHintSupportScore = selectionHints is null
                ? GetIntAny(el, "selectionHintSupportScore", "selection_hint_support_score", "supportScore", "SupportScore")
                : GetIntAny(selectionHints.Value, "supportScore", "support_score", "SupportScore"),
            SelectionHintFragmentScore = selectionHints is null
                ? GetIntAny(el, "selectionHintFragmentScore", "selection_hint_fragment_score", "fragmentScore", "FragmentScore")
                : GetIntAny(selectionHints.Value, "fragmentScore", "fragment_score", "FragmentScore"),
            SelectionHintNavigationScore = selectionHints is null
                ? GetIntAny(el, "selectionHintNavigationScore", "selection_hint_navigation_score", "navigationScore", "NavigationScore")
                : GetIntAny(selectionHints.Value, "navigationScore", "navigation_score", "NavigationScore"),
            SelectionHintQualityPenalty = selectionHints is null
                ? GetIntAny(el, "selectionHintQualityPenalty", "selection_hint_quality_penalty", "qualityPenalty", "QualityPenalty")
                : GetIntAny(selectionHints.Value, "qualityPenalty", "quality_penalty", "QualityPenalty"),
            ContentRole = GetStringFromContextHintsOrRoot(
                el,
                contentSignals,
                selectionHints,
                "contentRole", "content_role", "ContentRole"),
            NavigationReason = GetStringFromContextHintsOrRoot(
                el,
                contentSignals,
                selectionHints,
                "navigationReason", "navigation_reason", "NavigationReason"),
            RetrievalNavigationScore = GetDoubleFromContextHintsOrRoot(
                el,
                contentSignals,
                selectionHints,
                "navigationScore", "navigation_score", "NavigationScore",
                hintNames: new[] { "retrievalNavigationScore", "retrieval_navigation_score", "RetrievalNavigationScore" }),
            ContentDensityScore = GetDoubleFromContextHintsOrRoot(
                el,
                contentSignals,
                selectionHints,
                "contentDensityScore", "content_density_score", "ContentDensityScore")
        });
    }

    private static string? GetStringFromContextHintsOrRoot(
        JsonElement root,
        JsonElement? contentSignals,
        JsonElement? selectionHints,
        params string[] names)
    {
        if (contentSignals is { } context)
        {
            var value = GetStringAny(context, names);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        if (selectionHints is { } hints)
        {
            var value = GetStringAny(hints, names);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return GetStringAny(root, names);
    }

    private static int? GetIntFromObjectOrRoot(JsonElement root, JsonElement? value, params string[] names)
        => value is { } objectValue
            ? GetIntAny(objectValue, names) ?? GetIntAny(root, names)
            : GetIntAny(root, names);

    private static double? GetDoubleFromContextHintsOrRoot(
        JsonElement root,
        JsonElement? contentSignals,
        JsonElement? selectionHints,
        string primaryName,
        string snakeName,
        string pascalName,
        string[]? hintNames = null)
    {
        if (contentSignals is { } context)
        {
            var value = GetDoubleAny(context, primaryName, snakeName, pascalName);
            if (value.HasValue)
                return value;
        }

        if (selectionHints is { } hints)
        {
            var names = hintNames ?? new[] { primaryName, snakeName, pascalName };
            var value = GetDoubleAny(hints, names);
            if (value.HasValue)
                return value;
        }

        return GetDoubleAny(root, primaryName, snakeName, pascalName);
    }

    private static SourceExtractionDiagnosticSummary? ParseExtractionDiagnosticSummary(JsonElement root, JsonElement? extractionQuality)
    {
        var diagnostics = extractionQuality is null
            ? TryGetObjectAny(root, "diagnosticSummary", "diagnostic_summary", "extractionDiagnosticSummary", "extraction_diagnostic_summary", "DiagnosticSummary", "ExtractionDiagnosticSummary", "diagnostics", "Diagnostics")
            : TryGetObjectAny(extractionQuality.Value, "diagnosticSummary", "diagnostic_summary", "extractionDiagnosticSummary", "extraction_diagnostic_summary", "DiagnosticSummary", "ExtractionDiagnosticSummary", "diagnostics", "Diagnostics")
              ?? TryGetObjectAny(root, "diagnosticSummary", "diagnostic_summary", "extractionDiagnosticSummary", "extraction_diagnostic_summary", "DiagnosticSummary", "ExtractionDiagnosticSummary", "diagnostics", "Diagnostics");

        if (diagnostics is null)
            return null;

        var value = diagnostics.Value;
        var summary = new SourceExtractionDiagnosticSummary
        {
            NativeTextStatus = GetStringAny(value, "nativeTextStatus", "native_text_status", "NativeTextStatus"),
            NativeOcrRecommended = GetBoolAny(value, "nativeOcrRecommended", "native_ocr_recommended", "NativeOcrRecommended"),
            OcrMode = GetStringAny(value, "ocrMode", "ocr_mode", "OcrMode", "OCRMode"),
            OcrLanguages = GetStringAny(value, "ocrLanguages", "ocr_languages", "OcrLanguages", "OCRLanguages"),
            OcrDurationMs = GetLongAny(value, "ocrDurationMs", "ocr_duration_ms", "OcrDurationMs", "OCRDurationMs"),
            OcrFailureReason = GetStringAny(value, "ocrFailureReason", "ocr_failure_reason", "OcrFailureReason", "OCRFailureReason"),
            OcrAppliedReason = GetStringAny(value, "ocrAppliedReason", "ocr_applied_reason", "OcrAppliedReason", "OCRAppliedReason"),
            OcrTimedOut = GetBoolAny(value, "ocrTimedOut", "ocr_timed_out", "OcrTimedOut", "OCRTimedOut", "timedOut", "timed_out"),
            OcrAttemptedPageCount = GetIntAny(value, "ocrAttemptedPageCount", "ocr_attempted_page_count", "OcrAttemptedPageCount"),
            OcrSkippedPageCount = GetIntAny(value, "ocrSkippedPageCount", "ocr_skipped_page_count", "OcrSkippedPageCount"),
            OcrPagesWithNovelTextCount = GetIntAny(value, "ocrPagesWithNovelTextCount", "ocr_pages_with_novel_text_count", "OcrPagesWithNovelTextCount"),
            PageCount = GetIntAny(value, "pageCount", "page_count", "PageCount"),
            TextPageCount = GetIntAny(value, "textPageCount", "text_page_count", "TextPageCount"),
            EmptyPageCount = GetIntAny(value, "emptyPageCount", "empty_page_count", "EmptyPageCount"),
            SparsePageCount = GetIntAny(value, "sparsePageCount", "sparse_page_count", "SparsePageCount"),
            ImagePageCount = GetIntAny(value, "imagePageCount", "image_page_count", "ImagePageCount"),
            PageWarningCount = GetIntAny(value, "pageWarningCount", "page_warning_count", "PageWarningCount"),
            PageReviewRecommendedCount = GetIntAny(value, "pageReviewRecommendedCount", "page_review_recommended_count", "PageReviewRecommendedCount")
        };

        return HasDiagnosticValue(summary) ? summary : null;
    }

    private static bool HasDiagnosticValue(SourceExtractionDiagnosticSummary summary)
        => !string.IsNullOrWhiteSpace(summary.NativeTextStatus)
           || summary.NativeOcrRecommended is not null
           || !string.IsNullOrWhiteSpace(summary.OcrMode)
           || !string.IsNullOrWhiteSpace(summary.OcrLanguages)
           || summary.OcrDurationMs is not null
           || !string.IsNullOrWhiteSpace(summary.OcrFailureReason)
           || !string.IsNullOrWhiteSpace(summary.OcrAppliedReason)
           || summary.OcrTimedOut is not null
           || summary.OcrAttemptedPageCount is not null
           || summary.OcrSkippedPageCount is not null
           || summary.OcrPagesWithNovelTextCount is not null
           || summary.PageCount is not null
           || summary.TextPageCount is not null
           || summary.EmptyPageCount is not null
           || summary.SparsePageCount is not null
           || summary.ImagePageCount is not null
           || summary.PageWarningCount is not null
           || summary.PageReviewRecommendedCount is not null;

    private static JsonElement? TryGetArray(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array)
                return p;
        }

        return null;
    }

    private static JsonElement? TryGetObjectAny(JsonElement el, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (el.ValueKind == JsonValueKind.Object
                && el.TryGetProperty(k, out var p)
                && p.ValueKind == JsonValueKind.Object)
            {
                return p;
            }
        }

        return null;
    }

    private static string? GetStringFromQualityOrRoot(JsonElement root, JsonElement? extractionQuality, params string[] keys)
        => extractionQuality is null
            ? GetStringAny(root, keys)
            : GetStringAny(extractionQuality.Value, keys) ?? GetStringAny(root, keys);

    private static double? GetDoubleFromQualityOrRoot(JsonElement root, JsonElement? extractionQuality, params string[] keys)
        => extractionQuality is null
            ? GetDoubleAny(root, keys)
            : GetDoubleAny(extractionQuality.Value, keys) ?? GetDoubleAny(root, keys);

    private static bool? GetBoolFromQualityOrRoot(JsonElement root, JsonElement? extractionQuality, params string[] keys)
        => extractionQuality is null
            ? GetBoolAny(root, keys)
            : GetBoolAny(extractionQuality.Value, keys) ?? GetBoolAny(root, keys);

    private static string? GetStringAny(JsonElement el, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(k, out var p))
            {
                if (p.ValueKind == JsonValueKind.String) return p.GetString();
                if (p.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    return p.ToString();
            }
        }
        return null;
    }

    private static bool? GetBoolAny(JsonElement el, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(k, out var p))
            {
                if (p.ValueKind == JsonValueKind.True) return true;
                if (p.ValueKind == JsonValueKind.False) return false;
                if (p.ValueKind == JsonValueKind.String && bool.TryParse(p.GetString(), out var value)) return value;
            }
        }

        return null;
    }

    private static List<SourceContentCard> ExtractMatchedContentCards(JsonElement el)
    {
        var result = new List<SourceContentCard>();
        if (el.ValueKind != JsonValueKind.Object
            || (!el.TryGetProperty("matchedContentCards", out var cards)
                && !el.TryGetProperty("matched_content_cards", out cards)
                && !el.TryGetProperty("MatchedContentCards", out cards)
                && !el.TryGetProperty("contentCards", out cards)
                && !el.TryGetProperty("content_cards", out cards)
                && !el.TryGetProperty("ContentCards", out cards))
            || cards.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var card in cards.EnumerateArray())
        {
            if (card.ValueKind != JsonValueKind.Object)
                continue;

            var title = GetStringAny(card, "title", "Title");
            if (string.IsNullOrWhiteSpace(title))
                continue;

            result.Add(new SourceContentCard
            {
                Title = title.Trim(),
                ContentCardId = GetStringAny(card, "contentCardId", "content_card_id", "ContentCardId"),
                PageStart = GetIntAny(card, "pageStart", "page_start", "PageStart"),
                PageEnd = GetIntAny(card, "pageEnd", "page_end", "PageEnd"),
                Kind = GetStringAny(card, "kind", "Kind"),
                Signals = ExtractSignals(card).ToList(),
                Evidence = ExtractEvidence(card)
            });
        }

        return result;
    }

    private static SourceProfileSignals? ParseProfileSignals(JsonElement? profile)
    {
        if (profile is not { ValueKind: JsonValueKind.Object } value)
            return null;

        var result = new SourceProfileSignals
        {
            ProfileVersion = GetStringAny(value, "profileVersion", "profile_version", "ProfileVersion"),
            Language = GetStringAny(value, "language", "Language", "profileLanguage", "ProfileLanguage"),
            Keywords = ExtractStringList(value, "keywords", "keywordMatches", "Keywords", "KeywordMatches").Take(8).ToList(),
            Entities = ExtractStringList(value, "entities", "entityMatches", "Entities", "EntityMatches").Take(8).ToList(),
            Topics = ExtractStringList(value, "topics", "topicMatches", "Topics", "TopicMatches").Take(8).ToList(),
            HypotheticalQuestions = ExtractStringList(value, "hypotheticalQuestions", "hypothetical_questions", "HypotheticalQuestions").Take(4).ToList(),
            Limits = ExtractStringList(value, "limits", "limitMatches", "Limits", "LimitMatches").Take(4).ToList(),
            MatchedTerms = ExtractStringList(value, "matchedTerms", "matched_terms", "MatchedTerms").Take(12).ToList(),
            MatchCount = GetIntAny(value, "matchCount", "match_count", "MatchCount")
        };

        var hasValues = !string.IsNullOrWhiteSpace(result.ProfileVersion)
                        || !string.IsNullOrWhiteSpace(result.Language)
                        || result.Keywords.Count > 0
                        || result.Entities.Count > 0
                        || result.Topics.Count > 0
                        || result.HypotheticalQuestions.Count > 0
                        || result.Limits.Count > 0
                        || result.MatchedTerms.Count > 0
                        || result.MatchCount is > 0;

        return hasValues ? result : null;
    }

    private static IEnumerable<string> ExtractStringList(JsonElement source, params string[] names)
    {
        foreach (var name in names)
        {
            if (source.ValueKind != JsonValueKind.Object
                || !source.TryGetProperty(name, out var values)
                || values.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var value in values.EnumerateArray())
            {
                if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                    yield return CompactProfileSignalValue(value.GetString()!);
            }
        }
    }

    private static string CompactProfileSignalValue(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 160 ? trimmed : trimmed[..160].TrimEnd();
    }

    private static JsonElement? ExtractEvidence(JsonElement card)
    {
        if (card.ValueKind != JsonValueKind.Object
            || (!card.TryGetProperty("evidence", out var evidence)
                && !card.TryGetProperty("Evidence", out evidence)
                && !card.TryGetProperty("structuredEvidence", out evidence)
                && !card.TryGetProperty("structured_evidence", out evidence))
            || evidence.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return evidence.Clone();
    }

    private static IEnumerable<string> ExtractSignals(JsonElement card)
    {
        if (card.ValueKind != JsonValueKind.Object
            || (!card.TryGetProperty("signals", out var signals)
                && !card.TryGetProperty("Signals", out signals)
                && !card.TryGetProperty("qualitySignals", out signals)
                && !card.TryGetProperty("quality_signals", out signals)
                && !card.TryGetProperty("QualitySignals", out signals))
            || signals.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var signal in signals.EnumerateArray())
        {
            if (signal.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(signal.GetString()))
                yield return signal.GetString()!.Trim();
        }
    }

    private static int? GetIntAny(JsonElement el, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(k, out var p))
            {
                if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n)) return n;
                if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out var ns)) return ns;
            }
        }
        return null;
    }

    private static double? GetDoubleAny(JsonElement el, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(k, out var p))
            {
                if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d)) return d;
                if (p.ValueKind == JsonValueKind.String
                    && (double.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ds)
                        || double.TryParse(p.GetString(), out ds)))
                {
                    return ds;
                }
            }
        }
        return null;
    }

    private static long? GetLongAny(JsonElement el, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(k, out var p))
            {
                if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var n)) return n;
                if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), out var ns)) return ns;
            }
        }
        return null;
    }

    private static string SafeFileName(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        path = path.Replace('\\', '/');
        var idx = path.LastIndexOf('/');
        return idx >= 0 ? path[(idx + 1)..] : path;
    }
}
