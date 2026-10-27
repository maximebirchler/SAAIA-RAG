using System.Text.Json.Serialization;

namespace SAAIA.Contracts.DocumentIntelligence;

/// <summary>Secondary observations. Never primary blocks, grid cells or verified semantic relationships.</summary>
public sealed class CanonicalTableRegionObservationSet
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = "table_region_observation_set_v1";
    [JsonPropertyName("availability")]
    public string Availability { get; set; } = "unavailable";
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
    [JsonPropertyName("observations")]
    public List<CanonicalTableRegionObservation>? Observations { get; set; }
    [JsonPropertyName("attempts")]
    public List<CanonicalTableRegionObservationAttempt>? Attempts { get; set; }
}

public sealed class CanonicalTableRegionObservationAttempt
{
    [JsonPropertyName("sourceTableRef")]
    public string SourceTableRef { get; set; } = "";
    [JsonPropertyName("physicalPage")]
    public int? PhysicalPage { get; set; }
    [JsonPropertyName("attempted")]
    public bool Attempted { get; set; }
    [JsonPropertyName("status")]
    public string Status { get; set; } = "not_attempted";
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
    [JsonPropertyName("rawObservationSha256")]
    public string? RawObservationSha256 { get; set; }
    [JsonPropertyName("observationId")]
    public string? ObservationId { get; set; }
}

public sealed class CanonicalTableRegionObservation
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = "table_region_observation_v1";
    [JsonPropertyName("observationId")]
    public string ObservationId { get; set; } = "";
    [JsonPropertyName("documentId")]
    public Guid DocumentId { get; set; }
    [JsonPropertyName("revisionId")]
    public Guid RevisionId { get; set; }
    [JsonPropertyName("sourceSha256")]
    public string SourceSha256 { get; set; } = "";
    [JsonPropertyName("physicalPage")]
    public int PhysicalPage { get; set; }
    [JsonPropertyName("tableId")]
    public string TableId { get; set; } = "";
    [JsonPropertyName("sourceTableRef")]
    public string SourceTableRef { get; set; } = "";
    [JsonPropertyName("rawObservationSha256")]
    public string RawObservationSha256 { get; set; } = "";
    [JsonPropertyName("settingsSha256")]
    public string SettingsSha256 { get; set; } = "";
    [JsonPropertyName("region")]
    public CanonicalPolygon Region { get; set; } = new();
    [JsonPropertyName("coordinateFrame")]
    public CanonicalTableRegionCoordinateFrame CoordinateFrame { get; set; } = new();
    [JsonPropertyName("engineProvenance")]
    public CanonicalTableRegionEngineProvenance EngineProvenance { get; set; } = new();
    [JsonPropertyName("lines")]
    public List<CanonicalTableRegionObservationLine> Lines { get; set; } = [];
    [JsonPropertyName("physicalReading")]
    public CanonicalTablePhysicalReading PhysicalReading { get; set; } = new();
    [JsonPropertyName("qualitySignals")]
    public List<string> QualitySignals { get; set; } = [];
    [JsonPropertyName("semanticAccuracy")]
    public string SemanticAccuracy { get; set; } = "not_verified";
    [JsonPropertyName("canonicalGridRepaired")]
    public bool CanonicalGridRepaired { get; set; }
    [JsonIgnore]
    public byte[]? PublicationRawBytes { get; set; }
}

public sealed class CanonicalTableRegionObservationLine
{
    [JsonPropertyName("lineId")]
    public string LineId { get; set; } = "";
    [JsonPropertyName("rawOrdinal")]
    public int RawOrdinal { get; set; }
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
    [JsonPropertyName("textSha256")]
    public string TextSha256 { get; set; } = "";
    [JsonPropertyName("polygon")]
    public CanonicalPolygon Polygon { get; set; } = new();
    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }
}

