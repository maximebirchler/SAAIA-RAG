using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalLexicalExpansion;
using static SAAIA.Backend.Retrieval.RetrievalSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveLateStandardReferenceBackfill
{
    internal static async Task ExecuteAsync(InteractiveRetrievalFlow flow)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        var lateStandardReferencePhrases = ExtractStandardReferenceTitleLookupPhrases(plan.Request.Query);
        if (lateStandardReferencePhrases.Count > 1
            && !SelectionsCoverStandardReferencePhrases(lateStandardReferencePhrases, flow.Selection.Matches))
        {
            var (lateStandardReferenceMatches, lateStandardReferenceMs) = await flow.Session.Phases.MeasureAsync(
                phaseName: "retrieval_standard_reference_late_recovery",
                retriever: "standard_reference_document_name",
                action: () => SearchStandardReferenceDocumentNameContentMatchesAsync(
                    resources.DataSource,
                    resources.Context.TenantId,
                    plan.Request.Query,
                    lateStandardReferencePhrases,
                    plan.Scope.Category,
                    plan.Request.DocId,
                    plan.Request.DocPath,
                    Math.Min(plan.Selection.Candidates, Math.Max(plan.Selection.TopK, 16)),
                    resources.Context.CancellationToken,
                    plan.Scope.CategoryPath,
                    degradedRetrieverRef: flow.Session.Degradation.Mark),
                getReturnedCount: static matches => matches.Count);
            if (!SelectionsCoverStandardReferencePhrases(lateStandardReferencePhrases, lateStandardReferenceMatches))
            {
                lateStandardReferenceMatches.AddRange(await SearchStandardReferenceDocumentNameProfileMatchesAsync(
                    resources.DataSource,
                    resources.Context.TenantId,
                    plan.Request.Query,
                    lateStandardReferencePhrases,
                    plan.Scope.Category,
                    plan.Request.DocId,
                    plan.Request.DocPath,
                    Math.Min(plan.Selection.Candidates, Math.Max(plan.Selection.TopK, 16)),
                    resources.Context.CancellationToken,
                    plan.Scope.CategoryPath,
                    degradedRetrieverRef: flow.Session.Degradation.Mark,
                    commandTimeoutSeconds: plan.SparseCommandTimeoutSeconds));
                lateStandardReferenceMatches = DeduplicatePreciseTitleBackfillMatches(lateStandardReferenceMatches);
            }
            flow.Session.Timings.TitleAnchorRoutePhaseMs += lateStandardReferenceMs;

            if (lateStandardReferenceMatches.Count > 0)
            {
                var lateStandardReferenceSelectionPool = SelectionsCoverStandardReferencePhrases(
                    lateStandardReferencePhrases,
                    lateStandardReferenceMatches)
                    ? lateStandardReferenceMatches
                    : lateStandardReferenceMatches.Concat(flow.Selection.Matches).ToList();
                var mergedStandardReferenceSelections = CalibrateFusedMatches(
                    plan.Request.Query,
                    lateStandardReferenceSelectionPool,
                    plan.Request.Query);
                flow.Selection.Matches.Clear();
                flow.Selection.Keys.Clear();
                AddRankedMatchesDiagnosed(
                flow.Session.Diagnostics, flow.Selection.Matches,
                                    flow.Selection.Keys,
                                    mergedStandardReferenceSelections,
                                    plan.Selection.TopK,
                                    minScore: 0.0,
                                    Math.Max(plan.Selection.MaxPerDoc, 3),
                                    Math.Max(plan.Selection.MaxPerPage, 2),
                                    query: plan.Request.Query);
            }
        }
    }
}
