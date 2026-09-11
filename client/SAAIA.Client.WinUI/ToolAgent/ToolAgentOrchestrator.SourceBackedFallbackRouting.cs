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

    private void EmitPlanningFinalizerDecisionTrace(
        string context,
        string intent,
        string resolution,
        PlanningAnswerSupportAnalysis analysis,
        int sourceCount,
        string answerSource)
        => EmitRagTrace(
            "planning.finalizer.decision",
            ("context", context),
            ("intent", intent),
            ("handled", true),
            ("resolution", resolution),
            ("items", analysis.ItemCount),
            ("supported", analysis.SupportedItemCount),
            ("unsupported", analysis.UnsupportedItemCount),
            ("candidates", analysis.CandidateCount),
            ("sources", sourceCount),
            ("answer_source", answerSource));

    private static bool ShouldTreatStructuredPlanningRejectionAsTerminal(string? answerSource, string? query)
    {
        if (!ShouldGateStructuredSourceBackedPlanningCoverage(query)
            || string.IsNullOrWhiteSpace(answerSource))
        {
            return false;
        }

        return answerSource.StartsWith("structured_planning_rejected_unsupported:", StringComparison.Ordinal)
            || answerSource.StartsWith("structured_planning_insufficient_after_writer_repair:", StringComparison.Ordinal)
            || answerSource.StartsWith("router+tools_structured_planning_rejected_unsupported_after_repair:", StringComparison.Ordinal)
            || answerSource.StartsWith("standalone_topic_rag_structured_planning_rejected_unsupported:", StringComparison.Ordinal);
    }

    private void EmitPlanningInsufficientFallbackTrace(
        string context,
        string intent,
        string reason,
        int candidateCount,
        bool searchAlreadyExpanded)
        => EmitRagTrace(
            "planning.insufficient_fallback",
            ("context", context),
            ("intent", intent),
            ("reason", reason),
            ("candidate_count", candidateCount),
            ("search_expanded", searchAlreadyExpanded));

    private static (string answer, List<ToolMemory.SourceRef> sources) BuildSourceBackedWriterTimeoutFallback(
        ToolResults toolResults,
        string query,
        string language)
    {
        var intentQuery = ResolveSourceBackedFallbackIntentQuery(query);
        if (ShouldGateStructuredSourceBackedPlanningCoverage(intentQuery)
            && TryBuildSupportedStructuredPlanningAnswer(
                toolResults,
                language,
                intentQuery,
                out var supportedAnswer,
                out var supportedSources,
                out _))
        {
            return (RemoveTrailingModelEmittedSourceList(supportedAnswer).Trim(), supportedSources);
        }

        var answer = BuildReadableSourceBackedCandidateListFallbackAnswer(toolResults, intentQuery, language);
        if (string.IsNullOrWhiteSpace(answer) && LooksLikeAnyDocumentaryPlanningRequest(intentQuery))
        {
            answer = BuildReadablePartialPlanningEvidenceAnswer(
                SelectSourceBackedExtractiveHits(toolResults, intentQuery, maxHits: 8).ToList(),
                intentQuery,
                language);
        }

        if (string.IsNullOrWhiteSpace(answer))
        {
            answer = BuildSourceBackedSafeFallbackAnswer(
                toolResults,
                intentQuery,
                language,
                shouldAvoidRaw: true);
        }

        if (string.IsNullOrWhiteSpace(answer) || LooksLikeBroadEvidenceStillInsufficientAnswer(answer))
        {
            var readableFallback = BuildReadableSourceBackedCandidateListFallbackAnswer(toolResults, intentQuery, language);
            if (!string.IsNullOrWhiteSpace(readableFallback))
                answer = readableFallback;
        }

        if (string.IsNullOrWhiteSpace(answer))
        {
            answer = BuildSourceBackedSafeFallbackAnswer(
                toolResults,
                query,
                language,
                shouldAvoidRaw: true);
        }

        var sources = LooksLikeAnyDocumentaryPlanningRequest(intentQuery)
            ? DeriveSourcesFromPlanningHits(toolResults, intentQuery)
            : DeriveSourcesForSourceBackedFallback(toolResults, intentQuery);
        if (sources.Count == 0)
            sources = DeriveSourcesFromRankedRagHits(toolResults, intentQuery);
        if (sources.Count == 0)
            sources = DeriveSourcesFromRagHits(toolResults).Take(8).ToList();

        return ((answer ?? string.Empty).Trim(), sources);
    }

    private static string TryBuildNoRagEvidenceAnswerForEmptySearch(ToolResults toolResults, string language, string? query)
    {
        var ragItems = toolResults.Items
            .Where(static x => x.ToolName is "rag.search" or "rag.multi_search")
            .ToList();
        if (ragItems.Count == 0)
            return string.Empty;

        if (ragItems.Any(static x => HasRagBusyResult(x.Result) || IsRagBusyError(TryGetStructuredToolError(x))))
            return DeterministicAgentText.RagSearchBusy(language);

        if (ragItems.Any(static x => HasRagHits(x.Result)))
            return string.Empty;

        if (IsBroadenedSourceSearchConfirmationEnvelope(query))
        {
            var intentQuery = ResolveSourceBackedFallbackIntentQuery(query ?? string.Empty);
            return BuildBroadEvidenceStillInsufficientAnswer(
                language,
                query ?? string.Empty,
                intentQuery,
                nearbyHitCount: 0,
                searchAlreadyExpanded: true);
        }

        if (ShouldOfferBroadenedSourceSearch(query) || LooksLikeBroadEmptySourceSearchRequest(query))
        {
            if (HasAttemptedBroadenedSourceBackedRetrieval(toolResults))
            {
                return BuildBroadEvidenceStillInsufficientAnswer(
                    language,
                    query ?? string.Empty,
                    query ?? string.Empty,
                    nearbyHitCount: 0,
                    searchAlreadyExpanded: true);
            }

            return DeterministicAgentText.AnswerNotEnoughUsableInfo(language)
                + " "
                + DeterministicAgentText.SourceBackedExpandedSearchOffer(language);
        }

        return DeterministicAgentText.AnswerNotEnoughUsableInfo(language)
            + " "
            + DeterministicAgentText.SourceBackedClarificationRequest(language);
    }

    private static bool HasRagBusyResult(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object)
            return false;

        if (TryGetBool(result, "busy") is true || TryGetBool(result, "Busy") is true)
            return true;

        return IsRagBusyError(TryGetString(result, "error") ?? TryGetString(result, "Error"));
    }

    private static string TryBuildAmbiguousBareDocumentaryFragmentAnswer(
        ToolResults toolResults,
        string userMessage,
        string language,
        out List<ToolMemory.SourceRef> sources)
    {
        sources = new List<ToolMemory.SourceRef>();
        if (!LooksLikeAmbiguousBareDocumentaryFragment(userMessage))
            return string.Empty;

        var hasRagHits = toolResults.Items
            .Where(static x => x.ToolName is "rag.search" or "rag.multi_search")
            .Any(static x => HasRagHits(x.Result));
        if (!hasRagHits)
            return string.Empty;

        var answer = BuildSourceBackedSafeFallbackAnswer(
            toolResults,
            userMessage,
            language,
            shouldAvoidRaw: true);
        if (string.IsNullOrWhiteSpace(answer))
            answer = DeterministicAgentText.AnswerNotEnoughUsableInfo(language)
                + " "
                + DeterministicAgentText.SourceBackedClarificationRequest(language);

        sources = DeriveSourcesFromRankedRagHits(toolResults, userMessage);
        if (sources.Count == 0)
            sources = DeriveSourcesFromRagHits(toolResults).Take(8).ToList();

        return answer;
    }

    private static List<ToolMemory.SourceRef> DeriveSourcesForSourceBackedFallback(ToolResults toolResults, string query)
    {
        List<ToolMemory.SourceRef> sources;
        var requiresStrictStructuredPlanningSources = ShouldGateStructuredSourceBackedPlanningCoverage(query);
        if (LooksLikeAnyDocumentaryPlanningRequest(query))
        {
            sources = DeriveSourcesFromPlanningHits(toolResults, query);
            if (requiresStrictStructuredPlanningSources)
                return new List<ToolMemory.SourceRef>();
        }
        else if (LooksLikeGenericCollectionOrListRequest(query)
            || LooksLikeSourceBackedOptionRequest(query)
            || LooksLikeSourceBackedPairingRecommendationRequest(query)
            || LooksLikeSoftChoiceRecommendationRequest(query))
        {
            sources = DeriveSourcesFromOptionHits(toolResults, query);
        }
        else if (LooksLikeSourceBackedActionRequest(query)
            || LooksLikeComparativeDocumentaryRequest(query)
            || ShouldUseSourceBackedExtractiveAnswer(query, toolResults))
        {
            sources = DeriveSourcesFromExtractiveHits(toolResults, query);
        }
        else
        {
            sources = DeriveSourcesFromRagHits(toolResults).Take(8).ToList();
        }

        if (requiresStrictStructuredPlanningSources)
            return new List<ToolMemory.SourceRef>();

        if (sources.Count == 0)
            sources = DeriveSourcesFromExtractiveHits(toolResults, query);
        if (sources.Count == 0)
            sources = DeriveSourcesFromRagHits(toolResults).Take(8).ToList();

        return sources;
    }

    private static bool LooksLikeAmbiguousBareDocumentaryFragment(string? query)
    {
        var raw = CollapseWhitespace(query ?? string.Empty).Trim();
        if (raw.Length < 8 || raw.Length > 90 || raw.Contains('?'))
            return false;

        raw = raw.Trim(' ', '.', '!', ':', ';');
        var normalized = NormalizeLexicalLookup(raw);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2 || words.Length > 8)
            return false;

        if (!Regex.IsMatch(
                normalized,
                @"^(?:un|une|des|du|de la|de l|a|an|some|una|unos|unas|um|uma|ein|eine|einen|einem|uno|dei|delle|del)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        if (Regex.IsMatch(
                normalized,
                @"\b(?:donne|donner|trouve|trouver|cherche|chercher|liste|lister|montre|montrer|explique|expliquer|resume|resumer|propose|proposer|recommande|recommander|compare|comparer|give|find|search|list|show|explain|summari[sz]e|propose|recommend|compare|buscar|encontrar|listar|mostrar|explicar|resumir|proponer|recomendar|comparar|procurar|listar|mostrar|explicar|resumir|propor|recomendar|comparar|finden|suchen|auflisten|zeigen|erklaren|zusammenfassen|vorschlagen|empfehlen|vergleichen|trovare|cercare|elencare|mostrare|spiegare|riassumere|proporre|raccomandare|confrontare)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        if (Regex.IsMatch(
                normalized,
                @"\b(?:pour|avec|sans|selon|for|with|without|against|para|con|sin|sem|com|mit|ohne|gegen|per|con|senza)\b",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        return true;
    }

}
