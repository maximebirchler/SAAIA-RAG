using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalQueryPolicy;
using static SAAIA.Backend.Retrieval.RetrievalSelection;
using static SAAIA.Backend.Retrieval.RetrievalTitleSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveLateReferenceVersionRecovery
{
    internal static async Task ExecuteAsync(InteractiveRetrievalFlow flow)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        if (ShouldUseReferenceVersionOperandRoute(plan.Request.Query)
                    && !SelectionsCoverReferenceVersionOperandContent(plan.Request.Query, flow.Selection.Matches))
        {
            var lateReferenceVersionGroupKeys = ExtractReferenceVersionGroupKeysFromQuery(plan.Request.Query).ToList();
            if (lateReferenceVersionGroupKeys.Count == 0)
                lateReferenceVersionGroupKeys.AddRange(InferReferenceVersionOperandGroupKeys(plan.Request.Query, flow.Selection.Matches));
            lateReferenceVersionGroupKeys = lateReferenceVersionGroupKeys
                .Where(static key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.Ordinal)
                .Take(6)
                .ToList();

            if (lateReferenceVersionGroupKeys.Count > 0)
            {
                var (lateReferenceVersionOperandMatches, lateReferenceVersionOperandMs) = await flow.Session.Phases.MeasureAsync(
                    phaseName: "retrieval_reference_version_operand_late_recovery",
                    retriever: "reference_version_operand_document_name",
                    action: () => SearchReferenceVersionOperandDocumentNameContentMatchesAsync(
                        resources.DataSource,
                        resources.Context.TenantId,
                        plan.Request.Query,
                        lateReferenceVersionGroupKeys,
                        plan.Scope.Category,
                        plan.Request.DocId,
                        plan.Request.DocPath,
                        Math.Min(plan.Selection.Candidates, Math.Max(plan.Selection.TopK, 16)),
                        resources.Context.CancellationToken,
                        plan.Scope.CategoryPath,
                        degradedRetrieverRef: flow.Session.Degradation.Mark),
                    getReturnedCount: static matches => matches.Count);
                flow.Session.Timings.TitleAnchorRoutePhaseMs += lateReferenceVersionOperandMs;

                if (lateReferenceVersionOperandMatches.Count > 0)
                {
                    flow.Session.Timings.OrderingWatch.Restart();
                    RebuildSelectedKeys(flow.Selection.Matches, flow.Selection.Keys);
                    var calibratedLateReferenceVersionOperand = SuppressNavigationalNoise(
                        plan.Request.Query,
                        CalibrateFusedMatches(plan.Request.Query, lateReferenceVersionOperandMatches, plan.Request.Query));
                    AddRankedMatchesDiagnosed(
                    flow.Session.Diagnostics, flow.Selection.Matches,
                                            flow.Selection.Keys,
                                            calibratedLateReferenceVersionOperand,
                                            plan.Selection.TopK,
                                            minScore: 0.0,
                                            maxPerDoc: Math.Max(plan.Selection.MaxPerDoc, 2),
                                            maxPerPage: Math.Max(plan.Selection.MaxPerPage, 2),
                                            query: plan.Request.Query);
                    EnsureComparativeBackfillDocumentDiversity(
                        flow.Selection.Matches,
                        flow.Selection.Keys,
                        calibratedLateReferenceVersionOperand,
                        plan.Selection.TopK,
                        minScore: 0.0,
                        maxPerPage: Math.Max(plan.Selection.MaxPerPage, 2),
                        query: plan.Request.Query);
                    PruneUnrelatedReferenceVersionGroupSelections(plan.Request.Query, flow.Selection.Matches);
                    PruneCurrentReferenceVersionSelectionsWhenHistoricalVersionRequested(plan.Request.Query, flow.Selection.Matches);
                    PrioritizeFinalSelections(plan.Request.Query, flow.Selection.Matches);
                    RebuildSelectedKeys(flow.Selection.Matches, flow.Selection.Keys);
                    flow.Session.Timings.OrderingWatch.Stop();
                    flow.Session.Timings.SelectionMs += flow.Session.Timings.OrderingWatch.ElapsedMilliseconds;
                }
            }
        }
    }
}
