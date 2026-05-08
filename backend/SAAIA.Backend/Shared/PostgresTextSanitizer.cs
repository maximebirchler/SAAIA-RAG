using System.Text;
using System.Text.Json;

internal static class PostgresTextSanitizer
{
    internal static string Clean(string? text)
        => string.IsNullOrEmpty(text)
            ? string.Empty
            : TextEncodingSanitizer.RepairCommonMojibake(text.Replace("\0", string.Empty));

    internal static string? CleanOrNull(string? text)
    {
        if (text is null)
            return null;

        var clean = Clean(text);
        return string.IsNullOrWhiteSpace(clean) ? null : clean;
    }

    internal static string[] CleanArray(IEnumerable<string>? values)
        => values?
            .Select(Clean)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray()
           ?? [];

    internal static string? CleanJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        using var doc = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCleanJson(doc.RootElement, writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static string? CleanJson(JsonElement? element)
        => element.HasValue ? CleanJson(element.Value.GetRawText()) : null;

    private static void WriteCleanJson(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(Clean(property.Name));
                    WriteCleanJson(property.Value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCleanJson(item, writer);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(Clean(element.GetString()));
                break;
            case JsonValueKind.Number:
                element.WriteTo(writer);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
            default:
                writer.WriteNullValue();
                break;
        }
    }
}
