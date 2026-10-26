using static SAAIA.Backend.Retrieval.RetrievalCandidateSignals;
using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalNavigation;
using static SAAIA.Backend.Retrieval.RetrievalQueryPolicy;
using static SAAIA.Backend.Retrieval.RetrievalSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractivePrimaryLookup
{
    internal static async Task<InteractivePrimaryChannels> SearchAsync(InteractiveRetrievalFlow flow)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;
        Task<(List<RagMatch> Result, long DurationMs)> titleAnchorRouteMatchesTask = flow.Seeds.EarlyTitleAnchorRouteMatches is not null
            ? Task.FromResult((flow.Seeds.EarlyTitleAnchorRouteMatches, flow.Seeds.EarlyTitleAnchorRouteMs))
            : flow.Session.Phases.MeasureOptionalAsync(
                phaseName: "retrieval_title_anchor_route",
                retriever: "title_anchor_route",
                enabled: !(plan.Routes.SkipChunkRetrieversForDocumentOverview
              || plan.Routes.UseScopedProfileFallback
              || plan.Scope.HasExplicitFileDocumentHint
              || (plan.Routes.SkipUnanchoredTitleAnchorRouteForBroadDiversity && !plan.Routes.AllowNavigationCatalogRouteForBroadExploration)),
                skipReason: "query_scope_policy",
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
                    degradedRetrieverRef: flow.Session.Degradation.Mark,
                    allowNavigationCatalogRoute: plan.Routes.AllowNavigationCatalogRouteForBroadExploration),
                whenSkipped: static () => new List<RagMatch>(),
                getReturnedCount: static matches => matches.Count);
        Task<(List<RagMatch> Result, long DurationMs)> sparseMatchesTask = flow.Session.Phases.MeasureOptionalAsync(
                phaseName: "retrieval_sparse",
                retriever: "sparse_bm25",
                enabled: !(plan.Routes.SkipChunkRetrieversForDocumentOverview
            || (plan.Routes.UseScopedProfileFallback && !plan.Routes.AllowSparseAssistForScopedProfileFallback)
            || (plan.Routes.SkipSparseRetrieverForBroadDiversity && !plan.Routes.AllowSparseAssistForScopedProfileFallback)
            || plan.Scope.HasExplicitFileDocumentHint
            || plan.Routes.UseChoiceBetweenLexicalRoute
            || plan.Routes.UseBareVsComparativeOperandRoute),
                skipReason: "query_scope_policy",
                action: () => SearchSparseMatchesAsync(
                    resources.DataSource,
                    resources.Context.TenantId,
                    plan.Query.LexicalRetrievalSurface,
                    plan.Scope.Category,
                    plan.Request.DocId,
                    plan.Request.DocPath,
                    plan.SqlHeavyCandidateLimit,
                    resources.Context.CancellationToken,
                    sparseMsRef: value => flow.Session.Timings.SparseMs = value,
                    lexicalExpansionQuery: plan.Query.RetrievalQuery,
                    categoryPath: plan.Scope.CategoryPath,
                    includeProfileCardMatches: !plan.Routes.SkipSparseProfileCardAssist && !plan.Query.UsesDelimitedLexicalSurface,
                    degradedRetrieverRef: flow.Session.Degradation.Mark,
                    commandTimeoutSeconds: plan.SparseCommandTimeoutSeconds),
                whenSkipped: static () => new List<RagMatch>(),
                getReturnedCount: static matches => matches.Count);
        Task<(List<RagMatch> Result, long DurationMs)> denseMatchesTask = flow.Session.Phases.MeasureOptionalAsync(
                phaseName: "retrieval_dense",
                retriever: "dense_qdrant",
                enabled: !(plan.Routes.SkipChunkRetrieversForDocumentOverview
            || plan.Routes.UseScopedProfileFallback
            || plan.Scope.HasExplicitFileDocumentHint),
                skipReason: "query_scope_policy",
                action: () => SearchDenseMatchesAsync(
                    resources.DataSource,
                    resources.HttpClients,
                    resources.Options,
                    resources.Context.TenantId,
                    plan.Query.RetrievalQueryNorm,
                    plan.Scope.Category,
                    plan.Request.DocId,
                    plan.Request.DocPath,
                    plan.Selection.Candidates,
                    resources.Context.CancellationToken,
                    teiMsRef: value => flow.Session.Timings.TeiMs = value,
                    qdrantMsRef: value => flow.Session.Timings.QdrantMs = value,
                    qdrantStatusRef: value => flow.Session.Timings.QdrantStatus = value,
                    categoryPath: plan.Scope.CategoryPath,
                    degradedRetrieverRef: flow.Session.Degradation.Mark,
                    teiGovernor: resources.Context.TeiGovernor,
                    logger: resources.Context.Logger),
                whenSkipped: static () => new List<RagMatch>(),
                getReturnedCount: static matches => matches.Count);
        var skipDocumentProfileSearchForPreciseLookup =
            !plan.Routes.IncludeResearchSurfaces
            &&
            !plan.Routes.SkipChunkRetrieversForDocumentOverview
            && !plan.Routes.UseScopedProfileFallback
            && !plan.Routes.UseChoiceBetweenLexicalRoute
            && (plan.Routes.UseBareVsComparativeOperandRoute
                || ShouldSkipDocumentProfileSearchForPreciseLookup(plan.Request.Query)
                || ShouldSkipDocumentProfileSearchForComparativeLookup(plan.Request.Query));
        var allowComparativeDocumentProfileAssist = ShouldAllowDocumentProfileAssistForComparativeLookup(plan.Request.Query);
        var allowResearchDocumentProfileAssist = plan.Routes.IncludeResearchSurfaces || allowComparativeDocumentProfileAssist;
        var documentProfileCandidateCount = ResolveDocumentProfileCandidateCount(
            plan.Selection.Candidates,
            plan.Selection.TopK,
            plan.Routes.SkipChunkRetrieversForDocumentOverview || plan.Routes.UseScopedProfileFallback,
            allowResearchDocumentProfileAssist);
        var canSearchDocumentProfiles = plan.Routes.IncludeResearchSurfaces
            || (!skipDocumentProfileSearchForPreciseLookup
            && (allowComparativeDocumentProfileAssist || !ShouldSkipDocumentProfileSearchForQuantityLookup(plan.Request.Query))
            && (plan.Routes.SkipChunkRetrieversForDocumentOverview
                || plan.Routes.UseScopedProfileFallback
                || plan.Routes.UseChoiceBetweenLexicalRoute
                || !ShouldSkipDocumentProfileSearchForLowCostBroadQuery(plan.Request.Query, plan.Routes.Mode)));
        var deferDocumentProfileSearch = !plan.Routes.IncludeResearchSurfaces && !allowComparativeDocumentProfileAssist && ShouldDeferDocumentProfileSearch(
            canSearchDocumentProfiles,
            plan.Routes.SkipChunkRetrieversForDocumentOverview,
            plan.Routes.UseScopedProfileFallback,
            plan.Routes.AllowSparseAssistForScopedProfileFallback);
        Task<(List<RagMatch> Result, long DurationMs)> profileMatchesTask = SearchDocumentProfilesMeasuredAsync(
            enabled: canSearchDocumentProfiles && !deferDocumentProfileSearch && !plan.Scope.HasExplicitFileDocumentHint);
        Task<(List<RagMatch> Result, long DurationMs)> explicitDocumentScopedMatchesTask = flow.Session.Phases.MeasureOptionalAsync(
                phaseName: "retrieval_explicit_document_scoped_backfill",
                retriever: "document_hint_scoped_backfill",
                enabled: flow.Seeds.UseExplicitDocumentScopedBackfill,
                skipReason: "no_scoped_file_backfill_required",
                action: () => SearchDocumentHintScopedBackfillMatchesAsync(
                    resources.DataSource,
                    resources.Context.TenantId,
                    plan.Request.Query,
                    flow.Seeds.ExplicitDocumentScopedBackfillQuery,
                    plan.Scope.Category,
                    plan.Scope.CategoryPath,
                    Math.Max(plan.Selection.TopK, 8),
                    resources.Context.CancellationToken,
                    degradedRetrieverRef: flow.Session.Degradation.Mark,
                    includeProfileCardMatches: false,
                    commandTimeoutSeconds: plan.SparseCommandTimeoutSeconds),
                whenSkipped: static () => new List<RagMatch>(),
                getReturnedCount: static matches => matches.Count);

        Task<(List<RagMatch> Result, long DurationMs)> SearchDocumentProfilesMeasuredAsync(bool enabled = true)
            => flow.Session.Phases.MeasureOptionalAsync(
                phaseName: "retrieval_document_profile",
                retriever: "document_profile",
                enabled: enabled,
                skipReason: "query_policy_or_deferred_profile",
                action: () => plan.Routes.SkipChunkRetrieversForDocumentOverview || plan.Routes.UseScopedProfileFallback
                    ? SearchDocumentOverviewProfileMatchesAsync(
                        resources.DataSource,
                        resources.Context.TenantId,
                        plan.Query.RetrievalQuery,
                        plan.Scope.Category,
                        plan.Request.DocId,
                        plan.Request.DocPath,
                        documentProfileCandidateCount,
                        resources.Context.CancellationToken,
                        plan.Scope.CategoryPath,
                        plan.Routes.RequireDocumentOverviewProfileMatch,
                        degradedRetrieverRef: flow.Session.Degradation.Mark,
                        commandTimeoutSeconds: plan.SparseCommandTimeoutSeconds)
                    : SearchDocumentProfileMatchesAsync(
                        resources.DataSource,
                        resources.Context.TenantId,
                        plan.Query.RetrievalQuery,
                        plan.Scope.Category,
                        plan.Request.DocId,
                        plan.Request.DocPath,
                        documentProfileCandidateCount,
                        resources.Context.CancellationToken,
                        plan.Scope.CategoryPath,
                        degradedRetrieverRef: flow.Session.Degradation.Mark,
                        commandTimeoutSeconds: plan.SparseCommandTimeoutSeconds),
                whenSkipped: static () => new List<RagMatch>(),
                getReturnedCount: static matches => matches.Count);

        await Task.WhenAll(
            titleAnchorRouteMatchesTask,
            sparseMatchesTask,
            denseMatchesTask,
            profileMatchesTask,
            explicitDocumentScopedMatchesTask);

        var (titleAnchorRouteMatches, measuredTitleAnchorRoutePhaseMs) = await titleAnchorRouteMatchesTask;
        var (sparseMatches, measuredSparsePhaseMs) = await sparseMatchesTask;
        var (denseMatches, measuredDensePhaseMs) = await denseMatchesTask;
        var (profileMatches, measuredProfilePhaseMs) = await profileMatchesTask;
        var (explicitDocumentScopedMatches, measuredExplicitDocumentScopedMs) = await explicitDocumentScopedMatchesTask;
        var effectiveSparseMatches = explicitDocumentScopedMatches.Count == 0
            ? sparseMatches
            : sparseMatches.Concat(explicitDocumentScopedMatches).ToList();
        effectiveSparseMatches = PreferRequestedPageWindowMatches(effectiveSparseMatches, plan.Scope.RequestedPageStart, plan.Scope.RequestedPageEnd);
        var profileTitleOnlySparseMatches = !canSearchDocumentProfiles
            ? effectiveSparseMatches
                .Where(match => ShouldRecoverDocumentProfileForProfileTitleOnlySparseMatch(plan.Request.Query, match))
                .ToList()
            : [];
        if (profileTitleOnlySparseMatches.Count > 0)
        {
            var (recoveredProfileMatches, recoveredProfileMs) = await SearchDocumentProfilesMeasuredAsync();
            measuredProfilePhaseMs += recoveredProfileMs;
            recoveredProfileMatches = recoveredProfileMatches
                .Where(profile => profileTitleOnlySparseMatches.Any(sparse => IsSameDocument(profile, sparse)))
                .ToList();

            if (recoveredProfileMatches.Count > 0)
            {
                profileMatches = recoveredProfileMatches;
                effectiveSparseMatches = effectiveSparseMatches
                    .Where(match => !ShouldRecoverDocumentProfileForProfileTitleOnlySparseMatch(plan.Request.Query, match)
                        || !recoveredProfileMatches.Any(profile => IsSameDocument(profile, match)))
                    .ToList();
                flow.Session.Diagnostics.CapturePhase(
                    "profile_title_only_recovery",
                    "document_profile",
                    recoveredProfileMs,
                    recoveredProfileMatches);
            }
        }
        var effectiveDenseMatches = PreferRequestedPageWindowMatches(denseMatches, plan.Scope.RequestedPageStart, plan.Scope.RequestedPageEnd);
        var combinedTitleAnchorRouteMatches = flow.Seeds.QuotedTitleAnchorMatches.Count == 0
            ? titleAnchorRouteMatches
            : DeduplicatePreciseTitleBackfillMatches(flow.Seeds.QuotedTitleAnchorMatches.Concat(titleAnchorRouteMatches)).ToList();
        combinedTitleAnchorRouteMatches = PreferRequestedPageWindowMatches(combinedTitleAnchorRouteMatches, plan.Scope.RequestedPageStart, plan.Scope.RequestedPageEnd);
        flow.Selection.ReusablePreciseTitleCandidates = BuildReusablePreciseTitleRouteMatches(combinedTitleAnchorRouteMatches);
        flow.Session.Timings.TitleAnchorRoutePhaseMs += measuredTitleAnchorRoutePhaseMs;
        flow.Session.Timings.SparsePhaseMs = measuredSparsePhaseMs + measuredExplicitDocumentScopedMs;
        flow.Session.Timings.DensePhaseMs = measuredDensePhaseMs;
        flow.Session.Timings.ProfilePhaseMs = measuredProfilePhaseMs;
        flow.Session.Diagnostics.CapturePhase("title_anchor_route_broad", "title_anchor_route", measuredTitleAnchorRoutePhaseMs, titleAnchorRouteMatches);
        flow.Session.Diagnostics.CapturePhase("sparse_bm25", "sparse_bm25", flow.Session.Timings.SparsePhaseMs, effectiveSparseMatches);
        flow.Session.Diagnostics.CapturePhase("dense_qdrant", "dense_qdrant", flow.Session.Timings.DensePhaseMs, effectiveDenseMatches);
        flow.Session.Diagnostics.CapturePhase("document_profile", "document_profile", flow.Session.Timings.ProfilePhaseMs, profileMatches);
        return new(effectiveSparseMatches, effectiveDenseMatches, profileMatches, combinedTitleAnchorRouteMatches,
        deferDocumentProfileSearch, documentProfileCandidateCount, allowComparativeDocumentProfileAssist,
        ShouldPrioritizeDocumentProfilesForSelection(plan.Selection.PreferDocumentDiversity,
            plan.Routes.AllowSparseAssistForScopedProfileFallback || plan.Routes.AllowSparseAssistForBroadDiversity,
            allowComparativeDocumentProfileAssist));
    }
}
