using System.Text.Json;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private sealed record ResearchWorkspaceItem(string Key, string Label, string Purpose,
        string Status, string Note, IReadOnlyList<string> EvidenceIds);

    private static readonly string[] WorkspaceStates = ["candidate", "selected", "rejected", "needs_evidence"];

    private static object BuildResearchWorkspaceFunction(JsonElement prompt)
    {
        object Text(int maximum) => new { type = "string", maxLength = maximum };
        var itemProperties = new Dictionary<string, object>
        {
            ["key"] = Text(80), ["label"] = Text(200), ["purpose"] = Text(120),
            ["status"] = new { type = "string", @enum = WorkspaceStates }, ["note"] = Text(160),
            ["evidenceIds"] = new { type = "array", minItems = 1, maxItems = 4,
                items = new { type = "string", @enum = prompt.GetProperty("evidence").EnumerateArray()
                    .Select(e => ReadString(e, "evidenceId")).Distinct(StringComparer.Ordinal).ToArray() } }
        };
        return new { type = "function", function = new { name = "save_research_state", strict = true,
            description = "Replace your compact job research workspace. Choose which observed items to retain, their proposed purposes, decisions and gaps. Refer only to current evidence IDs. Memory is not documentary proof. Non-rejected references receive priority within the existing evidence budget. Save alone or alongside research; every function batch consumes the existing model-call budget.",
            parameters = new { type = "object", additionalProperties = false, required = new[] { "items" },
                properties = new { items = new { type = "array", maxItems = 32, items = new { type = "object",
                    properties = itemProperties, required = itemProperties.Keys.ToArray(), additionalProperties = false } } } } } };
    }

    private static IReadOnlyList<ResearchWorkspaceItem> ParseResearchWorkspace(JsonElement value, JsonElement prompt)
    {
        void Reject() => throw new AdvancedAnalysisProviderException("advanced_native_workspace_invalid");
        void Properties(JsonElement item, string[] expected)
        {
            if (item.ValueKind != JsonValueKind.Object) Reject();
            var names = item.EnumerateObject().Select(p => p.Name).ToArray();
            if (names.Length != expected.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Length
                || names.Any(n => !expected.Contains(n, StringComparer.Ordinal))) Reject();
        }
        string Text(JsonElement item, string name, int maximum, bool required = false)
        {
            if (!item.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) Reject();
            var text = property.GetString()!;
            if (text.Length > maximum || required && string.IsNullOrWhiteSpace(text)) Reject();
            return text;
        }
        if (!prompt.TryGetProperty("researchWorkspace", out var workspace)
            || !workspace.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.True
            || value.GetRawText().Length > 8_192) Reject();
        Properties(value, ["items"]);
        var items = value.GetProperty("items");
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 32) Reject();
        var visibleIds = prompt.GetProperty("evidence").EnumerateArray()
            .Select(e => ReadString(e, "evidenceId")).ToHashSet(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ResearchWorkspaceItem>();
        foreach (var item in items.EnumerateArray())
        {
            Properties(item, ["key", "label", "purpose", "status", "note", "evidenceIds"]);
            var key = Text(item, "key", 80, true); var label = Text(item, "label", 200, true);
            var purpose = Text(item, "purpose", 120); var state = Text(item, "status", 40, true);
            var note = Text(item, "note", 160);
            if (!keys.Add(key) || !WorkspaceStates.Contains(state, StringComparer.Ordinal)) Reject();
            var ids = item.GetProperty("evidenceIds");
            if (ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() is < 1 or > 4) Reject();
            var references = new List<string>();
            foreach (var id in ids.EnumerateArray())
            {
                if (id.ValueKind != JsonValueKind.String || !visibleIds.Contains(id.GetString()!)
                    || references.Contains(id.GetString()!, StringComparer.Ordinal)) Reject();
                references.Add(id.GetString()!);
            }
            result.Add(new(key, label, purpose, state, note, references));
        }
        if (JsonSerializer.Serialize(result, JsonOptions).Length > 8_192) Reject();
        return result;
    }

    private static IReadOnlyList<AdvancedAnalysisResolvedEvidence> PrioritizeResearchWorkspace(
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> evidence, IReadOnlyList<ResearchWorkspaceItem> workspace,
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> focused)
    {
        var ids = workspace.Where(i => i.Status != "rejected").SelectMany(i => i.EvidenceIds)
            .Distinct(StringComparer.Ordinal).ToArray();
        var byId = evidence.ToDictionary(e => e.Reference.EvidenceId!, StringComparer.Ordinal);
        return ids.Where(byId.ContainsKey).Select(id => byId[id])
            .Concat(focused.Where(e => !ids.Contains(e.Reference.EvidenceId!, StringComparer.Ordinal))).ToArray();
    }
}
