using System.Diagnostics;
using static SAAIA.Backend.Retrieval.RetrievalCandidateSignals;
using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveFuzzyTitleLead
{
    internal static async Task ExecuteAsync(InteractiveRetrievalFlow flow)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        if (!plan.Routes.SkipChunkRetrieversForDocumentOverview
                    && !plan.Scope.HasExplicitFileDocumentHint
                    && ShouldBackfillFuzzyTitleLead(plan.Request.Query, flow.Selection.Matches))
        {
            var focusedFuzzyQuery = BuildFocusedLexicalBackfillQuery(plan.Request.Query);
            if (!string.IsNullOrWhiteSpace(focusedFuzzyQuery))
            {
                var (fuzzyTitleLeadMatches, fuzzyTitleLeadMs) = await flow.Session.Phases.MeasureAsync(
                    phaseName: "retrieval_fuzzy_title_lead",
                    retriever: "fuzzy_title_lead",
                    action: () => SearchFuzzyTitleLeadMatchesAsync(
                        resources.DataSource,
                        resources.Context.TenantId,
                        focusedFuzzyQuery,
                        plan.Scope.Category,
                        plan.Request.DocId,
                        plan.Request.DocPath,
                        Math.Max(plan.Selection.TopK, 8),
                        resources.Context.CancellationToken,
                        plan.Scope.CategoryPath,
                        degradedRetrieverRef: flow.Session.Degradation.Mark),
                    getReturnedCount: static matches => matches.Count);
                flow.Session.Timings.SparsePhaseMs += fuzzyTitleLeadMs;

                var calibratedFuzzyMatches = CalibrateFusedMatches(focusedFuzzyQuery, fuzzyTitleLeadMatches, plan.Request.Query);
                calibratedFuzzyMatches = SuppressNavigationalNoise(plan.Request.Query, calibratedFuzzyMatches);
                var selectionSw = Stopwatch.StartNew();
                AddRankedMatchesDiagnosed(
                flow.Session.Diagnostics, flow.Selection.Matches,
                                    flow.Selection.Keys,
                                    calibratedFuzzyMatches,
                                    plan.Selection.TopK,
                                    minScore: 0.0,
                                    plan.Selection.MaxPerDoc,
                                    Math.Max(plan.Selection.MaxPerPage, 2));
                PruneNavigationalSelections(plan.Request.Query, flow.Selection.Matches);
                PromoteActionableSameDocumentEvidence(flow.Selection.Matches);
                if (!plan.Routes.SkipChunkRetrieversForDocumentOverview)
                    ApplyAutocut(flow.Selection.Matches, plan.Selection.SelectionMinScore, plan.Request.Query);
                RebuildSelectedKeys(flow.Selection.Matches, flow.Selection.Keys);
                selectionSw.Stop();
                flow.Session.Timings.SelectionMs += selectionSw.ElapsedMilliseconds;
            }
        }
    }
}
