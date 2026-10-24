using System.Diagnostics;
using static SAAIA.Backend.Retrieval.RetrievalCandidateSignals;
using static SAAIA.Backend.Retrieval.RetrievalSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveDirectTechnicalLinkedEvidence
{
    internal static async Task ExecuteAsync(InteractiveRetrievalFlow flow)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        if (!plan.Routes.SkipChunkRetrieversForDocumentOverview
                    && !plan.Scope.HasExplicitFileDocumentHint
                    && ShouldRunShortTechnicalDirectEvidenceLinkedBackfill(plan.Query.RankingQuery, flow.Selection.Matches))
        {
            var (shortTechnicalLinkedMatches, shortTechnicalLinkedMs) = await flow.Session.Phases.MeasureAsync(
                phaseName: "retrieval_short_technical_linked_context",
                retriever: "linked_context",
                action: () => SearchLinkedMatchesAsync(
                    resources.DataSource,
                    resources.Context.TenantId,
                    flow.Selection.Matches,
                    plan.Scope.Category,
                    plan.Request.DocId,
                    plan.Request.DocPath,
                    Math.Max(plan.Selection.TopK, 8),
                    resources.Context.CancellationToken,
                    plan.Scope.CategoryPath,
                    includeSamePageForLinkedAnchors: true,
                    maxAnchorCandidates: 12),
                getReturnedCount: static matches => matches.Count);
            flow.Session.Timings.LinkedPhaseMs += shortTechnicalLinkedMs;

            var directEvidenceLinkedMatches = shortTechnicalLinkedMatches
                .Where(match => ComputeShortTechnicalDirectEvidencePriority(plan.Query.RankingQuery, match, string.Empty, string.Empty) > 0)
                .ToList();
            if (directEvidenceLinkedMatches.Count == 0 && shortTechnicalLinkedMatches.Count > 0)
            {
                var (deepShortTechnicalLinkedMatches, deepShortTechnicalLinkedMs) = await flow.Session.Phases.MeasureAsync(
                    phaseName: "retrieval_short_technical_deep_linked_context",
                    retriever: "linked_context",
                    action: () => SearchLinkedMatchesAsync(
                        resources.DataSource,
                        resources.Context.TenantId,
                        shortTechnicalLinkedMatches,
                        plan.Scope.Category,
                        plan.Request.DocId,
                        plan.Request.DocPath,
                        Math.Max(plan.Selection.TopK * 2, 12),
                        resources.Context.CancellationToken,
                        plan.Scope.CategoryPath,
                        includeSamePageForLinkedAnchors: true,
                        maxAnchorCandidates: 16),
                    getReturnedCount: static matches => matches.Count);
                flow.Session.Timings.LinkedPhaseMs += deepShortTechnicalLinkedMs;

                shortTechnicalLinkedMatches = shortTechnicalLinkedMatches
                    .Concat(deepShortTechnicalLinkedMatches)
                    .DistinctBy(BuildRagMatchIdentityKey)
                    .ToList();
                directEvidenceLinkedMatches = shortTechnicalLinkedMatches
                    .Where(match => ComputeShortTechnicalDirectEvidencePriority(plan.Query.RankingQuery, match, string.Empty, string.Empty) > 0)
                    .ToList();
            }
            if (directEvidenceLinkedMatches.Count == 0 && shortTechnicalLinkedMatches.Count > 0)
            {
                var (thirdWaveShortTechnicalLinkedMatches, thirdWaveShortTechnicalLinkedMs) = await flow.Session.Phases.MeasureAsync(
                    phaseName: "retrieval_short_technical_third_linked_context",
                    retriever: "linked_context",
                    action: () => SearchLinkedMatchesAsync(
                        resources.DataSource,
                        resources.Context.TenantId,
                        shortTechnicalLinkedMatches,
                        plan.Scope.Category,
                        plan.Request.DocId,
                        plan.Request.DocPath,
                        Math.Max(plan.Selection.TopK * 3, 16),
                        resources.Context.CancellationToken,
                        plan.Scope.CategoryPath,
                        includeSamePageForLinkedAnchors: true,
                        maxAnchorCandidates: 24),
                    getReturnedCount: static matches => matches.Count);
                flow.Session.Timings.LinkedPhaseMs += thirdWaveShortTechnicalLinkedMs;

                shortTechnicalLinkedMatches = shortTechnicalLinkedMatches
                    .Concat(thirdWaveShortTechnicalLinkedMatches)
                    .DistinctBy(BuildRagMatchIdentityKey)
                    .ToList();
                directEvidenceLinkedMatches = shortTechnicalLinkedMatches
                    .Where(match => ComputeShortTechnicalDirectEvidencePriority(plan.Query.RankingQuery, match, string.Empty, string.Empty) > 0)
                    .ToList();
            }

            if (directEvidenceLinkedMatches.Count > 0)
            {
                var calibratedShortTechnicalSelections = CalibrateFusedMatches(
                    plan.Query.RankingQuery,
                    flow.Selection.Matches.Concat(directEvidenceLinkedMatches).ToList(),
                    plan.Request.Query);
                calibratedShortTechnicalSelections = SuppressNavigationalNoise(plan.Request.Query, calibratedShortTechnicalSelections);

                var selectionSw = Stopwatch.StartNew();
                flow.Selection.Matches.Clear();
                flow.Selection.Keys.Clear();
                AddRankedMatchesDiagnosed(
                flow.Session.Diagnostics, flow.Selection.Matches,
                                    flow.Selection.Keys,
                                    calibratedShortTechnicalSelections,
                                    plan.Selection.TopK,
                                    minScore: 0.0,
                                    Math.Max(plan.Selection.MaxPerDoc, 2),
                                    Math.Max(plan.Selection.MaxPerPage, 2),
                                    query: plan.Request.Query);
                PruneNavigationalSelections(plan.Request.Query, flow.Selection.Matches);
                PromoteActionableSameDocumentEvidence(flow.Selection.Matches);
                if (!plan.Routes.SkipChunkRetrieversForDocumentOverview)
                    ApplyAutocut(flow.Selection.Matches, plan.Selection.SelectionMinScore, plan.Request.Query);
                RebuildSelectedKeys(flow.Selection.Matches, flow.Selection.Keys);
                selectionSw.Stop();
                flow.Session.Timings.SelectionMs += selectionSw.ElapsedMilliseconds;
            }
        }
    }
}
