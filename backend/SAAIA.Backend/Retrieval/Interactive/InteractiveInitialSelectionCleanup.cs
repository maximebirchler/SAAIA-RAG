using System.Diagnostics;
using static SAAIA.Backend.Retrieval.RetrievalCandidateSignals;
using static SAAIA.Backend.Retrieval.RetrievalSelection;
using static SAAIA.Backend.Retrieval.RetrievalTitleSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveInitialSelectionCleanup
{
    internal static void Apply(InteractiveRetrievalFlow flow)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        PrioritizeExactTitleSelections(plan.Query.RankingQuery, flow.Selection.Matches);
        PrioritizeQuotedTitleSelections(plan.Request.Query, flow.Selection.Matches);
        PrioritizeDocumentHintSelections(plan.Request.Query, flow.Selection.Matches);
        PrioritizeOperationalSettingSelections(plan.Request.Query, flow.Selection.Matches);
        if (!plan.Routes.UseScopedProfileFallback)
        {
            var selectionSw = Stopwatch.StartNew();
            PruneWeakTitleExpansionSelections(plan.Query.RankingQuery, flow.Selection.Matches);
            PruneWeakAdjacentSiblingSelections(plan.Query.RankingQuery, flow.Selection.Matches);
            PrunePreciseTitleTailSelections(plan.Query.RankingQuery, flow.Selection.Matches);
            PruneUnmatchedPreciseTitleSelections(plan.Query.RankingQuery, flow.Selection.Matches);
            PruneUnpagedProfileSelectionsForPreciseLookup(plan.Query.RankingQuery, flow.Selection.Matches);
            selectionSw.Stop();
            flow.Session.Timings.SelectionMs += selectionSw.ElapsedMilliseconds;
        }
        var finalSelectionSw = Stopwatch.StartNew();
        PruneNavigationalSelections(plan.Request.Query, flow.Selection.Matches);
        PromoteActionableSameDocumentEvidence(flow.Selection.Matches);
        if (!plan.Routes.SkipChunkRetrieversForDocumentOverview)
            ApplyAutocut(flow.Selection.Matches, plan.Selection.SelectionMinScore, plan.Request.Query);
        RebuildSelectedKeys(flow.Selection.Matches, flow.Selection.Keys);
        finalSelectionSw.Stop();
        flow.Session.Timings.SelectionMs += finalSelectionSw.ElapsedMilliseconds;
    }
}
