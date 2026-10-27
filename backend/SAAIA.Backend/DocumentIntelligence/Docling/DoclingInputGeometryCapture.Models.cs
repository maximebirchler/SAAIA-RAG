using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class DoclingInputGeometryRefusal(string code) : Exception(code);
internal sealed class DoclingInputGeometryBudget(Func<long>? clock = null, long? maximumTicks = null)
{
    private readonly Func<long> now = clock ?? Stopwatch.GetTimestamp;
    private readonly long maximum = maximumTicks ?? 5 * Stopwatch.Frequency;
    private long? activeStarted;
    internal long UsedTicks { get; private set; }
    internal double Milliseconds => UsedTicks * 1000d / Stopwatch.Frequency;

    internal IDisposable Work(CancellationToken ct)
    {
        Check(ct);
        if (activeStarted.HasValue)
            throw new InvalidOperationException("geometry_budget_nested_scope");
        activeStarted = now();
        return new Scope(this, activeStarted.Value);
    }

    internal void Check(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (UsedTicks + (activeStarted is { } started ? Math.Max(0, now() - started) : 0) >= maximum)
            throw new DoclingInputGeometryRefusal("observer_budget_exceeded");
    }

    private sealed class Scope(DoclingInputGeometryBudget owner, long started) : IDisposable
    {
        public void Dispose()
        {
            owner.UsedTicks = checked(owner.UsedTicks + Math.Max(0, owner.now() - started));
            owner.activeStarted = null;
        }
    }
}

internal sealed record DoclingInputGeometryToken(string Pointer, string Kind, string? RawToken, string? DecodedString, long Offset, int Length);
internal sealed class DoclingInputGeometryFields
{
    public Dictionary<string, DoclingInputGeometryToken> Fields { get; } = new(StringComparer.Ordinal);
}

internal sealed class DoclingInputGeometryTable
{
    public int ArrayIndex { get; init; }
    public DoclingInputGeometryFields Values { get; } = new();
    public List<DoclingInputGeometryFields> Provenance { get; } = [];
    public List<DoclingInputGeometryFields> Cells { get; } = [];
}

internal sealed record DoclingInputGeometryLink(int SourceTableArrayIndex, string TableId, int PhysicalPage, int ProvenanceIndex, int CellIndex, string CellId, string TextSha256, string? CoordinateOrigin, Dictionary<string, string> InputDoubleBits, string[][]? OutputPointBits);
internal sealed class DoclingInputGeometrySnapshot
{
    public DoclingInputGeometryLimits Limits { get; } = new();
    public string SchemaVersion { get; } = "saaia-docling-projection-input-geometry.v1";
    public string CapturePoint { get; } = "final_http_entity_after_httpclient_decompression_before_backend_numeric_conversion";
    public string UploadBasis { get; } = "multipart_pdf_part_successful_serialization_same_attempt";
    public string NormalizerVersion { get; } = "minmax-origin-divide-clamp.v1";
    public string Availability { get; set; } = "unavailable";
    public string? Reason { get; set; }
    public string? SerializedPdfSha256 { get; set; }
    public long? SerializedPdfBytes { get; set; }
    public string? ResponseSha256 { get; set; }
    public long? ResponseBytes { get; set; }
    public int Attempt { get; init; }
    public int Serializations { get; set; }
    public bool SourceInventoryComplete { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? DocumentId { get; set; }
    public Guid? RevisionId { get; set; }
    public Guid? ProcessingRunId { get; set; }
    public string? ExpectedSourceSha256 { get; set; }
    public string? BackendCodeRevision { get; set; }
    public string? ConfiguredDeploymentRevision { get; set; }
    public string DeploymentRevisionBasis { get; } = "declared_configuration";
    public Dictionary<string, string>? ReportedPackageVersions { get; set; }
    public int? PageCount { get; set; }
    public int? TableCount { get; set; }
    public int? CellCount { get; set; }
    public int? OmittedCount { get; set; }
    public double ObserverMilliseconds { get; set; }
    public string ObserverMillisecondsBasis { get; } = "before_payload_serialization_and_gzip";
    public List<DoclingInputGeometryToken> Tokens { get; } = [];
    public Dictionary<string, DoclingInputGeometryFields> Pages { get; } = new(StringComparer.Ordinal);
    public List<DoclingInputGeometryTable> Tables { get; } = [];
    public List<DoclingInputGeometryLink> Links { get; } = [];
}

internal sealed class DoclingInputGeometryLimits
{
    public int ResponseEntityBytes { get; } = DoclingInputGeometryCapture.MaximumEntityBytes;
    public int Pages { get; } = 1024;
    public int Tables { get; } = 1024;
    public int Cells { get; } = 100000;
    public int ProjectionLinks { get; } = 100000;
    public int ProvenancesPerTable { get; } = 1024;
    public int NumericTokenBytes { get; } = 512;
    public int IdentifierCharacters { get; } = 512;
    public int PropertyNameUtf8Bytes { get; } = 2048;
    public int ObjectProperties { get; } = 128;
    public int JsonDepth { get; } = 128;
    public int PayloadJsonBytes { get; } = DoclingInputGeometryCapture.MaximumJsonBytes;
    public int PayloadGzipBytes { get; } = DoclingInputGeometryCapture.MaximumGzipBytes;
    public int OwnWorkMilliseconds { get; } = 5000;
}

internal sealed class DoclingInputGeometryArtifact
{
    internal const string ArtifactType = "docling_projection_input_geometry";
    internal const string Schema = "saaia-docling-projection-input-geometry.v1";
    private readonly byte[] json;
    private readonly byte[] gzip;
    internal Guid TenantId { get; }
    internal Guid DocumentId { get; }
    internal Guid RevisionId { get; }
    internal Guid ProcessingRunId { get; }
    internal string SourceSha256 { get; }
    internal string Sha256 { get; }
    internal long ByteSize => json.Length;
    internal long StoredSize => gzip.Length;

    internal byte[] CopyJson() => (byte[])json.Clone();
    internal byte[] CopyGzip() => (byte[])gzip.Clone();
    internal string StorageKey => $"diagnostic://revision/{RevisionId:D}/{ArtifactType}/{Sha256}";

    internal DoclingInputGeometryArtifact(Guid tenant, Guid document, Guid revision, Guid run, string source, string sha, byte[] jsonBytes, byte[] gzipBytes)
    {
        TenantId = tenant;
        DocumentId = document;
        RevisionId = revision;
        ProcessingRunId = run;
        SourceSha256 = source;
        Sha256 = sha;
        json = (byte[])jsonBytes.Clone();
        gzip = (byte[])gzipBytes.Clone();
    }
}
