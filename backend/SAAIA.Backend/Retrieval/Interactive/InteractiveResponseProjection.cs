using System.Diagnostics;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveResponseProjection
{
    internal static RagSearchResponse Build(InteractiveRetrievalFlow flow, Activity? searchActivity)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        flow.Session.Timings.Total.Stop();
        flow.Session.Diagnostics.CapturePhase("final_selected", "final_selection", flow.Session.Timings.SelectionMs, flow.Selection.Matches);

        var response = new RagSearchResponse(
            RequestId: resources.Context.RequestId,
            Query: plan.Request.Query,
            QueryNormalized: plan.Query.QueryNorm,
            Category: plan.Scope.RequestedCategory ?? plan.Scope.CategoryQueryHint,
            TopK: plan.Selection.RequestedTopK,
            MinScore: plan.Selection.SelectionMinScore,
            Candidates: plan.Selection.Candidates,
            MaxPerDoc: plan.Selection.MaxPerDoc,
            MaxPerPage: plan.Selection.MaxPerPage,
            QdrantStatus: flow.Session.Timings.QdrantStatus,
            Timings: new RagSearchTimings(
                TotalMs: flow.Session.Timings.Total.ElapsedMilliseconds,
                TeiMs: flow.Session.Timings.TeiMs,
                RerankMs: flow.Session.Timings.RerankMs,
                SparseMs: flow.Session.Timings.SparseMs,
                QdrantMs: flow.Session.Timings.QdrantMs,
                ExactMs: flow.Session.Timings.ExactMs,
                QuotedTitleMs: flow.Session.Timings.QuotedTitleMs,
                LocalTitleTokenMs: flow.Session.Timings.LocalTitleTokenMs,
                TitleAnchorRouteMs: flow.Session.Timings.TitleAnchorRoutePhaseMs,
                SparsePhaseMs: flow.Session.Timings.SparsePhaseMs,
                DenseMs: flow.Session.Timings.DensePhaseMs,
                ProfileMs: flow.Session.Timings.ProfilePhaseMs,
                LinkedMs: flow.Session.Timings.LinkedPhaseMs,
                FusionMs: flow.Session.Timings.FusionMs,
                RerankPhaseMs: flow.Session.Timings.RerankPhaseMs,
                SelectionMs: flow.Session.Timings.SelectionMs),
            Matches: flow.Selection.Matches.Take(plan.Selection.RequestedTopK).ToList(),
            DegradedRetrievers: flow.Session.Degradation.Retrievers.Count == 0
                ? null
                : flow.Session.Degradation.Retrievers.OrderBy(static retriever => retriever, StringComparer.Ordinal).ToArray(),
            DegradedRetrieverErrors: flow.Session.Degradation.Errors.Count == 0
                ? null
                : flow.Session.Degradation.Errors
                    .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
            Diagnostics: plan.Request.IncludeDiagnostics == true ? flow.Session.Diagnostics.Build(flow.Selection.Matches) : null);

        RetrievalTelemetry.CompleteSearch(searchActivity, response, plan.Routes.Mode, plan.Scope.HasCategoryFilter, plan.Scope.HasDocScope);
        RetrievalTelemetry.RecordSearch(response, plan.Routes.Mode, plan.Scope.HasCategoryFilter, plan.Scope.HasDocScope, flow.Session.Timings.ExactMs, flow.Session.Timings.SparsePhaseMs + flow.Session.Timings.TitleAnchorRoutePhaseMs + flow.Session.Timings.ProfilePhaseMs, flow.Session.Timings.DensePhaseMs, flow.Session.Timings.LinkedPhaseMs);

        using (var candidateLog = BackendDiagnostics.Start("retrieval.candidate_flow", resources.Context.Logger))
        {
            var snapshot = flow.Session.Diagnostics.Build(flow.Selection.Matches);
            candidateLog.Set("capture_mode", "metadata");
            candidateLog.Set("phase_counts", snapshot.Phases.Select(phase => new { phase.Name, phase.Returned, phase.OmittedCandidates }).ToArray());
            candidateLog.Set("candidate_samples", snapshot.Phases.Select(phase => new
            {
                phase.Name,
                Candidates = phase.TopCandidates.Select(candidate => new
                {
                    candidate.DocId, candidate.ChunkId, candidate.PageStart, candidate.PageEnd,
                    candidate.Rank, candidate.Score, candidate.Retriever
                }).ToArray()
            }).ToArray());
            candidateLog.Set("final_selection_ids", snapshot.Selection.Items.Select(candidate => new
            {
                candidate.DocId, candidate.ChunkId, candidate.PageStart, candidate.PageEnd
            }).ToArray());
            candidateLog.Set("rejection_counts", snapshot.RejectionCounts);
            candidateLog.Set("dropped_rejections", snapshot.DroppedRejections);
            candidateLog.Set("dropped_phases", snapshot.DroppedPhases);
            candidateLog.Complete();
        }
        return response;

    }
}
