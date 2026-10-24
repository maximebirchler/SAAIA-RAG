using System.Diagnostics;
using static SAAIA.Backend.Retrieval.RetrievalCandidateSignals;
using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveEnumerativeLookup
{
    internal static async Task ExecuteAsync(InteractiveRetrievalFlow flow)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        if (!plan.Routes.SkipChunkRetrieversForDocumentOverview
                    && !plan.Scope.HasExplicitFileDocumentHint
                    && ShouldBackfillEnumerativeSearch(plan.Request.Query, flow.Selection.Matches.Count, plan.Selection.TopK))
        {
            var focusedLexicalQuery = BuildFocusedLexicalBackfillQuery(plan.Request.Query);
            if (!string.IsNullOrWhiteSpace(focusedLexicalQuery)
                && !string.Equals(focusedLexicalQuery.Trim(), plan.Request.Query.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                var (backfillMatches, backfillDurationMs) = await flow.Session.Phases.MeasureAsync(
                    phaseName: "retrieval_lexical_backfill",
                    retriever: "lexical_backfill",
                    action: () => SearchSparseMatchesAsync(
                        resources.DataSource,
                        resources.Context.TenantId,
                        focusedLexicalQuery,
                        plan.Scope.Category,
                        plan.Request.DocId,
                        plan.Request.DocPath,
                        plan.SqlHeavyCandidateLimit,
                        resources.Context.CancellationToken,
                        sparseMsRef: value => flow.Session.Timings.SparseMs += value,
                        lexicalExpansionQuery: focusedLexicalQuery,
                        categoryPath: plan.Scope.CategoryPath,
                        includeProfileCardMatches: false,
                        degradedRetrieverRef: flow.Session.Degradation.Mark,
                        commandTimeoutSeconds: plan.SparseCommandTimeoutSeconds),
                    getReturnedCount: static matches => matches.Count);
                flow.Session.Timings.SparsePhaseMs += backfillDurationMs;

                var calibratedBackfill = CalibrateFusedMatches(focusedLexicalQuery, backfillMatches, plan.Request.Query);
                calibratedBackfill = SuppressNavigationalNoise(plan.Request.Query, calibratedBackfill);
                var selectionSw = Stopwatch.StartNew();
                AddRankedMatchesDiagnosed(
                flow.Session.Diagnostics, flow.Selection.Matches,
                                    flow.Selection.Keys,
                                    calibratedBackfill,
                                    plan.Selection.TopK,
                                    minScore: 0.0,
                                    plan.Selection.MaxPerDoc,
                                    Math.Max(plan.Selection.MaxPerPage, 2));
                PruneNavigationalSelections(plan.Request.Query, flow.Selection.Matches);
                PromoteActionableSameDocumentEvidence(flow.Selection.Matches);
                RebuildSelectedKeys(flow.Selection.Matches, flow.Selection.Keys);
                selectionSw.Stop();
                flow.Session.Timings.SelectionMs += selectionSw.ElapsedMilliseconds;
            }
        }
    }
}
