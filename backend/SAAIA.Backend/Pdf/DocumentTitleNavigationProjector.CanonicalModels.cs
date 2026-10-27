internal sealed record CanonicalNavigationSourceEvidence(
    string SourceTableId,
    int SourceTableOrdinal,
    int SourceRowIndex,
    IReadOnlyList<string> SourceCellIds,
    IReadOnlyList<int> SourceChunkIndices,
    string PrintedPageLabel,
    string PrintedPageLabelKind,
    string? TargetCanonicalBlockId);

internal sealed record CanonicalNavigationDiagnostics(
    int StructuredChunksRecognized,
    int TablesExamined,
    int RowsExamined,
    int EntriesProduced,
    int EntriesResolved,
    int EntriesUnresolved,
    int RowsRefused,
    bool LimitReached,
    IReadOnlyDictionary<string, int> ReasonCounts)
{
    public string SchemaVersion { get; init; } = "canonical_navigation_v1";
    public string Availability { get; init; } = "observed";
    public bool SourceChunkInventoryComplete { get; init; } = true;
}