public sealed class CanonicalTablePhysicalReading
{
    [JsonPropertyName("availability")]
    public string Availability { get; set; } = "unavailable";
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
    [JsonPropertyName("algorithm")]
    public string Algorithm { get; set; } = "observed_bottom_rule_bands_v1";
    [JsonPropertyName("nativeFrameAvailability")]
    public string? NativeFrameAvailability { get; set; }
    [JsonPropertyName("nativeFrameReason")]
    public string? NativeFrameReason { get; set; }
    [JsonPropertyName("nativeRotationDegrees")]
    public int? NativeRotationDegrees { get; set; }
    [JsonPropertyName("nativeCropBoxLeft")]
    public double? NativeCropBoxLeft { get; set; }
    [JsonPropertyName("nativeCropBoxBottom")]
    public double? NativeCropBoxBottom { get; set; }
    [JsonPropertyName("nativeMediaBoxLeft")]
    public double? NativeMediaBoxLeft { get; set; }
    [JsonPropertyName("nativeMediaBoxBottom")]
    public double? NativeMediaBoxBottom { get; set; }
    [JsonPropertyName("vectorObservationAvailability")]
    public string? VectorObservationAvailability { get; set; }
    [JsonPropertyName("vectorObservationReason")]
    public string? VectorObservationReason { get; set; }
    [JsonPropertyName("vectorRules")]
    public List<CanonicalTableVectorRule>? VectorRules { get; set; }
    [JsonPropertyName("vectorEvidenceSha256")]
    public string? VectorEvidenceSha256 { get; set; }
    [JsonPropertyName("bands")]
    public List<CanonicalTablePhysicalBand>? Bands { get; set; }
    [JsonPropertyName("unassignedLineIds")]
    public List<string> UnassignedLineIds { get; set; } = [];
    [JsonPropertyName("cellMembershipProven")]
    public bool CellMembershipProven { get; set; }
    [JsonPropertyName("semanticParentLinksAsserted")]
    public bool SemanticParentLinksAsserted { get; set; }
}

public sealed class CanonicalTablePhysicalBand
{
    [JsonPropertyName("bandOrdinal")]
    public int BandOrdinal { get; set; }
    [JsonPropertyName("polygon")]
    public CanonicalPolygon Polygon { get; set; } = new();
    [JsonPropertyName("supportingRuleIds")]
    public List<string> SupportingRuleIds { get; set; } = [];
    [JsonPropertyName("orderedLineIds")]
    public List<string> OrderedLineIds { get; set; } = [];
}

public sealed class CanonicalTableRegionEngineProvenance
{
    [JsonPropertyName("engine")]
    public string Engine { get; set; } = "";
    [JsonPropertyName("engineVersion")]
    public string EngineVersion { get; set; } = "";
    [JsonPropertyName("modelId")]
    public string ModelId { get; set; } = "";
    [JsonPropertyName("modelArtifacts")]
    public Dictionary<string, string> ModelArtifacts { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("runtimeSettings")]
    public CanonicalTableRegionRuntimeSettings? RuntimeSettings { get; set; }
    [JsonPropertyName("durationMs")]
    public double? DurationMs { get; set; }
}

public sealed class CanonicalTableRegionRuntimeSettings
{
    [JsonPropertyName("onnxruntimeIntraOpNumThreads")]
    public int? OnnxruntimeIntraOpNumThreads { get; set; }
    [JsonPropertyName("onnxruntimeInterOpNumThreads")]
    public int? OnnxruntimeInterOpNumThreads { get; set; }
    [JsonPropertyName("observedSessionCount")]
    public int? ObservedSessionCount { get; set; }
    [JsonPropertyName("providers")]
    public List<string>? Providers { get; set; }
}

public sealed class CanonicalTableRegionCoordinateFrame
{
    [JsonPropertyName("coordinateOrigin")]
    public string CoordinateOrigin { get; set; } = "TOPLEFT";
    [JsonPropertyName("pageWidthPoints")]
    public double PageWidthPoints { get; set; }
    [JsonPropertyName("pageHeightPoints")]
    public double PageHeightPoints { get; set; }
}

/// <summary>Observed axis-aligned source primitive; not a cell boundary or visibility guarantee.</summary>
public sealed class CanonicalTableVectorRule
{
    [JsonPropertyName("ruleId")]
    public string RuleId { get; set; } = "";
    [JsonPropertyName("axis")]
    public string Axis { get; set; } = "";
    [JsonPropertyName("coordinate")]
    public double Coordinate { get; set; }
    [JsonPropertyName("start")]
    public double Start { get; set; }
    [JsonPropertyName("end")]
    public double End { get; set; }
    [JsonPropertyName("thickness")]
    public double Thickness { get; set; }
    [JsonPropertyName("primitiveIds")]
    public List<string> PrimitiveIds { get; set; } = [];
}
