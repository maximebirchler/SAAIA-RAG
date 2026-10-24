using System.Diagnostics;
using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalQueryPolicy;
using static SAAIA.Backend.Retrieval.RetrievalSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractivePrimaryFusion
{
    internal static async Task ExecuteAsync(InteractiveRetrievalFlow flow, InteractivePrimaryChannels channels)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        var fusionSw = Stopwatch.StartNew();
        var exactAndQuotedMatches = flow.Seeds.TitleLookupMatches.Count == 0
            ? flow.Seeds.ExactMatches
            : flow.Seeds.ExactMatches.Concat(flow.Seeds.TitleLookupMatches).ToList();
        var fusedMatches = FuseWithRrf(exactAndQuotedMatches, channels.EffectiveSparseMatches, channels.EffectiveDenseMatches, channels.ProfileMatches, channels.CombinedTitleAnchorRouteMatches);
        fusedMatches = CalibrateFusedMatches(plan.Query.RetrievalQuery, fusedMatches, plan.Request.Query);
        fusedMatches = SuppressNavigationalNoise(plan.Request.Query, fusedMatches);
        fusedMatches = PreferRequestedPageWindowMatches(fusedMatches, plan.Scope.RequestedPageStart, plan.Scope.RequestedPageEnd);
        var suppressUnanchoredSpecificResults = !plan.Routes.UseScopedProfileFallback
            && !plan.Routes.UseChoiceBetweenLexicalRoute
            && ShouldSuppressUnanchoredSpecificResults(plan.Query.RetrievalQuery, fusedMatches);
        fusionSw.Stop();
        flow.Session.Timings.FusionMs += fusionSw.ElapsedMilliseconds;
        flow.Session.Diagnostics.CapturePhase("fusion_rrf", "rrf_fusion", flow.Session.Timings.FusionMs, fusedMatches);

        if (suppressUnanchoredSpecificResults)
        {
            flow.Session.Diagnostics.CapturePhase("fusion_suppressed", "selection_guard", null, fusedMatches);
            fusedMatches = [];
        }
        else
        {
            var preRerankMatches = fusedMatches.ToList();
            var (rerankAttempt, measuredRerankPhaseMs) = await flow.Session.Phases.MeasureAsync(
                phaseName: "retrieval_rerank",
                retriever: "tei_rerank",
                action: () => TryRerankWithTeiAsync(
                    resources.HttpClients,
                    resources.Options,
                    plan.Query.RetrievalQuery,
                    fusedMatches,
                    resources.Context.CancellationToken,
                    rerankMsRef: value => flow.Session.Timings.RerankMs = value,
                    teiGovernor: resources.Context.TeiGovernor,
                    degradedRetrieverRef: flow.Session.Degradation.Mark),
                getReturnedCount: static attempt => attempt.Matches.Count);
            flow.Session.Timings.RerankPhaseMs += measuredRerankPhaseMs;
            flow.Session.Diagnostics.CapturePhase("rerank_input", "tei_rerank", null, preRerankMatches);
            if (rerankAttempt.Applied)
            {
                fusionSw.Restart();
                fusedMatches = rerankAttempt.Matches;
                fusedMatches = CalibrateFusedMatches(plan.Query.RetrievalQuery, fusedMatches, plan.Query.SelectionQuery);
                fusedMatches = SuppressNavigationalNoise(plan.Query.SelectionQuery, fusedMatches);
                fusedMatches = PreferRequestedPageWindowMatches(fusedMatches, plan.Scope.RequestedPageStart, plan.Scope.RequestedPageEnd);
                fusionSw.Stop();
                flow.Session.Timings.FusionMs += fusionSw.ElapsedMilliseconds;
            }
            else
            {
                fusedMatches = PreferRequestedPageWindowMatches(rerankAttempt.Matches, plan.Scope.RequestedPageStart, plan.Scope.RequestedPageEnd);
            }
            flow.Session.Diagnostics.CapturePhase(
                rerankAttempt.Applied ? "rerank_output" : "rerank_skipped",
                "tei_rerank",
                measuredRerankPhaseMs,
                fusedMatches);
        }

        var selectionSw = Stopwatch.StartNew();

        if (ShouldRunBroadDiversityCandidateBackfill(plan.Request.Query, flow.Selection.Matches.Count, plan.Selection.TopK))
            flow.Selection.BroadDiversityCandidates = fusedMatches.ToList();
        AddRankedMatchesDiagnosed(
        flow.Session.Diagnostics, flow.Selection.Matches,
                        flow.Selection.Keys,
                        fusedMatches,
                        plan.Selection.TopK,
                        plan.Routes.UseScopedProfileFallback ? 0.0 : plan.Selection.SelectionMinScore,
                        plan.Selection.MaxPerDoc,
                        plan.Selection.MaxPerPage,
                        prioritizeDocumentProfiles: channels.PrioritizeDocumentProfilesForSelection,
                        query: plan.Query.SelectionQuery);
        selectionSw.Stop();
        flow.Session.Timings.SelectionMs += selectionSw.ElapsedMilliseconds;
        flow.Session.Diagnostics.CapturePhase("selection_after_fusion", "final_selection", selectionSw.ElapsedMilliseconds, flow.Selection.Matches);

    }
}
