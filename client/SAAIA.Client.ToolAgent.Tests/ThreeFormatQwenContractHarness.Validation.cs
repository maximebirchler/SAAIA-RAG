using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;

namespace SAAIA.Client.ToolAgent.Tests;

public static partial class ThreeFormatQwenContractHarness
{
    public static ParsedAction ParseDirectResponse(FrozenInputs inputs, ModelState state, string responseJson, string role = "controller")
    {
        var (name, arguments) = NativeCall(responseJson);
        var action = Actions(inputs, role).SingleOrDefault(a => string.Equals(ToolName(a), name, StringComparison.OrdinalIgnoreCase));
        Require(action is not null, "unknown_or_wrong_role_tool");
        return ValidateAction(inputs, state, role, action!, arguments);
    }

    public static string ParseRouteResponse(FrozenInputs inputs, string responseJson, string role = "controller")
    {
        var (name, arguments) = NativeCall(responseJson);
        Require(string.Equals(name, "select_source_backed_action", StringComparison.OrdinalIgnoreCase), "route_tool");
        ValidateSchema(inputs.Contracts.GetProperty("customSchemas").GetProperty(role + "Route"), arguments);
        return Text(arguments, "action");
    }

    public static ParsedAction ParseSpecializedPayloadResponse(FrozenInputs inputs, ModelState state, string selectedAction, string responseJson, string role = "controller")
    {
        Require(Actions(inputs, role).Contains(selectedAction), "selected_action");
        var result = ParseDirectResponse(inputs, state, responseJson, role);
        Require(result.Action == selectedAction, "route_payload_mismatch");
        return result;
    }

    public static ParsedAction ParseGrammarResponse(FrozenInputs inputs, ModelState state, string responseJson, string role = "controller")
    {
        var choice = Choice(responseJson);
        Require(Text(choice, "finish_reason") == "stop", "grammar_finish_reason");
        var message = choice.GetProperty("message");
        Require(!message.TryGetProperty("tool_calls", out var calls) || calls.ValueKind == JsonValueKind.Array && calls.GetArrayLength() == 0, "grammar_tool_fallback");
        var schema = inputs.Contracts.GetProperty("grammarSchemas").GetProperty(role);
        var value = NormalizeOptionalNulls(schema, ParseJson(Text(message, "content")));
        ValidateSchema(schema, value);
        var action = Text(value, "action");
        JsonElement payload;
        string[] active;
        switch (action)
        {
            case "answer":
                active = ["presentation", "claims"];
                payload = Project(value, active);
                break;
            case "search": case "navigation": case "content_cards": case "context":
                active = ["documentTool", "documentArguments"];
                Require(Text(value, "documentTool") == DocumentTools[action], "grammar_action_tool_mismatch");
                payload = value.GetProperty("documentArguments").Clone();
                break;
            case "clarify":
                active = ["message", "usefulEvidenceIds"];
                payload = JsonSerializer.SerializeToElement(new { message = Text(value, "message"), evidenceIds = value.GetProperty("usefulEvidenceIds") });
                break;
            case "insufficient": active = ["missingFacts", "usefulEvidenceIds"]; payload = Project(value, active); break;
            case "block": active = ["reason", "usefulEvidenceIds"]; payload = Project(value, active); break;
            case "accept": active = []; payload = ParseJson("{}"); break;
            default: throw new InvalidOperationException("A657_grammar_action");
        }
        foreach (var property in value.EnumerateObject().Where(p => p.Name != "action" && !active.Contains(p.Name)))
            Require(IsNeutral(property.Value), "grammar_inactive_field:" + property.Name);
        return ValidateAction(inputs, state, role, action, payload);
    }

