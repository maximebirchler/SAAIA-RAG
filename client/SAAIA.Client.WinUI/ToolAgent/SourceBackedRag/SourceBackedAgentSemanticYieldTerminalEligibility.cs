namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private void AddSemanticYieldTerminalDecisionRequestedTrace(
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        bool flatEvidenceGapTerminalDecisionNextTurn,
        int turn,
        int semanticYieldResolutionContinuationCount,
        int semanticYieldResolutionContinuationLimit,
        bool candidateAuditEnabled,
        int pendingSemanticCandidateCount,
        int consecutiveDocumentaryNoProgressTurns)
        => AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.IterationController,
            flatEvidenceGapTerminalDecisionNextTurn
                ? "source_backed_agent_v2.documentary_no_progress.terminal_decision_requested"
                : "source_backed_agent_v2.semantic_audit_zero_yield.terminal_decision_requested",
            ("turn", turn),
            ("continuations", semanticYieldResolutionContinuationCount),
            ("maximum_continuations", semanticYieldResolutionContinuationLimit),
            ("candidate_audit_enabled", candidateAuditEnabled),
            ("pending_candidates", pendingSemanticCandidateCount),
            ("consecutive_no_progress_turns", consecutiveDocumentaryNoProgressTurns),
            ("decision_source", "mechanical_budget_contract")));

    private static bool ShouldRequestSemanticYieldTerminalDecision(
        bool flatEvidenceGapTerminalDecisionNextTurn,
        bool semanticYieldResolutionPending,
        int semanticYieldResolutionContinuationCount,
        int semanticYieldResolutionContinuationLimit,
        bool candidateAuditEnabled,
        int pendingSemanticCandidateCount,
        bool dedicatedCandidateAuditTurn)
    {
        if (flatEvidenceGapTerminalDecisionNextTurn)
            return true;

        return semanticYieldResolutionPending
               && semanticYieldResolutionContinuationCount
               >= semanticYieldResolutionContinuationLimit
               && (!candidateAuditEnabled || pendingSemanticCandidateCount == 0)
               && !dedicatedCandidateAuditTurn;
    }

    internal static bool ShouldRequestSemanticYieldTerminalDecisionForTests(
        bool flatEvidenceGapTerminalDecisionNextTurn,
        bool semanticYieldResolutionPending,
        int semanticYieldResolutionContinuationCount,
        int semanticYieldResolutionContinuationLimit,
        bool candidateAuditEnabled,
        int pendingSemanticCandidateCount,
        bool dedicatedCandidateAuditTurn)
        => ShouldRequestSemanticYieldTerminalDecision(
            flatEvidenceGapTerminalDecisionNextTurn,
            semanticYieldResolutionPending,
            semanticYieldResolutionContinuationCount,
            semanticYieldResolutionContinuationLimit,
            candidateAuditEnabled,
            pendingSemanticCandidateCount,
            dedicatedCandidateAuditTurn);
}
