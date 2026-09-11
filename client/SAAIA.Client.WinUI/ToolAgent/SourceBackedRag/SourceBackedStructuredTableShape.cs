using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

internal sealed record SourceBackedStructuredTableShape(
    string FirstColumnHeader,
    IReadOnlyList<string> ColumnHeaders,
    IReadOnlyList<string> RowLabels)
{
    public IReadOnlyList<string> Headers
        => new[] { FirstColumnHeader }.Concat(ColumnHeaders).ToArray();

    public string HeaderLine
        => "| " + string.Join(" | ", Headers) + " |";

    public string SeparatorLine
        => "| " + string.Join(" | ", Enumerable.Repeat("---", Headers.Count)) + " |";
}

internal static class SourceBackedStructuredTableShapeBuilder
{
    public static SourceBackedStructuredTableShape? TryCreateFromIntake(SourceBackedIntake intake)
    {
        var rowLabels = ExtractPrefixedAxisLabels(intake.RequestedAxes, "row:");
        if (rowLabels.Count == 0)
            rowLabels = ExtractPrefixedAxisLabels(intake.RequestedAxes, "day:");

        var columnHeaders = ExtractPrefixedAxisLabels(intake.RequestedAxes, "column:");
        if (columnHeaders.Count == 0)
            columnHeaders = ExtractPrefixedAxisLabels(intake.RequestedAxes, "slot:");
        if (rowLabels.Count < 2 || columnHeaders.Count < 2)
            return null;

        var usesTypedDayRows = intake.RequestedAxes.Any(axis =>
            !string.IsNullOrWhiteSpace(axis)
            && axis.Trim().StartsWith("day:", StringComparison.OrdinalIgnoreCase));
        var firstColumnHeader = !string.IsNullOrWhiteSpace(intake.RowHeaderLabel)
            ? intake.RowHeaderLabel.Trim()
            : usesTypedDayRows
                ? string.Equals(intake.Language, "fr", StringComparison.OrdinalIgnoreCase) ? "Jour" : "Day"
                : string.Equals(intake.Language, "fr", StringComparison.OrdinalIgnoreCase) ? "Ligne" : "Row";
        return new SourceBackedStructuredTableShape(firstColumnHeader, columnHeaders, rowLabels);
    }

    public static void AppendRequestedTableShape(StringBuilder sb, SourceBackedIntake intake, string placeholder)
    {
        var shape = TryCreateFromIntake(intake);
        if (shape is null)
            return;

        sb.AppendLine("REQUESTED_TABLE_SHAPE:");
        sb.AppendLine(shape.HeaderLine);
        sb.AppendLine(shape.SeparatorLine);
        foreach (var rowLabel in shape.RowLabels)
            sb.AppendLine("| " + rowLabel + " | " + string.Join(" | ", shape.ColumnHeaders.Select(_ => placeholder)) + " |");
        sb.AppendLine("REQUESTED_TABLE_SHAPE_RULE: keep the labels exactly visible; replace placeholders with sourced content from selected or allowed evidence.");
    }

    public static string NormalizeLabel(string? value)
    {
        var decomposed = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var chars = decomposed
            .Where(static c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .Select(static c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ')
            .ToArray();
        return Regex.Replace(new string(chars), @"\s+", " ", RegexOptions.CultureInvariant).Trim();
    }

    private static IReadOnlyList<string> ExtractPrefixedAxisLabels(IEnumerable<string> axes, string prefix)
        => (axes ?? Array.Empty<string>())
            .Where(axis => !string.IsNullOrWhiteSpace(axis) && axis.Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(axis => axis.Trim()[prefix.Length..].Trim())
            .Where(static axis => !string.IsNullOrWhiteSpace(axis))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();
}
