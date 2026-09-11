using System.Text;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    internal static bool TryBuildStructuredCandidateAssignmentTable(
        string? rawOutput,
        EvidenceBundle bundle,
        IReadOnlyList<string> candidateEvidenceIds,
        string rowHeader,
        IReadOnlyList<string> rowLabels,
        IReadOnlyList<string> columnLabels,
        out string renderedTable,
        out IReadOnlyList<string> selectedEvidenceIds,
        out string failureReason)
    {
        renderedTable = string.Empty;
        selectedEvidenceIds = Array.Empty<string>();
        failureReason = string.Empty;
        var expectedCellCount = rowLabels.Count * columnLabels.Count;
        var assignmentLines = (rawOutput ?? string.Empty)
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0
                                  && !line.StartsWith("```", StringComparison.Ordinal))
            .ToArray();
        var assignments = assignmentLines
            .Select(line => Regex.Match(
                line,
                @"^C(\d{1,3})\s*=\s*(E\d+)\s*$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToArray();
        if (assignmentLines.Length != expectedCellCount
            || assignments.Any(static match => !match.Success))
        {
            failureReason =
                "structured_candidate_writer_assignment_count_invalid";
            return false;
        }

        var indexedAssignments = assignments
            .Select(static match => new
            {
                CellIndex = int.Parse(match.Groups[1].Value) - 1,
                EvidenceId = match.Groups[2].Value.ToUpperInvariant()
            })
            .ToArray();
        if (indexedAssignments.Select(static item => item.CellIndex)
                .Distinct().Count() != expectedCellCount
            || indexedAssignments.Any(item => item.CellIndex < 0
                                               || item.CellIndex >= expectedCellCount))
        {
            failureReason =
                "structured_candidate_writer_cell_id_invalid";
            return false;
        }
        var allowed = candidateEvidenceIds.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        if (indexedAssignments.Any(item => !allowed.Contains(item.EvidenceId)
                                               || !bundle.ById.ContainsKey(item.EvidenceId)))
        {
            failureReason =
                "structured_candidate_writer_evidence_id_invalid";
            return false;
        }
        if (indexedAssignments.Select(static item => item.EvidenceId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != expectedCellCount)
        {
            failureReason =
                "structured_candidate_writer_assignment_duplicate";
            return false;
        }

        var orderedAssignments = indexedAssignments
            .OrderBy(static item => item.CellIndex)
            .ToArray();
        var selected = orderedAssignments
            .Select(item => bundle.ById[item.EvidenceId])
            .ToArray();
        if (selected.Select(static item => item.VisibleSourceKey)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != expectedCellCount)
        {
            failureReason =
                "structured_candidate_writer_visible_source_duplicate";
            return false;
        }
        if (selected.Select(GetEvidenceDisplayValue)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != expectedCellCount)
        {
            failureReason =
                "structured_candidate_writer_display_value_duplicate";
            return false;
        }

        var shape = new SourceBackedStructuredTableShape(
            rowHeader,
            columnLabels,
            rowLabels);
        var table = new StringBuilder()
            .AppendLine(shape.HeaderLine)
            .AppendLine(shape.SeparatorLine);
        var evidenceIndex = 0;
        foreach (var rowLabel in rowLabels)
        {
            table.Append("| ").Append(rowLabel);
            foreach (var _ in columnLabels)
            {
                var evidence = selected[evidenceIndex++];
                table.Append(" | ")
                    .Append(GetEvidenceDisplayValue(evidence))
                    .Append(" [")
                    .Append(evidence.EvidenceId)
                    .Append(']');
            }
            table.AppendLine(" |");
        }

        renderedTable = table.ToString().TrimEnd();
        selectedEvidenceIds = selected
            .Select(static item => item.EvidenceId)
            .ToArray();
        return true;
    }

    internal static bool TryValidateStructuredCandidateWriterCompletion(
        SourceBackedAgentCompletion completion,
        EvidenceBundle bundle,
        IReadOnlyList<string> candidateEvidenceIds,
        int requiredEvidenceCount,
        out IReadOnlyList<string> selectedEvidenceIds,
        out string failureReason)
    {
        selectedEvidenceIds = Array.Empty<string>();
        failureReason = string.Empty;
        if (completion.ToolCalls.Count > 0)
        {
            failureReason = "structured_candidate_writer_must_not_call_tools";
            return false;
        }

        var occurrences = Regex.Matches(
                completion.Content ?? string.Empty,
                @"(?<![A-Za-z0-9])\[?(E\d{1,4})\]?(?![A-Za-z0-9])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(static match => match.Groups[1].Value.ToUpperInvariant())
            .ToArray();
        var distinct = occurrences
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (occurrences.Length != requiredEvidenceCount
            || distinct.Length != requiredEvidenceCount)
        {
            failureReason = "structured_candidate_writer_citation_count_invalid";
            return false;
        }

        var allowed = candidateEvidenceIds.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        if (distinct.Any(id => !allowed.Contains(id)
                               || !bundle.ById.ContainsKey(id)))
        {
            failureReason = "structured_candidate_writer_evidence_id_invalid";
            return false;
        }
        var selected = distinct.Select(id => bundle.ById[id]).ToArray();
        if (selected.Any(static item => !HasRenderableEvidenceValue(item)))
        {
            failureReason =
                "structured_candidate_writer_non_renderable_evidence";
            return false;
        }
        if (selected.Select(static item => item.VisibleSourceKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count()
            != selected.Length)
        {
            failureReason = "structured_candidate_writer_visible_source_duplicate";
            return false;
        }
        if (selected.Select(GetEvidenceDisplayValue)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count()
            != selected.Length)
        {
            failureReason = "structured_candidate_writer_display_value_duplicate";
            return false;
        }

        selectedEvidenceIds = distinct;
        return true;
    }

    private static string NormalizeStructuredCandidateWriterLabels(
        string? content,
        EvidenceBundle bundle,
        IReadOnlySet<string> selectedEvidenceIds,
        out int normalizedCellCount)
    {
        normalizedCellCount = 0;
        if (string.IsNullOrWhiteSpace(content))
            return content ?? string.Empty;

        var lines = content.Split(
            new[] { "\r\n", "\n" },
            StringSplitOptions.None);
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var trimmedLine = lines[lineIndex].Trim();
            if (!trimmedLine.StartsWith('|') || !trimmedLine.EndsWith('|'))
                continue;
            var cells = trimmedLine.Trim('|')
                .Split('|')
                .Select(static cell => cell.Trim())
                .ToArray();
            var changed = false;
            for (var cellIndex = 0; cellIndex < cells.Length; cellIndex++)
            {
                var ids = SourceContractVerifier.ExtractEvidenceIds(cells[cellIndex]);
                if (ids.Count != 1
                    || !selectedEvidenceIds.Contains(ids[0])
                    || !bundle.ById.TryGetValue(ids[0], out var item))
                {
                    continue;
                }
                var canonicalCell = GetEvidenceDisplayValue(item)
                                    + " ["
                                    + item.EvidenceId
                                    + "]";
                if (string.Equals(
                        cells[cellIndex],
                        canonicalCell,
                        StringComparison.Ordinal))
                {
                    continue;
                }
                cells[cellIndex] = canonicalCell;
                normalizedCellCount++;
                changed = true;
            }
            if (changed)
                lines[lineIndex] = "| " + string.Join(" | ", cells) + " |";
        }
        return string.Join(Environment.NewLine, lines);
    }

}
