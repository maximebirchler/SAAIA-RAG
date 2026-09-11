namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed record SourceBackedCatalogHint(
    string CategoryPath,
    string DisplayName,
    int? TotalDocuments,
    IReadOnlyList<string> Aliases,
    bool IsDefaultScope = false);

public sealed record SourceBackedMemorySourceAnchor(
    string? DocId,
    string? DocPath,
    string? DocName,
    int? PageStart,
    int? PageEnd,
    string? SourceHash);

public sealed record SourceBackedMemoryResearchNote(
    string TopicKey,
    string RequestShape,
    IReadOnlyList<string> Queries,
    string Outcome,
    bool Accepted,
    DateTimeOffset CreatedAtUtc);

public sealed record SourceBackedMemoryContext(
    string Profile,
    string PreferredLanguage,
    string PreferredStyle,
    string ActiveMode,
    string? PreviousUserMessage,
    string? PreviousAssistantAnswer,
    string? PreviousIntent,
    string? PreviousAnswerSource,
    string? FocusedDocumentId,
    string? FocusedDocumentPath,
    string? FocusedDocumentName,
    string? ResolvedCategoryPath,
    string? PendingClarificationKind,
    string? PendingClarificationHint,
    IReadOnlyList<SourceBackedMemorySourceAnchor> PreviousSourceAnchors,
    IReadOnlyList<SourceBackedMemoryResearchNote> RecentResearchNotes,
    IReadOnlyList<SourceBackedConversationTurnMemory>? ConversationTurns = null);

public sealed record SourceBackedCanonicalDiversityPolicy(
    IReadOnlyDictionary<string, int> MinimumDistinctValuesPerColumn,
    int MinimumDistinctValuesOverall,
    string Reason);

public sealed record SourceBackedInitialToolCall(
    string Id,
    string ToolName,
    System.Text.Json.JsonElement Arguments,
    string DecisionSource,
    string? QueryHint = null);

public sealed record SourceBackedInitialSemanticMission(
    System.Text.Json.JsonElement Arguments,
    string DecisionSource);

public sealed record SourceBackedIntake(
    string UserQuestion,
    string TaskKind,
    IReadOnlyList<string> ExplicitConstraints,
    IReadOnlyList<string> RequestedAxes,
    bool AllowsPartialAnswer,
    string Language,
    IReadOnlyList<SourceBackedCatalogHint>? CatalogHints = null,
    SourceBackedMemoryContext? MemoryContext = null,
    string? QuestionFocus = null,
    string? RequestedDocumentName = null,
    string? RowHeaderLabel = null,
    string? StructuredCellValueMode = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? CanonicalColumnValueRefs = null,
    IReadOnlyList<SourceBackedCanonicalContentCard>? CanonicalContentCards = null,
    IReadOnlyDictionary<string, string>? CanonicalColumnSemanticRoles = null,
    SourceBackedCanonicalDiversityPolicy? CanonicalDiversityPolicy = null,
    IReadOnlyList<string>? CanonicalItemClassTerms = null,
    string? CanonicalIdentityFieldTerm = null,
    IReadOnlyList<string>? CanonicalDetailFieldTerms = null,
    IReadOnlyList<SourceBackedInitialToolCall>? InitialToolCalls = null,
    SourceBackedInitialSemanticMission? InitialSemanticMission = null,
    SourceBackedDocumentResolutionObservation? RequestedDocumentResolution = null,
    SourceBackedDocumentScope DocumentScope =
        SourceBackedDocumentScope.RequestedDocument,
    string? DocumentScopeDisclosure = null,
    string? NamedReferenceKind = null);

public sealed record RetrievalPlan(
    string PlannerReasoningSummary,
    IReadOnlyList<RetrievalRequest> Requests,
    bool NeedsClarification,
    SourceBackedPlannerIntakeDecision? IntakeDecision = null,
    string? StrategyReviewDecision = null,
    string? QueryCoverageDecision = null,
    string? QueryCoverageReason = null,
    string? StructureReviewDecision = null,
    string? StructureReviewKind = null,
    string? StructureReviewReason = null,
    string? ColumnCoverageDecision = null,
    string? ColumnCoverageReason = null,
    string? QueryQualityDecision = null,
    string? QueryQualityReason = null,
    IReadOnlyList<SourceBackedStructureAnchorGroup>? StructureAnchorGroups = null,
    IReadOnlyList<string>? CanonicalItemClassTerms = null,
    string? CanonicalIdentityFieldTerm = null,
    IReadOnlyList<string>? CanonicalDetailFieldTerms = null);

