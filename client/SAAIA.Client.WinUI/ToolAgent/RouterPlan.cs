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
    public List<string> ReasoningTracePublic { get; set; } = new();
    public List<string> RiskFlags { get; set; } = new();
    public string? MemoryUpdate { get; set; } = null;
    public double? RouterConfidence { get; set; } = null;

    public List<ToolCall> ToolCalls { get; set; } = new();

    public sealed class ToolCall
    {
        public string Name { get; set; } = "";
        public JsonElement Args { get; set; }
    }
}
