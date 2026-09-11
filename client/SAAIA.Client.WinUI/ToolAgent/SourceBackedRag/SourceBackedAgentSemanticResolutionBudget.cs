namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private bool TryBlockSemanticResolutionContinuation(
        FastEvidenceReview review,
        int executedToolCalls,
        List<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn)
    {
        var continuationRequiresTool =
            review.SemanticResolutionWriterReviewAttempted
            && review.ProtocolValid
            && (string.Equals(
                    review.NextCapability,
                    "research",
                    StringComparison.Ordinal)
                || string.Equals(
                    review.NextCapability,
                    "documents_context",
                    StringComparison.Ordinal));
        if (!continuationRequiresTool
            || executedToolCalls < _options.MaximumToolCalls)
        {
            return false;
        }

        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.IterationController,
            "source_backed_agent_v2.semantic_resolution.continuation_blocked",
            ("turn", turn),
            ("reason", "tool_call_budget_exhausted"),
            ("next_capability", review.NextCapability),
            ("executed_tool_calls", executedToolCalls),
            ("maximum_tool_calls", _options.MaximumToolCalls),
            ("decision_source",
                "llm_evidence_judge+mechanical_budget_contract")));
        return true;
    }
}