    private static ParsedAction ValidateAction(FrozenInputs inputs, ModelState state, string role, string action, JsonElement arguments)
    {
        ValidateState(state);
        Require(Actions(inputs, role).Contains(action), "wrong_role_action");
        var schema = DocumentTools.TryGetValue(action, out var docTool)
            ? inputs.Contracts.GetProperty("authoritativeDocumentTools").EnumerateArray().Single(t => Text(t, "name") == docTool).GetProperty("parameters")
            : inputs.Contracts.GetProperty("customSchemas").GetProperty(Custom[action].Schema);
        var normalized = NormalizeOptionalNulls(schema, arguments);
        ValidateSchema(schema, normalized);
        ValidateIdentities(state, normalized);
        var name = ToolName(action);
        if (docTool is not null)
        {
            Require(SourceBackedAgentToolCatalog.TryResolveInternalName(name, normalized, out var internalName), "unknown_catalog_tool");
            return new(role, action, internalName, normalized);
        }
        return new(role, action, name, normalized);
    }

    private static string ToolName(string action) => DocumentTools.TryGetValue(action, out var name) ? name : Custom[action].Tool;
    private static JsonElement Project(JsonElement value, string[] names)
        => JsonSerializer.SerializeToElement(names.ToDictionary(n => n, n => value.GetProperty(n)));
    private static bool IsNeutral(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() == "", JsonValueKind.Array => value.GetArrayLength() == 0,
        JsonValueKind.Object => !value.EnumerateObject().Any(), _ => false
    };

    private static (string Name, JsonElement Arguments) NativeCall(string json)
    {
        var choice = Choice(json);
        Require(Text(choice, "finish_reason") == "tool_calls", "native_finish_reason");
        var message = choice.GetProperty("message");
        Require(!message.TryGetProperty("content", out var content) || content.ValueKind == JsonValueKind.Null
            || content.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(content.GetString()), "native_content_fallback");
        var calls = message.GetProperty("tool_calls");
        Require(calls.ValueKind == JsonValueKind.Array && calls.GetArrayLength() == 1, "native_exactly_one_call");
        var call = calls[0];
        Require(Text(call, "type") == "function" && !string.IsNullOrWhiteSpace(Text(call, "id")), "native_call_identity");
        var function = call.GetProperty("function");
        return (Text(function, "name"), ParseJson(Text(function, "arguments")));
    }
    private static JsonElement Choice(string json)
    {
        var root = ParseJson(json);
        var choices = root.GetProperty("choices");
        Require(choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() == 1, "exactly_one_choice");
        var choice = choices[0];
        Require(Text(choice.GetProperty("message"), "role") == "assistant", "assistant_role");
        return choice.Clone();
    }

