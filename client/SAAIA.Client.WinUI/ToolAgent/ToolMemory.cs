
namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed class ToolMemory
{
    public List<DocumentItem> LastListedDocuments { get; set; } = new();
    public Dictionary<string, DocumentItem> PdfMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int LastListOffset { get; set; } = 0;
    public int LastListLimit { get; set; } = 80;
    public string? LastListCategoryPath { get; set; } = null;
    public string? LastListQuery { get; set; } = null;
    public int? LastListTotal { get; set; } = null;
    public bool LastListEndOfList { get; set; } = false;

    public List<SourceRef> LastSourcesUsed { get; set; } = new();
    public DocumentItem? LastFocusedDocument { get; set; } = null;

    public string LastLanguage { get; set; } = "fr";
    public string LastStyle { get; set; } = "auto";
    public string LastMode { get; set; } = "auto";
    public string? LastUserDetectedLanguage { get; set; } = null;
    public string? LastAnswerLanguage { get; set; } = null;

    public string? LastUserMessage { get; set; } = null;
    public string? LastAssistantAnswer { get; set; } = null;
    public string? LastRequestedDocumentRef { get; set; } = null;
    public string? LastRouterIntent { get; set; } = null;
    public List<string> LastToolNames { get; set; } = new();
    public List<string> LastReasoningTracePublic { get; set; } = new();
    public List<string> LastRiskFlags { get; set; } = new();
    public string? LastPlannerMemoryUpdate { get; set; } = null;
    public double? LastRouterConfidence { get; set; } = null;
    public PendingClarificationState? PendingClarification { get; set; } = null;
    public DeterministicRenderState? LastDeterministicRender { get; set; } = null;
    public string? LastSearchOnlyCategory { get; set; } = null;
    public string? LastInventoryAction { get; set; } = null;
    public CategorySnapshot? LastResolvedCategory { get; set; } = null;
    public List<CategorySnapshot> LastPresentedCategories { get; set; } = new();
    public SummaryStatusSnapshot? LastSummaryStatusSnapshot { get; set; } = null;
    public Dictionary<string, string> SummaryTranslationCache { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public RuntimeCatalogSnapshot? CatalogSnapshotCache { get; set; } = null;
    public RuntimeCapabilitiesSnapshot? CapabilitiesCache { get; set; } = null;
    public PendingDirectCommand? StagedDirectCommand { get; set; } = null;
    public AdminOperationState? LastAdminOperation { get; set; } = null;

    public sealed class DocumentItem
    {
        public string DocId { get; set; } = "";
        public string DocPath { get; set; } = "";
        public string DocName { get; set; } = "";
        public string Category { get; set; } = "";
        public string CategoryPath { get; set; } = "";
        public string PdfRef { get; set; } = "";
        public int? Pages { get; set; }
        public DateTimeOffset? ModifiedAt { get; set; }
        public DateTimeOffset? IngestedAt { get; set; }
    }

    public sealed class SourceRef
    {
        public string DocPath { get; set; } = "";
        public int PageStart { get; set; } = 1;
        public int PageEnd { get; set; } = 1;
        public string Label { get; set; } = "";
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
        public string? CategoryPath { get; set; } = null;
        public string? CategoryRef { get; set; } = null;
        public string Mode { get; set; } = "missing";
        public int Total { get; set; }
        public int MissingStored { get; set; }
        public int StaleStored { get; set; }
        public List<SummaryStatusItem> Items { get; set; } = new();
    }

    public sealed class SummaryStatusItem
    {
        public string DocId { get; set; } = "";
        public string DocPath { get; set; } = "";
        public string DocName { get; set; } = "";
        public string Category { get; set; } = "";
        public string SummaryState { get; set; } = "missing";
    }

    public sealed class DeterministicRenderState
    {
        public string Kind { get; set; } = "";
        public string DataJson { get; set; } = "";
        public string? RouterIntent { get; set; } = null;
        public string? SourceUserMessage { get; set; } = null;
        public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    public sealed class PendingClarificationState
    {
        public string Kind { get; set; } = "";
        public string OriginalUserMessage { get; set; } = "";
        public string? Hint { get; set; } = null;
        public string? Language { get; set; } = null;
        public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    public sealed class RuntimeCatalogSnapshot
    {
        public string? SnapshotId { get; set; } = null;
        public string? CatalogVersion { get; set; } = null;
        public DateTimeOffset LoadedAtUtc { get; set; } = DateTimeOffset.UtcNow;
        public int? TotalDocuments { get; set; } = null;
        public int? TotalCategories { get; set; } = null;
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
        public string? JobId { get; set; } = null;
        public string? DocumentRef { get; set; } = null;
        public string? DocPath { get; set; } = null;
        public string Status { get; set; } = "queued";
        public string? LastError { get; set; } = null;
        public bool IsCompleted { get; set; }
        public bool IsSuccess { get; set; }
        public int? IndexedDocuments { get; set; } = null;
        public int? TotalCategories { get; set; } = null;
        public int? MaxDepth { get; set; } = null;
        public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LastUpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    public sealed class PendingDirectCommand
    {
        public string CommandId { get; set; } = "";
        public string ArgsJson { get; set; } = "{}";
        public string? CategoryRef { get; set; } = null;
        public string? CategoryPath { get; set; } = null;
        public string Source { get; set; } = "text_shortcut";
        public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    }
}
