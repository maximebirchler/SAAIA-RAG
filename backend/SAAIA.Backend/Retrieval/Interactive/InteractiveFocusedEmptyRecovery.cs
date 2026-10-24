using System.Diagnostics;
using static SAAIA.Backend.Retrieval.RetrievalAnswerGuidance;
using static SAAIA.Backend.Retrieval.RetrievalCandidateSignals;
using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalSelection;
using static SAAIA.Backend.Retrieval.RetrievalTitleSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveFocusedEmptyRecovery
{
    internal static async Task ExecuteAsync(InteractiveRetrievalFlow flow)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        if (!plan.Routes.SkipChunkRetrieversForDocumentOverview
                    && !plan.Scope.HasExplicitFileDocumentHint
                    && flow.Selection.Matches.Count == 0
                    && ShouldRunFocusedLookupEmptySelectionRecovery(plan.Request.Query))
        {
            var focusedRecoveryQuery = BuildFocusedLexicalBackfillQuery(plan.Request.Query);
            var focusedRecoveryLimit = Math.Min(plan.Selection.Candidates, Math.Max(plan.Selection.TopK * 4, 24));
            var (focusedRecoveryMatches, focusedRecoveryMs) = await flow.Session.Phases.MeasureAsync(
                phaseName: "retrieval_focused_empty_recovery",
                retriever: "focused_lookup_recovery",
                action: async () =>
                {
                    var recovery = new List<RagMatch>();
                    recovery.AddRange(await SearchTitleAnchorRouteMatchesAsync(
                        resources.DataSource,
                        resources.Context.TenantId,
                        focusedRecoveryQuery,
                        plan.Scope.Category,
                        plan.Request.DocId,
                        plan.Request.DocPath,
                        focusedRecoveryLimit,
                        resources.Context.CancellationToken,
                        plan.Scope.CategoryPath,
                        degradedRetrieverRef: flow.Session.Degradation.Mark));
                    recovery.AddRange(await SearchSparseMatchesAsync(
                        resources.DataSource,
                        resources.Context.TenantId,
                        focusedRecoveryQuery,
                        plan.Scope.Category,
                        plan.Request.DocId,
                        plan.Request.DocPath,
                        focusedRecoveryLimit,
                        resources.Context.CancellationToken,
                        sparseMsRef: value => flow.Session.Timings.SparseMs += value,
                        lexicalExpansionQuery: focusedRecoveryQuery,
                        categoryPath: plan.Scope.CategoryPath,
                        includeProfileCardMatches: true,
                        degradedRetrieverRef: flow.Session.Degradation.Mark,
                        commandTimeoutSeconds: plan.SparseCommandTimeoutSeconds));
                    recovery.AddRange(await SearchDenseMatchesAsync(
                        resources.DataSource,
                        resources.HttpClients,
                        resources.Options,
                        resources.Context.TenantId,
                        NormalizeQuery(focusedRecoveryQuery),
                        plan.Scope.Category,
                        plan.Request.DocId,
                        plan.Request.DocPath,
                        focusedRecoveryLimit,
                        resources.Context.CancellationToken,
                        teiMsRef: value => flow.Session.Timings.TeiMs += value,
                        qdrantMsRef: value => flow.Session.Timings.QdrantMs += value,
                        qdrantStatusRef: value => flow.Session.Timings.QdrantStatus = value,
                        categoryPath: plan.Scope.CategoryPath,
                        degradedRetrieverRef: flow.Session.Degradation.Mark,
                        teiGovernor: resources.Context.TeiGovernor));

                    return recovery
                        .GroupBy(BuildMatchDedupKey, StringComparer.OrdinalIgnoreCase)
                        .Select(static group => group
                            .OrderByDescending(match => match.Score)
                            .ThenBy(match => match.DocPath, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(match => match.ChunkIndex)
                            .First())
                        .ToList();
                },
                getReturnedCount: static matches => matches.Count);
            flow.Session.Timings.SelectionMs += focusedRecoveryMs;

            if (focusedRecoveryMatches.Count > 0)
            {
                var focusedRecoverySelectionSw = Stopwatch.StartNew();
                var calibratedRecovery = CalibrateFusedMatches(focusedRecoveryQuery, focusedRecoveryMatches, plan.Request.Query);
                calibratedRecovery = SuppressNavigationalNoise(plan.Request.Query, calibratedRecovery);
                AddRankedMatchesDiagnosed(
                flow.Session.Diagnostics, flow.Selection.Matches,
                                    flow.Selection.Keys,
                                    calibratedRecovery,
                                    plan.Selection.TopK,
                                    minScore: 0.0,
                                    Math.Max(plan.Selection.MaxPerDoc, 2),
                                    Math.Max(plan.Selection.MaxPerPage, 2),
                                    query: plan.Request.Query);
                if (flow.Selection.Matches.Count > 0 && flow.Selection.Matches.Count < plan.Selection.TopK)
                {
                    var (focusedRecoveryLinkedMatches, focusedRecoveryLinkedMs) = await flow.Session.Phases.MeasureAsync(
                        phaseName: "retrieval_focused_empty_recovery_linked_context",
                        retriever: "linked_context",
                        action: () => SearchLinkedMatchesAsync(
                            resources.DataSource,
                            resources.Context.TenantId,
                            flow.Selection.Matches,
                            plan.Scope.Category,
                            plan.Request.DocId,
                            plan.Request.DocPath,
                            plan.Selection.TopK - flow.Selection.Matches.Count,
                            resources.Context.CancellationToken,
                            plan.Scope.CategoryPath,
                            includeSamePageForLinkedAnchors: true),
                        getReturnedCount: static matches => matches.Count);
                    flow.Session.Timings.LinkedPhaseMs += focusedRecoveryLinkedMs;
                    AddRankedMatchesDiagnosed(
                    flow.Session.Diagnostics, flow.Selection.Matches,
                                            flow.Selection.Keys,
                                            focusedRecoveryLinkedMatches,
                                            plan.Selection.TopK,
                                            minScore: 0.0,
                                            Math.Max(plan.Selection.MaxPerDoc, 2),
                                            Math.Max(plan.Selection.MaxPerPage, 2),
                                            query: plan.Request.Query);
                }

                PruneNavigationalSelections(plan.Request.Query, flow.Selection.Matches);
                PromoteActionableSameDocumentEvidence(flow.Selection.Matches);
                PruneUnanchoredFocusedLookupSelections(plan.Request.Query, flow.Selection.Matches);
                RebuildSelectedKeys(flow.Selection.Matches, flow.Selection.Keys);
                focusedRecoverySelectionSw.Stop();
                flow.Session.Timings.SelectionMs += focusedRecoverySelectionSw.ElapsedMilliseconds;
            }
        }
    }
}
