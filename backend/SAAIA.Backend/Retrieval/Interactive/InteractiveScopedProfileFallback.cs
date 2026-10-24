using System.Diagnostics;
using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalQueryPolicy;
using static SAAIA.Backend.Retrieval.RetrievalSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveScopedProfileFallback
{
    internal static async Task ExecuteAsync(InteractiveRetrievalFlow flow)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        if (!plan.Routes.SkipChunkRetrieversForDocumentOverview
                    && flow.Selection.Matches.Count == 0
                    && ShouldUseScopedProfileFallback(plan.Request.Query, plan.Scope.HasCategoryFilter, plan.Routes.Mode))
        {
            var (fallbackProfiles, fallbackProfilesMs) = await flow.Session.Phases.MeasureAsync(
                phaseName: "retrieval_scoped_profile_fallback",
                retriever: "document_profile_fallback",
                action: () => SearchDocumentOverviewProfileMatchesAsync(
                    resources.DataSource,
                    resources.Context.TenantId,
                    plan.Request.Query,
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
            flow.Session.Timings.ProfilePhaseMs += fallbackProfilesMs;

            var selectionSw = Stopwatch.StartNew();
            AddRankedMatchesDiagnosed(
            flow.Session.Diagnostics, flow.Selection.Matches,
                            flow.Selection.Keys,
                            fallbackProfiles,
                            plan.Selection.TopK,
                            minScore: 0.0,
                            maxPerDoc: 1,
                            maxPerPage: Math.Max(plan.Selection.MaxPerPage, 1),
                            query: plan.Request.Query);
            selectionSw.Stop();
            flow.Session.Timings.SelectionMs += selectionSw.ElapsedMilliseconds;
        }
    }
}
