namespace SAAIA.Contracts;

/// <summary>An exact captured native line, independent of retrieval chunks and table observations.</summary>
public sealed class AdvancedAnalysisNativeObservationReference
{
    public string BlockId { get; init; } = string.Empty;
    public string RawTextSha256 { get; init; } = string.Empty;
    public int PhysicalPage { get; init; }
    public string LineId { get; init; } = string.Empty;
    public int LineOrdinal { get; init; }
    public string LineTextSha256 { get; init; } = string.Empty;
    public int UnicodeScalarStart { get; init; }
    public int UnicodeScalarEndExclusive { get; init; }
}
