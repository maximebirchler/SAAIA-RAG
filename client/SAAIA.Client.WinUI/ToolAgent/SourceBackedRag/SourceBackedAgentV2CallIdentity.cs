using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

public sealed partial class SourceBackedAgentV2Runner
{
    private static string BuildMechanicalCallKey(string toolName, JsonElement arguments)
    {
        if (!string.Equals(toolName, "rag.search", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(toolName, "rag.multi_search", StringComparison.OrdinalIgnoreCase))
        {
            return toolName + "|" + CanonicalizeArguments(arguments);
        }

        var queries = GetStringArray(arguments, "queries")
            .Select(NormalizeIdentityText)
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        var identity = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["categoryPath"] = NormalizeIdentityText(GetString(arguments, "categoryPath", "category")),
            ["chunkId"] = NormalizeIdentityText(GetString(arguments, "chunkId")),
            ["diversity"] = GetNumber(arguments, "diversity"),
            ["docId"] = NormalizeIdentityText(GetString(arguments, "docId")),
            ["docPath"] = NormalizeIdentityText(GetString(arguments, "docPath")),
            ["docRef"] = NormalizeIdentityText(GetString(arguments, "docRef")),
            ["maxPerDoc"] = GetInt(arguments, "maxPerDoc"),
            ["maxPerPage"] = GetInt(arguments, "maxPerPage"),
            ["mode"] = NormalizeIdentityText(GetString(arguments, "mode")),
            ["inventoryMode"] = NormalizeIdentityText(
                GetString(arguments, "inventoryMode")),
            ["offset"] = GetInt(arguments, "offset"),
            ["pageEnd"] = GetInt(arguments, "pageEnd"),
            ["pageStart"] = GetInt(arguments, "pageStart"),
            ["queries"] = queries,
            ["query"] = NormalizeIdentityText(GetString(arguments, "query", "q")),
            ["topK"] = GetInt(arguments, "topK")
        };
        return toolName + "|" + JsonSerializer.Serialize(identity, ClientJson.CamelCase);
    }

    private static string CanonicalizeArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            return arguments.GetRawText();

        var ordered = arguments.EnumerateObject()
            .OrderBy(static property => property.Name, StringComparer.Ordinal)
            .ToDictionary(
                static property => property.Name,
                static property => property.Value.Clone(),
                StringComparer.Ordinal);
        return JsonSerializer.Serialize(ordered, ClientJson.CamelCase);
    }

    private static double? GetNumber(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                && property.Value.TryGetDouble(out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static string NormalizeIdentityText(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(
                    " ",
                    value.Split(
                        (char[]?)null,
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Replace('\\', '/')
                .ToLowerInvariant();
}
