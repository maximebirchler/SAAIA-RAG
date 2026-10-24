using static SAAIA.Backend.Retrieval.RetrievalCandidateSignals;
using static SAAIA.Backend.Retrieval.RetrievalChannels;
using static SAAIA.Backend.Retrieval.RetrievalLexicalExpansion;
using static SAAIA.Backend.Retrieval.RetrievalSelection;
using static SAAIA.Backend.Retrieval.RetrievalTitleSelection;

namespace SAAIA.Backend.Retrieval;

internal static class InteractiveFinalStandardReferenceProfiles
{
    internal static async Task ExecuteAsync(InteractiveRetrievalFlow flow)
    {
        var resources = flow.Session.Resources;
        var plan = flow.Session.Plan;

        var finalStandardReferencePhrases = ExtractStandardReferenceTitleLookupPhrases(plan.Request.Query);
        if (finalStandardReferencePhrases.Count > 1
            && !SelectionsCoverStandardReferencePhrases(finalStandardReferencePhrases, flow.Selection.Matches))
        {
            var (finalStandardReferenceProfileMatches, finalStandardReferenceProfileMs) = await flow.Session.Phases.MeasureAsync(
                phaseName: "retrieval_standard_reference_profile_final_recovery",
                retriever: "standard_reference_document_name_profile",
                action: () => SearchStandardReferenceDocumentNameProfileMatchesAsync(
                    resources.DataSource,
                    resources.Context.TenantId,
                    plan.Request.Query,
                    finalStandardReferencePhrases,
                    plan.Scope.Category,
                    plan.Request.DocId,
                    plan.Request.DocPath,
                    Math.Min(plan.Selection.Candidates, Math.Max(plan.Selection.TopK, 16)),
                    resources.Context.CancellationToken,
                    plan.Scope.CategoryPath,
                    degradedRetrieverRef: flow.Session.Degradation.Mark,
                    commandTimeoutSeconds: plan.SparseCommandTimeoutSeconds),
                getReturnedCount: static matches => matches.Count);
            flow.Session.Timings.ProfilePhaseMs += finalStandardReferenceProfileMs;

            if (SelectionsCoverStandardReferencePhrases(finalStandardReferencePhrases, finalStandardReferenceProfileMatches))
            {
                flow.Session.Timings.OrderingWatch.Restart();
                flow.Selection.Matches.Clear();
                flow.Selection.Keys.Clear();
                AddRankedMatchesDiagnosed(
                flow.Session.Diagnostics, flow.Selection.Matches,
                                    flow.Selection.Keys,
                                    CalibrateFusedMatches(plan.Request.Query, finalStandardReferenceProfileMatches, plan.Request.Query),
                                    plan.Selection.TopK,
                                    minScore: 0.0,
                                    maxPerDoc: Math.Max(plan.Selection.MaxPerDoc, 2),
                                    maxPerPage: Math.Max(plan.Selection.MaxPerPage, 1),
                                    query: plan.Request.Query);
                PrioritizeFinalSelections(plan.Request.Query, flow.Selection.Matches);
                RebuildSelectedKeys(flow.Selection.Matches, flow.Selection.Keys);
                flow.Session.Timings.OrderingWatch.Stop();
                flow.Session.Timings.SelectionMs += flow.Session.Timings.OrderingWatch.ElapsedMilliseconds;
            }
        }

        PruneUnrequestedStandardReferenceSelections(plan.Request.Query, flow.Selection.Matches);
        RebuildSelectedKeys(flow.Selection.Matches, flow.Selection.Keys);

        PrioritizeShortTechnicalDirectEvidenceSelections(plan.Request.Query, flow.Selection.Matches);
        RebuildSelectedKeys(flow.Selection.Matches, flow.Selection.Keys);
    }
}
