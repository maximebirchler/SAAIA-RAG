using System.Text.Json;

namespace SAAIA.Backend.Models;

public sealed record AdminRuntimeCatalogResponseDto(
    string CdcAlignment,
    string Environment,
    IReadOnlyList<AdminRuntimeCatalogRuntimeDto> Runtimes,
    IReadOnlyList<AdminRuntimeWarmupProfileDto> WarmupProfiles,
    IReadOnlyList<AdminRuntimeCapabilityCatalogDto> Capabilities
);

public sealed record AdminRuntimeCatalogRuntimeDto(
    string Key,
    string Label,
    string Kind,
    bool Enabled,
    string? BaseUrl = null,
    string? Model = null,
    string? ReadinessStatus = null,
    string? ConfigurationSource = null,
    IReadOnlyList<string>? UsedByProfileKeys = null,
    IReadOnlyList<string>? RequiredByProfileKeys = null,
    IReadOnlyList<string>? ExpectedCapabilityKeys = null,
    IReadOnlyList<string>? DependencyRuntimeKeys = null,
    IReadOnlyList<string>? RequiredSettingKeys = null,
    IReadOnlyList<string>? MissingSettingKeys = null
);

public sealed record AdminRuntimeWarmupProfileDto(
    string Key,
    string Label,
    int PassCount,
    IReadOnlyList<string> Checks,
    AdminRuntimeHardwareRequirementsDto? HardwareRequirements = null,
    AdminRuntimeWarmupPerformanceBudgetsDto? PerformanceBudgets = null,
    AdminRuntimeWarmupCheckPolicyDto? CheckPolicy = null,
    AdminRuntimeWarmupRuntimeRequirementsDto? RuntimeRequirements = null,
    AdminRuntimeWarmupFreshnessPolicyDto? FreshnessPolicy = null
);

public sealed record AdminRuntimeHardwareRequirementsDto(
    int MinCpuCores,
    long MinAvailableMemoryMb,
    bool Require64BitProcess
);

public sealed record AdminRuntimeWarmupPerformanceBudgetsDto(
    long? MaxPassDurationMs,
    long? MaxQdrantCheckMs,
    long? MaxEmbeddingsCheckMs,
    long? MaxRerankCheckMs
);

public sealed record AdminRuntimeWarmupCheckPolicyDto(
    bool RequireRerankEnabled,
    bool AllowSkippedChecks,
    bool EnforcePerformanceBudgets
);

public sealed record AdminRuntimeWarmupRuntimeRequirementsDto(
    bool RequireQdrant,
    bool RequireEmbeddings,
    bool RequireRerank,
    bool RequireCollection,
    bool RequireEmbeddingsModel,
    bool RequireRerankModel
);

public sealed record AdminRuntimeWarmupFreshnessPolicyDto(
    long? MaxQualificationAgeHours,
    bool RequireRequalificationWhenExpired
);

public sealed record AdminRuntimeCapabilityCatalogDto(
    string Key,
    string DisplayName,
    string Family,
    string RuntimeKey,
    bool Implemented,
    bool DefaultDesiredEnabled,
    string StatusNote
);

public sealed record AdminRuntimeCapabilitiesResponseDto(
    string CdcAlignment,
    string Environment,
    IReadOnlyList<AdminRuntimeCapabilityStateDto> Items,
    IReadOnlyList<AdminRuntimeWarmupResultDto> WarmupResults
);

public sealed record AdminRuntimeCapabilityStateDto(
    string Key,
    string DisplayName,
    string Family,
    string RuntimeKey,
    bool Implemented,
    bool DesiredEnabled,
    bool Installed,
    bool Configured,
    bool Healthy,
    bool Qualified,
    bool Authorized,
    bool Selected,
    string ProfileKey,
    int PassCount,
    DateTimeOffset? LastCheckedAt = null,
    DateTimeOffset? LastQualifiedAt = null,
    string? LastError = null,
    IReadOnlyDictionary<string, object?>? Details = null,
    bool Stale = false,
    string? QualificationFingerprint = null,
    string? StaleReason = null,
    double? QualificationAgeHours = null,
    DateTimeOffset? QualificationExpiresAt = null,
    bool PersistedAuthorized = false,
    bool PersistedSelected = false,
    bool EffectiveAuthorized = false,
    bool EffectiveSelected = false
);

