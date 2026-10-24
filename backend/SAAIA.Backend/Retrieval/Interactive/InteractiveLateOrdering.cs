using System.Diagnostics;
using static SAAIA.Backend.Retrieval.RetrievalLexicalExpansion;
using static SAAIA.Backend.Retrieval.RetrievalSelection;
using static SAAIA.Backend.Retrieval.RetrievalTitleSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveLateOrdering
{
    internal static void Apply(InteractiveRetrievalFlow flow)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        flow.Session.Timings.OrderingWatch = Stopwatch.StartNew();
        CleanupLateSelectionBackfills(plan.Request.Query, flow.Selection.Matches);
        PruneUnrelatedReferenceVersionGroupSelections(plan.Request.Query, flow.Selection.Matches);
        PruneCurrentReferenceVersionSelectionsWhenHistoricalVersionRequested(plan.Request.Query, flow.Selection.Matches);
        PruneUnrequestedReferenceVersionYearSelections(plan.Request.Query, flow.Selection.Matches);
        PruneSupersededReferenceVersionSelections(plan.Request.Query, flow.Selection.Matches);
        PrioritizeFinalSelections(plan.Request.Query, flow.Selection.Matches);
        PrioritizeResponseDiversityCoverage(plan.Request.Query, flow.Selection.Matches, plan.Selection.RequestedTopK);
        ReattachStructuredContinuationCompanions(plan.Request.Query, flow.Selection.Matches, plan.Selection.TopK);
        PruneQualifiedQuotedTitleCompetitorDocuments(plan.Request.Query, flow.Selection.Matches);
        PrunePreciseTitleLocalNeighborhoodSelections(plan.Request.Query, flow.Selection.Matches);
        RebuildSelectedKeys(flow.Selection.Matches, flow.Selection.Keys);
        ReattachDocumentStatusOperandCompanions(
            plan.Request.Query,
            plan.Backfills.DocumentStatusOperands,
            flow.Selection.DocumentStatusOperandCandidates,
            flow.Selection.Matches,
            flow.Selection.Keys,
            plan.Selection.TopK);
        RebuildSelectedKeys(flow.Selection.Matches, flow.Selection.Keys);
        flow.Session.Timings.OrderingWatch.Stop();
        flow.Session.Timings.SelectionMs += flow.Session.Timings.OrderingWatch.ElapsedMilliseconds;
    }
}
