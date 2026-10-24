using System.Diagnostics;
using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalQueryPolicy;
using static SAAIA.Backend.Retrieval.RetrievalSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveComparativeTitles
{
    internal static async Task ExecuteAsync(InteractiveRetrievalFlow flow, InteractivePrimaryChannels channels)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;
        var selectionSw = new Stopwatch();

        if (!plan.Routes.SkipChunkRetrieversForDocumentOverview
                    && !plan.Scope.HasExplicitFileDocumentHint
                    && ShouldRunComparativeTitleBackfill(plan.Request.Query, flow.Selection.Matches))
        {
            var comparativeTitleBackfillQueries = BuildComparativeTitleBackfillQueries(
                plan.Request.Query,
                allowProfileAssistBackfill: channels.AllowComparativeDocumentProfileAssist);
            if (comparativeTitleBackfillQueries.Count > 0)
            {
                var (comparativeTitleBackfillMatches, comparativeTitleBackfillMs) = await flow.Session.Phases.MeasureAsync(
                    phaseName: "retrieval_comparative_title_backfill",
                    retriever: "title_anchor_route",
                    action: async () =>
                    {
                        var backfill = new List<RagMatch>();
                        foreach (var focusedQuery in comparativeTitleBackfillQueries)
                        {
                            var focusedTopK = plan.Routes.UseBareVsComparativeOperandRoute
                                ? Math.Min(Math.Max(plan.Selection.TopK, 8), 12)
                                : Math.Min(Math.Max(plan.Selection.TopK, 12), 32);
                            if (!plan.Routes.SkipUnanchoredTitleAnchorRouteForBroadDiversity)
                            {
                                backfill.AddRange(await SearchTitleAnchorRouteMatchesAsync(
                                    resources.DataSource,
                                    resources.Context.TenantId,
                                    focusedQuery,
                                    plan.Scope.Category,
                                    plan.Request.DocId,
                                    plan.Request.DocPath,
                                    focusedTopK,
                                    resources.Context.CancellationToken,
                                    plan.Scope.CategoryPath,
                                    degradedRetrieverRef: flow.Session.Degradation.Mark));
                            }
                            backfill.AddRange(await SearchLexicalContentFallbackMatchesAsync(
                                resources.DataSource,
                                resources.Context.TenantId,
                                focusedQuery,
                                plan.Scope.Category,
                                plan.Request.DocId,
                                plan.Request.DocPath,
                                focusedTopK,
                                resources.Context.CancellationToken,
                                categoryPath: plan.Scope.CategoryPath,
                                degradedRetrieverRef: flow.Session.Degradation.Mark,
                                commandTimeoutSeconds: plan.SparseCommandTimeoutSeconds));
                            if (!plan.Routes.SkipUnanchoredTitleAnchorRouteForBroadDiversity
                                && !plan.Routes.UseBareVsComparativeOperandRoute)
                            {
                                backfill.AddRange(await SearchSparseMatchesAsync(
                                    resources.DataSource,
                                    resources.Context.TenantId,
                                    focusedQuery,
                                    plan.Scope.Category,
                                    plan.Request.DocId,
                                    plan.Request.DocPath,
                                    focusedTopK,
                                    resources.Context.CancellationToken,
                                    sparseMsRef: value => flow.Session.Timings.SparseMs += value,
                                    lexicalExpansionQuery: focusedQuery,
                                    categoryPath: plan.Scope.CategoryPath,
                                    includeProfileCardMatches: false,
                                    degradedRetrieverRef: flow.Session.Degradation.Mark,
                                    commandTimeoutSeconds: plan.SparseCommandTimeoutSeconds));
                            }
                        }

                        return backfill
                            .GroupBy(BuildMatchDedupKey, StringComparer.OrdinalIgnoreCase)
                            .Select(static group => group
                                .OrderByDescending(match => match.Score)
                                .ThenBy(match => match.ChunkIndex)
                                .First())
                            .ToList();
                    },
                    getReturnedCount: static matches => matches.Count);
                flow.Session.Timings.TitleAnchorRoutePhaseMs += comparativeTitleBackfillMs;

                if (comparativeTitleBackfillMatches.Count > 0)
                {
                    selectionSw.Restart();
                    MergeComparativeSubjectBackfillMatches(
                        flow.Selection.Matches,
                        flow.Selection.Keys,
                        comparativeTitleBackfillMatches,
                        plan.Selection.TopK,
                        minScore: 0.0,
                        maxPerDoc: Math.Max(plan.Selection.MaxPerDoc, 2),
                        maxPerPage: Math.Max(plan.Selection.MaxPerPage, 2),
                        prioritizeDocumentProfiles: channels.PrioritizeDocumentProfilesForSelection,
                        query: plan.Request.Query);
                    PromoteActionableSameDocumentEvidence(flow.Selection.Matches);
                    RebuildSelectedKeys(flow.Selection.Matches, flow.Selection.Keys);
                    selectionSw.Stop();
                    flow.Session.Timings.SelectionMs += selectionSw.ElapsedMilliseconds;
                }
            }
        }
    }
}
