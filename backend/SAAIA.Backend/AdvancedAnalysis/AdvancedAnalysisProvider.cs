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
    string? ExactTitle = null,
    AdvancedAnalysisSourceOverview? SourceOverview = null,
    AdvancedAnalysisEvidenceStructure? SourceStructure = null,
    [property: System.Text.Json.Serialization.JsonIgnore] AdvancedAnalysisTableFragment? TableFragment = null,
    [property: System.Text.Json.Serialization.JsonIgnore] AdvancedAnalysisSearchRequest? TableReadWindow = null)
{
    public AdvancedAnalysisSourceQuality? SourceQuality { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AdvancedAnalysisTableObservationLineSource? SourceObservation { get; init; }
}

internal sealed record AdvancedAnalysisEvidenceStructure(
    string? SectionTitle,
    string? HeadingPath,
    string? PreviousChunkId,
    string? NextChunkId)
{
    public string? ChunkType { get; init; }
    public string? ChunkComposition { get; init; }
    public bool ContainsUnassignedPhysicalContext { get; init; }

    internal bool HasUnassignedPhysicalContext => ContainsUnassignedPhysicalContext
        || string.Equals(ChunkType, RetrievalChunkProjector.PhysicalPageContextChunkType, StringComparison.Ordinal);
}

internal sealed record AdvancedAnalysisSourceOverview(
    int FirstIndexedPhysicalPage,
    int LastIndexedPhysicalPage,
    int CanonicalChunkCount)
{
    public AdvancedAnalysisVisualAvailability? VisualAssets { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public CanonicalTableObservationInventory? TableRegionObservations { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AdvancedAnalysisVisualSources? VisualSources { get; init; }
}

internal sealed record AdvancedAnalysisVisualAvailability(string Status, long AvailableAssetCount,
    IReadOnlyList<int> PhysicalPages, bool AssetsOmitted, string InterpretationStatus, bool ImageToolAvailable);

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

    public string SelectionMode { get; init; } = string.Empty;

    public int? InputTokens { get; init; }

    public int? OutputTokens { get; init; }

    public int? CachedInputTokens { get; init; }

    public int? CacheWriteTokens { get; init; }

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

internal sealed record AdvancedAnalysisResearchArgumentFeedback(
    string ReasonCode, string Operation, string SourceKey,
    int? RequestedPageStart, int? RequestedPageEnd, int MaximumInclusivePages,
    AdvancedAnalysisToolBudget? ToolBudget = null, int? RequestedNewOperations = null);

internal sealed class AdvancedAnalysisProviderException : Exception
{
    public string ErrorCode { get; }

    public bool IsRetryable { get; }

    public long? RetryAfterMilliseconds { get; }

    public AdvancedAnalysisResearchArgumentFeedback? ResearchArgumentFeedback { get; }

    public AdvancedAnalysisProviderException(
        string errorCode,
        bool isRetryable = false,
        long? retryAfterMilliseconds = null,
        AdvancedAnalysisResearchArgumentFeedback? researchArgumentFeedback = null)
        : base(errorCode)
    {
        ErrorCode = errorCode;
        ResearchArgumentFeedback = researchArgumentFeedback;
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
