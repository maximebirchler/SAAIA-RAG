using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalNavigation;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveLookupRoutes
{
    internal static Task<List<RagMatch>> SearchPreciseTitleRouteBackfillAsync(
        InteractiveRetrievalFlow flow, string titleBackfillQuery, int limit)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;
        if (ShouldSkipLocalTitleTokenRouteForIndexedTitleAnchors(titleBackfillQuery,
                hasCategoryScope: plan.Scope.HasCategoryFilter, hasDocScope: plan.Scope.HasDocScope,
                skipChunkRetrieversForDocumentOverview: plan.Routes.SkipChunkRetrieversForDocumentOverview,
                useScopedProfileFallback: plan.Routes.UseScopedProfileFallback))
        {
            return SearchTitleAnchorRouteMatchesAsync(resources.DataSource, resources.Context.TenantId,
                titleBackfillQuery, plan.Scope.Category, plan.Request.DocId, plan.Request.DocPath, limit,
                resources.Context.CancellationToken, plan.Scope.CategoryPath,
                degradedRetrieverRef: flow.Session.Degradation.Mark, allowDirectChunkRoute: false);
        }
        return SearchLocalTitleTokenRouteMatchesAsync(resources.DataSource, resources.Context.TenantId,
            titleBackfillQuery, plan.Scope.Category, plan.Request.DocId, plan.Request.DocPath, limit,
            resources.Context.CancellationToken, plan.Scope.CategoryPath,
            degradedRetrieverRef: flow.Session.Degradation.Mark);
    }

    internal static Task<(List<RagMatch> Result, long DurationMs)> SearchDocumentProfilesMeasuredAsync(
        InteractiveRetrievalFlow flow, InteractivePrimaryChannels channels)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;
        return flow.Session.Phases.MeasureAsync("retrieval_document_profile", "document_profile",
            () => plan.Routes.SkipChunkRetrieversForDocumentOverview || plan.Routes.UseScopedProfileFallback
                ? SearchDocumentOverviewProfileMatchesAsync(resources.DataSource, resources.Context.TenantId,
                    plan.Query.RetrievalQuery, plan.Scope.Category, plan.Request.DocId, plan.Request.DocPath,
                    channels.DocumentProfileCandidateCount, resources.Context.CancellationToken, plan.Scope.CategoryPath,
                    plan.Routes.RequireDocumentOverviewProfileMatch, degradedRetrieverRef: flow.Session.Degradation.Mark,
                    commandTimeoutSeconds: plan.SparseCommandTimeoutSeconds)
                : SearchDocumentProfileMatchesAsync(resources.DataSource, resources.Context.TenantId,
                    plan.Query.RetrievalQuery, plan.Scope.Category, plan.Request.DocId, plan.Request.DocPath,
                    channels.DocumentProfileCandidateCount, resources.Context.CancellationToken, plan.Scope.CategoryPath,
                    degradedRetrieverRef: flow.Session.Degradation.Mark, commandTimeoutSeconds: plan.SparseCommandTimeoutSeconds),
            static matches => matches.Count);
    }
}
