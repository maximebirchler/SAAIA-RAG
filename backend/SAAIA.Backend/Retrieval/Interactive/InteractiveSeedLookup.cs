using static SAAIA.Backend.Retrieval.CanonicalRetrieval;
using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalLexicalExpansion;
using static SAAIA.Backend.Retrieval.RetrievalNavigation;
using static SAAIA.Backend.Retrieval.RetrievalQueryPolicy;
using static SAAIA.Backend.Retrieval.RetrievalSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveSeedLookup
{
    internal static async Task<InteractiveLookupSeeds> SearchAsync(InteractiveRetrievalSession session)
    {
        var resources = session.Resources;
        var plan = session.Plan;
        var phases = session.Phases;
        var degradation = session.Degradation;
        var diagnostics = session.Diagnostics;

        var (exactMatches, exactMs) = await phases.MeasureAsync(
            phaseName: "retrieval_exact_match",
            retriever: "exact_match",
            action: () => SearchSourceBackedCanonicalExactMatchesAsync(resources.DataSource, resources.Context.TenantId, plan.Request.Query, plan.Scope.Category, plan.Request.DocId, plan.Request.DocPath, ResolveExactMatchCandidateLimit(plan.Selection.TopK), resources.Context.CancellationToken, plan.Scope.CategoryPath),
            getReturnedCount: static matches => matches.Count);
        long titleAnchorRoutePhaseMs = 0;
        var (quotedTitleMatches, quotedTitleMs) = await phases.MeasureOptionalAsync(
            phaseName: "retrieval_quoted_title",
            retriever: "quoted_title",
            enabled: !plan.Routes.SkipPreciseTitleLookupForDocumentOverview && !plan.Routes.SkipUnquotedPreciseTitleScans && !plan.Routes.SkipQuotedTitleFullScan,
            skipReason: "query_policy",
            action: () => SearchQuotedTitleMatchesAsync(resources.DataSource, resources.Context.TenantId, plan.Request.Query, plan.Scope.Category, plan.Request.DocId, plan.Request.DocPath, plan.Selection.TopK, resources.Context.CancellationToken, plan.Scope.CategoryPath),
            whenSkipped: static () => new List<RagMatch>(),
            getReturnedCount: static matches => matches.Count);
        var (quotedTitleAnchorMatches, quotedTitleAnchorMs) = await phases.MeasureOptionalAsync(
            phaseName: "retrieval_quoted_title_anchor_route",
            retriever: "title_anchor_route",
            enabled: !plan.Routes.SkipPreciseTitleLookupForDocumentOverview && plan.Routes.HasQuotedLookupPhrases && plan.Scope.HasCategoryFilter && !plan.Scope.HasDocScope,
            skipReason: "query_scope_policy",
            action: async () =>
            {
                var anchorMatches = new List<RagMatch>();
                foreach (var phrase in ExtractTitleLookupPhrases(plan.Request.Query).Take(3))
                {
                    var titleAnchorQuery = phrase.Contains('"', StringComparison.Ordinal)
                        ? phrase
                        : $"\"{phrase}\"";
                    anchorMatches.AddRange(await SearchTitleAnchorRouteMatchesAsync(
                        resources.DataSource,
                        resources.Context.TenantId,
                        titleAnchorQuery,
                        plan.Scope.Category,
                        plan.Request.DocId,
                        plan.Request.DocPath,
                        Math.Min(plan.Selection.Candidates, Math.Max(plan.Selection.TopK, 16)),
                        resources.Context.CancellationToken,
                        plan.Scope.CategoryPath,
                        degradedRetrieverRef: degradation.Mark,
                        allowDirectChunkRoute: false));
                }

                return DeduplicatePreciseTitleBackfillMatches(anchorMatches)
                    .Take(Math.Min(plan.Selection.Candidates, Math.Max(plan.Selection.TopK, 16)))
                    .ToList();
            },
            whenSkipped: static () => new List<RagMatch>(),
            getReturnedCount: static matches => matches.Count);
        titleAnchorRoutePhaseMs += quotedTitleAnchorMs;
        var (explicitDocumentTitleMatches, explicitDocumentTitleMs) = await phases.MeasureOptionalAsync(
            phaseName: "retrieval_explicit_document_title",
            retriever: "explicit_document_title_route",
            enabled: plan.Scope.HasExplicitFileDocumentHint,
            skipReason: "no_explicit_file_hint",
            action: () => SearchExplicitDocumentTitleMatchesAsync(
                    resources.DataSource,
                    resources.Context.TenantId,
                    plan.Request.Query,
                    plan.Scope.Category,
                    plan.Request.DocId,
                    plan.Request.DocPath,
                    Math.Min(plan.Selection.Candidates, Math.Max(plan.Selection.TopK, 16)),
                    resources.Context.CancellationToken,
                    plan.Scope.CategoryPath,
                    degradedRetrieverRef: degradation.Mark),
            whenSkipped: static () => new List<RagMatch>(),
            getReturnedCount: static matches => matches.Count);
        titleAnchorRoutePhaseMs += explicitDocumentTitleMs;
        var (localTitleTokenMatches, localTitleTokenMs) = await phases.MeasureOptionalAsync(
            phaseName: "retrieval_local_title_token",
            retriever: "local_title_token_route",
            enabled: !plan.Routes.SkipPreciseTitleLookupForDocumentOverview && !plan.Routes.SkipLocalTitleTokenRoute,
            skipReason: "query_policy",
            action: () => SearchLocalTitleTokenRouteMatchesAsync(
                resources.DataSource,
                resources.Context.TenantId,
                plan.Request.Query,
                plan.Scope.Category,
                plan.Request.DocId,
                plan.Request.DocPath,
                ResolveLocalTitleTokenCandidateLimit(plan.Selection.TopK, plan.Scope.HasDocScope),
                resources.Context.CancellationToken,
                plan.Scope.CategoryPath,
                degradedRetrieverRef: degradation.Mark),
            whenSkipped: static () => new List<RagMatch>(),
            getReturnedCount: static matches => matches.Count);
        var titleLookupMatches = explicitDocumentTitleMatches
            .Concat(quotedTitleMatches)
            .Concat(quotedTitleAnchorMatches)
            .Concat(localTitleTokenMatches)
            .GroupBy(BuildMatchDedupKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderByDescending(match => match.Score)
                .ThenBy(match => match.DocPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(match => match.ChunkIndex)
                .First())
            .OrderByDescending(static match => match.Score)
            .ThenBy(static match => match.DocPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static match => match.ChunkIndex)
            .ToList();
        titleLookupMatches = PreferRequestedPageWindowMatches(titleLookupMatches, plan.Scope.RequestedPageStart, plan.Scope.RequestedPageEnd);
        diagnostics.CapturePhase("exact_match", "exact_match", exactMs, exactMatches);
        diagnostics.CapturePhase("quoted_title", "quoted_title", quotedTitleMs, quotedTitleMatches);
        diagnostics.CapturePhase("quoted_title_anchor", "title_anchor_route", quotedTitleAnchorMs, quotedTitleAnchorMatches);
        diagnostics.CapturePhase("explicit_document_title", "explicit_document_title_route", explicitDocumentTitleMs, explicitDocumentTitleMatches);
        diagnostics.CapturePhase("local_title_token", "local_title_token_route", localTitleTokenMs, localTitleTokenMatches);
        diagnostics.CapturePhase("title_lookup_combined", "title_lookup", null, titleLookupMatches);
        var explicitDocumentScopedBackfillQuery = plan.Scope.HasExplicitFileDocumentHint
            ? BuildDocumentHintScopedBackfillQuery(plan.Request.Query, plan.Query.RetrievalQuery)
            : string.Empty;
        var explicitDocumentTitleRouteReturnedContent = plan.Scope.HasExplicitFileDocumentHint
            && explicitDocumentTitleMatches.Count > 0;
        var useExplicitDocumentScopedBackfill = ShouldUseExplicitDocumentScopedBackfillRoute(
            plan.Request.Query,
            explicitDocumentScopedBackfillQuery)
            && !explicitDocumentTitleRouteReturnedContent;

        var shortCircuitAfterExact = ShouldShortCircuitAfterExact(plan.Request.Query, exactMatches);

        List<RagMatch>? earlyTitleAnchorRouteMatches = null;

        long earlyTitleAnchorRouteMs = 0;
        var shortCircuitAfterTitleAnchorRoute = false;
        if (!shortCircuitAfterExact

            && ShouldProbeUnquotedTitleAnchorRoute(plan.Request.Query, plan.Routes.SkipChunkRetrieversForDocumentOverview, plan.Routes.UseScopedProfileFallback))
        {
            var (probedTitleAnchorRouteMatches, measuredTitleAnchorRouteMs) = await phases.MeasureAsync(
                phaseName: "retrieval_title_anchor_route",
                retriever: "title_anchor_route",
                action: () => SearchTitleAnchorRouteMatchesAsync(
                    resources.DataSource,
                    resources.Context.TenantId,
                    plan.Request.Query,
                    plan.Scope.Category,
                    plan.Request.DocId,
                    plan.Request.DocPath,
                    Math.Min(plan.Selection.Candidates, Math.Max(plan.Selection.TopK, 16)),
                    resources.Context.CancellationToken,
                    plan.Scope.CategoryPath,
                    degradedRetrieverRef: degradation.Mark,
                    allowNavigationCatalogRoute: plan.Routes.AllowNavigationCatalogRouteForBroadExploration),
                getReturnedCount: static matches => matches.Count);
            earlyTitleAnchorRouteMatches = probedTitleAnchorRouteMatches;
            earlyTitleAnchorRouteMs = measuredTitleAnchorRouteMs;
            shortCircuitAfterTitleAnchorRoute = ShouldShortCircuitAfterTitleAnchorRoute(plan.Request.Query, probedTitleAnchorRouteMatches);
        }
        session.Timings.ExactMs = exactMs;
        session.Timings.QuotedTitleMs = quotedTitleMs;
        session.Timings.LocalTitleTokenMs = localTitleTokenMs;
        session.Timings.TitleAnchorRoutePhaseMs = titleAnchorRoutePhaseMs;
        var route = shortCircuitAfterExact ? InteractiveInitialRoute.Exact
            : shortCircuitAfterTitleAnchorRoute && earlyTitleAnchorRouteMatches is not null
                ? InteractiveInitialRoute.TitleAnchor : InteractiveInitialRoute.Broad;
        return new(exactMatches, quotedTitleAnchorMatches, titleLookupMatches,
            explicitDocumentScopedBackfillQuery, useExplicitDocumentScopedBackfill,
            earlyTitleAnchorRouteMatches, earlyTitleAnchorRouteMs, route);
    }
}