public sealed record SourceBackedStructureAnchorGroup(
    string Axis,
    IReadOnlyList<string> LabelsExact,
    string AnchorQuote,
    string Relation);

public sealed record SourceBackedIntakeAnchorEntry(
    string Axis,
    string LabelExact,
    string Quote,
    string Relation);

public sealed record SourceBackedIntakeAnchorRepairDecision(
    string Decision,
    string Kind,
    string Reason,
    string RowHeaderLabel,
    bool NeedsClarification,
    IReadOnlyList<SourceBackedIntakeAnchorEntry> Entries);

public sealed record SourceBackedIntakeRowAxisDecision(
    string Decision,
    string Reason,
    string RowHeaderLabel,
    string Quote,
    string Relation,
    IReadOnlyList<string> Labels);

public sealed record SourceBackedIntakeRowAxisMetadataPatch(
    string Decision,
    string Reason,
    string RowHeaderLabel,
    string Quote,
    string Relation);

public sealed record SourceBackedIntakeRowAxisCandidateSelectionPatch(
    string Decision,
    string Reason,
    string RowHeaderLabel,
    string Quote,
    string Relation,
    IReadOnlyList<string> SelectedLabelsExact);

public sealed record SourceBackedIntakeColumnAnchorDecision(
    string Label,
    string Quote,
    string Relation);

public sealed record SourceBackedIntakeColumnAxisDecision(
    string Decision,
    string Reason,
    IReadOnlyList<SourceBackedIntakeColumnAnchorDecision> Anchors);

public sealed record SourceBackedIntakeColumnAxisCompletenessDecision(
    IReadOnlyList<SourceBackedIntakeColumnAnchorDecision> MissingAnchors);

public sealed record SourceBackedIntakeColumnAxisCompletenessReviewDecision(int Count);

public sealed record SourceBackedIntakeColumnAxisSelectionPatch(
    IReadOnlyList<int> SelectedConflictCandidateIds);

public sealed record SourceBackedIntakeColumnAnchorMetadataPatch(
    int CandidateId,
    string Quote,
    string Relation);

public sealed record SourceBackedIntakeColumnAxisAnchorPatch(
    IReadOnlyList<SourceBackedIntakeColumnAnchorMetadataPatch> Anchors);

public sealed record SourceBackedIntakeShapeDecision(
    string Decision,
    string Kind,
    string Reason);

public sealed record SourceBackedPlannerIntakeDecision(
    string TaskKind,
    IReadOnlyList<string> ExplicitConstraints,
    IReadOnlyList<string> RowLabels,
    IReadOnlyList<string> ColumnLabels,
    bool? AllowsPartialAnswer,
    string Language,
    string? QuestionFocus = null,
    string? RequestedDocumentName = null,
    string? RowHeaderLabel = null,
    string? StructuredCellValueMode = null);

public sealed record RetrievalRequest(
    string ToolName,
    string Query,
    string? CategoryPath,
    string Purpose,
    string? DocId = null,
    string? DocPath = null,
    string? DocRef = null,
    string? ChunkId = null,
    int? PageStart = null,
    int? PageEnd = null,
    int? Limit = null,
    int? Offset = null,
    string? TargetFacetExact = null,
    IReadOnlyList<string>? QueryVariants = null,
    int? NextOffset = null,
    int? MaterializedEvidenceCount = null,
    int? NewEvidenceCount = null,
    System.Text.Json.JsonElement? ToolArguments = null,
    IReadOnlyList<string>? NewEvidenceIds = null,
    int? SemanticAuditApprovedCount = null,
    int? SemanticAuditRejectedCount = null,
    IReadOnlyDictionary<string, int>? SemanticCompatibilityCounts = null);

public sealed record SourceBackedToolActionDecision(
    string ActionDecision,
    RetrievalRequest? Request,
    string Reason);

public sealed record SourceBackedFacetQueryRevision(
    int RequestIndex,
    string TargetFacetExact,
    string Query,
    string Purpose,
    IReadOnlyList<string>? RepresentativeSourceTerms = null,
    IReadOnlyList<string>? QueryVariants = null);

public sealed record SourceBackedFacetQueryReviewDecision(
    string ReviewDecision,
    string Reason,
    IReadOnlyList<SourceBackedFacetQueryRevision> Requests,
    IReadOnlyList<string>? SourceItemClassTerms = null,
    string? IdentityFieldTerm = null,
    IReadOnlyList<string>? DetailFieldTerms = null);

