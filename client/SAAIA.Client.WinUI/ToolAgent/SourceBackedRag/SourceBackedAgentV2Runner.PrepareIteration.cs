using SAAIA.Client.Core.Execution;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private async Task<AgentIterationOutcome<SourceBackedPipelineResult>> PrepareIterationAsync(
        RunExecutionState runState, IterationExecutionState iteration, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        runState.lifecycle.BeginIteration(runState.turn);
        iteration.freshWriterOutputNeedsSemanticReview = false;
        ApplyGlobalStructuredCandidateMinimumGate(
            runState.useGlobalStructuredCandidateSelection, runState.candidateAuditEnabled,
            runState.bundle, runState.evidenceState.ObservedEvidenceIds, runState.evidenceState.SemanticallyAuditedEvidenceIds,
            runState.evidenceState.SemanticallyRejectedEvidenceIds, runState.requiredAtomicEvidenceCount,
            runState.candidatePoolTargetCount, runState.semanticRowLabels,
            runState.semanticColumnLabels, ref runState.candidateCollectionOpen,
            ref runState.selectionOnlyNextTurn, runState.traces, runState.traceId,
            ref runState.traceSequence, runState.turn);
        var candidateAuditTurn = PrepareCandidateAuditTurn(
            runState.candidateAuditEnabled, runState.candidateCollectionOpen,
            runState.bundle, runState.evidenceState.ObservedEvidenceIds,
            runState.evidenceState.PendingSemanticCandidateIds, runState.evidenceState.SemanticallyRejectedEvidenceIds);
        iteration.collectionCandidates = candidateAuditTurn.Candidates;
        iteration.dedicatedCandidateAuditTurn = candidateAuditTurn.IsDedicated;
        iteration.selectionEligibleObservedEvidenceIds = runState.evidenceState.ObservedEvidenceIds
            .Where(id => !runState.candidateAuditEnabled
                         || runState.evidenceState.SemanticallyAuditedEvidenceIds.Contains(id))
            .ToArray();
        iteration.hasCitableEvidence = iteration.selectionEligibleObservedEvidenceIds.Any(id =>
            runState.bundle.ById.TryGetValue(id, out var item)
            && !item.RiskFlags.Contains(
                "orientation_only",
                StringComparer.OrdinalIgnoreCase));
        iteration.observedCitableSourceCount = CountObservedSelectableEvidenceUnits(
            runState.bundle,
            iteration.selectionEligibleObservedEvidenceIds.Where(id =>
                !runState.evidenceState.SemanticallyRejectedEvidenceIds.Contains(id)),
            runState.atomicEvidenceMode);
        iteration.hasMechanicallyFinalizableEvidence = iteration.hasCitableEvidence
            && (runState.requiredAtomicEvidenceCount.GetValueOrDefault() <= 1
                || iteration.observedCitableSourceCount
                >= runState.requiredAtomicEvidenceCount.GetValueOrDefault());
        if (TryActivateCumulativeBudgetFinalization(
                iteration.hasMechanicallyFinalizableEvidence,
                runState.evidenceState.ExecutedRequests.Count,
                runState.turn,
                runState.evidenceState.PendingSemanticCandidateIds,
                ref runState.candidateCollectionOpen,
                ref runState.selectionOnlyNextTurn,
                ref runState.decisionOnlyNextTurn,
                ref runState.flatEvidenceGapTerminalDecisionNextTurn,
                runState.traces,
                runState.traceId,
                ref runState.traceSequence))
        {
            iteration.dedicatedCandidateAuditTurn = false;
        }
        if (runState.candidateCollectionEnabled)
            AddCandidateCollectionStateTrace(
                runState.traces, runState.traceId, ref runState.traceSequence, runState.turn, runState.candidateCollectionOpen,
                runState.evidenceState.ObservedEvidenceIds.Count, iteration.collectionCandidates.Count,
                runState.evidenceState.PendingSemanticCandidateIds.Count, runState.evidenceState.SemanticallyAuditedEvidenceIds.Count,
                runState.evidenceState.SemanticallyRejectedEvidenceIds.Count, iteration.observedCitableSourceCount);
        iteration.requiredEvidenceCount = runState.requiredAtomicEvidenceCount.GetValueOrDefault();
        iteration.semanticNeedsMoreEvidence = string.Equals(runState.semanticReviewDecision, "need_more_evidence", StringComparison.OrdinalIgnoreCase);
        var iterationPlan = AgentIterationPlanner.Plan(new(
            runState.turn, _options.MaximumTurns, runState.maximumRunTurns, _options.SeparateActionAndWriter,
            iteration.requiredEvidenceCount, iteration.hasCitableEvidence, iteration.observedCitableSourceCount,
            runState.candidateCollectionOpen, runState.selectionOnlyNextTurn, runState.decisionOnlyNextTurn,
            iteration.semanticNeedsMoreEvidence,
            runState.candidateAuditEnabled, runState.useFlatContentClaimAdequacy, runState.semanticLayoutDimensions is not null,
            runState.requireEvidenceSelection, runState.enableWorkspaceWriterHandoff, iteration.dedicatedCandidateAuditTurn, runState.directWriterRequested));
        iteration.finalizationTurn = iterationPlan.FinalizationTurn;
        var renderingReservationTurn = iterationPlan.RenderingReservationTurn;
        iteration.selectionDecisionTurn = iterationPlan.SelectionDecisionTurn;
        iteration.allowAdaptiveFlatSelection = iterationPlan.AllowAdaptiveFlatSelection;
        runState.lifecycle.PlanIntent(iterationPlan.Intent);
        iteration.flatEvidenceAdequacyCompletion = null;
        if (iteration.allowAdaptiveFlatSelection)
        {
            try
            {
                var checkpoint = await runState.adequacyCheckpoint.EvaluateAsync(iteration.selectionEligibleObservedEvidenceIds,
                    async (state, token) =>
                    {
                        var review = await ReviewFlatEvidenceAdequacyAsync(runState.intake, runState.semanticPlan, runState.bundle,
                            iteration.selectionEligibleObservedEvidenceIds, iteration.requiredEvidenceCount, runState.atomicEvidenceMode,
                            state.IncumbentIds, state.ReviewedEvidenceIds, runState.semanticReviewFeedback ?? string.Empty, token)
                            .ConfigureAwait(false);
                        TraceFlatEvidenceAdequacy(runState.traces, runState.traceId, ref runState.traceSequence, runState.turn,
                            review, iteration.selectionEligibleObservedEvidenceIds, iteration.requiredEvidenceCount);
                        return new EvidenceAdequacyReview<SourceBackedAgentCompletion>(review.ProtocolValid,
                            review.Decision, review.Reason, review.RoutedCompletion, review.TournamentSelectionEvidenceIds);
                    }, ct).ConfigureAwait(false);
                iteration.flatEvidenceAdequacyCompletion = checkpoint.Review?.RoutedCompletion;
                if (checkpoint.RequiresFeedback)
                    runState.semanticReviewFeedback = "CHECKPOINT D'ADEQUATION: continuer. Manque precise par le juge LLM: "
                        + checkpoint.Review!.Reason;
            }
            catch (SourceBackedLlmBudgetExceededException ex) when (string.Equals(ex.Reason, "terminal_budget_only", StringComparison.Ordinal))
            {
                TryActivateCumulativeBudgetFinalization(iteration.hasMechanicallyFinalizableEvidence,
                    runState.evidenceState.ExecutedRequests.Count, runState.turn, runState.evidenceState.PendingSemanticCandidateIds,
                    ref runState.candidateCollectionOpen, ref runState.selectionOnlyNextTurn, ref runState.decisionOnlyNextTurn,
                    ref runState.flatEvidenceGapTerminalDecisionNextTurn, runState.traces, runState.traceId, ref runState.traceSequence, force: true);
                runState.turn--;
                return AgentIterationOutcome<SourceBackedPipelineResult>.Continue;
            }
        }
        iteration.semanticSelectionDecisionTurn = iterationPlan.SemanticSelectionDecisionTurn;
        iteration.workspaceWriterDecisionTurn = iterationPlan.WorkspaceWriterDecisionTurn;
        iteration.semanticYieldTerminalDecisionTurn = ShouldRequestSemanticYieldTerminalDecision(
                runState.flatEvidenceGapTerminalDecisionNextTurn,
                runState.semanticYieldResolutionPending,
                runState.semanticYieldResolutionContinuationCount,
                runState.semanticYieldResolutionContinuationLimit,
                runState.candidateAuditEnabled,
                runState.evidenceState.PendingSemanticCandidateIds.Count,
                iteration.dedicatedCandidateAuditTurn);
        runState.compactSingleFollowUpNextTurn &= !(iteration.semanticSelectionDecisionTurn || iteration.semanticYieldTerminalDecisionTurn);
        if (iteration.semanticYieldTerminalDecisionTurn)
        {
            AddSemanticYieldTerminalDecisionRequestedTrace(
                runState.traces, runState.traceId, ref runState.traceSequence,
                runState.flatEvidenceGapTerminalDecisionNextTurn, runState.turn,
                runState.semanticYieldResolutionContinuationCount,
                runState.semanticYieldResolutionContinuationLimit,
                runState.candidateAuditEnabled, runState.evidenceState.PendingSemanticCandidateIds.Count,
                runState.consecutiveDocumentaryNoProgressTurns);
        }
        if (runState.turn == _options.MaximumTurns - 3)
            runState.messages.Add(SourceBackedAgentMessage.User(BuildLastExplorationTurnMessage()));
        if (iteration.finalizationTurn)
            runState.messages.Add(SourceBackedAgentMessage.User(BuildBudgetFinalizationMessage(
                _options.SeparateActionAndWriter)));
        else if (runState.selectionOnlyNextTurn)
            runState.messages.Add(SourceBackedAgentMessage.User(
                BuildNoProgressSelectionMessage(runState.requiredAtomicEvidenceCount)));
        else if (renderingReservationTurn)
            runState.messages.Add(SourceBackedAgentMessage.User(
                BuildRenderingBudgetSelectionMessage(runState.requiredAtomicEvidenceCount)));

        return AgentIterationOutcome<SourceBackedPipelineResult>.Advance;
    }
}
