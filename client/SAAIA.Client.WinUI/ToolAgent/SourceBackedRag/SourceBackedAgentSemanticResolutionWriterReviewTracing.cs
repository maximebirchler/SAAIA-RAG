namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private void AddSemanticResolutionTrace(
        List<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn,
        FastEvidenceReview review)
    {
        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.AnswerAdequacyJudge,
            "source_backed_agent_v2.semantic_resolution.completed",
            ("turn", turn),
            ("decision", review.Decision),
            ("next_capability", review.NextCapability),
            ("protocol_valid", review.ProtocolValid),
            ("selected_evidence_ids", review.EvidenceIds),
            ("lead_evidence_ids", review.LeadEvidenceIds),
            ("answer_adequacy", review.AnswerAdequacy),
            ("requested_deliverable_complete", review.RequestedDeliverableComplete),
            ("missing_user_input_prevents_unique_result",
                review.MissingUserInputPreventsUniqueResult),
            ("visible_context_evidence_id", review.VisibleContextEvidenceId),
            ("derived_alias_source", "decision"),
            ("prompt_tokens", review.Completion.PromptTokens),
            ("completion_tokens", review.Completion.CompletionTokens),
            ("server_cache_tokens", review.Completion.ServerCacheTokens),
            ("ms", review.ElapsedMilliseconds),
            ("protocol_error", review.Completion.ProtocolError)));
    }

    private void AddSemanticResolutionPublicationBlockedTrace(
        List<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn,
        string reason,
        IReadOnlyList<string>? reasons = null,
        int? repairAttempts = null)
    {
        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.AnswerAdequacyJudge,
            "source_backed_agent_v2.semantic_resolution_writer_review.publication_blocked",
            ("turn", turn),
            ("reason", reason),
            ("reasons", reasons),
            ("repair_attempts", repairAttempts)));
    }

    private Task<SourceBackedAgentCompletion> CompleteSemanticResolutionWriterAsync(
        IReadOnlyCollection<SourceBackedAgentMessage> messages,
        SourceBackedIntake intake,
        EvidenceBundle bundle,
        IReadOnlyList<string> selectedEvidenceIds,
        IReadOnlyList<string> leadEvidenceIds,
        string atomicEvidenceMode,
        string? semanticJudgeAssessment,
        CancellationToken ct) =>
        CompleteDedicatedWriterAsync(
            messages,
            "SELECTION_LLM_AUTORISEE: " + string.Join(", ", selectedEvidenceIds),
            intake,
            bundle,
            selectedEvidenceIds,
            atomicEvidenceMode,
            ct,
            semanticJudgeAssessment,
            leadEvidenceIds);

    private Task<SourceBackedAgentCompletion> CompleteFastEvidenceWriterAsync(
        FastEvidenceReview review,
        IReadOnlyCollection<SourceBackedAgentMessage> messages,
        SourceBackedIntake intake,
        EvidenceBundle bundle,
        IReadOnlyList<string> selectedEvidenceIds,
        string atomicEvidenceMode,
        List<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(review.Answer))
        {
            return Task.FromResult(BuildInlineFastAnswerCompletion(
                review, traces, traceId, ref traceSequence, turn));
        }
        if (review.SemanticResolutionWriterReviewAttempted)
        {
            AddTrace(traces, Trace(
                traceId,
                ref traceSequence,
                SourceBackedPipelineStep.Writer,
                "source_backed_agent_v2.semantic_resolution.writer_handoff.completed",
                ("turn", turn),
                ("decision_source", "llm_evidence_judge"),
                ("decision", "answer"),
                ("selected_evidence_ids", selectedEvidenceIds),
                ("lead_evidence_ids", review.LeadEvidenceIds),
                ("assessment_forwarded", !string.IsNullOrWhiteSpace(review.Assessment)),
                ("assessment_characters", review.Assessment.Length),
                ("source_window_expansion_policy", "unchanged")));
        }
        return CompleteSemanticResolutionWriterAsync(
            messages,
            intake,
            bundle,
            selectedEvidenceIds,
            review.LeadEvidenceIds,
            atomicEvidenceMode,
            review.SemanticResolutionWriterReviewAttempted
                ? review.Assessment
                : null,
            ct);
    }
}
