using static SAAIA.Backend.Retrieval.RetrievalAnswerGuidance;
using static SAAIA.Backend.Retrieval.RetrievalLexicalExpansion;
using static SAAIA.Backend.Retrieval.RetrievalMetadata;
using static SAAIA.Backend.Retrieval.RetrievalNavigation;
using static SAAIA.Backend.Retrieval.RetrievalQueryPolicy;
using static SAAIA.Backend.Retrieval.RetrievalSelection;
using static SAAIA.Backend.Retrieval.RetrievalTitleSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveRetrievalPlanner
{
    internal static async Task<InteractiveRetrievalPlan> CreateAsync(InteractiveRetrievalResources resources,
        RagSearchRequestDto request, InteractiveRetrievalTimings timings)
    {
        var req = request;

        const string catalogOverviewFallbackQuery = InteractiveRetrievalPlan.CatalogOverviewFallbackQuery;

        var repairedQuery = RepairLikelyMojibakeText(req.Query);
        if (!string.Equals(repairedQuery, req.Query, StringComparison.Ordinal))
            req = req with { Query = repairedQuery };

        var topK = req.TopK ?? resources.Options.DefaultTopK;
        topK = Math.Clamp(topK, 1, resources.Options.MaxTopK);
        var requestedTopK = topK;

        var requestedCategory = string.IsNullOrWhiteSpace(req.Category)
            ? null
            : req.Category.Trim().ToLowerInvariant();
        var categoryPath = await ResolveRagCategoryPathAsync(resources.DataSource, resources.Context.TenantId, req.CategoryPath, req.CategoryRef, resources.Context.CancellationToken);
        var category = string.IsNullOrWhiteSpace(categoryPath)
            ? requestedCategory
            : null;
        var categoryQueryHint = requestedCategory
                                ?? NormalizeRagCategory(ExtractTopLevelCategoryPath(categoryPath));

        var hasDocScope = !string.IsNullOrWhiteSpace(req.DocId) || !string.IsNullOrWhiteSpace(req.DocPath);
        var hasExplicitFileDocumentHint = HasExplicitFileLookupIntent(req.Query);
        var useBareVsComparativeOperandRoute = ShouldUseBareVsComparativeOperandRoute(req.Query);
        var useChoiceBetweenLexicalRoute = ShouldUseChoiceBetweenLexicalRoute(req.Query)
            && !hasDocScope
            && !hasExplicitFileDocumentHint;
        var retrievalQuery = useChoiceBetweenLexicalRoute
            ? BuildChoiceBetweenRetrievalQuery(req.Query)
            : ResolvePrimaryRetrievalQuery(req.Query, categoryQueryHint);
        retrievalQuery = ExpandGenericTechnicalRetrievalQuery(req.Query, retrievalQuery);
        var lexicalRetrievalSurface = ResolveLexicalRetrievalSurface(req.Query, retrievalQuery);
        var usesDelimitedLexicalSurface = !string.Equals(lexicalRetrievalSurface, req.Query, StringComparison.Ordinal);
        var queryNorm = NormalizeQuery(req.Query);
        var retrievalQueryNorm = NormalizeQuery(retrievalQuery);

        var includeResearchSurfaces = ShouldUseResearchSurfaces(req);
        var mode = ResolveEffectiveSearchMode(req.Mode, req.Query);
        if (includeResearchSurfaces && !string.Equals(mode, "focused", StringComparison.Ordinal))
            mode = "broad";
        topK = ResolveInternalSelectionTopK(
            req.Query,
            requestedTopK,
            resources.Options.MaxTopK,
            hasDocScope,
            hasExplicitFileDocumentHint,
            mode);
        if (usesDelimitedLexicalSurface)
            topK = Math.Min(Math.Max(topK, 16), resources.Options.MaxTopK);
        var preferItemInventory = ShouldPreferItemInventorySelection(req, mode, includeResearchSurfaces);
        var preferDocumentDiversity = (ShouldPreferDocumentDiversity(req.Query) || includeResearchSurfaces)
            && mode != "focused"
            && !preferItemInventory;
        double defMinScore = mode switch
        {
            "focused" => 0.35,
            "broad" => 0.15,
            _ => 0.25
        };
        int defCandidates = ResolveDefaultCandidateCount(mode, preferDocumentDiversity, topK);
        // CDC v3.1 §11.3: max 3 chunks per document default
        int defMaxPerDoc = ResolveDefaultMaxPerDoc(mode, preferDocumentDiversity, topK);

        var minScore = Math.Clamp(req.MinScore ?? defMinScore, 0.0, 1.0);
        var selectionMinScore = !req.MinScore.HasValue && ShouldRelaxDefaultScoreFloorForFocusedStructuredLookup(req.Query)
            ? 0.0
            : minScore;
        var candidates = Math.Clamp(req.Candidates ?? defCandidates, topK, Math.Max(topK, resources.Options.MaxTopK * 20));
        var maxPerDoc = Math.Clamp(req.MaxPerDoc ?? (preferItemInventory ? topK : defMaxPerDoc), 1, topK);
        var maxPerPage = Math.Clamp(req.MaxPerPage ?? (preferItemInventory ? Math.Min(topK, 16) : 1), 1, topK);
        var hasExplicitMaxPerDoc = req.MaxPerDoc.HasValue;
        var hasExplicitMaxPerPage = req.MaxPerPage.HasValue;
        var requestedPageStart = hasDocScope && req.PageStart is > 0
            ? req.PageStart.Value
            : (int?)null;
        var requestedPageEnd = requestedPageStart.HasValue
            ? Math.Max(requestedPageStart.Value, req.PageEnd is > 0 ? req.PageEnd.Value : requestedPageStart.Value)
            : (int?)null;
        var hasRequestedPageWindow = requestedPageStart.HasValue && requestedPageEnd.HasValue;

        if (req.Diversity != null)
        {
            if (req.Diversity.MaxChunksPerDoc.HasValue)
            {
                maxPerDoc = Math.Clamp(req.Diversity.MaxChunksPerDoc.Value, 1, topK);
                hasExplicitMaxPerDoc = true;
            }
            if (req.Diversity.PreferDistinctPages == true)
            {
                maxPerPage = 1;
                hasExplicitMaxPerPage = true;
            }
        }

        if (!hasExplicitMaxPerDoc && ShouldConstrainPreciseTitleLookup(req.Query))
            maxPerDoc = Math.Min(topK, Math.Max(maxPerDoc, 2));
        if (!hasExplicitMaxPerDoc && ShouldAllowMultipleChunksForComparativeSubject(req.Query))
            maxPerDoc = Math.Min(topK, Math.Max(maxPerDoc, 2));
        if (!hasExplicitMaxPerPage && ShouldConstrainPreciseTitleLookup(req.Query))
            maxPerPage = Math.Min(topK, Math.Max(maxPerPage, 2));
        if (hasRequestedPageWindow && !hasExplicitMaxPerDoc)
            maxPerDoc = Math.Min(topK, Math.Max(maxPerDoc, Math.Min(topK, 4)));
        if (hasRequestedPageWindow && !hasExplicitMaxPerPage)
            maxPerPage = Math.Min(topK, Math.Max(maxPerPage, 2));

        timings.Total.Start();
        var hasCategoryFilter = !string.IsNullOrWhiteSpace(category) || !string.IsNullOrWhiteSpace(categoryPath);
        var skipPreciseTitleLookupForDocumentOverview = ShouldSkipPreciseTitleLookupForDocumentOverview(
            req.Query,
            hasDocScope,
            mode);
        var skipChunkRetrieversForDocumentOverview = ShouldSkipChunkRetrieversForDocumentOverview(req.Query, hasDocScope, mode);
        var forceDocumentRoleProfileFallback = !hasDocScope
            && hasCategoryFilter
            && (ContainsDocumentRoleClassificationSurface(req.Query)
                || ContainsDelimitedDocumentRoleClassificationSurface(req.Query));
        var preferUnquotedTitleAnchorRoute = !hasDocScope
            && !forceDocumentRoleProfileFallback
            && ShouldProbeUnquotedTitleAnchorRoute(
                req.Query,
                skipChunkRetrieversForDocumentOverview,
                useScopedProfileFallback: false);
        var hasQuotedLookupPhrases = ExtractQuotedLookupPhrases(req.Query).Count > 0;
        var skipUnquotedPreciseTitleScans = preferUnquotedTitleAnchorRoute
            && !hasQuotedLookupPhrases;
        var skipUnquotedCategoryTitleScans = hasCategoryFilter
            && skipUnquotedPreciseTitleScans;
        var useScopedProfileFallback = !hasDocScope
            && (forceDocumentRoleProfileFallback
                || (!preferUnquotedTitleAnchorRoute
                    && ShouldUseScopedProfileFallback(req.Query, hasCategoryFilter, mode)));
        var useScopedCatalogDocumentOverviewFallback = !hasDocScope
            && hasCategoryFilter
            && ShouldUseScopedCatalogDocumentOverviewFallback(req.Query, mode);
        if (useScopedCatalogDocumentOverviewFallback)
        {
            retrievalQuery = catalogOverviewFallbackQuery;
            retrievalQueryNorm = NormalizeQuery(retrievalQuery);
        }
        var selectionQuery = useScopedCatalogDocumentOverviewFallback
            ? catalogOverviewFallbackQuery
            : usesDelimitedLexicalSurface
                ? lexicalRetrievalSurface
            : req.Query;

        var allowSparseAssistForScopedProfileFallback =
            useScopedProfileFallback && ShouldAllowSparseAssistForScopedProfileFallback(req.Query);
        var allowSparseAssistForBroadDiversity =
            !hasDocScope && (includeResearchSurfaces || ShouldAllowSparseAssistForBroadDiversity(req.Query));
        var allowNavigationCatalogRouteForBroadExploration =
            includeResearchSurfaces
            || ShouldAllowNavigationCatalogRouteForBroadExploration(req.Query, hasCategoryFilter || hasDocScope);
        var skipUnanchoredTitleAnchorRouteForBroadDiversity =
            !includeResearchSurfaces
            && (useChoiceBetweenLexicalRoute
                || ShouldSkipUnanchoredTitleAnchorRouteForBroadDiversity(req.Query));
        var skipSparseRetrieverForBroadDiversity = !hasDocScope
            && !includeResearchSurfaces
            && !allowSparseAssistForBroadDiversity
            && (ShouldSkipSparseRetrieverForBroadDiversity(req.Query, mode)
                || ShouldSkipSparseRetrieverForQuantityLookup(req.Query, mode));
        var skipSparseProfileCardAssist = !hasDocScope
            && !includeResearchSurfaces
            && ShouldSkipSparseProfileCardAssist(req.Query, mode);
        var requireDocumentOverviewProfileMatch = skipChunkRetrieversForDocumentOverview
            && ShouldRequireDocumentOverviewProfileMatch(req.Query);

        var skipLocalTitleTokenRoute = forceDocumentRoleProfileFallback
            || skipUnquotedCategoryTitleScans
            || skipUnquotedPreciseTitleScans
            || ShouldSkipLocalTitleTokenRouteForIndexedTitleAnchors(
            req.Query,
            hasCategoryScope: hasCategoryFilter,
            hasDocScope: hasDocScope,
            skipChunkRetrieversForDocumentOverview: skipChunkRetrieversForDocumentOverview,
            useScopedProfileFallback: useScopedProfileFallback);
        var skipQuotedTitleFullScan = ShouldSkipQuotedTitleFullScan(req.Query, hasDocScope);
        return new(req, new(requestedCategory, categoryPath, category, categoryQueryHint, hasDocScope, hasExplicitFileDocumentHint, requestedPageStart, requestedPageEnd, hasRequestedPageWindow, hasCategoryFilter),
        new(retrievalQuery, lexicalRetrievalSurface, usesDelimitedLexicalSurface, queryNorm, retrievalQueryNorm, selectionQuery, string.Equals(retrievalQuery.Trim(), req.Query.Trim(), StringComparison.OrdinalIgnoreCase) ? req.Query : retrievalQuery),
        new(topK, requestedTopK, minScore, selectionMinScore, candidates, maxPerDoc, maxPerPage, preferItemInventory, preferDocumentDiversity),
        new(useBareVsComparativeOperandRoute, useChoiceBetweenLexicalRoute, includeResearchSurfaces, mode, skipPreciseTitleLookupForDocumentOverview, skipChunkRetrieversForDocumentOverview, hasQuotedLookupPhrases, skipUnquotedPreciseTitleScans, useScopedProfileFallback, useScopedCatalogDocumentOverviewFallback, allowSparseAssistForScopedProfileFallback, allowSparseAssistForBroadDiversity, allowNavigationCatalogRouteForBroadExploration, skipUnanchoredTitleAnchorRouteForBroadDiversity, skipSparseRetrieverForBroadDiversity, skipSparseProfileCardAssist, requireDocumentOverviewProfileMatch, skipLocalTitleTokenRoute, skipQuotedTitleFullScan),
        new(ExtractDateAnchoredTitleLookupPhrases(req.Query), ExtractCompactDocumentCodeLookupPhrases(req.Query), ExtractDocumentStatusOperandLookupTerms(req.Query)),
        RagOptions.ResolveSearchSparseCommandTimeoutSeconds(resources.Options.SearchSparseCommandTimeoutSeconds),
        ResolveSqlHeavyRetrieverCandidateLimit(candidates, topK));
    }
}
