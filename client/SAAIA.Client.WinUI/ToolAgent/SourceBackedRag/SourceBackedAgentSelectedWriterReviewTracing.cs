namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private void TraceReusedSelectionReview(
        List<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn,
        IReadOnlyList<string>? selectedEvidenceIds)
        => AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.AnswerAdequacyJudge,
            "source_backed_agent_v2.semantic_review.skipped_after_explicit_selection",
            ("turn", turn),
            ("selected_evidence_ids", selectedEvidenceIds ?? Array.Empty<string>()),
            ("decision_source", "llm_evidence_judge"),
            ("mechanically_verified", true)));
}