public sealed record AdminRuntimeWarmupResultDto(
    Guid WarmupResultId,
    string CapabilityKey,
    string ProfileKey,
    int PassCount,
    bool Passed,
    DateTimeOffset MeasuredAt,
    IReadOnlyDictionary<string, object?>? Details = null
);

public sealed record AdminRuntimeRequalifyRequestDto(
    string? CapabilityKey = null,
    string? ProfileKey = null,
    bool? SelectWhenQualified = null
);

public sealed record AdminRuntimeRequalifyResponseDto(
    string CdcAlignment,
    string Environment,
    string ProfileKey,
    IReadOnlyList<AdminRuntimeCapabilityStateDto> Items,
    IReadOnlyList<AdminRuntimeWarmupResultDto> WarmupResults
);

public sealed record AdminRuntimeReconcileStaleRequestDto(
    string? CapabilityKey = null
);

public sealed record AdminRuntimeReconcileStaleResponseDto(
    string CdcAlignment,
    string Environment,
    int UpdatedCount,
    IReadOnlyList<AdminRuntimeCapabilityStateDto> Items
);

public sealed record AdminRuntimeCapabilitySelectionRequestDto(
    bool? DesiredEnabled = null,
    bool? Authorized = null,
    bool? Selected = null
);

public sealed record AdminRuntimeWarmupResultsResponseDto(
    string CdcAlignment,
    string Environment,
    IReadOnlyList<AdminRuntimeWarmupResultDto> Items
);

public sealed record AdminRuntimeEventsResponseDto(
    string CdcAlignment,
    string Environment,
    IReadOnlyList<AdminRuntimeCapabilityEventDto> Items
);

public sealed record AdminRuntimeCapabilityAEnrichmentCandidatesResponseDto(
    string CdcAlignment,
    string Environment,
    string CapabilityKey,
    string ProfileKey,
    int TotalCandidates,
    IReadOnlyList<AdminRuntimeCapabilityAEnrichmentCandidateDto> Items
);

public sealed record AdminRuntimeCapabilityAEnrichmentCandidatesArtifactDto(
    string Artifact,
    string CdcAlignment,
    string Environment,
    DateTimeOffset GeneratedAt,
    string CapabilityKey,
    string ProfileKey,
    int TotalCandidates,
    IReadOnlyList<AdminRuntimeCapabilityAEnrichmentCandidateDto> Items
);

public sealed record AdminRuntimeCapabilityACampaignsResponseDto(
    string CdcAlignment,
    string Environment,
    string CapabilityKey,
    IReadOnlyList<AdminRuntimeCapabilityACampaignDto> Items
);

public sealed record AdminRuntimeCapabilityACampaignsArtifactDto(
    string Artifact,
    string CdcAlignment,
    string Environment,
    DateTimeOffset GeneratedAt,
    string CapabilityKey,
    IReadOnlyList<AdminRuntimeCapabilityACampaignDto> Items
);

public sealed record AdminRuntimeCapabilityACampaignDetailResponseDto(
    string CdcAlignment,
    string Environment,
    string CapabilityKey,
    AdminRuntimeCapabilityACampaignDetailDto Item
);

public sealed record AdminRuntimeCapabilityACampaignDetailArtifactDto(
    string Artifact,
    string CdcAlignment,
    string Environment,
    DateTimeOffset GeneratedAt,
    string CapabilityKey,
    AdminRuntimeCapabilityACampaignDetailDto Item
);

public sealed record AdminRuntimeCapabilityBBackofficeCandidatesResponseDto(
    string CdcAlignment,
    string Environment,
    string CapabilityKey,
    string ProfileKey,
    int TotalCandidates,
    IReadOnlyList<AdminRuntimeCapabilityBBackofficeCandidateDto> Items
);

public sealed record AdminRuntimeCapabilityBBackofficeCandidatesArtifactDto(
    string Artifact,
    string CdcAlignment,
    string Environment,
    DateTimeOffset GeneratedAt,
    string CapabilityKey,
    string ProfileKey,
    int TotalCandidates,
    IReadOnlyList<AdminRuntimeCapabilityBBackofficeCandidateDto> Items
);

