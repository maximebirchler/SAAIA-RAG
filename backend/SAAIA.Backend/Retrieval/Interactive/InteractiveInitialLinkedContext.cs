using static SAAIA.Backend.Retrieval.RetrievalSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveInitialLinkedContext
{
    internal static async Task ExecuteAsync(InteractiveRetrievalFlow flow)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        var forcePreciseTitleLinkedBackfill = !plan.Routes.UseScopedProfileFallback
                    && ShouldRunPreciseTitleLinkedBackfill(plan.Request.Query, flow.Selection.Matches);
        if (!plan.Routes.UseScopedProfileFallback && (flow.Selection.Matches.Count < plan.Selection.TopK || forcePreciseTitleLinkedBackfill))
        {
            var linkedLimit = flow.Selection.Matches.Count < plan.Selection.TopK
                ? plan.Selection.TopK - flow.Selection.Matches.Count
                : Math.Min(plan.Selection.TopK, 6);
            var (linkedMatches, linkedDurationMs) = await flow.Session.Phases.MeasureAsync(
                phaseName: "retrieval_linked_context",
                retriever: "linked_context",
                action: () => SearchLinkedMatchesAsync(
                    resources.DataSource,
                    resources.Context.TenantId,
                    flow.Selection.Matches,
                    plan.Scope.Category,
                    plan.Request.DocId,
                    plan.Request.DocPath,
                    linkedLimit,
                    resources.Context.CancellationToken,
                    plan.Scope.CategoryPath),
                getReturnedCount: static matches => matches.Count);
            flow.Session.Timings.LinkedPhaseMs += linkedDurationMs;

            if (forcePreciseTitleLinkedBackfill && flow.Selection.Matches.Count >= plan.Selection.TopK && linkedMatches.Count > 0)
            {
                var mergedLinkedSelections = CalibrateFusedMatches(
                    plan.Query.RetrievalQuery,
                    flow.Selection.Matches.Concat(linkedMatches).ToList(),
                    plan.Request.Query);
                mergedLinkedSelections = SuppressNavigationalNoise(plan.Request.Query, mergedLinkedSelections);
                flow.Selection.Matches.Clear();
                flow.Selection.Keys.Clear();
                AddRankedMatchesDiagnosed(
                flow.Session.Diagnostics, flow.Selection.Matches,
                                        flow.Selection.Keys,
                                        mergedLinkedSelections,
                                        plan.Selection.TopK,
                                        minScore: 0.0,
                                        Math.Max(plan.Selection.MaxPerDoc, 2),
                                        Math.Max(plan.Selection.MaxPerPage, 2),
                                        query: plan.Request.Query);
            }
            else
            {
                AddRankedMatchesDiagnosed(flow.Session.Diagnostics, flow.Selection.Matches, flow.Selection.Keys, linkedMatches, plan.Selection.TopK, minScore: 0.0, Math.Max(plan.Selection.MaxPerDoc, 2), Math.Max(plan.Selection.MaxPerPage, 2));
            }
        }

        if (!plan.Routes.UseScopedProfileFallback && flow.Selection.Matches.Count < plan.Selection.TopK)
        {
            var (secondWaveLinkedMatches, secondWaveLinkedMs) = await flow.Session.Phases.MeasureAsync(
                phaseName: "retrieval_linked_context",
                retriever: "linked_context",
                action: () => SearchLinkedMatchesAsync(
                    resources.DataSource,
                    resources.Context.TenantId,
                    flow.Selection.Matches,
                    plan.Scope.Category,
                    plan.Request.DocId,
                    plan.Request.DocPath,
                    plan.Selection.TopK - flow.Selection.Matches.Count,
                    resources.Context.CancellationToken,
                    plan.Scope.CategoryPath),
                getReturnedCount: static matches => matches.Count);
            flow.Session.Timings.LinkedPhaseMs += secondWaveLinkedMs;
            AddRankedMatchesDiagnosed(flow.Session.Diagnostics, flow.Selection.Matches, flow.Selection.Keys, secondWaveLinkedMatches, plan.Selection.TopK, minScore: 0.0, Math.Max(plan.Selection.MaxPerDoc, 2), Math.Max(plan.Selection.MaxPerPage, 2));
        }
    }
}
