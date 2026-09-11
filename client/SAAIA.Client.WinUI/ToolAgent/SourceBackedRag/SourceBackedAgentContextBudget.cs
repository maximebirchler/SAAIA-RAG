namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    internal const int ContextSafetyReserveTokens = 64;

    private async Task<int?> CountInputTokensAsync(
        IReadOnlyList<SourceBackedAgentMessage> messages,
        IReadOnlyList<SourceBackedAgentToolDefinition> tools,
        bool requireToolCall,
        CancellationToken ct)
    {
        if (_llm is not ISourceBackedAgentInputTokenCounter tokenCounter)
            return null;

        return await tokenCounter.CountInputTokensAsync(
                messages,
                tools,
                ct,
                requireToolCall)
            .ConfigureAwait(false);
    }

    private bool ExceedsContextBudget(
        int? inputTokens,
        int maximumOutputTokens)
        => inputTokens is { } measured
           && measured
              + Math.Max(0, maximumOutputTokens)
              + ContextSafetyReserveTokens
              > _options.MaximumContextTokens;
}
