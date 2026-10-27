using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed record DoclingTableObservationProjectionReport(int AdmittedObservations,
    int RefusedObservations, long AdmittedRawBytes, long AdmittedCanonicalBytes);

internal sealed class DoclingTableRegionObservationEnvelope
{
    [JsonPropertyName("schema_version")] public string SchemaVersion { get; set; } = "";
    [JsonPropertyName("availability")] public string Availability { get; set; } = "";
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("source_sha256")] public string? SourceSha256 { get; set; }
    [JsonPropertyName("settings")] public JsonElement Settings { get; set; }
    [JsonPropertyName("source_table_count")] public int? SourceTableCount { get; set; }
    [JsonPropertyName("inspected_table_count")] public int? InspectedTableCount { get; set; }
    [JsonPropertyName("uninspected_table_count")] public int? UninspectedTableCount { get; set; }
    [JsonPropertyName("attempted_region_count")] public int? AttemptedRegionCount { get; set; }
    [JsonPropertyName("completed_observation_count")] public int? CompletedObservationCount { get; set; }
    [JsonPropertyName("attempts")] public List<DoclingTableRegionObservationAttempt>? Attempts { get; set; }
    [JsonPropertyName("observations")] public List<DoclingTableRegionObservation>? Observations { get; set; }
}

internal sealed class DoclingTableRegionObservationAttempt
{
    [JsonPropertyName("source_table_ref")] public string SourceTableRef { get; set; } = "";
    [JsonPropertyName("page_no")] public int? PageNumber { get; set; }
    [JsonPropertyName("attempted")] public bool? Attempted { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("raw_observation_sha256")] public string? RawObservationSha256 { get; set; }
}

internal sealed class DoclingTableRegionObservation
{
    [JsonPropertyName("source_table_ref")] public string SourceTableRef { get; set; } = "";
    [JsonPropertyName("page_no")] public int PageNumber { get; set; }
    [JsonPropertyName("bbox")] public DoclingBoundingBox BoundingBox { get; set; } = new();
    [JsonPropertyName("coordinate_origin")] public string CoordinateOrigin { get; set; } = "";
    [JsonPropertyName("page_width_points")] public double PageWidthPoints { get; set; }
    [JsonPropertyName("page_height_points")] public double PageHeightPoints { get; set; }
    [JsonPropertyName("raw_observation_json")] public string RawObservationJson { get; set; } = "";
    [JsonPropertyName("raw_observation_sha256")] public string RawObservationSha256 { get; set; } = "";
}
