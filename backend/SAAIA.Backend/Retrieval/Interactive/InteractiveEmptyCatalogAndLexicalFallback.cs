using System.Diagnostics;
using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalLexicalExpansion;
using static SAAIA.Backend.Retrieval.RetrievalQueryPolicy;
using static SAAIA.Backend.Retrieval.RetrievalSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveEmptyCatalogAndLexicalFallback
{
    internal static async Task ExecuteAsync(InteractiveRetrievalFlow flow)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        var useScopedCatalogEmptySelectionRecovery = ShouldRunScopedCatalogEmptySelectionRecovery(plan.Request.Query, plan.Scope.HasCategoryFilter, plan.Routes.Mode);
        if (flow.Selection.Matches.Count == 0
            && ((plan.Routes.SkipChunkRetrieversForDocumentOverview && ShouldUseCatalogDocumentOverviewFallback(plan.Request.Query))
                || plan.Routes.UseScopedCatalogDocumentOverviewFallback
                || useScopedCatalogEmptySelectionRecovery))
        {
            var (catalogProfiles, catalogProfilesMs) = await flow.Session.Phases.MeasureAsync(
                phaseName: "retrieval_catalog_document_overview_fallback",
                retriever: "document_profile_catalog",
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
            flow.Session.Timings.ProfilePhaseMs += catalogProfilesMs;

            var calibratedCatalogProfiles = CalibrateFusedMatches(
                InteractiveRetrievalPlan.CatalogOverviewFallbackQuery,
                catalogProfiles,
                plan.Request.Query);
            var catalogProfileSelections = useScopedCatalogEmptySelectionRecovery
                ? catalogProfiles
                : calibratedCatalogProfiles;
            var selectionSw = Stopwatch.StartNew();
            AddRankedMatchesDiagnosed(
            flow.Session.Diagnostics, flow.Selection.Matches,
                            flow.Selection.Keys,
                            catalogProfileSelections,
                            plan.Selection.TopK,
                            minScore: 0.0,
                            maxPerDoc: 1,
                            maxPerPage: Math.Max(plan.Selection.MaxPerPage, 1),
                            prioritizeDocumentProfiles: true,
                            query: plan.Query.SelectionQuery);
            selectionSw.Stop();
            flow.Session.Timings.SelectionMs += selectionSw.ElapsedMilliseconds;
        }

        if (flow.Selection.Matches.Count == 0)
        {
            var standardReferenceBackfillPhrases = ExtractStandardReferenceTitleLookupPhrases(plan.Request.Query);
            if (standardReferenceBackfillPhrases.Count > 0)
            {
                async Task<List<RagMatch>> SearchStandardReferenceTitleBackfillMatchesAsync()
                {
                    var backfill = new List<RagMatch>();
                    foreach (var phrase in standardReferenceBackfillPhrases.Take(4))
                    {
                        var titleAnchorQuery = phrase.Contains('"', StringComparison.Ordinal)
                            ? phrase
                            : $"\"{phrase}\"";
                        backfill.AddRange(await SearchTitleAnchorRouteMatchesAsync(
                            resources.DataSource,
                            resources.Context.TenantId,
                            titleAnchorQuery,
                            plan.Scope.Category,
                            plan.Request.DocId,
                            plan.Request.DocPath,
                            Math.Min(plan.Selection.Candidates, Math.Max(plan.Selection.TopK, 16)),
                            resources.Context.CancellationToken,
                            plan.Scope.CategoryPath,
                            degradedRetrieverRef: flow.Session.Degradation.Mark));
                    }

                    backfill.AddRange(await SearchStandardReferenceDocumentNameContentMatchesAsync(
                        resources.DataSource,
                        resources.Context.TenantId,
                        plan.Request.Query,
                        standardReferenceBackfillPhrases,
                        plan.Scope.Category,
                        plan.Request.DocId,
                        plan.Request.DocPath,
                        Math.Min(plan.Selection.Candidates, Math.Max(plan.Selection.TopK, 16)),
                        resources.Context.CancellationToken,
                        plan.Scope.CategoryPath,
                        degradedRetrieverRef: flow.Session.Degradation.Mark));
                    backfill.AddRange(await SearchStandardReferenceDocumentNameProfileMatchesAsync(
                        resources.DataSource,
                        resources.Context.TenantId,
                        plan.Request.Query,
                        standardReferenceBackfillPhrases,
                        plan.Scope.Category,
                        plan.Request.DocId,
                        plan.Request.DocPath,
                        Math.Min(plan.Selection.Candidates, Math.Max(plan.Selection.TopK, 16)),
                        resources.Context.CancellationToken,
                        plan.Scope.CategoryPath,
                        degradedRetrieverRef: flow.Session.Degradation.Mark,
                        commandTimeoutSeconds: plan.SparseCommandTimeoutSeconds));

                    return DeduplicatePreciseTitleBackfillMatches(backfill);
                }

                var (standardReferenceTitleBackfillMatches, standardReferenceTitleBackfillMs) = await flow.Session.Phases.MeasureAsync(
                    phaseName: "retrieval_standard_reference_title_backfill",
                    retriever: "title_anchor_route",
                    action: SearchStandardReferenceTitleBackfillMatchesAsync,
                    getReturnedCount: static matches => matches.Count);
                flow.Session.Timings.TitleAnchorRoutePhaseMs += standardReferenceTitleBackfillMs;

                if (standardReferenceTitleBackfillMatches.Count > 0)
                {
                    var calibratedStandardReferenceBackfill = SuppressNavigationalNoise(
                        plan.Request.Query,
                        CalibrateFusedMatches(plan.Request.Query, standardReferenceTitleBackfillMatches, plan.Request.Query));
                    var selectionSw = Stopwatch.StartNew();
                    AddRankedMatchesDiagnosed(
                    flow.Session.Diagnostics, flow.Selection.Matches,
                                            flow.Selection.Keys,
                                            calibratedStandardReferenceBackfill,
                                            plan.Selection.TopK,
                                            minScore: 0.0,
                                            Math.Max(plan.Selection.MaxPerDoc, 2),
                                            Math.Max(plan.Selection.MaxPerPage, 2),
                                            query: plan.Query.SelectionQuery);
                    selectionSw.Stop();
                    flow.Session.Timings.SelectionMs += selectionSw.ElapsedMilliseconds;
                }
            }
        }
    }
}
