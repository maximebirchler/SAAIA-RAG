using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public enum LlmProviderMode
{
    Local,
    OpenAiDev,
    RunPodBench
}

public enum LlmExternalExecutionPolicy
{
    ProductionLocal,
    DevelopmentExternalAllowed,
    BenchmarkExternalAllowed
}

public enum LlmLogicalRole
{
    Router,
    Writer,
    Critic,
    Agent,
    Unknown
}

public sealed record LlmRuntimeProfileMetadata(
    string? ModelPath,
    string? Quantization,
    int? ContextSize,
    int? BatchSize,
    int? UbatchSize,
    int? Threads,
    int? ThreadsBatch,
    int? GpuLayers,
    bool? FlashAttention);

public sealed record LlmProviderDescriptor(
    LlmProviderMode Mode,
    string Provider,
    string Runtime,
    string ModelId,
    string? RuntimeProfile,
    bool IsExternal,
    bool IsDevelopmentOnly,
    LlmRuntimeProfileMetadata? RuntimeParameters = null,
    int? ContextWindowTokens = null);

public sealed record LlmTokenUsage(
    int? InputTokens,
    int? OutputTokens,
    int? CachedInputTokens = null,
    int? CacheWriteTokens = null,
    int? ReasoningTokens = null);

public sealed record LlmCallMetrics(
    DateTimeOffset Timestamp,
    string RequestId,
    string TraceId,
    LlmProviderMode Mode,
    string Provider,
    string ModelId,
    LlmLogicalRole Role,
    long ElapsedMilliseconds,
    long? TimeToFirstTokenMilliseconds,
    LlmTokenUsage Usage,
    decimal? EstimatedCostUsd,
    bool Success,
    int RetryCount,
    string? ErrorCode);

public sealed class LlmProviderRequestTimeoutException(
    string provider,
    TimeSpan timeout,
    Exception innerException)
    : TimeoutException(
        $"LLM provider '{provider}' exceeded its configured {timeout.TotalSeconds:0}-second request timeout.",
        innerException);

/// <summary>
/// Single runtime boundary used by Router, Writer and Critic. Provider-specific
/// URLs, authentication and wire-format details remain behind this contract.
/// </summary>
public interface ILlmProvider :
    ILlmClient,
    ISourceBackedAgentLlmClient,
    ISourceBackedAgentStructuredLlmClient,
    ISourceBackedAgentInputTokenCounter,
    ISourceBackedAgentRuntimeContextProvider
{
    LlmProviderDescriptor Descriptor { get; }

    event Action<LlmCallMetrics>? CallCompleted;

    void ConfigureGeneration(double temperature, int maximumOutputTokens);

    IDisposable BeginTurn(string? correlationId = null);

    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct);
}
