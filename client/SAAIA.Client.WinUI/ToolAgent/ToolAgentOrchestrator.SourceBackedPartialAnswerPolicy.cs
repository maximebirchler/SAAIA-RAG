namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static bool ShouldAllowSourceBackedPartialAnswer(RouterPlan routerPlan)
        => !string.Equals(routerPlan.Mode, "strict", StringComparison.OrdinalIgnoreCase);
}
