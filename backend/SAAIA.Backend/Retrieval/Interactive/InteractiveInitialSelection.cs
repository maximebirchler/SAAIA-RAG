using static SAAIA.Backend.Retrieval.RetrievalSelection;
using static SAAIA.Backend.Retrieval.RetrievalNavigation;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveInitialSelection
{
    internal static async Task ExecuteAsync(InteractiveRetrievalFlow flow)
    {
        var plan = flow.Session.Plan;
        var selection = flow.Selection;
        if (flow.Seeds.Route == InteractiveInitialRoute.Exact)
        {
            AddRankedMatchesDiagnosed(flow.Session.Diagnostics, selection.Matches, selection.Keys,
                flow.Seeds.ExactMatches, plan.Selection.TopK, 0.0, plan.Selection.MaxPerDoc, plan.Selection.MaxPerPage);
            return;
        }
        if (flow.Seeds.Route == InteractiveInitialRoute.TitleAnchor)
        {
            var anchors = flow.Seeds.EarlyTitleAnchorRouteMatches!;
            flow.Session.Timings.TitleAnchorRoutePhaseMs = flow.Seeds.EarlyTitleAnchorRouteMs;
            selection.ReusablePreciseTitleCandidates = BuildReusablePreciseTitleRouteMatches(anchors);
            AddRankedMatchesDiagnosed(flow.Session.Diagnostics, selection.Matches, selection.Keys,
                flow.Seeds.ExactMatches.Concat(flow.Seeds.TitleLookupMatches).Concat(RankTitleAnchorRouteShortCircuitMatches(anchors)),
                plan.Selection.TopK, 0.0, plan.Selection.MaxPerDoc, plan.Selection.MaxPerPage, query: plan.Request.Query);
            return;
        }

        var channels = await InteractivePrimaryLookup.SearchAsync(flow).ConfigureAwait(false);
        await InteractiveStepDiagnostics.RunAsync(flow, "primary_fusion", () => InteractivePrimaryFusion.ExecuteAsync(flow, channels));
        await InteractiveStepDiagnostics.RunAsync(flow, "comparative_subjects", () => InteractiveComparativeSubjects.ExecuteAsync(flow, channels));
        await InteractiveStepDiagnostics.RunAsync(flow, "comparative_titles", () => InteractiveComparativeTitles.ExecuteAsync(flow, channels));
        await InteractiveStepDiagnostics.RunAsync(flow, "comparative_operand_profiles", () => InteractiveComparativeOperandProfiles.ExecuteAsync(flow));
        await InteractiveStepDiagnostics.RunAsync(flow, "precise_title_content", () => InteractivePreciseTitleContent.ExecuteAsync(flow));
        await InteractiveStepDiagnostics.RunAsync(flow, "deferred_profile_search", () => InteractiveDeferredProfileSearch.ExecuteAsync(flow, channels));
        await InteractiveStepDiagnostics.RunAsync(flow, "initial_linked_context", () => InteractiveInitialLinkedContext.ExecuteAsync(flow));
    }
}
