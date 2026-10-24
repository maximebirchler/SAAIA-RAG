using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalLexicalExpansion;
using static SAAIA.Backend.Retrieval.RetrievalSelection;
using static SAAIA.Backend.Retrieval.RetrievalTitleSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveLateDateTitleRecovery
{
    internal static async Task ExecuteAsync(InteractiveRetrievalFlow flow)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        if (plan.Backfills.DateDocumentTitles.Count > 0 && !SelectionsCoverDateAnchoredDocument(plan.Request.Query, flow.Selection.Matches))
        {
            var (lateDateDocumentBackfillMatches, lateDateDocumentBackfillMs) = await flow.Session.Phases.MeasureAsync(
                phaseName: "retrieval_date_document_name_late_recovery",
                retriever: "date_document_name",
                action: () => SearchDateAnchoredDocumentNameContentMatchesAsync(
                    resources.DataSource,
                    resources.Context.TenantId,
                    plan.Request.Query,
                    plan.Backfills.DateDocumentTitles,
                    plan.Scope.Category,
                    plan.Request.DocId,
                    plan.Request.DocPath,
                    Math.Min(plan.Selection.Candidates, Math.Max(plan.Selection.TopK, 16)),
                    resources.Context.CancellationToken,
                    plan.Scope.CategoryPath,
                    degradedRetrieverRef: flow.Session.Degradation.Mark),
                getReturnedCount: static matches => matches.Count);
            flow.Session.Timings.TitleAnchorRoutePhaseMs += lateDateDocumentBackfillMs;

            if (lateDateDocumentBackfillMatches.Count > 0)
            {
                flow.Session.Timings.OrderingWatch.Restart();
                flow.Selection.Matches.Clear();
                flow.Selection.Keys.Clear();
                var calibratedLateDateDocumentBackfill = SuppressNavigationalNoise(
                    plan.Request.Query,
                    CalibrateFusedMatches(plan.Request.Query, lateDateDocumentBackfillMatches, plan.Request.Query));
                AddRankedMatchesDiagnosed(
                flow.Session.Diagnostics, flow.Selection.Matches,
                                    flow.Selection.Keys,
                                    calibratedLateDateDocumentBackfill,
                                    plan.Selection.TopK,
                                    minScore: 0.0,
                                    maxPerDoc: Math.Max(plan.Selection.MaxPerDoc, 3),
                                    maxPerPage: Math.Max(plan.Selection.MaxPerPage, 2),
                                    query: plan.Request.Query);
                PrioritizeFinalSelections(plan.Request.Query, flow.Selection.Matches);
                RebuildSelectedKeys(flow.Selection.Matches, flow.Selection.Keys);
                flow.Session.Timings.OrderingWatch.Stop();
                flow.Session.Timings.SelectionMs += flow.Session.Timings.OrderingWatch.ElapsedMilliseconds;
            }
        }
    }
}
