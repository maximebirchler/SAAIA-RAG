using System.Text.Json.Serialization;

namespace SAAIA.Contracts.DocumentIntelligence;

public static class CanonicalSchema
{
    public const string DocumentVersion = "canonical_document_v1";
    public const string SourceAnchorVersion = "source_anchor_v1";
    public const string ManifestVersion = "ingestion_manifest_v1";
    public const string BundleVersion = "canonical_ingestion_bundle_v1";
    public const string NormalizedTopLeftCoordinateSpace = "normalized_top_left_v1";
}

public sealed class CanonicalDocument
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = CanonicalSchema.DocumentVersion;

    [JsonPropertyName("documentId")]
    public Guid DocumentId { get; set; }

    [JsonPropertyName("revisionId")]
    public Guid RevisionId { get; set; }

    [JsonPropertyName("source")]
    public CanonicalSourceIdentity Source { get; set; } = new();

    [JsonPropertyName("pageCount")]
    public int PageCount { get; set; }

    [JsonPropertyName("pages")]
    public List<CanonicalPage> Pages { get; set; } = [];

    [JsonPropertyName("sectionTree")]
    public List<CanonicalSectionNode> SectionTree { get; set; } = [];

    [JsonPropertyName("relations")]
    public List<CanonicalRelation> Relations { get; set; } = [];

    [JsonPropertyName("assets")]
    public List<CanonicalAsset> Assets { get; set; } = [];

    [JsonPropertyName("manifestSha256")]
    public string ManifestSha256 { get; set; } = "";
}

public sealed class CanonicalSourceIdentity
{
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("mediaType")]
    public string MediaType { get; set; } = "";

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }
}

public sealed class CanonicalPage
{
    [JsonPropertyName("pageNumber")]
    public int PageNumber { get; set; }

    [JsonPropertyName("geometry")]
    public CanonicalPageGeometry Geometry { get; set; } = new();

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("imageCoverage")]
    public double? ImageCoverage { get; set; }

    [JsonPropertyName("blocks")]
    public List<CanonicalBlock> Blocks { get; set; } = [];

    [JsonPropertyName("tables")]
    public List<CanonicalTable> Tables { get; set; } = [];

    [JsonPropertyName("figures")]
    public List<CanonicalFigure> Figures { get; set; } = [];

    [JsonPropertyName("qualityFlags")]
    public List<string> QualityFlags { get; set; } = [];
}

public sealed class CanonicalSectionNode
{
    [JsonPropertyName("sectionId")]
    public string SectionId { get; set; } = "";

    [JsonPropertyName("parentSectionId")]
    public string? ParentSectionId { get; set; }

    [JsonPropertyName("ordinal")]
    public int Ordinal { get; set; }

    [JsonPropertyName("level")]
    public int Level { get; set; }

    [JsonPropertyName("titleBlockIds")]
    public List<string> TitleBlockIds { get; set; } = [];

    [JsonPropertyName("contentBlockIds")]
    public List<string> ContentBlockIds { get; set; } = [];

    [JsonPropertyName("pageStart")]
    public int PageStart { get; set; }

    [JsonPropertyName("pageEnd")]
    public int PageEnd { get; set; }
}

public sealed class CanonicalAsset
{
    [JsonPropertyName("assetId")]
    public string AssetId { get; set; } = "";

    [JsonPropertyName("mediaType")]
    public string MediaType { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("storageKey")]
    public string StorageKey { get; set; } = "";

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }
}
