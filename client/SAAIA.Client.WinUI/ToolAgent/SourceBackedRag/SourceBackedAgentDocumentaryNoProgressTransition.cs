namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private void ApplyDocumentaryNoProgressTransition(
        SourceBackedIntake intake,
        string semanticPlan,
        EvidenceBundle bundle,
        IReadOnlyList<string> observedEvidenceIds,
        IReadOnlyList<RetrievalRequest> executedRequests,
        LlmEvidenceWorkspace evidenceWorkspace,
        ICollection<SourceBackedAgentMessage> messages,
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn,
        int? requiredAtomicEvidenceCount,
        bool requireEvidenceSelection,
        bool hasCitableEvidence,
        int observedCitableSourceCount,
        int consecutiveDocumentaryNoProgressTurns,
        int duplicateCallCount,
        int zeroNewEvidenceCallCount,
        bool flatEvidenceAdequacyGapActive,
        IReadOnlyList<string> flatEvidenceAdequacyIncumbentEvidenceIds,
        ref bool candidateCollectionOpen,
        ref bool selectionOnlyNextTurn,
        ref bool decisionOnlyNextTurn,
        ref bool compactSingleFollowUpNextTurn,
        ref bool yieldResolutionNextTurn,
        ref IReadOnlyList<string> compactFollowUpLeadEvidenceIds,
        ref string? semanticReviewFeedback,
        ref string? transientProtocolFeedback,
        ref bool flatEvidenceGapTerminalDecisionNextTurn)
    {
        if (_options.SeparateActionAndWriter
            && requiredAtomicEvidenceCount is > 0
            && (requiredAtomicEvidenceCount > 1 || requireEvidenceSelection)
            && hasCitableEvidence
            && observedCitableSourceCount >= requiredAtomicEvidenceCount.Value
            && consecutiveDocumentaryNoProgressTurns >= 2)
        {
            if (flatEvidenceAdequacyGapActive)
            {
                selectionOnlyNextTurn = false;
                decisionOnlyNextTurn = false;
                compactFollowUpLeadEvidenceIds =
                    flatEvidenceAdequacyIncumbentEvidenceIds;
                transientProtocolFeedback =
                    BuildSemanticGapNoProgressResolutionMessage();
                if (consecutiveDocumentaryNoProgressTurns >= 3)
                {
                    compactSingleFollowUpNextTurn = false;
                    yieldResolutionNextTurn = false;
                    flatEvidenceGapTerminalDecisionNextTurn = true;
                    CompactWorkingMessages(
                        messages, intake, semanticPlan, bundle,
                        observedEvidenceIds, executedRequests, evidenceWorkspace,
                        MergeAgentFeedback(
                            semanticReviewFeedback,
                            transientProtocolFeedback));
                    AddTrace(traces, Trace(
                        traceId,
                        ref traceSequence,
                        SourceBackedPipelineStep.IterationController,
                        "source_backed_agent_v2.documentary_no_progress.terminal_decision_scheduled",
                        ("turn", turn),
                        ("consecutive_no_progress_turns",
                            consecutiveDocumentaryNoProgressTurns),
                        ("duplicate_calls", duplicateCallCount),
                        ("zero_new_evidence_calls", zeroNewEvidenceCallCount),
                        ("required", requiredAtomicEvidenceCount.Value),
                        ("observed", observedCitableSourceCount),
                        ("lead_evidence_ids", compactFollowUpLeadEvidenceIds),
                        ("decision_source", "mechanical_budget_contract")));
                    return;
                }

                compactSingleFollowUpNextTurn = true;
                yieldResolutionNextTurn = true;
                flatEvidenceGapTerminalDecisionNextTurn = false;
                CompactWorkingMessages(
                    messages, intake, semanticPlan, bundle,
                    observedEvidenceIds, executedRequests, evidenceWorkspace,
                    MergeAgentFeedback(
                        semanticReviewFeedback,
                        transientProtocolFeedback));
                AddTrace(traces, Trace(
                    traceId,
                    ref traceSequence,
                    SourceBackedPipelineStep.IterationController,
                    "source_backed_agent_v2.documentary_no_progress.recovery_requested",
                    ("turn", turn),
                    ("consecutive_no_progress_turns",
                        consecutiveDocumentaryNoProgressTurns),
                    ("duplicate_calls", duplicateCallCount),
                    ("zero_new_evidence_calls", zeroNewEvidenceCallCount),
                    ("required", requiredAtomicEvidenceCount.Value),
                    ("observed", observedCitableSourceCount),
                    ("semantic_gap_active", true),
                    ("lead_evidence_ids", compactFollowUpLeadEvidenceIds)));
                return;
            }

            candidateCollectionOpen = false;
            selectionOnlyNextTurn = true;
            semanticReviewFeedback = BuildNoProgressSelectionMessage(
                requiredAtomicEvidenceCount);
            CompactWorkingMessages(
                messages, intake, semanticPlan, bundle,
                observedEvidenceIds, executedRequests, evidenceWorkspace,
                semanticReviewFeedback);
            AddTrace(traces, Trace(
                traceId,
                ref traceSequence,
                SourceBackedPipelineStep.IterationController,
                "source_backed_agent_v2.documentary_no_progress.selection_requested",
                ("turn", turn),
                ("consecutive_no_progress_turns",
                    consecutiveDocumentaryNoProgressTurns),
                ("duplicate_calls", duplicateCallCount),
                ("zero_new_evidence_calls", zeroNewEvidenceCallCount),
                ("required", requiredAtomicEvidenceCount.Value),
                ("observed", observedCitableSourceCount),
                ("semantic_gap_active", false)));
            return;
        }

        if (_options.SeparateActionAndWriter
            && requiredAtomicEvidenceCount is > 1
            && observedCitableSourceCount < requiredAtomicEvidenceCount.Value
            && consecutiveDocumentaryNoProgressTurns >= 2)
        {
            decisionOnlyNextTurn = false;
            compactSingleFollowUpNextTurn = true;
            yieldResolutionNextTurn = true;
            compactFollowUpLeadEvidenceIds = Array.Empty<string>();
            transientProtocolFeedback =
                BuildAtomicEvidenceCountActionRepairMessage(
                    requiredAtomicEvidenceCount.Value,
                    observedCitableSourceCount);
            CompactWorkingMessages(
                messages, intake, semanticPlan, bundle,
                observedEvidenceIds, executedRequests, evidenceWorkspace,
                MergeAgentFeedback(
                    semanticReviewFeedback,
                    transientProtocolFeedback));
            AddTrace(traces, Trace(
                traceId,
                ref traceSequence,
                SourceBackedPipelineStep.IterationController,
                "source_backed_agent_v2.documentary_no_progress.recovery_requested",
                ("turn", turn),
                ("consecutive_no_progress_turns",
                    consecutiveDocumentaryNoProgressTurns),
                ("duplicate_calls", duplicateCallCount),
                ("zero_new_evidence_calls", zeroNewEvidenceCallCount),
                ("required", requiredAtomicEvidenceCount.Value),
                ("observed", observedCitableSourceCount)));
            return;
        }

        if (_options.SeparateActionAndWriter
            && consecutiveDocumentaryNoProgressTurns >= 2)
        {
            decisionOnlyNextTurn = true;
            transientProtocolFeedback =
                BuildNoProgressDecisionMessage(hasCitableEvidence);
            CompactWorkingMessages(
                messages, intake, semanticPlan, bundle,
                observedEvidenceIds, executedRequests, evidenceWorkspace,
                MergeAgentFeedback(
                    semanticReviewFeedback,
                    transientProtocolFeedback));
            AddTrace(traces, Trace(
                traceId,
                ref traceSequence,
                SourceBackedPipelineStep.IterationController,
                "source_backed_agent_v2.documentary_no_progress.decision_requested",
                ("turn", turn),
                ("consecutive_no_progress_turns",
                    consecutiveDocumentaryNoProgressTurns),
                ("duplicate_calls", duplicateCallCount),
                ("zero_new_evidence_calls", zeroNewEvidenceCallCount),
                ("has_citable_evidence", hasCitableEvidence),
                ("observed", observedCitableSourceCount)));
            return;
        }

        transientProtocolFeedback = BuildDocumentaryNoProgressRepairMessage(
            hasCitableEvidence,
            duplicateCallCount,
            zeroNewEvidenceCallCount);
        CompactWorkingMessages(
            messages, intake, semanticPlan, bundle,
            observedEvidenceIds, executedRequests, evidenceWorkspace,
            MergeAgentFeedback(
                semanticReviewFeedback,
                transientProtocolFeedback));
    }
}
