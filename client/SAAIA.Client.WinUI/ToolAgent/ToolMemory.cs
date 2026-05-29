using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

/// <summary>
/// Runtime memory container aligned with the CDC memory model.
/// We keep the legacy flat property surface for compatibility, but the
/// underlying state is now grouped by responsibility:
/// workspace canonical memory,
/// session working memory,
/// execution / observability memory.
/// </summary>
public sealed class ToolMemory
{
    private const int DefaultListLimit = 80;

    public ToolMemory()
    {
        Preferences = new InteractionPreferencesMemory();
        Workspace = new WorkspaceCanonicalMemory();
        Session = new SessionWorkingMemory();
        Execution = new ExecutionObservabilityMemory();
    }

    public InteractionPreferencesMemory Preferences { get; }

    public WorkspaceCanonicalMemory Workspace { get; }

    public SessionWorkingMemory Session { get; }

    public ExecutionObservabilityMemory Execution { get; }

    public int SchemaVersion => 1;

    public string MemoryProfile => "cdc-v3-m1lite-m3-m6";

    public List<DocumentItem> LastListedDocuments
    {
        get => Session.LastListedDocuments;
        set => Session.LastListedDocuments = value ?? new();
    }

    public Dictionary<string, DocumentItem> PdfMap
    {
        get => Session.PdfMap;
        set => Session.PdfMap = value ?? new(StringComparer.OrdinalIgnoreCase);
    }

    public int LastListOffset
    {
        get => Session.LastListOffset;
        set => Session.LastListOffset = value;
    }

    public int LastListLimit
    {
        get => Session.LastListLimit;
        set => Session.LastListLimit = value;
    }

    public string? LastListCategoryPath
    {
        get => Session.LastListCategoryPath;
        set => Session.LastListCategoryPath = value;
    }

    public string? LastListQuery
    {
        get => Session.LastListQuery;
        set => Session.LastListQuery = value;
    }

    public int? LastListTotal
    {
        get => Session.LastListTotal;
        set => Session.LastListTotal = value;
    }

    public bool LastListEndOfList
    {
        get => Session.LastListEndOfList;
        set => Session.LastListEndOfList = value;
    }

    public List<SourceRef> LastSourcesUsed
    {
        get => Session.LastSourcesUsed;
        set => Session.LastSourcesUsed = value ?? new();
    }

    public DocumentItem? LastFocusedDocument
    {
        get => Session.LastFocusedDocument;
        set => Session.LastFocusedDocument = value;
    }

    public string LastLanguage
    {
        get => Preferences.Language;
        set => Preferences.Language = value;
    }

    public string LastStyle
    {
        get => Preferences.Style;
        set => Preferences.Style = value;
    }

    public string LastMode
    {
        get => Execution.ActiveMode;
        set => Execution.ActiveMode = value;
    }

    public string? LastUserDetectedLanguage
    {
        get => Execution.LastUserDetectedLanguage;
        set => Execution.LastUserDetectedLanguage = value;
    }

    public string? LastAnswerLanguage
    {
        get => Execution.LastAnswerLanguage;
        set => Execution.LastAnswerLanguage = value;
    }

    public string? LastUserMessage
    {
        get => Session.LastUserMessage;
        set => Session.LastUserMessage = value;
    }

    public string? LastAssistantAnswer
    {
        get => Session.LastAssistantAnswer;
        set => Session.LastAssistantAnswer = value;
    }

    public string? LastRequestedDocumentRef
    {
        get => Session.LastRequestedDocumentRef;
        set => Session.LastRequestedDocumentRef = value;
    }

    public string? LastRouterIntent
    {
        get => Execution.LastRouterIntent;
        set => Execution.LastRouterIntent = value;
    }

    public List<string> LastToolNames
    {
        get => Execution.LastToolNames;
        set => Execution.LastToolNames = value ?? new();
    }

    public List<string> LastRagQueries
    {
        get => Execution.LastRagQueries;
        set => Execution.LastRagQueries = value ?? new();
    }

    public List<string> LastRagHitLabels
    {
        get => Execution.LastRagHitLabels;
        set => Execution.LastRagHitLabels = value ?? new();
    }

    public List<string> LastRagDegradedRetrievers
    {
        get => Execution.LastRagDegradedRetrievers;
        set => Execution.LastRagDegradedRetrievers = value ?? new();
    }