public sealed record AdminRuntimeCapabilityBCampaignsResponseDto(
    string CdcAlignment,
    string Environment,
    string CapabilityKey,
    IReadOnlyList<AdminRuntimeCapabilityBCampaignDto> Items
);

public sealed record AdminRuntimeCapabilityBCampaignsArtifactDto(
    string Artifact,
    string CdcAlignment,
    string Environment,
    DateTimeOffset GeneratedAt,
    string CapabilityKey,
    IReadOnlyList<AdminRuntimeCapabilityBCampaignDto> Items
);

public sealed record AdminRuntimeCapabilityBCampaignDetailResponseDto(
    string CdcAlignment,
    string Environment,
    string CapabilityKey,
    AdminRuntimeCapabilityBCampaignDetailDto Item
);

public sealed record AdminRuntimeCapabilityBCampaignDetailArtifactDto(
    string Artifact,
    string CdcAlignment,
    string Environment,
    DateTimeOffset GeneratedAt,
    string CapabilityKey,
    AdminRuntimeCapabilityBCampaignDetailDto Item
);

public sealed record AdminRuntimeCapabilityBQualityReviewResponseDto(
    string CdcAlignment,
    string Environment,
    DateTimeOffset GeneratedAt,
    string CapabilityKey,
    double QualityThreshold,
    int TotalItems,
    IReadOnlyList<AdminRuntimeCapabilityBQualityReviewItemDto> Items
);

public sealed record AdminRuntimeCapabilityBQualityReviewArtifactDto(
    string Artifact,
    string CdcAlignment,
    string Environment,
    DateTimeOffset GeneratedAt,
    string CapabilityKey,
    double QualityThreshold,
    int TotalItems,
    IReadOnlyList<AdminRuntimeCapabilityBQualityReviewItemDto> Items
);

public sealed record AdminRuntimeCapabilityBQualityReviewSummaryResponseDto(
    string CdcAlignment,
    string Environment,
    DateTimeOffset GeneratedAt,
    string CapabilityKey,
    double QualityThreshold,
    AdminRuntimeCapabilityBQualityReviewSummaryDto Summary
);

public sealed record AdminRuntimeCapabilityBQualityReviewSummaryArtifactDto(
    string Artifact,
    string CdcAlignment,
    string Environment,
    DateTimeOffset GeneratedAt,
    string CapabilityKey,
    double QualityThreshold,
    AdminRuntimeCapabilityBQualityReviewSummaryDto Summary
);

public sealed record AdminRuntimeCapabilityAEnqueueRequestDto(
    IReadOnlyList<Guid>? DocIds = null,
    IReadOnlyList<string>? DocPaths = null,
    string? Category = null,
    IReadOnlyList<string>? ReasonFilters = null,
    int? MaxCandidates = null,
    bool DryRun = false,
    bool AllowUnsafeCandidates = false
);

public sealed record AdminRuntimeCapabilityAEnqueueResponseDto(
    string CdcAlignment,
    string Environment,
    string CapabilityKey,
    Guid CampaignId,
    bool DryRun,
    bool AllowUnsafeCandidates,
    int CandidateCount,
    int PlannedCount,
    int QueuedCount,
    int SkippedCount,
    IReadOnlyDictionary<string, int> ReasonCounts,
    IReadOnlyList<AdminRuntimeCapabilityAEnqueueItemDto> Items
);

public sealed record AdminRuntimeCapabilityBEnqueueRequestDto(
    IReadOnlyList<Guid>? DocIds = null,
    IReadOnlyList<string>? DocPaths = null,
    string? Category = null,
    int? MaxCandidates = null,
    bool DryRun = false,
    bool Force = false
);

public sealed record AdminRuntimeCapabilityBEnqueueResponseDto(
    string CdcAlignment,
    string Environment,
    string CapabilityKey,
    Guid CampaignId,
    bool DryRun,
    bool Force,
    int CandidateCount,
    int PlannedCount,
    int QueuedCount,
    int SkippedCount,
    IReadOnlyDictionary<string, int> ReasonCounts,
    IReadOnlyList<AdminRuntimeCapabilityBEnqueueItemDto> Items
);

