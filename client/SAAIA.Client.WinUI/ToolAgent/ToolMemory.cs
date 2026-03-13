namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed class ToolMemory
{
    // Dernière liste (pour "suite", "reprends à partir de PDF34", "source du PDFxx")
    public List<DocumentItem> LastListedDocuments { get; set; } = new();
    // Mapping global PDFxx -> document (persisté sur la session, pas uniquement la dernière page)
    public Dictionary<string, DocumentItem> PdfMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int LastListOffset { get; set; } = 0;
    public int LastListLimit { get; set; } = 80;
    public string? LastListCategoryPath { get; set; } = null;
    public string? LastListQuery { get; set; } = null;
    public int? LastListTotal { get; set; } = null;
    public bool LastListEndOfList { get; set; } = false;

    // Dernières sources utilisées (après un rag.search)
    public List<SourceRef> LastSourcesUsed { get; set; } = new();

    // Dernier document explicitement focalisé dans la conversation
    public DocumentItem? LastFocusedDocument { get; set; } = null;

    public string LastLanguage { get; set; } = "fr";

    public string? LastUserMessage { get; set; } = null;
    public string? LastAssistantAnswer { get; set; } = null;
    public string? LastRouterIntent { get; set; } = null;
    public List<string> LastToolNames { get; set; } = new();
    public List<string> LastReasoningTracePublic { get; set; } = new();
    public string? LastPlannerMemoryUpdate { get; set; } = null;
    public double? LastRouterConfidence { get; set; } = null;
    public PendingClarificationState? PendingClarification { get; set; } = null;
    public DeterministicRenderState? LastDeterministicRender { get; set; } = null;
    public string? LastSearchOnlyCategory { get; set; } = null;
    public Dictionary<string, string> SummaryTranslationCache { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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


    public sealed class DeterministicRenderState
    {
        public string Kind { get; set; } = "";
        public string DataJson { get; set; } = "";
        public string? RouterIntent { get; set; } = null;
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
}
