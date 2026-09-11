using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    private bool ShouldRunDocumentaryProbe(string effectiveUserMessage, RouterPlan plan)
    {
        if (plan is null)
            return false;
        if (!string.Equals(plan.Intent, "chat.general", StringComparison.OrdinalIgnoreCase))
            return false;
        if (plan.ToolCalls.Count > 0 || plan.NeedClarification)
            return false;

        var s = (effectiveUserMessage ?? string.Empty).Trim();
        if (s.Length < 12)
            return false;

        if (Regex.IsMatch(s, @"^(?:hi|hello|bonjour|salut|merci|thanks?|ok|okay)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return false;

        if (LooksLikeSourceBackedActionRequest(s))
            return false;
        if (LooksLikeComparativeDocumentaryRequest(s))
            return false;
        if (LooksLikeSourceBackedAdaptationRequest(s))
            return false;

        return Regex.IsMatch(s, @"\b(?:qu['Ã¢â‚¬â„¢]est\s*ce\s+que\s+tu\s+peux\s+me\s+dire|que\s+peux\s*tu\s+me\s+dire|parle\s*[- ]?moi|au\s+sujet\s+de|a\s+propos\s+de|ÃƒÂ \s+propos\s+de|what\s+can\s+you\s+tell\s+me|tell\s+me\s+about|about\s+the|regarding|concerning)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || s.Contains("?", StringComparison.Ordinal)
            || LooksLikeBroadDocumentaryInformationRequest(s);
    }


    private static bool ShouldDeferDocumentaryProbeWriterForBroaderExploration(
        ToolResults probeToolResults,
        string effectiveUserMessage,
        string language)
    {
        var probeExpansionQuery = ResolveSourceBackedFallbackIntentQuery(effectiveUserMessage);
        var probeExpansionAnalysis = AnalyzeSourceBackedEvidenceSufficiency(
            probeToolResults,
            probeExpansionQuery,
            language);
        return ShouldAllowSourceBackedBroadResearchPass(
                   probeExpansionQuery,
                   probeExpansionAnalysis,
                   IsBroadenedSourceSearchConfirmationEnvelope(effectiveUserMessage))
               || IsBroadenedSourceSearchConfirmationEnvelope(effectiveUserMessage)
               || ShouldRequireWriterForBroadDocumentaryFinal(probeToolResults, effectiveUserMessage, language);
    }

    private static int ResolveDocumentaryProbeTopK(string query)
        => LooksLikeAnyDocumentaryPlanningRequest(query)
            ? NormalizeSourceBackedPlanningTopK(null, query)
            : LooksLikeBroadSynthesisRequestShape(query)
              || LooksLikeUserNeedsSynthesizedDecisionOrPlan(query)
              || LooksLikeMultipleCandidateSynthesisRequest(query)
              || LooksLikeSoftChoiceRecommendationRequest(query)
              || LooksLikeSourceBackedPairingRecommendationRequest(query)
                ? 12
                : 8;

    private static bool ShouldExpandDocumentaryProbeRetrieval(ToolResults toolResults, string? query, string language)
    {
        if (!toolResults.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search"))
            return true;

        if (LooksLikeAnyDocumentaryPlanningRequest(query))
            return ShouldExpandSourceBackedPlanningRetrieval(toolResults, query, language);

        var coverage = EvaluateBroadSourceBackedSynthesisCoverage(toolResults, query);
        if (coverage.UsableHitCount == 0)
            return true;

        if (LooksLikeDocumentContentSelectionExplanationRequest(query))
            return coverage.DistinctDocumentCount < 3;

        if (LooksLikeBroadSynthesisRequestShape(query)
            || LooksLikeBroadSourceBackedCompositionRequest(query)
            || LooksLikeMultipleCandidateSynthesisRequest(query)
            || LooksLikeSoftChoiceRecommendationRequest(query)
            || LooksLikeSourceBackedPairingRecommendationRequest(query)
            || LooksLikeUserNeedsSynthesizedDecisionOrPlan(query))
        {
            return !coverage.IsAdequate;
        }

        return coverage.UsableHitCount < 2
               && coverage.RichEvidenceCount == 0
               && coverage.DistinctDocumentCount < 2;
    }

    private static bool ShouldUseWriterForDocumentaryProbeAnswer(ToolResults toolResults, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)
            || LooksLikeStrictCertificationOrExactProofRequest(query)
            || LooksLikeCorpusClaimVerificationRequest(query)
            || !toolResults.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search"))
        {
            return false;
        }

        var coverage = EvaluateBroadSourceBackedSynthesisCoverage(toolResults, query);
        if (coverage.UsableHitCount == 0)
            return false;

        if (LooksLikeDocumentContentSelectionExplanationRequest(query))
            return coverage.DistinctDocumentCount >= 3
                && coverage.UsableHitCount >= 3;

        if (ShouldAvoidRawSourceBackedFallback(query)
            || ShouldRequireWriterForBroadDocumentaryFinal(toolResults, query)
            || ShouldPreferWriterForPolishedSourceBackedAnswer(toolResults, query))
        {
            return coverage.RichEvidenceCount >= 1
                   || coverage.DistinctSourcePageCount >= 2
                   || coverage.UsableHitCount >= 2;
        }

        return ShouldUseWriterForBroadSourceBackedSynthesis(toolResults, query);
    }

    private static bool IsBetterDocumentaryProbeCoverage(
        ToolResults current,
        ToolResults candidate,
        string? query,
        string language)
    {
        if (!candidate.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search"))
            return false;
        if (!current.Items.Any(static item => item.ToolName is "rag.search" or "rag.multi_search"))
            return true;

        if (LooksLikeAnyDocumentaryPlanningRequest(query))
            return IsBetterSourceBackedPlanningCoverage(current, candidate, query, language);

        return IsBetterSourceBackedEvidenceCoverage(current, candidate, query, language);
    }

    private static string[] BuildDocumentaryProbeRetrievalQueries(string query)
    {
        var queries = new List<string>();
        void Add(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                AddDistinctQuery(queries, CollapseWhitespace(value));
        }

        if (LooksLikeAnyDocumentaryPlanningRequest(query))
        {
            foreach (var retrievalQuery in BuildPlanningExplorationRetrievalQueries(query))
                Add(retrievalQuery);
            if (!ShouldGateStructuredSourceBackedPlanningCoverage(query))
            {
                foreach (var retrievalQuery in BuildPlanningRetrievalQueries(query))
                    Add(retrievalQuery);
            }
        }
        else
        {
            Add(query);
            Add(NormalizeRagQueryForRetrieval(query));
            Add(BuildRagEvidenceSelectionQuery(query));
            foreach (var retrievalQuery in BuildSourceBackedEvidenceExpansionRetrievalQueries(query))
                Add(retrievalQuery);
        }

        var signalTerms = ExtractQuerySignalTerms(NormalizeLexicalLookup(query))
            .Where(static term => term.Length >= 4)
            .Where(static term => !IsGenericDocumentaryProbeTerm(term))
            .Take(6)
            .ToArray();
        if (!ShouldGateStructuredSourceBackedPlanningCoverage(query) && signalTerms.Length > 0)
        {
            Add(string.Join(' ', signalTerms));
            foreach (var term in signalTerms.Take(4))
                Add(term);
        }

        return queries
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToArray();
    }

    private static bool IsGenericDocumentaryProbeTerm(string term)
    {
        var normalized = NormalizeLexicalLookup(term);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        return normalized is
            "document" or "documents" or "source" or "sources" or "fichier" or "fichiers" or
            "dossier" or "dossiers" or "corpus" or "page" or "pages" or
            "sujet" or "theme" or "topic" or "about" or "regarding" or "concerning" or
            "explique" or "expliquer" or "expliquez" or "parle" or "parler" or
            "tell" or "explain" or "describe" or "descripcion" or "descricao" or
            "explica" or "explicar" or "erklare" or "erklaren" or "spiega" or "spiegare";
    }

    private static ToolResults BuildProbeRagToolResults(IReadOnlyList<RagItem> hits)
    {
        var payload = new
        {
            hits = hits.Select(x => new
            {
                score = x.Score,
                docId = x.DocId,
                docName = x.DocName,
                docPath = x.DocPath,
                category = x.Category,
                categoryPath = x.CategoryPath,
                categoryRef = x.CategoryRef,
                docLanguage = x.DocLanguage,
                profileLanguage = x.ProfileLanguage,
                pageStart = x.PageStart ?? 1,
                pageEnd = x.PageEnd ?? x.PageStart ?? 1,
                chunkId = x.ChunkId,
                chunkIndex = x.ChunkIndex,
                excerpt = string.IsNullOrWhiteSpace(x.Snippet) ? x.Text : x.Snippet,
                fullText = x.Text,
                sectionTitle = x.SectionTitle ?? x.Context?.SectionTitle,
                headingPath = x.HeadingPath ?? x.Context?.HeadingPath,
                retriever = x.Retriever,
                provenanceInfo = x.ProvenanceInfo,
                context = x.Context,
                rerankScore = x.RerankScore,
                exactMatchHit = x.ExactMatchHit ?? false,
                sourceHash = x.SourceHash,
                embeddingBasis = x.EmbeddingBasis,
                chunkType = x.ChunkType ?? x.Context?.ChunkType,
                prevChunkId = x.PrevChunkId ?? x.Context?.PrevChunkId,
                nextChunkId = x.NextChunkId ?? x.Context?.NextChunkId,
                sameSectionChunkId = x.SameSectionChunkId ?? x.Context?.SameSectionChunkId,
                hasTable = x.HasTable,
                hasWarning = x.HasWarning,
                hypQuestionsMatched = x.HypQuestionsMatched,
                extractionQuality = x.ExtractionQuality,
                contextualSnippet = x.ContextualSnippet,
                matchedContentCards = x.MatchedContentCards,
                profileSignals = x.ProfileSignals,
                selectionHints = x.SelectionHints
            }).ToList()
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = "rag.search",
            Result = doc.RootElement.Clone()
        });
        return toolResults;
    }

}