public sealed record AdminRuntimeCapabilityBClaimRequestDto(
    Guid? JobId = null,
    string? ExecutorId = null
);

public sealed record AdminRuntimeCapabilityBClaimResponseDto(
    string CdcAlignment,
    string Environment,
    string CapabilityKey,
    Guid JobId,
    Guid DocId,
    string DocPath,
    string Level,
    string ExecutionMode,
    string RuntimeCapabilityKey,
    string? RuntimeCapabilityStatus,
    string? RuntimeProfileKey,
    Guid? CampaignId,
    string LeaseToken,
    string ClaimedBy,
    DateTimeOffset ClaimedAt
);

public sealed record AdminRuntimeCapabilityBCompleteRequestDto(
    Guid JobId,
    string LeaseToken,
    string SummaryText,
    string? DocLanguage = null,
    string? SourceHash = null,
    JsonElement? Meta = null
);

public sealed record AdminRuntimeCapabilityBFailRequestDto(
    Guid JobId,
    string LeaseToken,
    string Error,
    JsonElement? Details = null
);

public sealed record AdminRuntimeCapabilityBFailResponseDto(
    string CdcAlignment,
    string Environment,
    string CapabilityKey,
    Guid JobId,
    Guid DocId,
    string DocPath,
    string Level,
    Guid? CampaignId,
    string LeaseToken,
    string FailedBy,
    string LastError,
    string Status,
    DateTimeOffset FailedAt
);

public sealed record AdminRuntimeRuntimeCatalogArtifactDto(
    string Artifact,
    string CdcAlignment,
    string Environment,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<AdminRuntimeCatalogRuntimeDto> Runtimes,
    IReadOnlyList<AdminRuntimeWarmupProfileDto> WarmupProfiles,
    IReadOnlyList<AdminRuntimeCapabilityCatalogDto> Capabilities
);

public sealed record AdminRuntimeModelCatalogEntryDto(
    string Key,
    string Label,
    string RuntimeKey,
    string Kind,
    bool Enabled,
    bool Implemented,
    string? BaseUrl = null,
    string? Model = null,
    string? StatusNote = null,
    string? ReadinessStatus = null,
    string? ConfigurationSource = null,
    IReadOnlyList<string>? UsedByProfileKeys = null,
    IReadOnlyList<string>? RequiredByProfileKeys = null,
    IReadOnlyList<string>? ExpectedCapabilityKeys = null,
    IReadOnlyList<string>? DependencyRuntimeKeys = null,
    IReadOnlyList<string>? RequiredSettingKeys = null,
    IReadOnlyList<string>? MissingSettingKeys = null
);

public sealed record AdminRuntimeModelCatalogArtifactDto(
    string Artifact,
    string CdcAlignment,
    string Environment,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<AdminRuntimeModelCatalogEntryDto> Items
);

public sealed record AdminRuntimeCapabilityStateArtifactDto(
    string Artifact,
    string CdcAlignment,
    string Environment,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<AdminRuntimeCapabilityStateDto> Items
);

public sealed record AdminRuntimeWarmupProfilesArtifactDto(
    string Artifact,
    string CdcAlignment,
    string Environment,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<AdminRuntimeWarmupProfileDto> Items
);

public sealed record AdminRuntimeWarmupResultsArtifactDto(
    string Artifact,
    string CdcAlignment,
    string Environment,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<AdminRuntimeWarmupResultDto> Items
);

public sealed record AdminRuntimeEventsArtifactDto(
    string Artifact,
    string CdcAlignment,
    string Environment,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<AdminRuntimeCapabilityEventDto> Items
);

public sealed record AdminRuntimeDiagnosticsResponseDto(
    string CdcAlignment,
    string Environment,
    DateTimeOffset GeneratedAt,
    AdminRuntimeDiagnosticsSummaryDto Summary,
    IReadOnlyList<AdminRuntimeCapabilityDiagnosticDto> Items
);

