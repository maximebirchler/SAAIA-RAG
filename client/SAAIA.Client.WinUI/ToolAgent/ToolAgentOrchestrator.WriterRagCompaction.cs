using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using SAAIA.Client.WinUI.Models;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static ToolResults CompactRagToolResultsForWriter(ToolResults toolResults, string userMessage)
    {
        var precise = !string.IsNullOrWhiteSpace(TryExtractRequestedItemTitle(userMessage))
                      || LooksLikeShortTechnicalEvidenceTopic(userMessage);
        var compacted = new ToolResults();
        var seenRagHitKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasRagEvidence = toolResults.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search");
        var ragEvidenceItems = toolResults.Items
            .Where(static item => item.ToolName is "rag.search" or "rag.multi_search"
                                  && string.IsNullOrWhiteSpace(item.Error)
                                  && item.Result.ValueKind == JsonValueKind.Object)
            .ToList();
        var mergeRagEvidence = ShouldMergeRagEvidenceForWriter(userMessage, precise, ragEvidenceItems.Count);
        if (mergeRagEvidence)
        {
            compacted.Items.Add(new ToolResults.Item
            {
                ToolName = ragEvidenceItems.Any(static item => item.ToolName == "rag.multi_search") ? "rag.multi_search" : "rag.search",
                Error = null,
                DurationMs = ragEvidenceItems.Sum(static item => Math.Max(0, item.DurationMs)),
                Result = CompactMergedRagResultsForWriter(ragEvidenceItems.Select(static item => item.Result).ToList(), userMessage, precise)
            });
        }

        foreach (var item in toolResults.Items)
        {
            if (item.ToolName is "rag.search" or "rag.multi_search"
                && string.IsNullOrWhiteSpace(item.Error)
                && item.Result.ValueKind == JsonValueKind.Object)
            {
                if (mergeRagEvidence)
                    continue;

                compacted.Items.Add(new ToolResults.Item
                {
                    ToolName = item.ToolName,
                    Error = item.Error,
                    DurationMs = item.DurationMs,
                    Result = CompactRagResultForWriter(item.Result, userMessage, precise, seenRagHitKeys)
                });
                continue;
            }

            if (item.ToolName == "summary.search"
                && string.IsNullOrWhiteSpace(item.Error)
                && item.Result.ValueKind == JsonValueKind.Object)
            {
                compacted.Items.Add(new ToolResults.Item
                {
                    ToolName = item.ToolName,
                    Error = item.Error,
                    DurationMs = item.DurationMs,
                    Result = CompactSummarySearchResultForWriter(item.Result)
                });
                continue;
            }

            if (item.ToolName is "documents.tree" or "documents.navigation" && string.IsNullOrWhiteSpace(item.Error))
                continue;

            if (hasRagEvidence && item.ToolName == "inventory.rendered" && string.IsNullOrWhiteSpace(item.Error))
                continue;

            compacted.Items.Add(item);
        }

        return compacted;
    }

    private static bool ShouldMergeRagEvidenceForWriter(string userMessage, bool precise, int ragEvidenceItemCount)
    {
        if (precise || ragEvidenceItemCount <= 1)
            return false;

        return LooksLikeAnyDocumentaryPlanningRequest(userMessage)
               || LooksLikeBroadSynthesisRequestShape(userMessage)
               || LooksLikeBroadSourceBackedCompositionRequest(userMessage)
               || LooksLikeMultipleCandidateSynthesisRequest(userMessage)
               || LooksLikeGenericCollectionOrListRequest(userMessage)
               || LooksLikeSoftChoiceRecommendationRequest(userMessage)
               || LooksLikeSourceBackedPairingRecommendationRequest(userMessage)
               || LooksLikeUserNeedsSynthesizedDecisionOrPlan(userMessage);
    }

    private static JsonElement CompactSummarySearchResultForWriter(JsonElement result, int maxItems = 20, int summaryTextChars = 1600)
    {
        try
        {
            if (!result.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return result;

            var list = new List<object?>();
            foreach (var it in items.EnumerateArray().Where(static entry => entry.ValueKind == JsonValueKind.Object).Take(Math.Max(0, maxItems)))
            {
                var source = TryBuildSourceRefFromSummarySearchItem(it);
                var sourcePayload = source is null
                    ? null
                    : BuildSourcePayloadItems(new List<ToolMemory.SourceRef> { source }).FirstOrDefault();
                var docPath = source?.DocPath ?? TryGetString(it, "docPath") ?? TryGetString(it, "DocPath") ?? string.Empty;
                var docName = TryGetString(it, "docName") ?? TryGetString(it, "DocName") ?? Path.GetFileName(docPath);

                list.Add(new
                {
                    docId = source?.DocId ?? TryGetString(it, "docId") ?? TryGetString(it, "DocId"),
                    docPath,
                    docName,
                    level = TryGetString(it, "level") ?? TryGetString(it, "Level"),
                    docLanguage = source?.DocLanguage ?? TryGetDocumentLanguage(it),
                    profileLanguage = source?.ProfileLanguage ?? TryGetString(it, "profileLanguage") ?? TryGetString(it, "ProfileLanguage"),
                    category = source?.Category ?? TryGetString(it, "category") ?? TryGetString(it, "Category"),
                    categoryRef = source?.CategoryRef ?? TryGetString(it, "categoryRef") ?? TryGetString(it, "CategoryRef"),
                    categoryPath = source?.CategoryPath ?? TryGetString(it, "categoryPath") ?? TryGetString(it, "CategoryPath"),
                    sourceHash = source?.SourceHash ?? TryGetString(it, "sourceHash") ?? TryGetString(it, "SourceHash"),
                    pageStart = source?.PageStart ?? TryGetInt(it, "pageStart") ?? TryGetInt(it, "PageStart"),
                    pageEnd = source?.PageEnd ?? TryGetInt(it, "pageEnd") ?? TryGetInt(it, "PageEnd"),
                    label = source?.Label ?? TryGetString(it, "label") ?? TryGetString(it, "Label"),
                    chunkId = source?.ChunkId ?? TryGetString(it, "chunkId") ?? TryGetString(it, "ChunkId"),
                    summaryText = TruncateForPrompt(TryGetString(it, "summaryText") ?? TryGetString(it, "SummaryText"), summaryTextChars),
                    evidenceSurface = "stored_summary",
                    sourceScope = "document_profile",
                    isFinalEvidence = false,
                    requiresConcreteRetrieval = true,
                    writerUse = "Use only to understand document scope or plan follow-up retrieval; do not cite as factual evidence unless supported by page-grounded RAG hits.",
                    meta = CompactSummaryMetaForPrompt(it),
                    extractionQuality = source is null ? CompactExtractionQualityForPrompt(it) : BuildSourceExtractionQualityPayload(source),
                    matchedContentCards = source is null ? CompactMatchedContentCardsForPrompt(it) : BuildSourceContentCardsPayload(source),
                    profileSignals = source is null ? CompactProfileSignalsForPrompt(it) : BuildSourceProfileSignalsPayload(source),
                    selectionHints = source is null ? CompactSelectionHintsForPrompt(it) : BuildSourceSelectionHintsPayload(source),
                    source = sourcePayload
                });
            }

            return JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                items = list,
                limit = TryGetInt(result, "limit"),
                offset = TryGetInt(result, "offset")
            })).RootElement.Clone();
        }
        catch
        {
            return result;
        }
    }

    private static object? CompactSummaryMetaForPrompt(JsonElement item)
    {
        var meta = TryGetObject(item, "meta")
                   ?? TryGetObject(item, "Meta")
                   ?? TryGetObject(item, "summaryMeta")
                   ?? TryGetObject(item, "SummaryMeta");
        if (meta is null)
            return null;

        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        AddIfPresent("generator", TryGetString(meta.Value, "generator") ?? TryGetString(meta.Value, "Generator"));
        AddIfPresent("strategy", TryGetString(meta.Value, "strategy") ?? TryGetString(meta.Value, "Strategy"));
        AddIfPresent("outputLanguage", TryGetString(meta.Value, "outputLanguage") ?? TryGetString(meta.Value, "OutputLanguage"));
        AddIfPresent("fallbackUsed", TryGetBool(meta.Value, "fallbackUsed") ?? TryGetBool(meta.Value, "FallbackUsed"));
        AddIfPresent("fallbackReason", TryGetString(meta.Value, "fallbackReason") ?? TryGetString(meta.Value, "FallbackReason"));
        AddIfPresent("qualityScore", TryGetDouble(meta.Value, "qualityScore") ?? TryGetDouble(meta.Value, "QualityScore"));
        AddIfPresent("extractionQuality",
            DeserializePromptObject(meta.Value, "extractionQuality")
            ?? DeserializePromptObject(meta.Value, "extraction_quality")
            ?? DeserializePromptObject(meta.Value, "ExtractionQuality"));
        AddIfPresent("qualitySignals",
            DeserializePromptObject(meta.Value, "qualitySignals")
            ?? DeserializePromptObject(meta.Value, "quality_signals")
            ?? DeserializePromptObject(meta.Value, "QualitySignals"));

        return payload.Count == 0 ? null : payload;

        void AddIfPresent(string key, object? value)
        {
            if (value is not null)
                payload[key] = value;
        }
    }

    private static JsonElement CompactRagResultForWriter(JsonElement result, string userMessage, bool precise, ISet<string>? seenRagHitKeys = null)
    {
        try
        {
            if (!result.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array)
                return result;

            var rawHits = hits.EnumerateArray()
                .Where(static it => it.ValueKind == JsonValueKind.Object)
                .Select(static it => it.Clone())
                .ToList();

            return CompactRagHitsForWriter(result, rawHits, userMessage, precise, seenRagHitKeys);
        }
        catch
        {
            return result;
        }
    }

    private static JsonElement CompactMergedRagResultsForWriter(
        IReadOnlyList<JsonElement> results,
        string userMessage,
        bool precise,
        int? maxHitsOverride = null)
    {
        try
        {
            var rawHits = new List<JsonElement>();
            var queries = new List<string>();
            foreach (var result in results)
            {
                if (result.ValueKind != JsonValueKind.Object)
                    continue;

                var meta = TryGetObject(result, "meta") ?? TryGetObject(result, "Meta");
                if (meta.HasValue)
                {
                    queries.AddRange(CompactStringArray(meta.Value, "queries", "Queries", maxItems: 8, maxChars: 120));
                }

                if (!result.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var hit in hits.EnumerateArray())
                {
                    if (hit.ValueKind != JsonValueKind.Object)
                        continue;

                    var retrievalQuery = TryGetString(hit, "retrievalQuery")
                                         ?? TryGetString(hit, "retrieval_query")
                                         ?? TryGetString(hit, "RetrievalQuery");
                    if (!string.IsNullOrWhiteSpace(retrievalQuery))
                        queries.Add(retrievalQuery);

                    rawHits.Add(hit.Clone());
                }
            }

            var metaPayload = new
            {
                merged = true,
                sourceResultCount = results.Count,
                rawHitCount = rawHits.Count,
                queries = queries
                    .Where(static query => !string.IsNullOrWhiteSpace(query))
                    .Select(static query => CollapseWhitespace(query))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(12)
                    .ToArray()
            };

            var maxHits = maxHitsOverride ?? (LooksLikeAnyDocumentaryPlanningRequest(userMessage)
                ? RagWriterMergedPlanningMaxHits
                : RagWriterMergedBroadMaxHits);

            return CompactRagHitsForWriter(
                originalResult: null,
                rawHits,
                userMessage,
                precise,
                seenRagHitKeys: null,
                maxHitsOverride: maxHits,
                metaOverride: metaPayload,
                guidanceOverride: null);
        }
        catch
        {
            var fallback = results.FirstOrDefault(static result => result.ValueKind == JsonValueKind.Object);
            return fallback.ValueKind == JsonValueKind.Object ? fallback : JsonDocument.Parse("""{"hits":[]}""").RootElement.Clone();
        }
    }

    private static JsonElement CompactRagHitsForWriter(
        JsonElement? originalResult,
        IReadOnlyList<JsonElement> rawHits,
        string userMessage,
        bool precise,
        ISet<string>? seenRagHitKeys = null,
        int? maxHitsOverride = null,
        object? metaOverride = null,
        object? guidanceOverride = null)
    {
        try
        {
            var prioritizeComparison = LooksLikeComparativeDocumentaryRequest(userMessage);
            var prioritizeEvidence = precise || prioritizeComparison;
            var prioritizePlanning = !prioritizeEvidence && LooksLikeAnyDocumentaryPlanningRequest(userMessage);
            var maxHits = maxHitsOverride
                          ?? (prioritizeComparison
                              ? RagWriterComparativeMaxHits
                              : prioritizeEvidence
                                  ? RagWriterMaxHits
                                  : prioritizePlanning
                                      ? RagWriterPlanningMaxHits
                                      : RagWriterBroadMaxHits);
            var excerptChars = prioritizeEvidence ? RagWriterMaxExcerptChars : RagWriterBroadExcerptChars;
            var fullTextChars = prioritizeEvidence ? RagWriterMaxFullTextChars : RagWriterBroadFullTextChars;
            var contextualChars = prioritizeEvidence ? RagWriterContextualTotalChars : RagWriterBroadContextualChars;
            var list = new List<object?>();
            var sourceHits = rawHits
                .Where(static it => !LooksLikeNavigationOnlyHit(BuildRagHitSummary(it)))
                .Where(static it => !LooksLikeLowSignalContentCandidateHit(BuildRagHitSummary(it)))
                .ToList();
            var rankedHits = RankRagHitsForWriter(sourceHits, userMessage);
            rankedHits = PreservePrimaryQueryTopHitsForWriter(
                rankedHits,
                rawHits,
                userMessage,
                maxHits);
            var selectedHits = PreserveComparativeEntityCoverageForWriter(
                rankedHits,
                sourceHits,
                userMessage,
                maxHits);
            var preserveCardLevel = LooksLikeBroadSynthesisRequestShape(userMessage)
                || selectedHits.Any(static hit => !string.IsNullOrWhiteSpace(TryBuildRagWriterContentCardKey(hit)));
            selectedHits = DeduplicateRagHitsForWriter(
                    selectedHits,
                    preserveCardLevel: preserveCardLevel)
                .ToList();
            if (seenRagHitKeys is not null)
            {
                selectedHits = selectedHits
                    .Where(hit => seenRagHitKeys.Add(BuildRagWriterVisiblePageKey(hit, preserveCardLevel)))
                    .ToList();
            }

            foreach (var it in selectedHits.Take(maxHits))
            {
                var hitSummary = BuildRagHitSummary(it);
                var docPath = TryGetString(it, "docPath") ?? string.Empty;
                var docName = TryGetString(it, "docName") ?? Path.GetFileName(docPath);
                var pageStart = ReadRagHitPageStart(it);
                var pageEnd = ReadRagHitPageEnd(it, pageStart);
                var rawExcerpt = TryGetString(it, "excerpt") ?? TryGetString(it, "text") ?? TryGetString(it, "snippet");
                var rawFullText = TryGetString(it, "fullText") ?? TryGetString(it, "text") ?? TryGetString(it, "snippet");
                var excerpt = TruncateForPrompt(rawExcerpt, excerptChars);
                var fullText = TruncateForPrompt(rawFullText, fullTextChars);
                var contextualSnippet = TruncateForPrompt(TryGetString(it, "contextualSnippet"), contextualChars);
                var extractionQuality = CompactExtractionQualityForPrompt(it);
                var contentSignals = CompactRetrievalContentSignalsForPrompt(it);
                var profileSignals = CompactProfileSignalsForPrompt(it);
                var writerEvidence = BuildWriterEvidenceCueForPrompt(hitSummary, userMessage, maxLength: prioritizeEvidence ? 180 : 260);
                var writerUse = BuildWriterUseCueForPrompt(hitSummary, userMessage);
                var keepBroadCardEvidence = ShouldKeepBroadWriterCardEvidence(hitSummary, list.Count, userMessage);
                var includeCardEvidence = prioritizeEvidence || keepBroadCardEvidence;
                var matchedContentCards = CompactMatchedContentCardsForPrompt(
                    it,
                    maxCards: prioritizeEvidence ? RagWriterMaxContentCards : ResolveBroadWriterContentCardLimit(userMessage, keepBroadCardEvidence),
                    includeEvidence: includeCardEvidence,
                    maxQuantityFacts: RagWriterMaxCardQuantityFacts,
                    maxFacts: RagWriterMaxCardFacts,
                    evidenceTextChars: RagWriterMaxCardEvidenceTextChars);
                var provenanceInfo = prioritizeEvidence
                    ? CompactRagProvenanceForPrompt(it)
                    : null;
                var context = prioritizeEvidence
                    ? CompactRagContextForPrompt(it)
                    : null;

                if (!prioritizeEvidence)
                {
                    var readableEvidence = string.IsNullOrWhiteSpace(writerEvidence) ? null : writerEvidence;
                    list.Add(new
                    {
                        docPath,
                        docName,
                        pageStart,
                        pageEnd,
                        excerpt = readableEvidence ?? (string.IsNullOrWhiteSpace(excerpt) ? null : excerpt),
                        fullText = (string?)null,
                        score = TryGetDouble(it, "score") ?? 0.0,
                        sectionTitle = TryGetString(it, "sectionTitle"),
                        headingPath = TryGetString(it, "headingPath"),
                        category = TryGetString(it, "category") ?? TryGetString(it, "Category"),
                        categoryPath = TryGetString(it, "categoryPath") ?? TryGetString(it, "category_path") ?? TryGetString(it, "CategoryPath"),
                        categoryRef = TryGetString(it, "categoryRef") ?? TryGetString(it, "category_ref") ?? TryGetString(it, "CategoryRef"),
                        docLanguage = TryGetDocumentLanguage(it),
                        profileLanguage = TryGetString(it, "profileLanguage") ?? TryGetString(it, "profile_language") ?? TryGetString(it, "ProfileLanguage"),
                        sourceHash = TryGetString(it, "sourceHash") ?? TryGetString(it, "source_hash") ?? TryGetString(it, "SourceHash"),
                        retrievalQuery = TryGetString(it, "retrievalQuery") ?? TryGetString(it, "retrieval_query") ?? TryGetString(it, "RetrievalQuery"),
                        retrievalQueryIndex = TryGetInt(it, "retrievalQueryIndex") ?? TryGetInt(it, "retrieval_query_index") ?? TryGetInt(it, "RetrievalQueryIndex"),
                        retrievalHitRank = TryGetInt(it, "retrievalHitRank") ?? TryGetInt(it, "retrieval_hit_rank") ?? TryGetInt(it, "RetrievalHitRank"),
                        retrievalQuerySpecificity = TryGetInt(it, "retrievalQuerySpecificity") ?? TryGetInt(it, "retrieval_query_specificity") ?? TryGetInt(it, "RetrievalQuerySpecificity"),
                        extractionQuality,
                        contentSignals,
                        matchedContentCards,
                        profileSignals,
                        selectionHints = BuildRagSelectionHintsPayload(hitSummary, userMessage),
                        writerEvidence = readableEvidence,
                        writerUse = string.IsNullOrWhiteSpace(writerUse) ? null : writerUse,
                        contextualSnippet = (string?)null
                    });
                    continue;
                }

                list.Add(new
                {
                    docId = TryGetString(it, "docId") ?? TryGetString(it, "doc_id") ?? TryGetString(it, "DocId"),
                    docPath,
                    docName,
                    pageStart,
                    pageEnd,
                    excerpt,
                    fullText,
                    score = TryGetDouble(it, "score") ?? 0.0,
                    sectionTitle = TryGetString(it, "sectionTitle"),
                    headingPath = TryGetString(it, "headingPath"),
                    retriever = TryGetString(it, "retriever"),
                    category = TryGetString(it, "category") ?? TryGetString(it, "Category"),
                    categoryPath = TryGetString(it, "categoryPath") ?? TryGetString(it, "category_path") ?? TryGetString(it, "CategoryPath"),
                    categoryRef = TryGetString(it, "categoryRef") ?? TryGetString(it, "category_ref") ?? TryGetString(it, "CategoryRef"),
                    docLanguage = TryGetDocumentLanguage(it),
                    profileLanguage = TryGetString(it, "profileLanguage") ?? TryGetString(it, "profile_language") ?? TryGetString(it, "ProfileLanguage"),
                    provenanceInfo,
                    context,
                    rerankScore = TryGetDouble(it, "rerankScore"),
                    exactMatchHit = TryGetBool(it, "exactMatchHit") ?? false,
                    sourceHash = TryGetString(it, "sourceHash") ?? TryGetString(it, "source_hash") ?? TryGetString(it, "SourceHash"),
                    retrievalQuery = TryGetString(it, "retrievalQuery") ?? TryGetString(it, "retrieval_query") ?? TryGetString(it, "RetrievalQuery"),
                    retrievalQueryIndex = TryGetInt(it, "retrievalQueryIndex") ?? TryGetInt(it, "retrieval_query_index") ?? TryGetInt(it, "RetrievalQueryIndex"),
                    retrievalHitRank = TryGetInt(it, "retrievalHitRank") ?? TryGetInt(it, "retrieval_hit_rank") ?? TryGetInt(it, "RetrievalHitRank"),
                    retrievalQuerySpecificity = TryGetInt(it, "retrievalQuerySpecificity") ?? TryGetInt(it, "retrieval_query_specificity") ?? TryGetInt(it, "RetrievalQuerySpecificity"),
                    embeddingBasis = TryGetString(it, "embeddingBasis") ?? TryGetString(it, "embedding_basis") ?? TryGetString(it, "EmbeddingBasis"),
                    chunkId = TryGetString(it, "chunkId") ?? TryGetString(it, "chunk_id") ?? TryGetString(it, "ChunkId"),
                    prevChunkId = TryGetString(it, "prevChunkId") ?? TryGetNestedString(it, "context", "prevChunkId"),
                    nextChunkId = TryGetString(it, "nextChunkId") ?? TryGetNestedString(it, "context", "nextChunkId"),
                    sameSectionChunkId = TryGetString(it, "sameSectionChunkId") ?? TryGetNestedString(it, "context", "sameSectionChunkId"),
                    chunkType = TryGetString(it, "chunkType"),
                    hasTable = TryGetBool(it, "hasTable"),
                    hasWarning = TryGetBool(it, "hasWarning"),
                    hypQuestionsMatched = TryGetBool(it, "hypQuestionsMatched"),
                    extractionQuality,
                    contentSignals,
                    matchedContentCards,
                    profileSignals,
                    selectionHints = BuildRagSelectionHintsPayload(hitSummary, userMessage),
                    writerEvidence = string.IsNullOrWhiteSpace(writerEvidence) ? null : writerEvidence,
                    writerUse = string.IsNullOrWhiteSpace(writerUse) ? null : writerUse,
                    contextualSnippet = string.IsNullOrWhiteSpace(contextualSnippet) ? null : contextualSnippet
                });
            }

            var meta = metaOverride
                       ?? (originalResult.HasValue ? CompactRagMetaForWriter(originalResult.Value) : null);

            object? guidance = guidanceOverride;
            if (guidance is null
                && originalResult.HasValue
                && originalResult.Value.TryGetProperty("guidance", out var guidanceEl)
                && guidanceEl.ValueKind == JsonValueKind.Object)
            {
                guidance = JsonSerializer.Deserialize<object>(guidanceEl.GetRawText());
            }

            return JsonDocument.Parse(JsonSerializer.Serialize(new { hits = list, guidance, meta })).RootElement.Clone();
        }
        catch
        {
            return originalResult ?? JsonDocument.Parse("""{"hits":[]}""").RootElement.Clone();
        }
    }

    private static IEnumerable<JsonElement> DeduplicateRagHitsForWriter(IEnumerable<JsonElement> hits, bool preserveCardLevel)
        => hits
            .GroupBy(hit => BuildRagWriterVisiblePageKey(hit, preserveCardLevel), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderByDescending(static hit => ComputeSourceBackedEvidenceRichnessScore(BuildRagHitSummary(hit)))
                .ThenByDescending(static hit => TryGetDouble(hit, "score") ?? 0.0)
                .First());

    private static string BuildRagWriterVisiblePageKey(JsonElement hit, bool preserveCardLevel)
    {
        var pageStart = Math.Max(1, ReadRagHitPageStart(hit));
        var contentCardKey = preserveCardLevel ? TryBuildRagWriterContentCardKey(hit) : string.Empty;
        var cardSuffix = string.IsNullOrWhiteSpace(contentCardKey) ? string.Empty : $"|card:{contentCardKey}";
        var categoryPath = NormalizeLexicalLookup(
            TryGetString(hit, "categoryPath")
            ?? TryGetString(hit, "category_path")
            ?? TryGetString(hit, "CategoryPath")
            ?? TryGetString(hit, "category")
            ?? TryGetString(hit, "Category")
            ?? string.Empty);
        var docPathRaw = TryGetString(hit, "docPath")
                         ?? TryGetString(hit, "doc_path")
                         ?? TryGetString(hit, "DocPath")
                         ?? string.Empty;
        var docPath = NormalizeLexicalLookup(docPathRaw);
        var docName = NormalizeLexicalLookup(
            TryGetString(hit, "docName")
            ?? TryGetString(hit, "doc_name")
            ?? TryGetString(hit, "DocName")
            ?? Path.GetFileName(docPathRaw));
        var fileName = NormalizeLexicalLookup(Path.GetFileName(docPathRaw));
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = docName;

        if (!string.IsNullOrWhiteSpace(categoryPath) && !string.IsNullOrWhiteSpace(fileName))
            return $"category:{categoryPath}|file:{fileName}|p:{pageStart}{cardSuffix}";

        if (!string.IsNullOrWhiteSpace(docPath) && LooksLikeQualifiedWriterDocPath(docPathRaw))
            return $"path:{docPath}|p:{pageStart}{cardSuffix}";

        var sourceHash = NormalizeLexicalLookup(
            TryGetString(hit, "sourceHash")
            ?? TryGetString(hit, "source_hash")
            ?? TryGetString(hit, "SourceHash")
            ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(sourceHash))
            return $"hash:{sourceHash}|p:{pageStart}{cardSuffix}";

        if (!string.IsNullOrWhiteSpace(fileName))
            return $"file:{fileName}|p:{pageStart}{cardSuffix}";

        if (!string.IsNullOrWhiteSpace(docName))
            return $"name:{docName}|p:{pageStart}{cardSuffix}";

        return $"unknown:{pageStart}{cardSuffix}";
    }

    private static string TryBuildRagWriterContentCardKey(JsonElement hit)
    {
        var cards = TryGetArray(hit, "matchedContentCards")
                    ?? TryGetArray(hit, "matched_content_cards")
                    ?? TryGetArray(hit, "MatchedContentCards")
                    ?? TryGetArray(hit, "contentCards")
                    ?? TryGetArray(hit, "content_cards")
                    ?? TryGetArray(hit, "ContentCards");
        if (!cards.HasValue || cards.Value.ValueKind != JsonValueKind.Array)
            return string.Empty;

        foreach (var card in cards.Value.EnumerateArray())
        {
            if (card.ValueKind != JsonValueKind.Object)
                continue;

            var id = NormalizeLexicalLookup(
                TryGetString(card, "contentCardId")
                ?? TryGetString(card, "content_card_id")
                ?? TryGetString(card, "ContentCardId")
                ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(id))
                return id;

            var title = NormalizeLexicalLookup(TryGetString(card, "title") ?? TryGetString(card, "Title") ?? string.Empty);
            var kind = NormalizeLexicalLookup(TryGetString(card, "kind") ?? TryGetString(card, "Kind") ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(title))
                return string.IsNullOrWhiteSpace(kind) ? title : $"{kind}:{title}";
        }

        return string.Empty;
    }

    private static bool LooksLikeQualifiedWriterDocPath(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && (path.Contains('/', StringComparison.Ordinal)
               || path.Contains('\\', StringComparison.Ordinal)
               || path.Contains(':', StringComparison.Ordinal));

    private static object? CompactRagMetaForWriter(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object)
            return null;

        var metaEl = TryGetObject(result, "meta")
                     ?? TryGetObject(result, "Meta");
        var metricsEl = TryGetObject(result, "metrics")
                        ?? TryGetObject(result, "Metrics")
                        ?? (metaEl.HasValue
                            ? TryGetObject(metaEl.Value, "metrics") ?? TryGetObject(metaEl.Value, "Metrics")
                            : null);

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["queries"] = metaEl.HasValue ? CompactStringArray(metaEl.Value, "queries", "Queries", maxItems: 8, maxChars: 160) : null,
            ["mode"] = metaEl.HasValue ? TryGetString(metaEl.Value, "mode") ?? TryGetString(metaEl.Value, "Mode") : null,
            ["category"] = metaEl.HasValue ? TryGetString(metaEl.Value, "category") ?? TryGetString(metaEl.Value, "Category") : null,
            ["categoryPath"] = metaEl.HasValue ? TryGetString(metaEl.Value, "categoryPath") ?? TryGetString(metaEl.Value, "CategoryPath") : null,
            ["categoryInferred"] = metaEl.HasValue ? TryGetBool(metaEl.Value, "categoryInferred") ?? TryGetBool(metaEl.Value, "CategoryInferred") : null,
            ["fanoutParallelism"] = metaEl.HasValue ? TryGetInt(metaEl.Value, "fanoutParallelism") ?? TryGetInt(metaEl.Value, "FanoutParallelism") : null,
            ["busyQueries"] = metaEl.HasValue ? CompactStringArray(metaEl.Value, "busyQueries", "BusyQueries", maxItems: 8, maxChars: 160) : null,
            ["degradedRetrievers"] = metaEl.HasValue ? CompactStringArray(metaEl.Value, "degradedRetrievers", "DegradedRetrievers", maxItems: 8, maxChars: 80) : null,
            ["metrics"] = metricsEl.HasValue ? CompactRagMetricsForWriter(metricsEl.Value) : null,
            ["queryRuns"] = metaEl.HasValue ? CompactRagQueryRunsForWriter(metaEl.Value) : null
        };

        var nonEmpty = payload
            .Where(static pair => pair.Value switch
            {
                null => false,
                string text => !string.IsNullOrWhiteSpace(text),
                IReadOnlyCollection<string> list => list.Count > 0,
                IReadOnlyCollection<object> list => list.Count > 0,
                _ => true
            })
            .ToDictionary(static pair => pair.Key, static pair => pair.Value!, StringComparer.Ordinal);

        return nonEmpty.Count == 0 ? null : nonEmpty;
    }

    private static object? CompactRagMetricsForWriter(JsonElement metrics)
    {
        if (metrics.ValueKind != JsonValueKind.Object)
            return null;

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tookMs"] = TryGetLong(metrics, "tookMs") ?? TryGetLong(metrics, "TookMs"),
            ["returned"] = TryGetInt(metrics, "returned") ?? TryGetInt(metrics, "Returned"),
            ["candidatesEvaluated"] = TryGetInt(metrics, "candidatesEvaluated") ?? TryGetInt(metrics, "CandidatesEvaluated"),
            ["retrieversUsed"] = CompactStringArray(metrics, "retrieversUsed", "RetrieversUsed", maxItems: 8, maxChars: 80),
            ["degradedRetrievers"] = CompactStringArray(metrics, "degradedRetrievers", "DegradedRetrievers", maxItems: 8, maxChars: 80)
        };

        var nonEmpty = payload
            .Where(static pair => pair.Value switch
            {
                null => false,
                IReadOnlyCollection<string> list => list.Count > 0,
                _ => true
            })
            .ToDictionary(static pair => pair.Key, static pair => pair.Value!, StringComparer.Ordinal);

        return nonEmpty.Count == 0 ? null : nonEmpty;
    }

    private static object? CompactRagQueryRunsForWriter(JsonElement meta)
    {
        var runs = TryGetArray(meta, "queryRuns")
                   ?? TryGetArray(meta, "QueryRuns")
                   ?? TryGetArray(meta, "query_runs");
        if (!runs.HasValue || runs.Value.ValueKind != JsonValueKind.Array)
            return null;

        var compact = new List<object>();
        foreach (var run in runs.Value.EnumerateArray())
        {
            if (run.ValueKind != JsonValueKind.Object)
                continue;

            var runMeta = TryGetObject(run, "meta") ?? TryGetObject(run, "Meta");
            var hits = TryGetArray(run, "hits") ?? TryGetArray(run, "Hits");
            var hitCount = TryGetInt(run, "hitCount") ?? TryGetInt(run, "HitCount");
            var metrics = runMeta.HasValue
                ? TryGetObject(runMeta.Value, "metrics") ?? TryGetObject(runMeta.Value, "Metrics")
                : null;
            compact.Add(new
            {
                query = TruncateForPrompt(TryGetString(run, "query") ?? TryGetString(run, "Query"), 160),
                hitCount = hitCount ?? (hits.HasValue && hits.Value.ValueKind == JsonValueKind.Array ? hits.Value.GetArrayLength() : (int?)null),
                error = TryGetString(run, "error") ?? TryGetString(run, "Error"),
                busy = TryGetBool(run, "busy") ?? TryGetBool(run, "Busy"),
                metrics = metrics.HasValue ? CompactRagMetricsForWriter(metrics.Value) : null
            });

            if (compact.Count >= 8)
                break;
        }

        return compact.Count == 0 ? null : compact;
    }

    private static string[] CompactStringArray(JsonElement item, string primaryName, string secondaryName, int maxItems, int maxChars)
    {
        var values = TryGetArray(item, primaryName)
                     ?? TryGetArray(item, secondaryName);
        if (!values.HasValue || values.Value.ValueKind != JsonValueKind.Array)
            return [];

        return values.Value.EnumerateArray()
            .Select(static value => value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(value => TruncateForPrompt(value, maxChars))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maxItems, 1, 32))
            .ToArray();
    }

    private static object? CompactRagProvenanceForPrompt(JsonElement item)
    {
        var provenance = TryGetObject(item, "provenanceInfo")
                         ?? TryGetObject(item, "ProvenanceInfo")
                         ?? TryGetObject(item, "provenance_info");
        if (!provenance.HasValue || provenance.Value.ValueKind != JsonValueKind.Object)
            return null;

        var value = provenance.Value;
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["channel"] = TryGetString(value, "channel") ?? TryGetString(value, "Channel"),
            ["label"] = TruncateForPrompt(TryGetString(value, "label") ?? TryGetString(value, "Label"), 160),
            ["chunkId"] = TryGetString(value, "chunkId") ?? TryGetString(value, "ChunkId"),
            ["pageStart"] = TryGetInt(value, "pageStart") ?? TryGetInt(value, "PageStart"),
            ["pageEnd"] = TryGetInt(value, "pageEnd") ?? TryGetInt(value, "PageEnd"),
            ["offsetStart"] = TryGetInt(value, "offsetStart") ?? TryGetInt(value, "OffsetStart"),
            ["offsetEnd"] = TryGetInt(value, "offsetEnd") ?? TryGetInt(value, "OffsetEnd")
        };

        var nonEmpty = payload
            .Where(static pair => pair.Value switch
            {
                null => false,
                string text => !string.IsNullOrWhiteSpace(text),
                _ => true
            })
            .ToDictionary(static pair => pair.Key, static pair => pair.Value!, StringComparer.Ordinal);

        return nonEmpty.Count == 0 ? null : nonEmpty;
    }

    private static object? CompactRagContextForPrompt(JsonElement item)
    {
        var context = TryGetObject(item, "context")
                      ?? TryGetObject(item, "Context");
        if (!context.HasValue || context.Value.ValueKind != JsonValueKind.Object)
            return null;

        var value = context.Value;
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["sectionTitle"] = TruncateForPrompt(TryGetString(value, "sectionTitle") ?? TryGetString(value, "SectionTitle"), 160),
            ["headingPath"] = TruncateForPrompt(TryGetString(value, "headingPath") ?? TryGetString(value, "HeadingPath"), 220),
            ["contentRole"] = TryGetString(value, "contentRole") ?? TryGetString(value, "ContentRole"),
            ["navigationReason"] = TruncateForPrompt(TryGetString(value, "navigationReason") ?? TryGetString(value, "NavigationReason"), 120),
            ["navigationScore"] = TryGetDouble(value, "navigationScore") ?? TryGetDouble(value, "NavigationScore"),
            ["contentDensityScore"] = TryGetDouble(value, "contentDensityScore") ?? TryGetDouble(value, "ContentDensityScore"),
            ["prevChunkId"] = TryGetString(value, "prevChunkId") ?? TryGetString(value, "PrevChunkId"),
            ["nextChunkId"] = TryGetString(value, "nextChunkId") ?? TryGetString(value, "NextChunkId"),
            ["sameSectionChunkId"] = TryGetString(value, "sameSectionChunkId") ?? TryGetString(value, "SameSectionChunkId")
        };

        var nonEmpty = payload
            .Where(static pair => pair.Value switch
            {
                null => false,
                string text => !string.IsNullOrWhiteSpace(text),
                _ => true
            })
            .ToDictionary(static pair => pair.Key, static pair => pair.Value!, StringComparer.Ordinal);

        return nonEmpty.Count == 0 ? null : nonEmpty;
    }
}
