using System.Text.Json.Serialization;

namespace SAAIA.Contracts;

/// <summary>Completeness of the associated signal list, not a source accuracy score.</summary>
public sealed record RagQualitySignalsMetadata(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("omittedCount")] int? OmittedCount = null,
    [property: JsonPropertyName("truncated")] bool? Truncated = null,
    [property: JsonPropertyName("malformed")] bool? Malformed = null);