public sealed record AdminRuntimeDiagnosticsArtifactDto(
    string Artifact,
    string CdcAlignment,
    string Environment,
    DateTimeOffset GeneratedAt,
    AdminRuntimeDiagnosticsSummaryDto Summary,
    IReadOnlyList<AdminRuntimeCapabilityDiagnosticDto> Items
);

public sealed record AdminRuntimeOperationalSummaryResponseDto(
    string CdcAlignment,
    string Environment,
    DateTimeOffset GeneratedAt,
    AdminRuntimeDiagnosticsOperationalSummaryDto Summary,
    IReadOnlyList<AdminRuntimeOperationalCapabilitySummaryDto> Items
);

public sealed record AdminRuntimeOperationalSummaryArtifactDto(
    string Artifact,
    string CdcAlignment,
    string Environment,
    DateTimeOffset GeneratedAt,
    AdminRuntimeDiagnosticsOperationalSummaryDto Summary,
    IReadOnlyList<AdminRuntimeOperationalCapabilitySummaryDto> Items
);

public sealed record AdminRuntimeLlmCapacityResponseDto(
    string CdcAlignment,
    string Environment,
    DateTimeOffset GeneratedAt,
    string Status,
    string? Path,
    string? Error,
    AdminRuntimeLlmCapacityPlanDto? Plan,
    AdminRuntimeLlmQueueSnapshotDto Queue
);

public sealed record AdminRuntimeLlmCapacityArtifactDto(
    string Artifact,
    string CdcAlignment,
    string Environment,
    DateTimeOffset GeneratedAt,
    string Status,
    string? Path,
    string? Error,
    AdminRuntimeLlmCapacityPlanDto? Plan,
    AdminRuntimeLlmQueueSnapshotDto Queue
);

public sealed record AdminRuntimeLlmCapacityPlanDto(
    string? Version,
    string? GeneratedAt,
    int LicenseSeats,
    string? Profile,
    string? ModelRepo,
    string? ModelFile,
    string? ModelLabel,
    string? Placement,
    int Instances,
    int SlotsPerInstance,
    int TotalSlots,
    int QueueLimit,
    int PerUserActiveLimit,
    int PerUserQueuedLimit,
    string? Notes,
    AdminRuntimeLlmCapacityHardwareDto? Hardware,
    AdminRuntimeLlmCapacityLlamaArgsDto? LlamaArgs
);

public sealed record AdminRuntimeLlmCapacityHardwareDto(
    int CpuCount,
    int TotalRamMiB,
    string? GpuName,
    int GpuVramMiB
);

public sealed record AdminRuntimeLlmCapacityLlamaArgsDto(
    int CtxSize,
    int BatchSize,
    int UBatchSize,
    int GpuLayers
);

public sealed record AdminRuntimeLlmQueueSnapshotDto(
    int Active,
    int Queued,
    int TotalSlots,
    int QueueLimit,
    int PerUserActiveLimit,
    int PerUserQueuedLimit,
    int AvailableSlots
);

public sealed record AdminRuntimeRetrievalKpisResponseDto(
    string CdcAlignment,
    string Environment,
    DateTimeOffset GeneratedAt,
    AdminRuntimeRetrievalKpiPolicyDto Policy,
    IReadOnlyList<AdminRuntimeMetricDefinitionDto> Metrics,
    IReadOnlyList<AdminRuntimeAlertDefinitionDto> Alerts,
    IReadOnlyList<string> DashboardPanels
);

public sealed record AdminRuntimeRetrievalKpisArtifactDto(
    string Artifact,
    string CdcAlignment,
    string Environment,
    DateTimeOffset GeneratedAt,
    AdminRuntimeRetrievalKpiPolicyDto Policy,
    IReadOnlyList<AdminRuntimeMetricDefinitionDto> Metrics,
    IReadOnlyList<AdminRuntimeAlertDefinitionDto> Alerts,
    IReadOnlyList<string> DashboardPanels
);

public sealed record AdminRuntimeCapabilityBKpisResponseDto(
    string CdcAlignment,
    string Environment,
    DateTimeOffset GeneratedAt,
    AdminRuntimeCapabilityBKpiPolicyDto Policy,
    IReadOnlyList<AdminRuntimeMetricDefinitionDto> Metrics,
    IReadOnlyList<AdminRuntimeAlertDefinitionDto> Alerts,
    IReadOnlyList<string> DashboardPanels
);

