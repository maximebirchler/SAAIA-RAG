using System.Text.Json;

namespace SAAIA.Contracts;

public sealed class AdvancedAnalysisHandoffEnvelope
{
    public const string CurrentSchemaVersion = "saaia.advanced-analysis-handoff.v1";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    public Guid HandoffId { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; }

    public string RequestText { get; init; } = string.Empty;

    public string Language { get; init; } = "fr";

    public string OriginIntent { get; init; } = string.Empty;

    public string ReasonCode { get; init; } = string.Empty;

    public string TransferStage { get; init; } = string.Empty;

    public AdvancedAnalysisLoadDescriptor Load { get; init; } = new();

    public AdvancedAnalysisResearchState ResearchState { get; init; } = new();

    public AdvancedAnalysisLocalBudgetSnapshot? LocalBudget { get; init; }

    public AdvancedAnalysisDataPolicy DataPolicy { get; init; } = new();
}

public sealed class AdvancedAnalysisLoadDescriptor
{
    public string PlanKind { get; init; } = string.Empty;

    public string Deliverable { get; init; } = string.Empty;

    public int AnswerUnitCount { get; init; }

    public int AtomicEvidenceCount { get; init; }

    public int RowCount { get; init; }

    public int ColumnCount { get; init; }

    public bool StructuredLayout { get; init; }

    public string AtomicEvidenceType { get; init; } = string.Empty;

    public string AtomicEvidenceMode { get; init; } = string.Empty;

    public string SelectionPolicy { get; init; } = string.Empty;

    public string QuestionFocus { get; init; } = string.Empty;

    public string RequestedDocumentName { get; init; } = string.Empty;

    public bool BoundedNamedDocumentExtraction { get; init; }

    public List<string> CandidateScopePaths { get; init; } = new();

    public List<string> RowLabels { get; init; } = new();

    public List<string> Columns { get; init; } = new();
}

public sealed class AdvancedAnalysisResearchState
{
    public bool MemoryIsEvidence { get; init; }

    public bool EvidenceRevalidationRequired { get; init; } = true;

    public List<string> ExecutedTools { get; init; } = new();

    public List<string> ExecutedQueries { get; init; } = new();

    public List<AdvancedAnalysisResearchAttempt> Attempts { get; init; } = new();

    public List<AdvancedAnalysisEvidenceReference> EvidenceReferences { get; init; } = new();
}

public sealed class AdvancedAnalysisResearchAttempt
{
    public DateTimeOffset CreatedAtUtc { get; init; }

    public List<string> Queries { get; init; } = new();

    public string? CategoryScope { get; init; }

    public string? DocPath { get; init; }

    public int? PageStart { get; init; }

    public int? PageEnd { get; init; }

    public string Outcome { get; init; } = string.Empty;

    public bool Accepted { get; init; }

    public string? RejectReason { get; init; }

    public int CandidateCountBefore { get; init; }

    public int? CandidateCountAfter { get; init; }

    public int UsableHitsBefore { get; init; }

    public int? UsableHitsAfter { get; init; }

    public long ElapsedMilliseconds { get; init; }
}

public sealed class AdvancedAnalysisEvidenceReference
{
    public string? EvidenceId { get; init; }

    public string? DocId { get; init; }

    public string? RevisionId { get; init; }

    public string? FileName { get; init; }

    public string? DocPath { get; init; }

    public string? SourceHash { get; init; }

    public int PageStart { get; init; }

    public int PageEnd { get; init; }

    public string? ChunkId { get; init; }

    public string? AnchorId { get; init; }

    public string? ContentCardId { get; init; }
}

public sealed class AdvancedAnalysisLocalBudgetSnapshot
{
    public string StopReason { get; init; } = string.Empty;

    public int MaximumTokens { get; init; }

    public long MaximumElapsedMilliseconds { get; init; }

    public int TerminalReserveTokens { get; init; }

    public long TerminalReserveMilliseconds { get; init; }

    public int ChargedTokens { get; init; }

    public int ReservedTokens { get; init; }

    public int RemainingTokens { get; init; }

    public long RemainingMilliseconds { get; init; }

    public bool NormalBudgetClosed { get; init; }

    public int AdmittedCalls { get; init; }

    public int CompletedCalls { get; init; }

    public int FailedCalls { get; init; }

    public string LastUsageSource { get; init; } = string.Empty;

    public string LastAdmissionReason { get; init; } = string.Empty;

    public string LastCallClass { get; init; } = string.Empty;
}

public sealed class AdvancedAnalysisDataPolicy
{
    public bool ExternalProviderContentAuthorized { get; init; }

    public bool ExternalProviderMetadataAuthorized { get; init; }

    public string AuthorizationSource { get; init; } = "server_policy_required";
}

public sealed class AdvancedAnalysisJobCreateRequest
{
    public string UserId { get; init; } = string.Empty;

    public Guid SessionId { get; init; }

    public AdvancedAnalysisHandoffEnvelope Handoff { get; init; } = new();
}

public sealed class AdvancedAnalysisJobDto
{
    public Guid JobId { get; init; }

    public Guid HandoffId { get; init; }

    public Guid SessionId { get; init; }

    public string Status { get; init; } = "queued";

    public int Revision { get; init; }

    public int AttemptCount { get; init; }

    public bool CancelRequested { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; }

    public DateTimeOffset AvailableAtUtc { get; init; }

    public DateTimeOffset ExpiresAtUtc { get; init; }

    public DateTimeOffset? StartedAtUtc { get; init; }

    public DateTimeOffset? FinishedAtUtc { get; init; }

    public string? ProviderKey { get; init; }

    public string? ProviderModel { get; init; }

    public JsonElement? Result { get; init; }

    public string? LastErrorCode { get; init; }
}

public sealed class AdvancedAnalysisResultEnvelope
{
    public const string CurrentSchemaVersion = "saaia.advanced-analysis-result.v1";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string Outcome { get; init; } = string.Empty;

    public string AnswerText { get; init; } = string.Empty;

    public string ProviderKey { get; init; } = string.Empty;

    public string ProviderModel { get; init; } = string.Empty;

    public int ProviderCallCount { get; init; }

    public int? InputTokens { get; init; }

    public int? OutputTokens { get; init; }

    public int? CachedInputTokens { get; init; }

    public decimal? EstimatedCostUsd { get; init; }

    public DateTimeOffset CompletedAtUtc { get; init; }

    public long ElapsedMilliseconds { get; init; }

    public List<AdvancedAnalysisResultEvidence> Evidence { get; init; } = new();

    public List<AdvancedAnalysisResultClaim> Claims { get; init; } = new();
}

public sealed class AdvancedAnalysisResultEvidence
{
    public string EvidenceId { get; init; } = string.Empty;

    public string DocId { get; init; } = string.Empty;

    public string RevisionId { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;

    public string DocPath { get; init; } = string.Empty;

    public string SourceHash { get; init; } = string.Empty;

    public int PageStart { get; init; }

    public int PageEnd { get; init; }

    public string? ChunkId { get; init; }

    public string? AnchorId { get; init; }

    public string? ContentCardId { get; init; }
}

public sealed class AdvancedAnalysisResultClaim
{
    public string ClaimId { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;

    public List<string> EvidenceIds { get; init; } = new();
}
