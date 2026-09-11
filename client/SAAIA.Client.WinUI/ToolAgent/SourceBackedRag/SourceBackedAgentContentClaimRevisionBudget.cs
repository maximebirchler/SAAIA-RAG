namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private const int MaximumDirectContentClaimRevisionsPerSelection = 1;

    private static string BuildEvidenceSelectionSignature(
        IReadOnlyList<string> evidenceIds)
        => string.Join(
            ",",
            evidenceIds
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Select(static id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase));

    private static string BuildContentClaimRevisionBudgetFeedback(
        SemanticReview review)
        => "BUDGET MECANIQUE DE REVISION DIRECTE EPUISE POUR LE MEME POOL DE PREUVES. "
           + "Ne relance pas automatiquement le writer avec ce pool. Reprends la decision "
           + "d'orchestration semantique: compare le contexte local, la recherche dans le "
           + "document focalise, la navigation structurelle et la recherche globale selon "
           + "le besoin restant. MOTIFS DU JUGE LLM: "
           + string.Join(" | ", review.Reasons.Take(4));

    private void TraceContentClaimRevisionBudget(
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn,
        string selectionSignature,
        int reasonCount)
        => AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.IterationController,
            "source_backed_agent_v2.content_claim_revision.budget_returned_to_orchestrator",
            ("turn", turn),
            ("selected_evidence", selectionSignature),
            ("direct_revision_budget",
                MaximumDirectContentClaimRevisionsPerSelection),
            ("reason_count", reasonCount),
            ("decision_source", "mechanical_revision_budget")));
}
