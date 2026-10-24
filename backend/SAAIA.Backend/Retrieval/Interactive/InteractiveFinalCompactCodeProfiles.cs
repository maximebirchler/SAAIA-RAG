using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalLexicalExpansion;
using static SAAIA.Backend.Retrieval.RetrievalQueryPolicy;
using static SAAIA.Backend.Retrieval.RetrievalSelection;
using static SAAIA.Backend.Retrieval.RetrievalTitleSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveFinalCompactCodeProfiles
{
    internal static async Task ExecuteAsync(InteractiveRetrievalFlow flow)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        var finalCompactDocumentCodeProfilePhrases = ExtractCompactDocumentCodeProfileRecoveryPhrases(plan.Request.Query)
                    .Where(phrase => !SelectionsCoverCompactDocumentCodePhrase(phrase, flow.Selection.Matches))
                    .ToArray();
        if (finalCompactDocumentCodeProfilePhrases.Length > 0
            && !plan.Routes.SkipChunkRetrieversForDocumentOverview
            && ShouldAllowCompactDocumentCodeEmptyRecovery(plan.Request.Query, plan.Scope.HasExplicitFileDocumentHint)
            && (flow.Selection.Matches.Count == 0 || ShouldPreferComparativeDocumentDiversity(plan.Request.Query)))
        {
            var (finalCompactProfileMatches, finalCompactProfileMs) = await flow.Session.Phases.MeasureAsync(
                phaseName: "retrieval_compact_document_code_profile_final_recovery",
                retriever: "compact_document_code_profile",
                action: async () =>
                {
                    var backfill = new List<RagMatch>();
                    foreach (var phrase in finalCompactDocumentCodeProfilePhrases.Take(4))
                    {
                        backfill.AddRange(await SearchDocumentOverviewProfileMatchesAsync(
                            resources.DataSource,
                            resources.Context.TenantId,
                            phrase,
                            plan.Scope.Category,
                            plan.Request.DocId,
                            plan.Request.DocPath,
                            Math.Min(Math.Max(plan.Selection.TopK, 8), 16),
                            resources.Context.CancellationToken,
                            plan.Scope.CategoryPath,
                            requireLexicalMatch: true,
                            degradedRetrieverRef: flow.Session.Degradation.Mark,
                            commandTimeoutSeconds: plan.SparseCommandTimeoutSeconds));
                    }

                    return DeduplicatePreciseTitleBackfillMatches(backfill);
                },
                getReturnedCount: static matches => matches.Count);
            flow.Session.Timings.ProfilePhaseMs += finalCompactProfileMs;

            if (finalCompactProfileMatches.Count > 0)
            {
                flow.Session.Timings.OrderingWatch.Restart();
                var compactProfileQuery = string.Join(' ', finalCompactDocumentCodeProfilePhrases);
                AddRankedMatchesDiagnosed(
                flow.Session.Diagnostics, flow.Selection.Matches,
                                    flow.Selection.Keys,
                                    CalibrateFusedMatches(compactProfileQuery, finalCompactProfileMatches, plan.Request.Query),
                                    plan.Selection.TopK,
                                    minScore: 0.0,
                                    Math.Max(plan.Selection.MaxPerDoc, 2),
                                    Math.Max(plan.Selection.MaxPerPage, 1),
                                    query: compactProfileQuery);
                PrioritizeFinalSelections(plan.Request.Query, flow.Selection.Matches);
                RebuildSelectedKeys(flow.Selection.Matches, flow.Selection.Keys);
                flow.Session.Timings.OrderingWatch.Stop();
                flow.Session.Timings.SelectionMs += flow.Session.Timings.OrderingWatch.ElapsedMilliseconds;
            }
        }
    }
}
