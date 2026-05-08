using System.Text.Json;
using System.Text.Json.Serialization;

internal static class IngestionJobPayloadJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(
        Guid docId,
        int version,
        string? source = null,
        int? indexedVersionBefore = null,
        IngestionCapabilityAProfileSeed? capabilityAProfileSeed = null)
    {
        var payload = new IngestionJobPayloadData
        {
            DocId = docId,
            Version = version,
            Source = NormalizeSource(source),
            IndexedVersionBefore = indexedVersionBefore,
            CapabilityAProfileSeed = NormalizeCapabilityAProfileSeed(capabilityAProfileSeed)
        };

        return JsonSerializer.Serialize(payload, SerializerOptions);
    }

    public static IngestionJobPayloadData Parse(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return IngestionJobPayloadData.Empty;

        try
        {
            var parsed = JsonSerializer.Deserialize<IngestionJobPayloadData>(payload, SerializerOptions);
            return NormalizePayload(parsed);
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

    public static IngestionCapabilityAProfileSeed? NormalizeCapabilityAProfileSeed(IngestionCapabilityAProfileSeed? seed)
    {
        if (seed is null)
            return null;

        var questions = NormalizeSeedList(seed.HypotheticalQuestions, maxItems: 8);
        var tags = NormalizeSeedList(seed.SuggestedTags, maxItems: 12);
        var titles = NormalizeSeedList(seed.KeySectionTitles, maxItems: 8);
        var preview = NormalizeSeedText(seed.PreviewText, maxChars: 500);
        var basedOnIndexedVersion = seed.BasedOnIndexedVersion is >= 0
            ? seed.BasedOnIndexedVersion
            : null;

        if (questions.Length == 0
            && tags.Length == 0
            && titles.Length == 0
            && string.IsNullOrWhiteSpace(preview))
        {
            return null;
        }

        return new IngestionCapabilityAProfileSeed(
            HypotheticalQuestions: questions,
            SuggestedTags: tags,
            KeySectionTitles: titles,
            PreviewText: preview,
            BasedOnIndexedVersion: basedOnIndexedVersion);
    }

    private static IngestionJobPayloadData NormalizePayload(IngestionJobPayloadData? payload)
    {
        if (payload is null)
            return IngestionJobPayloadData.Empty;

        return new IngestionJobPayloadData
        {
            DocId = payload.DocId,
            Version = payload.Version,
            Source = NormalizeSource(payload.Source),
            IndexedVersionBefore = payload.IndexedVersionBefore,
            CapabilityAProfileSeed = NormalizeCapabilityAProfileSeed(payload.CapabilityAProfileSeed)
        };
    }

    private static string[] NormalizeSeedList(IEnumerable<string>? values, int maxItems)
        => (values ?? [])
            .Select(static value => NormalizeSeedText(value, maxChars: 180))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxItems))
            .ToArray();

    private static string? NormalizeSeedText(string? value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = string.Join(
            ' ',
            value.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Trim();
        if (normalized.Length <= maxChars)
            return normalized;

        var cut = normalized.LastIndexOf(' ', maxChars - 1);
        if (cut < maxChars / 2)
            cut = maxChars;
        return normalized[..cut].TrimEnd();
    }

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

        [JsonPropertyName("capabilityAProfileSeed")]
        public IngestionCapabilityAProfileSeed? CapabilityAProfileSeed { get; init; }
    }
}

internal sealed record IngestionCapabilityAProfileSeed(
    [property: JsonPropertyName("hypotheticalQuestions")]
    IReadOnlyList<string> HypotheticalQuestions,
    [property: JsonPropertyName("suggestedTags")]
    IReadOnlyList<string> SuggestedTags,
    [property: JsonPropertyName("keySectionTitles")]
    IReadOnlyList<string> KeySectionTitles,
    [property: JsonPropertyName("previewText")]
    string? PreviewText,
    [property: JsonPropertyName("basedOnIndexedVersion")]
    int? BasedOnIndexedVersion);
