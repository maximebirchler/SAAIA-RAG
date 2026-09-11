using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private const string SemanticDisplayValueHint =
        "semanticDisplayValue";

    private const string SemanticCompatibleColumnLabelsHint =
        "semanticCompatibleColumnLabels";

    private sealed record SemanticSelectionMechanicalValidation(
        HashSet<string> VisibleCitableIds,
        string[] UnknownEvidenceIds,
        string[] ResubmittedRejectedEvidenceIds,
        IReadOnlyList<DuplicateVisibleSourceSelection> DuplicateVisibleSources,
        IReadOnlyList<string> DuplicateVisibleSourceDetails,
        IReadOnlyList<DuplicateDisplayValueSelection> DuplicateDisplayValues,
        IReadOnlyList<string> DuplicateDisplayValueDetails,
        string[] NonRenderableEvidenceIds)
    {
        public bool HasFailure =>
            UnknownEvidenceIds.Length > 0
            || ResubmittedRejectedEvidenceIds.Length > 0
            || DuplicateVisibleSources.Count > 0
            || DuplicateDisplayValues.Count > 0
            || NonRenderableEvidenceIds.Length > 0;
    }

    private static SemanticSelectionMechanicalValidation
        ValidateSemanticSelectionMechanically(
            bool parsed,
            EvidenceBundle bundle,
            IReadOnlyList<string> declaredSelection,
            IEnumerable<string> selectionEligibleObservedEvidenceIds,
            ISet<string> semanticallyRejectedEvidenceIds,
            string atomicEvidenceMode)
    {
        var visibleCitableIds = BuildVisibleCitableEvidenceIdSet(
            bundle,
            selectionEligibleObservedEvidenceIds);
        var duplicateVisibleSources = parsed
                                      && !string.Equals(
                                          atomicEvidenceMode,
                                          "content_claim",
                                          StringComparison.OrdinalIgnoreCase)
            ? FindDuplicateVisibleSourceSelections(bundle, declaredSelection)
            : Array.Empty<DuplicateVisibleSourceSelection>();
        var duplicateDisplayValues = parsed
            ? FindDuplicateDisplayValueSelections(bundle, declaredSelection)
            : Array.Empty<DuplicateDisplayValueSelection>();
        return new SemanticSelectionMechanicalValidation(
            visibleCitableIds,
            FindMissingSetMembers(declaredSelection, visibleCitableIds),
            FindSetMembers(declaredSelection, semanticallyRejectedEvidenceIds),
            duplicateVisibleSources,
            DescribeDuplicateVisibleSourceSelections(duplicateVisibleSources),
            duplicateDisplayValues,
            DescribeDuplicateDisplayValueSelections(duplicateDisplayValues),
            parsed
                ? FindNonRenderableSelectionEvidenceIds(bundle, declaredSelection)
                : Array.Empty<string>());
    }

    private static SemanticLayoutDimensions? ReadSemanticLayoutDimensions(
        string semanticPlan,
        int requiredCount)
    {
        var dimensionsLine = semanticPlan
            .Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static line => line.StartsWith(
                "DIMENSIONS:",
                StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(dimensionsLine))
            return null;

        var numbers = new List<int>();
        var digits = new System.Text.StringBuilder();
        foreach (var character in dimensionsLine)
        {
            if (char.IsDigit(character))
            {
                digits.Append(character);
                continue;
            }

            if (digits.Length == 0)
                continue;
            if (int.TryParse(digits.ToString(), out var parsed))
                numbers.Add(parsed);
            digits.Clear();
        }
        if (digits.Length > 0 && int.TryParse(digits.ToString(), out var trailing))
            numbers.Add(trailing);

        if (numbers.Count < 2
            || numbers[0] is < 1 or > 80
            || numbers[1] is < 1 or > 12
            || numbers[0] * numbers[1] != requiredCount)
        {
            return null;
        }

        return new SemanticLayoutDimensions(numbers[0], numbers[1]);
    }

    private static bool TryReadSemanticSelectionLayout(
        JsonElement arguments,
        out SemanticSelectionLayout? layout,
        out string contractError)
    {
        layout = null;
        contractError = string.Empty;
        if (!TryGetPropertyIgnoreCase(arguments, "layout", out var layoutElement)
            || layoutElement.ValueKind != JsonValueKind.Object)
        {
            contractError = "selection_layout_missing_or_not_object";
            return false;
        }

        var rowHeader = GetString(layoutElement, "rowHeader")?.Trim();
        if (!IsValidLayoutLabel(rowHeader))
        {
            contractError = "selection_layout_row_header_invalid";
            return false;
        }
        if (!TryReadLayoutLabels(layoutElement, "columns", 12, out var columns)
            || columns.Count == 0)
        {
            contractError = "selection_layout_columns_invalid";
            return false;
        }
        if (!TryReadLayoutLabels(layoutElement, "rows", 80, out var rowLabels)
            || rowLabels.Count == 0)
        {
            contractError = "selection_layout_rows_invalid";
            return false;
        }

        layout = new SemanticSelectionLayout(rowHeader!, columns, rowLabels);
        return true;
    }

    private static bool TryReadLayoutLabels(
        JsonElement root,
        string propertyName,
        int maximumCount,
        out IReadOnlyList<string> labels)
    {
        labels = Array.Empty<string>();
        if (!TryGetPropertyIgnoreCase(root, propertyName, out var element)
            || element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            var label = item.ValueKind == JsonValueKind.String
                ? item.GetString()?.Trim()
                : null;
            if (!IsValidLayoutLabel(label))
                return false;
            values.Add(label!);
        }

        if (values.Count > maximumCount
            || values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Count)
        {
            return false;
        }

        labels = values;
        return true;
    }

    private static bool TryReadLayoutEvidenceIds(
        JsonElement row,
        out IReadOnlyList<string> evidenceIds)
    {
        evidenceIds = Array.Empty<string>();
        if (!TryGetPropertyIgnoreCase(row, "evidenceIds", out var idsElement)
            || idsElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var values = new List<string>();
        foreach (var item in idsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                return false;
            var normalized = NormalizeSemanticSelectionEvidenceId(item.GetString());
            if (normalized is null)
                return false;
            values.Add(normalized);
        }

        if (values.Count > 80)
        {
            return false;
        }

        evidenceIds = values;
        return true;
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement root,
        string propertyName,
        out JsonElement value)
    {
        value = default;
        if (root.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(
                    property.Name,
                    propertyName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            value = property.Value;
            return true;
        }

        return false;
    }

    private static bool IsValidLayoutLabel(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Length <= 80
           && !value.Contains('\r')
           && !value.Contains('\n');

    private static string? NormalizeSemanticSelectionEvidenceId(string? raw)
    {
        var value = raw?.Trim().Trim('[', ']') ?? string.Empty;
        if (value.Length < 2
            || char.ToUpperInvariant(value[0]) != 'E'
            || !value[1..].All(char.IsDigit)
            || !int.TryParse(value[1..], out var number)
            || number <= 0)
        {
            return null;
        }

        return "E" + number;
    }

    private static bool HasRenderableEvidenceValue(EvidenceItem item)
    {
        var displayValue = GetEvidenceDisplayValue(item);
        return SourceContractVerifier.HasSubstantiveVisibleValue(displayValue)
               && !string.Equals(
                   displayValue.Trim().Trim('[', ']'),
                   item.EvidenceId,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string RenderSemanticSelectionLayout(
        EvidenceBundle bundle,
        SemanticSelectionLayout layout,
        IReadOnlyList<string> evidenceIds)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("| ")
            .Append(EscapeMarkdownTableCell(layout.RowHeader));
        foreach (var column in layout.Columns)
            builder.Append(" | ").Append(EscapeMarkdownTableCell(column));
        builder.AppendLine(" |");
        builder.Append("| ---");
        foreach (var _ in layout.Columns)
            builder.Append(" | ---");
        builder.AppendLine(" |");
        var evidenceIndex = 0;
        foreach (var rowLabel in layout.RowLabels)
        {
            builder.Append("| ").Append(EscapeMarkdownTableCell(rowLabel));
            foreach (var _ in layout.Columns)
            {
                var evidenceId = evidenceIds[evidenceIndex++];
                var displayValue = bundle.ById.TryGetValue(evidenceId, out var item)
                    ? GetEvidenceDisplayValue(item)
                    : evidenceId;
                builder.Append(" | ")
                    .Append(EscapeMarkdownTableCell(displayValue))
                    .Append(" [")
                    .Append(evidenceId)
                    .Append(']');
            }
            builder.AppendLine(" |");
        }

        return builder.ToString().Trim();
    }

    private static IReadOnlyList<DuplicateVisibleSourceSelection>
        FindDuplicateVisibleSourceSelections(
            EvidenceBundle bundle,
            IReadOnlyList<string> evidenceIds)
    {
        var firstEvidenceByVisibleSource = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        var duplicates = new List<DuplicateVisibleSourceSelection>();
        foreach (var evidenceId in evidenceIds)
        {
            if (!bundle.ById.TryGetValue(evidenceId, out var item)
                || (string.IsNullOrWhiteSpace(item.DocPath)
                    && string.IsNullOrWhiteSpace(item.DocName))
                || item.PageStart is not > 0)
            {
                continue;
            }

            if (firstEvidenceByVisibleSource.TryAdd(
                    item.VisibleSourceKey,
                    evidenceId))
            {
                continue;
            }

            var sourceName = item.DocName ?? item.DocPath ?? "?";
            var sourceLabel = $"{sourceName}|page={item.PageStart}";
            duplicates.Add(new DuplicateVisibleSourceSelection(
                firstEvidenceByVisibleSource[item.VisibleSourceKey],
                evidenceId,
                sourceLabel));
        }

        return duplicates;
    }

    private static IReadOnlyList<DuplicateDisplayValueSelection>
        FindDuplicateDisplayValueSelections(
            EvidenceBundle bundle,
            IReadOnlyList<string> evidenceIds)
    {
        var firstEvidenceByDisplayValue = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        var duplicates = new List<DuplicateDisplayValueSelection>();
        foreach (var evidenceId in evidenceIds)
        {
            if (!bundle.ById.TryGetValue(evidenceId, out var item))
                continue;
            var displayValue = GetEvidenceDisplayValue(item);
            if (string.IsNullOrWhiteSpace(displayValue))
                continue;
            if (firstEvidenceByDisplayValue.TryAdd(displayValue, evidenceId))
                continue;
            duplicates.Add(new DuplicateDisplayValueSelection(
                firstEvidenceByDisplayValue[displayValue],
                evidenceId,
                displayValue));
        }

        return duplicates;
    }

    private static string GetEvidenceDisplayValue(EvidenceItem item)
    {
        if (item.SelectionHints.TryGetValue(
                SemanticDisplayValueHint,
                out var semanticDisplayValue)
            && !string.IsNullOrWhiteSpace(semanticDisplayValue))
        {
            return TrimDisplayValue(semanticDisplayValue, 160);
        }

        if (item.SelectionHints.TryGetValue(
                "sourceAnchorLabel",
                out var sourceAnchorLabel)
            && !string.IsNullOrWhiteSpace(sourceAnchorLabel))
        {
            return TrimDisplayValue(sourceAnchorLabel, 160);
        }

        if (item.MatchedContentCards is { } cards
            && cards.ValueKind == JsonValueKind.Array)
        {
            foreach (var card in cards.EnumerateArray())
            {
                var title = GetString(card, "title", "name", "label");
                if (!string.IsNullOrWhiteSpace(title))
                    return TrimDisplayValue(title, 160);
            }
        }

        var excerpt = item.Excerpt?.Trim() ?? string.Empty;
        var sentenceEnd = excerpt.IndexOf(". ", StringComparison.Ordinal);
        if (sentenceEnd > 0)
            excerpt = excerpt[..sentenceEnd];
        return TrimDisplayValue(excerpt, 160);
    }

    private static string TrimDisplayValue(string value, int maximumLength)
    {
        var normalized = string.Join(
            " ",
            value.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength].TrimEnd() + "…";
    }

    private static string EscapeMarkdownTableCell(string value)
        => value
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
}