public sealed record SourceBackedQueryYieldVerdict(IReadOnlyList<int> InsufficientRequestIndexes);

public sealed record SourceBackedQueryAnswerShapeDecision(bool ContainsItemIdentifierAndDetails);

public sealed record SourceBackedEvidenceStatusActionDecision(string RecommendedNextAction);

public sealed record SourceBackedQuerySourceDomainBrief(
    string SharedSourceItemClass,
    string AnswerBearingAttributesPerItem);

public sealed record SourceBackedQuerySourceDomainVerdict(IReadOnlyList<string> FailureModes);

public sealed record SourceBackedAtomicSemanticReviewDecision(string Decision);

public sealed record SourceBackedFacetCoverageVerdict(IReadOnlyList<int> ExcludedFacetIndexes);

public sealed record SourceBackedCandidateValueLeakageDecision(bool ContainsCandidateValues);

public sealed record SourceBackedFacetScopeAuditDecision(
    string Decision,
    string Reason,
    IReadOnlyList<int> ScopeIds);

public sealed record SourceBackedPlannerFacetAssignment(
    int RequestId,
    int FacetId);

public sealed record SourceBackedPlannerFacetAssignmentPatch(
    IReadOnlyList<SourceBackedPlannerFacetAssignment> Assignments);

public sealed record SourceBackedCompactFacetPlanRequest(
    int RequestIndex,
    string TargetFacetExact,
    string ToolName,
    string Query,
    string Purpose,
    IReadOnlyList<string>? ItemClassTerms = null,
    IReadOnlyList<string>? EvidenceFieldTerms = null,
    IReadOnlyList<string>? DisambiguationTerms = null);

public sealed record SourceBackedCompactFacetPlanDecision(
    string PlanDecision,
    string Reason,
    string QueryCoverageDecision,
    string QueryCoverageReason,
    string ColumnCoverageDecision,
    string ColumnCoverageReason,
    string? SharedCategoryPath,
    IReadOnlyList<SourceBackedCompactFacetPlanRequest> Requests,
    IReadOnlyList<string>? SharedItemClassTerms = null,
    string? SharedIdentityFieldTerm = null,
    IReadOnlyList<string>? SharedDetailFieldTerms = null);

public sealed record EvidenceJudgeDecision(
    string Decision,
    IReadOnlyList<string> EvidenceIdsToUse,
    IReadOnlyList<string> EvidenceIdsToIgnore,
    IReadOnlyList<RetrievalRequest> FollowUpRequests,
    IReadOnlyList<string> MissingEvidenceNotes);

public sealed record WriterDraft(
    string Answer,
    IReadOnlyList<string> CitedEvidenceIds);

public sealed record AnswerAdequacyReview(
    string Decision,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<RetrievalRequest> FollowUpRequests,
    WriterDraft? RevisedDraft,
    IReadOnlyList<string>? AuditedClaimRefs = null,
    IReadOnlyList<string>? UnsupportedClaimRefs = null);

internal sealed record SourceBackedStructuredCellClaim(
    string ClaimRef,
    string RowLabel,
    string ColumnHeader,
    string ClaimText,
    IReadOnlyList<string> EvidenceIds);

internal sealed record SourceBackedStructuredValueTypeCandidate(
    string CandidateId,
    string ColumnHeader,
    string ClaimText,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<string> ClaimRefs);

internal sealed record SourceBackedStructuredValueTypeCheck(
    string CandidateId,
    string Decision,
    string Reason);

internal sealed record SourceBackedStructuredValueTypeReview(
    IReadOnlyList<SourceBackedStructuredValueTypeCheck> Checks);

internal sealed record SourceBackedStructuredThinCellPatch(
    string ClaimRef,
    string Text,
    IReadOnlyList<string> EvidenceIds);

internal sealed record SourceBackedStructuredThinCellPatchSet(
    IReadOnlyList<SourceBackedStructuredThinCellPatch> Patches);

public sealed record SourceVerificationError(
    string Code,
    string Message,
    string? EvidenceId,
    SourceBackedPipelineStep Step);

public sealed record SourceVerificationResult(
    bool IsValid,
    IReadOnlyList<SourceVerificationError> Errors,
    IReadOnlyList<EvidenceItem> CitedEvidence);
