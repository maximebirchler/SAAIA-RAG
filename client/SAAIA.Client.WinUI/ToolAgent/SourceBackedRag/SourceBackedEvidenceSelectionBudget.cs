namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

internal static class SourceBackedEvidenceSelectionBudget
{
    internal const int DefaultSmallAnswerMaxEvidenceIds = 6;
    internal const int DefaultBroadAnswerMaxEvidenceIds = 8;
    internal const int StructuredAnswerMinimumSelectionCapacity = 8;
    internal const int StructuredAnswerMaxEvidenceIds = 16;

    internal static int ForWriting(SourceBackedIntake intake)
    {
        var tableShape = SourceBackedStructuredTableShapeBuilder.TryCreateFromIntake(intake);
        if (tableShape is not null)
        {
            var requestedCellCount = tableShape.RowLabels.Count * tableShape.ColumnHeaders.Count;
            return Math.Clamp(
                requestedCellCount,
                StructuredAnswerMinimumSelectionCapacity,
                StructuredAnswerMaxEvidenceIds);
        }

        return intake.RequestedAxes.Count >= 6
            ? DefaultBroadAnswerMaxEvidenceIds
            : DefaultSmallAnswerMaxEvidenceIds;
    }

    internal static int MinForWriting(
        SourceBackedIntake intake,
        EvidenceBundle bundle,
        IReadOnlyCollection<string>? ignoredEvidenceIds = null)
    {
        _ = intake;
        _ = bundle;
        _ = ignoredEvidenceIds;

        // Source usefulness and the size of the selected set are semantic LLM decisions.
        // Code only requires a non-empty, known selection and verifies the written output.
        return 1;
    }
}
