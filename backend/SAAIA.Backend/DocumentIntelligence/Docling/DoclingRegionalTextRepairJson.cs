using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>Strict, bounded reads of private repair evidence. Failure messages never copy its content.</summary>
internal static class DoclingRegionalTextRepairJson
{
    internal const string Schema = "saaia_text_region_repair_v1";
    internal static InvalidDataException Invalid(string code = "proof")
        => new("docling_regional_text_repair_" + code + "_invalid");

    internal static JsonElement Field(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var result)
            ? result : throw Invalid();
    internal static string String(JsonElement value, string name, int maximum = 256)
    {
        var field = Field(value, name);
        if (field.ValueKind != JsonValueKind.String || field.GetString() is not { } text || text.Length > maximum)
            throw Invalid();
        return text;
    }
    internal static int Int(JsonElement value, string name)
    {
        var field = Field(value, name);
        return field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out var result) ? result : throw Invalid();
    }
    internal static double Number(JsonElement value, string name)
    {
        var field = Field(value, name);
        if (field.ValueKind != JsonValueKind.Number || !field.TryGetDouble(out var result) || !double.IsFinite(result))
            throw Invalid();
        return result;
    }
    internal static bool Bool(JsonElement value, string name)
    {
        var field = Field(value, name);
        if (field.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid();
        return field.GetBoolean();
    }
    internal static JsonElement[] Array(JsonElement value, string name, int maximum)
    {
        var field = Field(value, name);
        if (field.ValueKind != JsonValueKind.Array || field.GetArrayLength() > maximum) throw Invalid();
        return field.EnumerateArray().ToArray();
    }
    internal static string BoundedJson(JsonElement value)
    {
        var json = value.GetRawText();
        if (json.Length > 262_144 || Encoding.UTF8.GetByteCount(json) > 262_144) throw Invalid("provenance_budget");
        return json;
    }
    internal static void EqualString(JsonElement value, string name, string expected)
    {
        if (String(value, name, Math.Max(256, expected.Length)) != expected) throw Invalid();
    }
    internal static bool IsSha(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    internal static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    internal static void CheckHash(JsonElement value, string name, string text)
    {
        if (!string.Equals(String(value, name), Hash(text), StringComparison.OrdinalIgnoreCase)) throw Invalid();
    }
    internal static void SameProvenance(JsonElement proof, DoclingTextItem item, bool requireSingleRegion = true)
    {
        var observed = Array(proof, "source_provenance", requireSingleRegion ? 1 : 1_024);
        if (observed.Length != item.Provenance.Count || requireSingleRegion && observed.Length != 1) throw Invalid();
        for (var index = 0; index < observed.Length; index++)
        {
            DoclingProvenance supplied;
            try { supplied = observed[index].Deserialize<DoclingProvenance>() ?? throw Invalid(); }
            catch (JsonException) { throw Invalid(); }
            var actual = item.Provenance[index];
            if (supplied.PageNumber != actual.PageNumber || supplied.CharacterSpan is null || !supplied.CharacterSpan.SequenceEqual(actual.CharacterSpan))
                throw Invalid();
            if (supplied.BoundingBox is null && actual.BoundingBox is null) continue;
            if (supplied.BoundingBox is not { } first || actual.BoundingBox is not { } second
                || first.Left != second.Left || first.Right != second.Right || first.Top != second.Top
                || first.Bottom != second.Bottom || first.CoordinateOrigin != second.CoordinateOrigin) throw Invalid();
        }
    }
}
