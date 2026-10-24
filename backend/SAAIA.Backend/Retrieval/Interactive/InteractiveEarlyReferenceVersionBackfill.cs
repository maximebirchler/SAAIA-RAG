using System.Diagnostics;
using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalQueryPolicy;
using static SAAIA.Backend.Retrieval.RetrievalSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveEarlyReferenceVersionBackfill
{
    internal static async Task ExecuteAsync(InteractiveRetrievalFlow flow)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        if (flow.Selection.Matches.Count > 0 && ShouldUseReferenceVersionOperandRoute(plan.Request.Query))
        {
            var referenceVersionGroupKeys = InferReferenceVersionOperandGroupKeys(plan.Request.Query, flow.Selection.Matches);
            if (referenceVersionGroupKeys.Count > 0)
            {
                var (referenceVersionOperandBackfillMatches, referenceVersionOperandBackfillMs) = await flow.Session.Phases.MeasureAsync(
                    phaseName: "retrieval_reference_version_operand_backfill",
                    retriever: "reference_version_operand_document_name",
                    action: () => SearchReferenceVersionOperandDocumentNameContentMatchesAsync(
                        resources.DataSource,
                        resources.Context.TenantId,
                        plan.Request.Query,
                        referenceVersionGroupKeys,
                        plan.Scope.Category,
                        plan.Request.DocId,
                        plan.Request.DocPath,
                        Math.Min(plan.Selection.Candidates, Math.Max(plan.Selection.TopK, 16)),
                        resources.Context.CancellationToken,
                        plan.Scope.CategoryPath,
                        degradedRetrieverRef: flow.Session.Degradation.Mark),
                    getReturnedCount: static matches => matches.Count);
                flow.Session.Timings.TitleAnchorRoutePhaseMs += referenceVersionOperandBackfillMs;

                if (referenceVersionOperandBackfillMatches.Count > 0)
                {
                    var referenceVersionSelectionSw = Stopwatch.StartNew();
                    var calibratedReferenceVersionBackfill = SuppressNavigationalNoise(
                        plan.Request.Query,
                        CalibrateFusedMatches(plan.Request.Query, referenceVersionOperandBackfillMatches, plan.Request.Query));
                    RebuildSelectedKeys(flow.Selection.Matches, flow.Selection.Keys);
                    if (flow.Selection.Matches.Count < plan.Selection.TopK)
                    {
                        AddRankedMatchesDiagnosed(
                        flow.Session.Diagnostics, flow.Selection.Matches,
                                                    flow.Selection.Keys,
                                                    calibratedReferenceVersionBackfill,
                                                    plan.Selection.TopK,
                                                    minScore: 0.0,
                                                    maxPerDoc: Math.Max(plan.Selection.MaxPerDoc, 2),
                                                    maxPerPage: Math.Max(plan.Selection.MaxPerPage, 2),
                                                    query: plan.Request.Query);
                    }

                    EnsureComparativeBackfillDocumentDiversity(
                        flow.Selection.Matches,
                        flow.Selection.Keys,
                        calibratedReferenceVersionBackfill,
                        plan.Selection.TopK,
                        minScore: 0.0,
                        maxPerPage: Math.Max(plan.Selection.MaxPerPage, 2),
                        query: plan.Request.Query);
                    CleanupLateSelectionBackfills(plan.Request.Query, flow.Selection.Matches);
                    RebuildSelectedKeys(flow.Selection.Matches, flow.Selection.Keys);
                    referenceVersionSelectionSw.Stop();
                    flow.Session.Timings.SelectionMs += referenceVersionSelectionSw.ElapsedMilliseconds;
                }
            }
        }
    }
}
