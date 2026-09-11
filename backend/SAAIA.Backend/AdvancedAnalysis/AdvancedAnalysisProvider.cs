using SAAIA.Contracts;

namespace SAAIA.Backend.AdvancedAnalysis;

internal enum AdvancedAnalysisProviderLocation
{
    Internal,
    ExternalService
}

internal sealed record AdvancedAnalysisResolvedEvidence(
    AdvancedAnalysisResultEvidence Reference,
    string Content);

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
}

internal interface IAdvancedAnalysisProvider
{
    string ProviderKey { get; }

    AdvancedAnalysisProviderLocation Location { get; }

    Task<AdvancedAnalysisProviderResult> ExecuteAsync(
        AdvancedAnalysisProviderRequest request,
        IAdvancedAnalysisToolGateway tools,
        CancellationToken cancellationToken);
}

internal sealed class AdvancedAnalysisProviderException : Exception
{
    public string ErrorCode { get; }

    public AdvancedAnalysisProviderException(string errorCode)
        : base(errorCode)
    {
        ErrorCode = errorCode;
    }
}

internal sealed class DisabledAdvancedAnalysisProvider : IAdvancedAnalysisProvider
{
    public string ProviderKey => "disabled";

    public AdvancedAnalysisProviderLocation Location =>
        AdvancedAnalysisProviderLocation.Internal;

    public Task<AdvancedAnalysisProviderResult> ExecuteAsync(
        AdvancedAnalysisProviderRequest request,
        IAdvancedAnalysisToolGateway tools,
        CancellationToken cancellationToken)
        => throw new AdvancedAnalysisProviderException("provider_disabled");
}