public sealed record AdminRuntimeCapabilityAKpisResponseDto(
    string CdcAlignment,
    string Environment,
    DateTimeOffset GeneratedAt,
    AdminRuntimeCapabilityAKpiPolicyDto Policy,
    IReadOnlyList<AdminRuntimeMetricDefinitionDto> Metrics,
    IReadOnlyList<AdminRuntimeAlertDefinitionDto> Alerts,
    IReadOnlyList<string> DashboardPanels
);

public sealed record AdminRuntimeCapabilityAKpisArtifactDto(
    string Artifact,
    string CdcAlignment,
    string Environment,
    DateTimeOffset GeneratedAt,
    AdminRuntimeCapabilityAKpiPolicyDto Policy,
    IReadOnlyList<AdminRuntimeMetricDefinitionDto> Metrics,
    IReadOnlyList<AdminRuntimeAlertDefinitionDto> Alerts,
    IReadOnlyList<string> DashboardPanels
);

public sealed record AdminRuntimeCapabilityBKpisArtifactDto(
    string Artifact,
    string CdcAlignment,
    string Environment,
    DateTimeOffset GeneratedAt,
    AdminRuntimeCapabilityBKpiPolicyDto Policy,
    IReadOnlyList<AdminRuntimeMetricDefinitionDto> Metrics,
    IReadOnlyList<AdminRuntimeAlertDefinitionDto> Alerts,
    IReadOnlyList<string> DashboardPanels
);

public sealed record AdminRuntimeRetrievalKpiPolicyDto(
    int ObservationWindowMinutes,
    double RetrievalP95TargetMs,
    double RerankP95TargetMs,
    double ZeroResultRateTargetPercent,
    string ZeroResultRateFormula,
    string Notes
);

public sealed record AdminRuntimeCapabilityAKpiPolicyDto(
    int ObservationWindowMinutes,
    double OperationP95TargetMs,
    double SkipRateTargetPercent,
    double ReadyToEnqueueRateTargetPercent,
    double OffsetBackfillShareTargetPercent,
    string SkipRateFormula,
    string ReadyToEnqueueRateFormula,
    string OffsetBackfillShareFormula,
    string Notes
);

public sealed record AdminRuntimeCapabilityBKpiPolicyDto(
    int ObservationWindowMinutes,
    double GenerationP95TargetMs,
    double LiveFallbackRateTargetPercent,
    double FailureRateTargetPercent,
    double QualityScoreTarget,
    string LiveFallbackRateFormula,
    string FailureRateFormula,
    string Notes
);

public sealed record AdminRuntimeMetricDefinitionDto(
    string Key,
    string Instrument,
    string Aggregation,
    string Unit,
    string Description,
    IReadOnlyList<string>? Tags = null
);

public sealed record AdminRuntimeAlertDefinitionDto(
    string Key,
    string Severity,
    string Condition,
    string RecommendedAction
);

public sealed record AdminRuntimeDiagnosticsSummaryDto(
    int TotalCapabilities,
    int ImplementedCapabilities,
    int QualifiedCapabilities,
    int SelectedCapabilities,
    int PersistedSelectedCapabilities,
    int BlockedCapabilities,
    int StaleCapabilities,
    AdminRuntimeDiagnosticsOperationalSummaryDto? Operational = null
);

public sealed record AdminRuntimeDiagnosticsOperationalSummaryDto(
    int CapabilityACandidateCount,
    int CapabilityAReadyToEnqueueCount,
    int CapabilityAOffsetBackfillCandidateCount,
    int CapabilityBBacklogCount,
    int CapabilityBReadyToEnqueueCount,
    int CapabilityBActiveJobCount,
    int? CapabilityBLatestCampaignProgressPercent = null
);

