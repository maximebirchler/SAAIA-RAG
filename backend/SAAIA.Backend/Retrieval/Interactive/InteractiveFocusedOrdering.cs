using System.Diagnostics;
using static SAAIA.Backend.Retrieval.RetrievalTitleSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveFocusedOrdering
{
    internal static void Apply(InteractiveRetrievalFlow flow)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        flow.Session.Timings.OrderingWatch = Stopwatch.StartNew();
        PruneUnanchoredFocusedLookupSelections(plan.Query.SelectionQuery, flow.Selection.Matches);
        flow.Session.Timings.OrderingWatch.Stop();
        flow.Session.Timings.SelectionMs += flow.Session.Timings.OrderingWatch.ElapsedMilliseconds;
    }
}