    public List<string> LastReasoningTracePublic
    {
        get => Execution.LastReasoningTracePublic;
        set => Execution.LastReasoningTracePublic = value ?? new();
    }

    public List<string> LastRiskFlags
    {
        get => Execution.LastRiskFlags;
        set => Execution.LastRiskFlags = value ?? new();
    }

    public string? LastPlannerMemoryUpdate
    {
        get => Execution.LastPlannerMemoryUpdate;
        set => Execution.LastPlannerMemoryUpdate = value;
    }

    public double? LastRouterConfidence
    {
        get => Execution.LastRouterConfidence;
        set => Execution.LastRouterConfidence = value;
    }

    public PendingClarificationState? PendingClarification
    {
        get => Session.PendingClarification;
        set => Session.PendingClarification = value;
    }

    public DeterministicRenderState? LastDeterministicRender
    {
        get => Session.LastDeterministicRender;
        set => Session.LastDeterministicRender = value;
    }

    public string? LastSearchOnlyCategory
    {
        get => Session.LastSearchOnlyCategory;
        set => Session.LastSearchOnlyCategory = value;
    }

    public string? LastInventoryAction
    {
        get => Session.LastInventoryAction;
        set => Session.LastInventoryAction = value;
    }

    public CategorySnapshot? LastResolvedCategory
    {
        get => Session.LastResolvedCategory;
        set => Session.LastResolvedCategory = value;
    }

    public List<CategorySnapshot> LastPresentedCategories
    {
        get => Session.LastPresentedCategories;
        set => Session.LastPresentedCategories = value ?? new();
    }

    public SummaryStatusSnapshot? LastSummaryStatusSnapshot
    {
        get => Session.LastSummaryStatusSnapshot;
        set => Session.LastSummaryStatusSnapshot = value;
    }

    public Dictionary<string, string> SummaryTranslationCache
    {
        get => Session.SummaryTranslationCache;
        set => Session.SummaryTranslationCache = value ?? new(StringComparer.OrdinalIgnoreCase);
    }

    public RuntimeCatalogSnapshot? CatalogSnapshotCache
    {
        get => Workspace.CatalogSnapshotCache;
        set => Workspace.CatalogSnapshotCache = value;
    }

    public RuntimeCapabilitiesSnapshot? CapabilitiesCache
    {
        get => Workspace.CapabilitiesCache;
        set => Workspace.CapabilitiesCache = value;
    }

    public List<DocumentItem> WorkspaceKnownDocuments
    {
        get => Workspace.KnownDocuments;
        set => Workspace.KnownDocuments = value ?? new();
    }

    public PendingDirectCommand? StagedDirectCommand
    {
        get => Execution.StagedDirectCommand;
        set => Execution.StagedDirectCommand = value;
    }

    public AdminOperationState? LastAdminOperation
    {
        get => Execution.LastAdminOperation;
        set => Execution.LastAdminOperation = value;
    }

    public sealed class InteractionPreferencesMemory
    {
        public string Language { get; set; } = "fr";

        public string Style { get; set; } = "auto";
    }

    public sealed class WorkspaceCanonicalMemory
    {
        public RuntimeCatalogSnapshot? CatalogSnapshotCache { get; set; }

        public RuntimeCapabilitiesSnapshot? CapabilitiesCache { get; set; }

        public List<DocumentItem> KnownDocuments { get; set; } = new();
    }

    public sealed class SessionWorkingMemory
    {
        public List<DocumentItem> LastListedDocuments { get; set; } = new();

        public Dictionary<string, DocumentItem> PdfMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public int LastListOffset { get; set; }

        public int LastListLimit { get; set; } = DefaultListLimit;

        public string? LastListCategoryPath { get; set; }

        public string? LastListQuery { get; set; }

        public int? LastListTotal { get; set; }

        public bool LastListEndOfList { get; set; }

        public List<SourceRef> LastSourcesUsed { get; set; } = new();

        public DocumentItem? LastFocusedDocument { get; set; }

        public string? LastUserMessage { get; set; }

        public string? LastAssistantAnswer { get; set; }

        public string? LastRequestedDocumentRef { get; set; }

        public PendingClarificationState? PendingClarification { get; set; }

