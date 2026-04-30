namespace SAAIA.Backend;

internal sealed record RuntimeOperationResult<T>(
    T? Payload,
    string? Error);

internal sealed record CapabilityBCompletionResult(
    Guid JobId,
    Guid DocId,
    string DocPath,
    string Level,
    string SourceHash,
    int SummaryLength,
    string CompletedBy,
    Guid? CampaignId);

internal sealed record CapabilityBExecutionContext(
    Guid JobId,
    Guid DocId,
    string DocPath,
    string Level,
    string Status,
    string ExecutionMode,
    string RuntimeCapabilityKey,
    string? RuntimeCapabilityStatus,
    bool? RuntimeCapabilitySelected,
    string? RuntimeProfileKey,
    Guid? CampaignId,
    string? EnqueueSource,
    string LeaseToken,
    string ClaimedBy,
    DateTimeOffset? ClaimedAt);

internal sealed record CapabilityBDocumentRow(
    Guid DocId,
    string DocPath,
    string DocName,
    string? Category,
    int? PageCount,
    int IndexedVersion);

internal sealed record CapabilityBExecutionJobRow(
    Guid JobId,
    Guid? DocId,
    string? DocPath,
    string? Level,
    string Status,
    string? ExecutionMode,
    string? RuntimeCapabilityKey,
    string? RuntimeCapabilityStatus,
    bool? RuntimeCapabilitySelected,
    string? RuntimeProfileKey,
    string? EnqueueSource,
    string? ExecutionLeaseToken,
    string? ExecutionClaimedBy,
    DateTime? ExecutionClaimedAt,
    Guid? CampaignId,
    string PayloadJson);
