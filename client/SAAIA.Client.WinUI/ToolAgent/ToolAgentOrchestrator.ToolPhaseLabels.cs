using SAAIA.Client.WinUI.Localization;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static string PhaseLabelForTool(string toolName, string language)
        => DeterministicAgentText.ToolPhase(toolName, language);
}
