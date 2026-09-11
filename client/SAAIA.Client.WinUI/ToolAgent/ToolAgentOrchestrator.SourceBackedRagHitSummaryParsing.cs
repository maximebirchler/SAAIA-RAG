using System.Text.Json;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static RagHitSummary BuildRagHitSummary(JsonElement h)
    {
        var docPath = TryGetString(h, "docPath") ?? TryGetString(h, "doc_path") ?? string.Empty;
        var docName = TryGetString(h, "docName") ?? TryGetString(h, "doc_name") ?? Path.GetFileName(docPath);
        var pageStart = ReadRagHitPageStart(h);
        var pageEnd = ReadRagHitPageEnd(h, pageStart);
        var excerpt = TryGetString(h, "excerpt") ?? TryGetString(h, "text") ?? string.Empty;
        var fullText = TryGetString(h, "fullText") ?? TryGetString(h, "full_text");
        var contextualSnippet = TryGetString(h, "contextualSnippet") ?? TryGetString(h, "contextual_snippet") ?? TryGetString(h, "ContextualSnippet");
        var sectionTitle = TryGetString(h, "sectionTitle") ?? TryGetString(h, "section_title") ?? TryGetNestedString(h, "context", "sectionTitle");
        var headingPath = TryGetString(h, "headingPath") ?? TryGetString(h, "heading_path") ?? TryGetNestedString(h, "context", "headingPath");
        var prevChunkId = TryGetString(h, "prevChunkId") ?? TryGetString(h, "prev_chunk_id") ?? TryGetNestedString(h, "context", "prevChunkId");
        var nextChunkId = TryGetString(h, "nextChunkId") ?? TryGetString(h, "next_chunk_id") ?? TryGetNestedString(h, "context", "nextChunkId");
        var sameSectionChunkId = TryGetString(h, "sameSectionChunkId") ?? TryGetString(h, "same_section_chunk_id") ?? TryGetNestedString(h, "context", "sameSectionChunkId");
        var originalChunkType = TryGetString(h, "originalChunkType") ?? TryGetString(h, "original_chunk_type") ?? TryGetNestedString(h, "context", "originalChunkType");
        var provenanceInfo = TryGetObject(h, "provenanceInfo") ?? TryGetObject(h, "provenance_info") ?? TryGetObject(h, "ProvenanceInfo");
        var offsetStart = provenanceInfo.HasValue
            ? TryGetInt(provenanceInfo.Value, "offsetStart") ?? TryGetInt(provenanceInfo.Value, "offset_start") ?? TryGetInt(provenanceInfo.Value, "OffsetStart")
            : null;
        offsetStart ??= TryGetInt(h, "offsetStart") ?? TryGetInt(h, "offset_start") ?? TryGetInt(h, "OffsetStart");
        var offsetEnd = provenanceInfo.HasValue
            ? TryGetInt(provenanceInfo.Value, "offsetEnd") ?? TryGetInt(provenanceInfo.Value, "offset_end") ?? TryGetInt(provenanceInfo.Value, "OffsetEnd")
            : null;
        offsetEnd ??= TryGetInt(h, "offsetEnd") ?? TryGetInt(h, "offset_end") ?? TryGetInt(h, "OffsetEnd");
        var retrievalQuery = TryGetString(h, "retrievalQuery") ?? TryGetString(h, "retrieval_query") ?? TryGetString(h, "RetrievalQuery");
        var retrievalQueryIndex = TryGetInt(h, "retrievalQueryIndex") ?? TryGetInt(h, "retrieval_query_index") ?? TryGetInt(h, "RetrievalQueryIndex");
        var retrievalHitRank = TryGetInt(h, "retrievalHitRank") ?? TryGetInt(h, "retrieval_hit_rank") ?? TryGetInt(h, "RetrievalHitRank");
        var retrievalQuerySpecificity = TryGetInt(h, "retrievalQuerySpecificity") ?? TryGetInt(h, "retrieval_query_specificity") ?? TryGetInt(h, "RetrievalQuerySpecificity");
        var retriever = TryGetString(h, "retriever");
        var embeddingBasis = TryGetString(h, "embeddingBasis") ?? TryGetString(h, "embedding_basis");
        var score = TryGetDouble(h, "score") ?? 0.0;
        var exactMatchHit = TryGetBool(h, "exactMatchHit") ?? TryGetBool(h, "exact_match_hit") ?? false;
        var docLanguage = TryGetDocumentLanguage(h);
        var profileLanguage = TryGetString(h, "profileLanguage") ?? TryGetString(h, "profile_language") ?? TryGetString(h, "ProfileLanguage");
        var sourceHash = TryGetString(h, "sourceHash") ?? TryGetString(h, "source_hash") ?? TryGetString(h, "SourceHash");
        var docId = TryGetString(h, "docId") ?? TryGetString(h, "doc_id") ?? TryGetString(h, "DocId");
        var category = TryGetString(h, "category") ?? TryGetString(h, "Category");
        var categoryRef = TryGetString(h, "categoryRef") ?? TryGetString(h, "category_ref") ?? TryGetString(h, "CategoryRef");
        var categoryPath = TryGetString(h, "categoryPath") ?? TryGetString(h, "category_path") ?? TryGetString(h, "CategoryPath");
        var chunkId = TryGetString(h, "chunkId") ?? TryGetString(h, "chunk_id") ?? TryGetString(h, "ChunkId");
        var quality = ExtractRagHitExtractionQualitySignals(h);
        var chunkQuality = ExtractRagHitChunkQualitySignals(h);
        var qualityElement = TryGetObject(h, "extractionQuality") ?? TryGetObject(h, "extraction_quality") ?? TryGetObject(h, "ExtractionQuality");
        var diagnosticSummary = TryBuildSourceExtractionDiagnosticRef(h, qualityElement);
        var matchedContentCards = ExtractRagHitMatchedContentCards(h);
        var selectionHints = TryGetObject(h, "selectionHints") ?? TryGetObject(h, "selection_hints") ?? TryGetObject(h, "SelectionHints");
        var selectionHintRole = selectionHints.HasValue
            ? TryGetString(selectionHints.Value, "evidenceRole") ?? TryGetString(selectionHints.Value, "evidence_role") ?? TryGetString(selectionHints.Value, "EvidenceRole")
            : null;
        var selectionHintActionabilityScore = selectionHints.HasValue
            ? TryGetInt(selectionHints.Value, "actionabilityScore") ?? TryGetInt(selectionHints.Value, "actionability_score") ?? TryGetInt(selectionHints.Value, "ActionabilityScore")
            : null;
        var selectionHintSupportScore = selectionHints.HasValue
            ? TryGetInt(selectionHints.Value, "supportScore") ?? TryGetInt(selectionHints.Value, "support_score") ?? TryGetInt(selectionHints.Value, "SupportScore")
            : null;
        var selectionHintFragmentScore = selectionHints.HasValue
            ? TryGetInt(selectionHints.Value, "fragmentScore") ?? TryGetInt(selectionHints.Value, "fragment_score") ?? TryGetInt(selectionHints.Value, "FragmentScore")
            : null;
        var selectionHintNavigationScore = selectionHints.HasValue
            ? TryGetInt(selectionHints.Value, "navigationScore") ?? TryGetInt(selectionHints.Value, "navigation_score") ?? TryGetInt(selectionHints.Value, "NavigationScore")
            : null;
        var selectionHintQualityPenalty = selectionHints.HasValue
            ? TryGetInt(selectionHints.Value, "qualityPenalty") ?? TryGetInt(selectionHints.Value, "quality_penalty") ?? TryGetInt(selectionHints.Value, "QualityPenalty")
            : null;
        var contentRole = TryGetString(h, "contentRole")
                          ?? TryGetString(h, "ContentRole")
                          ?? TryGetNestedString(h, "context", "contentRole")
                          ?? TryGetNestedString(h, "Context", "ContentRole");
        var navigationReason = TryGetString(h, "navigationReason")
                               ?? TryGetString(h, "NavigationReason")
                               ?? TryGetNestedString(h, "context", "navigationReason")
                               ?? TryGetNestedString(h, "Context", "NavigationReason");
        var navigationScore = TryGetDouble(h, "navigationScore")
                              ?? TryGetDouble(h, "NavigationScore")
                              ?? TryGetNestedDouble(h, "context", "navigationScore")
                              ?? TryGetNestedDouble(h, "Context", "NavigationScore");
        var contentDensityScore = TryGetDouble(h, "contentDensityScore")
                                   ?? TryGetDouble(h, "ContentDensityScore")
                                   ?? TryGetNestedDouble(h, "context", "contentDensityScore")
                                   ?? TryGetNestedDouble(h, "Context", "ContentDensityScore");
        var contextElement = TryGetObject(h, "context")
                             ?? TryGetObject(h, "Context")
                             ?? TryGetObject(h, "contentSignals")
                             ?? TryGetObject(h, "content_signals")
                             ?? TryGetObject(h, "ContentSignals");
        var contentSignalsElement = TryGetObject(h, "contentSignals")
                                    ?? TryGetObject(h, "content_signals")
                                    ?? TryGetObject(h, "ContentSignals");
        var sourceUnitOrdinals = contextElement.HasValue
            ? TryGetIntList(contextElement.Value, "sourceUnitOrdinals", "source_unit_ordinals", "SourceUnitOrdinals")
            : new List<int>();
        if (sourceUnitOrdinals.Count == 0 && contentSignalsElement.HasValue)
            sourceUnitOrdinals = TryGetIntList(contentSignalsElement.Value, "sourceUnitOrdinals", "source_unit_ordinals", "SourceUnitOrdinals");
        if (sourceUnitOrdinals.Count == 0)
            sourceUnitOrdinals = TryGetIntList(h, "sourceUnitOrdinals", "source_unit_ordinals", "SourceUnitOrdinals");
        var sourceUnitStartOrdinal = contextElement.HasValue
            ? TryGetInt(contextElement.Value, "sourceUnitStartOrdinal") ?? TryGetInt(contextElement.Value, "source_unit_start_ordinal") ?? TryGetInt(contextElement.Value, "SourceUnitStartOrdinal")
            : null;
        if (!sourceUnitStartOrdinal.HasValue && contentSignalsElement.HasValue)
            sourceUnitStartOrdinal = TryGetInt(contentSignalsElement.Value, "sourceUnitStartOrdinal") ?? TryGetInt(contentSignalsElement.Value, "source_unit_start_ordinal") ?? TryGetInt(contentSignalsElement.Value, "SourceUnitStartOrdinal");
        sourceUnitStartOrdinal ??= TryGetInt(h, "sourceUnitStartOrdinal") ?? TryGetInt(h, "source_unit_start_ordinal") ?? TryGetInt(h, "SourceUnitStartOrdinal");
        var sourceUnitEndOrdinal = contextElement.HasValue
            ? TryGetInt(contextElement.Value, "sourceUnitEndOrdinal") ?? TryGetInt(contextElement.Value, "source_unit_end_ordinal") ?? TryGetInt(contextElement.Value, "SourceUnitEndOrdinal")
            : null;
        if (!sourceUnitEndOrdinal.HasValue && contentSignalsElement.HasValue)
            sourceUnitEndOrdinal = TryGetInt(contentSignalsElement.Value, "sourceUnitEndOrdinal") ?? TryGetInt(contentSignalsElement.Value, "source_unit_end_ordinal") ?? TryGetInt(contentSignalsElement.Value, "SourceUnitEndOrdinal");
        sourceUnitEndOrdinal ??= TryGetInt(h, "sourceUnitEndOrdinal") ?? TryGetInt(h, "source_unit_end_ordinal") ?? TryGetInt(h, "SourceUnitEndOrdinal");
        var sourceUnitCount = contextElement.HasValue
            ? TryGetInt(contextElement.Value, "sourceUnitCount") ?? TryGetInt(contextElement.Value, "source_unit_count") ?? TryGetInt(contextElement.Value, "SourceUnitCount")
            : null;
        if (!sourceUnitCount.HasValue && contentSignalsElement.HasValue)
            sourceUnitCount = TryGetInt(contentSignalsElement.Value, "sourceUnitCount") ?? TryGetInt(contentSignalsElement.Value, "source_unit_count") ?? TryGetInt(contentSignalsElement.Value, "SourceUnitCount");
        sourceUnitCount ??= TryGetInt(h, "sourceUnitCount") ?? TryGetInt(h, "source_unit_count") ?? TryGetInt(h, "SourceUnitCount");
        var chunkComposition = contextElement.HasValue
            ? TryGetString(contextElement.Value, "chunkComposition") ?? TryGetString(contextElement.Value, "chunk_composition") ?? TryGetString(contextElement.Value, "ChunkComposition")
            : null;
        if (string.IsNullOrWhiteSpace(chunkComposition) && contentSignalsElement.HasValue)
            chunkComposition = TryGetString(contentSignalsElement.Value, "chunkComposition") ?? TryGetString(contentSignalsElement.Value, "chunk_composition") ?? TryGetString(contentSignalsElement.Value, "ChunkComposition");
        chunkComposition ??= TryGetString(h, "chunkComposition") ?? TryGetString(h, "chunk_composition") ?? TryGetString(h, "ChunkComposition");
        var hasTable = TryGetBool(h, "hasTable") ?? TryGetBool(h, "HasTable") ?? false;

        return new RagHitSummary(
            docPath,
            docName,
            pageStart,
            pageEnd,
            excerpt,
            sectionTitle,
            headingPath,
            retriever,
            score,
            exactMatchHit,
            fullText,
            contextualSnippet,
            embeddingBasis,
            quality.QualityStatus,
            quality.ExtractionConfidence,
            quality.DocumentExtractionConfidence,
            quality.PageExtractionConfidence,
            quality.ManualReviewRecommended,
            quality.DocumentManualReviewRecommended,
            quality.PageManualReviewRecommended,
            quality.OcrAttempted,
            quality.OcrApplied,
            docLanguage,
            profileLanguage,
            BuildSourceProfileSignalsRef(h),
            matchedContentCards,
            sourceHash,
            docId,
            category,
            categoryRef,
            categoryPath,
            chunkId,
            quality.ExtractionSource,
            quality.DocumentQualityStatus,
            quality.PageQualityStatus,
            quality.TextStatus,
            chunkQuality.ChunkTextStatus,
            chunkQuality.ChunkTextSparse,
            chunkQuality.ChunkOcrCandidate,
            quality.OcrRecommended,
            quality.Signals,
            chunkQuality.Signals,
            selectionHintRole,
            selectionHintActionabilityScore,
            selectionHintSupportScore,
            selectionHintFragmentScore,
            selectionHintNavigationScore,
            selectionHintQualityPenalty,
            diagnosticSummary,
            contentRole,
            navigationReason,
            navigationScore,
            contentDensityScore,
            prevChunkId,
            nextChunkId,
            sameSectionChunkId,
            originalChunkType,
            offsetStart,
            offsetEnd,
            retrievalQuery,
            retrievalQueryIndex,
            retrievalHitRank,
            retrievalQuerySpecificity,
            hasTable,
            sourceUnitOrdinals,
            sourceUnitStartOrdinal,
            sourceUnitEndOrdinal,
            sourceUnitCount,
            chunkComposition);
    }

    private static int ReadRagHitPageStart(JsonElement h, int fallback = 1)
        => Math.Max(
            1,
            TryGetInt(h, "pageStart")
            ?? TryGetInt(h, "page_start")
            ?? TryGetInt(h, "PageStart")
            ?? TryGetInt(h, "fromPage")
            ?? TryGetInt(h, "FromPage")
            ?? TryGetInt(h, "page_from")
            ?? TryGetInt(h, "page")
            ?? TryGetInt(h, "Page")
            ?? TryGetInt(h, "p")
            ?? TryGetInt(h, "P")
            ?? fallback);

    private static int ReadRagHitPageEnd(JsonElement h, int pageStart)
        => Math.Max(
            pageStart,
            TryGetInt(h, "pageEnd")
            ?? TryGetInt(h, "page_end")
            ?? TryGetInt(h, "PageEnd")
            ?? TryGetInt(h, "toPage")
            ?? TryGetInt(h, "ToPage")
            ?? TryGetInt(h, "page_to")
            ?? pageStart);

    private static IReadOnlyList<RagHitContentCardSummary>? ExtractRagHitMatchedContentCards(JsonElement h)
    {
        if (h.ValueKind != JsonValueKind.Object
            || (!h.TryGetProperty("matchedContentCards", out var cards)
                && !h.TryGetProperty("matched_content_cards", out cards)
                && !h.TryGetProperty("MatchedContentCards", out cards)
                && !h.TryGetProperty("contentCards", out cards)
                && !h.TryGetProperty("content_cards", out cards)
                && !h.TryGetProperty("ContentCards", out cards))
            || cards.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<RagHitContentCardSummary>();
        foreach (var card in cards.EnumerateArray())
        {
            if (card.ValueKind != JsonValueKind.Object)
                continue;

            var title = TryGetString(card, "title") ?? TryGetString(card, "Title");
            if (string.IsNullOrWhiteSpace(title))
                continue;

            var signals = ExtractCompactSignals(card, "signals")
                .Concat(ExtractCompactSignals(card, "Signals"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray();

            list.Add(new RagHitContentCardSummary(
                title.Trim(),
                TryGetString(card, "contentCardId") ?? TryGetString(card, "content_card_id") ?? TryGetString(card, "ContentCardId"),
                TryGetInt(card, "pageStart") ?? TryGetInt(card, "page_start") ?? TryGetInt(card, "PageStart"),
                TryGetInt(card, "pageEnd") ?? TryGetInt(card, "page_end") ?? TryGetInt(card, "PageEnd"),
                TryGetString(card, "kind") ?? TryGetString(card, "Kind"),
                signals.Length == 0 ? null : signals,
                TryBuildRagHitContentCardEvidence(card),
                TryGetRawContentCardEvidence(card)));
        }

        return list.Count == 0 ? null : list;
    }

    private static JsonElement? TryGetRawContentCardEvidence(JsonElement card)
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

    private static RagHitContentCardEvidenceSummary? TryBuildRagHitContentCardEvidence(JsonElement card)
    {
        if (card.ValueKind != JsonValueKind.Object
            || (!card.TryGetProperty("evidence", out var evidence)
                && !card.TryGetProperty("Evidence", out evidence)
                && !card.TryGetProperty("structuredEvidence", out evidence)
                && !card.TryGetProperty("structured_evidence", out evidence))
            || evidence.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        RagHitScaleBasisSummary? scaleBasis = null;
        var scaleBasisElement = TryGetObject(evidence, "scaleBasis")
                                ?? TryGetObject(evidence, "scale_basis")
                                ?? TryGetObject(evidence, "ScaleBasis");
        if (scaleBasisElement is { ValueKind: JsonValueKind.Object } sb)
        {
            var count = TryGetInt(sb, "count") ?? TryGetInt(sb, "Count") ?? 0;
            if (count is > 0 and <= 200)
                scaleBasis = new RagHitScaleBasisSummary(count, TryGetString(sb, "label") ?? TryGetString(sb, "Label"));
        }

        var facts = new List<RagHitQuantityFactSummary>();
        var factsElement = TryGetArray(evidence, "quantityFacts")
                           ?? TryGetArray(evidence, "quantity_facts")
                           ?? TryGetArray(evidence, "QuantityFacts");
        if (factsElement is { ValueKind: JsonValueKind.Array } qf)
        {
            foreach (var fact in qf.EnumerateArray())
            {
                if (fact.ValueKind != JsonValueKind.Object)
                    continue;

                var value = TryGetDouble(fact, "value") ?? TryGetDouble(fact, "Value");
                var unit = TryGetString(fact, "unit") ?? TryGetString(fact, "Unit");
                var label = TryGetString(fact, "label") ?? TryGetString(fact, "Label");
                if (value is not > 0 || string.IsNullOrWhiteSpace(unit) || string.IsNullOrWhiteSpace(label))
                    continue;

                facts.Add(new RagHitQuantityFactSummary(
                    value.Value,
                    unit.Trim(),
                    CollapseWhitespace(label),
                    TryGetString(fact, "sourceText") ?? TryGetString(fact, "source_text") ?? TryGetString(fact, "SourceText")));
            }
        }

        var genericFacts = new List<RagHitEvidenceFactSummary>();
        var genericFactsElement = TryGetArray(evidence, "facts")
                                  ?? TryGetArray(evidence, "Facts");
        if (genericFactsElement is { ValueKind: JsonValueKind.Array } gf)
        {
            foreach (var fact in gf.EnumerateArray())
            {
                if (fact.ValueKind != JsonValueKind.Object)
                    continue;

                var label = TryGetString(fact, "label") ?? TryGetString(fact, "Label") ?? TryGetString(fact, "name") ?? TryGetString(fact, "Name");
                var value = TryGetString(fact, "value") ?? TryGetString(fact, "Value") ?? TryGetString(fact, "amount") ?? TryGetString(fact, "Amount");
                var sourceText = TryGetString(fact, "sourceText")
                                 ?? TryGetString(fact, "source_text")
                                 ?? TryGetString(fact, "SourceText")
                                 ?? TryGetString(fact, "quote")
                                 ?? TryGetString(fact, "Quote")
                                 ?? TryGetString(fact, "text")
                                 ?? TryGetString(fact, "Text");
                if (string.IsNullOrWhiteSpace(label) && string.IsNullOrWhiteSpace(value) && string.IsNullOrWhiteSpace(sourceText))
                    continue;

                genericFacts.Add(new RagHitEvidenceFactSummary(
                    CollapseWhitespace(TryGetString(fact, "kind") ?? TryGetString(fact, "Kind") ?? TryGetString(fact, "type") ?? TryGetString(fact, "Type") ?? "fact"),
                    CollapseWhitespace(string.IsNullOrWhiteSpace(label) ? "fact" : label),
                    string.IsNullOrWhiteSpace(value) ? null : CollapseWhitespace(value),
                    TryGetString(fact, "unit") ?? TryGetString(fact, "Unit"),
                    string.IsNullOrWhiteSpace(sourceText) ? null : CollapseWhitespace(sourceText),
                    TryGetInt(fact, "pageStart") ?? TryGetInt(fact, "page_start") ?? TryGetInt(fact, "PageStart") ?? TryGetInt(fact, "page") ?? TryGetInt(fact, "Page"),
                    TryGetInt(fact, "pageEnd") ?? TryGetInt(fact, "page_end") ?? TryGetInt(fact, "PageEnd") ?? TryGetInt(fact, "page") ?? TryGetInt(fact, "Page"),
                    TryGetDouble(fact, "confidence") ?? TryGetDouble(fact, "Confidence")));
            }
        }

        var nonScalableReasons = ExtractCompactSignals(evidence, "nonScalableReasons")
            .Concat(ExtractCompactSignals(evidence, "non_scalable_reasons"))
            .Concat(ExtractCompactSignals(evidence, "NonScalableReasons"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var schemaVersion = TryGetString(evidence, "schemaVersion")
                            ?? TryGetString(evidence, "schema_version")
                            ?? TryGetString(evidence, "SchemaVersion");
        var confidence = TryGetDouble(evidence, "confidence") ?? TryGetDouble(evidence, "Confidence");
        var language = TryGetString(evidence, "language") ?? TryGetString(evidence, "Language");
        if (scaleBasis is null
            && facts.Count == 0
            && genericFacts.Count == 0
            && nonScalableReasons.Length == 0
            && string.IsNullOrWhiteSpace(schemaVersion)
            && confidence is null
            && string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        return new RagHitContentCardEvidenceSummary(
            schemaVersion,
            scaleBasis,
            facts,
            nonScalableReasons,
            confidence,
            language,
            genericFacts);
    }

    private static (string? ChunkTextStatus, bool? ChunkTextSparse, bool? ChunkOcrCandidate, IReadOnlyList<string>? Signals) ExtractRagHitChunkQualitySignals(JsonElement h)
    {
        var nestedQuality = TryGetObject(h, "extractionQuality") ?? TryGetObject(h, "extraction_quality") ?? TryGetObject(h, "ExtractionQuality");
        var quality = nestedQuality ?? h;
        var hasNestedQuality = nestedQuality.HasValue;

        string? GetQualityString(params string[] names)
        {
            foreach (var name in names)
            {
                var value = TryGetString(quality, name);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            if (!hasNestedQuality)
                return null;

            foreach (var name in names)
            {
                var value = TryGetString(h, name);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return null;
        }

        bool? GetQualityBool(params string[] names)
        {
            foreach (var name in names)
            {
                var value = TryGetBool(quality, name);
                if (value.HasValue)
                    return value;
            }

            if (!hasNestedQuality)
                return null;

            foreach (var name in names)
            {
                var value = TryGetBool(h, name);
                if (value.HasValue)
                    return value;
            }

            return null;
        }

        var signals = ExtractCompactSignals(quality, "chunkQualitySignals")
            .Concat(ExtractCompactSignals(quality, "chunk_quality_signals"))
            .Concat(ExtractCompactSignals(quality, "ChunkQualitySignals"))
            .Concat(hasNestedQuality ? ExtractCompactSignals(h, "chunkQualitySignals") : Array.Empty<string>())
            .Concat(hasNestedQuality ? ExtractCompactSignals(h, "chunk_quality_signals") : Array.Empty<string>())
            .Concat(hasNestedQuality ? ExtractCompactSignals(h, "ChunkQualitySignals") : Array.Empty<string>())
            .Where(static signal => !string.IsNullOrWhiteSpace(signal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        return (
            GetQualityString("chunkTextStatus", "chunk_text_status", "ChunkTextStatus"),
            GetQualityBool("chunkTextSparse", "chunk_text_sparse", "ChunkTextSparse"),
            GetQualityBool("chunkOcrCandidate", "chunk_ocr_candidate", "ChunkOcrCandidate"),
            signals.Count == 0 ? null : signals);
    }

    private static (string? QualityStatus, double? ExtractionConfidence, double? DocumentExtractionConfidence, double? PageExtractionConfidence, bool ManualReviewRecommended, bool DocumentManualReviewRecommended, bool PageManualReviewRecommended, bool OcrAttempted, bool OcrApplied, string? ExtractionSource, string? DocumentQualityStatus, string? PageQualityStatus, string? TextStatus, bool OcrRecommended, IReadOnlyList<string>? Signals) ExtractRagHitExtractionQualitySignals(JsonElement h)
    {
        if (h.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null, null, false, false, false, false, false, null, null, null, null, false, null);
        }

        var hasNestedQuality =
            (h.TryGetProperty("extractionQuality", out var quality)
                || h.TryGetProperty("extraction_quality", out quality)
                || h.TryGetProperty("ExtractionQuality", out quality))
            && quality.ValueKind == JsonValueKind.Object;
        if (!hasNestedQuality)
            quality = h;

        string? GetQualityString(params string[] names)
        {
            foreach (var name in names)
            {
                var value = TryGetString(quality, name);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            if (!hasNestedQuality)
                return null;

            foreach (var name in names)
            {
                var value = TryGetString(h, name);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return null;
        }

        double? GetQualityDouble(params string[] names)
        {
            foreach (var name in names)
            {
                var value = TryGetDouble(quality, name);
                if (value.HasValue)
                    return value;
            }

            if (!hasNestedQuality)
                return null;

            foreach (var name in names)
            {
                var value = TryGetDouble(h, name);
                if (value.HasValue)
                    return value;
            }

            return null;
        }

        bool? GetQualityBool(params string[] names)
        {
            foreach (var name in names)
            {
                var value = TryGetBool(quality, name);
                if (value.HasValue)
                    return value;
            }

            if (!hasNestedQuality)
                return null;

            foreach (var name in names)
            {
                var value = TryGetBool(h, name);
                if (value.HasValue)
                    return value;
            }

            return null;
        }

        var extractionSource = GetQualityString("extractionSource", "extraction_source", "ExtractionSource");
        var documentQualityStatus = GetQualityString("documentQualityStatus", "document_quality_status", "DocumentQualityStatus");
        var pageQualityStatus = GetQualityString("pageQualityStatus", "page_quality_status", "PageQualityStatus");
        var textStatus = GetQualityString("textStatus", "text_status", "TextStatus");
        var qualityStatus =
            pageQualityStatus
            ?? documentQualityStatus;
        var pageExtractionConfidence = GetQualityDouble("pageExtractionConfidence", "page_extraction_confidence", "PageExtractionConfidence");
        var documentExtractionConfidence = GetQualityDouble("documentExtractionConfidence", "document_extraction_confidence", "DocumentExtractionConfidence");
        var confidence = pageExtractionConfidence ?? documentExtractionConfidence;
        var pageManualReview = GetQualityBool("pageManualReviewRecommended", "page_manual_review_recommended", "PageManualReviewRecommended") ?? false;
        var documentManualReview = GetQualityBool("documentManualReviewRecommended", "document_manual_review_recommended", "DocumentManualReviewRecommended") ?? false;
        var manualReview = pageManualReview || documentManualReview;
        var ocrApplied = GetQualityBool("ocrApplied", "ocr_applied", "OcrApplied") ?? false;
        var ocrAttempted = GetQualityBool("ocrAttempted", "ocr_attempted", "OcrAttempted", "OCRAttempted") ?? false;
        var ocrRecommended = GetQualityBool("ocrRecommended", "ocr_recommended", "OcrRecommended") ?? false;
        var signals = ExtractCompactSignals(quality, "signals")
            .Concat(ExtractCompactSignals(quality, "Signals"))
            .Concat(ExtractCompactSignals(quality, "chunkQualitySignals"))
            .Concat(ExtractCompactSignals(quality, "chunk_quality_signals"))
            .Concat(ExtractCompactSignals(quality, "ChunkQualitySignals"))
            .Concat(hasNestedQuality ? ExtractCompactSignals(h, "signals") : Array.Empty<string>())
            .Concat(hasNestedQuality ? ExtractCompactSignals(h, "Signals") : Array.Empty<string>())
            .Concat(hasNestedQuality ? ExtractCompactSignals(h, "chunkQualitySignals") : Array.Empty<string>())
            .Concat(hasNestedQuality ? ExtractCompactSignals(h, "chunk_quality_signals") : Array.Empty<string>())
            .Concat(hasNestedQuality ? ExtractCompactSignals(h, "ChunkQualitySignals") : Array.Empty<string>())
            .Where(static signal => !string.IsNullOrWhiteSpace(signal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        return (
            qualityStatus,
            confidence,
            documentExtractionConfidence,
            pageExtractionConfidence,
            manualReview,
            documentManualReview,
            pageManualReview,
            ocrAttempted,
            ocrApplied,
            extractionSource,
            documentQualityStatus,
            pageQualityStatus,
            textStatus,
            ocrRecommended,
            signals.Count == 0 ? null : signals);
    }

    private static IEnumerable<RagHitSummary> EnumerateRagHitSummaries(ToolResults toolResults)
    {
        foreach (var item in toolResults.Items.Where(x => x.ToolName is "rag.search" or "rag.multi_search"))
        {
            if (item.Result.ValueKind != JsonValueKind.Object
                || !item.Result.TryGetProperty("hits", out var hits)
                || hits.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var h in hits.EnumerateArray())
            {
                if (h.ValueKind != JsonValueKind.Object)
                    continue;

                yield return BuildRagHitSummary(h);
            }
        }

        foreach (var hit in EnumerateDocumentContextHitSummaries(toolResults))
            yield return hit;
    }

    private static IEnumerable<RagHitSummary> EnumerateDocumentContextHitSummaries(ToolResults toolResults)
    {
        foreach (var item in toolResults.Items.Where(static x => x.ToolName == "documents.context"))
        {
            var root = item.Result;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("items", out var contextItems)
                || contextItems.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var document = TryGetObject(root, "document") ?? TryGetObject(root, "Document");
            var docPath = document.HasValue
                ? TryGetString(document.Value, "docPath") ?? TryGetString(document.Value, "DocPath") ?? string.Empty
                : string.Empty;
            var docName = document.HasValue
                ? TryGetString(document.Value, "docName") ?? TryGetString(document.Value, "DocName") ?? Path.GetFileName(docPath)
                : Path.GetFileName(docPath);
            var docId = document.HasValue
                ? TryGetString(document.Value, "docId") ?? TryGetString(document.Value, "DocId")
                : null;
            var category = document.HasValue
                ? TryGetString(document.Value, "category") ?? TryGetString(document.Value, "Category")
                : null;
            var categoryRef = document.HasValue
                ? TryGetString(document.Value, "categoryRef") ?? TryGetString(document.Value, "CategoryRef")
                : null;
            var categoryPath = document.HasValue
                ? TryGetString(document.Value, "categoryPath") ?? TryGetString(document.Value, "CategoryPath")
                : null;

            foreach (var contextItem in contextItems.EnumerateArray())
            {
                if (contextItem.ValueKind != JsonValueKind.Object)
                    continue;

                var text = TryGetString(contextItem, "text") ?? TryGetString(contextItem, "Text") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                var pageStart = TryGetInt(contextItem, "pageStart")
                                ?? TryGetInt(contextItem, "PageStart")
                                ?? 1;
                var pageEnd = TryGetInt(contextItem, "pageEnd")
                              ?? TryGetInt(contextItem, "PageEnd")
                              ?? pageStart;
                var itemDocPath = NullIfWhiteSpace(TryGetString(contextItem, "docPath") ?? TryGetString(contextItem, "DocPath"))
                                  ?? docPath;
                var itemDocName = NullIfWhiteSpace(TryGetString(contextItem, "docName") ?? TryGetString(contextItem, "DocName"))
                                  ?? NullIfWhiteSpace(docName)
                                  ?? Path.GetFileName(itemDocPath);
                var itemDocId = NullIfWhiteSpace(TryGetString(contextItem, "docId") ?? TryGetString(contextItem, "DocId"))
                                ?? docId;
                var itemCategory = NullIfWhiteSpace(TryGetString(contextItem, "category") ?? TryGetString(contextItem, "Category"))
                                   ?? category;
                var itemCategoryRef = NullIfWhiteSpace(TryGetString(contextItem, "categoryRef") ?? TryGetString(contextItem, "CategoryRef"))
                                      ?? categoryRef;
                var itemCategoryPath = NullIfWhiteSpace(TryGetString(contextItem, "categoryPath") ?? TryGetString(contextItem, "CategoryPath"))
                                       ?? categoryPath;
                var selectionHints = TryGetObject(contextItem, "selectionHints")
                                     ?? TryGetObject(contextItem, "selection_hints")
                                     ?? TryGetObject(contextItem, "SelectionHints");
                var contentSignals = TryGetObject(contextItem, "contentSignals")
                                     ?? TryGetObject(contextItem, "content_signals")
                                     ?? TryGetObject(contextItem, "ContentSignals");
                yield return new RagHitSummary(
                    itemDocPath,
                    itemDocName,
                    Math.Max(1, pageStart),
                    Math.Max(Math.Max(1, pageStart), pageEnd),
                    text,
                    TryGetString(contextItem, "sectionTitle") ?? TryGetString(contextItem, "SectionTitle"),
                    TryGetString(contextItem, "headingPath") ?? TryGetString(contextItem, "HeadingPath"),
                    Retriever: "documents.context",
                    Score: 0.0,
                    ExactMatchHit: true,
                    FullText: text,
                    ContextualSnippet: text,
                    DocId: itemDocId,
                    Category: itemCategory,
                    CategoryRef: itemCategoryRef,
                    CategoryPath: itemCategoryPath,
                    ChunkId: TryGetString(contextItem, "chunkId") ?? TryGetString(contextItem, "ChunkId"),
                    SourceHash: TryGetString(contextItem, "sourceHash") ?? TryGetString(contextItem, "SourceHash"),
                    DocLanguage: TryGetDocumentLanguage(contextItem),
                    ProfileLanguage: TryGetString(contextItem, "profileLanguage") ?? TryGetString(contextItem, "ProfileLanguage"),
                    MatchedContentCards: ExtractRagHitMatchedContentCards(contextItem),
                    SelectionHintRole: selectionHints.HasValue ? TryGetString(selectionHints.Value, "evidenceRole") ?? TryGetString(selectionHints.Value, "EvidenceRole") : null,
                    SelectionHintActionabilityScore: selectionHints.HasValue ? TryGetInt(selectionHints.Value, "actionabilityScore") ?? TryGetInt(selectionHints.Value, "ActionabilityScore") : null,
                    SelectionHintSupportScore: selectionHints.HasValue ? TryGetInt(selectionHints.Value, "supportScore") ?? TryGetInt(selectionHints.Value, "SupportScore") : null,
                    SelectionHintFragmentScore: selectionHints.HasValue ? TryGetInt(selectionHints.Value, "fragmentScore") ?? TryGetInt(selectionHints.Value, "FragmentScore") : null,
                    SelectionHintNavigationScore: selectionHints.HasValue ? TryGetInt(selectionHints.Value, "navigationScore") ?? TryGetInt(selectionHints.Value, "NavigationScore") : null,
                    SelectionHintQualityPenalty: selectionHints.HasValue ? TryGetInt(selectionHints.Value, "qualityPenalty") ?? TryGetInt(selectionHints.Value, "QualityPenalty") : null,
                    ContentRole: TryGetString(contextItem, "contentRole") ?? TryGetString(contextItem, "ContentRole") ?? (contentSignals.HasValue ? TryGetString(contentSignals.Value, "contentRole") ?? TryGetString(contentSignals.Value, "ContentRole") : null),
                    NavigationReason: TryGetString(contextItem, "navigationReason") ?? TryGetString(contextItem, "NavigationReason") ?? (contentSignals.HasValue ? TryGetString(contentSignals.Value, "navigationReason") ?? TryGetString(contentSignals.Value, "NavigationReason") : null),
                    NavigationScore: TryGetDouble(contextItem, "navigationScore") ?? TryGetDouble(contextItem, "NavigationScore") ?? (contentSignals.HasValue ? TryGetDouble(contentSignals.Value, "navigationScore") ?? TryGetDouble(contentSignals.Value, "NavigationScore") : null),
                    ContentDensityScore: TryGetDouble(contextItem, "contentDensityScore") ?? TryGetDouble(contextItem, "ContentDensityScore") ?? (contentSignals.HasValue ? TryGetDouble(contentSignals.Value, "contentDensityScore") ?? TryGetDouble(contentSignals.Value, "ContentDensityScore") : null),
                    PrevChunkId: TryGetString(contextItem, "previousChunkId") ?? TryGetString(contextItem, "PreviousChunkId") ?? TryGetString(contextItem, "prevChunkId") ?? TryGetString(contextItem, "PrevChunkId"),
                    NextChunkId: TryGetString(contextItem, "nextChunkId") ?? TryGetString(contextItem, "NextChunkId"));
            }
        }
    }
}
