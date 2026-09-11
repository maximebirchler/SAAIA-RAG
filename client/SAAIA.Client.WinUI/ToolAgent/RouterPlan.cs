using System.Text.Json;
using System.Text.Json.Serialization;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public enum RouterPlanOrigin
{
    LocalFallback,
    Llm
}

public sealed class RouterPlan
{
    public string Mode { get; set; } = "auto";         // auto|standard|strict
    public string Language { get; set; } = "fr";       // fr|en|es|pt|de|it
    public string Intent { get; set; } = "chat.general";
    public string ResponseFormat { get; set; } = "auto";

    [JsonIgnore]
    public RouterPlanOrigin Origin { get; set; } = RouterPlanOrigin.LocalFallback;

    public bool NeedClarification { get; set; } = false;
    public List<string> ClarificationQuestions { get; set; } = new();
    public ClarificationDecisionPlan? Clarification { get; set; }
    public List<string> ReasoningTracePublic { get; set; } = new();
    public List<string> RiskFlags { get; set; } = new();
    public string? MemoryUpdate { get; set; } = null;
    public double? RouterConfidence { get; set; } = null;

    public List<ToolCall> ToolCalls { get; set; } = new();
    public SourceBackedMissionPlan? SourceBackedMission { get; set; }

    [JsonIgnore]
    public List<string> GroundedGridDiscoveryQueries { get; set; } = new();

    public sealed class ToolCall
    {
        public string Name { get; set; } = "";
        public JsonElement Args { get; set; }

        [JsonIgnore]
        public string? QueryHint { get; set; }
    }

    public sealed class SourceBackedMissionPlan
    {
        public string PlanKind { get; set; } = "";
        public string Deliverable { get; set; } = "";
        public bool StructuredLayout { get; set; }
        public int RowCount { get; set; } = 1;
        public int ColumnCount { get; set; } = 1;
        public int AtomicEvidenceCount { get; set; } = 1;
        public string AtomicEvidenceType { get; set; } = "";
        public string AtomicEvidenceMode { get; set; } = "named_item";
        public string SelectionPolicy { get; set; } = "single_item";
        public string AtomicEvidenceTypeStatus { get; set; } = "llm_routed";
        public string InitialCapability { get; set; } = "";
        public string QuestionFocus { get; set; } = "";
        public bool UsesFocusedDocument { get; set; }
        public string NamedReferenceKind { get; set; } = "";
        public string RequestedDocumentName { get; set; } = "";
        public bool BoundedNamedDocumentExtraction { get; set; }
        public List<string> CandidateScopePaths { get; set; } = new();
        public string RowHeader { get; set; } = "";
        public List<string> RowLabels { get; set; } = new();
        public List<string> Columns { get; set; } = new();
    }

    public sealed class ClarificationDecisionPlan
    {
        public string Message { get; set; } = "";
        public List<string> Options { get; set; } = new();
        public string ExecutionImpact { get; set; } = "";
        public string ResumeRoute { get; set; } = "";
        public string AmbiguityKind { get; set; } = "";
    }
}
