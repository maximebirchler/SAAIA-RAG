using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private EvidenceWorkspaceApplication ApplyEvidenceWorkspaceToolCalls(
        IReadOnlyList<SourceBackedAgentToolCall> toolCalls,
        ICollection<SourceBackedAgentMessage> messages,
        HashSet<string> observedEvidenceIdSet,
        List<string> observedEvidenceIds,
        EvidenceBundle bundle,
        LlmEvidenceWorkspace evidenceWorkspace,
        ISet<string> semanticallyRejectedEvidenceIds,
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn,
        bool enableWriterHandoff,
        int? requiredReadyEvidenceCount)
    {
        var documentaryToolCalls = toolCalls
            .Where(static call => !string.Equals(
                call.Name,
                EvidenceWorkspaceToolName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var completedUsefulToolCall = false;
        var noOpWorkspaceCallCount = 0;
        IReadOnlyList<string> readyEvidenceIds = Array.Empty<string>();
        foreach (var workspaceCall in toolCalls.Where(
                     static call => string.Equals(
                         call.Name,
                         EvidenceWorkspaceToolName,
                         StringComparison.OrdinalIgnoreCase)))
        {
            if (!TryReadEvidenceWorkspaceUpdate(
                    workspaceCall.Arguments,
                    observedEvidenceIdSet,
                    bundle,
                    evidenceWorkspace,
                    out var workspaceUpdate,
                    out var workspaceError))
            {
                messages.Add(SourceBackedAgentMessage.Tool(
                    workspaceCall.Id,
                    workspaceCall.Name,
                    SourceBackedAgentObservationCompactor.BuildProtocolError(
                        workspaceCall.Name,
                        workspaceError,
                        "Utilise uniquement des EvidenceId deja visibles, citables et non contradictoires, ou poursuis sans modifier l'espace de preuves.")));
                AddTrace(traces, Trace(
                    traceId,
                    ref traceSequence,
                    SourceBackedPipelineStep.IterationController,
                    "source_backed_agent_v2.evidence_workspace.rejected",
                    ("turn", turn),
                    ("error", workspaceError)));
                continue;
            }

            var readyToWrite = string.Equals(
                workspaceUpdate.Status,
                EvidenceWorkspaceReadyStatus,
                StringComparison.Ordinal);
            var validStatus = string.Equals(
                                  workspaceUpdate.Status,
                                  EvidenceWorkspaceContinueStatus,
                                  StringComparison.Ordinal)
                              || readyToWrite;
            var duplicateReadySourceSelections = readyToWrite
                ? FindDuplicateVisibleSourceSelections(
                    bundle,
                    workspaceUpdate.RetainEvidenceIds)
                : Array.Empty<DuplicateVisibleSourceSelection>();
            var handoffError = !validStatus
                ? "workspace_status_invalid"
                : readyToWrite && !enableWriterHandoff
                    ? "workspace_writer_handoff_not_available"
                    : readyToWrite && toolCalls.Count != 1
                        ? "workspace_ready_to_write_must_be_the_only_tool_call"
                        : readyToWrite
                          && requiredReadyEvidenceCount is > 0
                           && workspaceUpdate.RetainEvidenceIds.Count
                           != requiredReadyEvidenceCount.Value
                             ? "workspace_ready_to_write_requires_exactly_"
                               + requiredReadyEvidenceCount.Value
                               + "_retained_evidence_ids"
                            : readyToWrite
                              && duplicateReadySourceSelections.Count > 0
                                ? "workspace_ready_to_write_requires_unique_visible_sources:"
                                  + string.Join(
                                      ",",
                                      duplicateReadySourceSelections.Select(
                                          static duplicate =>
                                              duplicate.DuplicateEvidenceId
                                              + "=same_source_as_"
                                              + duplicate.KeptEvidenceId))
                             : null;
            if (handoffError is not null)
            {
                messages.Add(SourceBackedAgentMessage.Tool(
                    workspaceCall.Id,
                    workspaceCall.Name,
                    SourceBackedAgentObservationCompactor.BuildProtocolError(
                        workspaceCall.Name,
                        handoffError,
                        enableWriterHandoff
                            ? "Choisis continue_research pour poursuivre, ou appelle ready_to_write seul avec exactement la selection finale requise."
                            : "Utilise continue_research et poursuis avec les capacites exposees.")));
                AddTrace(traces, Trace(
                    traceId,
                    ref traceSequence,
                    SourceBackedPipelineStep.IterationController,
                    "source_backed_agent_v2.evidence_workspace.rejected",
                    ("turn", turn),
                    ("error", handoffError),
                    ("status", workspaceUpdate.Status),
                    ("retained_count", workspaceUpdate.RetainEvidenceIds.Count),
                    ("retained_evidence_ids", workspaceUpdate.RetainEvidenceIds),
                    ("rejected_count", workspaceUpdate.RejectEvidenceIds.Count)));
                continue;
            }

            if (!readyToWrite && !evidenceWorkspace.WouldChange(workspaceUpdate))
            {
                noOpWorkspaceCallCount++;
                messages.Add(SourceBackedAgentMessage.Tool(
                    workspaceCall.Id,
                    workspaceCall.Name,
                    SourceBackedAgentObservationCompactor.BuildProtocolError(
                        workspaceCall.Name,
                        "workspace_update_is_unchanged",
                        "Cette decision est deja memorisee exactement. Choisis une action documentaire differente, combine une nouvelle decision avec cette action, ou termine la collecte lorsque les preuves suffisent.")));
                AddTrace(traces, Trace(
                    traceId,
                    ref traceSequence,
                    SourceBackedPipelineStep.IterationController,
                    "source_backed_agent_v2.evidence_workspace.duplicate_rejected",
                    ("turn", turn),
                    ("status", workspaceUpdate.Status),
                    ("retained_evidence_ids", workspaceUpdate.RetainEvidenceIds),
                    ("rejected_evidence_ids", workspaceUpdate.RejectEvidenceIds)));
                continue;
            }

            foreach (var evidenceId in workspaceUpdate.RetainEvidenceIds)
                evidenceWorkspace.Retain(evidenceId, workspaceUpdate.Note);
            foreach (var evidenceId in workspaceUpdate.RejectEvidenceIds)
            {
                evidenceWorkspace.Reject(evidenceId, workspaceUpdate.Note);
                semanticallyRejectedEvidenceIds.Add(evidenceId);
                observedEvidenceIdSet.Remove(evidenceId);
                observedEvidenceIds.RemoveAll(id => string.Equals(
                    id,
                    evidenceId,
                    StringComparison.OrdinalIgnoreCase));
            }
            messages.Add(SourceBackedAgentMessage.Tool(
                workspaceCall.Id,
                workspaceCall.Name,
                BuildEvidenceWorkspaceResult(
                    workspaceUpdate,
                    evidenceWorkspace)));
            completedUsefulToolCall = true;
            if (readyToWrite)
                readyEvidenceIds = workspaceUpdate.RetainEvidenceIds;
            AddTrace(traces, Trace(
                traceId,
                ref traceSequence,
                SourceBackedPipelineStep.IterationController,
                "source_backed_agent_v2.evidence_workspace.updated",
                ("turn", turn),
                ("status", workspaceUpdate.Status),
                ("retained_evidence_ids", workspaceUpdate.RetainEvidenceIds),
                ("rejected_evidence_ids", workspaceUpdate.RejectEvidenceIds),
                ("note", workspaceUpdate.Note),
                ("note_characters", workspaceUpdate.Note.Length),
                ("retained_total", evidenceWorkspace.RetainedEvidenceIds.Count),
                ("rejected_total", evidenceWorkspace.RejectedEvidenceIds.Count)));
        }

        return new EvidenceWorkspaceApplication(
            documentaryToolCalls,
            completedUsefulToolCall,
            noOpWorkspaceCallCount,
            readyEvidenceIds);
    }

    private bool ApplyWorkspaceWriterHandoff(
        EvidenceWorkspaceApplication application,
        ref IReadOnlyList<string>? activeSelectionIds,
        ref SemanticSelectionLayout? activeSelectionLayout,
        SemanticSelectionLayout? canonicalSelectionLayout,
        ref bool directWriterRequested,
        ref bool selectionOnlyNextTurn,
        ref bool decisionOnlyNextTurn,
        ref int consecutiveDocumentaryNoProgressTurns,
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn)
    {
        if (application.ReadyEvidenceIds.Count == 0)
            return false;

        activeSelectionIds = application.ReadyEvidenceIds;
        activeSelectionLayout = canonicalSelectionLayout;
        directWriterRequested = true;
        selectionOnlyNextTurn = false;
        decisionOnlyNextTurn = false;
        consecutiveDocumentaryNoProgressTurns = 0;
        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.IterationController,
            "source_backed_agent_v2.evidence_workspace.ready_to_write_accepted",
            ("turn", turn),
            ("selected_evidence_ids", application.ReadyEvidenceIds),
            ("rows", canonicalSelectionLayout?.RowLabels.Count),
            ("columns", canonicalSelectionLayout?.Columns.Count),
            ("selection_source", "llm_evidence_workspace")));
        return true;
    }
}
