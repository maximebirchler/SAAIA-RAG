namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private sealed record DuplicateToolCallResolution(
        SourceBackedAgentToolCall Call,
        string CallKey,
        bool Rejected,
        int DuplicateCallCount);

    private DuplicateToolCallResolution ResolveDuplicateDocumentaryToolCall(
        SourceBackedAgentToolCall originalCall,
        SourceBackedAgentToolCall normalizedCall,
        string internalToolName,
        IReadOnlySet<string> executedCallKeys,
        ICollection<SourceBackedAgentMessage> messages,
        ICollection<SourceBackedTraceEvent> traces,
        string traceId,
        ref int traceSequence,
        int turn,
        bool hasCitableEvidence)
    {
        var callKey = BuildMechanicalCallKey(
            internalToolName,
            normalizedCall.Arguments);
        if (!executedCallKeys.Contains(callKey))
        {
            return new DuplicateToolCallResolution(
                normalizedCall,
                callKey,
                Rejected: false,
                DuplicateCallCount: 0);
        }

        // A repeated request is not a decision to visit the next page. Return the
        // duplicate observation so the model can choose an explicit continuation.
        messages.Add(SourceBackedAgentMessage.Tool(
            originalCall.Id,
            originalCall.Name,
            SourceBackedAgentObservationCompactor.BuildProtocolError(
                originalCall.Name,
                "duplicate_tool_call",
                BuildDuplicateToolCallRepairMessage(hasCitableEvidence))));
        AddTrace(traces, Trace(
            traceId,
            ref traceSequence,
            SourceBackedPipelineStep.RetrievalTools,
            "source_backed_agent_v2.tool.duplicate_rejected",
            ("turn", turn),
            ("tool", internalToolName),
            ("arguments", TrimPromptValue(
                normalizedCall.Arguments.GetRawText(),
                800)),
            ("call_key", TrimPromptValue(callKey, 800))));
        return new DuplicateToolCallResolution(
            normalizedCall,
            callKey,
            Rejected: true,
            DuplicateCallCount: 1);
    }
}
