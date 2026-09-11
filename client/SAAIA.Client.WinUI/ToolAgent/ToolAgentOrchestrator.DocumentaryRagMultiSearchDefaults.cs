using System;
using System.Linq;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private void ApplyDocumentaryRagMultiSearchDefaults(
        RouterPlan.ToolCall call,
        DocumentaryRagDefaultsContext context,
        string? trustedCategoryScope)
    {
        var queries = TryGetStringArrayArg(call.Args, "queries");
        if (context.PreferSingleRagSearch && !context.HasExactItemTitle)
        {
            call.Name = "rag.search";
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
            call.Args = CreateJsonArgs(new
            {
                queries = BuildInitialSourceBackedPlanningProbeQueries(context.EffectiveUserMessage, queries),
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

        if (context.HasExactItemTitle
            && !queries.Any(q => string.Equals(q, context.ExactItemTitle, StringComparison.OrdinalIgnoreCase)))
        {
            queries.Insert(0, context.ExactItemTitle!);
        }

        if (context.IsComparativeDocumentaryRequest && !context.HasExactItemTitle)
        {
            var comparativeQueries = BuildComparativeRetrievalQueries(context.EffectiveUserMessage).ToList();
            foreach (var query in queries.AsEnumerable().Reverse())
            {
                var supplementalQuery = NormalizeComparativeSupplementalRetrievalQuery(query, context.EffectiveUserMessage);
                if (!comparativeQueries.Any(q => string.Equals(q, supplementalQuery, StringComparison.OrdinalIgnoreCase)))
                    comparativeQueries.Insert(0, supplementalQuery);
            }

            call.Args = CreateJsonArgs(new
            {
                queries = comparativeQueries.Take(ResolveComparativeRetrievalQueryLimit(context.EffectiveUserMessage)).ToArray(),
                topK = NormalizeComparativeTopK(TryGetIntArg(call.Args, "topK"), context.EffectiveUserMessage),
                category = trustedCategoryScope,
                mode = "balanced"
            });
            return;
        }

        if (context.IsDocumentVersionTraceabilityRequest && !context.HasExactItemTitle)
        {
            ApplyActionListDocumentaryMultiSearchDefaults(call, context, trustedCategoryScope, queries, queryLimit: 12, actionTopK: false, broadMode: false);
            return;
        }

        if (context.IsBroadDocumentaryInformationRequest && !context.HasExactItemTitle)
        {
            var broadQueries = BuildDocumentaryProbeRetrievalQueries(context.EffectiveUserMessage).ToList();
            foreach (var query in queries.AsEnumerable().Reverse())
            {
                if (!IsLowValueRouterRagQuery(query)
                    && !broadQueries.Any(q => string.Equals(q, query, StringComparison.OrdinalIgnoreCase)))
                    broadQueries.Insert(0, query);
            }

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

        if (context.IsSourceBackedRecommendationRequest && !context.HasExactItemTitle)
        {
            ApplyActionListDocumentaryMultiSearchDefaults(
                call,
                context,
                trustedCategoryScope,
                queries,
                queryLimit: ResolveSourceBackedActionRetrievalQueryLimit(context.EffectiveUserMessage),
                actionTopK: true,
                broadMode: context.UseBroadResearchSurfaces);
            return;
        }

        if ((context.IsSourceBackedAdaptationRequest || context.IsSourceBackedActionRequest) && !context.HasExactItemTitle)
        {
            ApplyActionListDocumentaryMultiSearchDefaults(
                call,
                context,
                trustedCategoryScope,
                queries,
                queryLimit: ResolveSourceBackedActionRetrievalQueryLimit(context.EffectiveUserMessage),
                actionTopK: true,
                broadMode: context.UseBroadResearchSurfaces);
            return;
        }

        if (context.IsDocumentaryContentRequest && !context.HasExactItemTitle)
        {
            ApplyActionListDocumentaryMultiSearchDefaults(call, context, trustedCategoryScope, queries, queryLimit: 12, actionTopK: false, broadMode: context.UseBroadResearchSurfaces);
            return;
        }

        if (queries.Count == 0)
            queries.Add(context.RetrievalQuery);
        else if (!context.IsDocumentaryPlanning
            && !context.HasExactItemTitle
            && !queries.Any(q => string.Equals(q, context.RetrievalQuery, StringComparison.OrdinalIgnoreCase)))
        {
            queries.Insert(0, context.RetrievalQuery);
        }

        var normalizedTopK = NormalizeIntArg(TryGetIntArg(call.Args, "topK"), 8, 1, 20);
        if (context.HasExactItemTitle)
        {
            normalizedTopK = LooksLikeStructuredItemCardRequest(context.EffectiveUserMessage)
                ? 20
                : Math.Max(12, normalizedTopK);
        }

        call.Args = CreateJsonArgs(new
        {
            queries = queries.Take(context.HasExactItemTitle ? 8 : 5).ToArray(),
            topK = normalizedTopK,
            category = trustedCategoryScope,
            mode = context.UseBroadResearchSurfaces ? "broad" : NormalizeRagMode(TryGetStringArg(call.Args, "mode")),
            researchMode = context.UseBroadResearchSurfaces ? "source_exploration" : null,
            includeResearchSurfaces = context.UseBroadResearchSurfaces ? true : (bool?)null
        });
    }

    private void ApplyActionListDocumentaryMultiSearchDefaults(
        RouterPlan.ToolCall call,
        DocumentaryRagDefaultsContext context,
        string? trustedCategoryScope,
        System.Collections.Generic.List<string> existingQueries,
        int queryLimit,
        bool actionTopK,
        bool broadMode)
    {
        var actionQueries = BuildSourceBackedActionRetrievalQueries(context.EffectiveUserMessage).ToList();
        foreach (var query in existingQueries.AsEnumerable().Reverse())
        {
            if (!IsLowValueRouterRagQuery(query)
                && !actionQueries.Any(q => string.Equals(q, query, StringComparison.OrdinalIgnoreCase)))
                actionQueries.Insert(0, query);
        }

        call.Args = CreateJsonArgs(new
        {
            queries = actionQueries.Take(queryLimit).ToArray(),
            topK = actionTopK
                ? NormalizeSourceBackedActionTopK(TryGetIntArg(call.Args, "topK"), context.EffectiveUserMessage)
                : Math.Max(12, NormalizeIntArg(TryGetIntArg(call.Args, "topK"), 12, 1, 20)),
            category = trustedCategoryScope,
            mode = broadMode ? "broad" : NormalizeRagMode(TryGetStringArg(call.Args, "mode")),
            researchMode = broadMode ? "source_exploration" : null,
            includeResearchSurfaces = broadMode ? true : (bool?)null
        });
    }
}
