using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalLexicalExpansion;
using static SAAIA.Backend.Retrieval.RetrievalSelection;
using static SAAIA.Backend.Retrieval.RetrievalTitleSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveFinalLexicalProfiles
{
    internal static async Task ExecuteAsync(InteractiveRetrievalFlow flow)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        if (flow.Selection.Matches.Count == 0
                    && ShouldAllowLexicalProfileEmptyRecovery(plan.Request.Query, plan.Scope.HasExplicitFileDocumentHint))
        {
            var lexicalProfileRecoveryQuery = BuildLexicalProfileEmptyRecoveryQuery(plan.Request.Query, plan.Scope.CategoryQueryHint);
            if (!string.IsNullOrWhiteSpace(lexicalProfileRecoveryQuery))
            {
                var (lexicalProfileRecoveryMatches, lexicalProfileRecoveryMs) = await flow.Session.Phases.MeasureAsync(
                    phaseName: "retrieval_lexical_profile_empty_recovery",
                    retriever: "lexical_profile_recovery",
                    action: () => SearchDocumentOverviewProfileMatchesAsync(
                        resources.DataSource,
                        resources.Context.TenantId,
                        lexicalProfileRecoveryQuery,
                        plan.Scope.Category,
                        plan.Request.DocId,
                        plan.Request.DocPath,
                        Math.Min(Math.Max(plan.Selection.TopK, 8), 16),
                        resources.Context.CancellationToken,
                        plan.Scope.CategoryPath,
                        requireLexicalMatch: true,
                        degradedRetrieverRef: flow.Session.Degradation.Mark,
                        commandTimeoutSeconds: plan.SparseCommandTimeoutSeconds),
                    getReturnedCount: static matches => matches.Count);
                flow.Session.Timings.ProfilePhaseMs += lexicalProfileRecoveryMs;

                if (lexicalProfileRecoveryMatches.Count > 0)
                {
                    flow.Session.Timings.OrderingWatch.Restart();
                    AddRankedMatchesDiagnosed(
                    flow.Session.Diagnostics, flow.Selection.Matches,
                                            flow.Selection.Keys,
                                            CalibrateFusedMatches(lexicalProfileRecoveryQuery, lexicalProfileRecoveryMatches, plan.Request.Query),
                                            plan.Selection.TopK,
                                            minScore: 0.0,
                                            Math.Max(plan.Selection.MaxPerDoc, 2),
                                            Math.Max(plan.Selection.MaxPerPage, 1),
                                            query: lexicalProfileRecoveryQuery);
                    PrioritizeFinalSelections(plan.Request.Query, flow.Selection.Matches);
                    RebuildSelectedKeys(flow.Selection.Matches, flow.Selection.Keys);
                    flow.Session.Timings.OrderingWatch.Stop();
                    flow.Session.Timings.SelectionMs += flow.Session.Timings.OrderingWatch.ElapsedMilliseconds;
                }
            }
        }
    }
}
