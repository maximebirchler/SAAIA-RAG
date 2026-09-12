using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal enum AdvancedAnalysisProviderLocation
{
    Internal,
    ExternalService
}

internal sealed record AdvancedAnalysisResolvedEvidence(
    AdvancedAnalysisResultEvidence Reference,
    string Content,
    string? ExactTitle = null);

internal sealed record AdvancedAnalysisProviderRequest(
    Guid JobId,
    Guid TenantId,
    string UserId,
    AdvancedAnalysisHandoffEnvelope Handoff,
    IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence,
    IReadOnlyList<AdvancedAnalysisToolEventSummary> PreviousToolEvents);

internal sealed class AdvancedAnalysisProviderResult
{
    public string Outcome { get; init; } = string.Empty;

    public string AnswerText { get; init; } = string.Empty;

    public List<AdvancedAnalysisResultClaim> Claims { get; init; } = new();

    public string ModelId { get; init; } = string.Empty;

    public int ProviderCallCount { get; init; }

    public int? InputTokens { get; init; }

    public int? OutputTokens { get; init; }

    public int? CachedInputTokens { get; init; }

    public decimal? EstimatedCostUsd { get; init; }
}

internal interface IAdvancedAnalysisProvider
{
    string ProviderKey { get; }

    string ModelId => string.Empty;

    AdvancedAnalysisProviderLocation Location { get; }

    Task<AdvancedAnalysisProviderResult> ExecuteAsync(
        AdvancedAnalysisProviderRequest request,
        IAdvancedAnalysisToolGateway tools,
        CancellationToken cancellationToken);
}

internal sealed class AdvancedAnalysisProviderException : Exception
{
    public string ErrorCode { get; }

    public bool IsRetryable { get; }

    public long? RetryAfterMilliseconds { get; }

    public AdvancedAnalysisProviderException(
        string errorCode,
        bool isRetryable = false,
        long? retryAfterMilliseconds = null)
        : base(errorCode)
    {
        ErrorCode = errorCode;
        IsRetryable = isRetryable;
        RetryAfterMilliseconds = retryAfterMilliseconds is >= 0
            ? retryAfterMilliseconds
            : null;
    }
}

internal sealed class DisabledAdvancedAnalysisProvider : IAdvancedAnalysisProvider
{
    public string ProviderKey => "disabled";

    public string ModelId => string.Empty;

    public AdvancedAnalysisProviderLocation Location =>
        AdvancedAnalysisProviderLocation.Internal;

    public Task<AdvancedAnalysisProviderResult> ExecuteAsync(
        AdvancedAnalysisProviderRequest request,
        IAdvancedAnalysisToolGateway tools,
        CancellationToken cancellationToken)
        => throw new AdvancedAnalysisProviderException("provider_disabled");
}
