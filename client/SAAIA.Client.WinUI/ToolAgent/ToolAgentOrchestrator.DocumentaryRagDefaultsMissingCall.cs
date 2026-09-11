namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private void AddDefaultDocumentaryRagCall(RouterPlan plan, DocumentaryRagDefaultsContext context)
    {
        if (context.IsDocumentaryPlanning && !context.HasExactItemTitle)
        {
            plan.ToolCalls.Add(new RouterPlan.ToolCall
            {
                Name = "rag.multi_search",
                Args = CreateJsonArgs(new
                {
                    queries = BuildInitialSourceBackedPlanningProbeQueries(context.EffectiveUserMessage),
                    topK = InitialSourceBackedPlanningProbeTopK,
                    maxPerDoc = InitialSourceBackedPlanningProbeMaxPerDoc,
                    maxPerPage = InitialSourceBackedPlanningProbeMaxPerPage,
                    category = context.CategoryScope,
                    mode = "broad",
                    researchMode = "source_exploration",
                    includeResearchSurfaces = true
                })
            });
            return;
        }

        if (context.IsSourceBackedRecommendationRequest && !context.HasExactItemTitle)
        {
            plan.ToolCalls.Add(new RouterPlan.ToolCall
            {
                Name = "rag.multi_search",
                Args = CreateJsonArgs(new
                {
                    queries = BuildSourceBackedActionRetrievalQueries(context.EffectiveUserMessage),
                    topK = NormalizeSourceBackedActionTopK(null, context.EffectiveUserMessage),
                    category = context.CategoryScope,
                    mode = context.UseBroadResearchSurfaces ? "broad" : "balanced",
                    researchMode = context.UseBroadResearchSurfaces ? "source_exploration" : null,
                    includeResearchSurfaces = context.UseBroadResearchSurfaces ? true : (bool?)null
                })
            });
            return;
        }

        var useSingleSearch = ShouldAddDefaultSingleDocumentaryRagSearch(context);
        plan.ToolCalls.Add(new RouterPlan.ToolCall
        {
            Name = useSingleSearch ? "rag.search" : "rag.multi_search",
            Args = useSingleSearch
                ? CreateJsonArgs(new
                {
                    query = context.RetrievalQuery,
                    topK = context.TopK,
                    category = context.CategoryScope,
                    mode = context.UseBroadResearchSurfaces ? "broad" : "balanced",
                    researchMode = context.UseBroadResearchSurfaces ? "source_exploration" : null,
                    includeResearchSurfaces = context.UseBroadResearchSurfaces ? true : (bool?)null
                })
                : CreateJsonArgs(new
                {
                    queries = BuildDefaultDocumentaryMultiSearchQueries(context),
                    topK = ResolveDefaultDocumentaryMultiSearchTopK(context),
                    category = context.CategoryScope,
                    mode = context.UseBroadResearchSurfaces ? "broad" : "balanced",
                    researchMode = context.UseBroadResearchSurfaces ? "source_exploration" : null,
                    includeResearchSurfaces = context.UseBroadResearchSurfaces ? true : (bool?)null
                })
        });
    }

    private static bool ShouldAddDefaultSingleDocumentaryRagSearch(DocumentaryRagDefaultsContext context)
        => context.PreferSingleRagSearch
           || (!context.HasExactItemTitle
               && !context.IsComparativeDocumentaryRequest
               && !context.IsSourceBackedAdaptationRequest
               && !context.IsDocumentaryContentRequest
               && !context.IsBroadDocumentaryInformationRequest
               && !context.IsSourceBackedActionRequest
               && !context.IsDocumentVersionTraceabilityRequest
               && !context.IsSourceBackedRecommendationRequest);

    private string[] BuildDefaultDocumentaryMultiSearchQueries(DocumentaryRagDefaultsContext context)
    {
        if (context.HasExactItemTitle)
            return BuildPreciseRetrievalQueries(context.ExactItemTitle!, context.RetrievalQuery, context.EffectiveUserMessage);

        if (context.IsSourceBackedAdaptationRequest
            || context.IsSourceBackedRecommendationRequest
            || context.IsDocumentaryContentRequest
            || context.IsDocumentVersionTraceabilityRequest
            || context.IsSourceBackedActionRequest)
            return BuildSourceBackedActionRetrievalQueries(context.EffectiveUserMessage);

        if (context.IsBroadDocumentaryInformationRequest)
            return BuildDocumentaryProbeRetrievalQueries(context.EffectiveUserMessage);

        return BuildComparativeRetrievalQueries(context.EffectiveUserMessage);
    }

    private int ResolveDefaultDocumentaryMultiSearchTopK(DocumentaryRagDefaultsContext context)
    {
        if (context.HasExactItemTitle)
            return 20;

        if (context.IsComparativeDocumentaryRequest)
            return NormalizeComparativeTopK(null, context.EffectiveUserMessage);

        if (context.IsSourceBackedActionRequest
            || context.IsSourceBackedAdaptationRequest
            || context.IsSourceBackedRecommendationRequest)
            return NormalizeSourceBackedActionTopK(null, context.EffectiveUserMessage);

        return context.IsBroadDocumentaryInformationRequest ? 12 : 12;
    }
}
