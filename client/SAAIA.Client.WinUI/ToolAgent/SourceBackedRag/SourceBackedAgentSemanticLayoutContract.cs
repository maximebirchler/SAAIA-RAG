namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    internal static SourceBackedIntake ApplySemanticLayoutContractToIntake(
        SourceBackedIntake intake,
        string? rowHeader,
        IReadOnlyList<string> rowLabels,
        IReadOnlyList<string> columnLabels)
    {
        ArgumentNullException.ThrowIfNull(intake);
        if (rowLabels.Count < 2 || columnLabels.Count < 2)
            return intake;

        var preservedAxes = (intake.RequestedAxes ?? Array.Empty<string>())
            .Where(static axis => !string.IsNullOrWhiteSpace(axis))
            .Select(static axis => axis.Trim())
            .Where(static axis => !IsStructuredLayoutAxis(axis));
        var canonicalAxes = preservedAxes
            .Concat(rowLabels.Select(static label => "row:" + label.Trim()))
            .Concat(columnLabels.Select(static label => "column:" + label.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return intake with
        {
            RequestedAxes = canonicalAxes,
            RowHeaderLabel = string.IsNullOrWhiteSpace(rowHeader)
                ? intake.RowHeaderLabel
                : rowHeader.Trim()
        };
    }

    private static bool IsStructuredLayoutAxis(string axis)
        => axis.StartsWith("row:", StringComparison.OrdinalIgnoreCase)
           || axis.StartsWith("day:", StringComparison.OrdinalIgnoreCase)
           || axis.StartsWith("column:", StringComparison.OrdinalIgnoreCase)
           || axis.StartsWith("slot:", StringComparison.OrdinalIgnoreCase);
}
