using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public static partial class SourceContractVerifier
{
    private static IReadOnlyList<string> FindThinMarkdownTableEvidenceCells(string? answer, SourceBackedIntake intake)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return Array.Empty<string>();

        var axisLabels = BuildNormalizedAxisLabelSet(intake);
        var thinCells = new List<string>();
        foreach (var rawLine in answer.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            if (!line.Contains('|') || !EvidenceIdPattern.IsMatch(line))
                continue;

            foreach (var rawCell in line.Split('|'))
            {
                var cell = rawCell.Trim();
                if (cell.Length == 0
                    || MarkdownTableSeparatorPattern.IsMatch(cell)
                    || !EvidenceIdPattern.IsMatch(cell))
                {
                    continue;
                }

                var cleaned = CleanStructuredCellContent(cell);
                var normalized = NormalizeAxisText(cleaned);
                if (SourceLocatorPattern.IsMatch(cell)
                    || axisLabels.Contains(normalized))
                {
                    thinCells.Add(TrimCellForMessage(cell));
                }
            }
        }

        return thinCells
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<SourceBackedStructuredCellClaim> ExtractThinRequestedStructuredCellClaims(
        string? answer,
        SourceBackedIntake intake)
    {
        var axisLabels = BuildNormalizedAxisLabelSet(intake);
        return ExtractRequestedStructuredCellClaims(answer, intake)
            .Where(claim => IsThinStructuredClaimText(claim.ClaimText, axisLabels))
            .ToArray();
    }

    private static bool IsThinStructuredClaimText(string? text, IReadOnlySet<string> axisLabels)
    {
        var cleaned = CleanStructuredCellContent(text ?? string.Empty);
        var normalized = NormalizeAxisText(cleaned);
        return SourceLocatorPattern.IsMatch(text ?? string.Empty)
               || axisLabels.Contains(normalized);
    }

    private static HashSet<string> BuildNormalizedAxisLabelSet(SourceBackedIntake intake)
    {
        var labels = ExtractAxisLabels(intake.RequestedAxes ?? Array.Empty<string>(), "day:")
            .Concat(ExtractAxisLabels(intake.RequestedAxes ?? Array.Empty<string>(), "slot:"))
            .Concat(ExtractAxisLabels(intake.RequestedAxes ?? Array.Empty<string>(), "row:"))
            .Concat(ExtractAxisLabels(intake.RequestedAxes ?? Array.Empty<string>(), "column:"))
            .Concat((intake.RequestedAxes ?? Array.Empty<string>())
                .Where(static axis => !string.IsNullOrWhiteSpace(axis) && !axis.Contains(':'))
                .Select(static axis => axis.Trim()));

        return labels
            .Select(NormalizeAxisText)
            .Where(static label => label.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string CleanStructuredCellContent(string cell)
    {
        var text = EvidenceIdPattern.Replace(cell, " ");
        text = SourceLocatorPattern.Replace(text, " ");
        text = Regex.Replace(text, @"[`*_#>\[\]\(\),.;:]", " ", RegexOptions.CultureInvariant);
        return Regex.Replace(text, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
    }

    private static string TrimCellForMessage(string cell)
    {
        cell = Regex.Replace(cell, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
        return cell.Length <= 80 ? cell : cell[..77] + "...";
    }

    private static bool RequiresStructuredAnswer(SourceBackedIntake intake)
        => SourceBackedStructuredTableShapeBuilder.TryCreateFromIntake(intake) is not null;

    private static IReadOnlyList<string> FindMissingRequestedStructureAxes(string? answer, SourceBackedIntake intake)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return Array.Empty<string>();

        var axes = intake.RequestedAxes ?? Array.Empty<string>();
        var dayAxes = ExtractAxisLabels(axes, "day:");
        var slotAxes = ExtractAxisLabels(axes, "slot:");
        var rowAxes = ExtractAxisLabels(axes, "row:");
        var columnAxes = ExtractAxisLabels(axes, "column:");
        var requiredAxes = new List<string>();
        if (rowAxes.Count >= 2)
            requiredAxes.AddRange(rowAxes);
        if (columnAxes.Count >= 2)
            requiredAxes.AddRange(columnAxes);
        if (dayAxes.Count >= 2)
            requiredAxes.AddRange(dayAxes);
        if (slotAxes.Count >= 2)
            requiredAxes.AddRange(slotAxes);

        if (requiredAxes.Count == 0)
        {
            var plainAxes = axes
                .Where(static axis => !string.IsNullOrWhiteSpace(axis) && !axis.Contains(':'))
                .Select(static axis => axis.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToArray();
            if (plainAxes.Length >= 2)
                requiredAxes.AddRange(plainAxes);
        }

        if (requiredAxes.Count == 0)
            return Array.Empty<string>();

        var normalizedAnswer = NormalizeAxisText(answer);
        return requiredAxes
            .Where(axis => !AxisAppears(normalizedAnswer, axis))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> ExtractAxisLabels(IEnumerable<string> axes, string prefix)
        => axes
            .Where(axis => !string.IsNullOrWhiteSpace(axis) && axis.Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(axis => axis.Trim()[prefix.Length..].Trim())
            .Where(static axis => !string.IsNullOrWhiteSpace(axis))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();

    private static bool AxisAppears(string normalizedAnswer, string axis)
    {
        var normalizedAxis = NormalizeAxisText(axis);
        if (string.IsNullOrWhiteSpace(normalizedAxis))
            return true;

        var pattern = @"(?:^|\s)" + Regex.Escape(normalizedAxis).Replace("\\ ", @"\s+", StringComparison.Ordinal) + @"(?:$|\s)";
        return Regex.IsMatch(normalizedAnswer, pattern, RegexOptions.CultureInvariant);
    }

    private static string NormalizeAxisText(string? value)
    {
        var decomposed = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var chars = decomposed
            .Where(static c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .Select(static c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ')
            .ToArray();
        return Regex.Replace(new string(chars), @"\s+", " ", RegexOptions.CultureInvariant).Trim();
    }

    private static bool LooksStructured(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return false;

        var nonEmptyLines = answer
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0)
            .ToArray();
        if (nonEmptyLines.Length >= 4)
            return true;

        var structuredLineCount = StructuredLinePattern.Matches(answer).Count;
        if (structuredLineCount >= 3)
            return true;

        var tableLineCount = nonEmptyLines.Count(static line => line.StartsWith('|'));
        return tableLineCount >= 2;
    }
}
