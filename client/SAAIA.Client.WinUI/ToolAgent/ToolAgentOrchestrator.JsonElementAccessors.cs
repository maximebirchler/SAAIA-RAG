using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static object? DeserializePromptObject(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(propertyName, out var value)
            || value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<object>(value.GetRawText());
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetString(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
    }

    private static string? TryGetNestedString(JsonElement obj, string parent, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(parent, out var nested) || nested.ValueKind != JsonValueKind.Object) return null;
        return TryGetString(nested, prop);
    }

    private static double? TryGetNestedDouble(JsonElement obj, string parent, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(parent, out var nested) || nested.ValueKind != JsonValueKind.Object) return null;
        return TryGetDouble(nested, prop);
    }

    private static JsonElement? TryGetObject(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(prop, out var value) || value.ValueKind != JsonValueKind.Object) return null;
        return value;
    }

    private static JsonElement? TryGetArray(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(prop, out var value) || value.ValueKind != JsonValueKind.Array) return null;
        return value;
    }

    private static int? TryGetInt(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var n2)) return n2;
        return null;
    }

    private static List<int> TryGetIntList(JsonElement obj, params string[] props)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return new List<int>();

        foreach (var prop in props)
        {
            if (!obj.TryGetProperty(prop, out var value) || value.ValueKind != JsonValueKind.Array)
                continue;

            var values = value.EnumerateArray()
                .Select(static item =>
                {
                    if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number))
                        return number;
                    if (item.ValueKind == JsonValueKind.String && int.TryParse(item.GetString(), out var parsed))
                        return parsed;
                    return (int?)null;
                })
                .Where(static item => item.HasValue)
                .Select(static item => item!.Value)
                .Distinct()
                .OrderBy(static item => item)
                .ToList();
            if (values.Count > 0)
                return values;
        }

        return new List<int>();
    }

    private static long? TryGetLong(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var n2)) return n2;
        return null;
    }

    private static double? TryGetDouble(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var d2)) return d2;
        return null;
    }

    private static bool? TryGetBool(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.True) return true;
        if (v.ValueKind == JsonValueKind.False) return false;
        if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var parsed)) return parsed;
        return null;
    }
}
