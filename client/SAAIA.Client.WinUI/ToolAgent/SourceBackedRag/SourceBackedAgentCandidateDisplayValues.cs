namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private const string NamedSourceItemClassification =
        "named_source_item";
    private const string AxisOrRoleClassification =
        "axis_or_role";
    private const string CategoryOrCollectionClassification =
        "category_or_collection";
    private const string InstructionOrFragmentClassification =
        "instruction_or_fragment";
    private const string NotNamedSourceItemClassification =
        "not_named_source_item";
    private const string OtherWrongTypeClassification =
        "other_wrong_type";

    private static IReadOnlyList<string> BuildCandidateDisplayValueOptions(
        EvidenceItem item)
    {
        var values = new List<string>();
        if (item.SelectionHints.TryGetValue(
                "sourceAnchorLabel",
                out var sourceAnchorLabel))
        {
            AddCandidateDisplayValueOption(values, sourceAnchorLabel);
        }
        AddCandidateDisplayValueOption(
            values,
            GetEvidenceDisplayValue(item));
        if (item.SelectionHints.TryGetValue(
                "headingPath",
                out var headingPath)
            && !string.IsNullOrWhiteSpace(headingPath))
        {
            foreach (var segment in headingPath.Split(
                         " > ",
                         StringSplitOptions.RemoveEmptyEntries
                         | StringSplitOptions.TrimEntries)
                     .Reverse())
            {
                AddCandidateDisplayValueOption(values, segment);
            }
        }
        return values;
    }

    private static void AddCandidateDisplayValueOption(
        ICollection<string> values,
        string? value)
    {
        var normalized = TrimDisplayValue(value ?? string.Empty, 160);
        if (normalized.Length == 0
            || values.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        values.Add(normalized);
    }
}
