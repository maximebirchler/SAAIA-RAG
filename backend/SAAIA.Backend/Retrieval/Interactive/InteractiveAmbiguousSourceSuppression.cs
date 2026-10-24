using static SAAIA.Backend.Retrieval.RetrievalLexicalExpansion;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveAmbiguousSourceSuppression
{
    internal static void Apply(InteractiveRetrievalFlow flow)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        if (ShouldSuppressUnscopedAmbiguousDocumentReferenceSources(plan.Request.Query, plan.Request.DocId, plan.Request.DocPath))
        {
            flow.Selection.Matches.Clear();
            flow.Selection.Keys.Clear();
        }
    }
}
