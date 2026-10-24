using static SAAIA.Backend.Retrieval.RetrievalQueryPolicy;
using static SAAIA.Backend.Retrieval.RetrievalSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveFinalDocumentHintEvidence
{
    internal static async Task ExecuteAsync(InteractiveRetrievalFlow flow)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        var finalDocumentHintTokens = ExtractDocumentHintTokens(plan.Request.Query);
        if (!plan.Scope.HasDocScope
            && !plan.Routes.UseScopedProfileFallback
            && !plan.Routes.SkipChunkRetrieversForDocumentOverview
            && finalDocumentHintTokens.Count > 0)
        {
            var finalDocumentHintLinkedAnchors = flow.Selection.Matches
                .Where(match =>
                    !IsDocumentProfileMatch(match)
                    && IsContentSelectionCandidate(match)
                    && DocumentMatchesHint(finalDocumentHintTokens, match))
                .Take(3)
                .ToList();
            if (finalDocumentHintLinkedAnchors.Count > 0)
            {
                var (finalDocumentHintLinkedMatches, finalDocumentHintLinkedMs) = await flow.Session.Phases.MeasureAsync(
                    phaseName: "retrieval_final_document_hint_linked_context",
                    retriever: "linked_context",
                    action: () => SearchLinkedMatchesAsync(
                        resources.DataSource,
                        resources.Context.TenantId,
                        finalDocumentHintLinkedAnchors,
                        plan.Scope.Category,
                        docId: null,
                        docPath: null,
                        Math.Min(plan.Selection.TopK, 6),
                        resources.Context.CancellationToken,
                        plan.Scope.CategoryPath),
                    getReturnedCount: static matches => matches.Count);
                flow.Session.Timings.LinkedPhaseMs += finalDocumentHintLinkedMs;

                if (finalDocumentHintLinkedMatches.Count > 0)
                {
                    var mergedFinalLinkedSelections = CalibrateFusedMatches(
                        plan.Query.RetrievalQuery,
                        flow.Selection.Matches.Concat(finalDocumentHintLinkedMatches).ToList(),
                        plan.Request.Query);
                    mergedFinalLinkedSelections = SuppressNavigationalNoise(plan.Request.Query, mergedFinalLinkedSelections);
                    flow.Selection.Matches.Clear();
                    flow.Selection.Keys.Clear();
                    AddRankedMatchesDiagnosed(
                    flow.Session.Diagnostics, flow.Selection.Matches,
                                            flow.Selection.Keys,
                                            mergedFinalLinkedSelections,
                                            plan.Selection.TopK,
                                            minScore: 0.0,
                                            Math.Max(plan.Selection.MaxPerDoc, 2),
                                            Math.Max(plan.Selection.MaxPerPage, 2),
                                            query: plan.Request.Query);
                }
            }
        }
    }
}
