using System.Text.Json.Serialization;

internal sealed record CanonicalNavigationSourceEvidence(
    string SourceTableId,
    int SourceTableOrdinal,
    int SourceRowIndex,
    IReadOnlyList<string> SourceCellIds,
    IReadOnlyList<int> SourceChunkIndices,
    string PrintedPageLabel,
    string PrintedPageLabelKind,
    string? TargetCanonicalBlockId)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CanonicalNavigationNativeTitleEvidence? NativeTitleCorroboration { get; init; }
}

internal sealed record CanonicalNavigationNativeRunSlice(string RunId, string TextSha256, int StartUtf16, int LengthUtf16);

internal sealed record CanonicalNavigationNativeTitleEvidence(
    string SchemaVersion, int SourcePhysicalPage, string SourceSha256, string TitleSha256,
    string Comparison, string GeometryBasis, string GeometryWarning, string PrintedPageLabelCorroboration,
    IReadOnlyList<CanonicalNavigationNativeRunSlice> RunSlices);

internal sealed record CanonicalNavigationNativeTitleDiagnostics(
    int Candidates, int Links, int Refused, int PagesValidated, long ScanUnitsReserved,
    IReadOnlyDictionary<string, int> ReasonCounts);

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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CanonicalNavigationNativeTitleDiagnostics? NativeTitleCorroboration { get; init; }
}
