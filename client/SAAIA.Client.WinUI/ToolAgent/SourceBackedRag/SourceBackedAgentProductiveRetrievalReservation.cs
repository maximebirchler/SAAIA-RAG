namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private int ReserveProductiveRetrievalFollowUp(
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn,
        bool candidateCollectionOpen,
        int pendingSemanticCandidateCount,
        IReadOnlyList<string> newlyObservedEvidenceIds,
        int maximumRunTurns,
        int maximumRunTurnCeiling)
    {
        if (!_options.SeparateActionAndWriter
            || !_options.SemanticCandidateAuditEnabled
            || !candidateCollectionOpen
            || newlyObservedEvidenceIds.Count == 0
            || pendingSemanticCandidateCount == 0)
        {
            return maximumRunTurns;
        }

        var previousMaximumRunTurns = maximumRunTurns;
        maximumRunTurns = Math.Min(
            maximumRunTurnCeiling,
            Math.Max(maximumRunTurns, turn + 2));
        if (maximumRunTurns <= previousMaximumRunTurns)
            return maximumRunTurns;

        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.IterationController,
            "source_backed_agent_v2.productive_retrieval.follow_up_reserved",
            ("turn", turn),
            ("new_evidence_count", newlyObservedEvidenceIds.Count),
            ("new_evidence_ids", newlyObservedEvidenceIds),
            ("previous_maximum_run_turns", previousMaximumRunTurns),
            ("maximum_run_turns", maximumRunTurns),
            ("maximum_run_turn_ceiling", maximumRunTurnCeiling),
            ("audit_turn", turn + 1),
            ("writer_turn", turn + 2),
            ("decision_source", "mechanical_lifecycle_reservation")));
        return maximumRunTurns;
    }
}
