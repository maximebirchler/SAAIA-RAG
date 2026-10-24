using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SAAIA.Client.WinUI.Localization;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{

    private static bool ShouldRespectLlmRouterGeneralWithoutTools(RouterPlan? plan)
        => plan is not null
           && plan.Origin == RouterPlanOrigin.Llm
           && string.Equals(plan.Intent, "chat.general", StringComparison.OrdinalIgnoreCase)
           && !plan.NeedClarification
           && (plan.ToolCalls is null || plan.ToolCalls.Count == 0);

    private static int ResolveSourceBackedEvidenceExplorationRagCallBudget(
        string? effectiveUserMessage,
        SourceBackedEvidenceSufficiency currentAnalysis,
        bool forceBroadenedExploration)
    {
        if (forceBroadenedExploration
            || LooksLikeGenericCollectionOrListRequest(effectiveUserMessage)
            || LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage)
            || LooksLikeBroadSourceBackedCompositionRequest(effectiveUserMessage)
            || LooksLikeMultipleCandidateSynthesisRequest(effectiveUserMessage)
            || LooksLikeSoftChoiceRecommendationRequest(effectiveUserMessage)
            || LooksLikeSourceBackedPairingRecommendationRequest(effectiveUserMessage)
            || LooksLikeUserNeedsSynthesizedDecisionOrPlan(effectiveUserMessage)
            || currentAnalysis.Kind is "planning" or "broad")
        {
            var budget = MaxBroadExplorationRagToolCalls;
            if (UsesSourceBackedPlanningCoverage(effectiveUserMessage)
                || currentAnalysis.Kind == "planning")
            {
                var targetSlots = Math.Max(
                    currentAnalysis.TargetSlotCount,
                    ResolveSourceBackedPlanningTargetItemCount(effectiveUserMessage));
                var minimumCandidates = Math.Max(
                    currentAnalysis.MinimumCandidateCount,
                    ResolveMinimumBroadSourceBackedSynthesisHitCount(effectiveUserMessage));
                if (targetSlots >= 10 || minimumCandidates >= 6)
                    budget = Math.Max(
                        budget,
                        Math.Min(48, Math.Max(targetSlots, minimumCandidates) + 18));
            }

            return forceBroadenedExploration
                ? Math.Min(48, Math.Max(budget, MaxBroadExplorationRagToolCalls + 12))
                : budget;
        }

        return MaxRagToolCalls;
    }

    private static int ResolveSourceBackedEvidenceExplorationTimeoutMs(
        string? effectiveUserMessage,
        SourceBackedEvidenceSufficiency currentAnalysis)
    {
        if (!UsesSourceBackedPlanningCoverage(effectiveUserMessage)
            && !string.Equals(currentAnalysis.Kind, "planning", StringComparison.OrdinalIgnoreCase))
        {
            return SourceBackedEvidenceExplorationTimeoutMs;
        }

        var targetSlots = Math.Max(
            currentAnalysis.TargetSlotCount,
            ResolveSourceBackedPlanningTargetItemCount(effectiveUserMessage));
        var minimumCandidates = Math.Max(
            currentAnalysis.MinimumCandidateCount,
            ResolveMinimumBroadSourceBackedSynthesisHitCount(effectiveUserMessage));
        var target = Math.Max(targetSlots, minimumCandidates);
        if (target <= 8)
            return SourceBackedEvidenceExplorationTimeoutMs;

        var extraMs = Math.Min(360000, (target - 8) * 30000);
        return Math.Clamp(
            SourceBackedEvidenceExplorationTimeoutMs + extraMs,
            SourceBackedEvidenceExplorationTimeoutMs,
            MaxSourceBackedEvidenceExplorationTimeoutMs);
    }

    private static int ResolveSourceBackedPlanningWriterTimeoutMs(string? effectiveUserMessage)
    {
        if (ShouldGateStructuredSourceBackedPlanningCoverage(effectiveUserMessage)
            || LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage)
            || LooksLikeBroadSourceBackedCompositionRequest(effectiveUserMessage)
            || LooksLikeMultipleCandidateSynthesisRequest(effectiveUserMessage))
        {
            return ExtendedSourceBackedPlanningWriterTimeoutMs;
        }

        return SourceBackedPlanningWriterTimeoutMs;
    }

    private static bool ShouldUseStageWideSourceBackedWriterTimeout(string? effectiveUserMessage)
        => !ShouldGateStructuredSourceBackedPlanningCoverage(effectiveUserMessage)
           && !LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage)
           && !LooksLikeBroadSourceBackedCompositionRequest(effectiveUserMessage)
           && !LooksLikeMultipleCandidateSynthesisRequest(effectiveUserMessage);

    private static int ResolveSourceBackedContextReadPassLimit(string? effectiveUserMessage)
    {
        var targetSlots = ResolveSourceBackedPlanningTargetItemCount(effectiveUserMessage);
        var surplus = Math.Max(4, (int)Math.Ceiling(targetSlots * 0.5d));
        return Math.Clamp(targetSlots + surplus, 12, 32);
    }

    private static int ResolveSourceBackedContextReadPassBatchLimit(string? effectiveUserMessage)
    {
        var targetSlots = ResolveSourceBackedPlanningTargetItemCount(effectiveUserMessage);
        return Math.Clamp((int)Math.Ceiling(targetSlots * 0.55d), 6, 12);
    }

    private static bool LooksLikeSourceBackedBroadResearchRequest(string? effectiveUserMessage)
        => LooksLikeGenericCollectionOrListRequest(effectiveUserMessage)
           || LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage)
           || LooksLikeBroadSourceBackedCompositionRequest(effectiveUserMessage)
           || LooksLikeMultipleCandidateSynthesisRequest(effectiveUserMessage)
           || LooksLikeSoftChoiceRecommendationRequest(effectiveUserMessage)
           || LooksLikeSourceBackedPairingRecommendationRequest(effectiveUserMessage)
           || LooksLikeUserNeedsSynthesizedDecisionOrPlan(effectiveUserMessage);

    private static bool ShouldUseResearchSurfacesForBroadRagRequest(string? effectiveUserMessage)
        => LooksLikeSourceBackedBroadResearchRequest(effectiveUserMessage)
           || IsBroadenedSourceSearchConfirmationEnvelope(effectiveUserMessage);

    private static bool ShouldAllowSourceBackedBroadResearchPass(
        string? effectiveUserMessage,
        SourceBackedEvidenceSufficiency currentAnalysis,
        bool forceBroadenedExploration)
    {
        if (forceBroadenedExploration)
            return true;
        if (!LooksLikeSourceBackedBroadResearchRequest(effectiveUserMessage))
            return false;
        if (currentAnalysis.IsSufficient)
            return false;

        if (UsesSourceBackedPlanningCoverage(effectiveUserMessage)
            || currentAnalysis.Kind == "planning")
        {
            return currentAnalysis.ShouldExplore
                   || currentAnalysis.CandidateCount < currentAnalysis.MinimumCandidateCount;
        }

        return currentAnalysis.ShouldExplore
               || currentAnalysis.CandidateCount < currentAnalysis.MinimumCandidateCount
               || currentAnalysis.DistinctSourcePageCount < Math.Min(3, Math.Max(1, currentAnalysis.MinimumCandidateCount));
    }

    private static string[] BuildSourceBackedNavigationOrientationQueries(string effectiveUserMessage, string? categoryScope)
    {
        var queries = new List<string>();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            value = CollapseWhitespace(value ?? string.Empty);
            if (string.IsNullOrWhiteSpace(value))
                return;

            var key = NormalizeLooseLookup(NormalizeRagQueryForRetrieval(value));
            if (string.IsNullOrWhiteSpace(key) || !emitted.Add(key))
                return;

            queries.Add(value);
        }

        var broadRequest = LooksLikeSourceBackedBroadResearchRequest(effectiveUserMessage);
        var planningRequest = LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage)
                              || UsesSourceBackedPlanningCoverage(effectiveUserMessage);

        if (broadRequest)
        {
            if (planningRequest)
            {
                foreach (var query in BuildPlanningExplorationRetrievalQueries(effectiveUserMessage).Take(3))
                    Add(query);
            }
            else
            {
                foreach (var query in BuildNavigationDiscoveryRetrievalQueries(effectiveUserMessage).Take(4))
                    Add(query);
            }

            if (!string.IsNullOrWhiteSpace(categoryScope))
                Add($"{categoryScope} {NormalizeRagQueryForRetrieval(effectiveUserMessage)}");
        }

        Add(effectiveUserMessage);
        Add(NormalizeRagQueryForRetrieval(effectiveUserMessage));
        Add(BuildRagEvidenceSelectionQuery(effectiveUserMessage));

        if (!broadRequest)
        {
            if (planningRequest)
            {
                foreach (var query in BuildPlanningExplorationRetrievalQueries(effectiveUserMessage).Take(3))
                    Add(query);
            }
            else
            {
                foreach (var query in BuildNavigationDiscoveryRetrievalQueries(effectiveUserMessage).Take(4))
                    Add(query);
            }

            if (!string.IsNullOrWhiteSpace(categoryScope))
                Add($"{categoryScope} {NormalizeRagQueryForRetrieval(effectiveUserMessage)}");
        }

        return queries.Take(10).ToArray();
    }

    private static string[] BuildSourceBackedSummaryOrientationQueries(string effectiveUserMessage, string? categoryScope)
    {
        var queries = new List<string>();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            value = CollapseWhitespace(value ?? string.Empty);
            if (string.IsNullOrWhiteSpace(value))
                return;

            var key = NormalizeLooseLookup(NormalizeRagQueryForRetrieval(value));
            if (string.IsNullOrWhiteSpace(key) || !emitted.Add(key))
                return;

            queries.Add(value);
        }

        var broadRequest = LooksLikeSourceBackedBroadResearchRequest(effectiveUserMessage);
        var planningRequest = LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage)
                              || UsesSourceBackedPlanningCoverage(effectiveUserMessage);

        if (broadRequest)
        {
            if (planningRequest)
            {
                foreach (var query in BuildPlanningExplorationRetrievalQueries(effectiveUserMessage).Take(4))
                    Add(query);
            }
            else
            {
                foreach (var query in BuildNavigationDiscoveryRetrievalQueries(effectiveUserMessage).Take(4))
                    Add(query);
            }

            if (!string.IsNullOrWhiteSpace(categoryScope))
                Add($"{categoryScope} {NormalizeRagQueryForRetrieval(effectiveUserMessage)}");
        }

        Add(effectiveUserMessage);
        Add(NormalizeRagQueryForRetrieval(effectiveUserMessage));
        Add(BuildRagEvidenceSelectionQuery(effectiveUserMessage));

        if (!broadRequest)
        {
            if (planningRequest)
            {
                foreach (var query in BuildPlanningExplorationRetrievalQueries(effectiveUserMessage).Take(4))
                    Add(query);
            }
            else
            {
                foreach (var query in BuildNavigationDiscoveryRetrievalQueries(effectiveUserMessage).Take(4))
                    Add(query);
            }

            if (!string.IsNullOrWhiteSpace(categoryScope))
                Add($"{categoryScope} {NormalizeRagQueryForRetrieval(effectiveUserMessage)}");
        }

        return queries.Take(10).ToArray();
    }
}
