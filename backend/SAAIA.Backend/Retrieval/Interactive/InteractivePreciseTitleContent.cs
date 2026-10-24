using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalLexicalExpansion;
using static SAAIA.Backend.Retrieval.RetrievalNavigation;
using static SAAIA.Backend.Retrieval.RetrievalResultProjector;
using static SAAIA.Backend.Retrieval.RetrievalSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractivePreciseTitleContent
{
    internal static async Task ExecuteAsync(InteractiveRetrievalFlow flow)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        IReadOnlyList<string> preciseBackfillPhrases = plan.Routes.SkipPreciseTitleLookupForDocumentOverview
                    ? Array.Empty<string>()
                    : ExtractTitleLookupPhrases(plan.Request.Query);
        var hasResolvedQuotedTitleSelection = preciseBackfillPhrases.Count > 0
            && flow.Selection.Matches.Any(match =>
                HasResolvedPreciseTitleSelection(preciseBackfillPhrases, match));
        var hasSufficientPreciseContentSelection = preciseBackfillPhrases.Count > 0
            && HasSufficientPreciseContentSelection(plan.Request.Query, preciseBackfillPhrases, flow.Selection.Matches);
        var forcePreciseTitleBackfill = !plan.Routes.UseScopedProfileFallback
            && preciseBackfillPhrases.Count > 0
            && flow.Selection.Matches.Count > 0
            && !hasSufficientPreciseContentSelection;
        if (forcePreciseTitleBackfill)
        {
            async Task<List<RagMatch>> SearchPreciseTitleBackfillMatchesAsync()
            {
                var backfill = BuildReusablePreciseTitleBackfillMatches(
                    plan.Request.Query,
                    preciseBackfillPhrases,
                    flow.Selection.ReusablePreciseTitleCandidates);
                var reusableBackfillIsSufficient = HasReusablePreciseTitleBackfillSelection(
                        plan.Request.Query,
                        preciseBackfillPhrases,
                        flow.Selection.Matches,
                        backfill);
                if (reusableBackfillIsSufficient
                    && !ShouldContinuePreciseTitleBackfillAfterReusableSelection(
                        plan.Request.Query,
                        preciseBackfillPhrases,
                        backfill))
                {
                    return DeduplicatePreciseTitleBackfillMatches(backfill);
                }

                var scopedDocCandidates = flow.Selection.Matches
                    .Take(8)
                    .Where(match =>
                        IsPreciseTitleBackfillScopeCandidate(preciseBackfillPhrases, match)
                        || (!hasResolvedQuotedTitleSelection
                            && !IsDocumentProfileMatch(match)
                            && match.Score >= 0.65
                            && IsContentSelectionCandidate(match)))
                    .Where(static match => !string.IsNullOrWhiteSpace(match.DocPath))
                    .GroupBy(static match => NormalizeRagDocPath(match.DocPath), StringComparer.OrdinalIgnoreCase)
                    .Select(static group => group.First())
                    .Take(3)
                    .ToList();
                foreach (var phrase in ExtractTitleLookupPhrases(plan.Request.Query).Take(3))
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
                            preciseBackfillPhrases,
                            flow.Selection.Matches,
                            backfill);
                    if (reusableBackfillIsSufficient
                        && !ShouldContinuePreciseTitleBackfillAfterReusableSelection(
                            plan.Request.Query,
                            preciseBackfillPhrases,
                            backfill))
                    {
                        continue;
                    }

                    var matches = await InteractiveLookupRoutes.SearchPreciseTitleRouteBackfillAsync(
flow, titleBackfillQuery,
                        Math.Min(plan.Selection.TopK, 8));
                    backfill.AddRange(matches);
                }

                return DeduplicatePreciseTitleBackfillMatches(backfill);
            }

            var (preciseTitleBackfillMatches, preciseTitleBackfillMs) = await flow.Session.Phases.MeasureAsync(
                phaseName: "retrieval_precise_title_backfill",
                retriever: "local_title_token_route",
                action: SearchPreciseTitleBackfillMatchesAsync,
                getReturnedCount: static matches => matches.Count);
            flow.Session.Timings.LocalTitleTokenMs += preciseTitleBackfillMs;

            if (preciseTitleBackfillMatches.Count > 0)
            {
                var mergedTitleSelections = CalibrateFusedMatches(
                    plan.Query.RetrievalQuery,
                    flow.Selection.Matches.Concat(preciseTitleBackfillMatches).ToList(),
                    plan.Request.Query);
                mergedTitleSelections = PrioritizePreciseTitleBackfillSelections(
                    plan.Request.Query,
                    mergedTitleSelections,
                    preciseTitleBackfillMatches);
                mergedTitleSelections = SuppressNavigationalNoise(plan.Request.Query, mergedTitleSelections);
                flow.Selection.Matches.Clear();
                flow.Selection.Keys.Clear();
                AddRankedMatchesDiagnosed(
                flow.Session.Diagnostics, flow.Selection.Matches,
                                        flow.Selection.Keys,
                                        mergedTitleSelections,
                                        plan.Selection.TopK,
                                        minScore: 0.0,
                                        Math.Max(plan.Selection.MaxPerDoc, 2),
                                        Math.Max(plan.Selection.MaxPerPage, 2),
                                        query: plan.Request.Query);
            }
        }
    }
}
