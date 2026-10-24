using SAAIA.Client.Core.Execution;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private async Task<AgentIterationOutcome<SourceBackedPipelineResult>> ProcessIterationSubmissionsAndToolsAsync(
        RunExecutionState runState, IterationExecutionState iteration, CancellationToken ct)
    {
        var semanticYieldTerminalSubmission =
            EvaluateSemanticYieldTerminalDecision(
                iteration.completion,
                iteration.semanticYieldTerminalDecisionTurn,
                runState.intake,
                runState.evidenceState.ExecutedRequests,
                runState.bundle,
                runState.firstDraft,
                runState.latestDraft,
                runState.verification,
                runState.repaired,
                runState.evidenceState.SemanticallyRejectedEvidenceIds,
                runState.traces,
                runState.traceId,
                ref runState.traceSequence,
                runState.turn,
                runState.consecutiveDocumentaryNoProgressTurns,
                iteration.observedCitableSourceCount);
        if (semanticYieldTerminalSubmission.Submitted)
        {
            if (semanticYieldTerminalSubmission.Result is not null)
                return AgentIterationOutcome<SourceBackedPipelineResult>.Complete(semanticYieldTerminalSubmission.Result);
            runState.semanticYieldTerminalProtocolAttemptCount++;
            runState.messages.Add(SourceBackedAgentMessage.Assistant(
                iteration.completion.Content,
                iteration.completion.ToolCalls));
            foreach (var call in iteration.completion.ToolCalls)
            {
                runState.messages.Add(SourceBackedAgentMessage.Tool(
                    call.Id,
                    call.Name,
                    SourceBackedAgentObservationCompactor.BuildProtocolError(
                        call.Name,
                        semanticYieldTerminalSubmission.FailureReason,
                        "Choisis clarification ou insufficiency et fournis un message concis.")));
            }
            runState.transientProtocolFeedback =
                "La décision terminale ne respecte pas son contrat; corrige uniquement ses deux champs.";
            runState.compactSingleFollowUpNextTurn = true;
            runState.yieldResolutionNextTurn = true;
            runState.maximumRunTurns = Math.Min(
                runState.maximumRunTurns,
                runState.turn + (runState.semanticYieldTerminalProtocolAttemptCount < 2 ? 1 : 0));
            if (runState.semanticYieldTerminalProtocolAttemptCount >= 2)
            {
                AddTrace(runState.traces, Trace(
                    runState.traceId,
                    ref runState.traceSequence,
                    SourceBackedPipelineStep.IterationController,
                    "source_backed_agent_v2.semantic_audit_zero_yield.terminal_protocol_exhausted",
                    ("turn", runState.turn),
                    ("attempts", runState.semanticYieldTerminalProtocolAttemptCount),
                    ("decision_source", "mechanical_protocol_budget")));
            }
            return AgentIterationOutcome<SourceBackedPipelineResult>.Continue;
        }
        var insufficiencySubmission =
            EvaluateSourceInsufficiencySubmission(
                iteration.completion,
                iteration.yieldResolutionTurn,
                runState.intake,
                runState.evidenceState.ExecutedRequests,
                runState.bundle,
                runState.firstDraft,
                runState.latestDraft,
                runState.verification,
                runState.repaired,
                runState.evidenceState.SemanticallyRejectedEvidenceIds,
                runState.traces,
                runState.traceId,
                ref runState.traceSequence,
                runState.turn,
                runState.consecutiveDocumentaryNoProgressTurns,
                iteration.observedCitableSourceCount);
        if (insufficiencySubmission.Submitted)
        {
            if (insufficiencySubmission.Result is not null)
                return AgentIterationOutcome<SourceBackedPipelineResult>.Complete(insufficiencySubmission.Result);
            runState.messages.Add(SourceBackedAgentMessage.Assistant(
                iteration.completion.Content,
                iteration.completion.ToolCalls));
            foreach (var call in iteration.completion.ToolCalls)
            {
                runState.messages.Add(SourceBackedAgentMessage.Tool(
                    call.Id,
                    call.Name,
                    SourceBackedAgentObservationCompactor.BuildProtocolError(
                        call.Name,
                        insufficiencySubmission.FailureReason,
                        "Déclare l'insuffisance uniquement pendant une résolution de rendement, ou choisis une action documentaire autorisée.")));
            }
            runState.transientProtocolFeedback =
                "La déclaration d'insuffisance ne respecte pas son contrat; corrige-la ou poursuis la recherche.";
            if (iteration.yieldResolutionTurn)
            {
                runState.compactSingleFollowUpNextTurn = true;
                runState.yieldResolutionNextTurn = true;
            }
            return AgentIterationOutcome<SourceBackedPipelineResult>.Continue;
        }
        var clarificationSubmission =
            EvaluateSourceBackedClarificationSubmission(
                iteration.completion,
                runState.intake,
                runState.evidenceState.ExecutedRequests,
                runState.bundle,
                runState.firstDraft,
                runState.latestDraft,
                runState.verification,
                runState.repaired,
                runState.evidenceState.SemanticallyRejectedEvidenceIds,
                runState.traces,
                runState.traceId,
                ref runState.traceSequence,
                runState.turn,
                runState.evidenceState.ObservedEvidenceIds.Count);
        if (clarificationSubmission.Submitted)
        {
            if (clarificationSubmission.Result is not null)
                return AgentIterationOutcome<SourceBackedPipelineResult>.Complete(clarificationSubmission.Result);
            runState.messages.Add(SourceBackedAgentMessage.Assistant(
                iteration.completion.Content,
                iteration.completion.ToolCalls));
            foreach (var call in iteration.completion.ToolCalls)
            {
                runState.messages.Add(SourceBackedAgentMessage.Tool(
                    call.Id,
                    call.Name,
                    SourceBackedAgentObservationCompactor.BuildProtocolError(
                        call.Name,
                        clarificationSubmission.FailureReason,
                        "La clarification doit etre l'unique action et n'est disponible qu'apres une observation du corpus. Corrige l'appel ou poursuis avec un outil documentaire.")));
            }
            runState.transientProtocolFeedback =
                "La clarification soumise ne respecte pas son contrat; corrige-la ou poursuis la recherche.";
            return AgentIterationOutcome<SourceBackedPipelineResult>.Continue;
        }

        var semanticSelectionHandling =
            await ProcessSemanticSelectionSubmissionAsync(
                    iteration.completion, runState.requiredAtomicEvidenceCount, runState.requireEvidenceSelection,
                    iteration.allowAdaptiveFlatSelection, runState.atomicEvidenceMode,
                    runState.semanticColumnLabels, runState.semanticRowHeader, runState.semanticRowLabels, runState.bundle,
                    iteration.selectionEligibleObservedEvidenceIds, runState.evidenceState.SemanticallyRejectedEvidenceIds,
                    runState.evidenceState.ObservedEvidenceIdSet, runState.evidenceState.ObservedEvidenceIds, runState.evidenceState.PendingSemanticCandidateIds,
                    runState.evidenceWorkspace, runState.selectionOnlyNextTurn, runState.semanticReviewFeedback,
                    runState.traces, runState.traceId, runState.traceSequence, runState.turn, runState.lastInvalidSelectionProtocolKey,
                    runState.consecutiveIdenticalInvalidSelections, runState.maximumRunTurns,
                    runState.maximumRunTurnCeiling, runState.selectionProtocolRepairTurns, runState.messages, runState.intake,
                    runState.semanticPlan, runState.evidenceState.ExecutedRequests, runState.evidenceState.SemanticallyAuditedEvidenceIds,
                    runState.activeSemanticSelectionIds, runState.activeSemanticSelectionLayout,
                    runState.explicitSemanticSelectionActive, runState.consecutiveDocumentaryNoProgressTurns, ct)
                .ConfigureAwait(false);
        iteration.completion = semanticSelectionHandling.Completion;
        iteration.freshWriterOutputNeedsSemanticReview |= semanticSelectionHandling.FreshWriterOutputGenerated;
        runState.activeSemanticSelectionIds = semanticSelectionHandling.ActiveSemanticSelectionIds;
        runState.activeSemanticSelectionLayout = semanticSelectionHandling.ActiveSemanticSelectionLayout;
        runState.explicitSemanticSelectionActive = semanticSelectionHandling.ExplicitSemanticSelectionActive;
        runState.selectionOnlyNextTurn = semanticSelectionHandling.SelectionOnlyNextTurn;
        runState.consecutiveDocumentaryNoProgressTurns =
            semanticSelectionHandling.ConsecutiveDocumentaryNoProgressTurns;
        runState.semanticReviewFeedback = semanticSelectionHandling.SemanticReviewFeedback;
        runState.traceSequence = semanticSelectionHandling.TraceSequence;
        runState.lastInvalidSelectionProtocolKey = semanticSelectionHandling.LastInvalidSelectionProtocolKey;
        runState.consecutiveIdenticalInvalidSelections =
            semanticSelectionHandling.ConsecutiveIdenticalInvalidSelections;
        runState.maximumRunTurns = semanticSelectionHandling.MaximumRunTurns;
        runState.selectionProtocolRepairTurns = semanticSelectionHandling.SelectionProtocolRepairTurns;
        if (semanticSelectionHandling.AdaptiveFlatSelectionAccepted)
            runState.candidateCollectionOpen = false;
        if (semanticSelectionHandling.ContinueRun)
            return AgentIterationOutcome<SourceBackedPipelineResult>.Continue;

        if (iteration.completion.ToolCalls.Count > 0)
        {
            runState.lifecycle.BeginTools();
            var toolBatchExecution =
                await ExecuteDocumentaryToolBatchAsync(
                        iteration.completion, runState.messages, runState.intake, runState.evidenceState.CumulativeResults, runState.bundle,
                        runState.evidenceWorkspace, runState.evidenceState.ObservedEvidenceIdSet, runState.evidenceState.ObservedEvidenceIds,
                        runState.evidenceState.SemanticallyRejectedEvidenceIds, runState.traces, runState.traceId, runState.traceSequence,
                        runState.turn, runState.enableWorkspaceWriterHandoff, runState.requiredAtomicEvidenceCount,
                        runState.activeSemanticSelectionIds, runState.activeSemanticSelectionLayout,
                        runState.canonicalWorkspaceSelectionLayout, runState.directWriterRequested,
                        runState.selectionOnlyNextTurn, runState.decisionOnlyNextTurn,
                        runState.consecutiveDocumentaryNoProgressTurns,
                        runState.evidenceState.ResolvedNavigationEvidenceIds, runState.evidenceState.ExecutedRequests, runState.evidenceState.ExecutedCallKeys,
                        runState.totalToolCalls, runState.candidateCollectionOpen,
                        runState.semanticCandidateStrategyOutcome, iteration.hasCitableEvidence,
                        runState.transientProtocolFeedback, runState.evidenceState.SemanticallyAuditedEvidenceIds,
                        runState.evidenceState.PendingSemanticCandidateIds, ct)
                    .ConfigureAwait(false);
            runState.lifecycle.EndTools();
            runState.bundle = toolBatchExecution.Bundle;
            runState.activeSemanticSelectionIds = toolBatchExecution.ActiveSemanticSelectionIds;
            runState.activeSemanticSelectionLayout = toolBatchExecution.ActiveSemanticSelectionLayout;
            runState.directWriterRequested = toolBatchExecution.DirectWriterRequested;
            runState.selectionOnlyNextTurn = toolBatchExecution.SelectionOnlyNextTurn;
            runState.decisionOnlyNextTurn = toolBatchExecution.DecisionOnlyNextTurn;
            runState.consecutiveDocumentaryNoProgressTurns =
                toolBatchExecution.ConsecutiveDocumentaryNoProgressTurns;
            runState.transientProtocolFeedback = toolBatchExecution.TransientProtocolFeedback;
            runState.totalToolCalls = toolBatchExecution.TotalToolCalls;
            runState.traceSequence = toolBatchExecution.TraceSequence;
            var completedUsefulToolCall = toolBatchExecution.CompletedUsefulToolCall;
            var duplicateCallCount = toolBatchExecution.DuplicateCallCount;
            var zeroNewEvidenceCallCount = toolBatchExecution.ZeroNewEvidenceCallCount;
            var workspaceReadyForWriter = toolBatchExecution.WorkspaceReadyForWriter;
            var documentaryToolCallCount = toolBatchExecution.DocumentaryToolCallCount;
            var workspaceNoOpCallCount = toolBatchExecution.WorkspaceNoOpCallCount;
            var newlyObservedEvidenceIds = toolBatchExecution.NewlyObservedEvidenceIds;

            if (iteration.compactSingleFollowUpTurn
                && runState.semanticYieldResolutionPending
                && documentaryToolCallCount > 0)
            {
                runState.semanticYieldResolutionContinuationCount++;
                AddTrace(runState.traces, Trace(
                    runState.traceId,
                    ref runState.traceSequence,
                    SourceBackedPipelineStep.IterationController,
                    "source_backed_agent_v2.semantic_audit_zero_yield.continuation_recorded",
                    ("turn", runState.turn),
                    ("continuations", runState.semanticYieldResolutionContinuationCount),
                    ("maximum_continuations",
                        runState.semanticYieldResolutionContinuationLimit),
                    ("decision_source", "llm_orchestrator")));
            }

            runState.maximumRunTurns = ReserveProductiveRetrievalFollowUp(
                runState.traces, runState.traceId, ref runState.traceSequence, runState.turn,
                runState.candidateCollectionOpen, runState.evidenceState.PendingSemanticCandidateIds.Count,
                newlyObservedEvidenceIds, runState.maximumRunTurns,
                runState.maximumRunTurnCeiling);

            if (completedUsefulToolCall)
            {
                runState.transientProtocolFeedback = null;
                runState.consecutiveDocumentaryNoProgressTurns = 0;
                runState.selectionOnlyNextTurn = false;
                runState.decisionOnlyNextTurn = false;
                runState.yieldResolutionNextTurn = false;
                if (!workspaceReadyForWriter)
                {
                    runState.activeSemanticSelectionIds = null;
                    runState.activeSemanticSelectionLayout = null;
                }
                if (iteration.semanticNeedsMoreEvidence)
                    runState.semanticReviewDecision = null;
                CompactWorkingMessages(
                    runState.messages,
                    runState.intake,
                    runState.semanticPlan,
                    runState.bundle,
                    runState.evidenceState.ObservedEvidenceIds,
                    runState.evidenceState.ExecutedRequests,
                    runState.evidenceWorkspace,
                    runState.semanticReviewFeedback,
                    includeEvidenceDetails: !runState.candidateCollectionOpen);
                var fastEvidenceReviewCandidateIds =
                    BuildFastEvidenceReviewCandidateInput(
                        runState.bundle,
                        newlyObservedEvidenceIds,
                        runState.fastEvidenceReviewCarryForwardCandidateIds,
                        runState.evidenceState.SemanticallyRejectedEvidenceIds);
                var maximumFastEvidenceReviewCandidates =
                    ResolveFastEvidenceReviewCandidateLimit(
                        runState.intake,
                        runState.fastEvidenceReviewCarryForwardCandidateIds.Count);
                var carriedCandidateIdsBeforeReview =
                    runState.fastEvidenceReviewCarryForwardCandidateIds
                        .Where(id =>
                            fastEvidenceReviewCandidateIds.Contains(
                                id,
                                StringComparer.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                var fastEvidenceReviewSignature =
                    BuildFastEvidenceReviewSignature(
                        runState.bundle,
                        fastEvidenceReviewCandidateIds,
                        runState.evidenceState.SemanticallyRejectedEvidenceIds,
                        maximumFastEvidenceReviewCandidates);
                if (iteration.compactSingleFollowUpTurn
                    && fastEvidenceReviewSignature.Length == 0)
                {
                    runState.compactSingleFollowUpNextTurn = true;
                }
                if (iteration.compactSingleFollowUpTurn
                    && runState.semanticYieldResolutionPending
                    && runState.evidenceState.PendingSemanticCandidateIds.Count == 0)
                {
                    runState.compactSingleFollowUpNextTurn = true;
                    runState.yieldResolutionNextTurn = true;
                    AddTrace(runState.traces, Trace(
                        runState.traceId,
                        ref runState.traceSequence,
                        SourceBackedPipelineStep.IterationController,
                        "source_backed_agent_v2.semantic_audit_zero_yield.resolution_persisted",
                        ("turn", runState.turn),
                        ("new_evidence", newlyObservedEvidenceIds.Count),
                        ("pending_candidates", runState.evidenceState.PendingSemanticCandidateIds.Count),
                        ("decision_source", "mechanical_state_contract")));
                }
                if (fastEvidenceReviewSignature.Length > 0
                    && !string.Equals(
                        fastEvidenceReviewSignature,
                        runState.lastFastEvidenceReviewSignature,
                        StringComparison.Ordinal)
                    && CanAttemptFastEvidenceReview(
                        runState.intake,
                        runState.requireEvidenceSelection,
                        runState.requiredAtomicEvidenceCount,
                        runState.semanticLayoutDimensions)
                    && runState.evidenceState.ObservedEvidenceIds.Count > 0)
                {
                    runState.lastFastEvidenceReviewSignature =
                        fastEvidenceReviewSignature;
                    var fastReviewAttempt = await ReviewInitialEvidenceWithinCumulativeBudgetAsync(runState.intake, runState.semanticPlan, runState.bundle, fastEvidenceReviewCandidateIds,
                        runState.evidenceState.SemanticallyRejectedEvidenceIds, maximumFastEvidenceReviewCandidates, ct).ConfigureAwait(false);
                    if (fastReviewAttempt.TerminalBudgetOnly)
                    {
                        if (fastReviewAttempt.Review is not null) runState.bundle = PreserveTerminalFastReviewSemanticSelection(fastReviewAttempt.Review, runState.bundle, runState.evidenceState.SemanticallyAuditedEvidenceIds, runState.evidenceState.SemanticallyRejectedEvidenceIds);
                        var hasCurrentFinalizableEvidence = HasCurrentMechanicallyFinalizableEvidence(
                            runState.bundle, runState.evidenceState.ObservedEvidenceIds, runState.candidateAuditEnabled, runState.evidenceState.SemanticallyAuditedEvidenceIds,
                            runState.evidenceState.SemanticallyRejectedEvidenceIds, runState.requiredAtomicEvidenceCount, runState.atomicEvidenceMode);
                        TryActivateCumulativeBudgetFinalization(
                            hasCurrentFinalizableEvidence, runState.evidenceState.ExecutedRequests.Count, runState.turn,
                            runState.evidenceState.PendingSemanticCandidateIds, ref runState.candidateCollectionOpen, ref runState.selectionOnlyNextTurn,
                            ref runState.decisionOnlyNextTurn, ref runState.flatEvidenceGapTerminalDecisionNextTurn,
                            runState.traces, runState.traceId, ref runState.traceSequence, force: true);
                        runState.turn--;
                        return AgentIterationOutcome<SourceBackedPipelineResult>.Continue;
                    }
                    var fastReview = fastReviewAttempt.Review!;
                    runState.fastEvidenceReviewCarryForwardCandidateIds =
                        fastReview.ProtocolValid
                            ? Array.Empty<string>()
                            : (fastReview.PresentedCandidateIds
                               ?? Array.Empty<string>())
                            .Where(id =>
                                !runState.evidenceState.SemanticallyRejectedEvidenceIds.Contains(
                                    id))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Take(6)
                            .ToArray();
                    AddTrace(runState.traces, Trace(
                        runState.traceId,
                        ref runState.traceSequence,
                        SourceBackedPipelineStep.AnswerAdequacyJudge,
                        "source_backed_agent_v2.fast_evidence_review.completed",
                        ("turn", runState.turn),
                        ("decision", fastReview.Decision),
                        ("next_capability",
                            fastReview.NextCapability),
                        ("protocol_valid", fastReview.ProtocolValid),
                        ("anchor_verified",
                            fastReview.AnchorVerified),
                        ("selected_evidence_ids", fastReview.EvidenceIds),
                        ("lead_evidence_ids",
                            fastReview.LeadEvidenceIds),
                        ("anchor_excerpt",
                            TrimPromptValue(
                                fastReview.AnchorExcerpt,
                                180)),
                        ("missing", fastReview.Missing),
                        ("assessment", fastReview.Assessment),
                        ("prompt_characters", fastReview.PromptCharacters),
                        ("evidence_context_characters", fastReview.EvidenceContextCharacters),
                        ("candidate_count", fastReview.CandidateCount),
                        ("candidate_input_count",
                            fastEvidenceReviewCandidateIds.Count),
                        ("maximum_candidate_count",
                            maximumFastEvidenceReviewCandidates),
                        ("presented_candidate_ids", fastReview.PresentedCandidateIds ?? Array.Empty<string>()),
                        ("presented_evidence_ids",
                            fastReview.PresentedEvidenceIds
                            ?? Array.Empty<string>()),
                        ("carried_candidate_ids_before_review",
                            carriedCandidateIdsBeforeReview),
                        ("carried_candidate_ids_after_review",
                            runState.fastEvidenceReviewCarryForwardCandidateIds),
                        ("answer_redirected_to_writer", fastReview.AnswerMechanicallyRedirectedToWriter),
                        ("source_window_item_count", fastReview.SourceWindowItemCount),
                        ("evidence_pool_budget", fastReview.EvidencePoolBudget),
                        ("evidence_pool_eligible_item_count", fastReview.EvidencePoolEligibleItemCount),
                        ("evidence_pool_truncated_item_count", fastReview.EvidencePoolTruncatedItemCount),
                        ("answer_adequacy", fastReview.AnswerAdequacy),
                        ("requested_deliverable_complete", fastReview.RequestedDeliverableComplete),
                        ("missing_user_input_prevents_unique_result", fastReview.MissingUserInputPreventsUniqueResult),
                        ("visible_context_evidence_id", fastReview.VisibleContextEvidenceId),
                        ("ms", fastReview.ElapsedMilliseconds),
                        ("single_selection_scope_decision",
                            fastReview.SingleSelectionScopeDecision),
                        ("single_selection_scope_basis", fastReview.SingleSelectionScopeBasis),
                        ("single_selection_scope_reason", fastReview.SingleSelectionScopeReason),
                        ("single_selection_scope_protocol_valid",
                            fastReview.SingleSelectionScopeProtocolValid),
                        ("single_selection_scope_ms",
                            fastReview.SingleSelectionScopeElapsedMilliseconds),
                        ("single_selection_scope_normalized",
                            fastReview.SingleSelectionScopeMechanicallyNormalized),
                        ("protocol_error", fastReview.Completion.ProtocolError),
                        ("raw_content",
                            fastReview.ProtocolValid ? null
                                : TrimPromptValue(
                                    fastReview.Completion.ProtocolRawOutput ?? fastReview.Completion.Content,
                                    700)),
                        ("finish_reason",
                            fastReview.Completion.FinishReason),
                        ("prompt_tokens", fastReview.Completion.PromptTokens),
                        ("completion_tokens", fastReview.Completion.CompletionTokens),
                        ("server_cache_tokens",
                            fastReview.Completion.ServerCacheTokens),
                        ("server_prompt_evaluated_tokens",
                            fastReview.Completion.ServerPromptTokensEvaluated),
                        ("server_prompt_ms",
                            fastReview.Completion.ServerPromptMilliseconds),
                        ("server_predicted_tokens",
                            fastReview.Completion.ServerPredictedTokens),
                        ("server_predicted_ms",
                                fastReview.Completion.ServerPredictedMilliseconds)));
                    if (fastReview.SemanticResolutionWriterReviewAttempted) AddSemanticResolutionTrace(runState.traces, runState.traceId, ref runState.traceSequence, runState.turn, fastReview);
                    if ((fastReview.SemanticAnswerTransactionAttempted || fastReview.SemanticResolutionWriterReviewAttempted) && !fastReview.ProtocolValid)
                    {
                        iteration.completion = fastReview.Completion;
                        return AgentIterationOutcome<SourceBackedPipelineResult>.Stop;
                    }
                    if (!string.IsNullOrWhiteSpace(
                            fastReview.ClarificationQuestion))
                    {
                        var clarification =
                            new SourceBackedClarificationDecision(
                                fastReview.ClarificationQuestion,
                                Array.Empty<string>(),
                                fastReview.ClarificationQuestion,
                                "other");
                        AddTrace(runState.traces, Trace(
                            runState.traceId,
                            ref runState.traceSequence,
                            SourceBackedPipelineStep.EvidenceJudge,
                            "source_backed_agent_v2.clarification.requested",
                            ("turn", runState.turn),
                            ("observed_candidates",
                                fastReview.CandidateCount),
                            ("ambiguity_kind", "other"),
                            ("option_count", 0),
                            ("execution_impact",
                                fastReview.ClarificationQuestion),
                            ("decision_source",
                                "llm_evidence_judge")));
                        return AgentIterationOutcome<SourceBackedPipelineResult>.Complete(BuildResult(
                            runState.intake,
                            runState.evidenceState.ExecutedRequests,
                            runState.bundle,
                            runState.firstDraft,
                            runState.latestDraft,
                            runState.verification,
                            runState.repaired,
                            semanticAccepted: false,
                            runState.evidenceState.SemanticallyRejectedEvidenceIds,
                            new[]
                            {
                                fastReview.ClarificationQuestion
                            },
                            runState.traces.ToArray(),
                            clarification));
                    }
                    if (fastReview.Ready)
                    {
                        runState.consecutiveFastEvidenceResearchZeroYieldReviews = 0;
                        runState.explicitSemanticSelectionActive = true;
                        runState.compactFollowUpLeadEvidenceIds = Array.Empty<string>();
                        runState.activeSemanticSelectionIds = fastReview.EvidenceIds;
                        runState.activeSemanticSelectionLayout = null;
                        if (fastReview.SemanticResolutionWriterReviewAttempted)
                        {
                            runState.semanticResolutionWriterReviewActive = true;
                            runState.inlineFastAnswerRequiresIndependentSemanticReview = true;
                            runState.inlineFastAnswerDirectRevisionSignatures.Clear();
                        }
                        if (!fastReview.SemanticResolutionWriterReviewAttempted && !string.IsNullOrWhiteSpace(fastReview.Answer))
                        {
                            runState.inlineFastAnswerRequiresIndependentSemanticReview = !fastReview.AnswerSemanticallyFinal;
                            runState.inlineFastAnswerDirectRevisionSignatures.Clear();
                        }
                        iteration.completion = await CompleteFastEvidenceWriterAsync(
                                fastReview, runState.messages, runState.intake, runState.bundle, runState.activeSemanticSelectionIds, runState.atomicEvidenceMode,
                                runState.traces, runState.traceId, ref runState.traceSequence, runState.turn, ct)
                            .ConfigureAwait(false);
                        iteration.freshWriterOutputNeedsSemanticReview |= string.IsNullOrWhiteSpace(fastReview.Answer);
                        AddWriterTrace(runState.traces, runState.traceId, ref runState.traceSequence,
                            runState.turn, iteration.completion, directRevision: false);
                        iteration.completedByFastEvidenceWriter = true;
                    }
                    else if (!string.IsNullOrWhiteSpace(fastReview.Missing))
                    {
                        if (fastReview.ProtocolValid)
                        {
                            if (TryBlockSemanticResolutionContinuation(fastReview, runState.totalToolCalls, runState.traces, runState.traceId, ref runState.traceSequence, runState.turn)) return AgentIterationOutcome<SourceBackedPipelineResult>.Stop;
                            var fastReviewRequestsResearch = string.Equals(
                                fastReview.NextCapability,
                                "research",
                                StringComparison.Ordinal);
                            if (fastReviewRequestsResearch)
                            {
                                runState.consecutiveFastEvidenceResearchZeroYieldReviews++;
                                AddTrace(runState.traces, Trace(
                                    runState.traceId,
                                    ref runState.traceSequence,
                                    SourceBackedPipelineStep.IterationController,
                                    "source_backed_agent_v2.fast_evidence_review.zero_yield.observed",
                                    ("turn", runState.turn),
                                    ("consecutive_research_reviews",
                                        runState.consecutiveFastEvidenceResearchZeroYieldReviews),
                                    ("new_evidence",
                                        newlyObservedEvidenceIds.Count),
                                    ("decision_source",
                                        "llm_evidence_judge")));
                            }
                            else
                            {
                                runState.consecutiveFastEvidenceResearchZeroYieldReviews = 0;
                            }
                            SourceBackedAgentToolCall?
                                leadContextAction = null;
                            if (string.Equals(
                                    fastReview.NextCapability,
                                    "documents_context",
                                    StringComparison.Ordinal)
                                && TryBuildLeadContextAction(
                                    runState.bundle,
                                    fastReview.LeadEvidenceIds,
                                    out var resolvedLeadContextAction))
                            {
                                leadContextAction =
                                    resolvedLeadContextAction;
                            }
                            if (leadContextAction is not null)
                            {
                                runState.routerInitialActions =
                                    new[] { leadContextAction };
                                runState.plannedFirstAction = null;
                                runState.initialActionDecisionSource =
                                    "llm_evidence_judge";
                                runState.compactFollowUpLeadEvidenceIds =
                                    Array.Empty<string>();
                                runState.compactSingleFollowUpNextTurn = false;
                                AddTrace(runState.traces, Trace(
                                    runState.traceId,
                                    ref runState.traceSequence,
                                    SourceBackedPipelineStep.IterationController,
                                    "source_backed_agent_v2.fast_evidence_review.next_action_scheduled",
                                    ("turn", runState.turn),
                                    ("decision_source",
                                        "llm_evidence_judge"),
                                    ("capability",
                                        fastReview.NextCapability),
                                    ("lead_evidence_ids",
                                        fastReview.LeadEvidenceIds),
                                    ("tool",
                                        leadContextAction!.Name),
                                    ("arguments",
                                        TrimPromptValue(
                                            leadContextAction.Arguments
                                                .GetRawText(),
                                            800))));
                            }
                            else
                            {
                                runState.compactFollowUpLeadEvidenceIds =
                                    fastReview.LeadEvidenceIds;
                                runState.compactSingleFollowUpNextTurn = true;
                            }
                            if (!string.Equals(
                                    fastReview.NextCapability,
                                    "documents_context",
                                    StringComparison.Ordinal))
                            {
                                foreach (var rejectedEvidenceId
                                         in fastReview.PresentedEvidenceIds
                                            ?? fastEvidenceReviewCandidateIds)
                                {
                                    runState.evidenceState.SemanticallyRejectedEvidenceIds.Add(
                                        rejectedEvidenceId);
                                    runState.evidenceState.PendingSemanticCandidateIds.Remove(
                                        rejectedEvidenceId);
                                }
                            }
                            if (fastReviewRequestsResearch
                                && runState.consecutiveFastEvidenceResearchZeroYieldReviews >= 2)
                            {
                                runState.semanticYieldResolutionPending = true;
                                runState.yieldResolutionNextTurn = true;
                                runState.compactSingleFollowUpNextTurn = true;
                                runState.semanticYieldResolutionContinuationCount = 0;
                                runState.semanticYieldResolutionContinuationLimit = 0;
                                runState.compactFollowUpLeadEvidenceIds =
                                    Array.Empty<string>();
                                AddTrace(runState.traces, Trace(
                                    runState.traceId,
                                    ref runState.traceSequence,
                                    SourceBackedPipelineStep.IterationController,
                                    "source_backed_agent_v2.fast_evidence_review.zero_yield.resolution_requested",
                                    ("turn", runState.turn),
                                    ("consecutive_research_reviews",
                                        runState.consecutiveFastEvidenceResearchZeroYieldReviews),
                                    ("executed_requests",
                                        runState.evidenceState.ExecutedRequests.Count),
                                    ("rejected_evidence",
                                        runState.evidenceState.SemanticallyRejectedEvidenceIds.Count),
                                    ("rejected_evidence_ids",
                                        runState.evidenceState.SemanticallyRejectedEvidenceIds),
                                    ("decision_source",
                                        "llm_evidence_judge+mechanical_budget_contract")));
                            }
                        }
                        else if (fastReview.LeadEvidenceIds.Count > 0)
                        {
                            runState.compactFollowUpLeadEvidenceIds =
                                fastReview.LeadEvidenceIds;
                            runState.compactSingleFollowUpNextTurn = true;
                        }
                        runState.semanticReviewFeedback =
                            "REVUE DE SUFFISANCE DU PREMIER LOT: "
                            + TrimPromptValue(
                                fastReview.Missing,
                                240);
                        runState.messages.Add(SourceBackedAgentMessage.User(
                            runState.semanticReviewFeedback));
                    }
                }
            }
            else if (documentaryToolCallCount
                         + workspaceNoOpCallCount
                     > 0
                     && !completedUsefulToolCall
                     && duplicateCallCount + zeroNewEvidenceCallCount
                     == documentaryToolCallCount
                        + workspaceNoOpCallCount)
            {
                runState.consecutiveDocumentaryNoProgressTurns++;
                AddTrace(runState.traces, Trace(
                    runState.traceId,
                    ref runState.traceSequence,
                    SourceBackedPipelineStep.IterationController,
                    "source_backed_agent_v2.documentary_progress.none",
                    ("turn", runState.turn),
                    ("attempted_calls",
                        documentaryToolCallCount
                        + workspaceNoOpCallCount),
                    ("duplicate_calls", duplicateCallCount),
                    ("zero_new_evidence_calls", zeroNewEvidenceCallCount),
                    ("consecutive_no_progress_turns",
                        runState.consecutiveDocumentaryNoProgressTurns),
                    ("observed", iteration.observedCitableSourceCount),
                    ("required",
                        runState.requiredAtomicEvidenceCount.GetValueOrDefault())));
                if (iteration.compactSingleFollowUpTurn
                    && runState.semanticYieldResolutionPending)
                {
                    runState.compactSingleFollowUpNextTurn = true;
                    runState.yieldResolutionNextTurn = true;
                    AddTrace(runState.traces, Trace(
                        runState.traceId,
                        ref runState.traceSequence,
                        SourceBackedPipelineStep.IterationController,
                        "source_backed_agent_v2.semantic_audit_zero_yield.resolution_persisted",
                        ("turn", runState.turn),
                        ("new_evidence", 0),
                        ("pending_candidates", runState.evidenceState.PendingSemanticCandidateIds.Count),
                        ("decision_source", "mechanical_state_contract")));
                }
                ApplyDocumentaryNoProgressTransition(
                    runState.intake, runState.semanticPlan, runState.bundle, runState.evidenceState.ObservedEvidenceIds,
                    runState.evidenceState.ExecutedRequests, runState.evidenceWorkspace, runState.messages,
                    runState.traces, runState.traceId, ref runState.traceSequence, runState.turn,
                    runState.requiredAtomicEvidenceCount, runState.requireEvidenceSelection,
                    iteration.hasCitableEvidence, iteration.observedCitableSourceCount,
                    runState.consecutiveDocumentaryNoProgressTurns,
                    duplicateCallCount, zeroNewEvidenceCallCount,
                    runState.adequacyCheckpoint.GapActive,
                    runState.adequacyCheckpoint.IncumbentIds,
                    ref runState.candidateCollectionOpen,
                    ref runState.selectionOnlyNextTurn,
                    ref runState.decisionOnlyNextTurn,
                    ref runState.compactSingleFollowUpNextTurn,
                    ref runState.yieldResolutionNextTurn,
                    ref runState.compactFollowUpLeadEvidenceIds,
                    ref runState.semanticReviewFeedback,
                    ref runState.transientProtocolFeedback,
                    ref runState.flatEvidenceGapTerminalDecisionNextTurn);
            }
            if (!iteration.completedByFastEvidenceWriter)
                return AgentIterationOutcome<SourceBackedPipelineResult>.Continue;
        }

        return AgentIterationOutcome<SourceBackedPipelineResult>.Advance;
    }
}
