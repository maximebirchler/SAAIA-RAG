using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public static partial class EvidenceBundleBuilder
{
    private static IReadOnlyDictionary<string, string> BuildCodeHints(ToolResults.Item item, int sequence)
        => new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["tool_error"] = item.Error ?? string.Empty,
            ["duration_ms"] = item.DurationMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["tool_sequence"] = sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });

    private static IReadOnlyDictionary<string, string> BuildCodeHints(
        ToolResults.Item item,
        int sequence,
        JsonElement source)
    {
        var hints = new Dictionary<string, string>(
            BuildCodeHints(item, sequence),
            StringComparer.OrdinalIgnoreCase);
        AddSourceHint(hints, source, "chunk_type", "chunkType", "chunk_type");
        AddSourceHint(hints, source, "content_role", "contentRole", "content_role");
        AddSourceHint(hints, source, "source_window_error", "sourceWindowError");
        AddSourceHint(
            hints,
            source,
            "chunk_composition",
            "chunkComposition",
            "chunk_composition");
        AddSourceHint(
            hints,
            source,
            "section_title",
            "sectionTitle",
            "section_title");
        AddSourceHint(
            hints,
            source,
            "heading_path",
            "headingPath",
            "heading_path");
        AddSourceHint(
            hints,
            source,
            "navigation_reason",
            "navigationReason",
            "navigation_reason");
        return new ReadOnlyDictionary<string, string>(hints);
    }

    private static void AddSourceHint(
        IDictionary<string, string> hints,
        JsonElement source,
        string targetName,
        params string[] sourceNames)
    {
        var value = GetString(source, sourceNames)?.Trim();
        if (!string.IsNullOrWhiteSpace(value))
            hints[targetName] = value;
    }

    private static IReadOnlyList<string> BuildRiskFlags(JsonElement hit, string? toolName = null)
    {
        var flags = new List<string>();
        if (string.IsNullOrWhiteSpace(GetString(hit, "excerpt", "text", "Text", "snippet", "content", "fullText")))
            flags.Add("empty_excerpt");

        if (string.Equals(toolName, "documents.navigation", StringComparison.OrdinalIgnoreCase))
            flags.Add("orientation_only");

        return flags;
    }

    private static IReadOnlyDictionary<string, string> ReadStringMap(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value) || value.ValueKind != JsonValueKind.Object)
                continue;

            return new ReadOnlyDictionary<string, string>(value.EnumerateObject()
                .Where(static property => property.Value.ValueKind is
                    JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                .ToDictionary(
                    static property => property.Name,
                    static property => property.Value.ToString(),
                    StringComparer.OrdinalIgnoreCase));
        }

        return new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }

    private static JsonElement? ReadMatchedContentCards(JsonElement element)
    {
        if ((!TryGetProperty(element, "matchedContentCards", out var cards)
             && !TryGetProperty(element, "matched_content_cards", out cards)
             && !TryGetProperty(element, "contentCards", out cards)
             && !TryGetProperty(element, "content_cards", out cards))
            || cards.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        return cards.ValueKind is JsonValueKind.Array or JsonValueKind.Object
            ? cards.Clone()
            : null;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(element, name, out var value) && value.ValueKind != JsonValueKind.Null)
                return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        }

        return null;
    }

    private static int? GetInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
                return number;
        }

        return null;
    }

    private static double? GetDouble(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
                return number;
            if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), out number))
                return number;
        }

        return null;
    }

    private static string NormalizeEvidenceText(string? value)
        => Regex.Replace(value ?? string.Empty, @"\s+", " ", RegexOptions.CultureInvariant)
            .Trim()
            .ToLowerInvariant();
}
