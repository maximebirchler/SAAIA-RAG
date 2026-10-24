using System.Diagnostics;
using static SAAIA.Backend.Retrieval.RetrievalSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveDeferredProfileSearch
{
    internal static async Task ExecuteAsync(InteractiveRetrievalFlow flow, InteractivePrimaryChannels channels)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        if (channels.DeferDocumentProfileSearch
                    && ShouldRunDeferredDocumentProfileSearch(
                        flow.Selection.Matches,
                        plan.Selection.TopK,
                        plan.Selection.PreferDocumentDiversity,
                        plan.Routes.UseScopedProfileFallback,
                        plan.Routes.AllowSparseAssistForScopedProfileFallback))
        {
            var (deferredProfileMatches, deferredProfileMs) = await InteractiveLookupRoutes.SearchDocumentProfilesMeasuredAsync(flow, channels);
            flow.Session.Timings.ProfilePhaseMs += deferredProfileMs;

            var selectionSwDeferred = Stopwatch.StartNew();
            AddRankedMatchesDiagnosed(
            flow.Session.Diagnostics, flow.Selection.Matches,
                                flow.Selection.Keys,
                                RankDeferredDocumentProfileMatches(deferredProfileMatches),
                                plan.Selection.TopK,
                                minScore: 0.0,
                                plan.Selection.MaxPerDoc,
                                    plan.Selection.MaxPerPage,
                                    prioritizeDocumentProfiles: ShouldPrioritizeDocumentProfilesForSelection(
                                        plan.Selection.PreferDocumentDiversity,
                                        plan.Routes.AllowSparseAssistForScopedProfileFallback || plan.Routes.AllowSparseAssistForBroadDiversity,
                                        channels.AllowComparativeDocumentProfileAssist));
            selectionSwDeferred.Stop();
            flow.Session.Timings.SelectionMs += selectionSwDeferred.ElapsedMilliseconds;
        }
    }
}
