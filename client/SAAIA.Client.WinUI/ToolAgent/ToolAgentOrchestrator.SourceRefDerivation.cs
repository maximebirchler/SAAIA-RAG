using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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

    private static bool TryParseAnswerEnvelope(string json, out string? finalAnswer, out List<ToolMemory.SourceRef>? sources)
    {
        finalAnswer = null;
        sources = null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("finalAnswer", out var a) && a.ValueKind == JsonValueKind.String)
                finalAnswer = a.GetString();
            else
                finalAnswer = "";

            if (root.TryGetProperty("sources", out var sArr) && sArr.ValueKind == JsonValueKind.Array)
            {
                var list = new List<ToolMemory.SourceRef>();
                foreach (var s in sArr.EnumerateArray())
                {
                    if (s.ValueKind != JsonValueKind.Object) continue;

                    var source = TryBuildSourceRefFromJsonElement(s);
                    if (source is not null)
                        list.Add(source);
                }

                sources = list;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ToolMemory.SourceRef? TryBuildSourceFromResolveResult(ToolResults toolResults)
    {
        try
        {
            var item = toolResults.Items.LastOrDefault(x => x.ToolName == "sources.resolve");
            if (item is null) return null;

            var res = item.Result;
            if (res.ValueKind != JsonValueKind.Object) return null;
            if (!res.TryGetProperty("source", out var src) || src.ValueKind != JsonValueKind.Object) return null;

            return TryBuildSourceRefFromJsonElement(src);
        }
        catch
        {
            return null;
        }
    }

    private static ToolMemory.SourceRef? TryBuildSourceRefFromJsonElement(JsonElement src)
    {
        if (src.ValueKind != JsonValueKind.Object)
            return null;

        var docPath = TryGetString(src, "docPath") ?? TryGetString(src, "doc_path") ?? TryGetString(src, "path") ?? "";
        if (string.IsNullOrWhiteSpace(docPath))
            return null;

        var ps = TryGetInt(src, "pageStart") ?? TryGetInt(src, "page_start") ?? TryGetInt(src, "page") ?? 1;
        var pe = TryGetInt(src, "pageEnd") ?? TryGetInt(src, "page_end") ?? ps;
        ps = Math.Max(1, ps);
        pe = Math.Max(ps, pe);

        var docName = NullIfWhiteSpace(TryGetString(src, "docName") ?? TryGetString(src, "doc_name") ?? TryGetString(src, "DocName"));
        var label = TryGetString(src, "label")
            ?? docName
            ?? Path.GetFileName(docPath);
        if (string.IsNullOrWhiteSpace(label))
            label = docPath;

        var quality = ExtractRagHitExtractionQualitySignals(src);
        var chunkQuality = ExtractRagHitChunkQualitySignals(src);
        var qualityElement = TryGetObject(src, "extractionQuality") ?? TryGetObject(src, "extraction_quality") ?? TryGetObject(src, "ExtractionQuality");
        var contentSignalsElement =
            TryGetObject(src, "contentSignals")
            ?? TryGetObject(src, "content_signals")
            ?? TryGetObject(src, "ContentSignals")
            ?? TryGetObject(src, "context")
            ?? TryGetObject(src, "Context");
        var diagnosticSummary = TryBuildSourceExtractionDiagnosticRef(src, qualityElement);
        var extractionSource = qualityElement.HasValue
            ? TryGetString(qualityElement.Value, "extractionSource") ?? TryGetString(qualityElement.Value, "extraction_source") ?? TryGetString(qualityElement.Value, "ExtractionSource")
            : TryGetString(src, "extractionSource") ?? TryGetString(src, "extraction_source") ?? TryGetString(src, "ExtractionSource");
        var documentQualityStatus = qualityElement.HasValue
            ? TryGetString(qualityElement.Value, "documentQualityStatus") ?? TryGetString(qualityElement.Value, "document_quality_status") ?? TryGetString(qualityElement.Value, "DocumentQualityStatus")
            : TryGetString(src, "documentQualityStatus") ?? TryGetString(src, "document_quality_status") ?? TryGetString(src, "DocumentQualityStatus");
        var pageQualityStatus = qualityElement.HasValue
            ? TryGetString(qualityElement.Value, "pageQualityStatus") ?? TryGetString(qualityElement.Value, "page_quality_status") ?? TryGetString(qualityElement.Value, "PageQualityStatus")
            : TryGetString(src, "pageQualityStatus") ?? TryGetString(src, "page_quality_status") ?? TryGetString(src, "PageQualityStatus");
        var textStatus = qualityElement.HasValue
            ? TryGetString(qualityElement.Value, "textStatus") ?? TryGetString(qualityElement.Value, "text_status") ?? TryGetString(qualityElement.Value, "TextStatus")
            : TryGetString(src, "textStatus") ?? TryGetString(src, "text_status") ?? TryGetString(src, "TextStatus");
        var ocrAttempted = qualityElement.HasValue
            ? TryGetBool(qualityElement.Value, "ocrAttempted") ?? TryGetBool(qualityElement.Value, "ocr_attempted") ?? TryGetBool(qualityElement.Value, "OcrAttempted") ?? TryGetBool(qualityElement.Value, "OCRAttempted") ?? false
            : TryGetBool(src, "ocrAttempted") ?? TryGetBool(src, "ocr_attempted") ?? TryGetBool(src, "OcrAttempted") ?? TryGetBool(src, "OCRAttempted") ?? false;
        var ocrRecommended = qualityElement.HasValue
            ? TryGetBool(qualityElement.Value, "ocrRecommended") ?? TryGetBool(qualityElement.Value, "ocr_recommended") ?? TryGetBool(qualityElement.Value, "OcrRecommended") ?? false
            : TryGetBool(src, "ocrRecommended") ?? TryGetBool(src, "ocr_recommended") ?? TryGetBool(src, "OcrRecommended") ?? false;
        var signals = (quality.Signals ?? Array.Empty<string>())
            .Concat(qualityElement.HasValue ? ExtractCompactSignals(qualityElement.Value, "signals") : Array.Empty<string>())
            .Concat(qualityElement.HasValue ? ExtractCompactSignals(qualityElement.Value, "Signals") : Array.Empty<string>())
            .Where(static signal => !string.IsNullOrWhiteSpace(signal))
            .Select(static signal => signal.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
        var chunkSignals = (chunkQuality.Signals ?? Array.Empty<string>())
            .Where(static signal => !string.IsNullOrWhiteSpace(signal))
            .Select(static signal => signal.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        var cards = ExtractRagHitMatchedContentCards(src)?
            .Where(static card => !string.IsNullOrWhiteSpace(card.Title))
            .Select(static card => new ToolMemory.SourceContentCardRef
            {
                Title = card.Title,
                ContentCardId = NullIfWhiteSpace(card.ContentCardId),
                PageStart = card.PageStart,
                PageEnd = card.PageEnd,
                Kind = NullIfWhiteSpace(card.Kind),
                Signals = card.Signals?
                    .Where(static signal => !string.IsNullOrWhiteSpace(signal))
                    .Select(static signal => signal.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(8)
                    .ToList() ?? new List<string>(),
                Evidence = GetSourceContentCardEvidenceElement(card)
            })
            .Take(5)
            .ToList() ?? new List<ToolMemory.SourceContentCardRef>();
        var selectionHints = TryGetObject(src, "selectionHints") ?? TryGetObject(src, "selection_hints") ?? TryGetObject(src, "SelectionHints");
        var provenanceInfo = TryGetObject(src, "provenanceInfo") ?? TryGetObject(src, "provenance_info") ?? TryGetObject(src, "ProvenanceInfo");
        var sectionTitle = contentSignalsElement.HasValue
            ? TryGetString(contentSignalsElement.Value, "sectionTitle") ?? TryGetString(contentSignalsElement.Value, "section_title") ?? TryGetString(contentSignalsElement.Value, "SectionTitle")
            : null;
        sectionTitle ??= TryGetString(src, "sectionTitle") ?? TryGetString(src, "section_title") ?? TryGetString(src, "SectionTitle");
        var headingPath = contentSignalsElement.HasValue
            ? TryGetString(contentSignalsElement.Value, "headingPath") ?? TryGetString(contentSignalsElement.Value, "heading_path") ?? TryGetString(contentSignalsElement.Value, "HeadingPath")
            : null;
        headingPath ??= TryGetString(src, "headingPath") ?? TryGetString(src, "heading_path") ?? TryGetString(src, "HeadingPath");
        var prevChunkId = contentSignalsElement.HasValue
            ? TryGetString(contentSignalsElement.Value, "prevChunkId") ?? TryGetString(contentSignalsElement.Value, "prev_chunk_id") ?? TryGetString(contentSignalsElement.Value, "PrevChunkId")
            : null;
        prevChunkId ??= TryGetString(src, "prevChunkId") ?? TryGetString(src, "prev_chunk_id") ?? TryGetString(src, "PrevChunkId");
        var nextChunkId = contentSignalsElement.HasValue
            ? TryGetString(contentSignalsElement.Value, "nextChunkId") ?? TryGetString(contentSignalsElement.Value, "next_chunk_id") ?? TryGetString(contentSignalsElement.Value, "NextChunkId")
            : null;
        nextChunkId ??= TryGetString(src, "nextChunkId") ?? TryGetString(src, "next_chunk_id") ?? TryGetString(src, "NextChunkId");
        var sameSectionChunkId = contentSignalsElement.HasValue
            ? TryGetString(contentSignalsElement.Value, "sameSectionChunkId") ?? TryGetString(contentSignalsElement.Value, "same_section_chunk_id") ?? TryGetString(contentSignalsElement.Value, "SameSectionChunkId")
            : null;
        sameSectionChunkId ??= TryGetString(src, "sameSectionChunkId") ?? TryGetString(src, "same_section_chunk_id") ?? TryGetString(src, "SameSectionChunkId");
        var originalChunkType = contentSignalsElement.HasValue
            ? TryGetString(contentSignalsElement.Value, "originalChunkType") ?? TryGetString(contentSignalsElement.Value, "original_chunk_type") ?? TryGetString(contentSignalsElement.Value, "OriginalChunkType")
            : null;
        originalChunkType ??= TryGetString(src, "originalChunkType") ?? TryGetString(src, "original_chunk_type") ?? TryGetString(src, "OriginalChunkType");
        var offsetStart = provenanceInfo.HasValue
            ? TryGetInt(provenanceInfo.Value, "offsetStart") ?? TryGetInt(provenanceInfo.Value, "offset_start") ?? TryGetInt(provenanceInfo.Value, "OffsetStart")
            : null;
        offsetStart ??= TryGetInt(src, "offsetStart") ?? TryGetInt(src, "offset_start") ?? TryGetInt(src, "OffsetStart");
        var offsetEnd = provenanceInfo.HasValue
            ? TryGetInt(provenanceInfo.Value, "offsetEnd") ?? TryGetInt(provenanceInfo.Value, "offset_end") ?? TryGetInt(provenanceInfo.Value, "OffsetEnd")
            : null;
        offsetEnd ??= TryGetInt(src, "offsetEnd") ?? TryGetInt(src, "offset_end") ?? TryGetInt(src, "OffsetEnd");
        var contentRole = contentSignalsElement.HasValue
            ? TryGetString(contentSignalsElement.Value, "contentRole") ?? TryGetString(contentSignalsElement.Value, "content_role") ?? TryGetString(contentSignalsElement.Value, "ContentRole")
            : null;
        contentRole ??= TryGetString(src, "contentRole") ?? TryGetString(src, "content_role") ?? TryGetString(src, "ContentRole");
        var navigationReason = contentSignalsElement.HasValue
            ? TryGetString(contentSignalsElement.Value, "navigationReason") ?? TryGetString(contentSignalsElement.Value, "navigation_reason") ?? TryGetString(contentSignalsElement.Value, "NavigationReason")
            : null;
        navigationReason ??= TryGetString(src, "navigationReason") ?? TryGetString(src, "navigation_reason") ?? TryGetString(src, "NavigationReason");
        var retrievalNavigationScore = contentSignalsElement.HasValue
            ? TryGetDouble(contentSignalsElement.Value, "retrievalNavigationScore") ?? TryGetDouble(contentSignalsElement.Value, "retrieval_navigation_score") ?? TryGetDouble(contentSignalsElement.Value, "RetrievalNavigationScore") ?? TryGetDouble(contentSignalsElement.Value, "navigationScore") ?? TryGetDouble(contentSignalsElement.Value, "navigation_score") ?? TryGetDouble(contentSignalsElement.Value, "NavigationScore")
            : null;
        retrievalNavigationScore ??= TryGetDouble(src, "retrievalNavigationScore") ?? TryGetDouble(src, "retrieval_navigation_score") ?? TryGetDouble(src, "RetrievalNavigationScore") ?? TryGetDouble(src, "navigationScore") ?? TryGetDouble(src, "navigation_score") ?? TryGetDouble(src, "NavigationScore");
        var contentDensityScore = contentSignalsElement.HasValue
            ? TryGetDouble(contentSignalsElement.Value, "contentDensityScore") ?? TryGetDouble(contentSignalsElement.Value, "content_density_score") ?? TryGetDouble(contentSignalsElement.Value, "ContentDensityScore")
            : null;
        contentDensityScore ??= TryGetDouble(src, "contentDensityScore") ?? TryGetDouble(src, "content_density_score") ?? TryGetDouble(src, "ContentDensityScore");
        var sourceUnitOrdinals = contentSignalsElement.HasValue
            ? TryGetIntList(contentSignalsElement.Value, "sourceUnitOrdinals", "source_unit_ordinals", "SourceUnitOrdinals")
            : new List<int>();
        if (sourceUnitOrdinals.Count == 0)
            sourceUnitOrdinals = TryGetIntList(src, "sourceUnitOrdinals", "source_unit_ordinals", "SourceUnitOrdinals");
        var sourceUnitStartOrdinal = contentSignalsElement.HasValue
            ? TryGetInt(contentSignalsElement.Value, "sourceUnitStartOrdinal") ?? TryGetInt(contentSignalsElement.Value, "source_unit_start_ordinal") ?? TryGetInt(contentSignalsElement.Value, "SourceUnitStartOrdinal")
            : null;
        sourceUnitStartOrdinal ??= TryGetInt(src, "sourceUnitStartOrdinal") ?? TryGetInt(src, "source_unit_start_ordinal") ?? TryGetInt(src, "SourceUnitStartOrdinal");
        var sourceUnitEndOrdinal = contentSignalsElement.HasValue
            ? TryGetInt(contentSignalsElement.Value, "sourceUnitEndOrdinal") ?? TryGetInt(contentSignalsElement.Value, "source_unit_end_ordinal") ?? TryGetInt(contentSignalsElement.Value, "SourceUnitEndOrdinal")
            : null;
        sourceUnitEndOrdinal ??= TryGetInt(src, "sourceUnitEndOrdinal") ?? TryGetInt(src, "source_unit_end_ordinal") ?? TryGetInt(src, "SourceUnitEndOrdinal");
        var sourceUnitCount = contentSignalsElement.HasValue
            ? TryGetInt(contentSignalsElement.Value, "sourceUnitCount") ?? TryGetInt(contentSignalsElement.Value, "source_unit_count") ?? TryGetInt(contentSignalsElement.Value, "SourceUnitCount")
            : null;
        sourceUnitCount ??= TryGetInt(src, "sourceUnitCount") ?? TryGetInt(src, "source_unit_count") ?? TryGetInt(src, "SourceUnitCount");
        var chunkComposition = contentSignalsElement.HasValue
            ? TryGetString(contentSignalsElement.Value, "chunkComposition") ?? TryGetString(contentSignalsElement.Value, "chunk_composition") ?? TryGetString(contentSignalsElement.Value, "ChunkComposition")
            : null;
        chunkComposition ??= TryGetString(src, "chunkComposition") ?? TryGetString(src, "chunk_composition") ?? TryGetString(src, "ChunkComposition");

        return new ToolMemory.SourceRef
        {
            EvidenceId = NullIfWhiteSpace(TryGetString(src, "evidenceId") ?? TryGetString(src, "evidence_id") ?? TryGetString(src, "EvidenceId")),
            DocId = NullIfWhiteSpace(TryGetString(src, "docId") ?? TryGetString(src, "doc_id") ?? TryGetString(src, "DocId")),
            DocPath = docPath.Replace('\\', '/'),
            DocName = docName,
            PageStart = ps,
            PageEnd = pe,
            Label = label,
            SourceHash = NullIfWhiteSpace(TryGetString(src, "sourceHash") ?? TryGetString(src, "source_hash") ?? TryGetString(src, "SourceHash")),
            RevisionId = NullIfWhiteSpace(TryGetString(src, "revisionId") ?? TryGetString(src, "revision_id") ?? TryGetString(src, "RevisionId")),
            DocLanguage = NullIfWhiteSpace(TryGetDocumentLanguage(src)),
            ProfileLanguage = NullIfWhiteSpace(TryGetString(src, "profileLanguage") ?? TryGetString(src, "profile_language") ?? TryGetString(src, "ProfileLanguage")),
            Category = NullIfWhiteSpace(TryGetString(src, "category") ?? TryGetString(src, "Category")),
            CategoryRef = NullIfWhiteSpace(TryGetString(src, "categoryRef") ?? TryGetString(src, "category_ref") ?? TryGetString(src, "CategoryRef")),
            CategoryPath = NullIfWhiteSpace(TryGetString(src, "categoryPath") ?? TryGetString(src, "category_path") ?? TryGetString(src, "CategoryPath")),
            ChunkId = NullIfWhiteSpace(TryGetString(src, "chunkId") ?? TryGetString(src, "chunk_id") ?? TryGetString(src, "ChunkId")),
            AnchorId = NullIfWhiteSpace(TryGetString(src, "anchorId") ?? TryGetString(src, "anchor_id") ?? TryGetString(src, "AnchorId")),
            ContentCardId = NullIfWhiteSpace(TryGetString(src, "contentCardId") ?? TryGetString(src, "content_card_id") ?? TryGetString(src, "ContentCardId")),
            SectionTitle = NullIfWhiteSpace(sectionTitle),
            HeadingPath = NullIfWhiteSpace(headingPath),
            PrevChunkId = NullIfWhiteSpace(prevChunkId),
            NextChunkId = NullIfWhiteSpace(nextChunkId),
            SameSectionChunkId = NullIfWhiteSpace(sameSectionChunkId),
            OriginalChunkType = NullIfWhiteSpace(originalChunkType),
            OffsetStart = offsetStart,
            OffsetEnd = offsetEnd,
            SourceUnitOrdinals = sourceUnitOrdinals,
            SourceUnitStartOrdinal = sourceUnitStartOrdinal,
            SourceUnitEndOrdinal = sourceUnitEndOrdinal,
            SourceUnitCount = sourceUnitCount,
            ChunkComposition = NullIfWhiteSpace(chunkComposition),
            ExtractionSource = NullIfWhiteSpace(extractionSource),
            DocumentQualityStatus = NullIfWhiteSpace(documentQualityStatus),
            PageQualityStatus = NullIfWhiteSpace(pageQualityStatus),
            TextStatus = NullIfWhiteSpace(textStatus),
            ChunkTextStatus = NullIfWhiteSpace(chunkQuality.ChunkTextStatus),
            ChunkTextSparse = chunkQuality.ChunkTextSparse,
            ChunkOcrCandidate = chunkQuality.ChunkOcrCandidate,
            QualityStatus = NullIfWhiteSpace(quality.QualityStatus ?? pageQualityStatus ?? documentQualityStatus),
            ExtractionConfidence = quality.ExtractionConfidence,
            DocumentExtractionConfidence = quality.DocumentExtractionConfidence,
            PageExtractionConfidence = quality.PageExtractionConfidence,
            ManualReviewRecommended = quality.ManualReviewRecommended,
            DocumentManualReviewRecommended = quality.DocumentManualReviewRecommended,
            PageManualReviewRecommended = quality.PageManualReviewRecommended,
            OcrAttempted = ocrAttempted,
            OcrApplied = quality.OcrApplied,
            OcrRecommended = ocrRecommended,
            ExtractionDiagnosticSummary = diagnosticSummary,
            QualitySignals = signals,
            ChunkQualitySignals = chunkSignals,
            MatchedContentCards = cards,
            ProfileSignals = BuildSourceProfileSignalsRef(src),
            SelectionHintEvidenceRole = selectionHints.HasValue
                ? NullIfWhiteSpace(TryGetString(selectionHints.Value, "evidenceRole") ?? TryGetString(selectionHints.Value, "evidence_role") ?? TryGetString(selectionHints.Value, "EvidenceRole"))
                : null,
            SelectionHintActionabilityScore = selectionHints.HasValue
                ? TryGetInt(selectionHints.Value, "actionabilityScore") ?? TryGetInt(selectionHints.Value, "actionability_score") ?? TryGetInt(selectionHints.Value, "ActionabilityScore")
                : null,
            SelectionHintSupportScore = selectionHints.HasValue
                ? TryGetInt(selectionHints.Value, "supportScore") ?? TryGetInt(selectionHints.Value, "support_score") ?? TryGetInt(selectionHints.Value, "SupportScore")
                : null,
            SelectionHintFragmentScore = selectionHints.HasValue
                ? TryGetInt(selectionHints.Value, "fragmentScore") ?? TryGetInt(selectionHints.Value, "fragment_score") ?? TryGetInt(selectionHints.Value, "FragmentScore")
                : null,
            SelectionHintNavigationScore = selectionHints.HasValue
                ? TryGetInt(selectionHints.Value, "navigationScore") ?? TryGetInt(selectionHints.Value, "navigation_score") ?? TryGetInt(selectionHints.Value, "NavigationScore")
                : null,
            SelectionHintQualityPenalty = selectionHints.HasValue
                ? TryGetInt(selectionHints.Value, "qualityPenalty") ?? TryGetInt(selectionHints.Value, "quality_penalty") ?? TryGetInt(selectionHints.Value, "QualityPenalty")
                : null,
            ContentRole = NullIfWhiteSpace(contentRole),
            NavigationReason = NullIfWhiteSpace(navigationReason),
            RetrievalNavigationScore = retrievalNavigationScore,
            ContentDensityScore = contentDensityScore
        };
    }

    private static string? TryGetDocumentLanguage(JsonElement value)
        => TryGetString(value, "docLanguage")
           ?? TryGetString(value, "doc_language")
           ?? TryGetString(value, "DocLanguage")
           ?? TryGetString(value, "documentLanguage")
           ?? TryGetString(value, "document_language")
           ?? TryGetString(value, "DocumentLanguage")
           ?? TryGetString(value, "sourceLanguage")
           ?? TryGetString(value, "source_language")
           ?? TryGetString(value, "SourceLanguage");




    private static List<ToolMemory.SourceRef> DeriveSourcesFromRankedRagHits(ToolResults toolResults, string query, int maxSources = 8)
    {
        var evidenceQuery = BuildRagEvidenceSelectionQuery(query);
        var requestedTitle = LooksLikeShortTechnicalEvidenceTopic(evidenceQuery)
            ? null
            : TryExtractRequestedItemTitle(query);
        var hits = EnumerateRagHitSummaries(toolResults)
            .Where(hit => !LooksLikeNavigationOnlyHit(hit) || IsUsableExactItemNavigationOverride(requestedTitle, hit))
            .Where(hit => !LooksLikeLowSignalContentCandidateHit(hit) || IsUsableExactItemNavigationOverride(requestedTitle, hit))
            .OrderByDescending(hit => ComputeRagHitLexicalRelevance(evidenceQuery, GetRagHitLookupText(hit)))
            .ToList();

        if (!string.IsNullOrWhiteSpace(requestedTitle))
        {
            var exactTitleHits = hits
                .Where(hit => RagHitContainsRequestedTitle(hit, requestedTitle!)
                    || RagHitHasUsableRequestedTitleAnchor(requestedTitle!, hit))
                .ToList();
            var anchoredExactTitleHits = exactTitleHits
                .Where(hit => RagHitHasUsableRequestedTitleAnchor(requestedTitle!, hit))
                .ToList();
            if (anchoredExactTitleHits.Count > 0)
                exactTitleHits = anchoredExactTitleHits;
            if (exactTitleHits.Count > 0)
                hits = exactTitleHits;
        }
        else
        {
            hits = FilterHitsToDominantTopLevel(hits, query).ToList();
        }

        return hits
            .GroupBy(hit => $"{hit.DocPath}|{hit.PageStart}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(maxSources)
            .Select(BuildSourceRefFromRagHit)
            .ToList();
    }

    private static List<ToolMemory.SourceRef> DeriveSourcesFromExtractiveHits(ToolResults toolResults, string query, int maxSources = 8)
    {
        if (LooksLikeDocumentVersionTraceabilityRequest(query))
        {
            var traceabilitySources = DeriveSourcesFromDocumentVersionTraceabilityHits(
                toolResults,
                query,
                Math.Min(maxSources, 4));
            if (traceabilitySources.Count > 0)
                return traceabilitySources;
        }

        var evidenceQuery = BuildRagEvidenceSelectionQuery(query);
        var requestedTitle = LooksLikeShortTechnicalEvidenceTopic(evidenceQuery)
            ? null
            : TryExtractRequestedItemTitle(query);
        var requestedDocumentFileTitle = ResolveRequestedDocumentFileTitle(query, requestedTitle);
        var explicitDocumentFileReferences = ExtractExplicitDocumentFileReferenceQueries(query);
        var hasMultipleExplicitComparativeDocumentReferences = LooksLikeComparativeDocumentaryRequest(query)
            && explicitDocumentFileReferences.Count > 1;
        var shouldScopeToSingleRequestedDocumentFile = !hasMultipleExplicitComparativeDocumentReferences
            && !string.IsNullOrWhiteSpace(requestedDocumentFileTitle);
        var allHits = EnumerateRagHitSummaries(toolResults)
            .Where(hit => !LooksLikeNavigationOnlyHit(hit)
                || IsUsableExactItemNavigationOverride(requestedTitle, hit)
                || MatchesRequestedDocumentFileIdentity(requestedDocumentFileTitle, hit))
            .Where(hit => !LooksLikeLowSignalContentCandidateHit(hit)
                || IsUsableExactItemNavigationOverride(requestedTitle, hit)
                || MatchesRequestedDocumentFileIdentity(requestedDocumentFileTitle, hit)
                || MatchesExplicitComparativeDocumentReference(query, hit))
            .ToList();
        if (string.IsNullOrWhiteSpace(requestedDocumentFileTitle)
            && !string.IsNullOrWhiteSpace(requestedTitle)
            && allHits.Count > 0
            && (!RagHitsContainRequestedTitle(allHits, requestedTitle!)
                || !allHits.Any(hit => (RagHitContainsRequestedTitle(hit, requestedTitle!)
                        || RagHitHasUsableRequestedTitleAnchor(requestedTitle!, hit)
                        || IsUsableExactItemNavigationOverride(requestedTitle!, hit))
                    && !LooksLikeExactItemReferenceOnlyHit(requestedTitle!, hit))))
        {
            return DeriveSourcesFromMissingExactItemCloseLeads(requestedTitle!, allHits);
        }

        var sourceLimit = maxSources;
        if (LooksLikeRankingDocumentaryRequest(query))
            sourceLimit = Math.Min(sourceLimit, 4);
        else if (!string.IsNullOrWhiteSpace(requestedTitle))
            sourceLimit = Math.Min(sourceLimit, 4);
        else if (LooksLikeSourceBackedQuantityScalingRequest(query))
            sourceLimit = Math.Min(sourceLimit, 1);
        else if (LooksLikeSourceBackedAdaptationRequest(query))
            sourceLimit = Math.Min(sourceLimit, 5);

        List<RagHitSummary> selectedHits;
        if (LooksLikeSourceBackedQuantityScalingRequest(query) && TryExtractTargetScaleCount(query, out var targetCount))
        {
            selectedHits = SelectQuantityScalingCandidates(EnumerateRagHitSummaries(toolResults), query, targetCount)
                .Select(candidate => candidate.Hit)
                .Take(sourceLimit)
                .ToList();
            if (selectedHits.Count == 0)
                selectedHits = SelectSourceBackedExtractiveHits(toolResults, query, sourceLimit).ToList();
        }
        else
        {
            selectedHits = SelectSourceBackedExtractiveHits(toolResults, query, sourceLimit).ToList();
        }
        if (shouldScopeToSingleRequestedDocumentFile)
        {
            var documentHits = selectedHits
                .Where(hit => CandidateMatchesDocumentIdentity(requestedDocumentFileTitle!, hit.DocName, hit.DocPath))
                .ToList();
            if (documentHits.Count == 0)
            {
                documentHits = allHits
                    .Where(hit => CandidateMatchesDocumentIdentity(requestedDocumentFileTitle!, hit.DocName, hit.DocPath))
                    .Take(sourceLimit)
                    .ToList();
            }

            if (documentHits.Count > 0)
                selectedHits = documentHits;
        }
        if (!string.IsNullOrWhiteSpace(requestedTitle) && !hasMultipleExplicitComparativeDocumentReferences)
        {
            var stronglyAnchoredHits = selectedHits
                .Where(hit => LooksLikeStructuredItemCardRequest(query)
                    ? ExactItemEvidenceStartsWithRequestedTitle(requestedTitle!, hit) || HasStrongExactItemTitleAnchor(requestedTitle!, hit) || IsUsableExactItemNavigationOverride(requestedTitle!, hit)
                    : RagHitHasUsableRequestedTitleAnchor(requestedTitle!, hit) || IsUsableExactItemNavigationOverride(requestedTitle!, hit))
                .ToList();
            if (stronglyAnchoredHits.Count > 0)
                selectedHits = stronglyAnchoredHits;

            selectedHits = selectedHits
                .OrderByDescending(hit => ComputeExactItemCardEvidenceScore(requestedTitle!, hit))
                .ThenByDescending(hit => hit.Score)
                .ToList();
        }
        else if (LooksLikeSourceBackedAdaptationRequest(query))
        {
            selectedHits = RankSourceBackedAdaptationHits(selectedHits, query)
                .Take(sourceLimit)
                .ToList();
        }
        else if (LooksLikeTechnicalRankingQuery(query))
        {
            var structuredEnoughHits = selectedHits
                .Where(static hit => !LooksLikeLowStructureShortProcedureHit(hit))
                .ToList();
            if (structuredEnoughHits.Count > 0)
                selectedHits = structuredEnoughHits;

            var nonVeryShortHits = selectedHits
                .Where(static hit => ExtractBestVisibleDurationMinutes(hit) is not int minutes || minutes > 3)
                .ToList();
            if (nonVeryShortHits.Count > 0)
                selectedHits = nonVeryShortHits;
        }

        var requestedMaxMinutes = TryExtractRequestedMaxMinutes(query);
        if (requestedMaxMinutes.HasValue)
        {
            var timeCompatibleHits = selectedHits
                .Where(hit =>
                {
                    var visibleMinutes = ExtractBestVisibleDurationMinutes(hit);
                    return !visibleMinutes.HasValue || visibleMinutes.Value <= requestedMaxMinutes.Value;
                })
                .ToList();
            if (timeCompatibleHits.Count > 0)
                selectedHits = timeCompatibleHits;
        }

        var excludedTerms = ExtractSourceBackedExcludedTerms(query);
        if (excludedTerms.Count > 0)
        {
            var compliantHits = selectedHits
                .Where(hit => !RagHitContainsAnyExcludedTerm(hit, excludedTerms))
                .ToList();
            if (compliantHits.Count > 0)
                selectedHits = compliantHits;
        }

        return selectedHits
            .GroupBy(hit => $"{hit.DocPath}|{hit.PageStart}|{hit.PageEnd}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(sourceLimit)
            .Select(BuildSourceRefFromRagHit)
            .ToList();
    }

}