public sealed record AdminRuntimeCapabilityOperationalSummaryDto(
    int CandidateCount,
    int ReadyToEnqueueCount,
    int BlockedByActiveJobCount,
    int BlockedByCooldownCount,
    int ActiveCapabilityJobCount,
    int TotalCampaignCount,
    int ActiveCampaignCount,
    int TerminalCapabilityJobCount,
    int StoredSummaryCount,
    IReadOnlyDictionary<string, int>? ReasonCounts = null,
    int? OffsetBackfillCandidateCount = null,
    int? LatestCampaignProgressPercent = null,
    Guid? LatestCampaignId = null,
    string? LatestCampaignStatus = null,
    DateTimeOffset? LatestCampaignOccurredAt = null
);

public sealed record AdminRuntimeOperationalCapabilitySummaryDto(
    string Key,
    string DisplayName,
    string Family,
    string Status,
    bool Implemented,
    bool Qualified,
    bool Selected,
    bool Stale,
    AdminRuntimeCapabilityOperationalSummaryDto Summary,
    IReadOnlyList<string> Recommendations
);

public sealed record AdminRuntimeCapabilityDiagnosticDto(
    string Key,
    string DisplayName,
    string Family,
    string Status,
    string ProfileKey,
    bool Implemented,
    bool Stale,
    bool Qualified,
    bool Authorized,
    bool Selected,
    bool PersistedAuthorized,
    bool PersistedSelected,
    string? StaleReason,
    double? QualificationAgeHours,
    DateTimeOffset? QualificationExpiresAt,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Recommendations,
    DateTimeOffset? LastCheckedAt = null,
    DateTimeOffset? LastQualifiedAt = null,
    string? LastError = null,
    AdminRuntimeCapabilityOperationalSummaryDto? OperationalSummary = null
);

public sealed record AdminRuntimeCapabilityEventDto(
    Guid EventId,
    string CapabilityKey,
    string? ProfileKey,
    string EventType,
    string Actor,
    string? Reason,
    DateTimeOffset OccurredAt,
    IReadOnlyDictionary<string, object?>? Details = null
);

public sealed record AdminRuntimeCapabilityAEnrichmentCandidateDto(
    Guid DocId,
    string DocPath,
    string DocName,
    string Category,
    string Status,
    int IngestionVersion,
    int IndexedVersion,
    int PriorityScore,
    bool FileExists,
    string? RecommendedAction,
    IReadOnlyList<string> Reasons,
    string? PreviewText = null,
    IReadOnlyList<string>? KeySectionTitles = null,
    IReadOnlyList<string>? SuggestedTags = null,
    IReadOnlyList<string>? HypotheticalQuestions = null,
    double? QualityScore = null,
    AdminRuntimeCapabilityAQualitySignalsDto? QualitySignals = null
);

public sealed record AdminRuntimeCapabilityAQualitySignalsDto(
    int SectionTitleCount,
    int ExcerptCount,
    int SuggestedTagCount,
    int HypotheticalQuestionCount,
    double SectionCoverageScore,
    double TagScore,
    double QuestionScore,
    double PreviewScore
);

public sealed record AdminRuntimeCapabilityBBackofficeCandidateDto(
    Guid DocId,
    string DocPath,
    string DocName,
    string Category,
    string SummaryState,
    bool HasActiveJob,
    string? RecommendedAction,
    IReadOnlyList<string> Reasons,
    int PriorityScore = 0,
    bool PolicyBlocked = false,
    string? PolicyBlockReason = null,
    string? LastJobStatus = null,
    DateTimeOffset? LastJobFinishedAt = null,
    string? LastJobError = null
);

public sealed record AdminRuntimeCapabilityAEnqueueItemDto(
    Guid? DocId,
    string? DocPath,
    bool Queued,
    Guid? JobId = null,
    string? Reason = null,
    string? PreviewText = null,
    IReadOnlyList<string>? KeySectionTitles = null,
    IReadOnlyList<string>? SuggestedTags = null,
    IReadOnlyList<string>? HypotheticalQuestions = null,
    double? QualityScore = null,
    AdminRuntimeCapabilityAQualitySignalsDto? QualitySignals = null
);

