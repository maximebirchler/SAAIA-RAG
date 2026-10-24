using static SAAIA.Backend.Retrieval.RetrievalSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveRequestedPageWindow
{
    internal static void Apply(InteractiveRetrievalFlow flow)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        if (plan.Scope.HasRequestedPageWindow && flow.Selection.Matches.Count > 0)
        {
            flow.Session.Timings.OrderingWatch.Restart();
            var preferredSelections = PreferRequestedPageWindowMatches(flow.Selection.Matches, plan.Scope.RequestedPageStart, plan.Scope.RequestedPageEnd);
            flow.Selection.Matches.Clear();
            flow.Selection.Matches.AddRange(preferredSelections.Take(plan.Selection.TopK));
            RebuildSelectedKeys(flow.Selection.Matches, flow.Selection.Keys);
            flow.Session.Timings.OrderingWatch.Stop();
            flow.Session.Timings.SelectionMs += flow.Session.Timings.OrderingWatch.ElapsedMilliseconds;
        }
    }
}
