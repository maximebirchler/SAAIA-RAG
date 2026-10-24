using System.Diagnostics;
using static SAAIA.Backend.Retrieval.RetrievalCandidateSignals;
using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveDirectTechnicalPhrase
{
    internal static async Task ExecuteAsync(InteractiveRetrievalFlow flow)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        if (!plan.Routes.SkipChunkRetrieversForDocumentOverview
                    && !plan.Scope.HasExplicitFileDocumentHint
                    && ShouldRunShortTechnicalDirectPhraseBackfill(plan.Query.RankingQuery, flow.Selection.Matches))
        {
            var (shortTechnicalPhraseMatches, shortTechnicalPhraseMs) = await flow.Session.Phases.MeasureAsync(
                phaseName: "retrieval_short_technical_direct_phrase",
                retriever: "sparse_bm25",
                action: () => SearchShortTechnicalDirectPhraseMatchesAsync(
                    resources.DataSource,
                    resources.Context.TenantId,
                    plan.Query.RankingQuery,
                    plan.Scope.Category,
                    plan.Request.DocId,
                    plan.Request.DocPath,
                    Math.Max(plan.Selection.TopK * 2, 12),
                    resources.Context.CancellationToken,
                    plan.Scope.CategoryPath,
                    degradedRetrieverRef: flow.Session.Degradation.Mark),
                getReturnedCount: static matches => matches.Count);
            flow.Session.Timings.SparsePhaseMs += shortTechnicalPhraseMs;

            if (shortTechnicalPhraseMatches.Count > 0)
            {
                var calibratedShortTechnicalSelections = CalibrateFusedMatches(
                    plan.Query.RankingQuery,
                    flow.Selection.Matches.Concat(shortTechnicalPhraseMatches).DistinctBy(BuildRagMatchIdentityKey).ToList(),
                    plan.Request.Query);
                calibratedShortTechnicalSelections = SuppressNavigationalNoise(plan.Request.Query, calibratedShortTechnicalSelections);

                var selectionSw = Stopwatch.StartNew();
                flow.Selection.Matches.Clear();
                flow.Selection.Keys.Clear();
                AddRankedMatchesDiagnosed(
                flow.Session.Diagnostics, flow.Selection.Matches,
                                    flow.Selection.Keys,
                                    calibratedShortTechnicalSelections,
                                    plan.Selection.TopK,
                                    minScore: 0.0,
                                    Math.Max(plan.Selection.MaxPerDoc, 2),
                                    Math.Max(plan.Selection.MaxPerPage, 2),
                                    query: plan.Request.Query);
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
