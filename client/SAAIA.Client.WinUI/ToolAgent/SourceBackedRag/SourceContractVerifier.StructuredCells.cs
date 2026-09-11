using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public static partial class SourceContractVerifier
{
    internal static int CountIncompleteRequestedMarkdownTableCells(
        string? answer,
        SourceBackedIntake intake)
        => FindIncompleteRequestedMarkdownTableCells(answer, intake).Count;

    internal static int CountCitationBackedIncompleteRequestedMarkdownTableCells(
        string? answer,
        SourceBackedIntake intake)
    {
        var shape = SourceBackedStructuredTableShapeBuilder.TryCreateFromIntake(intake);
        if (shape is null || string.IsNullOrWhiteSpace(answer))
            return 0;

        var rows = ExtractMarkdownRows(answer);
        if (rows.Count < 3)
            return 0;

        var header = rows[0];
        var dataRows = rows
            .Skip(1)
            .Where(row => !IsSeparatorRow(row))
            .ToArray();
        if (dataRows.Length == 0)
            return 0;

        var columnIndexes = BuildColumnIndexes(header, shape);
        var rowByLabel = dataRows
            .Where(static row => row.Count > 0)
            .GroupBy(row => SourceBackedStructuredTableShapeBuilder.NormalizeLabel(row[0]))
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);

        var count = 0;
        foreach (var rowLabel in shape.RowLabels)
        {
            if (!rowByLabel.TryGetValue(SourceBackedStructuredTableShapeBuilder.NormalizeLabel(rowLabel), out var row))
                continue;

            foreach (var column in shape.ColumnHeaders)
            {
                if (!columnIndexes.TryGetValue(SourceBackedStructuredTableShapeBuilder.NormalizeLabel(column), out var columnIndex))
                    continue;

                var cell = columnIndex < row.Count ? row[columnIndex] : string.Empty;
                if (IsEmptyOrPlaceholderCell(cell) && ExtractEvidenceIds(cell).Count > 0)
                    count++;
            }
        }

        return count;
    }

    internal static int CountRequestedStructuredTableCells(SourceBackedIntake intake)
    {
        var shape = SourceBackedStructuredTableShapeBuilder.TryCreateFromIntake(intake);
        return shape is null ? 0 : shape.RowLabels.Count * shape.ColumnHeaders.Count;
    }

    internal static IReadOnlyList<SourceBackedStructuredCellClaim> ExtractRequestedStructuredCellClaims(
        string? answer,
        SourceBackedIntake intake)
    {
        var shape = SourceBackedStructuredTableShapeBuilder.TryCreateFromIntake(intake);
        if (shape is null || string.IsNullOrWhiteSpace(answer))
            return Array.Empty<SourceBackedStructuredCellClaim>();

        var rows = ExtractMarkdownRows(answer);
        if (rows.Count < 3)
            return Array.Empty<SourceBackedStructuredCellClaim>();

        var header = rows[0];
        var dataRows = rows
            .Skip(1)
            .Where(row => !IsSeparatorRow(row))
            .ToArray();
        var columnIndexes = BuildColumnIndexes(header, shape);
        var rowByLabel = dataRows
            .Where(static row => row.Count > 0)
            .GroupBy(row => SourceBackedStructuredTableShapeBuilder.NormalizeLabel(row[0]))
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);

        var claims = new List<SourceBackedStructuredCellClaim>();
        var claimIndex = 0;
        foreach (var rowLabel in shape.RowLabels)
        {
            if (!rowByLabel.TryGetValue(SourceBackedStructuredTableShapeBuilder.NormalizeLabel(rowLabel), out var row))
                continue;

            foreach (var column in shape.ColumnHeaders)
            {
                if (!columnIndexes.TryGetValue(SourceBackedStructuredTableShapeBuilder.NormalizeLabel(column), out var columnIndex)
                    || columnIndex >= row.Count)
                {
                    continue;
                }

                var cell = row[columnIndex];
                if (IsEmptyOrPlaceholderCell(cell))
                    continue;

                claimIndex++;
                claims.Add(new SourceBackedStructuredCellClaim(
                    $"C{claimIndex:00}",
                    rowLabel,
                    column,
                    ExtractStructuredCellClaimText(cell),
                    ExtractEvidenceIds(cell)));
            }
        }

        return claims;
    }

    private static IReadOnlyList<string> FindIncompleteRequestedMarkdownTableCells(
        string? answer,
        SourceBackedIntake intake)
    {
        var shape = SourceBackedStructuredTableShapeBuilder.TryCreateFromIntake(intake);
        if (shape is null || string.IsNullOrWhiteSpace(answer))
            return Array.Empty<string>();

        var rows = ExtractMarkdownRows(answer);
        if (rows.Count < 3)
            return Array.Empty<string>();

        var header = rows[0];
        var dataRows = rows
            .Skip(1)
            .Where(row => !IsSeparatorRow(row))
            .ToArray();
        if (dataRows.Length == 0)
            return Array.Empty<string>();

        var columnIndexes = BuildColumnIndexes(header, shape);
        var rowByLabel = dataRows
            .Where(static row => row.Count > 0)
            .GroupBy(row => SourceBackedStructuredTableShapeBuilder.NormalizeLabel(row[0]))
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);

        var incomplete = new List<string>();
        foreach (var rowLabel in shape.RowLabels)
        {
            if (!rowByLabel.TryGetValue(SourceBackedStructuredTableShapeBuilder.NormalizeLabel(rowLabel), out var row))
            {
                incomplete.Add($"{rowLabel}: missing row");
                continue;
            }

            foreach (var column in shape.ColumnHeaders)
            {
                if (!columnIndexes.TryGetValue(SourceBackedStructuredTableShapeBuilder.NormalizeLabel(column), out var columnIndex))
                    continue;

                var cell = columnIndex < row.Count ? row[columnIndex] : string.Empty;
                if (IsEmptyOrPlaceholderCell(cell))
                    incomplete.Add($"{rowLabel}/{column}");
            }
        }

        return incomplete
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<IReadOnlyList<string>> ExtractMarkdownRows(string answer)
        => answer
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Select(static line => line.Trim())
            .Where(static line => line.StartsWith('|') && line.Contains('|'))
            .Select(SplitMarkdownRow)
            .Where(static row => row.Count > 0)
            .ToArray();

    private static IReadOnlyList<string> SplitMarkdownRow(string line)
        => line.Trim().Trim('|')
            .Split('|')
            .Select(static cell => cell.Trim())
            .ToArray();

    private static bool IsSeparatorRow(IReadOnlyList<string> row)
        => row.All(static cell => MarkdownTableSeparatorPattern.IsMatch(cell.Trim()));

    private static Dictionary<string, int> BuildColumnIndexes(
        IReadOnlyList<string> header,
        SourceBackedStructuredTableShape shape)
    {
        var indexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < header.Count; index++)
        {
            var normalized = SourceBackedStructuredTableShapeBuilder.NormalizeLabel(header[index]);
            if (!string.IsNullOrWhiteSpace(normalized))
                indexes[normalized] = index;
        }

        for (var columnIndex = 0; columnIndex < shape.ColumnHeaders.Count; columnIndex++)
        {
            var normalized = SourceBackedStructuredTableShapeBuilder.NormalizeLabel(shape.ColumnHeaders[columnIndex]);
            indexes.TryAdd(normalized, columnIndex + 1);
        }

        return indexes;
    }

    private static bool IsEmptyOrPlaceholderCell(string cell)
    {
        var trimmed = cell.Trim();
        if (trimmed.Length == 0)
            return true;

        var withoutEvidenceIds = EvidenceIdPattern.Replace(trimmed, " ");
        var normalized = Regex.Replace(withoutEvidenceIds, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
        if (normalized.Length == 0)
            return true;

        var placeholder = normalized.Trim('.', '…', '-', '_', ' ', '\t').Trim();
        return placeholder.Length == 0
               || string.Equals(normalized, "n/a", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, "na", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, "null", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractStructuredCellClaimText(string cell)
    {
        var text = EvidenceIdPattern.Replace(cell, " ");
        text = SourceLocatorPattern.Replace(text, " ");
        return Regex.Replace(text, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
    }

}
