using System.Text.Json;
using System.Text.Json.Serialization;

internal static class IngestionJobPayloadJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(Guid docId, int version, string? source = null, int? indexedVersionBefore = null)
    {
        var payload = new IngestionJobPayloadData
        {
            DocId = docId,
            Version = version,
            Source = NormalizeSource(source),
            IndexedVersionBefore = indexedVersionBefore
        };

        return JsonSerializer.Serialize(payload, SerializerOptions);
    }

    public static IngestionJobPayloadData Parse(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return IngestionJobPayloadData.Empty;

        try
        {
            return JsonSerializer.Deserialize<IngestionJobPayloadData>(payload, SerializerOptions)
                   ?? IngestionJobPayloadData.Empty;
        }
        catch (JsonException)
        {
            return IngestionJobPayloadData.Empty;
        }
        catch (NotSupportedException)
        {
            return IngestionJobPayloadData.Empty;
        }
    }

    public static string? NormalizeSource(string? source)
        => string.IsNullOrWhiteSpace(source) ? null : source.Trim().ToLowerInvariant();

    public sealed class IngestionJobPayloadData
    {
        public static readonly IngestionJobPayloadData Empty = new();

        [JsonPropertyName("docId")]
        public Guid? DocId { get; init; }

        [JsonPropertyName("version")]
        public int? Version { get; init; }

        [JsonPropertyName("source")]
        public string? Source { get; init; }

        [JsonPropertyName("indexedVersionBefore")]
        public int? IndexedVersionBefore { get; init; }
    }
}
