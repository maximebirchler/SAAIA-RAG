using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalLexicalExpansion;
using static SAAIA.Backend.Retrieval.RetrievalNavigation;
using static SAAIA.Backend.Retrieval.RetrievalResultProjector;
using static SAAIA.Backend.Retrieval.RetrievalSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveFinalPreciseTitleBackfill
{
    internal static async Task ExecuteAsync(InteractiveRetrievalFlow flow)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        IReadOnlyList<string> finalPreciseBackfillPhrases = plan.Routes.SkipPreciseTitleLookupForDocumentOverview
                    ? Array.Empty<string>()
                    : ExtractTitleLookupPhrases(plan.Request.Query);
        var finalHasSufficientPreciseContentSelection = finalPreciseBackfillPhrases.Count > 0
            && HasSufficientPreciseContentSelection(plan.Request.Query, finalPreciseBackfillPhrases, flow.Selection.Matches);
        if (!plan.Routes.UseScopedProfileFallback
            && finalPreciseBackfillPhrases.Count > 0
            && flow.Selection.Matches.Count > 0
            && !finalHasSufficientPreciseContentSelection)
        {
            async Task<List<RagMatch>> SearchFinalPreciseTitleBackfillMatchesAsync()
            {
                var backfill = BuildReusablePreciseTitleBackfillMatches(
                    plan.Request.Query,
                    finalPreciseBackfillPhrases,
                    flow.Selection.ReusablePreciseTitleCandidates);
                var reusableBackfillIsSufficient = HasReusablePreciseTitleBackfillSelection(
                        plan.Request.Query,
                        finalPreciseBackfillPhrases,
                        flow.Selection.Matches,
                        backfill);
                if (reusableBackfillIsSufficient
                    && !ShouldContinuePreciseTitleBackfillAfterReusableSelection(
                        plan.Request.Query,
                        finalPreciseBackfillPhrases,
                        backfill))
                {
                    return DeduplicatePreciseTitleBackfillMatches(backfill);
                }

                var scopedDocCandidates = flow.Selection.Matches
                    .Take(8)
                    .Where(static match => !IsDocumentProfileMatch(match))
                    .Where(static match => IsContentSelectionCandidate(match))
                    .Where(static match => !string.IsNullOrWhiteSpace(match.DocPath))
                    .GroupBy(static match => NormalizeRagDocPath(match.DocPath), StringComparer.OrdinalIgnoreCase)
                    .Select(static group => group.First())
                    .Take(3)
                    .ToList();

                foreach (var phrase in finalPreciseBackfillPhrases.Take(3))
                {
                    var titleBackfillQuery = phrase.Contains('"', StringComparison.Ordinal)
                        ? phrase
                        : $"\"{phrase}\"";
                    var preferIndexedTitleBackfill = ShouldSkipLocalTitleTokenRouteForIndexedTitleAnchors(
                        titleBackfillQuery,
                        hasCategoryScope: plan.Scope.HasCategoryFilter,
                        hasDocScope: false,
                        skipChunkRetrieversForDocumentOverview: plan.Routes.SkipChunkRetrieversForDocumentOverview,
                        plan.Routes.UseScopedProfileFallback);

                    var scopedSearchTasks = scopedDocCandidates
                        .Select(scopedDoc => preferIndexedTitleBackfill
                            ? SearchTitleAnchorRouteMatchesAsync(
                                resources.DataSource,
                                resources.Context.TenantId,
                                titleBackfillQuery,
                                plan.Scope.Category,
                                docId: null,
                                scopedDoc.DocPath,
                                Math.Min(plan.Selection.TopK, 8),
                                resources.Context.CancellationToken,
                                plan.Scope.CategoryPath,
                                degradedRetrieverRef: flow.Session.Degradation.Mark)
                            : SearchLocalTitleTokenRouteMatchesAsync(
                                resources.DataSource,
                                resources.Context.TenantId,
                                titleBackfillQuery,
                                plan.Scope.Category,
                                docId: null,
                                scopedDoc.DocPath,
                                Math.Min(plan.Selection.TopK, 8),
                                resources.Context.CancellationToken,
                                plan.Scope.CategoryPath,
                                degradedRetrieverRef: flow.Session.Degradation.Mark))
                        .ToArray();
                    foreach (var scopedMatches in await Task.WhenAll(scopedSearchTasks))
                    {
                        backfill.AddRange(scopedMatches);
                    }

                    reusableBackfillIsSufficient = HasReusablePreciseTitleBackfillSelection(
                            plan.Request.Query,
                            finalPreciseBackfillPhrases,
                            flow.Selection.Matches,
                            backfill);
                    if (reusableBackfillIsSufficient
                        && !ShouldContinuePreciseTitleBackfillAfterReusableSelection(
                            plan.Request.Query,
                            finalPreciseBackfillPhrases,
                            backfill))
                    {
                        continue;
                    }

                    var globalMatches = await InteractiveLookupRoutes.SearchPreciseTitleRouteBackfillAsync(
flow, titleBackfillQuery,
                        Math.Min(Math.Max(plan.Selection.TopK, 12), 24));
                    backfill.AddRange(globalMatches);
                }

                return DeduplicatePreciseTitleBackfillMatches(backfill);
            }

            var (finalPreciseTitleBackfillMatches, finalPreciseTitleBackfillMs) = await flow.Session.Phases.MeasureAsync(
                phaseName: "retrieval_final_precise_title_backfill",
                retriever: "local_title_token_route",
                action: SearchFinalPreciseTitleBackfillMatchesAsync,
                getReturnedCount: static matches => matches.Count);
            flow.Session.Timings.LocalTitleTokenMs += finalPreciseTitleBackfillMs;

            if (finalPreciseTitleBackfillMatches.Count > 0)
            {
                var mergedFinalSelections = CalibrateFusedMatches(
                    plan.Query.RetrievalQuery,
                    flow.Selection.Matches.Concat(finalPreciseTitleBackfillMatches).ToList(),
                    plan.Request.Query);
                mergedFinalSelections = PrioritizePreciseTitleBackfillSelections(
                    plan.Request.Query,
                    mergedFinalSelections,
                    finalPreciseTitleBackfillMatches);
                mergedFinalSelections = SuppressNavigationalNoise(plan.Request.Query, mergedFinalSelections);
                flow.Selection.Matches.Clear();
                flow.Selection.Keys.Clear();
                AddRankedMatchesDiagnosed(
                flow.Session.Diagnostics, flow.Selection.Matches,
                                    flow.Selection.Keys,
                                    mergedFinalSelections,
                                    plan.Selection.TopK,
                                    minScore: 0.0,
                                    Math.Max(plan.Selection.MaxPerDoc, 2),
                                    Math.Max(plan.Selection.MaxPerPage, 2),
                                    query: plan.Request.Query);
            }
        }
    }
}
