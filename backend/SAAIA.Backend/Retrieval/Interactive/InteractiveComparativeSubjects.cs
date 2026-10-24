using System.Diagnostics;
using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalQueryPolicy;
using static SAAIA.Backend.Retrieval.RetrievalSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveComparativeSubjects
{
    internal static async Task ExecuteAsync(InteractiveRetrievalFlow flow, InteractivePrimaryChannels channels)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;
        var selectionSw = new Stopwatch();

        if (!plan.Routes.SkipChunkRetrieversForDocumentOverview
                    && !plan.Scope.HasExplicitFileDocumentHint
                    && ShouldAllowMultipleChunksForComparativeSubject(plan.Request.Query))
        {
            var comparativeSubjectBackfillQueries = BuildComparativeSubjectBackfillQueries(plan.Request.Query);
            if (comparativeSubjectBackfillQueries.Count > 0)
            {
                var (comparativeSubjectBackfillMatches, comparativeSubjectBackfillMs) = await flow.Session.Phases.MeasureAsync(
                    phaseName: "retrieval_comparative_subject_backfill",
                    retriever: "title_anchor_route",
                    action: async () =>
                    {
                        var backfill = new List<RagMatch>();
                        foreach (var focusedQuery in comparativeSubjectBackfillQueries)
                        {
                            if (!plan.Routes.SkipUnanchoredTitleAnchorRouteForBroadDiversity)
                            {
                                backfill.AddRange(await SearchTitleAnchorRouteMatchesAsync(
                                    resources.DataSource,
                                    resources.Context.TenantId,
                                    focusedQuery,
                                    plan.Scope.Category,
                                    plan.Request.DocId,
                                    plan.Request.DocPath,
                                    Math.Min(Math.Max(plan.Selection.TopK, 12), 32),
                                    resources.Context.CancellationToken,
                                    plan.Scope.CategoryPath,
                                    degradedRetrieverRef: flow.Session.Degradation.Mark));
                            }
                            if (!plan.Routes.UseBareVsComparativeOperandRoute)
                            {
                                backfill.AddRange(await SearchLexicalContentFallbackMatchesAsync(
                                    resources.DataSource,
                                    resources.Context.TenantId,
                                    focusedQuery,
                                    plan.Scope.Category,
                                    plan.Request.DocId,
                                    plan.Request.DocPath,
                                    Math.Min(Math.Max(plan.Selection.TopK, 12), 32),
                                    resources.Context.CancellationToken,
                                    categoryPath: plan.Scope.CategoryPath,
                                    degradedRetrieverRef: flow.Session.Degradation.Mark,
                                    commandTimeoutSeconds: plan.SparseCommandTimeoutSeconds));
                                if (!plan.Routes.SkipUnanchoredTitleAnchorRouteForBroadDiversity)
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
                flow.Session.Timings.TitleAnchorRoutePhaseMs += comparativeSubjectBackfillMs;

                if (comparativeSubjectBackfillMatches.Count > 0)
                {
                    selectionSw.Restart();
                    MergeComparativeSubjectBackfillMatches(
                        flow.Selection.Matches,
                        flow.Selection.Keys,
                        comparativeSubjectBackfillMatches,
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
