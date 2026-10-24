using System.Diagnostics;
using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalQueryPolicy;
using static SAAIA.Backend.Retrieval.RetrievalSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveComparativeOperandProfiles
{
    internal static async Task ExecuteAsync(InteractiveRetrievalFlow flow)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;
        var selectionSw = new Stopwatch();

        if (!plan.Routes.SkipChunkRetrieversForDocumentOverview
                    && !plan.Scope.HasExplicitFileDocumentHint
                    && ShouldRunComparativeOperandProfileBackfill(plan.Request.Query, flow.Selection.Matches))
        {
            var comparativeOperandProfileQueries = BuildComparativeOperandProfileBackfillQueries(plan.Request.Query);
            if (comparativeOperandProfileQueries.Count > 0)
            {
                var (comparativeOperandProfileMatches, comparativeOperandProfileMs) = await flow.Session.Phases.MeasureAsync(
                    phaseName: "retrieval_comparative_operand_profile_backfill",
                    retriever: "document_profile",
                    action: async () =>
                    {
                        var backfill = new List<RagMatch>();
                        foreach (var focusedQuery in comparativeOperandProfileQueries)
                        {
                            backfill.AddRange(await SearchDocumentProfileMatchesAsync(
                                resources.DataSource,
                                resources.Context.TenantId,
                                focusedQuery,
                                plan.Scope.Category,
                                plan.Request.DocId,
                                plan.Request.DocPath,
                                Math.Min(Math.Max(plan.Selection.TopK, 12), 32),
                                resources.Context.CancellationToken,
                                plan.Scope.CategoryPath,
                                degradedRetrieverRef: flow.Session.Degradation.Mark,
                                commandTimeoutSeconds: plan.SparseCommandTimeoutSeconds));
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
                flow.Session.Timings.ProfilePhaseMs += comparativeOperandProfileMs;

                if (comparativeOperandProfileMatches.Count > 0)
                {
                    selectionSw.Restart();
                    MergeComparativeSubjectBackfillMatches(
                        flow.Selection.Matches,
                        flow.Selection.Keys,
                        comparativeOperandProfileMatches,
                        plan.Selection.TopK,
                        minScore: 0.0,
                        maxPerDoc: Math.Max(plan.Selection.MaxPerDoc, 2),
                        maxPerPage: Math.Max(plan.Selection.MaxPerPage, 2),
                        prioritizeDocumentProfiles: true,
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
