using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

/// <summary>
/// OpenAI-compatible native tool contract used by the source-backed agent loop.
/// The LLM owns semantic planning; these records only preserve protocol state.
/// </summary>
public sealed record SourceBackedAgentToolDefinition(
    string Name,
    string Description,
    JsonElement Parameters);

public sealed record SourceBackedAgentToolCall(
    string Id,
    string Name,
    JsonElement Arguments,
    string? ArgumentError = null);

public sealed record SourceBackedAgentMessage(
    string Role,
    string? Content,
    IReadOnlyList<SourceBackedAgentToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    string? Name = null)
{
    public static SourceBackedAgentMessage System(string content)
        => new("system", content);

    public static SourceBackedAgentMessage User(string content)
        => new("user", content);

    public static SourceBackedAgentMessage Assistant(
        string? content,
        IReadOnlyList<SourceBackedAgentToolCall>? toolCalls = null)
        => new("assistant", content, toolCalls);

    public static SourceBackedAgentMessage Tool(
        string toolCallId,
        string name,
        string content)
        => new("tool", content, ToolCallId: toolCallId, Name: name);
}

public sealed record SourceBackedAgentCompletion(
    string Content,
    IReadOnlyList<SourceBackedAgentToolCall> ToolCalls,
    string FinishReason,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    int? ServerCacheTokens = null,
    int? ServerPromptTokensEvaluated = null,
    double? ServerPromptMilliseconds = null,
    int? ServerPredictedTokens = null,
    double? ServerPredictedMilliseconds = null,
    string? ProtocolError = null,
    string? ProtocolRawOutput = null,
    int? CachedInputTokens = null,
    int? CacheWriteTokens = null,
    int? ReasoningTokens = null,
    int RetryCount = 0);

public interface ISourceBackedAgentLlmClient
{
    Task<SourceBackedAgentCompletion> CompleteAsync(
        IReadOnlyList<SourceBackedAgentMessage> messages,
        IReadOnlyList<SourceBackedAgentToolDefinition> tools,
        int maxTokens,
        CancellationToken ct,
        double? temperatureOverride = null,
        bool requireToolCall = false);
}

/// <summary>
/// Optional structured-output capability for short source-backed decisions.
/// The LLM still owns the semantic choice; the schema only makes the transport
/// shape and evidence identities mechanically reliable.
/// </summary>
public interface ISourceBackedAgentStructuredLlmClient
{
    Task<SourceBackedAgentCompletion> CompleteStructuredAsync(
        IReadOnlyList<SourceBackedAgentMessage> messages,
        LlmStructuredOutputContract contract,
        int maxTokens,
        CancellationToken ct,
        double? temperatureOverride = null);
}

public interface ISourceBackedAgentInputTokenCounter
{
    Task<int?> CountInputTokensAsync(
        IReadOnlyList<SourceBackedAgentMessage> messages,
        IReadOnlyList<SourceBackedAgentToolDefinition> tools,
        CancellationToken ct,
        bool requireToolCall = false);
}

public interface ISourceBackedAgentRuntimeContextProvider
{
    Task<int?> GetRuntimeContextTokensAsync(CancellationToken ct);
}

public interface ISourceBackedAgentToolExecutor
{
    Task<ToolResults> ExecuteToolCallAsync(
        SourceBackedIntake intake,
        string toolName,
        JsonElement arguments,
        CancellationToken ct);
}
