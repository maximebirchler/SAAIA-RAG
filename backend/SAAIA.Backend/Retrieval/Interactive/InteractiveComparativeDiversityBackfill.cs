using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalQueryPolicy;
using static SAAIA.Backend.Retrieval.RetrievalSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveComparativeDiversityBackfill
{
    internal static async Task ExecuteAsync(InteractiveRetrievalFlow flow)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        if (!plan.Routes.SkipChunkRetrieversForDocumentOverview
                    && !plan.Scope.HasExplicitFileDocumentHint
                    && !plan.Routes.SkipUnanchoredTitleAnchorRouteForBroadDiversity
                    && ShouldRunFinalComparativeSparseDiversityBackfill(plan.Request.Query, flow.Selection.Matches))
        {
            var comparativeSubjectBackfillQueries = BuildComparativeSubjectBackfillQueries(plan.Request.Query);
            var (comparativeSparseBackfillMatches, comparativeSparseBackfillMs) = await flow.Session.Phases.MeasureAsync(
                phaseName: "retrieval_final_comparative_sparse_backfill",
                retriever: "sparse_bm25",
                action: async () =>
                {
                    var backfill = new List<RagMatch>();
                    foreach (var focusedQuery in comparativeSubjectBackfillQueries)
                    {
                        backfill.AddRange(await SearchSparseMatchesAsync(
                            resources.DataSource,
                            resources.Context.TenantId,
                            focusedQuery,
                            plan.Scope.Category,
                            plan.Request.DocId,
                            plan.Request.DocPath,
                            Math.Min(Math.Max(plan.Selection.TopK, 12), 32),
                            resources.Context.CancellationToken,
                            sparseMsRef: value => flow.Session.Timings.SparseMs += value,
                            lexicalExpansionQuery: focusedQuery,
                            categoryPath: plan.Scope.CategoryPath,
                            includeProfileCardMatches: false,
                            degradedRetrieverRef: flow.Session.Degradation.Mark,
                            commandTimeoutSeconds: plan.SparseCommandTimeoutSeconds));
                    }

                    return backfill
                        .GroupBy(BuildMatchDedupKey, StringComparer.OrdinalIgnoreCase)
                        .Select(static group => group
                            .OrderByDescending(match => match.Score)
                            .ThenBy(match => match.DocPath, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(match => match.ChunkIndex)
                            .First())
                        .ToList();
                },
                getReturnedCount: static matches => matches.Count);
            flow.Session.Timings.SparsePhaseMs += comparativeSparseBackfillMs;

            if (comparativeSparseBackfillMatches.Count > 0)
            {
                flow.Session.Timings.OrderingWatch.Restart();
                RebuildSelectedKeys(flow.Selection.Matches, flow.Selection.Keys);
                EnsureComparativeBackfillDocumentDiversity(
                    flow.Selection.Matches,
                    flow.Selection.Keys,
                    comparativeSparseBackfillMatches,
                    plan.Selection.TopK,
                    minScore: 0.0,
                    maxPerPage: Math.Max(plan.Selection.MaxPerPage, 2),
                    query: plan.Request.Query);
                PruneNavigationalSelections(plan.Request.Query, flow.Selection.Matches);
                PromoteActionableSameDocumentEvidence(flow.Selection.Matches);
                RebuildSelectedKeys(flow.Selection.Matches, flow.Selection.Keys);
                flow.Session.Timings.OrderingWatch.Stop();
                flow.Session.Timings.SelectionMs += flow.Session.Timings.OrderingWatch.ElapsedMilliseconds;
            }
        }
    }
}
