using static SAAIA.Backend.Retrieval.RetrievalQueryPolicy;
using static SAAIA.Backend.Retrieval.RetrievalSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveDocumentHintBackfill
{
    internal static async Task ExecuteAsync(InteractiveRetrievalFlow flow)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        if (!plan.Scope.HasDocScope
                    && !plan.Scope.HasExplicitFileDocumentHint
                    && !plan.Routes.UseScopedProfileFallback
                    && !plan.Routes.SkipChunkRetrieversForDocumentOverview
                    && ShouldRunDocumentHintScopedBackfill(plan.Request.Query, flow.Selection.Matches))
        {
            var documentHintBackfillQuery = BuildDocumentHintScopedBackfillQuery(plan.Request.Query, plan.Query.RetrievalQuery);
            if (!string.IsNullOrWhiteSpace(documentHintBackfillQuery))
            {
                var (documentHintBackfillMatches, documentHintBackfillMs) = await flow.Session.Phases.MeasureAsync(
                    phaseName: "retrieval_document_hint_scoped_backfill",
                    retriever: "document_hint_scoped_backfill",
                    action: () => SearchDocumentHintScopedBackfillMatchesAsync(
                        resources.DataSource,
                        resources.Context.TenantId,
                        plan.Request.Query,
                        documentHintBackfillQuery,
                        plan.Scope.Category,
                        plan.Scope.CategoryPath,
                        Math.Max(plan.Selection.TopK, 8),
                        resources.Context.CancellationToken,
                        degradedRetrieverRef: flow.Session.Degradation.Mark,
                        commandTimeoutSeconds: plan.SparseCommandTimeoutSeconds),
                    getReturnedCount: static matches => matches.Count);
                flow.Session.Timings.SparsePhaseMs += documentHintBackfillMs;

                if (documentHintBackfillMatches.Count > 0)
                {
                    var mergedDocumentHintSelections = CalibrateFusedMatches(
                        documentHintBackfillQuery,
                        flow.Selection.Matches.Concat(documentHintBackfillMatches).ToList(),
                        plan.Request.Query);
                    mergedDocumentHintSelections = SuppressNavigationalNoise(plan.Request.Query, mergedDocumentHintSelections);
                    flow.Selection.Matches.Clear();
                    flow.Selection.Keys.Clear();
                    AddRankedMatchesDiagnosed(
                    flow.Session.Diagnostics, flow.Selection.Matches,
                                            flow.Selection.Keys,
                                            mergedDocumentHintSelections,
                                            plan.Selection.TopK,
                                            minScore: 0.0,
                                            Math.Max(plan.Selection.MaxPerDoc, 2),
                                            Math.Max(plan.Selection.MaxPerPage, 2),
                                            query: plan.Request.Query);

                    var documentHintTokens = ExtractDocumentHintTokens(plan.Request.Query);
                    var documentHintLinkedAnchors = documentHintBackfillMatches
                        .Where(match => DocumentMatchesHint(documentHintTokens, match))
                        .Take(3)
                        .ToList();
                    if (documentHintLinkedAnchors.Count > 0)
                    {
                        var (documentHintLinkedMatches, documentHintLinkedMs) = await flow.Session.Phases.MeasureAsync(
                            phaseName: "retrieval_document_hint_linked_context",
                            retriever: "linked_context",
                            action: () => SearchLinkedMatchesAsync(
                                resources.DataSource,
                                resources.Context.TenantId,
                                documentHintLinkedAnchors,
                                plan.Scope.Category,
                                docId: null,
                                docPath: null,
                                Math.Min(plan.Selection.TopK, 6),
                                resources.Context.CancellationToken,
                                plan.Scope.CategoryPath),
                            getReturnedCount: static matches => matches.Count);
                        flow.Session.Timings.LinkedPhaseMs += documentHintLinkedMs;

                        if (documentHintLinkedMatches.Count > 0)
                        {
                            var mergedLinkedDocumentHintSelections = CalibrateFusedMatches(
                                documentHintBackfillQuery,
                                flow.Selection.Matches.Concat(documentHintLinkedMatches).ToList(),
                                plan.Request.Query);
                            mergedLinkedDocumentHintSelections = SuppressNavigationalNoise(plan.Request.Query, mergedLinkedDocumentHintSelections);
                            flow.Selection.Matches.Clear();
                            flow.Selection.Keys.Clear();
                            AddRankedMatchesDiagnosed(
                            flow.Session.Diagnostics, flow.Selection.Matches,
                                                            flow.Selection.Keys,
                                                            mergedLinkedDocumentHintSelections,
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
    }
}