public sealed record AdminRuntimeCapabilityBEnqueueItemDto(
    Guid? DocId,
    string? DocPath,
    bool Queued,
    Guid? JobId = null,
    string? Reason = null,
    string? JobStatus = null,
    bool? JobResultStored = null,
    DateTimeOffset? JobFinishedAt = null,
    string? StoredSummaryFreshness = null
);

public sealed record AdminRuntimeCapabilityBQualitySignalsDto(
    int? LineCount,
    double? LengthScore,
    double? StructureScore,
    double? SectionCoverageScore,
    int? MatchedSectionCount,
    int? ExpectedSectionCount,
    double? KeywordCoverageScore,
    int? MatchedKeywordCount,
    int? ExpectedKeywordCount
);

public sealed record AdminRuntimeCapabilityBQualityReviewItemDto(
    Guid DocId,
    string DocPath,
    string DocName,
    string? Category,
    string Level,
    double QualityScore,
    string Severity,
    string RecommendedAction,
    string? Strategy,
    bool FallbackUsed,
    string? FallbackReason,
    string? RuntimeCapabilityStatus,
    int SummaryLength,
    DateTimeOffset UpdatedAt,
    AdminRuntimeCapabilityBQualitySignalsDto Signals,
    IReadOnlyList<string> Recommendations
);

public sealed record AdminRuntimeNamedCountDto(
    string Key,
    int Count
);

public sealed record AdminRuntimeCapabilityBQualityReviewSummaryDto(
    int TotalLowQualitySummaries,
    int FallbackSummaryCount,
    int LiveLlmSummaryCount,
    int RuntimeUnavailableCount,
    double? LowestQualityScore,
    DateTimeOffset? LatestUpdatedAt,
    IReadOnlyList<AdminRuntimeNamedCountDto> StrategyCounts,
    IReadOnlyList<AdminRuntimeNamedCountDto> RuntimeStatusCounts,
    IReadOnlyList<string> Recommendations
);

public sealed record AdminRuntimeCapabilityACampaignDto(
    Guid CampaignId,
    string CapabilityKey,
    string? ProfileKey,
    string Status,
    bool DryRun,
    bool AllowUnsafeCandidates,
    int CandidateCount,
    int PlannedCount,
    int QueuedCount,
    int SkippedCount,
    IReadOnlyDictionary<string, int> ReasonCounts,
    DateTimeOffset OccurredAt
);

public sealed record AdminRuntimeCapabilityACampaignDetailDto(
    Guid CampaignId,
    string CapabilityKey,
    string? ProfileKey,
    string Status,
    bool DryRun,
    bool AllowUnsafeCandidates,
    int CandidateCount,
    int PlannedCount,
    int QueuedCount,
    int SkippedCount,
    IReadOnlyDictionary<string, int> ReasonCounts,
    DateTimeOffset OccurredAt,
    IReadOnlyList<AdminRuntimeCapabilityAEnqueueItemDto> Items
);

public sealed record AdminRuntimeCapabilityBCampaignDto(
    Guid CampaignId,
    string CapabilityKey,
    string? ProfileKey,
    string Status,
    bool DryRun,
    bool Force,
    int CandidateCount,
    int PlannedCount,
    int QueuedCount,
    int SkippedCount,
    IReadOnlyDictionary<string, int> ReasonCounts,
    IReadOnlyDictionary<string, int> JobStatusCounts,
    int TrackedJobCount,
    int ActiveJobCount,
    int TerminalJobCount,
    int StoredSummaryCount,
    int? ProgressPercent,
    DateTimeOffset OccurredAt
);

public sealed record AdminRuntimeCapabilityBCampaignDetailDto(
    Guid CampaignId,
    string CapabilityKey,
    string? ProfileKey,
    string Status,
    bool DryRun,
    bool Force,
    int CandidateCount,
    int PlannedCount,
    int QueuedCount,
    int SkippedCount,
    IReadOnlyDictionary<string, int> ReasonCounts,
    IReadOnlyDictionary<string, int> JobStatusCounts,
    int TrackedJobCount,
    int ActiveJobCount,
    int TerminalJobCount,
    int StoredSummaryCount,
    int? ProgressPercent,
    DateTimeOffset OccurredAt,
    IReadOnlyList<AdminRuntimeCapabilityBEnqueueItemDto> Items
);
