using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalLexicalExpansion;
using static SAAIA.Backend.Retrieval.RetrievalQueryPolicy;
using static SAAIA.Backend.Retrieval.RetrievalSelection;
using static SAAIA.Backend.Retrieval.RetrievalTitleSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveEmptyTitleAndProfileRecovery
{
    internal static async Task ExecuteAsync(InteractiveRetrievalFlow flow)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        if (flow.Selection.Matches.Count == 0 && !plan.Scope.HasExplicitFileDocumentHint)
        {
            var lateDocumentStatusOperandTerms = ExtractDocumentStatusOperandLookupTerms(plan.Request.Query);
            if (lateDocumentStatusOperandTerms.Count > 0)
            {
                var (lateDocumentStatusRecoveryMatches, lateDocumentStatusRecoveryMs) = await flow.Session.Phases.MeasureAsync(
                    phaseName: "retrieval_document_status_operand_empty_recovery",
                    retriever: "document_status_operand",
                    action: () => SearchDocumentStatusOperandContentMatchesAsync(
                        resources.DataSource,
                        resources.Context.TenantId,
                        plan.Request.Query,
                        lateDocumentStatusOperandTerms,
                        plan.Scope.Category,
                        plan.Request.DocId,
                        plan.Request.DocPath,
                        Math.Min(plan.Selection.Candidates, Math.Max(plan.Selection.TopK, 16)),
                        resources.Context.CancellationToken,
                        plan.Scope.CategoryPath,
                        degradedRetrieverRef: flow.Session.Degradation.Mark),
                    getReturnedCount: static matches => matches.Count);
                flow.Session.Timings.TitleAnchorRoutePhaseMs += lateDocumentStatusRecoveryMs;

                if (lateDocumentStatusRecoveryMatches.Count > 0)
                {
                    flow.Session.Timings.OrderingWatch.Restart();
                    var calibratedLateDocumentStatusRecovery = SuppressNavigationalNoise(
                        plan.Request.Query,
                        CalibrateFusedMatches(plan.Request.Query, lateDocumentStatusRecoveryMatches, plan.Request.Query));
                    AddRankedMatchesDiagnosed(
                    flow.Session.Diagnostics, flow.Selection.Matches,
                                            flow.Selection.Keys,
                                            calibratedLateDocumentStatusRecovery,
                                            plan.Selection.TopK,
                                            minScore: 0.0,
                                            maxPerDoc: Math.Max(plan.Selection.MaxPerDoc, 2),
                                            maxPerPage: Math.Max(plan.Selection.MaxPerPage, 2),
                                            query: plan.Request.Query);
                    PrioritizeFinalSelections(plan.Request.Query, flow.Selection.Matches);
                    RebuildSelectedKeys(flow.Selection.Matches, flow.Selection.Keys);
                    flow.Session.Timings.OrderingWatch.Stop();
                    flow.Session.Timings.SelectionMs += flow.Session.Timings.OrderingWatch.ElapsedMilliseconds;
                }
            }
        }

        if (flow.Selection.Matches.Count == 0
            && plan.Scope.HasCategoryFilter
            && ShouldUseGenericVersionConflictOverviewRecovery(plan.Request.Query))
        {
            var (genericVersionOverviewMatches, genericVersionOverviewMs) = await flow.Session.Phases.MeasureAsync(
                phaseName: "retrieval_generic_version_conflict_overview_recovery",
                retriever: "document_profile",
                action: () => SearchDocumentOverviewProfileMatchesAsync(
                    resources.DataSource,
                    resources.Context.TenantId,
                    InteractiveRetrievalPlan.CatalogOverviewFallbackQuery,
                    plan.Scope.Category,
                    plan.Request.DocId,
                    plan.Request.DocPath,
                    Math.Min(Math.Max(plan.Selection.TopK * 2, 12), 24),
                    resources.Context.CancellationToken,
                    plan.Scope.CategoryPath,
                    requireLexicalMatch: false,
                    degradedRetrieverRef: flow.Session.Degradation.Mark,
                    commandTimeoutSeconds: plan.SparseCommandTimeoutSeconds),
                getReturnedCount: static matches => matches.Count);
            flow.Session.Timings.ProfilePhaseMs += genericVersionOverviewMs;

            if (genericVersionOverviewMatches.Count > 0)
            {
                flow.Session.Timings.OrderingWatch.Restart();
                AddRankedMatchesDiagnosed(
                flow.Session.Diagnostics, flow.Selection.Matches,
                                    flow.Selection.Keys,
                                    CalibrateFusedMatches(InteractiveRetrievalPlan.CatalogOverviewFallbackQuery, genericVersionOverviewMatches, plan.Request.Query),
                                    plan.Selection.TopK,
                                    minScore: 0.0,
                                    maxPerDoc: 1,
                                    maxPerPage: Math.Max(plan.Selection.MaxPerPage, 1),
                                    query: plan.Request.Query);
                RebuildSelectedKeys(flow.Selection.Matches, flow.Selection.Keys);
                flow.Session.Timings.OrderingWatch.Stop();
                flow.Session.Timings.SelectionMs += flow.Session.Timings.OrderingWatch.ElapsedMilliseconds;
            }
        }

        if (plan.Scope.HasExplicitFileDocumentHint
            && !SelectionsCoverExplicitFileLookupGroups(plan.Request.Query, flow.Selection.Matches))
        {
            var (lateExplicitFileProfileMatches, lateExplicitFileProfileMs) = await flow.Session.Phases.MeasureAsync(
                phaseName: "retrieval_explicit_file_profile_late_recovery",
                retriever: "document_hint_profile_recovery",
                action: () => SearchExplicitFileHintProfileRecoveryMatchesAsync(
                    resources.DataSource,
                    resources.Context.TenantId,
                    plan.Request.Query,
                    plan.Scope.Category,
                    plan.Scope.CategoryPath,
                    Math.Min(Math.Max(plan.Selection.TopK, 8), 16),
                    resources.Context.CancellationToken,
                    degradedRetrieverRef: flow.Session.Degradation.Mark,
                    commandTimeoutSeconds: plan.SparseCommandTimeoutSeconds),
                getReturnedCount: static matches => matches.Count);
            flow.Session.Timings.ProfilePhaseMs += lateExplicitFileProfileMs;

            if (lateExplicitFileProfileMatches.Count > 0)
            {
                flow.Session.Timings.OrderingWatch.Restart();
                flow.Selection.Matches.Clear();
                flow.Selection.Keys.Clear();
                AddRankedMatchesDiagnosed(
                flow.Session.Diagnostics, flow.Selection.Matches,
                                    flow.Selection.Keys,
                                    CalibrateFusedMatches(plan.Request.Query, lateExplicitFileProfileMatches, plan.Request.Query),
                                    plan.Selection.TopK,
                                    minScore: 0.0,
                                    maxPerDoc: Math.Max(plan.Selection.MaxPerDoc, 2),
                                    maxPerPage: Math.Max(plan.Selection.MaxPerPage, 1),
                                    query: plan.Request.Query);
                RebuildSelectedKeys(flow.Selection.Matches, flow.Selection.Keys);
                flow.Session.Timings.OrderingWatch.Stop();
                flow.Session.Timings.SelectionMs += flow.Session.Timings.OrderingWatch.ElapsedMilliseconds;
            }
        }
    }
}
