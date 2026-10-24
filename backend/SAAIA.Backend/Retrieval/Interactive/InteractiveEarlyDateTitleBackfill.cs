using System.Diagnostics;
using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalLexicalExpansion;
using static SAAIA.Backend.Retrieval.RetrievalSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveEarlyDateTitleBackfill
{
    internal static async Task ExecuteAsync(InteractiveRetrievalFlow flow)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        if (plan.Backfills.DateDocumentTitles.Count > 0 && !SelectionsCoverDateAnchoredDocument(plan.Request.Query, flow.Selection.Matches))
        {
            var (dateDocumentBackfillMatches, dateDocumentBackfillMs) = await flow.Session.Phases.MeasureAsync(
                phaseName: "retrieval_date_document_name_backfill",
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
            flow.Session.Timings.TitleAnchorRoutePhaseMs += dateDocumentBackfillMs;

            if (dateDocumentBackfillMatches.Count > 0)
            {
                var dateSelectionSw = Stopwatch.StartNew();
                var calibratedDateDocumentBackfill = SuppressNavigationalNoise(
                    plan.Request.Query,
                    CalibrateFusedMatches(plan.Request.Query, dateDocumentBackfillMatches, plan.Request.Query));
                RebuildSelectedKeys(flow.Selection.Matches, flow.Selection.Keys);
                AddRankedMatchesDiagnosed(
                flow.Session.Diagnostics, flow.Selection.Matches,
                                    flow.Selection.Keys,
                                    calibratedDateDocumentBackfill,
                                    plan.Selection.TopK,
                                    minScore: 0.0,
                                    maxPerDoc: Math.Max(plan.Selection.MaxPerDoc, 3),
                                    maxPerPage: Math.Max(plan.Selection.MaxPerPage, 2),
                                    query: plan.Request.Query);
                CleanupLateSelectionBackfills(plan.Request.Query, flow.Selection.Matches);
                RebuildSelectedKeys(flow.Selection.Matches, flow.Selection.Keys);
                dateSelectionSw.Stop();
                flow.Session.Timings.SelectionMs += dateSelectionSw.ElapsedMilliseconds;
            }
        }
    }
}
