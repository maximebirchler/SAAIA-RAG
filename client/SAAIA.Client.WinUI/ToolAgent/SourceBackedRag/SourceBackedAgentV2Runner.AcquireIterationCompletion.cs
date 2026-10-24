using SAAIA.Client.Core.Execution;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private async Task<AgentIterationOutcome<SourceBackedPipelineResult>> AcquireIterationCompletionAsync(
        RunExecutionState runState, IterationExecutionState iteration, CancellationToken ct)
    {
        CandidateAuditExecutionResult? candidateAuditExecution = null;
        ResearchTransitionExecution? researchTransitionExecution = null;
        iteration.structuredCandidateWriterTurn = false;
        iteration.structuredCandidateWriterProtocolError = null;
        iteration.compactSingleFollowUpTurn = false;
        iteration.yieldResolutionTurn = runState.yieldResolutionNextTurn
                                  || runState.semanticYieldResolutionPending;
        iteration.completedByFastEvidenceWriter = false;
        iteration.reviewDirectedRevisionCompletedThisTurn = false;
        iteration.reviewDirectedRevisionExactClaimCountSatisfied = true;
        iteration.selectableEvidenceIds = Array.Empty<string>();
        if (iteration.flatEvidenceAdequacyCompletion is not null)
        {
            iteration.completion = iteration.flatEvidenceAdequacyCompletion;
        }
        else if (runState.namedDocumentTerminalCompletion is not null)
        {
            iteration.completion = runState.namedDocumentTerminalCompletion;
            runState.namedDocumentTerminalCompletion = null;
        }
        else if (TryApplyInitialAction(
                ref runState.routerInitialActions,
                ref runState.plannedFirstAction,
                runState.initialActionDecisionSource,
                runState.traces,
                runState.traceId,
                ref runState.traceSequence,
                runState.turn,
                out iteration.completion))
        {
        }
        else if (_options.SeparateActionAndWriter
            && runState.directWriterRequested)
        {
            iteration.reviewDirectedRevisionCompletedThisTurn =
                runState.commitNextVerifiedReviewDirectedRevision;
            runState.commitNextVerifiedReviewDirectedRevision = false;
            var requiredClaimCountForThisRevision =
                runState.reviewDirectedRevisionRequiredClaimCount;
            runState.reviewDirectedRevisionRequiredClaimCount = null;
            var writerHandoff =
                await CompleteDirectWriterHandoffWithNamedValueRepairAsync(
                        runState.messages,
                        runState.verification,
                        runState.directWriterRevisionInstruction,
                        runState.intake,
                        runState.bundle,
                        runState.activeSemanticSelectionIds,
                        runState.activeSemanticSelectionLayout,
                        runState.atomicEvidenceMode,
                        runState.boundedNamedDocumentExtraction
                            ? runState.requiredAtomicEvidenceCount
                            : null,
                        iteration.reviewDirectedRevisionCompletedThisTurn
                        && runState.boundedNamedDocumentExtraction
                        && requiredClaimCountForThisRevision is > 0,
                        ct)
                    .ConfigureAwait(false);
            iteration.completion = writerHandoff.Completion;
            TraceNamedValueProtocolRepair(
                writerHandoff, runState.traces, runState.traceId, ref runState.traceSequence, runState.turn);
            iteration.reviewDirectedRevisionExactClaimCountSatisfied =
                requiredClaimCountForThisRevision is null
                || writerHandoff.StructuredClaimCount
                    == requiredClaimCountForThisRevision;
            iteration.freshWriterOutputNeedsSemanticReview = true;
            iteration.structuredCandidateWriterProtocolError = writerHandoff.ProtocolError;
            runState.directWriterRequested = false;
            runState.directWriterRevisionInstruction = null;
            if (writerHandoff.Layout is { } renderedLayout)
            {
                TraceStructuredRendererCompletion(renderedLayout, runState.activeSemanticSelectionIds, iteration.completion, runState.traces, runState.traceId, ref runState.traceSequence, runState.turn);
            }
            else
            {
                AddWriterTrace(
                    runState.traces,
                    runState.traceId,
                    ref runState.traceSequence,
                    runState.turn,
                    iteration.completion,
                    directRevision: true);
            }
        }
        else
        {
            iteration.selectableEvidenceIds = iteration.semanticSelectionDecisionTurn
                ? SelectEligibleWorkspaceEvidenceIds(
                    runState.bundle,
                    iteration.selectionEligibleObservedEvidenceIds,
                    runState.evidenceState.SemanticallyRejectedEvidenceIds,
                    Math.Min(
                        80,
                        Math.Max(
                            12,
                            Math.Max(
                                iteration.requiredEvidenceCount + 8,
                                runState.candidatePoolTargetCount))))
                : Array.Empty<string>();
            NavigationAnchorEligibilityAuditExecution navigationAnchorAudit;
            try
            {
                navigationAnchorAudit = runState.candidateCollectionOpen
                                        && runState.candidateAuditEnabled
                                        && !iteration.dedicatedCandidateAuditTurn
                    ? await CompleteNavigationAnchorEligibilityAuditAsync(
                        runState.intake, runState.semanticPlan, runState.semanticCandidateObjectType, runState.semanticCandidateEligibilityRule,
                        runState.bundle, runState.evidenceState.ResolvedNavigationEvidenceIds,
                        runState.evidenceState.AuditedNavigationEvidenceIds, ct).ConfigureAwait(false)
                    : NoNavigationAnchorEligibilityAudit(runState.bundle);
            }
            catch (SourceBackedLlmBudgetExceededException ex) when (
                string.Equals(
                    ex.Reason,
                    "terminal_budget_only",
                    StringComparison.Ordinal))
            {
                TryActivateCumulativeBudgetFinalization(
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
                    ref runState.traceSequence,
                    force: true);
                runState.turn--;
                return AgentIterationOutcome<SourceBackedPipelineResult>.Continue;
            }
            runState.bundle = navigationAnchorAudit.Bundle;
            TraceNavigationAnchorEligibilityAudit(runState.traces, runState.traceId, ref runState.traceSequence, runState.turn, navigationAnchorAudit);
            var turnTools = BuildTurnEvidenceTools(
                runState.tools,
                runState.bundle,
                iteration.selectionEligibleObservedEvidenceIds,
                runState.evidenceState.SemanticallyRejectedEvidenceIds,
                runState.enableWorkspaceWriterHandoff,
                runState.requiredAtomicEvidenceCount,
                runState.canonicalWorkspaceSelectionLayout is not null);
            var menu = AgentToolMenuPlanner.Select(new AgentToolMenuInput(
                    iteration.dedicatedCandidateAuditTurn, iteration.semanticYieldTerminalDecisionTurn, runState.compactSingleFollowUpNextTurn,
                    runState.candidateCollectionOpen, iteration.semanticSelectionDecisionTurn, iteration.workspaceWriterDecisionTurn,
                    runState.decisionOnlyNextTurn, iteration.finalizationTurn, iteration.allowAdaptiveFlatSelection, iteration.hasCitableEvidence,
                    iteration.requiredEvidenceCount, iteration.observedCitableSourceCount, SemanticSelectionToolName, EvidenceWorkspaceToolName),
                turnTools, static tool => tool.Name,
                mode => mode switch
                {
                    AgentToolMenu.SemanticYield => BuildSemanticYieldTerminalDecisionTools(),
                    AgentToolMenu.CompactFollowUp => BuildCompactSingleFollowUpTools(turnTools, iteration.yieldResolutionTurn),
                    AgentToolMenu.CandidateCollection => BuildCandidateCollectionTools(turnTools,
                        runState.semanticCandidateStrategyOutcome.CandidateScopePaths, runState.bundle, runState.evidenceState.ResolvedNavigationEvidenceIds,
                        runState.evidenceState.ExecutedRequests, _options.MaximumWorkingEvidenceItems, runState.intake),
                    AgentToolMenu.WorkspaceSelection => new[] { BuildEvidenceWorkspaceTool(true,
                        runState.requiredAtomicEvidenceCount, runState.canonicalWorkspaceSelectionLayout is not null, iteration.selectableEvidenceIds) },
                    AgentToolMenu.EvidenceSelection => new[] { BuildSemanticSelectionTool(iteration.requiredEvidenceCount,
                        runState.semanticLayoutDimensions, iteration.selectableEvidenceIds, runState.semanticColumnLabels, runState.semanticRowHeader, runState.semanticRowLabels) },
                    _ => Array.Empty<SourceBackedAgentToolDefinition>()
                },
                () => BuildSemanticSelectionTool(iteration.requiredEvidenceCount, dimensions: null,
                    allowedEvidenceIds: iteration.selectionEligibleObservedEvidenceIds, canonicalColumnLabels: null,
                    minimumCount: 1, provisionalFlatTarget: true),
                BuildSourceBackedClarificationTool);
            var actionTools = menu.Tools.ToArray();
            ClientDiagnostics.Journal.Emit("agent.tool_menu.selected", "success", menu.Menu.ToString(),
                new Dictionary<string, long?> { ["tools"] = actionTools.Length, ["iteration"] = runState.turn });
            var compactCandidateCollectionForContextRecovery = false;
            var actionFeedback = MergeAgentFeedback(runState.semanticReviewFeedback, runState.transientProtocolFeedback);
            IReadOnlyList<SourceBackedAgentMessage> BuildActionMessages()
                => iteration.dedicatedCandidateAuditTurn
                    ? Array.Empty<SourceBackedAgentMessage>()
                    : iteration.semanticYieldTerminalDecisionTurn
                    ? BuildSemanticYieldTerminalDecisionMessages(
                        runState.intake,
                        runState.semanticPlan,
                        runState.evidenceState.ExecutedRequests,
                        actionFeedback,
                        runState.semanticYieldResolutionContinuationCount)
                    : runState.candidateCollectionOpen
                      && UsesResearchTransitionTools(actionTools)
                    ? BuildResearchTransitionDecisionMessages(
                        runState.intake,
                        runState.semanticPlan,
                        runState.semanticCandidateObjectType,
                        runState.semanticCandidateEligibilityRule,
                        runState.bundle,
                        runState.evidenceState.ResolvedNavigationEvidenceIds,
                        runState.evidenceState.ExecutedRequests,
                        runState.semanticCandidateStrategyOutcome.CandidateScopePaths,
                        iteration.observedCitableSourceCount,
                        runState.candidatePoolTargetCount,
                        actionFeedback,
                        _options.MaximumWorkingEvidenceItems,
                        compactCandidateCollectionForContextRecovery)
                    : runState.compactSingleFollowUpNextTurn
                    ? BuildCompactSingleFollowUpMessages(
                        runState.intake,
                        runState.semanticPlan,
                        runState.bundle,
                        runState.evidenceState.ObservedEvidenceIds,
                        runState.evidenceState.SemanticallyRejectedEvidenceIds,
                        runState.compactFollowUpLeadEvidenceIds,
                        runState.evidenceState.ExecutedRequests,
                        actionFeedback)
                    : runState.candidateCollectionOpen
                      && compactCandidateCollectionForContextRecovery
                    ? BuildCompactCandidateCollectionRetrievalMessages(
                        runState.intake, runState.semanticPlan,
                        runState.semanticCandidateStrategyOutcome, runState.bundle,
                        runState.evidenceState.ResolvedNavigationEvidenceIds, runState.evidenceState.ExecutedRequests,
                        iteration.selectionEligibleObservedEvidenceIds,
                        iteration.observedCitableSourceCount, runState.candidatePoolTargetCount,
                        actionFeedback, _options.MaximumWorkingEvidenceItems,
                        iteration.allowAdaptiveFlatSelection)
                    : runState.candidateCollectionOpen
                    ? BuildCandidateCollectionRetrievalMessages(
                        runState.messages, runState.semanticCandidateStrategyOutcome,
                        runState.bundle, iteration.selectionEligibleObservedEvidenceIds,
                        iteration.observedCitableSourceCount, runState.candidatePoolTargetCount,
                        actionFeedback,
                        iteration.allowAdaptiveFlatSelection)
                    : iteration.semanticSelectionDecisionTurn
                    ? BuildSemanticSelectionDecisionMessages(
                        runState.intake,
                        runState.semanticPlan,
                        runState.bundle,
                        iteration.selectableEvidenceIds,
                         runState.semanticReviewFeedback,
                         iteration.requiredEvidenceCount,
                         runState.semanticLayoutDimensions,
                         runState.semanticRowHeader,
                         runState.semanticRowLabels,
                         runState.semanticColumnLabels,
                         canonicalLayoutAlreadyDefined:
                            runState.semanticLayoutDimensions is not null
                            && !string.IsNullOrWhiteSpace(runState.semanticRowHeader)
                            && runState.semanticColumnLabels.Length > 0
                            && runState.semanticRowLabels.Any(),
                        useWorkspaceWriterHandoff:
                            iteration.workspaceWriterDecisionTurn)
                    : _options.SeparateActionAndWriter
                      && runState.compactSingleFollowUpNextTurn
                        ? BuildCompactSingleFollowUpMessages(
                            runState.intake,
                            runState.semanticPlan,
                            runState.bundle,
                            runState.evidenceState.ObservedEvidenceIds,
                            runState.evidenceState.SemanticallyRejectedEvidenceIds,
                            runState.compactFollowUpLeadEvidenceIds,
                            runState.evidenceState.ExecutedRequests,
                            runState.semanticReviewFeedback)
                    : _options.SeparateActionAndWriter
                        ? runState.messages
                            .Concat(new[]
                            {
                                SourceBackedAgentMessage.User(BuildActionDecisionMessage(
                                    runState.requiredAtomicEvidenceCount,
                                    runState.semanticLayoutDimensions,
                                    runState.requireEvidenceSelection,
                                    runState.enableWorkspaceWriterHandoff))
                            })
                            .ToArray()
                        : runState.messages.ToArray();
            var actionMaxTokens = _options.SeparateActionAndWriter
                ? iteration.semanticSelectionDecisionTurn
                    ? Math.Clamp(
                        96 + iteration.requiredEvidenceCount * 12,
                        128,
                        512)
                    : iteration.semanticYieldTerminalDecisionTurn
                        ? Math.Min(_options.MaximumActionTokens, 128)
                    : runState.compactSingleFollowUpNextTurn
                        ? Math.Min(_options.MaximumActionTokens, 192)
                    : runState.candidateCollectionOpen
                        ? Math.Min(_options.MaximumActionTokens, 256)
                    : iteration.selectionDecisionTurn
                        ? Math.Min(_options.MaximumActionTokens, 192)
                    : runState.compactSingleFollowUpNextTurn
                        ? Math.Min(_options.MaximumActionTokens, 192)
                    : _options.MaximumActionTokens
                : _options.MaximumOutputTokens;
            var requireActionToolCall = runState.candidateCollectionOpen
                                        || iteration.semanticSelectionDecisionTurn
                                         || runState.compactSingleFollowUpNextTurn
                                         || iteration.semanticYieldTerminalDecisionTurn;
            if (!requireActionToolCall
                && !iteration.dedicatedCandidateAuditTurn
                && !runState.decisionOnlyNextTurn
                && !iteration.finalizationTurn
                && runState.evidenceState.ObservedEvidenceIds.Count > 0)
            {
                actionTools = actionTools
                    .Append(BuildSourceBackedClarificationTool())
                    .ToArray();
            }
            var useStructuredCandidateWriter = ShouldUseStructuredCandidateWriter(
                iteration.semanticSelectionDecisionTurn, runState.semanticLayoutDimensions,
                runState.semanticRowLabels, runState.semanticColumnLabels, iteration.requiredEvidenceCount);
            iteration.compactSingleFollowUpTurn =
                runState.compactSingleFollowUpNextTurn
                || iteration.semanticYieldTerminalDecisionTurn;
            var contextRecoveryAttempted = false;
            var effectiveActionMaxTokens = actionMaxTokens;
            int? exactInputTokens = null;
            var cumulativeBudgetRestartTurn = false;
            while (true)
            {
                var actionMessages = BuildActionMessages();
                exactInputTokens = useStructuredCandidateWriter
                    ? null
                    : await CountInputTokensAsync(
                            actionMessages,
                            actionTools,
                            requireActionToolCall,
                            ct)
                        .ConfigureAwait(false);
                if (!contextRecoveryAttempted
                    && ExceedsContextBudget(
                        exactInputTokens,
                        effectiveActionMaxTokens))
                {
                    contextRecoveryAttempted = true;
                    compactCandidateCollectionForContextRecovery |= runState.candidateCollectionOpen;
                    effectiveActionMaxTokens = Math.Min(
                        effectiveActionMaxTokens,
                        192);
                    if (!iteration.semanticSelectionDecisionTurn)
                    {
                        CompactWorkingMessages(
                            runState.messages,
                            runState.intake,
                            runState.semanticPlan,
                            runState.bundle,
                            runState.evidenceState.ObservedEvidenceIds,
                            runState.evidenceState.ExecutedRequests,
                            runState.evidenceWorkspace,
                            MergeAgentFeedback(
                                runState.semanticReviewFeedback,
                                runState.transientProtocolFeedback),
                            emergencyContextRecovery: true,
                            includeEvidenceDetails: !runState.candidateCollectionOpen);
                        runState.messages.Add(SourceBackedAgentMessage.User(
                            BuildContextRecoveryActionMessage()));
                    }
                    AddTrace(runState.traces, Trace(
                        runState.traceId,
                        ref runState.traceSequence,
                        SourceBackedPipelineStep.IterationController,
                        "source_backed_agent_v2.context.recovery_requested",
                        ("turn", runState.turn),
                        ("reason", "preflight_exact_token_budget"),
                        ("selection_decision",
                            iteration.semanticSelectionDecisionTurn),
                        ("input_tokens", exactInputTokens),
                        ("reserved_output_tokens", effectiveActionMaxTokens),
                        ("safety_reserve_tokens", ContextSafetyReserveTokens),
                        ("maximum_context_tokens", _options.MaximumContextTokens)));
                    continue;
                }
                if (contextRecoveryAttempted
                    && exactInputTokens is { } measuredInputTokens
                    && ExceedsContextBudget(
                        exactInputTokens,
                        effectiveActionMaxTokens))
                {
                    var availableOutputTokens =
                        _options.MaximumContextTokens
                        - measuredInputTokens
                        - ContextSafetyReserveTokens;
                    if (availableOutputTokens >= 64)
                    {
                        effectiveActionMaxTokens = Math.Min(
                            effectiveActionMaxTokens,
                            availableOutputTokens);
                        AddTrace(runState.traces, Trace(
                            runState.traceId,
                            ref runState.traceSequence,
                            SourceBackedPipelineStep.IterationController,
                            "source_backed_agent_v2.context.output_budget_adjusted",
                            ("turn", runState.turn),
                            ("input_tokens", measuredInputTokens),
                            ("reserved_output_tokens", effectiveActionMaxTokens),
                            ("safety_reserve_tokens", ContextSafetyReserveTokens),
                            ("maximum_context_tokens", _options.MaximumContextTokens)));
                    }
                    else
                    {
                        AddTrace(runState.traces, Trace(
                            runState.traceId,
                            ref runState.traceSequence,
                            SourceBackedPipelineStep.IterationController,
                            "source_backed_agent_v2.context.preflight_blocked",
                            ("turn", runState.turn),
                            ("input_tokens", measuredInputTokens),
                            ("minimum_output_tokens", 64),
                            ("safety_reserve_tokens", ContextSafetyReserveTokens),
                            ("maximum_context_tokens", _options.MaximumContextTokens),
                            ("tool_names", actionTools.Select(static tool => tool.Name))));
                        throw new InvalidOperationException(
                            "Source-backed action request remains outside the measured "
                            + $"context budget after emergency compaction: input={measuredInputTokens}, "
                            + $"context={_options.MaximumContextTokens}.");
                    }
                }
                try
                {
                    if (useStructuredCandidateWriter)
                    {
                        iteration.freshWriterOutputNeedsSemanticReview = true;
                        iteration.structuredCandidateWriterTurn = true;
                        StructuredAssignmentRevisionExecution? revision = null;
                        StructuredCandidateWriterExecution? writerExecution = null;
                        if (runState.pendingStructuredAssignmentReview is not null
                            && runState.pendingStructuredAssignmentDraft is not null)
                        {
                            revision =
                                await CompleteStructuredAssignmentRevisionAsync(
                                    runState.intake,
                                    runState.bundle,
                                    runState.pendingStructuredAssignmentReview,
                                    runState.pendingStructuredAssignmentDraft,
                                    iteration.selectableEvidenceIds,
                                    runState.structuredRejectedEvidenceByCell,
                                    ct)
                                .ConfigureAwait(false);
                            iteration.completion = revision.Completion;
                            AddTrace(runState.traces, Trace(
                                runState.traceId,
                                ref runState.traceSequence,
                                SourceBackedPipelineStep.Writer,
                                "source_backed_agent_v2.structured_assignment_revision.patch_completed",
                                ("turn", runState.turn),
                                ("protocol_valid", revision.ProtocolValid),
                                ("revised_cells", revision.RevisedCellCount),
                                ("available_candidates", revision.AvailableCandidateCount),
                                ("raw_output", revision.RawOutput),
                                ("failure_reason", revision.FailureReason),
                                ("attempts", revision.Attempts),
                                ("context_recovery_used", revision.ContextRecoveryUsed),
                                ("input_tokens_exact", revision.ExactInputTokens),
                                ("decision_source", "llm_semantic_assignment_patch")));
                        }
                        else
                        {
                            writerExecution =
                                await CompleteStructuredCandidateWriterAsync(
                                        runState.intake,
                                        runState.bundle,
                                        iteration.selectableEvidenceIds,
                                        runState.semanticRowHeader,
                                        runState.semanticRowLabels,
                                        runState.semanticColumnLabels,
                                        runState.semanticColumnRoles,
                                        MergeAgentFeedback(
                                            runState.semanticReviewFeedback,
                                            runState.transientProtocolFeedback),
                                        ct)
                                    .ConfigureAwait(false);
                            iteration.completion = writerExecution.Completion;
                            if (TryApplyStructuredCandidateWriterEvidenceRequest(
                                    writerExecution, iteration.observedCitableSourceCount, runState.turn,
                                    ref runState.candidatePoolTargetCount,
                                    ref runState.candidateCollectionOpen, ref runState.selectionOnlyNextTurn,
                                    ref runState.directWriterRequested, ref runState.semanticReviewFeedback,
                                    ref runState.maximumRunTurns, runState.maximumRunTurnCeiling,
                                    runState.traces, runState.traceId, ref runState.traceSequence))
                                continue;
                        }
                        IReadOnlyList<string> structuredWriterSelectionIds =
                            Array.Empty<string>();
                        var structuredWriterFailureReason =
                            revision?.FailureReason
                            ?? writerExecution?.FailureReason
                            ?? string.Empty;
                        var structuredWriterProtocolValid = revision?.ProtocolValid
                                                            ?? writerExecution?.ProtocolValid
                                                            ?? true;
                        if (structuredWriterProtocolValid)
                        {
                            structuredWriterProtocolValid =
                                TryValidateStructuredCandidateWriterCompletion(
                                    iteration.completion,
                                    runState.bundle,
                                    iteration.selectableEvidenceIds,
                                    iteration.requiredEvidenceCount,
                                    out structuredWriterSelectionIds,
                                    out structuredWriterFailureReason);
                        }
                        iteration.structuredCandidateWriterProtocolError =
                            structuredWriterProtocolValid
                                ? null
                                : revision?.FailureReason
                                  ?? structuredWriterFailureReason;
                        if (structuredWriterProtocolValid)
                        {
                            runState.activeSemanticSelectionIds =
                                structuredWriterSelectionIds;
                            runState.activeSemanticSelectionLayout =
                                runState.canonicalWorkspaceSelectionLayout;
                            runState.explicitSemanticSelectionActive = true;
                            iteration.completion = iteration.completion with
                            {
                                Content = NormalizeStructuredCandidateWriterLabels(
                                    iteration.completion.Content,
                                    runState.bundle,
                                    structuredWriterSelectionIds.ToHashSet(
                                        StringComparer.OrdinalIgnoreCase),
                                    out var normalizedStructuredWriterCells)
                            };
                            AddTrace(runState.traces, Trace(
                                runState.traceId,
                                ref runState.traceSequence,
                                SourceBackedPipelineStep.SourceVerifier,
                                "source_backed_agent_v2.structured_candidate_writer.labels_normalized",
                                ("turn", runState.turn),
                                ("normalized_cells", normalizedStructuredWriterCells),
                                ("decision_source", "mechanical_source_contract")));
                        }
                        iteration.completedByFastEvidenceWriter = true;
                        TraceStructuredCandidateWriterCompletion(
                            runState.traces, runState.traceId, ref runState.traceSequence, runState.turn,
                            structuredWriterProtocolValid,
                            structuredWriterFailureReason,
                            runState.semanticRowLabels.Count, runState.semanticColumnLabels.Length,
                            structuredWriterSelectionIds, iteration.completion, writerExecution);
                    }
                    else
                    {
                        (iteration.completion, candidateAuditExecution) =
                            await CompleteActionOrCandidateAuditAsync(
                                iteration.dedicatedCandidateAuditTurn,
                                runState.intake,
                                runState.semanticPlan,
                                runState.semanticCandidateObjectType,
                                runState.semanticCandidateEligibilityRule,
                                runState.semanticRowHeader,
                                runState.semanticRowLabels,
                                runState.semanticColumnLabels,
                                iteration.collectionCandidates, actionMessages,
                                actionTools, effectiveActionMaxTokens,
                                requireActionToolCall, ct)
                                .ConfigureAwait(false);
                        if (!iteration.dedicatedCandidateAuditTurn
                            && runState.candidateCollectionOpen)
                        {
                            researchTransitionExecution =
                                CompleteResearchTransition(iteration.completion);
                            iteration.completion = researchTransitionExecution.Completion;
                            if (researchTransitionExecution.Attempted)
                            {
                                AddTrace(runState.traces, Trace(
                                    runState.traceId,
                                    ref runState.traceSequence,
                                    SourceBackedPipelineStep.Planner,
                                    "source_backed_agent_v2.research_transition.strategy_completed",
                                    ("turn", runState.turn),
                                    ("action_id", researchTransitionExecution.ActionId),
                                    ("protocol_valid", researchTransitionExecution.ProtocolValid),
                                    ("llm_calls", researchTransitionExecution.LlmCallCount),
                                    ("failure_reason", researchTransitionExecution.FailureReason),
                                    ("decision_source", "llm_semantic_strategy")));
                            }
                        }
                    }
                    if (iteration.compactSingleFollowUpTurn)
                    {
                        iteration.completion = ApplyCompactSingleFollowUpAdjustment(
                            iteration.completion, runState.intake, runState.bundle, runState.traces, runState.traceId,
                            ref runState.traceSequence, runState.turn);
                    }
                }
                catch (SourceBackedLlmBudgetExceededException ex) when (
                    string.Equals(
                        ex.Reason,
                        "terminal_budget_only",
                        StringComparison.Ordinal)
                    && !iteration.semanticSelectionDecisionTurn
                    && !iteration.semanticYieldTerminalDecisionTurn)
                {
                    if (runState.candidateCollectionOpen
                        && UsesResearchTransitionTools(actionTools)
                        && !compactCandidateCollectionForContextRecovery
                        && !ex.Snapshot.NormalBudgetClosed)
                    {
                        // A large request can fail admission while a smaller
                        // one still fits. Keep the same tools and output limit;
                        // a second refusal uses the existing terminal path.
                        compactCandidateCollectionForContextRecovery = true;
                        AddTrace(runState.traces, Trace(
                            runState.traceId, ref runState.traceSequence,
                            SourceBackedPipelineStep.IterationController,
                            "source_backed_agent_v2.cumulative_budget.research_compaction_requested",
                            ("turn", runState.turn),
                            ("input_tokens", exactInputTokens),
                            ("reserved_output_tokens", effectiveActionMaxTokens),
                            ("charged_tokens", ex.Snapshot.ChargedTokens),
                            ("decision_source", "mechanical_budget_contract")));
                        continue;
                    }
                    TryActivateCumulativeBudgetFinalization(
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
                        ref runState.traceSequence,
                        force: true);
                    cumulativeBudgetRestartTurn = true;
                    break;
                }
                catch (HttpRequestException ex) when (
                    !contextRecoveryAttempted
                    && !iteration.semanticSelectionDecisionTurn
                    && IsContextWindowExceeded(ex))
                {
                    contextRecoveryAttempted = true;
                    compactCandidateCollectionForContextRecovery |= runState.candidateCollectionOpen;
                    CompactWorkingMessages(
                        runState.messages,
                        runState.intake,
                        runState.semanticPlan,
                        runState.bundle,
                        runState.evidenceState.ObservedEvidenceIds,
                        runState.evidenceState.ExecutedRequests,
                        runState.evidenceWorkspace,
                        MergeAgentFeedback(
                            runState.semanticReviewFeedback,
                            runState.transientProtocolFeedback),
                        emergencyContextRecovery: true,
                        includeEvidenceDetails: !runState.candidateCollectionOpen);
                    runState.messages.Add(SourceBackedAgentMessage.User(
                        BuildContextRecoveryActionMessage()));
                    AddTrace(runState.traces, Trace(
                        runState.traceId,
                        ref runState.traceSequence,
                        SourceBackedPipelineStep.IterationController,
                        "source_backed_agent_v2.context.recovery_requested",
                        ("turn", runState.turn),
                        ("reason", "request_context_overflow"),
                        ("maximum_context_tokens", _options.MaximumContextTokens)));
                    continue;
                }
                if (!contextRecoveryAttempted
                    && !iteration.semanticSelectionDecisionTurn
                    && IsTruncatedToolProtocolCompletion(iteration.completion))
                {
                    contextRecoveryAttempted = true;
                    compactCandidateCollectionForContextRecovery |= runState.candidateCollectionOpen;
                    CompactWorkingMessages(
                        runState.messages,
                        runState.intake,
                        runState.semanticPlan,
                        runState.bundle,
                        runState.evidenceState.ObservedEvidenceIds,
                        runState.evidenceState.ExecutedRequests,
                        runState.evidenceWorkspace,
                        MergeAgentFeedback(
                            runState.semanticReviewFeedback,
                            runState.transientProtocolFeedback),
                        emergencyContextRecovery: true,
                        includeEvidenceDetails: !runState.candidateCollectionOpen);
                    runState.messages.Add(SourceBackedAgentMessage.User(
                        BuildContextRecoveryActionMessage()));
                    AddTrace(runState.traces, Trace(
                        runState.traceId,
                        ref runState.traceSequence,
                        SourceBackedPipelineStep.IterationController,
                        "source_backed_agent_v2.context.recovery_requested",
                        ("turn", runState.turn),
                        ("reason", "truncated_tool_protocol"),
                        ("prompt_tokens", iteration.completion.PromptTokens),
                        ("completion_tokens", iteration.completion.CompletionTokens),
                        ("maximum_context_tokens", _options.MaximumContextTokens)));
                    continue;
                }
                break;
            }
            if (cumulativeBudgetRestartTurn)
            {
                runState.turn--;
                return AgentIterationOutcome<SourceBackedPipelineResult>.Continue;
            }
            if (iteration.compactSingleFollowUpTurn)
            {
                runState.compactSingleFollowUpNextTurn = false;
                runState.yieldResolutionNextTurn = false;
            }
            AddTrace(runState.traces, Trace(
                runState.traceId,
                ref runState.traceSequence,
                SourceBackedPipelineStep.IterationController,
                "source_backed_agent_v2.turn.completed",
                ("turn", runState.turn),
                ("finish_reason", iteration.completion.FinishReason),
                ("tool_calls", iteration.completion.ToolCalls.Count),
                ("content_characters", iteration.completion.Content?.Length ?? 0),
                ("content_evidence_ids", SourceContractVerifier.ExtractEvidenceIds(
                    iteration.completion.Content ?? string.Empty).Count),
                 ("input_tokens_exact", exactInputTokens),
                 ("prompt_tokens", iteration.completion.PromptTokens),
                 ("completion_tokens", iteration.completion.CompletionTokens),
                 ("server_cache_tokens", iteration.completion.ServerCacheTokens),
                 ("server_prompt_evaluated_tokens",
                     iteration.completion.ServerPromptTokensEvaluated),
                 ("server_prompt_ms", iteration.completion.ServerPromptMilliseconds),
                 ("server_predicted_tokens", iteration.completion.ServerPredictedTokens),
                  ("server_predicted_ms",
                       iteration.completion.ServerPredictedMilliseconds),
                  ("compact_follow_up", iteration.compactSingleFollowUpTurn)));
            AddCandidateAuditPerformanceTrace(
                candidateAuditExecution,
                runState.traces,
                runState.traceId,
                ref runState.traceSequence,
                runState.turn);
            if (TryApplyCandidateCollectionAudit(
                    iteration.completion, iteration.collectionCandidates,
                    runState.evidenceState.PendingSemanticCandidateIds, ref runState.bundle, runState.evidenceWorkspace,
                    runState.evidenceState.SemanticallyAuditedEvidenceIds,
                    runState.evidenceState.SemanticallyRejectedEvidenceIds,
                    runState.evidenceState.ObservedEvidenceIdSet, runState.evidenceState.ObservedEvidenceIds,
                     runState.candidatePoolTargetCount,
                     runState.semanticLayoutDimensions is null,
                     runState.messages, runState.intake, runState.semanticPlan,
                     runState.semanticRowLabels, runState.semanticColumnLabels,
                    runState.evidenceState.ExecutedRequests, runState.traces, runState.traceId, ref runState.traceSequence, runState.turn,
                    ref runState.candidateCollectionOpen, ref runState.selectionOnlyNextTurn,
                    ref runState.compactSingleFollowUpNextTurn,
                    ref runState.yieldResolutionNextTurn,
                    ref runState.semanticYieldResolutionPending,
                    ref runState.semanticYieldResolutionContinuationCount,
                    ref runState.semanticYieldResolutionContinuationLimit,
                    runState.maximumRunTurnCeiling, ref runState.maximumRunTurns,
                    ref runState.semanticReviewFeedback,
                    candidateAuditExecution?.LlmCallCount ?? 1,
                    candidateAuditExecution?.CandidateDecisionCount ?? 0,
                    candidateAuditExecution?.ProtocolRepairCount ?? 0,
                    candidateAuditExecution?.LabelReviewLlmCallCount ?? 0,
                    candidateAuditExecution?.ElapsedMilliseconds,
                    candidateAuditExecution?.Decision))
                return AgentIterationOutcome<SourceBackedPipelineResult>.Continue;
            if (_options.SeparateActionAndWriter
                && !iteration.structuredCandidateWriterTurn
                && iteration.completion.ToolCalls.Count == 0)
            {
                runState.decisionOnlyNextTurn = false;
                if (!iteration.hasCitableEvidence && !iteration.finalizationTurn)
                {
                    if (HandleNoCitableActionDecision(
                            iteration.completion.Content, runState.bundle, runState.consecutiveDocumentaryNoProgressTurns,
                            runState.noCitableActionDecisionProgress, runState.messages, runState.traces, runState.traceId,
                            ref runState.traceSequence, runState.turn, runState.intake.Language,
                            out var terminalNote))
                    {
                        runState.semanticReviewReasons = new[] { terminalNote };
                        return AgentIterationOutcome<SourceBackedPipelineResult>.Stop;
                    }
                    return AgentIterationOutcome<SourceBackedPipelineResult>.Continue;
                }
                if (runState.requiredAtomicEvidenceCount is > 0
                    && iteration.observedCitableSourceCount < runState.requiredAtomicEvidenceCount.Value
                    && !iteration.finalizationTurn)
                {
                    runState.messages.Add(SourceBackedAgentMessage.User(
                        BuildAtomicEvidenceCountActionRepairMessage(
                            runState.requiredAtomicEvidenceCount.Value,
                            iteration.observedCitableSourceCount)));
                    AddTrace(runState.traces, Trace(
                        runState.traceId,
                        ref runState.traceSequence,
                        SourceBackedPipelineStep.SourceVerifier,
                        "source_backed_agent_v2.action.atomic_evidence_count_rejected",
                        ("turn", runState.turn),
                        ("required", runState.requiredAtomicEvidenceCount.Value),
                        ("observed", iteration.observedCitableSourceCount)));
                    return AgentIterationOutcome<SourceBackedPipelineResult>.Continue;
                }
                if (runState.requireEvidenceSelection
                    && runState.requiredAtomicEvidenceCount is > 0)
                {
                    if (iteration.observedCitableSourceCount
                        < runState.requiredAtomicEvidenceCount.Value)
                    {
                        if (iteration.finalizationTurn)
                        {
                            runState.semanticReviewReasons = new[]
                            {
                                "Décision finale de l'orchestrateur LLM: "
                                + TrimPromptValue(
                                    iteration.completion.Content,
                                    300),
                                "Le contrat mécanique exige "
                                + runState.requiredAtomicEvidenceCount.Value
                                + " sources visibles distinctes, mais "
                                + iteration.observedCitableSourceCount
                                + " seulement ont été observées."
                            };
                            AddTrace(runState.traces, Trace(
                                runState.traceId,
                                ref runState.traceSequence,
                                SourceBackedPipelineStep.EvidenceJudge,
                                "source_backed_agent_v2.insufficient_evidence.declared",
                                ("turn", runState.turn),
                                ("required",
                                    runState.requiredAtomicEvidenceCount.Value),
                                ("observed",
                                    iteration.observedCitableSourceCount),
                                ("decision",
                                    TrimPromptValue(
                                        iteration.completion.Content,
                                        300))));
                            return AgentIterationOutcome<SourceBackedPipelineResult>.Stop;
                        }
                        return AgentIterationOutcome<SourceBackedPipelineResult>.Continue;
                    }
                    runState.selectionOnlyNextTurn = true;
                    runState.transientProtocolFeedback =
                        BuildEvidenceSelectionRequiredMessage(
                            runState.requiredAtomicEvidenceCount.Value,
                            runState.enableWorkspaceWriterHandoff,
                            runState.canonicalWorkspaceSelectionLayout is not null);
                    runState.messages.Add(SourceBackedAgentMessage.User(
                        runState.transientProtocolFeedback));
                    AddTrace(runState.traces, Trace(
                        runState.traceId,
                        ref runState.traceSequence,
                        SourceBackedPipelineStep.IterationController,
                        "source_backed_agent_v2.action.evidence_selection_requested",
                        ("turn", runState.turn),
                        ("required", runState.requiredAtomicEvidenceCount.Value),
                        ("observed", iteration.observedCitableSourceCount),
                        ("reason", "writer_handoff_requires_explicit_llm_selection")));
                    return AgentIterationOutcome<SourceBackedPipelineResult>.Continue;
                }
                var actionDecision = iteration.completion.Content;
                if (runState.requiredAtomicEvidenceCount is > 1)
                {
                    runState.selectionOnlyNextTurn = true;
                    runState.semanticReviewFeedback = BuildSemanticSelectionContractRepairMessage(
                        runState.requiredAtomicEvidenceCount.Value,
                        0,
                        Array.Empty<string>(),
                        Array.Empty<string>(),
                        Array.Empty<string>(),
                        Array.Empty<string>(),
                        "selection_tool_required");
                    CompactWorkingMessages(
                        runState.messages,
                        runState.intake,
                        runState.semanticPlan,
                        runState.bundle,
                        runState.evidenceState.ObservedEvidenceIds,
                        runState.evidenceState.ExecutedRequests,
                        runState.evidenceWorkspace,
                        runState.semanticReviewFeedback);
                    AddTrace(runState.traces, Trace(
                        runState.traceId,
                        ref runState.traceSequence,
                        SourceBackedPipelineStep.SourceVerifier,
                        "source_backed_agent_v2.action.semantic_selection_rejected",
                        ("turn", runState.turn),
                        ("required", runState.requiredAtomicEvidenceCount.Value),
                        ("declared", 0),
                        ("unknown_evidence_ids", Array.Empty<string>())));
                    return AgentIterationOutcome<SourceBackedPipelineResult>.Continue;
                }
                iteration.completion = await CompleteDedicatedWriterAsync(
                        runState.messages, actionDecision, runState.intake, runState.bundle,
                        runState.activeSemanticSelectionIds, runState.atomicEvidenceMode, ct)
                    .ConfigureAwait(false);
                iteration.freshWriterOutputNeedsSemanticReview = true;
                AddWriterTrace(
                    runState.traces,
                    runState.traceId,
                    ref runState.traceSequence,
                    runState.turn,
                    iteration.completion,
                    directRevision: false);
            }
        }

        return AgentIterationOutcome<SourceBackedPipelineResult>.Advance;
    }
}