        public DeterministicRenderState? LastDeterministicRender { get; set; }

        public string? LastSearchOnlyCategory { get; set; }

        public string? LastInventoryAction { get; set; }

        public CategorySnapshot? LastResolvedCategory { get; set; }

        public List<CategorySnapshot> LastPresentedCategories { get; set; } = new();

        public SummaryStatusSnapshot? LastSummaryStatusSnapshot { get; set; }

        public Dictionary<string, string> SummaryTranslationCache { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class ExecutionObservabilityMemory
    {
        public string ActiveMode { get; set; } = "auto";

        public string? LastUserDetectedLanguage { get; set; }

        public string? LastAnswerLanguage { get; set; }

        public string? LastRouterIntent { get; set; }

        public List<string> LastToolNames { get; set; } = new();

        public List<string> LastRagQueries { get; set; } = new();

        public List<string> LastRagHitLabels { get; set; } = new();

        public List<string> LastRagDegradedRetrievers { get; set; } = new();

        public List<RagEvidenceExplorationTrace> LastRagEvidenceExploration { get; set; } = new();

        public List<string> LastReasoningTracePublic { get; set; } = new();

        public List<string> LastRiskFlags { get; set; } = new();

        public string? LastPlannerMemoryUpdate { get; set; }

        public double? LastRouterConfidence { get; set; }

        public PendingDirectCommand? StagedDirectCommand { get; set; }

        public AdminOperationState? LastAdminOperation { get; set; }
    }

    public sealed class RagEvidenceExplorationTrace
    {
        public string Label { get; set; } = "";

        public List<string> Queries { get; set; } = new();

        public string? KindBefore { get; set; }

        public string? ReasonBefore { get; set; }

        public int ScoreBefore { get; set; }

        public int UsableHitsBefore { get; set; }

        public int CandidateCountBefore { get; set; }

        public int DistinctDocumentsBefore { get; set; }

        public int DistinctPagesBefore { get; set; }

        public int MinimumCandidates { get; set; }

        public int TargetSlots { get; set; }

        public long ElapsedMs { get; set; }

        public bool Accepted { get; set; }

        public string? RejectReason { get; set; }

        public string? ReasonAfter { get; set; }

        public int? ScoreAfter { get; set; }

        public int? UsableHitsAfter { get; set; }

        public int? CandidateCountAfter { get; set; }

        public int? DistinctDocumentsAfter { get; set; }

        public int? DistinctPagesAfter { get; set; }
    }

    public sealed class DocumentItem
    {
        public string DocId { get; set; } = "";
        public string DocPath { get; set; } = "";
        public string DocName { get; set; } = "";
        public string Category { get; set; } = "";
        public string? CategoryRef { get; set; }
        public string CategoryPath { get; set; } = "";
        public string PdfRef { get; set; } = "";
        public string? SourceHash { get; set; }
        public string? DocLanguage { get; set; }
        public string? ProfileLanguage { get; set; }
        public int? Pages { get; set; }
        public DateTimeOffset? ModifiedAt { get; set; }
        public DateTimeOffset? IngestedAt { get; set; }
    }

    public sealed class SourceRef
    {
        public string? DocId { get; set; }
        public string DocPath { get; set; } = "";
        public string? DocName { get; set; }
        public int PageStart { get; set; } = 1;
        public int PageEnd { get; set; } = 1;
        public string Label { get; set; } = "";
        public string? SourceHash { get; set; }
        public string? DocLanguage { get; set; }
        public string? ProfileLanguage { get; set; }
        public string? Category { get; set; }
        public string? CategoryRef { get; set; }
        public string? CategoryPath { get; set; }
        public string? ChunkId { get; set; }
        public string? SectionTitle { get; set; }
        public string? HeadingPath { get; set; }
        public string? PrevChunkId { get; set; }
        public string? NextChunkId { get; set; }
        public string? SameSectionChunkId { get; set; }
        public string? OriginalChunkType { get; set; }
        public int? OffsetStart { get; set; }
        public int? OffsetEnd { get; set; }
        public string? ExtractionSource { get; set; }
        public string? DocumentQualityStatus { get; set; }
        public string? PageQualityStatus { get; set; }
        public string? TextStatus { get; set; }
        public string? ChunkTextStatus { get; set; }
        public bool? ChunkTextSparse { get; set; }
        public bool? ChunkOcrCandidate { get; set; }
        public string? QualityStatus { get; set; }
        public double? ExtractionConfidence { get; set; }
        public double? DocumentExtractionConfidence { get; set; }
        public double? PageExtractionConfidence { get; set; }
        public bool ManualReviewRecommended { get; set; }
        public bool DocumentManualReviewRecommended { get; set; }
        public bool PageManualReviewRecommended { get; set; }
        public bool OcrAttempted { get; set; }
        public bool OcrApplied { get; set; }
        public bool OcrRecommended { get; set; }
        public SourceExtractionDiagnosticRef? ExtractionDiagnosticSummary { get; set; }
        public List<string> QualitySignals { get; set; } = new();
        public List<string> ChunkQualitySignals { get; set; } = new();
        public List<SourceContentCardRef> MatchedContentCards { get; set; } = new();
        public SourceProfileSignalsRef? ProfileSignals { get; set; }
        public string? SelectionHintEvidenceRole { get; set; }
        public int? SelectionHintActionabilityScore { get; set; }
        public int? SelectionHintSupportScore { get; set; }
        public int? SelectionHintFragmentScore { get; set; }
        public int? SelectionHintNavigationScore { get; set; }
        public int? SelectionHintQualityPenalty { get; set; }
        public string? ContentRole { get; set; }
        public string? NavigationReason { get; set; }
        public double? RetrievalNavigationScore { get; set; }
        public double? ContentDensityScore { get; set; }
    }

    public sealed class SourceProfileSignalsRef
    {
        public string? ProfileVersion { get; set; }
        public string? Language { get; set; }
        public List<string> Keywords { get; set; } = new();
        public List<string> Entities { get; set; } = new();
        public List<string> Topics { get; set; } = new();
        public List<string> HypotheticalQuestions { get; set; } = new();
        public List<string> Limits { get; set; } = new();
        public List<string> MatchedTerms { get; set; } = new();
        public int? MatchCount { get; set; }
    }

    public sealed class SourceExtractionDiagnosticRef
    {
        public string? NativeTextStatus { get; set; }
        public bool? NativeOcrRecommended { get; set; }
        public string? OcrMode { get; set; }
        public string? OcrLanguages { get; set; }
        public long? OcrDurationMs { get; set; }
        public string? OcrFailureReason { get; set; }
        public string? OcrAppliedReason { get; set; }
        public bool? OcrTimedOut { get; set; }
        public int? OcrAttemptedPageCount { get; set; }
        public int? OcrSkippedPageCount { get; set; }
        public int? OcrPagesWithNovelTextCount { get; set; }
        public int? PageCount { get; set; }
        public int? TextPageCount { get; set; }
        public int? EmptyPageCount { get; set; }
        public int? SparsePageCount { get; set; }
        public int? ImagePageCount { get; set; }
        public int? PageWarningCount { get; set; }
        public int? PageReviewRecommendedCount { get; set; }
        public SourceRetrievalChunkQualityRef? RetrievalChunkQuality { get; set; }
    }

    public sealed class SourceRetrievalChunkQualityRef
    {
        public int? TotalChunkCount { get; set; }
        public int? SearchableChunkCount { get; set; }
        public int? RejectedChunkCount { get; set; }
        public bool? ManualReviewRecommended { get; set; }
        public Dictionary<string, int> RejectionReasons { get; set; } = new();
    }

    public sealed class SourceContentCardRef
    {
        public string Title { get; set; } = "";
        public string? ContentCardId { get; set; }
        public int? PageStart { get; set; }
        public int? PageEnd { get; set; }
        public string? Kind { get; set; }
        public List<string> Signals { get; set; } = new();
        public JsonElement? Evidence { get; set; }
    }

    public sealed class CategorySnapshot
    {
        public string CategoryRef { get; set; } = "";
        public string CategoryPath { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public int Ordinal { get; set; }
        public int TotalDocuments { get; set; }
        public List<string> Aliases { get; set; } = new();
    }

    public sealed class SummaryStatusSnapshot
    {
        public string? CategoryPath { get; set; }
        public string? CategoryRef { get; set; }
        public string Mode { get; set; } = "missing";
        public int Total { get; set; }
        public int MissingStored { get; set; }
        public int StaleStored { get; set; }
        public int ProfileMissing { get; set; }
        public List<SummaryStatusItem> Items { get; set; } = new();
    }

    public sealed class SummaryStatusItem
    {
        public string DocId { get; set; } = "";
        public string DocPath { get; set; } = "";
        public string DocName { get; set; } = "";
        public string Category { get; set; } = "";
        public string? CategoryRef { get; set; }
        public string? CategoryPath { get; set; }
        public string SummaryState { get; set; } = "missing";
        public string? CapabilityBProfileState { get; set; }
        public bool CapabilityBHasBackofficeProfile { get; set; }
        public List<string> CapabilityBReasons { get; set; } = new();
        public bool HasActiveSummaryJob { get; set; }
        public string? ActiveSummaryJobId { get; set; }
        public string? ActiveSummaryJobType { get; set; }
        public string? ActiveSummaryJobStatus { get; set; }
        public string? ActiveSummaryJobExecutionMode { get; set; }
        public string? ActiveSummaryJobRuntimeCapabilityKey { get; set; }
        public string? ActiveSummaryJobRuntimeCapabilityStatus { get; set; }
        public string? ActiveSummaryJobEnqueueSource { get; set; }
        public string? ActiveSummaryJobCampaignId { get; set; }
        public bool CapabilityBReadyToEnqueue { get; set; }
        public string? CapabilityBRecommendedAction { get; set; }
        public bool CapabilityBPolicyBlocked { get; set; }
        public string? CapabilityBPolicyBlockReason { get; set; }
        public double? CapabilityBPriorityScore { get; set; }
        public string? CapabilityBLastJobStatus { get; set; }
        public string? CapabilityBLastJobFinishedAt { get; set; }
        public string? CapabilityBLastJobError { get; set; }
    }

    public sealed class DeterministicRenderState
    {
        public string Kind { get; set; } = "";
        public string DataJson { get; set; } = "";
        public string? RouterIntent { get; set; }
        public string? SourceUserMessage { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    public sealed class PendingClarificationState
    {
        public string Kind { get; set; } = "";
        public string OriginalUserMessage { get; set; } = "";
        public string? Hint { get; set; }
        public string? Language { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    public sealed class RuntimeCatalogSnapshot
    {
        public string? SnapshotId { get; set; }
        public string? CatalogVersion { get; set; }
        public DateTimeOffset LoadedAtUtc { get; set; } = DateTimeOffset.UtcNow;
        public int? TotalDocuments { get; set; }
        public int? TotalCategories { get; set; }
        public List<CategorySnapshot> Categories { get; set; } = new();
    }

    public sealed class RuntimeCapabilitiesSnapshot
    {
        public bool IsAuthenticated { get; set; }
        public bool IsAdmin { get; set; }
        public string DefaultLocale { get; set; } = "fr-CH";
        public DateTimeOffset LoadedAtUtc { get; set; } = DateTimeOffset.UtcNow;
        public List<string> DirectCommandIds { get; set; } = new();
        public List<string> AdminCommandIds { get; set; } = new();
    }

    public sealed class AdminOperationState
    {
        public string OperationKind { get; set; } = "";
        public string DisplayLabel { get; set; } = "";
        public string? JobId { get; set; }
        public string? DocumentRef { get; set; }
        public string? DocPath { get; set; }
        public string Status { get; set; } = "queued";
        public string? LastError { get; set; }
        public bool IsCompleted { get; set; }
        public bool IsSuccess { get; set; }
        public int? IndexedDocuments { get; set; }
        public int? TotalCategories { get; set; }
        public int? MaxDepth { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LastUpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    public sealed class PendingDirectCommand
    {
        public string CommandId { get; set; } = "";
        public string ArgsJson { get; set; } = "{}";
        public string? CategoryRef { get; set; }
        public string? CategoryPath { get; set; }
        public string Source { get; set; } = "text_shortcut";
        public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    public void PromoteCategoriesToWorkspace(IEnumerable<CategorySnapshot>? categories)
    {
        if (categories is null)
            return;

        var merged = new Dictionary<string, CategorySnapshot>(StringComparer.OrdinalIgnoreCase);

        if (Workspace.CatalogSnapshotCache?.Categories is { Count: > 0 })
        {
            foreach (var existing in Workspace.CatalogSnapshotCache.Categories)
            {
                var key = BuildCategoryKey(existing);
                if (!string.IsNullOrWhiteSpace(key))
                    merged[key] = CloneCategory(existing);
            }
        }

        foreach (var incoming in categories)
        {
            if (incoming is null)
                continue;

            var key = BuildCategoryKey(incoming);
            if (string.IsNullOrWhiteSpace(key))
                continue;

            if (merged.TryGetValue(key, out var existing))
            {
                merged[key] = MergeCategory(existing, incoming);
            }
            else
            {
                merged[key] = CloneCategory(incoming);
            }
        }

        if (merged.Count == 0)
            return;

        Workspace.CatalogSnapshotCache ??= new RuntimeCatalogSnapshot
        {
            LoadedAtUtc = DateTimeOffset.UtcNow
        };

        Workspace.CatalogSnapshotCache.Categories = merged.Values
            .OrderBy(x => x.Ordinal == 0 ? int.MaxValue : x.Ordinal)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Workspace.CatalogSnapshotCache.TotalCategories = Workspace.CatalogSnapshotCache.Categories.Count;
        Workspace.CatalogSnapshotCache.LoadedAtUtc = DateTimeOffset.UtcNow;
    }

    public void PromoteDocumentsToWorkspace(IEnumerable<DocumentItem>? documents)
    {
        if (documents is null)
            return;

        var merged = new Dictionary<string, DocumentItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var existing in Workspace.KnownDocuments)
        {
            var key = BuildDocumentKey(existing);
            if (!string.IsNullOrWhiteSpace(key))
                merged[key] = CloneDocument(existing);
        }

        foreach (var incoming in documents)
        {
            if (incoming is null)
                continue;

            var key = BuildDocumentKey(incoming);
            if (string.IsNullOrWhiteSpace(key))
                continue;

            if (merged.TryGetValue(key, out var existing))
            {
                merged[key] = MergeDocument(existing, incoming);
            }
            else
            {
                merged[key] = CloneDocument(incoming);
            }
        }

        Workspace.KnownDocuments = merged.Values
            .OrderBy(x => x.DocName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.DocPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildCategoryKey(CategorySnapshot category)
    {
        if (!string.IsNullOrWhiteSpace(category.CategoryRef))
            return category.CategoryRef.Trim();
        if (!string.IsNullOrWhiteSpace(category.CategoryPath))
            return category.CategoryPath.Trim();
        return category.DisplayName?.Trim() ?? string.Empty;
    }

    private static string BuildDocumentKey(DocumentItem document)
    {
        if (!string.IsNullOrWhiteSpace(document.DocId))
            return document.DocId.Trim();
        if (!string.IsNullOrWhiteSpace(document.DocPath))
            return document.DocPath.Trim();
        return document.DocName?.Trim() ?? string.Empty;
    }

    private static CategorySnapshot CloneCategory(CategorySnapshot source)
        => new()
        {
            CategoryRef = source.CategoryRef,
            CategoryPath = source.CategoryPath,
            DisplayName = source.DisplayName,
            Ordinal = source.Ordinal,
            TotalDocuments = source.TotalDocuments,
            Aliases = source.Aliases?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new()
        };

    private static CategorySnapshot MergeCategory(CategorySnapshot existing, CategorySnapshot incoming)
    {
        var merged = CloneCategory(existing);
        if (!string.IsNullOrWhiteSpace(incoming.CategoryRef))
            merged.CategoryRef = incoming.CategoryRef;
        if (!string.IsNullOrWhiteSpace(incoming.CategoryPath))
            merged.CategoryPath = incoming.CategoryPath;
        if (!string.IsNullOrWhiteSpace(incoming.DisplayName))
            merged.DisplayName = incoming.DisplayName;
        if (incoming.Ordinal > 0)
            merged.Ordinal = incoming.Ordinal;
        if (incoming.TotalDocuments > 0)
            merged.TotalDocuments = incoming.TotalDocuments;

        foreach (var alias in incoming.Aliases ?? new List<string>())
        {
            if (!string.IsNullOrWhiteSpace(alias) && !merged.Aliases.Contains(alias, StringComparer.OrdinalIgnoreCase))
                merged.Aliases.Add(alias);
        }

        return merged;
    }

    private static DocumentItem CloneDocument(DocumentItem source)
        => new()
        {
            DocId = source.DocId,
            DocPath = source.DocPath,
            DocName = source.DocName,
            Category = source.Category,
            CategoryRef = source.CategoryRef,
            CategoryPath = source.CategoryPath,
            PdfRef = source.PdfRef,
            SourceHash = source.SourceHash,
            DocLanguage = source.DocLanguage,
            ProfileLanguage = source.ProfileLanguage,
            Pages = source.Pages,
            ModifiedAt = source.ModifiedAt,
            IngestedAt = source.IngestedAt
        };

    private static DocumentItem MergeDocument(DocumentItem existing, DocumentItem incoming)
    {
        var merged = CloneDocument(existing);
        if (!string.IsNullOrWhiteSpace(incoming.DocId))
            merged.DocId = incoming.DocId;
        if (!string.IsNullOrWhiteSpace(incoming.DocPath))
            merged.DocPath = incoming.DocPath;
        if (!string.IsNullOrWhiteSpace(incoming.DocName))
            merged.DocName = incoming.DocName;
        if (!string.IsNullOrWhiteSpace(incoming.Category))
            merged.Category = incoming.Category;
        if (!string.IsNullOrWhiteSpace(incoming.CategoryRef))
            merged.CategoryRef = incoming.CategoryRef;
        if (!string.IsNullOrWhiteSpace(incoming.CategoryPath))
            merged.CategoryPath = incoming.CategoryPath;
        if (!string.IsNullOrWhiteSpace(incoming.PdfRef))
            merged.PdfRef = incoming.PdfRef;
        if (!string.IsNullOrWhiteSpace(incoming.SourceHash))
            merged.SourceHash = incoming.SourceHash;
        if (!string.IsNullOrWhiteSpace(incoming.DocLanguage))
            merged.DocLanguage = incoming.DocLanguage;
        if (!string.IsNullOrWhiteSpace(incoming.ProfileLanguage))
            merged.ProfileLanguage = incoming.ProfileLanguage;
        merged.Pages ??= incoming.Pages;
        merged.ModifiedAt ??= incoming.ModifiedAt;
        merged.IngestedAt ??= incoming.IngestedAt;
        return merged;
    }

    /// <summary>
    /// Resets per-conversation state while preserving:
    /// - user preferences (language/style)
    /// - workspace canonical snapshots (M1-lite)
    /// Clears:
    /// - all session working memory (M3)
    /// - all execution/observability state (M6)
    /// This aligns with CDC guidance: mode/document focus/category resolution
    /// must not leak across conversations.
    /// </summary>
    public void ResetConversationState()
    {
        Session.LastListedDocuments = new();
        Session.PdfMap = new(StringComparer.OrdinalIgnoreCase);
        Session.LastListOffset = 0;
        Session.LastListLimit = DefaultListLimit;
        Session.LastListCategoryPath = null;
        Session.LastListQuery = null;
        Session.LastListTotal = null;
        Session.LastListEndOfList = false;
        Session.LastSourcesUsed = new();
        Session.LastFocusedDocument = null;
        Session.LastUserMessage = null;
        Session.LastAssistantAnswer = null;
        Session.LastRequestedDocumentRef = null;
        Session.PendingClarification = null;
        Session.LastDeterministicRender = null;
        Session.LastSearchOnlyCategory = null;
        Session.LastInventoryAction = null;
        Session.LastResolvedCategory = null;
        Session.LastPresentedCategories = new();
        Session.LastSummaryStatusSnapshot = null;
        Session.SummaryTranslationCache = new(StringComparer.OrdinalIgnoreCase);

        Execution.ActiveMode = "auto";
        Execution.LastUserDetectedLanguage = null;
        Execution.LastAnswerLanguage = null;
        Execution.LastRouterIntent = null;
        Execution.LastToolNames = new();
        Execution.LastRagQueries = new();
        Execution.LastRagHitLabels = new();
        Execution.LastRagDegradedRetrievers = new();
        Execution.LastRagEvidenceExploration = new();
        Execution.LastReasoningTracePublic = new();
        Execution.LastRiskFlags = new();
        Execution.LastPlannerMemoryUpdate = null;
        Execution.LastRouterConfidence = null;
        Execution.StagedDirectCommand = null;
        Execution.LastAdminOperation = null;
    }
}
