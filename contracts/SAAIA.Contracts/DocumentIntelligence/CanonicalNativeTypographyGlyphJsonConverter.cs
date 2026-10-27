using System.Text.Json;
using System.Text.Json.Serialization;

namespace SAAIA.Contracts.DocumentIntelligence;

/// <summary>Lossless five-value glyph tuple. Shared baseline Y and metrics live on its run.</summary>
public sealed class CanonicalNativeTypographyGlyphJsonConverter : JsonConverter<CanonicalNativeTypographyGlyph>
{
    public override CanonicalNativeTypographyGlyph Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 5)
            throw new JsonException("native_typography_glyph_tuple_invalid");
        return new()
        {
            NativeLetterOrdinal = value[0].GetInt32(), RunTextOffsetUtf16 = value[1].GetInt32(),
            RunTextLengthUtf16 = value[2].GetInt32(), StartBaselineX = Number(value[3]), EndBaselineX = Number(value[4])
        };
    }

    public override void Write(Utf8JsonWriter writer, CanonicalNativeTypographyGlyph value, JsonSerializerOptions options)
    {
        writer.WriteStartArray(); writer.WriteNumberValue(value.NativeLetterOrdinal); writer.WriteNumberValue(value.RunTextOffsetUtf16);
        writer.WriteNumberValue(value.RunTextLengthUtf16); Write(writer, value.StartBaselineX); Write(writer, value.EndBaselineX); writer.WriteEndArray();
    }

    private static double? Number(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetDouble();
    private static void Write(Utf8JsonWriter writer, double? value) { if (value is null) writer.WriteNullValue(); else writer.WriteNumberValue(value.Value); }
}
