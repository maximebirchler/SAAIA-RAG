using System.Globalization;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private sealed record DocumentaryToolBatchExecution(
        EvidenceBundle Bundle,
        IReadOnlyList<string>? ActiveSemanticSelectionIds,
        SemanticSelectionLayout? ActiveSemanticSelectionLayout,
        bool DirectWriterRequested, bool SelectionOnlyNextTurn, bool DecisionOnlyNextTurn,
        int ConsecutiveDocumentaryNoProgressTurns, string? TransientProtocolFeedback,
        int TotalToolCalls, int TraceSequence, bool CompletedUsefulToolCall,
        int DuplicateCallCount, int ZeroNewEvidenceCallCount, bool WorkspaceReadyForWriter,
        int DocumentaryToolCallCount, int WorkspaceNoOpCallCount,
        IReadOnlyList<string> NewlyObservedEvidenceIds);

    private async Task<DocumentaryToolBatchExecution>
        ExecuteDocumentaryToolBatchAsync(
            SourceBackedAgentCompletion completion,
            List<SourceBackedAgentMessage> messages,
            SourceBackedIntake intake,
            ToolResults cumulativeResults,
            EvidenceBundle bundle,
            LlmEvidenceWorkspace evidenceWorkspace,
            HashSet<string> observedEvidenceIdSet,
            List<string> observedEvidenceIds,
            HashSet<string> semanticallyRejectedEvidenceIds,
            List<SourceBackedTraceEvent> traces,
            string traceId,
            int traceSequence,
            int turn,
            bool enableWorkspaceWriterHandoff,
            int? requiredAtomicEvidenceCount,
            IReadOnlyList<string>? activeSemanticSelectionIds,
            SemanticSelectionLayout? activeSemanticSelectionLayout,
            SemanticSelectionLayout? canonicalWorkspaceSelectionLayout,
            bool directWriterRequested,
            bool selectionOnlyNextTurn,
            bool decisionOnlyNextTurn,
            int consecutiveDocumentaryNoProgressTurns,
            HashSet<string> resolvedNavigationEvidenceIds,
            List<RetrievalRequest> executedRequests,
            HashSet<string> executedCallKeys,
            int totalToolCalls,
            bool candidateCollectionOpen,
            SemanticCandidateStrategyOutcome semanticCandidateStrategyOutcome,
            bool hasCitableEvidence,
            string? transientProtocolFeedback,
            HashSet<string> semanticallyAuditedEvidenceIds,
            HashSet<string> pendingSemanticCandidateIds,
            CancellationToken ct)
    {
        messages.Add(SourceBackedAgentMessage.Assistant(completion.Content, completion.ToolCalls));
        var completedUsefulToolCall = false;
        var duplicateCallCount = 0;
        var zeroNewEvidenceCallCount = 0;
        var workspaceApplication = ApplyEvidenceWorkspaceToolCalls(
            completion.ToolCalls,
            messages,
            observedEvidenceIdSet,
            observedEvidenceIds,
            bundle,
            evidenceWorkspace,
            semanticallyRejectedEvidenceIds,
            traces,
            traceId,
            ref traceSequence,
            turn,
            enableWorkspaceWriterHandoff,
            requiredAtomicEvidenceCount);
        var documentaryToolCalls =
            workspaceApplication.DocumentaryToolCalls;
        duplicateCallCount +=
            workspaceApplication.NoOpWorkspaceCallCount;
        completedUsefulToolCall |=
            workspaceApplication.CompletedUsefulToolCall;
        var workspaceReadyForWriter = ApplyWorkspaceWriterHandoff(
            workspaceApplication,
            ref activeSemanticSelectionIds,
            ref activeSemanticSelectionLayout,
            canonicalWorkspaceSelectionLayout,
            ref directWriterRequested,
            ref selectionOnlyNextTurn,
            ref decisionOnlyNextTurn,
            ref consecutiveDocumentaryNoProgressTurns,
            traces,
            traceId,
            ref traceSequence,
            turn);
        var pendingExecutions = new List<(SourceBackedAgentToolCall Call, string InternalToolName)>();
        foreach (var call in documentaryToolCalls)
        {
            var normalizedCall = string.IsNullOrWhiteSpace(call.ArgumentError)
                ? call with
                {
                    Arguments =
                        SourceBackedAgentToolArgumentNormalizer.NormalizeDocumentLocator(
                            call.Arguments,
                            bundle)
                }
                : call;
            string? protocolError = candidateCollectionOpen ? ValidateResolvedDocumentReadingCall(call, intake) : null;
            var internalToolName = string.Empty;
            if (IsResearchTransitionToolName(normalizedCall.Name))
            {
                var requestedDocumentFocusEvidenceId = GetString(
                    normalizedCall.Arguments,
                    "documentFocusEvidenceId");
                if (TryExpandResearchTransitionCall(
                        normalizedCall,
                        bundle,
                        resolvedNavigationEvidenceIds,
                        executedRequests,
                        _options.MaximumWorkingEvidenceItems,
                        out var transitionCall,
                        out var transitionDecision,
                        out var ignoredTransitionEvidenceIdCount,
                        out var transitionError))
                {
                    normalizedCall = transitionCall;
                    AddTrace(traces, Trace(
                        traceId,
                        ref traceSequence,
                        SourceBackedPipelineStep.EvidenceJudge,
                        "source_backed_agent_v2.research_transition.accepted",
                        ("turn", turn),
                        ("decision", transitionDecision),
                        ("ignored_evidence_ids",
                            ignoredTransitionEvidenceIdCount),
                        ("translated_tool", normalizedCall.Name),
                        ("document_focus_evidence_id",
                            string.IsNullOrWhiteSpace(
                                requestedDocumentFocusEvidenceId)
                                ? "-"
                                : requestedDocumentFocusEvidenceId),
                        ("document_focus_doc_id",
                            GetString(normalizedCall.Arguments, "docId")),
                        ("document_focus_doc_path",
                            GetString(normalizedCall.Arguments, "docPath")),
                        ("arguments", TrimPromptValue(
                            normalizedCall.Arguments.GetRawText(),
                            1200))));
                }
                else
                {
                    protocolError = transitionError;
                }
            }
            if (string.Equals(
                    normalizedCall.Name,
                    NavigationContextBatchToolName,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (TryExpandNavigationContextBatchArguments(
                        normalizedCall.Arguments,
                        bundle,
                        out var expandedArguments,
                        out var batchError))
                {
                    normalizedCall = normalizedCall with
                    {
                        Arguments = expandedArguments
                    };
                }
                else
                {
                    protocolError = batchError;
                }
            }
            if (protocolError is null)
            {
                protocolError = ValidateToolCall(
                    normalizedCall,
                    out internalToolName);
            }
            if (protocolError is null)
            {
                protocolError = ValidateObservedDocumentLocator(
                    normalizedCall, internalToolName, bundle);
            }
            if (protocolError is null)
            {
                normalizedCall =
                    ApplyMechanicalToolArgumentAdjustments(
                        normalizedCall,
                        internalToolName,
                        candidateCollectionOpen
                            ? semanticCandidateStrategyOutcome.CandidateScopePaths
                            : Array.Empty<string>(),
                        traces,
                        traceId,
                        ref traceSequence,
                        turn);
            }
            if (protocolError is not null)
            {
                const string repairGuidance =
                    "Corrige l'appel ou choisis une autre action a partir des outils exposes.";
                transientProtocolFeedback = repairGuidance;
                messages.Add(SourceBackedAgentMessage.Tool(
                    call.Id,
                    call.Name,
                    SourceBackedAgentObservationCompactor.BuildProtocolError(
                        call.Name,
                        protocolError,
                        repairGuidance)));
                AddTrace(traces, Trace(
                    traceId,
                    ref traceSequence,
                    SourceBackedPipelineStep.RetrievalTools,
                    "source_backed_agent_v2.tool.rejected",
                    ("turn", turn),
                    ("tool", call.Name),
                    ("arguments",
                        TrimPromptValue(normalizedCall.Arguments.GetRawText(), 800)),
                    ("error", protocolError)));
                continue;
            }
            var duplicateResolution = ResolveDuplicateDocumentaryToolCall(
                call, normalizedCall, internalToolName,
                executedCallKeys, messages, traces, traceId,
                ref traceSequence, turn, hasCitableEvidence);
            normalizedCall = duplicateResolution.Call;
            var callKey = duplicateResolution.CallKey;
            duplicateCallCount += duplicateResolution.DuplicateCallCount;
            if (duplicateResolution.Rejected)
                continue;
            if (totalToolCalls >= _options.MaximumToolCalls)
            {
                messages.Add(SourceBackedAgentMessage.Tool(
                    call.Id,
                    call.Name,
                    SourceBackedAgentObservationCompactor.BuildProtocolError(
                        call.Name,
                        "tool_call_budget_exhausted",
                        "Aucun nouvel outil ne peut etre execute dans ce run. Utilise les preuves deja observees ou produis une insuffisance honnete.")));
                AddTrace(traces, Trace(
                    traceId,
                    ref traceSequence,
                    SourceBackedPipelineStep.RetrievalTools,
                    "source_backed_agent_v2.tool.rejected",
                    ("turn", turn),
                    ("tool", internalToolName),
                    ("arguments",
                        TrimPromptValue(normalizedCall.Arguments.GetRawText(), 800)),
                    ("error", "tool_call_budget_exhausted"),
                    ("executed_tool_calls", totalToolCalls)));
                continue;
            }
            totalToolCalls++;
            executedCallKeys.Add(callKey);
            pendingExecutions.Add((normalizedCall, internalToolName));
        }
        if (pendingExecutions.Count > 1)
        {
            AddTrace(traces, Trace(
                traceId,
                ref traceSequence,
                SourceBackedPipelineStep.RetrievalTools,
                "source_backed_agent_v2.parallel_batch.started",
                ("turn", turn),
                ("tool_calls", pendingExecutions.Count)));
        }

        var executionTasks = pendingExecutions
            .Select(async pending => (
                pending.Call,
                pending.InternalToolName,
                Results: await _toolExecutor.ExecuteToolCallAsync(
                        intake,
                        pending.InternalToolName,
                        pending.Call.Arguments,
                        ct)
                    .ConfigureAwait(false)))
            .ToArray();
        var executions = await Task.WhenAll(executionTasks).ConfigureAwait(false);
        if (pendingExecutions.Count > 1)
        {
            AddTrace(traces, Trace(
                traceId,
                ref traceSequence,
                SourceBackedPipelineStep.RetrievalTools,
                "source_backed_agent_v2.parallel_batch.completed",
                ("turn", turn),
                ("tool_calls", pendingExecutions.Count)));
        }

        var executionsWithSequences = new List<(
            SourceBackedAgentToolCall Call,
            string InternalToolName,
            ToolResults Results,
            int FirstSequence,
            int? NextOffset)>(executions.Length);
        foreach (var execution in executions)
        {
            var firstSequence = cumulativeResults.Items.Count + 1;
            foreach (var item in execution.Results.Items)
                cumulativeResults.Items.Add(item);
            var nextOffset =
                SourceBackedAgentObservationCompactor.ReadNextOffset(
                    execution.Results.Items);
            if (string.Equals(
                    execution.Call.Name,
                    NavigationContextBatchToolName,
                    StringComparison.OrdinalIgnoreCase))
            {
                var resolvedThisBatch = RecordResolvedNavigationEvidenceIds(
                    execution.Call.Arguments,
                    execution.Results,
                    resolvedNavigationEvidenceIds);
                AddTrace(traces, Trace(
                    traceId,
                    ref traceSequence,
                    SourceBackedPipelineStep.RetrievalTools,
                    "source_backed_agent_v2.navigation_anchors.consumed",
                    ("turn", turn),
                    ("resolved_this_batch", resolvedThisBatch),
                    ("resolved_total",
                        resolvedNavigationEvidenceIds.Count)));
            }
            executionsWithSequences.Add((
                execution.Call,
                execution.InternalToolName,
                execution.Results,
                firstSequence,
                nextOffset));
        }

        var rebuiltBundle = EvidenceBundleBuilder.FromToolResults(
            cumulativeResults,
            intake.UserQuestion,
            traceId,
            traceSequence + 1,
            materializeMatchedContentCards: true);
        bundle = PreserveAgentSemanticAnnotations(
            bundle,
            rebuiltBundle,
            out var preservedSemanticAnnotationCount);
        bundle = ApplyNamedDocumentIdentityContract(
            intake,
            bundle,
            out var requestedDocumentIdentityMismatches);
        if (requestedDocumentIdentityMismatches.Count > 0)
        {
            AddTrace(traces, Trace(
                traceId,
                ref traceSequence,
                SourceBackedPipelineStep.SourceVerifier,
                "source_backed_agent_v2.requested_document_identity.suppressed",
                ("turn", turn),
                ("evidence_ids", requestedDocumentIdentityMismatches),
                ("count", requestedDocumentIdentityMismatches.Count),
                ("document_scope", intake.DocumentScope),
                ("decision_source", "mechanical_identity_contract")));
        }
        if (preservedSemanticAnnotationCount > 0)
        {
            AddTrace(traces, Trace(
                traceId,
                ref traceSequence,
                SourceBackedPipelineStep.EvidenceJudge,
                "source_backed_agent_v2.evidence.semantic_annotations_preserved",
                ("turn", turn),
                ("preserved_annotations", preservedSemanticAnnotationCount),
                ("decision_source", "mechanical_evidence_identity")));
        }
        var materializedExecutions = executionsWithSequences
            .Select(execution => (
                execution.Call,
                execution.InternalToolName,
                execution.Results,
                execution.FirstSequence,
                execution.NextOffset,
                NewlyMaterializedEvidence:
                SourceBackedAgentObservationCompactor.SelectEvidenceItems(
                    bundle,
                    execution.FirstSequence,
                    execution.Results.Items.Count,
                    SourceBackedAgentObservationCompactor.ResolveMaximumItems(
                        execution.Call.Name,
                        _options))))
            .ToArray();
        var unauditedEvidence = materializedExecutions
            .SelectMany(static execution =>
                execution.NewlyMaterializedEvidence)
            .Where(IsMechanicallyCitableCandidate)
            .Where(HasRenderableEvidenceValue)
            .Where(item => !semanticallyAuditedEvidenceIds.Contains(
                item.EvidenceId))
            .DistinctBy(static item => item.EvidenceId)
            .ToArray();
        foreach (var candidate in unauditedEvidence)
            pendingSemanticCandidateIds.Add(candidate.EvidenceId);
        var invalidAuditEvidenceIds =
            FindMechanicallyNonRenderableEvidenceIds(
                materializedExecutions.SelectMany(static execution =>
                    execution.NewlyMaterializedEvidence));
        TraceMechanicallySuppressedEvidence(
            traces, traceId, ref traceSequence, turn,
            invalidAuditEvidenceIds);

        var newlyObservedEvidenceIds = new List<string>();
        foreach (var execution in materializedExecutions)
        {
            var evidenceToObserve = execution.NewlyMaterializedEvidence
                .Where(item => !semanticallyRejectedEvidenceIds.Contains(
                    item.EvidenceId))
                .Where(IsRequestedDocumentIdentityEligible)
                .Where(item => !invalidAuditEvidenceIds.Contains(
                    item.EvidenceId))
                .ToArray();
            var newlyObservedEvidenceIdsForExecution =
                new List<string>();
            foreach (var item in evidenceToObserve.Where(item =>
                         !semanticallyRejectedEvidenceIds.Contains(
                             item.EvidenceId)))
            {
                if (observedEvidenceIdSet.Add(item.EvidenceId))
                {
                    observedEvidenceIds.Add(item.EvidenceId);
                    newlyObservedEvidenceIds.Add(item.EvidenceId);
                    newlyObservedEvidenceIdsForExecution.Add(
                        item.EvidenceId);
                }
            }
            executedRequests.Add(ToRetrievalRequest(
                execution.InternalToolName,
                execution.Call.Arguments,
                turn,
                execution.NextOffset,
                execution.NewlyMaterializedEvidence.Count,
                newlyObservedEvidenceIdsForExecution.Count,
                newlyObservedEvidenceIdsForExecution));
            var visibleCategoryPaths = SourceBackedRetrievalScope
                .GetAvailableCategoryPaths(intake);
            var compactObservation = SourceBackedAgentObservationCompactor.Build(
                execution.Call.Name, execution.Results.Items, bundle,
                execution.FirstSequence, _options, execution.Call.Arguments,
                visibleCategoryPaths);
            messages.Add(SourceBackedAgentMessage.Tool(
                execution.Call.Id, execution.Call.Name, compactObservation));
            using var compactDocument = JsonDocument.Parse(compactObservation);
            if (compactDocument.RootElement.TryGetProperty(
                    "scopeYield", out var scopeYield))
                AddTrace(traces, Trace(
                    traceId, ref traceSequence, SourceBackedPipelineStep.RetrievalTools,
                    "source_backed_agent_v2.scope.zero_yield",
                    ("turn", turn), ("tool", execution.InternalToolName),
                    ("requested_category_path",
                        scopeYield.GetProperty("requestedCategoryPath").GetString()),
                    ("requested_scope_listed",
                        scopeYield.GetProperty("requestedScopeListed").GetBoolean()),
                    ("visible_category_paths", visibleCategoryPaths.Take(40).ToArray()),
                    ("semantic_decision_owner", "llm")));
            if (newlyObservedEvidenceIdsForExecution.Count > 0)
                completedUsefulToolCall = true;
            else
                zeroNewEvidenceCallCount++;
            AddTrace(traces, Trace(
                traceId,
                ref traceSequence,
                SourceBackedPipelineStep.RetrievalTools,
                "source_backed_agent_v2.tool.executed",
                ("turn", turn),
                ("tool", execution.InternalToolName),
                ("arguments", TrimPromptValue(
                    execution.Call.Arguments.GetRawText(),
                    800)),
                ("results", execution.Results.Items.Count),
                ("materialized_evidence_count",
                    execution.NewlyMaterializedEvidence.Count),
                ("evidence_items", bundle.Items.Count),
                ("new_evidence_count",
                    newlyObservedEvidenceIdsForExecution.Count),
                ("new_evidence_ids",
                    newlyObservedEvidenceIdsForExecution),
                ("new_evidence_sources",
                    newlyObservedEvidenceIdsForExecution
                    .Select(id => bundle.ById.TryGetValue(id, out var item)
                        ? string.Join(
                            "@",
                            item.DocName ?? item.DocPath ?? item.DocId ?? "source-inconnue",
                            item.PageStart?.ToString(CultureInfo.InvariantCulture) ?? "?")
                        : id)
                    .Take(12)
                    .ToArray())));
        }

        return new DocumentaryToolBatchExecution(
            bundle, activeSemanticSelectionIds, activeSemanticSelectionLayout,
            directWriterRequested, selectionOnlyNextTurn, decisionOnlyNextTurn,
            consecutiveDocumentaryNoProgressTurns, transientProtocolFeedback,
            totalToolCalls, traceSequence, completedUsefulToolCall,
            duplicateCallCount, zeroNewEvidenceCallCount, workspaceReadyForWriter,
            documentaryToolCalls.Length, workspaceApplication.NoOpWorkspaceCallCount,
            newlyObservedEvidenceIds);
    }
}
