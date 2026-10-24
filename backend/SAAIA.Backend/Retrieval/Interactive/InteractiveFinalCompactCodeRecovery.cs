using static SAAIA.Backend.Retrieval.RetrievalAnswerGuidance;
using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalLexicalExpansion;
using static SAAIA.Backend.Retrieval.RetrievalSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveFinalCompactCodeRecovery
{
    internal static async Task ExecuteAsync(InteractiveRetrievalFlow flow)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        if (flow.Selection.Matches.Count == 0
                    && !plan.Routes.SkipChunkRetrieversForDocumentOverview
                    && ShouldAllowCompactDocumentCodeEmptyRecovery(plan.Request.Query, plan.Scope.HasExplicitFileDocumentHint)
                    && plan.Backfills.CompactDocumentCodes.Count > 0)
        {
            var finalCompactCodeRecoveryLimit = Math.Min(plan.Selection.Candidates, Math.Max(plan.Selection.TopK * 4, 24));
            var (finalCompactCodeRecoveryMatches, finalCompactCodeRecoveryMs) = await flow.Session.Phases.MeasureAsync(
                phaseName: "retrieval_compact_document_code_final_empty_recovery",
                retriever: "compact_document_code_recovery",
                action: async () =>
                {
                    var recovery = new List<RagMatch>();
                    foreach (var compactCode in plan.Backfills.CompactDocumentCodes.Take(3))
                    {
                        recovery.AddRange(await SearchTitleAnchorRouteMatchesAsync(
                            resources.DataSource,
                            resources.Context.TenantId,
                            compactCode,
                            plan.Scope.Category,
                            plan.Request.DocId,
                            plan.Request.DocPath,
                            finalCompactCodeRecoveryLimit,
                            resources.Context.CancellationToken,
                            plan.Scope.CategoryPath,
                            degradedRetrieverRef: flow.Session.Degradation.Mark));
                        recovery.AddRange(await SearchDenseMatchesAsync(
                            resources.DataSource,
                            resources.HttpClients,
                            resources.Options,
                            resources.Context.TenantId,
                            NormalizeQuery(compactCode),
                            plan.Scope.Category,
                            plan.Request.DocId,
                            plan.Request.DocPath,
                            finalCompactCodeRecoveryLimit,
                            resources.Context.CancellationToken,
                            teiMsRef: value => flow.Session.Timings.TeiMs += value,
                            qdrantMsRef: value => flow.Session.Timings.QdrantMs += value,
                            qdrantStatusRef: value => flow.Session.Timings.QdrantStatus = value,
                            categoryPath: plan.Scope.CategoryPath,
                            degradedRetrieverRef: flow.Session.Degradation.Mark,
                            teiGovernor: resources.Context.TeiGovernor));
                    }

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
            flow.Session.Timings.TitleAnchorRoutePhaseMs += finalCompactCodeRecoveryMs;

            if (finalCompactCodeRecoveryMatches.Count > 0)
            {
                flow.Session.Timings.OrderingWatch.Restart();
                var compactRecoveryQuery = string.Join(' ', plan.Backfills.CompactDocumentCodes);
                var calibratedRecovery = CalibrateFusedMatches(
                    compactRecoveryQuery,
                    finalCompactCodeRecoveryMatches,
                    plan.Request.Query);
                AddRankedMatchesDiagnosed(
                flow.Session.Diagnostics, flow.Selection.Matches,
                                    flow.Selection.Keys,
                                    SuppressNavigationalNoise(plan.Request.Query, calibratedRecovery),
                                    plan.Selection.TopK,
                                    minScore: 0.0,
                                    Math.Max(plan.Selection.MaxPerDoc, 2),
                                    Math.Max(plan.Selection.MaxPerPage, 2),
                                    query: compactRecoveryQuery);
                PruneNavigationalSelections(plan.Request.Query, flow.Selection.Matches);
                RebuildSelectedKeys(flow.Selection.Matches, flow.Selection.Keys);
                flow.Session.Timings.OrderingWatch.Stop();
                flow.Session.Timings.SelectionMs += flow.Session.Timings.OrderingWatch.ElapsedMilliseconds;
            }
        }
    }
}
