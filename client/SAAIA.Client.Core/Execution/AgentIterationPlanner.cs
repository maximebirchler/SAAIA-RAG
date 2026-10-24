namespace SAAIA.Client.Core.Execution;

public enum AgentIterationIntent { CollectEvidence, ReviewCandidates, SelectEvidence, WriteAnswer, FinalizeAnswer }
public sealed record AgentIterationInput(int Turn, int MaximumTurns, int MaximumRunTurns,
    bool SeparateActionAndWriter, int RequiredEvidenceCount, bool HasCitableEvidence,
    int ObservedCitableSources, bool CandidateCollectionOpen, bool SelectionOnlyRequested,
    bool DecisionOnlyRequested, bool SemanticNeedsMoreEvidence, bool CandidateAuditEnabled,
    bool UseFlatAdequacy, bool HasSemanticLayout, bool RequireEvidenceSelection,
    bool EnableWorkspaceWriterHandoff, bool DedicatedCandidateAudit, bool DirectWriterRequested);
public sealed record AgentIterationPlan(AgentIterationIntent Intent, bool FinalizationTurn,
    bool RenderingReservationTurn, bool SelectionDecisionTurn, bool AllowAdaptiveFlatSelection,
    bool SemanticSelectionDecisionTurn, bool WorkspaceWriterDecisionTurn);

/// <summary>Mechanical iteration gates only. Semantic suitability remains the model's decision.</summary>
public static class AgentIterationPlanner
{
    public static AgentIterationPlan Plan(AgentIterationInput input)
    {
        var structured = input.SeparateActionAndWriter && input.RequiredEvidenceCount > 1;
        var finalization = structured
            ? input.Turn >= input.MaximumRunTurns || (input.Turn >= input.MaximumTurns
                && !input.SemanticNeedsMoreEvidence && input.ObservedCitableSources >= input.RequiredEvidenceCount)
            : input.Turn >= input.MaximumTurns || (!input.SeparateActionAndWriter
                && input.Turn >= input.MaximumTurns - 2 && input.HasCitableEvidence && !input.SemanticNeedsMoreEvidence);
        var reservation = input.SeparateActionAndWriter && input.RequiredEvidenceCount > 1
            && input.ObservedCitableSources >= input.RequiredEvidenceCount && input.Turn >= input.MaximumTurns - 2
            && !input.SemanticNeedsMoreEvidence;
        var selection = !input.CandidateCollectionOpen && (finalization || input.SelectionOnlyRequested
            || input.DecisionOnlyRequested || reservation);
        var flat = input.CandidateCollectionOpen && (input.CandidateAuditEnabled || input.UseFlatAdequacy)
            && !input.HasSemanticLayout && input.RequiredEvidenceCount > 1 && input.ObservedCitableSources > 0;
        var semanticSelection = !input.CandidateCollectionOpen && input.SeparateActionAndWriter
            && input.RequiredEvidenceCount > 0 && (input.RequiredEvidenceCount > 1 || input.RequireEvidenceSelection || input.SelectionOnlyRequested)
            && (input.SelectionOnlyRequested || reservation || (finalization && input.ObservedCitableSources >= input.RequiredEvidenceCount));
        var intent = input.DirectWriterRequested ? AgentIterationIntent.WriteAnswer
            : input.DedicatedCandidateAudit ? AgentIterationIntent.ReviewCandidates
            : semanticSelection ? AgentIterationIntent.SelectEvidence
            : finalization ? AgentIterationIntent.FinalizeAnswer : AgentIterationIntent.CollectEvidence;
        return new(intent, finalization, reservation, selection, flat, semanticSelection,
            semanticSelection && input.EnableWorkspaceWriterHandoff);
    }
}
