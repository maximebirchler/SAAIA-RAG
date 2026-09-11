namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static bool ShouldAllowSourceBackedPartialAnswer(RouterPlan routerPlan)
        => !string.Equals(routerPlan.Mode, "strict", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> BuildSourceBackedRequestedAxes(string effectiveUserMessage, string language)
    {
        var dayAxes = DetectRequestedDayAxisLabels(effectiveUserMessage, language)
            .Select(static axis => "day:" + axis);
        var slotAxes = DetectRequestedPlanningSlotAxisLabels(effectiveUserMessage, language)
            .Select(static axis => "slot:" + axis);

        return dayAxes
            .Concat(slotAxes)
            .Where(static axis => !string.IsNullOrWhiteSpace(axis))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToArray();
    }
}
