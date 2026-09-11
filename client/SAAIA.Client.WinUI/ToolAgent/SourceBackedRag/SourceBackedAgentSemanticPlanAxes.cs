using System.Collections.ObjectModel;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static bool TryReadSemanticPlanAxes(
        JsonElement arguments,
        bool structuredLayout,
        int expectedRowCount,
        int expectedColumnCount,
        out string rowHeader,
        out IReadOnlyList<string> rowLabels,
        out IReadOnlyDictionary<string, string> columnRoles,
        out string contractError)
    {
        rowHeader = NormalizeSemanticPlanLine(GetString(arguments, "rowHeader"));
        rowLabels = Array.Empty<string>();
        columnRoles = EmptySemanticColumnRoles();
        contractError = string.Empty;
        if (!TryGetPropertyIgnoreCase(arguments, "rowLabels", out var rowLabelsElement)
            || rowLabelsElement.ValueKind != JsonValueKind.Array
            || !TryGetPropertyIgnoreCase(arguments, "columns", out var columnsElement)
            || columnsElement.ValueKind != JsonValueKind.Array)
        {
            contractError = "semantic_plan_axes_missing";
            return false;
        }

        if (!structuredLayout)
        {
            if (rowHeader.Length != 0
                || rowLabelsElement.GetArrayLength() != 0
                || columnsElement.GetArrayLength() != 0)
            {
                contractError = "semantic_plan_unstructured_axes_must_be_empty";
                return false;
            }

            return true;
        }
        if (!IsValidLayoutLabel(rowHeader))
        {
            contractError = "semantic_plan_row_header_invalid";
            return false;
        }

        var parsedRowLabels = rowLabelsElement
            .EnumerateArray()
            .Select(static item => item.ValueKind == JsonValueKind.String
                ? NormalizeSemanticPlanLine(item.GetString())
                : string.Empty)
            .ToArray();
        if (parsedRowLabels.Length != expectedRowCount
            || parsedRowLabels.Any(static label => !IsValidLayoutLabel(label))
            || parsedRowLabels.Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != parsedRowLabels.Length)
        {
            contractError = "semantic_plan_row_labels_invalid";
            return false;
        }

        var normalizedRowHeader = rowHeader;
        var parsedColumns = columnsElement
            .EnumerateArray()
            .Select(static item => item.ValueKind == JsonValueKind.String
                ? NormalizeSemanticPlanLine(item.GetString())
                : string.Empty)
            .ToArray();
        if (parsedColumns.Length != expectedColumnCount
            || parsedColumns.Any(static label => !IsValidLayoutLabel(label))
            || parsedColumns.Any(label => string.Equals(
                label,
                normalizedRowHeader,
                StringComparison.OrdinalIgnoreCase))
            || parsedColumns.Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != parsedColumns.Length)
        {
            contractError = "semantic_plan_columns_invalid";
            return false;
        }

        rowLabels = parsedRowLabels;
        columnRoles = new ReadOnlyDictionary<string, string>(
            parsedColumns.ToDictionary(
                static label => label,
                static label => label,
                StringComparer.OrdinalIgnoreCase));
        return true;
    }

    private static IReadOnlyDictionary<string, string> EmptySemanticColumnRoles()
        => new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
}
