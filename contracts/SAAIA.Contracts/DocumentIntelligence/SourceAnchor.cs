using System.Text.Json.Serialization;

namespace SAAIA.Contracts.DocumentIntelligence;

public sealed class SourceAnchor
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = CanonicalSchema.SourceAnchorVersion;

    [JsonPropertyName("anchorId")]
    public string AnchorId { get; set; } = "";

    [JsonPropertyName("documentId")]
    public Guid DocumentId { get; set; }

    [JsonPropertyName("revisionId")]
    public Guid RevisionId { get; set; }

    [JsonPropertyName("sourceSha256")]
    public string SourceSha256 { get; set; } = "";

    [JsonPropertyName("manifestSha256")]
    public string ManifestSha256 { get; set; } = "";

    [JsonPropertyName("projectionId")]
    public string ProjectionId { get; set; } = "";

    [JsonPropertyName("projectionType")]
    public string ProjectionType { get; set; } = "";

    [JsonPropertyName("precision")]
    public string Precision { get; set; } = "";

    [JsonPropertyName("regions")]
    public List<SourceAnchorRegion> Regions { get; set; } = [];
}

public sealed class SourceAnchorRegion
{
    [JsonPropertyName("pageNumber")]
    public int PageNumber { get; set; }

    [JsonPropertyName("blockIds")]
    public List<string> BlockIds { get; set; } = [];

    [JsonPropertyName("spanIds")]
    public List<string> SpanIds { get; set; } = [];

    [JsonPropertyName("tableCellIds")]
    public List<string> TableCellIds { get; set; } = [];

    [JsonPropertyName("polygon")]
    public CanonicalPolygon? Polygon { get; set; }

    [JsonPropertyName("rawTextSha256")]
    public string RawTextSha256 { get; set; } = "";
}

public sealed class ResolvedSourceEvidence
{
    [JsonPropertyName("pageNumber")]
    public int PageNumber { get; set; }

    [JsonPropertyName("rawText")]
    public string RawText { get; set; } = "";

    [JsonPropertyName("canonicalText")]
    public string CanonicalText { get; set; } = "";

    [JsonPropertyName("displayText")]
    public string DisplayText { get; set; } = "";
}
