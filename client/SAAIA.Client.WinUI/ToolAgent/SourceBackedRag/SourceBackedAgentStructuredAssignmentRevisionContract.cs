using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    internal static bool TryApplyStructuredAssignmentRevision(
        WriterDraft rejectedDraft,
        SourceBackedStructuredTableShape shape,
        IReadOnlyList<int> rejectedIndexes,
        IReadOnlyList<string> availableEvidenceIds,
        IReadOnlyDictionary<int, string> forbiddenEvidenceByCell,
        EvidenceBundle bundle,
        string rawOutput,
        out string revisedContent,
        out string failureReason)
    {
        revisedContent = rejectedDraft.Answer;
        failureReason = string.Empty;
        var assignments = Regex.Matches(
                (rawOutput ?? string.Empty).Trim().Trim('`', ' ', '\r', '\n'),
                StructuredAssignmentLinePattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(static match => new
            {
                Index = int.Parse(match.Groups[1].Value) - 1,
                EvidenceId = match.Groups[2].Value.ToUpperInvariant()
            })
            .ToArray();
        var expectedIndexes = rejectedIndexes.ToHashSet();
        var allowedEvidenceIds = availableEvidenceIds.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        if (assignments.Length != rejectedIndexes.Count)
        {
            failureReason =
                "Le patch CXX=E# ne contient pas exactement une ligne par cellule refusee: "
                + $"attendu={rejectedIndexes.Count}, analyse={assignments.Length}.";
            return false;
        }

        var duplicatedCellIndexes = assignments
            .GroupBy(static assignment => assignment.Index)
            .Where(static group => group.Count() > 1)
            .Select(static group => StructuredCellKey(group.Key))
            .ToArray();
        if (duplicatedCellIndexes.Length > 0)
        {
            failureReason =
                "Le patch CXX=E# affecte plusieurs fois les cellules: "
                + string.Join(", ", duplicatedCellIndexes) + ".";
            return false;
        }

        var unexpectedCellIndexes = assignments
            .Where(assignment => !expectedIndexes.Contains(assignment.Index))
            .Select(static assignment => StructuredCellKey(assignment.Index))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unexpectedCellIndexes.Length > 0)
        {
            failureReason =
                "Le patch CXX=E# modifie des cellules qui ne sont pas refusees: "
                + string.Join(", ", unexpectedCellIndexes) + ".";
            return false;
        }

        var duplicatedEvidenceIds = assignments
            .GroupBy(
                static assignment => assignment.EvidenceId,
                StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToArray();
        if (duplicatedEvidenceIds.Length > 0)
        {
            failureReason =
                "Le patch CXX=E# duplique les candidats: "
                + string.Join(", ", duplicatedEvidenceIds) + ".";
            return false;
        }

        var unavailableEvidenceIds = assignments
            .Where(assignment => !allowedEvidenceIds.Contains(
                assignment.EvidenceId))
            .Select(static assignment => assignment.EvidenceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unavailableEvidenceIds.Length > 0)
        {
            failureReason =
                "Le patch CXX=E# utilise des candidats hors du pool autorise: "
                + string.Join(", ", unavailableEvidenceIds) + ".";
            return false;
        }

        var forbiddenAssignments = assignments
            .Where(assignment =>
                forbiddenEvidenceByCell.TryGetValue(
                    assignment.Index,
                    out var forbiddenEvidenceId)
                && string.Equals(
                    forbiddenEvidenceId,
                    assignment.EvidenceId,
                    StringComparison.OrdinalIgnoreCase))
            .Select(static assignment =>
                StructuredCellKey(assignment.Index) + "=" + assignment.EvidenceId)
            .ToArray();
        if (forbiddenAssignments.Length > 0)
        {
            failureReason =
                "Le patch CXX=E# reaffecte la preuve refusee a la meme cellule: "
                + string.Join(", ", forbiddenAssignments) + ".";
            return false;
        }

        if (!TryReadStructuredTable(
                rejectedDraft.Answer,
                shape,
                out var revisedLines,
                out var revisedRows,
                out var columnIndexes))
        {
            failureReason = "Le tableau precedent ne peut pas etre aligne sur ses axes.";
            return false;
        }
        var assignmentByIndex = assignments.ToDictionary(
            static assignment => assignment.Index,
            static assignment => assignment.EvidenceId);
        var cellIndex = 0;
        foreach (var rowLabel in shape.RowLabels)
        {
            var normalizedRow = SourceBackedStructuredTableShapeBuilder.NormalizeLabel(rowLabel);
            if (!revisedRows.TryGetValue(normalizedRow, out var revisedRow))
            {
                failureReason = "Une ligne attendue manque dans le tableau precedent.";
                return false;
            }
            foreach (var columnHeader in shape.ColumnHeaders)
            {
                if (assignmentByIndex.TryGetValue(cellIndex, out var evidenceId))
                {
                    var normalizedColumn = SourceBackedStructuredTableShapeBuilder
                        .NormalizeLabel(columnHeader);
                    if (!columnIndexes.TryGetValue(normalizedColumn, out var columnIndex)
                        || columnIndex >= revisedRow.Cells.Length
                        || !bundle.ById.TryGetValue(evidenceId, out var evidence))
                    {
                        failureReason =
                            "Une cellule ou une preuve du patch ne peut pas etre resolue.";
                        return false;
                    }
                    revisedRow.Cells[columnIndex] =
                        GetEvidenceDisplayValue(evidence) + " [" + evidence.EvidenceId + "]";
                }
                cellIndex++;
            }
        }
        foreach (var row in revisedRows.Values)
            revisedLines[row.LineIndex] = "| " + string.Join(" | ", row.Cells) + " |";
        var newline = rejectedDraft.Answer.Contains("\r\n", StringComparison.Ordinal)
            ? "\r\n"
            : "\n";
        revisedContent = string.Join(newline, revisedLines);
        return true;
    }

    private static bool TryReadStructuredTable(
        string content,
        SourceBackedStructuredTableShape shape,
        out string[] lines,
        out Dictionary<string, (int LineIndex, string[] Cells)> rows,
        out Dictionary<string, int> columnIndexes)
    {
        lines = (content ?? string.Empty).Split(
            new[] { "\r\n", "\n" },
            StringSplitOptions.None);
        rows = new Dictionary<string, (int, string[])>(
            StringComparer.OrdinalIgnoreCase);
        columnIndexes = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase);
        var headerIndex = -1;
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var cells = SplitStructuredMarkdownRow(lines[lineIndex]);
            if (cells.Length == 0)
                continue;
            var normalizedCells = cells
                .Select(SourceBackedStructuredTableShapeBuilder.NormalizeLabel)
                .ToArray();
            if (!normalizedCells.Contains(
                    SourceBackedStructuredTableShapeBuilder.NormalizeLabel(
                        shape.FirstColumnHeader),
                    StringComparer.OrdinalIgnoreCase)
                || shape.ColumnHeaders.Any(column => !normalizedCells.Contains(
                    SourceBackedStructuredTableShapeBuilder.NormalizeLabel(column),
                    StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }
            headerIndex = lineIndex;
            for (var columnIndex = 0; columnIndex < normalizedCells.Length; columnIndex++)
                columnIndexes[normalizedCells[columnIndex]] = columnIndex;
            break;
        }

        if (headerIndex < 0)
            return false;
        var expectedRows = shape.RowLabels
            .Select(SourceBackedStructuredTableShapeBuilder.NormalizeLabel)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var lineIndex = headerIndex + 1; lineIndex < lines.Length; lineIndex++)
        {
            var cells = SplitStructuredMarkdownRow(lines[lineIndex]);
            if (cells.Length == 0)
                continue;
            var normalizedRow = SourceBackedStructuredTableShapeBuilder
                .NormalizeLabel(cells[0]);
            if (expectedRows.Contains(normalizedRow))
                rows[normalizedRow] = (lineIndex, cells);
        }
        return rows.Count == expectedRows.Count;
    }

    private static string[] SplitStructuredMarkdownRow(string line)
    {
        var trimmed = (line ?? string.Empty).Trim();
        if (!trimmed.StartsWith('|') || !trimmed.Contains('|'))
            return Array.Empty<string>();
        return trimmed.Trim('|')
            .Split('|', StringSplitOptions.None)
            .Select(static cell => cell.Trim())
            .ToArray();
    }
}
