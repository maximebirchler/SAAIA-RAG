namespace SAAIA.Backend;

internal sealed record RuntimeOperationResult<T>(
    T? Payload,
    string? Error);

internal sealed record CapabilityBCompletionResult(
    Guid JobId,
    Guid DocId,
    string DocPath,
    string Level,
    string DocLanguage,
    string DocLanguageSource,
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
    int IndexedVersion,
    string? ProfileLanguage = null,
    string? SourceHash = null,
    Guid? RevisionId = null,
    CapabilityBExtractionQualitySnapshot? ExtractionQuality = null);

internal sealed record CapabilityBExtractionQualitySnapshot(
    string Status,
    string TextStatus,
    double? ExtractionConfidence,
    bool ManualReviewRecommended,
    bool OcrAttempted,
    bool OcrApplied,
    bool OcrRecommended,
    string? OcrFailureReason,
    int? PageCount,
    int? TextPageCount,
    int? EmptyPageCount,
    int? SparsePageCount,
    int? TotalWordCount,
    int? TotalCharCount,
    double? TextPageRatio,
    IReadOnlyList<string> Signals,
    string Source)
{
    internal bool RequiresCaution =>
        ManualReviewRecommended
        || ContainsQualityFlag(Status, "manual_review")
        || ContainsQualityFlag(Status, "low_text")
        || ContainsQualityFlag(Status, "ocr_failed")
        || string.Equals(TextStatus, "empty_text", StringComparison.OrdinalIgnoreCase)
        || string.Equals(TextStatus, "low_text", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrWhiteSpace(OcrFailureReason)
        || ExtractionConfidence is < 0.6d;

    private static bool ContainsQualityFlag(string? value, string flag)
        => !string.IsNullOrWhiteSpace(value)
           && value.Contains(flag, StringComparison.OrdinalIgnoreCase);
}

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
    int? PriorityScore,
    string PayloadJson);
