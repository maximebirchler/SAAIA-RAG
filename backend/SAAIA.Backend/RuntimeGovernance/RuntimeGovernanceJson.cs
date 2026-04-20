using System.Text.Json;

namespace SAAIA.Backend;

internal static class RuntimeGovernanceJson
{
    internal static IReadOnlyDictionary<string, object?>? ParseDetails(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        using var doc = JsonDocument.Parse(json);
        return ConvertObject(doc.RootElement);
    }

    private static Dictionary<string, object?> ConvertObject(JsonElement element)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var property in element.EnumerateObject())
            result[property.Name] = ConvertValue(property.Value);

        return result;
    }

    private static object? ConvertValue(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Object => ConvertObject(element),
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertValue).ToArray(),
            JsonValueKind.String => element.TryGetDateTimeOffset(out var dto) ? dto : element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
}