    public static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        CheckUnique(document.RootElement);
        return document.RootElement.Clone();
    }
    private static void CheckUnique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject()) { Require(names.Add(p.Name), "duplicate_json_property"); CheckUnique(p.Value); }
        }
        if (value.ValueKind == JsonValueKind.Array) foreach (var v in value.EnumerateArray()) CheckUnique(v);
    }

    // Deliberately limited to the keywords in the hash-sealed A656 schemas.
    // Unknown validation keywords fail closed instead of silently weakening them.
    private static void ValidateSchema(JsonElement schema, JsonElement value)
    {
        string[] known = ["type", "description", "properties", "required", "additionalProperties", "anyOf", "enum", "items", "minItems", "maxItems", "uniqueItems", "minLength", "maxLength", "pattern", "minimum", "maximum"];
        foreach (var p in schema.EnumerateObject()) Require(known.Contains(p.Name), "unsupported_schema_keyword:" + p.Name);
        if (schema.TryGetProperty("type", out var type))
            Require(type.GetString() switch
            {
                "object" => value.ValueKind == JsonValueKind.Object, "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String, "number" => value.ValueKind == JsonValueKind.Number,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var n) && n == decimal.Truncate(n),
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False, _ => false
            }, "schema_type");
        if (schema.TryGetProperty("enum", out var enums)) Require(enums.EnumerateArray().Any(e => SameJson(e, value)), "schema_enum");
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required)) foreach (var r in required.EnumerateArray()) Require(value.TryGetProperty(r.GetString()!, out _), "schema_required:" + r.GetString());
            foreach (var p in value.EnumerateObject())
            {
                if (schema.TryGetProperty("properties", out var properties) && properties.TryGetProperty(p.Name, out var child)) ValidateSchema(child, p.Value);
                else Require(!schema.TryGetProperty("additionalProperties", out var extra) || extra.ValueKind != JsonValueKind.False, "schema_extra:" + p.Name);
            }
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var array = value.EnumerateArray().ToArray();
            Bound(schema, "minItems", array.Length, true); Bound(schema, "maxItems", array.Length, false);
            if (schema.TryGetProperty("items", out var item)) foreach (var v in array) ValidateSchema(item, v);
            if (schema.TryGetProperty("uniqueItems", out var unique) && unique.GetBoolean())
                for (var a = 0; a < array.Length; a++) for (var b = a + 1; b < array.Length; b++) Require(!SameJson(array[a], array[b]), "schema_unique");
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            var s = value.GetString()!;
            var length = s.EnumerateRunes().Count();
            Bound(schema, "minLength", length, true); Bound(schema, "maxLength", length, false);
            if (schema.TryGetProperty("pattern", out var pattern)) Require(Regex.IsMatch(s, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), "schema_pattern");
        }
        if (value.ValueKind == JsonValueKind.Number)
        { Bound(schema, "minimum", value.GetDouble(), true); Bound(schema, "maximum", value.GetDouble(), false); }
        if (schema.TryGetProperty("anyOf", out var alternatives))
            Require(alternatives.EnumerateArray().Any(s => Matches(s, value)), "schema_anyOf");
    }
    private static bool Matches(JsonElement schema, JsonElement value)
    { try { ValidateSchema(schema, value); return true; } catch (InvalidOperationException) { return false; } }
    private static void Bound(JsonElement schema, string key, double value, bool minimum)
    { if (schema.TryGetProperty(key, out var bound)) Require(minimum ? value >= bound.GetDouble() : value <= bound.GetDouble(), "schema_" + key); }
    private static JsonElement NormalizeOptionalNulls(JsonElement schema, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var properties))
        {
            var required = schema.TryGetProperty("required", out var r) ? r.EnumerateArray().Select(e => e.GetString()).ToHashSet() : [];
            var result = new Dictionary<string, JsonElement>();
            foreach (var p in value.EnumerateObject())
            {
                if (properties.TryGetProperty(p.Name, out var child))
                {
                    if (p.Value.ValueKind == JsonValueKind.Null && !required.Contains(p.Name)) continue;
                    result[p.Name] = NormalizeOptionalNulls(child, p.Value);
                }
                else result[p.Name] = p.Value.Clone();
            }
            return JsonSerializer.SerializeToElement(result);
        }
        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var item))
            return JsonSerializer.SerializeToElement(value.EnumerateArray().Select(v => NormalizeOptionalNulls(item, v)).ToArray());
        return value.Clone();
    }
    private static void ValidateIdentities(ModelState state, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array) { foreach (var v in value.EnumerateArray()) ValidateIdentities(state, v); return; }
        if (value.ValueKind != JsonValueKind.Object) return;
        foreach (var p in value.EnumerateObject())
        {
            if (p.Name is "evidenceIds" or "usefulEvidenceIds")
                foreach (var id in p.Value.EnumerateArray().Select(e => e.GetString()!))
                    Require(state.Evidence.Any(e => e.Id == id) && !state.RejectedEvidenceIds.Contains(id), "unknown_or_rejected_evidence");
            if (p.Name is "docId" or "docPath" or "chunkId" or "docRef" or "path" or "categoryPath" or "categoryRef")
            {
                var observed = p.Name switch
                {
                    "docId" => state.Evidence.Select(e => e.DocId), "docPath" => state.Evidence.Select(e => e.DocPath),
                    "docRef" => state.Evidence.SelectMany(e => new[] { e.DocId, e.DocPath, e.DocName }),
                    "chunkId" => state.Evidence.Select(e => e.ChunkId), _ => Enumerable.Empty<string>()
                };
                Require(observed.Contains(p.Value.GetString(), StringComparer.Ordinal), "unobserved_identity:" + p.Name);
            }
            if (p.Name is "pageStart" or "pageEnd")
                Require(state.Evidence.Any(e => e.PageStart == p.Value.GetDecimal()), "unobserved_page");
            ValidateIdentities(state, p.Value);
        }
    }
}
