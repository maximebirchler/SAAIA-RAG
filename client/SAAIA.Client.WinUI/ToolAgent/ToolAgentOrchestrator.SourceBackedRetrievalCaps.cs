using System;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static int NormalizeSourceBackedActionTopK(int? requestedTopK, string effectiveUserMessage)
    {
        var topK = NormalizeIntArg(requestedTopK, 8, 1, 20);
        if (LooksLikeSourceBackedVerificationChecklistRequest(effectiveUserMessage)
            || LooksLikeCorpusClaimVerificationRequest(effectiveUserMessage))
        {
            return Math.Min(topK, 6);
        }

        if (LooksLikeSourceBackedPairingRecommendationRequest(effectiveUserMessage))
            return Math.Max(12, topK);

        if (LooksLikeMultipleCandidateSynthesisRequest(effectiveUserMessage)
            || LooksLikeBroadSourceBackedCompositionRequest(effectiveUserMessage))
        {
            return Math.Max(10, topK);
        }

        return LooksLikeSoftChoiceRecommendationRequest(effectiveUserMessage) ? 8 : topK;
    }

    private static int NormalizeSourceBackedPlanningTopK(int? requestedTopK, string effectiveUserMessage)
    {
        var targetSlots = ResolveSourceBackedPlanningTargetItemCount(effectiveUserMessage);
        var needsStructuredCoverage = ShouldGateStructuredSourceBackedPlanningCoverage(effectiveUserMessage);
        var maxTopK = needsStructuredCoverage ? 48 : 24;
        var defaultTopK = targetSlots >= 10
            ? Math.Min(maxTopK, Math.Max(24, targetSlots + 9))
            : 10;
        var topK = NormalizeIntArg(requestedTopK, defaultTopK, 4, maxTopK);
        return targetSlots >= 10 ? Math.Min(maxTopK, Math.Max(defaultTopK, topK)) : Math.Max(8, topK);
    }

    private static (int TopK, int MaxPerDoc, int MaxPerPage) ResolveSourceBackedPlanningInventoryCaps(int? requestedTopK, string effectiveUserMessage)
    {
        var topK = NormalizeSourceBackedPlanningTopK(requestedTopK, effectiveUserMessage);
        var targetSlots = ResolveSourceBackedPlanningTargetItemCount(effectiveUserMessage);
        var maxPerPage = Math.Min(topK, Math.Clamp(targetSlots, 8, 24));
        return (topK, topK, maxPerPage);
    }

    private static int NormalizeComparativeTopK(int? requestedTopK, string effectiveUserMessage)
    {
        if (CountExplicitDocumentFileReferences(effectiveUserMessage) > 1)
            return NormalizeIntArg(requestedTopK, 6, 1, 12);

        return Math.Max(12, NormalizeIntArg(requestedTopK, 12, 1, 20));
    }

    private static bool ShouldPreferSingleRagSearchForDocumentaryRequest(string effectiveUserMessage)
        => LooksLikeSourceBackedVerificationChecklistRequest(effectiveUserMessage)
            || LooksLikeCorpusClaimVerificationRequest(effectiveUserMessage);
}