using System;
using System.Linq;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private void ApplyDocumentaryRagSearchDefaults(
        RouterPlan.ToolCall call,
        DocumentaryRagDefaultsContext context,
        string? trustedCategoryScope)
    {
        if (context.PreferSingleRagSearch && !context.HasExactItemTitle)
        {
            call.Args = CreateJsonArgs(new
            {
                query = context.RetrievalQuery,
                topK = NormalizeIntArg(TryGetIntArg(call.Args, "topK"), 8, 1, 12),
                category = trustedCategoryScope,
                mode = NormalizeRagMode(TryGetStringArg(call.Args, "mode"))
            });
            return;
        }

        if (context.IsDocumentaryPlanning && !context.HasExactItemTitle)
        {
            call.Name = "rag.multi_search";
            call.Args = CreateJsonArgs(new
            {
                queries = BuildInitialSourceBackedPlanningProbeQueries(
                    context.EffectiveUserMessage,
                    new[] { TryGetStringArg(call.Args, "query") ?? string.Empty }),
                topK = InitialSourceBackedPlanningProbeTopK,
                maxPerDoc = InitialSourceBackedPlanningProbeMaxPerDoc,
                maxPerPage = InitialSourceBackedPlanningProbeMaxPerPage,
                category = trustedCategoryScope,
                mode = "broad",
                researchMode = "source_exploration",
                includeResearchSurfaces = true
            });
            return;
        }

        if (context.HasExactItemTitle)
        {
            call.Name = "rag.multi_search";
            call.Args = CreateJsonArgs(new
            {
                queries = BuildPreciseRetrievalQueries(context.ExactItemTitle!, context.RetrievalQuery, context.EffectiveUserMessage),
                topK = 20,
                category = trustedCategoryScope,
                mode = "balanced"
            });
            return;
        }

        if (context.IsComparativeDocumentaryRequest)
        {
            var comparativeQueries = BuildComparativeRetrievalQueries(context.EffectiveUserMessage).ToList();
            var existingQuery = NormalizeRagQueryForRetrieval(TryGetStringArg(call.Args, "query"));
            existingQuery = NormalizeComparativeSupplementalRetrievalQuery(existingQuery, context.EffectiveUserMessage);
            if (!IsLowValueRouterRagQuery(existingQuery)
                && !comparativeQueries.Any(q => string.Equals(q, existingQuery, StringComparison.OrdinalIgnoreCase)))
            {
                comparativeQueries.Add(existingQuery);
            }

            call.Name = "rag.multi_search";
            call.Args = CreateJsonArgs(new
            {
                queries = comparativeQueries.Take(ResolveComparativeRetrievalQueryLimit(context.EffectiveUserMessage)).ToArray(),
                topK = NormalizeComparativeTopK(TryGetIntArg(call.Args, "topK"), context.EffectiveUserMessage),
                category = trustedCategoryScope,
                mode = "balanced"
            });
            return;
        }

        if (context.IsDocumentVersionTraceabilityRequest)
        {
            var actionQueries = BuildSourceBackedActionRetrievalQueries(context.EffectiveUserMessage).ToList();
            var existingQuery = NormalizeRagQueryForRetrieval(TryGetStringArg(call.Args, "query"));
            if (!IsLowValueRouterRagQuery(existingQuery)
                && !actionQueries.Any(q => string.Equals(q, existingQuery, StringComparison.OrdinalIgnoreCase)))
            {
                actionQueries.Insert(0, existingQuery);
            }

            call.Name = "rag.multi_search";
            call.Args = CreateJsonArgs(new
            {
                queries = actionQueries.Take(12).ToArray(),
                topK = Math.Max(12, NormalizeIntArg(TryGetIntArg(call.Args, "topK"), 12, 1, 20)),
                category = trustedCategoryScope,
                mode = "balanced"
            });
            return;
        }

        if (context.IsBroadDocumentaryInformationRequest)
        {
            var broadQueries = BuildDocumentaryProbeRetrievalQueries(context.EffectiveUserMessage).ToList();
            var existingQuery = NormalizeRagQueryForRetrieval(TryGetStringArg(call.Args, "query"));
            if (!IsLowValueRouterRagQuery(existingQuery)
                && !broadQueries.Any(q => string.Equals(q, existingQuery, StringComparison.OrdinalIgnoreCase)))
            {
                broadQueries.Add(existingQuery);
            }

            call.Name = "rag.multi_search";
            call.Args = CreateJsonArgs(new
            {
                queries = broadQueries.Take(16).ToArray(),
                topK = Math.Max(12, NormalizeIntArg(TryGetIntArg(call.Args, "topK"), 12, 1, 20)),
                category = trustedCategoryScope,
                mode = "broad",
                researchMode = "source_exploration",
                includeResearchSurfaces = true
            });
            return;
        }

        if (context.IsSourceBackedRecommendationRequest)
        {
            ApplyActionLikeDocumentaryRagSearchDefaults(call, context, trustedCategoryScope, useBroadMode: true);
            return;
        }

        if (context.IsSourceBackedAdaptationRequest || context.IsSourceBackedActionRequest)
        {
            ApplyActionLikeDocumentaryRagSearchDefaults(call, context, trustedCategoryScope, useBroadMode: false);
            return;
        }

        if (context.IsDocumentaryContentRequest)
        {
            var actionQueries = BuildSourceBackedActionRetrievalQueries(context.EffectiveUserMessage).ToList();
            var existingQuery = NormalizeRagQueryForRetrieval(TryGetStringArg(call.Args, "query"));
            if (!IsLowValueRouterRagQuery(existingQuery)
                && !actionQueries.Any(q => string.Equals(q, existingQuery, StringComparison.OrdinalIgnoreCase)))
            {
                actionQueries.Insert(0, existingQuery);
            }

            call.Name = "rag.multi_search";
            call.Args = CreateJsonArgs(new
            {
                queries = actionQueries.Take(12).ToArray(),
                topK = Math.Max(12, NormalizeIntArg(TryGetIntArg(call.Args, "topK"), 12, 1, 20)),
                category = trustedCategoryScope,
                mode = "balanced"
            });
            return;
        }

        call.Args = CreateJsonArgs(new
        {
            query = context.RetrievalQuery,
            topK = context.HasExactItemTitle
                ? 20
                : NormalizeIntArg(TryGetIntArg(call.Args, "topK"), 8, 1, 20),
            category = trustedCategoryScope,
            mode = NormalizeRagMode(TryGetStringArg(call.Args, "mode"))
        });
    }

    private void ApplyActionLikeDocumentaryRagSearchDefaults(
        RouterPlan.ToolCall call,
        DocumentaryRagDefaultsContext context,
        string? trustedCategoryScope,
        bool useBroadMode)
    {
        var actionQueries = BuildSourceBackedActionRetrievalQueries(context.EffectiveUserMessage).ToList();
        var existingQuery = NormalizeRagQueryForRetrieval(TryGetStringArg(call.Args, "query"));
        if (!IsLowValueRouterRagQuery(existingQuery)
            && !actionQueries.Any(q => string.Equals(q, existingQuery, StringComparison.OrdinalIgnoreCase)))
        {
            actionQueries.Insert(0, existingQuery);
        }

        call.Name = "rag.multi_search";
        call.Args = CreateJsonArgs(new
        {
            queries = actionQueries.Take(ResolveSourceBackedActionRetrievalQueryLimit(context.EffectiveUserMessage)).ToArray(),
            topK = NormalizeSourceBackedActionTopK(TryGetIntArg(call.Args, "topK"), context.EffectiveUserMessage),
            category = trustedCategoryScope,
            mode = useBroadMode && context.UseBroadResearchSurfaces ? "broad" : "balanced",
            researchMode = useBroadMode && context.UseBroadResearchSurfaces ? "source_exploration" : null,
            includeResearchSurfaces = useBroadMode && context.UseBroadResearchSurfaces ? true : (bool?)null
        });
    }
}
