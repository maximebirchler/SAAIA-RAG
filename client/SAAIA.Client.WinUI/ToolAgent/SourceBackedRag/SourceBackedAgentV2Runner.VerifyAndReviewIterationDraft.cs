using SAAIA.Client.Core.Execution;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private async Task<AgentIterationOutcome<SourceBackedPipelineResult>> VerifyAndReviewIterationDraftAsync(
        RunExecutionState runState, IterationExecutionState iteration, CancellationToken ct)
    {
        runState.latestDraft = BuildDraft(iteration.completion.Content);
        runState.firstDraft ??= runState.latestDraft;
        var selectedWriterVerification = VerifySelectedWriterDraft(
            runState.latestDraft,
            runState.bundle,
            runState.intake,
            runState.activeSemanticSelectionIds,
            runState.atomicEvidenceMode,
            iteration.structuredCandidateWriterTurn);
        var writerCitedContextEvidenceBeyondSemanticSelection =
            selectedWriterVerification.CitedContextEvidenceBeyondSemanticSelection;
        runState.verification = selectedWriterVerification.Verification;
        if (!string.IsNullOrWhiteSpace(
                iteration.structuredCandidateWriterProtocolError ??= iteration.completion.ProtocolError))
        {
            runState.verification = runState.verification with
            {
                IsValid = false,
                Errors = runState.verification.Errors.Concat(new[]
                {
                    new SourceVerificationError(
                        iteration.structuredCandidateWriterProtocolError!,
                        "Le writer structure n'a pas respecte le contrat mecanique "
                        + "de cardinalite, d'identifiants ou d'unicite des sources.",
                        null,
                        SourceBackedPipelineStep.SourceVerifier)
                }).ToArray()
            };
        }
        TraceVerifiedAnswer(runState.verification, selectedWriterVerification, writerCitedContextEvidenceBeyondSemanticSelection, runState.traces, runState.traceId, ref runState.traceSequence, runState.turn);
        if (!string.IsNullOrWhiteSpace(iteration.structuredCandidateWriterProtocolError))
        {
            return AgentIterationOutcome<SourceBackedPipelineResult>.Stop;
        }
        if (runState.verification.IsValid)
        {
            if (TryCompleteVerifiedAnswerWithoutFreshSemanticReview(
                    iteration.reviewDirectedRevisionCompletedThisTurn,
                    iteration.reviewDirectedRevisionExactClaimCountSatisfied,
                    runState.explicitSemanticSelectionActive,
                    iteration.freshWriterOutputNeedsSemanticReview,
                    runState.inlineFastAnswerRequiresIndependentSemanticReview,
                    writerCitedContextEvidenceBeyondSemanticSelection,
                    runState.activeSemanticSelectionIds,
                    runState.activeSemanticSelectionLayout,
                    runState.intake, runState.evidenceState.ExecutedRequests, runState.bundle, runState.firstDraft, runState.latestDraft,
                    runState.verification, runState.repaired, runState.evidenceState.SemanticallyRejectedEvidenceIds,
                    runState.traces, runState.traceId, ref runState.traceSequence, runState.turn,
                    out var verifiedResult))
                return AgentIterationOutcome<SourceBackedPipelineResult>.Complete(verifiedResult);

            using var cumulativeBudgetTerminalReviewScope =
                SourceBackedLlmCumulativeBudgetContext.PushTerminalCall();
            var semanticReview = runState.activeSemanticSelectionLayout is not null
                ? await ReviewStructuredAssignmentsAsync(
                        runState.intake,
                        runState.semanticPlan,
                        runState.latestDraft,
                        runState.bundle,
                        runState.evidenceState.ObservedEvidenceIds,
                        GetStructuredAssignmentRejectedCellIndexes(
                            runState.pendingStructuredAssignmentReview),
                        ct)
                    .ConfigureAwait(false)
                : await ReviewSemanticsAsync(
                        runState.intake,
                        runState.semanticPlan,
                        runState.latestDraft,
                        runState.bundle,
                        runState.evidenceState.ObservedEvidenceIds,
                        ct)
                    .ConfigureAwait(false);
            runState.semanticReviewReasons = semanticReview.Reasons;
            runState.semanticReviewDecision = semanticReview.Decision;
            var accumulatedStructuredRejections =
                AccumulateStructuredAssignmentRejections(
                    runState.intake,
                    runState.latestDraft,
                    semanticReview,
                    runState.structuredRejectedEvidenceByCell);
            AddTrace(runState.traces, Trace(
                runState.traceId,
                ref runState.traceSequence,
                SourceBackedPipelineStep.AnswerAdequacyJudge,
                "source_backed_agent_v2.semantic_review.completed",
                ("turn", runState.turn),
                ("decision", semanticReview.Decision),
                ("reasons", semanticReview.Reasons),
                ("contract_adjusted", semanticReview.ContractAdjusted),
                ("rejected_evidence_count", semanticReview.RejectedEvidenceIds.Count),
                ("rejected_evidence_ids", semanticReview.RejectedEvidenceIds),
                ("preferred_alternative_count",
                    semanticReview.PreferredAlternativeEvidenceIds.Count),
                ("preferred_alternative_evidence_ids",
                    semanticReview.PreferredAlternativeEvidenceIds),
                ("prompt_tokens", semanticReview.PromptTokens),
                ("completion_tokens", semanticReview.CompletionTokens),
                ("finish_reason", semanticReview.FinishReason),
                ("attempts", semanticReview.Attempts),
                ("truncation_retry_exhausted",
                    semanticReview.TruncationRetryExhausted),
                ("context_recovery_used",
                    semanticReview.ContextRecoveryUsed),
                ("evidence_context_mode", semanticReview.EvidenceContextMode),
                ("exact_input_tokens",
                    semanticReview.ExactInputTokens),
                ("cited_evidence_characters",
                    semanticReview.CitedEvidenceCharacters),
                ("accumulated_structured_assignment_rejections",
                    accumulatedStructuredRejections)));
            if (semanticReview.TruncationRetryExhausted)
            {
                if (runState.semanticResolutionWriterReviewActive)
                    AddSemanticResolutionPublicationBlockedTrace(runState.traces, runState.traceId, ref runState.traceSequence, runState.turn,
                        "semantic_review_protocol_failed",
                        new[] { semanticReview.Decision });
                AddTrace(runState.traces, Trace(
                    runState.traceId,
                    ref runState.traceSequence,
                    SourceBackedPipelineStep.AnswerAdequacyJudge,
                    "source_backed_agent_v2.semantic_review.protocol_failed",
                    ("turn", runState.turn),
                    ("attempts", semanticReview.Attempts),
                    ("finish_reason", semanticReview.FinishReason)));
                return AgentIterationOutcome<SourceBackedPipelineResult>.Stop;
            }
            if (!semanticReview.IsAccepted)
            {
                runState.repaired = true;
                foreach (var rejectedEvidenceId in semanticReview.RejectedEvidenceIds)
                {
                    runState.evidenceWorkspace.Reject(
                        rejectedEvidenceId,
                        string.Join(" ", semanticReview.Reasons.Take(2)));
                    runState.evidenceState.SemanticallyRejectedEvidenceIds.Add(rejectedEvidenceId);
                    runState.evidenceState.ObservedEvidenceIdSet.Remove(rejectedEvidenceId);
                    runState.evidenceState.ObservedEvidenceIds.RemoveAll(id => string.Equals(
                        id,
                        rejectedEvidenceId,
                        StringComparison.OrdinalIgnoreCase));
                }
                if (TryScheduleInlineFastAnswerRevision(
                        runState.inlineFastAnswerRequiresIndependentSemanticReview, runState.semanticResolutionWriterReviewActive,
                        runState.inlineFastAnswerDirectRevisionSignatures, Math.Max(1, _options.MaximumSemanticCorrectionTurns), semanticReview,
                        runState.bundle, runState.evidenceState.ObservedEvidenceIds, runState.evidenceState.SemanticallyRejectedEvidenceIds, ref runState.activeSemanticSelectionIds, runState.activeSemanticSelectionLayout,
                        ref runState.semanticReviewFeedback, ref runState.directWriterRevisionInstruction, ref runState.directWriterRequested, ref runState.candidateCollectionOpen,
                        ref runState.selectionOnlyNextTurn,
                        ref runState.decisionOnlyNextTurn, runState.traces, runState.traceId, ref runState.traceSequence, runState.turn))
                {
                    runState.commitNextVerifiedReviewDirectedRevision =
                        !runState.semanticResolutionWriterReviewActive
                        && runState.requiredAtomicEvidenceCount.GetValueOrDefault() <= 1
                        && CanCommitReviewDirectedRevisionAfterSourceVerification(
                            semanticReview);
                    runState.reviewDirectedRevisionRequiredClaimCount = null;
                    return AgentIterationOutcome<SourceBackedPipelineResult>.Continue;
                }
                if (runState.semanticResolutionWriterReviewActive)
                {
                    AddSemanticResolutionPublicationBlockedTrace(runState.traces, runState.traceId, ref runState.traceSequence, runState.turn,
                        semanticReview.Decision, semanticReview.Reasons,
                        runState.inlineFastAnswerDirectRevisionSignatures.Count);
                    return AgentIterationOutcome<SourceBackedPipelineResult>.Stop;
                }
                if (runState.inlineFastAnswerRequiresIndependentSemanticReview)
                {
                    runState.inlineFastAnswerRequiresIndependentSemanticReview = false;
                    runState.explicitSemanticSelectionActive = false;
                }
                if (TryScheduleFlatNamedItemRevision(runState.useFlatContentClaimAdequacy, runState.activeSemanticSelectionIds, runState.activeSemanticSelectionLayout, semanticReview, runState.contentClaimDirectRevisionSelectionSignatures, ref runState.semanticReviewFeedback, ref runState.directWriterRevisionInstruction, ref runState.directWriterRequested, ref runState.candidateCollectionOpen, ref runState.selectionOnlyNextTurn, ref runState.decisionOnlyNextTurn, runState.traces, runState.traceId, ref runState.traceSequence, runState.turn))
                {
                    runState.commitNextVerifiedReviewDirectedRevision = false;
                    runState.reviewDirectedRevisionRequiredClaimCount = null;
                    return AgentIterationOutcome<SourceBackedPipelineResult>.Continue;
                }
                var normalizedRejectedDraft = NormalizeDraftForProgressComparison(
                    runState.latestDraft.Answer);
                var revisionMadeNoProgress = string.Equals(
                    semanticReview.Decision,
                    "revise",
                    StringComparison.OrdinalIgnoreCase)
                    && !runState.semanticallyRejectedDraftSignatures.Add(
                        normalizedRejectedDraft);
                if (revisionMadeNoProgress)
                {
                    runState.semanticReviewDecision = "need_more_evidence";
                    runState.semanticReviewFeedback = BuildSemanticRevisionNoProgressFeedback(
                        semanticReview);
                    CompactWorkingMessages(
                        runState.messages,
                        runState.intake,
                        runState.semanticPlan,
                        runState.bundle,
                        runState.evidenceState.ObservedEvidenceIds,
                        runState.evidenceState.ExecutedRequests,
                        runState.evidenceWorkspace,
                        runState.semanticReviewFeedback);
                    runState.directWriterRequested = false;
                    runState.activeSemanticSelectionIds = null;
                    runState.activeSemanticSelectionLayout = null;
                    runState.pendingStructuredAssignmentReview = null;
                    runState.pendingStructuredAssignmentDraft = null;
                    runState.selectionOnlyNextTurn = false;
                    runState.candidateCollectionOpen = runState.candidateCollectionEnabled;
                    AddTrace(runState.traces, Trace(
                        runState.traceId,
                        ref runState.traceSequence,
                        SourceBackedPipelineStep.IterationController,
                        "source_backed_agent_v2.semantic_revision.no_progress_returned_to_orchestrator",
                        ("turn", runState.turn),
                        ("answer_characters", runState.latestDraft.Answer.Length),
                        ("seen_rejected_drafts",
                            runState.semanticallyRejectedDraftSignatures.Count),
                        ("cited_evidence", runState.verification.CitedEvidence.Count)));
                    return AgentIterationOutcome<SourceBackedPipelineResult>.Continue;
                }
                if (TryScheduleContentClaimDirectRevision(
                        runState.useFlatContentClaimAdequacy,
                        runState.activeSemanticSelectionIds,
                        runState.activeSemanticSelectionLayout,
                        semanticReview,
                        runState.contentClaimDirectRevisionSelectionSignatures,
                        runState.boundedNamedDocumentExtraction,
                        runState.requiredAtomicEvidenceCount,
                        ref runState.semanticReviewFeedback,
                        ref runState.directWriterRevisionInstruction,
                        ref runState.directWriterRequested,
                        ref runState.commitNextVerifiedReviewDirectedRevision,
                        ref runState.reviewDirectedRevisionRequiredClaimCount,
                        ref runState.candidateCollectionOpen,
                        ref runState.selectionOnlyNextTurn,
                        ref runState.decisionOnlyNextTurn,
                        runState.traces, runState.traceId, ref runState.traceSequence, runState.turn,
                        out var flatContentClaimRevisionCandidate,
                        out var contentClaimSelectionSignature))
                    return AgentIterationOutcome<SourceBackedPipelineResult>.Continue;
                if (flatContentClaimRevisionCandidate)
                {
                    runState.semanticReviewFeedback = BuildContentClaimRevisionBudgetFeedback(
                        semanticReview);
                    CompactWorkingMessages(
                        runState.messages,
                        runState.intake,
                        runState.semanticPlan,
                        runState.bundle,
                        runState.evidenceState.ObservedEvidenceIds,
                        runState.evidenceState.ExecutedRequests,
                        runState.evidenceWorkspace,
                        runState.semanticReviewFeedback);
                    runState.directWriterRequested = false;
                    runState.directWriterRevisionInstruction = null;
                    runState.activeSemanticSelectionIds = null;
                    runState.activeSemanticSelectionLayout = null;
                    runState.pendingStructuredAssignmentReview = null;
                    runState.pendingStructuredAssignmentDraft = null;
                    runState.selectionOnlyNextTurn = false;
                    runState.decisionOnlyNextTurn = false;
                    runState.candidateCollectionOpen = runState.candidateCollectionEnabled;
                    TraceContentClaimRevisionBudget(
                        runState.traces, runState.traceId, ref runState.traceSequence, runState.turn,
                        contentClaimSelectionSignature,
                        semanticReview.Reasons.Count);
                    return AgentIterationOutcome<SourceBackedPipelineResult>.Continue;
                }
                var structuredAssignmentRevision =
                    iteration.structuredCandidateWriterTurn
                    && string.Equals(
                        semanticReview.FinishReason,
                        StructuredAssignmentBatchedReviewFinishReason,
                        StringComparison.OrdinalIgnoreCase);
                var unselectedStructuredCandidateCount = structuredAssignmentRevision
                    ? iteration.selectableEvidenceIds.Count(id =>
                        !runState.latestDraft.CitedEvidenceIds.Contains(
                            id,
                            StringComparer.OrdinalIgnoreCase))
                    : 0;
                var freshEvidenceLifecycleAvailable =
                    runState.turn + 3 <= runState.maximumRunTurnCeiling;
                var assignmentCollectionDecision =
                    EvaluateStructuredAssignmentCollectionNeed(
                        structuredAssignmentRevision,
                        semanticReview,
                        runState.pendingStructuredAssignmentReview,
                        unselectedStructuredCandidateCount,
                        iteration.observedCitableSourceCount);
                var assignmentContinuationResolution =
                    await ResolveRepeatedStructuredAssignmentContinuationAsync(
                        runState.intake, runState.bundle, runState.latestDraft, semanticReview,
                        iteration.selectableEvidenceIds, runState.structuredRejectedEvidenceByCell,
                        freshEvidenceLifecycleAvailable, iteration.observedCitableSourceCount,
                        assignmentCollectionDecision, runState.traces, runState.traceId,
                        runState.traceSequence, runState.turn, ct).ConfigureAwait(false);
                assignmentCollectionDecision =
                    assignmentContinuationResolution.Decision;
                runState.traceSequence = assignmentContinuationResolution.TraceSequence;
                TraceStructuredAssignmentCollectionDecision(
                    runState.traces, runState.traceId, ref runState.traceSequence, runState.turn,
                    assignmentCollectionDecision,
                    freshEvidenceLifecycleAvailable);
                var rejectedStructuredCellCount = assignmentCollectionDecision.RejectedCellCount;
                var missingStructuredAlternativeCount = assignmentCollectionDecision.MissingAlternativeCount;
                var expandedCandidatePoolTarget = assignmentCollectionDecision.CandidatePoolTargetCount;
                var structuredAssignmentRequiresCollection = assignmentCollectionDecision.RequiresCollection;
                runState.pendingStructuredAssignmentReview = structuredAssignmentRevision
                    ? semanticReview
                    : null;
                runState.pendingStructuredAssignmentDraft = structuredAssignmentRevision
                    ? runState.latestDraft
                    : null;
                runState.semanticReviewFeedback = structuredAssignmentRequiresCollection
                    ? BuildStructuredAssignmentEvidenceGapFeedback(
                        runState.intake,
                        runState.latestDraft,
                        semanticReview,
                        rejectedStructuredCellCount,
                        unselectedStructuredCandidateCount,
                        assignmentCollectionDecision.RepeatedRejectedCellCount,
                        expandedCandidatePoolTarget,
                        assignmentCollectionDecision.ResearchNeed)
                    : structuredAssignmentRevision
                    ? BuildStructuredAssignmentRevisionFeedback(
                        runState.intake,
                        semanticReview,
                        runState.latestDraft,
                        runState.activeSemanticSelectionIds
                        ?? runState.latestDraft.CitedEvidenceIds)
                    : BuildSemanticJudgeFeedback(semanticReview);
                if (_options.SeparateActionAndWriter)
                {
                    var reviseStructuredAssignments =
                        iteration.structuredCandidateWriterTurn
                        && string.Equals(
                            semanticReview.Decision,
                            "revise",
                            StringComparison.OrdinalIgnoreCase)
                        && semanticReview.RejectedEvidenceIds.Count == 0
                        && !structuredAssignmentRequiresCollection;
                    if (structuredAssignmentRequiresCollection)
                    {
                        runState.semanticReviewDecision = "need_more_evidence";
                        runState.candidatePoolTargetCount = expandedCandidatePoolTarget;
                        runState.candidateCollectionOpen = true;
                        runState.selectionOnlyNextTurn = false;
                        runState.maximumRunTurns = Math.Min(
                            runState.maximumRunTurnCeiling,
                            Math.Max(runState.maximumRunTurns, runState.turn + 3));
                        AddTrace(runState.traces, Trace(
                            runState.traceId,
                            ref runState.traceSequence,
                            SourceBackedPipelineStep.IterationController,
                            "source_backed_agent_v2.structured_assignment_revision.collection_reopened",
                            ("turn", runState.turn),
                            ("rejected_cells", rejectedStructuredCellCount),
                            ("unselected_candidates", unselectedStructuredCandidateCount),
                            ("missing_alternatives", missingStructuredAlternativeCount),
                            ("repeated_rejected_cells",
                                assignmentCollectionDecision
                                    .RepeatedRejectedCellCount),
                            ("observed_candidates", iteration.observedCitableSourceCount),
                            ("candidate_pool_target", runState.candidatePoolTargetCount),
                            ("decision_source",
                                assignmentCollectionDecision.DecisionSource)));
                    }
                    if (string.Equals(
                            semanticReview.Decision,
                            "need_more_evidence",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        runState.candidateCollectionOpen = runState.candidateCollectionEnabled;
                    }
                    CompactWorkingMessages(
                        runState.messages,
                        runState.intake,
                        runState.semanticPlan,
                        runState.bundle,
                        runState.evidenceState.ObservedEvidenceIds,
                        runState.evidenceState.ExecutedRequests,
                        runState.evidenceWorkspace,
                        runState.semanticReviewFeedback);
                    runState.directWriterRequested = false;
                    runState.activeSemanticSelectionIds = null;
                    runState.activeSemanticSelectionLayout = null;
                    runState.selectionOnlyNextTurn = reviseStructuredAssignments;
                    if (reviseStructuredAssignments)
                    {
                        runState.maximumRunTurns = Math.Min(
                            runState.maximumRunTurnCeiling,
                            Math.Max(runState.maximumRunTurns, runState.turn + 2));
                    }
                    return AgentIterationOutcome<SourceBackedPipelineResult>.Continue;
                }

                runState.messages.Add(SourceBackedAgentMessage.Assistant(iteration.completion.Content));
                runState.messages.Add(SourceBackedAgentMessage.User(runState.semanticReviewFeedback));
                runState.directWriterRequested = false;
                return AgentIterationOutcome<SourceBackedPipelineResult>.Continue;
            }

            runState.semanticAccepted = true;
            return AgentIterationOutcome<SourceBackedPipelineResult>.Complete(BuildResult(
                runState.intake,
                runState.evidenceState.ExecutedRequests,
                runState.bundle,
                runState.firstDraft,
                runState.latestDraft,
                runState.verification,
                runState.repaired,
                runState.semanticAccepted,
                runState.evidenceState.SemanticallyRejectedEvidenceIds,
                runState.semanticReviewReasons,
                runState.traces));
        }

        runState.repaired = true;
        var mechanicalFailureSignature =
            BuildMechanicalFailureSignature(runState.verification);
        if (string.Equals(
                runState.lastMechanicalFailureSignature,
                mechanicalFailureSignature,
                StringComparison.Ordinal))
        {
            AddTrace(runState.traces, Trace(
                runState.traceId,
                ref runState.traceSequence,
                SourceBackedPipelineStep.IterationController,
                "source_backed_agent_v2.mechanical_repair.no_progress_stopped",
                ("turn", runState.turn),
                ("failure_signature", mechanicalFailureSignature),
                ("answer_characters", runState.latestDraft.Answer.Length),
                ("cited_evidence", runState.verification.CitedEvidence.Count)));
            return AgentIterationOutcome<SourceBackedPipelineResult>.Stop;
        }
        runState.lastMechanicalFailureSignature = mechanicalFailureSignature;
        if (iteration.structuredCandidateWriterTurn)
        {
            AddTrace(runState.traces, Trace(
                runState.traceId,
                ref runState.traceSequence,
                SourceBackedPipelineStep.IterationController,
                "source_backed_agent_v2.structured_candidate_writer.mechanical_repair_exhausted",
                ("turn", runState.turn),
                ("failure_signature", mechanicalFailureSignature),
                ("answer_characters", runState.latestDraft.Answer.Length),
                ("cited_evidence", runState.verification.CitedEvidence.Count),
                ("decision", "terminal_insufficient_evidence")));
            return AgentIterationOutcome<SourceBackedPipelineResult>.Stop;
        }
        if (runState.activeSemanticSelectionLayout is not null)
        {
            runState.semanticReviewFeedback = BuildMechanicalRepairMessage(runState.verification);
            CompactWorkingMessages(
                runState.messages,
                runState.intake,
                runState.semanticPlan,
                runState.bundle,
                runState.evidenceState.ObservedEvidenceIds,
                runState.evidenceState.ExecutedRequests,
                runState.evidenceWorkspace,
                runState.semanticReviewFeedback);
            runState.activeSemanticSelectionIds = null;
            runState.activeSemanticSelectionLayout = null;
            runState.directWriterRequested = false;
            return AgentIterationOutcome<SourceBackedPipelineResult>.Continue;
        }

        runState.messages.Add(SourceBackedAgentMessage.Assistant(iteration.completion.Content));
        runState.messages.Add(SourceBackedAgentMessage.User(BuildMechanicalRepairMessage(runState.verification)));
        runState.directWriterRequested = _options.SeparateActionAndWriter;

        return AgentIterationOutcome<SourceBackedPipelineResult>.Advance;
    }
}
