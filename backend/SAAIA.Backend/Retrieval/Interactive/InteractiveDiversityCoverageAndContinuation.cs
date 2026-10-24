using System.Diagnostics;
using static SAAIA.Backend.Retrieval.RetrievalQueryPolicy;
using static SAAIA.Backend.Retrieval.RetrievalSelection;
using static SAAIA.Backend.Retrieval.RetrievalTitleSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveDiversityCoverageAndContinuation
{
    internal static async Task ExecuteAsync(InteractiveRetrievalFlow flow)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        if (flow.Selection.BroadDiversityCandidates is { Count: > 0 }
                    && ShouldRunBroadDiversityCandidateBackfill(plan.Request.Query, flow.Selection.Matches.Count, plan.Selection.TopK))
        {
            flow.Session.Timings.OrderingWatch.Restart();
            RebuildSelectedKeys(flow.Selection.Matches, flow.Selection.Keys);
            AddRankedMatchesDiagnosed(
            flow.Session.Diagnostics, flow.Selection.Matches,
                            flow.Selection.Keys,
                            flow.Selection.BroadDiversityCandidates,
                            plan.Selection.TopK,
                            minScore: 0.0,
                            plan.Selection.MaxPerDoc,
                            plan.Selection.MaxPerPage,
                            prioritizeDocumentProfiles: true,
                            query: plan.Request.Query);
            PruneNavigationalSelections(plan.Request.Query, flow.Selection.Matches);
            PromoteActionableSameDocumentEvidence(flow.Selection.Matches);
            RebuildSelectedKeys(flow.Selection.Matches, flow.Selection.Keys);
            flow.Session.Timings.OrderingWatch.Stop();
            flow.Session.Timings.SelectionMs += flow.Session.Timings.OrderingWatch.ElapsedMilliseconds;
        }

        if (flow.Selection.BroadDiversityCandidates is { Count: > 0 }
            && ShouldRunBroadDiversityCandidateCoverage(plan.Request.Query, plan.Selection.TopK))
        {
            flow.Session.Timings.OrderingWatch.Restart();
            RebuildSelectedKeys(flow.Selection.Matches, flow.Selection.Keys);
            EnsureBroadDiversityCandidateCoverage(
                flow.Selection.Matches,
                flow.Selection.Keys,
                flow.Selection.BroadDiversityCandidates,
                plan.Selection.TopK,
                minScore: 0.0,
                maxPerPage: Math.Max(plan.Selection.MaxPerPage, 2),
                query: plan.Request.Query);
            PruneNavigationalSelections(plan.Request.Query, flow.Selection.Matches);
            PromoteActionableSameDocumentEvidence(flow.Selection.Matches);
            RebuildSelectedKeys(flow.Selection.Matches, flow.Selection.Keys);
            flow.Session.Timings.OrderingWatch.Stop();
            flow.Session.Timings.SelectionMs += flow.Session.Timings.OrderingWatch.ElapsedMilliseconds;
        }

        if (!plan.Routes.UseScopedProfileFallback && ShouldRunStructuredContinuationBackfill(plan.Request.Query, flow.Selection.Matches))
        {
            var structuredContinuationAnchors = SelectStructuredContinuationAnchors(
                plan.Request.Query,
                flow.Selection.Matches,
                Math.Min(plan.Selection.TopK, 4));
            if (structuredContinuationAnchors.Count > 0)
            {
                var structuredContinuationLimit = Math.Min(plan.Selection.TopK, Math.Max(4, structuredContinuationAnchors.Count * 2));
                var (structuredContinuationMatches, structuredContinuationMs) = await flow.Session.Phases.MeasureAsync(
                    phaseName: "retrieval_structured_continuation",
                    retriever: "linked_context",
                    action: () => SearchLinkedMatchesAsync(
                        resources.DataSource,
                        resources.Context.TenantId,
                        structuredContinuationAnchors,
                        plan.Scope.Category,
                        plan.Request.DocId,
                        plan.Request.DocPath,
                        structuredContinuationLimit,
                        resources.Context.CancellationToken,
                        plan.Scope.CategoryPath,
                        includeSamePageForLinkedAnchors: true),
                    getReturnedCount: static matches => matches.Count);
                flow.Session.Timings.LinkedPhaseMs += structuredContinuationMs;

                var structuredContinuations = FilterStructuredContinuationMatches(
                    plan.Request.Query,
                    structuredContinuationAnchors,
                    structuredContinuationMatches,
                    structuredContinuationLimit);
                if (structuredContinuations.Count > 0)
                {
                    var structuredContinuationSelectionSw = Stopwatch.StartNew();
                    MergeStructuredContinuationSelections(plan.Request.Query, flow.Selection.Matches, structuredContinuations, plan.Selection.TopK);
                    structuredContinuationSelectionSw.Stop();
                    flow.Session.Timings.SelectionMs += structuredContinuationSelectionSw.ElapsedMilliseconds;
                }
            }
        }
    }
}
