namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private sealed record SemanticSelectionHandling(
        bool ContinueRun,
        SourceBackedAgentCompletion Completion,
        IReadOnlyList<string>? ActiveSemanticSelectionIds,
        SemanticSelectionLayout? ActiveSemanticSelectionLayout,
        bool ExplicitSemanticSelectionActive,
        bool SelectionOnlyNextTurn,
        int ConsecutiveDocumentaryNoProgressTurns,
        string? SemanticReviewFeedback,
        int TraceSequence,
        string? LastInvalidSelectionProtocolKey,
        int ConsecutiveIdenticalInvalidSelections,
        int MaximumRunTurns,
        int SelectionProtocolRepairTurns,
        bool AdaptiveFlatSelectionAccepted,
        bool FreshWriterOutputGenerated);

    private async Task<SemanticSelectionHandling>
        ProcessSemanticSelectionSubmissionAsync(
            SourceBackedAgentCompletion completion,
            int? requiredAtomicEvidenceCount,
            bool requireEvidenceSelection,
            bool allowAdaptiveFlatSelection,
            string atomicEvidenceMode,
            IReadOnlyList<string> semanticColumnLabels,
            string semanticRowHeader,
            IReadOnlyList<string> semanticRowLabels,
            EvidenceBundle bundle,
            IReadOnlyList<string> selectionEligibleObservedEvidenceIds,
            HashSet<string> semanticallyRejectedEvidenceIds,
            HashSet<string> observedEvidenceIdSet,
            List<string> observedEvidenceIds,
            HashSet<string> pendingSemanticCandidateIds,
            LlmEvidenceWorkspace evidenceWorkspace,
            bool selectionOnlyNextTurn,
            string? semanticReviewFeedback,
            List<SourceBackedTraceEvent> traces,
            string traceId,
            int traceSequence,
            int turn,
            string? lastInvalidSelectionProtocolKey,
            int consecutiveIdenticalInvalidSelections,
            int maximumRunTurns,
            int maximumRunTurnCeiling,
            int selectionProtocolRepairTurns,
            List<SourceBackedAgentMessage> messages,
            SourceBackedIntake intake,
            string semanticPlan,
            List<RetrievalRequest> executedRequests,
            HashSet<string> semanticallyAuditedEvidenceIds,
            IReadOnlyList<string>? activeSemanticSelectionIds,
            SemanticSelectionLayout? activeSemanticSelectionLayout,
            bool explicitSemanticSelectionActive,
            int consecutiveDocumentaryNoProgressTurns,
            CancellationToken ct)
    {
        var adaptiveFlatSelectionAccepted = false;
        var freshWriterOutputGenerated = false;
        SemanticSelectionHandling BuildResult(bool continueRun)
            => new(
                continueRun, completion, activeSemanticSelectionIds,
                activeSemanticSelectionLayout, explicitSemanticSelectionActive,
                selectionOnlyNextTurn, consecutiveDocumentaryNoProgressTurns,
                semanticReviewFeedback, traceSequence, lastInvalidSelectionProtocolKey,
                consecutiveIdenticalInvalidSelections, maximumRunTurns,
                selectionProtocolRepairTurns,
                adaptiveFlatSelectionAccepted, freshWriterOutputGenerated);

        if (_options.SeparateActionAndWriter
            && requiredAtomicEvidenceCount is > 0
            && (requiredAtomicEvidenceCount > 1 || requireEvidenceSelection || selectionOnlyNextTurn))
        {
            var selectionCalls = completion.ToolCalls
                .Where(static call => string.Equals(
                    call.Name,
                    SemanticSelectionToolName,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (selectionCalls.Length > 0)
            {
                IReadOnlyList<string> declaredSelection = Array.Empty<string>();
                SemanticSelectionLayout? declaredLayout = null;
                var selectionContractError = string.Empty;
                var parsed = selectionCalls.Length == 1
                             && completion.ToolCalls.Count == 1
                             && TryReadSemanticSelection(
                                 selectionCalls[0].Arguments,
                                 semanticColumnLabels,
                                 semanticRowHeader,
                                 semanticRowLabels,
                                 out declaredSelection,
                                 out declaredLayout,
                                 out selectionContractError);
                var selectionValidation =
                    ValidateSemanticSelectionMechanically(
                        parsed,
                        bundle,
                        declaredSelection,
                        selectionEligibleObservedEvidenceIds,
                        semanticallyRejectedEvidenceIds,
                        atomicEvidenceMode);
                var selectionCountAccepted =
                    declaredSelection.Count == requiredAtomicEvidenceCount.Value
                    || allowAdaptiveFlatSelection
                    && declaredLayout is null
                    && declaredSelection.Count is > 0
                    && declaredSelection.Count <= requiredAtomicEvidenceCount.Value;
                if (!parsed
                    || !selectionCountAccepted
                    || selectionValidation.HasFailure)
                {
                    SuppressNonRenderableSelectionEvidence(
                        selectionValidation.NonRenderableEvidenceIds,
                        observedEvidenceIdSet,
                        observedEvidenceIds,
                        pendingSemanticCandidateIds,
                        evidenceWorkspace);
                    var selectableVisibleSourceCount =
                        CountObservedSelectableEvidenceUnits(
                        bundle,
                        selectionValidation.VisibleCitableIds.Where(id =>
                            !semanticallyRejectedEvidenceIds.Contains(id)),
                        atomicEvidenceMode);
                    selectionOnlyNextTurn =
                        selectableVisibleSourceCount >= requiredAtomicEvidenceCount.Value;
                    semanticReviewFeedback = BuildSemanticSelectionContractRepairMessage(
                        requiredAtomicEvidenceCount.Value,
                        declaredSelection.Count,
                        selectionValidation.UnknownEvidenceIds,
                        selectionValidation.ResubmittedRejectedEvidenceIds,
                        selectionValidation.DuplicateVisibleSourceDetails,
                        selectionValidation.DuplicateDisplayValueDetails,
                        selectionContractError,
                        selectableVisibleSourceCount,
                        selectionValidation.NonRenderableEvidenceIds);
                    var repeatedProtocolStopped =
                        StopRepeatedSelectionProtocolWhenNeeded(
                            traces, traceId, ref traceSequence, turn,
                            selectionCalls, selectionContractError,
                            ref lastInvalidSelectionProtocolKey,
                            ref consecutiveIdenticalInvalidSelections,
                            ref maximumRunTurns);
                    if (!repeatedProtocolStopped)
                    {
                        ExtendSelectionProtocolRepairBudgetWhenPossible(
                            traces, traceId, ref traceSequence, turn,
                            selectionOnlyNextTurn, selectionContractError,
                            maximumRunTurnCeiling, ref maximumRunTurns,
                            ref selectionProtocolRepairTurns);
                    }
                    CompactWorkingMessages(
                        messages,
                        intake,
                        semanticPlan,
                        bundle,
                        observedEvidenceIds,
                        executedRequests,
                        evidenceWorkspace,
                        semanticReviewFeedback);
                    AddTrace(traces, Trace(
                        traceId,
                        ref traceSequence,
                        SourceBackedPipelineStep.SourceVerifier,
                        "source_backed_agent_v2.action.semantic_selection_rejected",
                        ("turn", turn),
                        ("required", requiredAtomicEvidenceCount.Value),
                        ("declared", declaredSelection.Count),
                        ("unknown_evidence_ids", selectionValidation.UnknownEvidenceIds),
                        ("semantically_rejected_evidence_ids",
                            selectionValidation.ResubmittedRejectedEvidenceIds),
                        ("duplicate_visible_source_ids",
                            selectionValidation.DuplicateVisibleSources.Select(
                                static duplicate =>
                                    duplicate.DuplicateEvidenceId)),
                        ("duplicate_visible_source_details",
                            selectionValidation.DuplicateVisibleSourceDetails),
                        ("duplicate_display_value_ids",
                            selectionValidation.DuplicateDisplayValues.Select(
                                static duplicate =>
                                    duplicate.DuplicateEvidenceId)),
                        ("duplicate_display_value_details",
                            selectionValidation.DuplicateDisplayValueDetails),
                        ("non_renderable_evidence_ids",
                            selectionValidation.NonRenderableEvidenceIds),
                        ("selectable_visible_sources", selectableVisibleSourceCount),
                        ("selection_contract_error", selectionContractError),
                        ("selection_arguments",
                            selectionCalls.Length == 1
                                ? TrimPromptValue(
                                    selectionCalls[0].Arguments.GetRawText(),
                                    1800)
                                : string.Empty),
                        ("mixed_tool_calls", completion.ToolCalls.Count != 1)));
                    return BuildResult(true);
                }

                activeSemanticSelectionIds = declaredSelection;
                activeSemanticSelectionLayout = declaredLayout;
                adaptiveFlatSelectionAccepted = allowAdaptiveFlatSelection;
                explicitSemanticSelectionActive = declaredSelection.All(id =>
                    semanticallyAuditedEvidenceIds.Contains(id)
                    && bundle.ById.TryGetValue(id, out var item)
                    && item.SelectionHints.ContainsKey(
                        SemanticDisplayValueHint));
                selectionOnlyNextTurn = false;
                consecutiveDocumentaryNoProgressTurns = 0;
                AddTrace(traces, Trace(
                    traceId,
                    ref traceSequence,
                    SourceBackedPipelineStep.IterationController,
                    "source_backed_agent_v2.action.semantic_selection_accepted",
                    ("turn", turn),
                    ("selected_evidence", declaredSelection),
                    ("count_mode", allowAdaptiveFlatSelection
                        ? "adaptive_flat_target"
                        : "exact"),
                    ("planned_target", requiredAtomicEvidenceCount.Value),
                    ("selected_count", declaredSelection.Count)));
                if (declaredLayout is not null)
                {
                    completion = new SourceBackedAgentCompletion(
                        RenderSemanticSelectionLayout(
                            bundle,
                            declaredLayout,
                            declaredSelection),
                        Array.Empty<SourceBackedAgentToolCall>(),
                        "stop",
                        0,
                        0);
                    AddTrace(traces, Trace(
                        traceId,
                        ref traceSequence,
                        SourceBackedPipelineStep.Writer,
                        "source_backed_agent_v2.structured_renderer.completed",
                        ("turn", turn),
                        ("rows", declaredLayout.RowLabels.Count),
                        ("columns", declaredLayout.Columns.Count),
                        ("evidence_ids", declaredSelection.Count),
                        ("content_characters", completion.Content.Length)));
                }
                else
                {
                    using var cumulativeBudgetTerminalWriterScope =
                        SourceBackedLlmCumulativeBudgetContext.PushTerminalCall();
                    completion = await CompleteDedicatedWriterAsync(
                            messages, "SELECTION_LLM_AUTORISEE: "
                                      + string.Join(", ", declaredSelection),
                            intake, bundle, activeSemanticSelectionIds,
                            atomicEvidenceMode, ct)
                        .ConfigureAwait(false);
                    freshWriterOutputGenerated = true;
                    AddWriterTrace(
                        traces,
                        traceId,
                        ref traceSequence,
                        turn,
                        completion,
                        directRevision: false);
                }
            }
        }

        return BuildResult(false);
    }
}
