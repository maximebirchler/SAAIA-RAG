namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private bool TryActivateCumulativeBudgetFinalization(
        bool hasFinalizableEvidence,
        int executedRequestCount,
        int turn,
        ISet<string> pendingSemanticCandidateIds,
        ref bool candidateCollectionOpen,
        ref bool selectionOnlyNextTurn,
        ref bool decisionOnlyNextTurn,
        ref bool flatEvidenceGapTerminalDecisionNextTurn,
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        bool force = false)
    {
        var budget = SourceBackedLlmCumulativeBudgetContext.Current;
        var snapshot = budget?.GetSnapshot();
        if (snapshot is null
            || (!force && !snapshot.NormalBudgetClosed))
            return false;

        if (snapshot.RemainingTokens <= 0
            || snapshot.RemainingMilliseconds <= 0)
        {
            throw new SourceBackedLlmBudgetExceededException(
                snapshot.RemainingMilliseconds <= 0
                    ? "time_budget_exhausted"
                    : "token_budget_exhausted",
                snapshot);
        }

        if (!hasFinalizableEvidence && executedRequestCount == 0)
        {
            throw new SourceBackedLlmBudgetExceededException(
                "cumulative_budget_exhausted_before_document_observation",
                snapshot);
        }

        // The state transition is mechanical. It closes new exploration but
        // never selects an EvidenceId: Qwen retains that semantic decision.
        pendingSemanticCandidateIds.Clear();
        candidateCollectionOpen = false;
        decisionOnlyNextTurn = false;
        selectionOnlyNextTurn = hasFinalizableEvidence;
        flatEvidenceGapTerminalDecisionNextTurn = !hasFinalizableEvidence;
        var terminalContract = hasFinalizableEvidence
            ? "semantic_selection_then_writer"
            : BuildSemanticYieldTerminalDecisionTools()[0].Name;

        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.IterationController,
            "source_backed_agent_v2.cumulative_budget.terminal_budget_only",
            ("turn", turn),
            ("terminal_budget_only", true),
            ("charged_tokens", snapshot.ChargedTokens),
            ("reserved_tokens", snapshot.ReservedTokens),
            ("remaining_tokens", snapshot.RemainingTokens),
            ("remaining_ms", snapshot.RemainingMilliseconds),
            ("has_finalizable_evidence", hasFinalizableEvidence),
            ("executed_requests", executedRequestCount),
            ("terminal_contract", terminalContract),
            ("decision_source", "mechanical_budget_contract")));
        return true;
    }
}
