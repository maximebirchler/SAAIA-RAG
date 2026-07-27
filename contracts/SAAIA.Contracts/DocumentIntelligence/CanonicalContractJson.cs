using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SAAIA.Contracts.DocumentIntelligence;

public static class CanonicalContractJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        WriteIndented = false
    };

    public static string Serialize<T>(T value)
        => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json, Options)
           ?? throw new JsonException($"Unable to deserialize {typeof(T).Name}.");

    public static string SerializeCanonical<T>(T value)
    {
        var element = JsonSerializer.SerializeToElement(value, Options);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteCanonical(writer, element);

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string ComputeSha256<T>(T value)
        => Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(SerializeCanonical(value))))
            .ToLowerInvariant();

    public static IngestionManifestEnvelope CreateEnvelope(IngestionManifest manifest)
        => new()
        {
            Manifest = manifest,
            ManifestSha256 = ComputeSha256(manifest)
        };

    public static bool HasValidHash(IngestionManifestEnvelope envelope)
        => string.Equals(
            envelope.ManifestSha256,
            ComputeSha256(envelope.Manifest),
            StringComparison.OrdinalIgnoreCase);

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element
                             .EnumerateObject()
                             .OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;

            default:
                throw new JsonException($"Unsupported JSON value kind {element.ValueKind}.");
        }
    }
}

public static class CanonicalStableId
{
    public static string Create(string kind, params string?[] parts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, kind.Trim().ToLowerInvariant());
        foreach (var part in parts)
            Append(hash, part ?? "");

        var suffix = Convert.ToHexString(hash.GetHashAndReset().AsSpan(0, 16)).ToLowerInvariant();
        return $"{kind.Trim().ToLowerInvariant()}_{suffix}";
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
