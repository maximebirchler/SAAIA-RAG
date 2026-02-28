namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed class ToolMemory
{
    // Dernière liste (pour "suite", "reprends à partir de PDF34", "source du PDFxx")
    public List<DocumentItem> LastListedDocuments { get; set; } = new();
    public int LastListOffset { get; set; } = 0;
    public int LastListLimit { get; set; } = 80;
    public string? LastListCategory { get; set; } = null;
    public string? LastListQuery { get; set; } = null;
    public int? LastListTotal { get; set; } = null;

    // Dernières sources utilisées (après un rag.search)
    public List<SourceRef> LastSourcesUsed { get; set; } = new();

    public string LastLanguage { get; set; } = "fr";

    public sealed class DocumentItem
    {
        public string DocId { get; set; } = "";
        public string DocPath { get; set; } = "";
        public string DocName { get; set; } = "";
        public string Category { get; set; } = "";
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
}