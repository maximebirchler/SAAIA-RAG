namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private sealed record DirectWriterHandoff(
        SourceBackedAgentCompletion Completion,
        SemanticSelectionLayout? Layout,
        string? ProtocolError = null,
        int? StructuredClaimCount = null,
        bool NamedValueProtocolRepairAttempted = false,
        SourceBackedAgentCompletion? OriginalCompletion = null,
        string? OriginalProtocolError = null);

    private static SemanticSelectionLayout?
        BuildCanonicalWorkspaceSelectionLayout(
            SemanticLayoutDimensions? dimensions,
            string rowHeader,
            IReadOnlyList<string> columnLabels,
            IReadOnlyList<string> rowLabels)
        => dimensions is not null
           && !string.IsNullOrWhiteSpace(rowHeader)
           && columnLabels.Count > 0
           && rowLabels.Count > 0
            ? new SemanticSelectionLayout(
                rowHeader,
                columnLabels,
                rowLabels)
            : null;

    private async Task<DirectWriterHandoff>
        CompleteDirectWriterHandoffAsync(
            IReadOnlyList<SourceBackedAgentMessage> messages,
            SourceVerificationResult? verification,
            string? directRevisionInstruction,
            SourceBackedIntake intake,
            EvidenceBundle bundle,
        IReadOnlyList<string>? activeSelectionIds,
        SemanticSelectionLayout? activeSelectionLayout,
        string atomicEvidenceMode,
        int? requiredStructuredClaimCount,
        CancellationToken ct)
    {
        using var cumulativeBudgetTerminalScope =
            SourceBackedLlmCumulativeBudgetContext.PushTerminalCall();
        if (activeSelectionLayout is not null
            && activeSelectionIds is { Count: > 0 })
        {
            return new DirectWriterHandoff(
                new SourceBackedAgentCompletion(
                    RenderSemanticSelectionLayout(
                        bundle,
                        activeSelectionLayout,
                        activeSelectionIds),
                    Array.Empty<SourceBackedAgentToolCall>(),
                    "stop",
                    0,
                    0),
                activeSelectionLayout);
        }

        if (_options.StructuredFlatWriterEnabled
            && activeSelectionIds is { Count: > 0 }
            && _llm is ISourceBackedAgentStructuredLlmClient)
        {
            var execution = await CompleteStructuredFlatWriterAsync(
                    intake,
                    bundle,
                    activeSelectionIds,
                    atomicEvidenceMode,
                    !string.IsNullOrWhiteSpace(directRevisionInstruction)
                        ? directRevisionInstruction
                        : verification is null
                            ? null
                            : BuildMechanicalRepairMessage(verification),
                    ct,
                    requiredClaimCount: requiredStructuredClaimCount)
                .ConfigureAwait(false);
            return new DirectWriterHandoff(
                execution.Completion,
                null,
                execution.ProtocolValid ? null : execution.FailureReason,
                execution.ProtocolValid ? execution.ClaimCount : null);
        }

        var completion = await CompleteDedicatedWriterAsync(
                messages,
                !string.IsNullOrWhiteSpace(directRevisionInstruction)
                    ? directRevisionInstruction
                    : verification is null
                        ? null
                        : BuildMechanicalRepairMessage(verification),
                intake,
                bundle,
                activeSelectionIds,
                atomicEvidenceMode,
                ct)
            .ConfigureAwait(false);
        return new DirectWriterHandoff(completion, null);
    }
}
