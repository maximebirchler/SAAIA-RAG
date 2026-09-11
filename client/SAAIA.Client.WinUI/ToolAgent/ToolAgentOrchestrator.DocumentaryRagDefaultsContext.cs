namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private sealed record DocumentaryRagDefaultsContext(
        string EffectiveUserMessage,
        string? ExactItemTitle,
        bool IsSourceBackedActionRequest,
        bool IsComparativeDocumentaryRequest,
        bool IsSourceBackedAdaptationRequest,
        bool IsDocumentaryContentRequest,
        bool IsBroadDocumentaryInformationRequest,
        bool IsDocumentVersionTraceabilityRequest,
        bool IsDocumentaryPlanning,
        bool IsSourceBackedRecommendationRequest,
        bool PreferSingleRagSearch,
        bool UseBroadResearchSurfaces,
        string RetrievalQuery,
        int TopK,
        string? CategoryScope)
    {
        public bool HasExactItemTitle => !string.IsNullOrWhiteSpace(ExactItemTitle);
    }

    private DocumentaryRagDefaultsContext? BuildDocumentaryRagDefaultsContext(string effectiveUserMessage)
    {
        var exactItemTitle = TryExtractRequestedItemTitle(effectiveUserMessage);
        var isSourceBackedActionRequest = LooksLikeSourceBackedActionRequest(effectiveUserMessage);
        var isComparativeDocumentaryRequest = LooksLikeComparativeDocumentaryRequest(effectiveUserMessage);
        var isSourceBackedAdaptationRequest = LooksLikeSourceBackedAdaptationRequest(effectiveUserMessage);
        var isDocumentaryContentRequest = LooksLikeDocumentaryContentRequest(effectiveUserMessage);
        var isBroadDocumentaryInformationRequest = LooksLikeBroadDocumentaryInformationRequest(effectiveUserMessage);
        var isDocumentVersionTraceabilityRequest = LooksLikeDocumentVersionTraceabilityRequest(effectiveUserMessage);
        var isDocumentaryPlanning = LooksLikeAnyDocumentaryPlanningRequest(effectiveUserMessage);
        if (isDocumentaryPlanning && ShouldGateStructuredSourceBackedPlanningCoverage(effectiveUserMessage))
            exactItemTitle = null;

        var isSourceBackedRecommendationRequest =
            LooksLikeSoftChoiceRecommendationRequest(effectiveUserMessage)
            || LooksLikeSourceBackedPairingRecommendationRequest(effectiveUserMessage)
            || LooksLikeMultipleCandidateSynthesisRequest(effectiveUserMessage)
            || LooksLikeBroadSourceBackedCompositionRequest(effectiveUserMessage);
        var preferSingleRagSearch = ShouldPreferSingleRagSearchForDocumentaryRequest(effectiveUserMessage);
        if (isComparativeDocumentaryRequest && CountExplicitDocumentFileReferences(effectiveUserMessage) > 1)
            exactItemTitle = null;

        var useBroadResearchSurfaces = string.IsNullOrWhiteSpace(exactItemTitle)
            && ShouldUseResearchSurfacesForBroadRagRequest(effectiveUserMessage);
        if (string.IsNullOrWhiteSpace(exactItemTitle)
            && !isSourceBackedActionRequest
            && !isComparativeDocumentaryRequest
            && !isSourceBackedAdaptationRequest
            && !isDocumentaryContentRequest
            && !isBroadDocumentaryInformationRequest
            && !isDocumentVersionTraceabilityRequest
            && !isDocumentaryPlanning
            && !isSourceBackedRecommendationRequest)
            return null;

        var normalizedOriginalQuery = NormalizeRagQueryForRetrieval(effectiveUserMessage);
        var retrievalQuery = !string.IsNullOrWhiteSpace(exactItemTitle)
            ? CollapseWhitespace($"{exactItemTitle} {normalizedOriginalQuery}")
            : normalizedOriginalQuery;
        if (string.IsNullOrWhiteSpace(retrievalQuery))
            retrievalQuery = effectiveUserMessage;

        return new DocumentaryRagDefaultsContext(
            effectiveUserMessage,
            exactItemTitle,
            isSourceBackedActionRequest,
            isComparativeDocumentaryRequest,
            isSourceBackedAdaptationRequest,
            isDocumentaryContentRequest,
            isBroadDocumentaryInformationRequest,
            isDocumentVersionTraceabilityRequest,
            isDocumentaryPlanning,
            isSourceBackedRecommendationRequest,
            preferSingleRagSearch,
            useBroadResearchSurfaces,
            retrievalQuery,
            string.IsNullOrWhiteSpace(exactItemTitle) ? 8 : 20,
            ResolveRagCategoryScope(effectiveUserMessage));
    }
}
