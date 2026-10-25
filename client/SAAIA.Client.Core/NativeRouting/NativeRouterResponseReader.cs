using System.Text.Json;

namespace SAAIA.Client.Core.NativeRouting;

/// <summary>Mechanical native-router JSON decoding and structural grid validation; no semantic routing decisions.</summary>
public static class NativeRouterResponseReader
{

    public static string ReadNativeRouterString(
        JsonElement root,
        string propertyName)
        => TryReadNativeRouterProperty(root, propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim() ?? string.Empty
            : string.Empty;


    public static string NormalizeNativeRouterOptionalReference(string value)
        => value.Trim() switch
        {
            "-" or "none" or "null" or "n/a" => string.Empty,
            var normalized => normalized
        };


    public static bool TryReadNativeRouterLayout(
        JsonElement root,
        string compactKind,
        int atomicEvidenceCount,
        out string rowHeader,
        out List<string> rowLabels,
        out List<string> columns)
    {
        rowHeader = ReadNativeRouterString(root, "rowHeader");
        rowLabels = ReadNativeRouterStringArray(root, "rows");
        columns = ReadNativeRouterStringArray(root, "columns");

        if (!string.Equals(compactKind, "grid", StringComparison.Ordinal))
        {
            return rowHeader.Length == 0
                   && rowLabels.Count == 0
                   && columns.Count == 0;
        }

        var normalizedRowHeader = rowHeader;
        return rowHeader.Length is >= 1 and <= 60
               && IsStructurallyValidNativeRouterLabel(rowHeader)
               && rowLabels.Count is >= 1 and <= 80
               && columns.Count is >= 1 and <= 12
               && rowLabels.All(static label => label.Length is >= 1 and <= 60)
               && columns.All(static label => label.Length is >= 1 and <= 60)
               && rowLabels.All(IsStructurallyValidNativeRouterLabel)
               && columns.All(IsStructurallyValidNativeRouterLabel)
               && rowLabels.Distinct(StringComparer.OrdinalIgnoreCase).Count()
               == rowLabels.Count
               && columns.Distinct(StringComparer.OrdinalIgnoreCase).Count()
               == columns.Count
               && columns.All(label => !string.Equals(
                   label,
                   normalizedRowHeader,
                   StringComparison.OrdinalIgnoreCase))
               && (long)rowLabels.Count * columns.Count
                == atomicEvidenceCount;
    }


    private static bool IsStructurallyValidNativeRouterLabel(string label)
        => label.Any(char.IsLetterOrDigit)
           && !label.Any(char.IsControl)
           && HasBalancedNativeRouterDelimiters(label, '(', ')')
           && HasBalancedNativeRouterDelimiters(label, '[', ']')
           && HasBalancedNativeRouterDelimiters(label, '{', '}');


    private static bool HasBalancedNativeRouterDelimiters(
        string value,
        char opening,
        char closing)
    {
        var depth = 0;
        foreach (var character in value)
        {
            if (character == opening)
            {
                depth++;
                continue;
            }
            if (character != closing || --depth >= 0)
                continue;

            return false;
        }

        return depth == 0;
    }


    public static List<string> ReadNativeRouterStringArray(
        JsonElement root,
        string propertyName)
    {
        if (!TryReadNativeRouterProperty(root, propertyName, out var property))
            return new List<string>();
        if (property.ValueKind != JsonValueKind.Array)
            return new List<string> { string.Empty };

        return property
            .EnumerateArray()
            .Select(static item => item.ValueKind == JsonValueKind.String
                ? item.GetString()?.Trim() ?? string.Empty
                : string.Empty)
            .ToList();
    }


    public static bool TryReadNativeRouterInteger(
        JsonElement root,
        string propertyName,
        out int value)
    {
        value = 0;
        return TryReadNativeRouterProperty(
                   root,
                   propertyName,
                   out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetInt32(out value);
    }


    public static bool TryReadNativeRouterBoolean(
        JsonElement root,
        string propertyName,
        out bool value)
    {
        value = false;
        if (!TryReadNativeRouterProperty(
                root,
                propertyName,
                out var property)
            || property.ValueKind is not (
                JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }


    public static bool TryReadNativeRouterProperty(
        JsonElement root,
        string propertyName,
        out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(
                        property.Name,
                        propertyName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

}
