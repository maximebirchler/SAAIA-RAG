using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalLexicalExpansion;
using static SAAIA.Backend.Retrieval.RetrievalNavigation;
using static SAAIA.Backend.Retrieval.RetrievalQueryPolicy;
using static SAAIA.Backend.Retrieval.RetrievalSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveFileAndCompanionRecovery
{
    internal static async Task ExecuteAsync(InteractiveRetrievalFlow flow)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        var explicitFileLookupTokens = ExtractExplicitFileLookupPhrases(plan.Request.Query);
        if (!ContainsDocumentOverviewIntent(plan.Request.Query)
            && explicitFileLookupTokens.Count > 0
            && !SelectionsCoverExplicitFileLookupGroups(plan.Request.Query, flow.Selection.Matches))
        {
            var (explicitDocumentRecoveryMatches, explicitDocumentRecoveryMs) = await flow.Session.Phases.MeasureAsync(
                phaseName: "retrieval_explicit_document_title_recovery",
                retriever: "explicit_document_title_route",
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
                    degradedRetrieverRef: flow.Session.Degradation.Mark),
                getReturnedCount: static matches => matches.Count);
            flow.Session.Timings.TitleAnchorRoutePhaseMs += explicitDocumentRecoveryMs;
            if (explicitDocumentRecoveryMatches.Count > 0)
            {
                var mergedExplicitSelections = CalibrateFusedMatches(
                    plan.Request.Query,
                    explicitDocumentRecoveryMatches.Concat(flow.Selection.Matches).ToList(),
                    plan.Request.Query);
                flow.Selection.Matches.Clear();
                flow.Selection.Keys.Clear();
                AddRankedMatchesDiagnosed(
                flow.Session.Diagnostics, flow.Selection.Matches,
                                    flow.Selection.Keys,
                                    mergedExplicitSelections,
                                    plan.Selection.TopK,
                                    minScore: 0.0,
                                    Math.Min(plan.Selection.TopK, Math.Max(plan.Selection.MaxPerDoc, 3)),
                                    Math.Min(plan.Selection.TopK, Math.Max(plan.Selection.MaxPerPage, 2)),
                                    query: plan.Request.Query);
            }
            else
            {
                flow.Selection.Matches.Clear();
                flow.Selection.Keys.Clear();
            }
        }

        if (ContainsDocumentCompanionRelationIntent(plan.Request.Query)
            && ExtractStandardReferenceTitleLookupPhrases(plan.Request.Query).Count > 0)
        {
            var companionBackfill = new List<RagMatch>();
            foreach (var phrase in ExtractStandardReferenceTitleLookupPhrases(plan.Request.Query).Take(2))
            {
                var titleAnchorQuery = phrase.Contains('"', StringComparison.Ordinal)
                    ? phrase
                    : $"\"{phrase}\"";
                var (referenceCompanionMatches, referenceCompanionMs) = await flow.Session.Phases.MeasureAsync(
                    phaseName: "retrieval_reference_companion_title_anchor",
                    retriever: "title_anchor_route",
                    action: () => SearchTitleAnchorRouteMatchesAsync(
                        resources.DataSource,
                        resources.Context.TenantId,
                        titleAnchorQuery,
                        plan.Scope.Category,
                        plan.Request.DocId,
                        plan.Request.DocPath,
                        Math.Min(plan.Selection.Candidates, Math.Max(plan.Selection.TopK, 16)),
                        resources.Context.CancellationToken,
                        plan.Scope.CategoryPath,
                        degradedRetrieverRef: flow.Session.Degradation.Mark,
                        allowDirectChunkRoute: false),
                    getReturnedCount: static matches => matches.Count);
                flow.Session.Timings.TitleAnchorRoutePhaseMs += referenceCompanionMs;
                companionBackfill.AddRange(referenceCompanionMatches);
            }

            if (companionBackfill.Count > 0)
            {
                AddRankedMatchesDiagnosed(
                flow.Session.Diagnostics, flow.Selection.Matches,
                                    flow.Selection.Keys,
                                    CalibrateFusedMatches(plan.Request.Query, companionBackfill, plan.Request.Query),
                                    plan.Selection.TopK,
                                    minScore: 0.0,
                                    Math.Max(plan.Selection.MaxPerDoc, 3),
                                    Math.Max(plan.Selection.MaxPerPage, 2),
                                    query: plan.Request.Query);
            }
        }
    }
}
