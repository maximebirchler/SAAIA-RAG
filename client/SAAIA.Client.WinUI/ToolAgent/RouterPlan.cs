using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed class RouterPlan
{
    public string Mode { get; set; } = "auto";         // auto|standard|strict
    public string Language { get; set; } = "fr";       // fr|en|es|pt|de|it
    public string Intent { get; set; } = "chat.general";
    public string ResponseFormat { get; set; } = "auto";

    public bool NeedClarification { get; set; } = false;
    public List<string> ClarificationQuestions { get; set; } = new();
    public List<string> ReasoningTracePublic { get; set; } = new();
    public string? MemoryUpdate { get; set; } = null;
    public double? Confidence { get; set; } = null;

    public List<ToolCall> ToolCalls { get; set; } = new();

    public sealed class ToolCall
    {
        public string Name { get; set; } = "";
        public JsonElement Args { get; set; }
    }
}