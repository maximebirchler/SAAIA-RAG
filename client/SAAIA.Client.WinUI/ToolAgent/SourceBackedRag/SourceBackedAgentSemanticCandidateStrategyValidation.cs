namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static string? FindEmbeddedLayoutCoordinate(
        string value,
        IEnumerable<string> layoutCoordinateLabels)
    {
        foreach (var rawLabel in layoutCoordinateLabels)
        {
            var label = NormalizeSemanticPlanLine(rawLabel);
            if (label.Length < 2)
                continue;

            var searchStart = 0;
            while (searchStart < value.Length)
            {
                var index = value.IndexOf(
                    label,
                    searchStart,
                    StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                    break;

                var beforeIsBoundary = index == 0
                                       || !char.IsLetterOrDigit(value[index - 1]);
                var end = index + label.Length;
                var afterIsBoundary = end == value.Length
                                      || !char.IsLetterOrDigit(value[end]);
                if (beforeIsBoundary && afterIsBoundary)
                    return label;

                searchStart = index + 1;
            }
        }

        return null;
    }
}
