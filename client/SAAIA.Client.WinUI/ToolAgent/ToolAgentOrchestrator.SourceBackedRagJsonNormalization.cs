using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{

    private static JsonElement NormalizeRagHits(JsonElement raw, bool sourceBackedCanonical = false)
    {
        try
        {
            if (raw.ValueKind != JsonValueKind.Object)
                return JsonDocument.Parse("{\"hits\":[]}").RootElement;

            var sourceHits = default(JsonElement);
            if (raw.TryGetProperty("hits", out var hits0) && hits0.ValueKind == JsonValueKind.Array)
            {
                sourceHits = hits0;
            }
            else if (raw.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                sourceHits = items;
            }
            else
            {
                return JsonDocument.Parse("{\"hits\":[]}").RootElement;
            }

            var list = new List<object>();
            foreach (var it in sourceHits.EnumerateArray())
            {
                if (it.ValueKind != JsonValueKind.Object) continue;

                var docId = TryGetString(it, "docId") ?? TryGetString(it, "doc_id") ?? TryGetString(it, "DocId");
                var docPath = TryGetString(it, "docPath") ?? TryGetString(it, "DocPath") ?? "";
                docPath = (docPath ?? "").Trim().Replace('\\', '/').TrimStart('/');

                var docName = TryGetString(it, "docName") ?? TryGetString(it, "DocName") ?? Path.GetFileName(docPath);
                var score = TryGetDouble(it, "score") ?? 0.0;

                var ps = TryGetInt(it, "pageStart") ?? TryGetInt(it, "page") ?? 1;
                var pe = TryGetInt(it, "pageEnd") ?? ps;

                var text = TruncateForPrompt(
                    TryGetString(it, "excerpt")
                    ?? TryGetString(it, "snippet")
                    ?? TryGetString(it, "Snippet")
                    ?? TryGetString(it, "text")
                    ?? TryGetString(it, "Text")
                    ?? "",
                    RagWriterMaxExcerptChars);

                // The canonical bundle owns source text. Prompt projections may
                // be bounded separately, but must not replace it with a preview.
                var fullText = sourceBackedCanonical
                    ? TryGetString(it, "text")
                      ?? TryGetString(it, "Text")
                      ?? TryGetString(it, "fullText")
                      ?? TryGetString(it, "excerpt")
                      ?? TryGetString(it, "snippet")
                      ?? TryGetString(it, "Snippet")
                      ?? string.Empty
                    : TruncateForPrompt(
                        TryGetString(it, "text")
                        ?? TryGetString(it, "Text")
                        ?? text,
                        RagWriterMaxFullTextChars);
                var rawContextualSnippet = TryGetString(it, "contextualSnippet")
                    ?? TryGetString(it, "ContextualSnippet");
                // Retrieval context can span other source units. Keep it separate
                // from the canonical chunk and do not select its content by heuristics.
                var contextualSnippet = sourceBackedCanonical
                    ? TruncateForPrompt(rawContextualSnippet ?? string.Empty, RagWriterContextualTotalChars)
                    : CompactContextualSnippetForPrompt(rawContextualSnippet);
                if (!sourceBackedCanonical && !string.IsNullOrWhiteSpace(contextualSnippet))
                {
                    var contextualEvidence = StripContextualMetadataForEvidence(contextualSnippet);
                    if (!string.IsNullOrWhiteSpace(contextualEvidence)
                        && LooksLikeConcreteContextualEvidence(contextualEvidence)
                        && !fullText.Contains(contextualEvidence, StringComparison.OrdinalIgnoreCase))
                    {
                        fullText = TruncateForPrompt(
                            CollapseWhitespace($"{fullText} {contextualEvidence}"),
                            RagWriterMaxFullTextChars);
                    }
                }

                var sectionTitle = TryGetString(it, "sectionTitle")
                    ?? TryGetString(it, "SectionTitle")
                    ?? TryGetNestedString(it, "context", "sectionTitle")
                    ?? TryGetNestedString(it, "Context", "SectionTitle");
                var headingPath = TryGetString(it, "headingPath")
                    ?? TryGetString(it, "HeadingPath")
                    ?? TryGetNestedString(it, "context", "headingPath")
                    ?? TryGetNestedString(it, "Context", "HeadingPath");
                var retriever = TryGetString(it, "retriever") ?? TryGetString(it, "Retriever");
                var exactMatchHit = TryGetBool(it, "exactMatchHit") ?? TryGetBool(it, "ExactMatchHit") ?? false;
                var category = TryGetString(it, "category") ?? TryGetString(it, "Category");
                var categoryPath = TryGetString(it, "categoryPath") ?? TryGetString(it, "CategoryPath");
                var categoryRef = TryGetString(it, "categoryRef") ?? TryGetString(it, "CategoryRef");
                var docLanguage = TryGetDocumentLanguage(it);
                var profileLanguage = TryGetString(it, "profileLanguage") ?? TryGetString(it, "ProfileLanguage");
                var provenanceInfo = DeserializePromptObject(it, "provenanceInfo") ?? DeserializePromptObject(it, "ProvenanceInfo");
                var context = DeserializePromptObject(it, "context") ?? DeserializePromptObject(it, "Context");
                var rerankScore = TryGetDouble(it, "rerankScore") ?? TryGetDouble(it, "RerankScore");
                var sourceHash = TryGetString(it, "sourceHash") ?? TryGetString(it, "SourceHash");
                var revisionId = TryGetString(it, "revisionId")
                                 ?? TryGetString(it, "revision_id")
                                 ?? TryGetString(it, "RevisionId");
                var embeddingBasis = TryGetString(it, "embeddingBasis") ?? TryGetString(it, "EmbeddingBasis");
                var chunkId = TryGetString(it, "chunkId") ?? TryGetString(it, "ChunkId") ?? TryGetString(it, "chunk_id");
                var prevChunkId = TryGetString(it, "prevChunkId") ?? TryGetString(it, "PrevChunkId") ?? TryGetNestedString(it, "context", "prevChunkId") ?? TryGetNestedString(it, "Context", "PrevChunkId");
                var nextChunkId = TryGetString(it, "nextChunkId") ?? TryGetString(it, "NextChunkId") ?? TryGetNestedString(it, "context", "nextChunkId") ?? TryGetNestedString(it, "Context", "NextChunkId");
                var sameSectionChunkId = TryGetString(it, "sameSectionChunkId") ?? TryGetString(it, "SameSectionChunkId") ?? TryGetNestedString(it, "context", "sameSectionChunkId") ?? TryGetNestedString(it, "Context", "SameSectionChunkId");
                var chunkType = TryGetString(it, "chunkType")
                    ?? TryGetString(it, "ChunkType")
                    ?? TryGetNestedString(it, "context", "chunkType")
                    ?? TryGetNestedString(it, "Context", "ChunkType");
                var hasTable = TryGetBool(it, "hasTable") ?? TryGetBool(it, "HasTable");
                var hasWarning = TryGetBool(it, "hasWarning") ?? TryGetBool(it, "HasWarning");
                var hypQuestionsMatched = TryGetBool(it, "hypQuestionsMatched") ?? TryGetBool(it, "HypQuestionsMatched");
                var retrievalQuery = TryGetString(it, "retrievalQuery") ?? TryGetString(it, "retrieval_query") ?? TryGetString(it, "RetrievalQuery");
                var retrievalQueryIndex = TryGetInt(it, "retrievalQueryIndex") ?? TryGetInt(it, "retrieval_query_index") ?? TryGetInt(it, "RetrievalQueryIndex");
                var retrievalHitRank = TryGetInt(it, "retrievalHitRank") ?? TryGetInt(it, "retrieval_hit_rank") ?? TryGetInt(it, "RetrievalHitRank");
                var retrievalQuerySpecificity = TryGetInt(it, "retrievalQuerySpecificity") ?? TryGetInt(it, "retrieval_query_specificity") ?? TryGetInt(it, "RetrievalQuerySpecificity");
                var extractionQuality = CompactExtractionQualityForPrompt(it);
                var matchedContentCards = CompactMatchedContentCardsForPrompt(it);
                var profileSignals = CompactProfileSignalsForPrompt(it);
                var contentSignals = CompactRetrievalContentSignalsForPrompt(it);

                list.Add(new
                {
                    docId,
                    docPath,
                    docName,
                    pageStart = ps,
                    pageEnd = pe,
                    excerpt = text,
                    fullText,
                    score,
                    sectionTitle,
                    headingPath,
                    retriever,
                    category,
                    categoryPath,
                    categoryRef,
                    docLanguage,
                    profileLanguage,
                    exactMatchHit,
                    provenanceInfo,
                    context,
                    rerankScore,
                    sourceHash,
                    revisionId,
                    embeddingBasis,
                    chunkId,
                    prevChunkId,
                    nextChunkId,
                    sameSectionChunkId,
                    chunkType,
                    hasTable,
                    hasWarning,
                    hypQuestionsMatched,
                    retrievalQuery,
                    retrievalQueryIndex,
                    retrievalHitRank,
                    retrievalQuerySpecificity,
                    extractionQuality,
                    matchedContentCards,
                    profileSignals,
                    contentSignals,
                    // Canonical evidence leaves semantic selection to the client model.
                    selectionHints = sourceBackedCanonical
                        ? null
                        : BuildRagSelectionHintsPayload(BuildRagHitSummary(it), query: null),
                    contextualSnippet = string.IsNullOrWhiteSpace(contextualSnippet) ? null : contextualSnippet
                });
            }

            var guidance = DeserializePromptObject(raw, "guidance");
            var metrics = DeserializePromptObject(raw, "metrics")
                          ?? DeserializePromptObject(raw, "timings");
            var meta = metrics is null ? null : new { metrics };
            var query = TryGetString(raw, "query") ?? TryGetString(raw, "Query");
            var payload = new { query, hits = list, guidance, meta };
            return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;
        }
        catch
        {
            return JsonDocument.Parse("{\"hits\":[]}").RootElement;
        }
    }

    private static object? CompactMatchedContentCardsForPrompt(
        JsonElement item,
        int maxCards = 12,
        bool includeEvidence = true,
        int maxQuantityFacts = 12,
        int maxFacts = 16,
        int evidenceTextChars = 180)
    {
        if (item.ValueKind != JsonValueKind.Object
            || (!item.TryGetProperty("matchedContentCards", out var cards)
                && !item.TryGetProperty("matched_content_cards", out cards)
                && !item.TryGetProperty("MatchedContentCards", out cards)
                && !item.TryGetProperty("contentCards", out cards)
                && !item.TryGetProperty("content_cards", out cards)
                && !item.TryGetProperty("ContentCards", out cards))
            || cards.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var compact = new List<object>();
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
            var evidence = includeEvidence
                ? CompactContentCardEvidenceForPrompt(card, maxQuantityFacts, maxFacts, evidenceTextChars)
                : null;
            var hasEvidence = evidence is not null;

            compact.Add(new
            {
                title = title.Trim(),
                contentCardId = TryGetString(card, "contentCardId") ?? TryGetString(card, "content_card_id") ?? TryGetString(card, "ContentCardId"),
                pageStart = TryGetInt(card, "pageStart") ?? TryGetInt(card, "page_start") ?? TryGetInt(card, "PageStart"),
                pageEnd = TryGetInt(card, "pageEnd") ?? TryGetInt(card, "page_end") ?? TryGetInt(card, "PageEnd"),
                kind = TryGetString(card, "kind") ?? TryGetString(card, "Kind"),
                signals = signals.Length == 0 ? null : signals,
                evidenceKind = hasEvidence ? "card_fact" : "card_title",
                isFinalEvidence = hasEvidence,
                requiresConcreteRetrieval = !hasEvidence,
                evidence
            });

            if (compact.Count >= Math.Clamp(maxCards, 1, 12))
                break;
        }

        return compact.Count == 0 ? null : compact;
    }

    private static object? CompactProfileSignalsForPrompt(JsonElement item)
    {
        var profile = TryGetObject(item, "profileSignals")
                      ?? TryGetObject(item, "profile_signals")
                      ?? TryGetObject(item, "ProfileSignals");
        if (profile is null)
            return null;

        var value = profile.Value;
        var compact = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["profileVersion"] = TryGetString(value, "profileVersion") ?? TryGetString(value, "profile_version") ?? TryGetString(value, "ProfileVersion"),
            ["language"] = TryGetString(value, "language") ?? TryGetString(value, "Language"),
            ["keywords"] = ExtractProfileSignalList(value, "keywords", "keywordMatches", "Keywords", "KeywordMatches", maxItems: 8),
            ["entities"] = ExtractProfileSignalList(value, "entities", "entityMatches", "Entities", "EntityMatches", maxItems: 8),
            ["topics"] = ExtractProfileSignalList(value, "topics", "topicMatches", "Topics", "TopicMatches", maxItems: 8),
            ["hypotheticalQuestions"] = ExtractProfileSignalList(value, "hypotheticalQuestions", "hypothetical_questions", "HypotheticalQuestions", maxItems: 4),
            ["limits"] = ExtractProfileSignalList(value, "limits", "limitMatches", "Limits", "LimitMatches", maxItems: 4),
            ["matchedTerms"] = ExtractProfileSignalList(value, "matchedTerms", "matched_terms", "MatchedTerms", maxItems: 12),
            ["matchCount"] = TryGetInt(value, "matchCount") ?? TryGetInt(value, "match_count") ?? TryGetInt(value, "MatchCount")
        };

        var filtered = compact
            .Where(static pair => pair.Value switch
            {
                null => false,
                string text => !string.IsNullOrWhiteSpace(text),
                List<string> list => list.Count > 0,
                _ => true
            })
            .ToDictionary(static pair => pair.Key, static pair => pair.Value!, StringComparer.Ordinal);

        return filtered.Count == 0 ? null : filtered;
    }

    private static object? CompactRetrievalContentSignalsForPrompt(JsonElement item)
    {
        var contentRole = TryGetString(item, "contentRole")
                          ?? TryGetString(item, "ContentRole")
                          ?? TryGetNestedString(item, "context", "contentRole")
                          ?? TryGetNestedString(item, "Context", "ContentRole");
        var navigationReason = TryGetString(item, "navigationReason")
                               ?? TryGetString(item, "NavigationReason")
                               ?? TryGetNestedString(item, "context", "navigationReason")
                               ?? TryGetNestedString(item, "Context", "NavigationReason");
        var navigationScore = TryGetDouble(item, "navigationScore")
                              ?? TryGetDouble(item, "NavigationScore")
                              ?? TryGetNestedDouble(item, "context", "navigationScore")
                              ?? TryGetNestedDouble(item, "Context", "NavigationScore");
        var contentDensityScore = TryGetDouble(item, "contentDensityScore")
                                  ?? TryGetDouble(item, "ContentDensityScore")
                                  ?? TryGetNestedDouble(item, "context", "contentDensityScore")
                                  ?? TryGetNestedDouble(item, "Context", "ContentDensityScore");
        var contextElement = TryGetObject(item, "context")
                             ?? TryGetObject(item, "Context")
                             ?? TryGetObject(item, "contentSignals")
                             ?? TryGetObject(item, "content_signals")
                             ?? TryGetObject(item, "ContentSignals");
        var contentSignalsElement = TryGetObject(item, "contentSignals")
                                    ?? TryGetObject(item, "content_signals")
                                    ?? TryGetObject(item, "ContentSignals");
        var sourceUnitOrdinals = contextElement.HasValue
            ? TryGetIntList(contextElement.Value, "sourceUnitOrdinals", "source_unit_ordinals", "SourceUnitOrdinals")
            : new List<int>();
        if (sourceUnitOrdinals.Count == 0 && contentSignalsElement.HasValue)
            sourceUnitOrdinals = TryGetIntList(contentSignalsElement.Value, "sourceUnitOrdinals", "source_unit_ordinals", "SourceUnitOrdinals");
        if (sourceUnitOrdinals.Count == 0)
            sourceUnitOrdinals = TryGetIntList(item, "sourceUnitOrdinals", "source_unit_ordinals", "SourceUnitOrdinals");
        var sourceUnitStartOrdinal = contextElement.HasValue
            ? TryGetInt(contextElement.Value, "sourceUnitStartOrdinal") ?? TryGetInt(contextElement.Value, "source_unit_start_ordinal") ?? TryGetInt(contextElement.Value, "SourceUnitStartOrdinal")
            : null;
        if (!sourceUnitStartOrdinal.HasValue && contentSignalsElement.HasValue)
            sourceUnitStartOrdinal = TryGetInt(contentSignalsElement.Value, "sourceUnitStartOrdinal") ?? TryGetInt(contentSignalsElement.Value, "source_unit_start_ordinal") ?? TryGetInt(contentSignalsElement.Value, "SourceUnitStartOrdinal");
        sourceUnitStartOrdinal ??= TryGetInt(item, "sourceUnitStartOrdinal") ?? TryGetInt(item, "source_unit_start_ordinal") ?? TryGetInt(item, "SourceUnitStartOrdinal");
        var sourceUnitEndOrdinal = contextElement.HasValue
            ? TryGetInt(contextElement.Value, "sourceUnitEndOrdinal") ?? TryGetInt(contextElement.Value, "source_unit_end_ordinal") ?? TryGetInt(contextElement.Value, "SourceUnitEndOrdinal")
            : null;
        if (!sourceUnitEndOrdinal.HasValue && contentSignalsElement.HasValue)
            sourceUnitEndOrdinal = TryGetInt(contentSignalsElement.Value, "sourceUnitEndOrdinal") ?? TryGetInt(contentSignalsElement.Value, "source_unit_end_ordinal") ?? TryGetInt(contentSignalsElement.Value, "SourceUnitEndOrdinal");
        sourceUnitEndOrdinal ??= TryGetInt(item, "sourceUnitEndOrdinal") ?? TryGetInt(item, "source_unit_end_ordinal") ?? TryGetInt(item, "SourceUnitEndOrdinal");
        var sourceUnitCount = contextElement.HasValue
            ? TryGetInt(contextElement.Value, "sourceUnitCount") ?? TryGetInt(contextElement.Value, "source_unit_count") ?? TryGetInt(contextElement.Value, "SourceUnitCount")
            : null;
        if (!sourceUnitCount.HasValue && contentSignalsElement.HasValue)
            sourceUnitCount = TryGetInt(contentSignalsElement.Value, "sourceUnitCount") ?? TryGetInt(contentSignalsElement.Value, "source_unit_count") ?? TryGetInt(contentSignalsElement.Value, "SourceUnitCount");
        sourceUnitCount ??= TryGetInt(item, "sourceUnitCount") ?? TryGetInt(item, "source_unit_count") ?? TryGetInt(item, "SourceUnitCount");
        var chunkComposition = contextElement.HasValue
            ? TryGetString(contextElement.Value, "chunkComposition") ?? TryGetString(contextElement.Value, "chunk_composition") ?? TryGetString(contextElement.Value, "ChunkComposition")
            : null;
        if (string.IsNullOrWhiteSpace(chunkComposition) && contentSignalsElement.HasValue)
            chunkComposition = TryGetString(contentSignalsElement.Value, "chunkComposition") ?? TryGetString(contentSignalsElement.Value, "chunk_composition") ?? TryGetString(contentSignalsElement.Value, "ChunkComposition");
        chunkComposition ??= TryGetString(item, "chunkComposition") ?? TryGetString(item, "chunk_composition") ?? TryGetString(item, "ChunkComposition");

        if (string.IsNullOrWhiteSpace(contentRole)
            && string.IsNullOrWhiteSpace(navigationReason)
            && !navigationScore.HasValue
            && !contentDensityScore.HasValue
            && sourceUnitOrdinals.Count == 0
            && !sourceUnitStartOrdinal.HasValue
            && !sourceUnitEndOrdinal.HasValue
            && !sourceUnitCount.HasValue
            && string.IsNullOrWhiteSpace(chunkComposition))
        {
            return null;
        }

        return new
        {
            contentRole,
            navigationReason,
            navigationScore,
            contentDensityScore,
            sourceUnitOrdinals = sourceUnitOrdinals.Count == 0 ? null : sourceUnitOrdinals,
            sourceUnitStartOrdinal,
            sourceUnitEndOrdinal,
            sourceUnitCount,
            chunkComposition
        };
    }

    private static object? CompactContentCardEvidenceForPrompt(
        JsonElement card,
        int maxQuantityFacts = 12,
        int maxFacts = 16,
        int evidenceTextChars = 180)
    {
        var evidence = TryBuildRagHitContentCardEvidence(card);
        if (evidence is null)
            return null;

        return new
        {
            schemaVersion = evidence.SchemaVersion,
            scaleBasis = evidence.ScaleBasis is null
                ? null
                : new { count = evidence.ScaleBasis.Count, label = evidence.ScaleBasis.Label },
            quantityFacts = evidence.QuantityFacts.Count == 0
                ? null
                : evidence.QuantityFacts.Take(Math.Clamp(maxQuantityFacts, 1, 24)).Select(fact => new
                {
                    value = fact.Value,
                    unit = fact.Unit,
                    label = fact.Label,
                    sourceText = TruncateForPrompt(fact.SourceText, evidenceTextChars)
                }),
            facts = evidence.Facts is not { Count: > 0 }
                ? null
                : evidence.Facts.Take(Math.Clamp(maxFacts, 1, 32)).Select(fact => new
                {
                    kind = fact.Kind,
                    label = fact.Label,
                    value = fact.Value,
                    unit = fact.Unit,
                    sourceText = TruncateForPrompt(fact.SourceText, evidenceTextChars),
                    pageStart = fact.PageStart,
                    pageEnd = fact.PageEnd,
                    confidence = fact.Confidence
                }),
            nonScalableReasons = evidence.NonScalableReasons.Count == 0 ? null : evidence.NonScalableReasons,
            confidence = evidence.Confidence,
            language = evidence.Language
        };
    }

    private static object? CompactSelectionHintsForPrompt(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || (!item.TryGetProperty("selectionHints", out var hints)
                && !item.TryGetProperty("selection_hints", out hints)
                && !item.TryGetProperty("SelectionHints", out hints))
            || hints.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        var compact = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["evidenceRole"] = TryGetString(hints, "evidenceRole") ?? TryGetString(hints, "evidence_role") ?? TryGetString(hints, "EvidenceRole"),
            ["actionabilityScore"] = TryGetInt(hints, "actionabilityScore") ?? TryGetInt(hints, "actionability_score") ?? TryGetInt(hints, "ActionabilityScore"),
            ["supportScore"] = TryGetInt(hints, "supportScore") ?? TryGetInt(hints, "support_score") ?? TryGetInt(hints, "SupportScore"),
            ["fragmentScore"] = TryGetInt(hints, "fragmentScore") ?? TryGetInt(hints, "fragment_score") ?? TryGetInt(hints, "FragmentScore"),
            ["navigationScore"] = TryGetInt(hints, "navigationScore") ?? TryGetInt(hints, "navigation_score") ?? TryGetInt(hints, "NavigationScore"),
            ["qualityPenalty"] = TryGetInt(hints, "qualityPenalty") ?? TryGetInt(hints, "quality_penalty") ?? TryGetInt(hints, "QualityPenalty")
        };

        var nonEmpty = compact
            .Where(static pair => pair.Value is not null)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value!, StringComparer.Ordinal);
        return nonEmpty.Count == 0 ? null : nonEmpty;
    }
}
