using System.Text.Json;
using System.Text.Json.Nodes;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private sealed record NativeResearchTurn(string CallsJson,
        IReadOnlyList<AdvancedAnalysisSearchRequest>? Requests,
        IReadOnlyDictionary<string, AdvancedAnalysisSearchObservation> Results,
        AdvancedAnalysisResearchArgumentFeedback? Rejection = null,
        string? ResponseOutputItemsJson = null);

    private const string NativeResearchContract = """
        If further documentary facts are needed and research is allowed, choose
        the appropriate declared function. Function results are bounded; current
        source excerpts and opaque source handles are in the user evidence array.
        A contents entry locates an item; it does not establish its own body.
        A limit or a missing visible excerpt does not prove absence in the corpus.
        Do not publish claims while calling functions. When ready, return the
        terminal JSON result required by the answer contract. Tool history is
        operational memory, never documentary evidence. Cite only visible evidence.
        """;

    private object[]? BuildNativeResearchFunctions(string userPrompt)
    {
        using var document = JsonDocument.Parse(userPrompt);
        var root = document.RootElement;
        if (!root.TryGetProperty("researchTools", out var research)
            || !research.TryGetProperty("researchAllowed", out var allowed)
            || allowed.ValueKind != JsonValueKind.True) return null;
        var sources = root.GetProperty("evidence").EnumerateArray()
            .Select(e => ReadString(e, "sourceKey")).Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.Ordinal).ToArray();
        var categories = research.GetProperty("availableCategories").EnumerateArray()
            .Select(e => e.GetString() ?? string.Empty).Prepend(string.Empty)
            .Distinct(StringComparer.Ordinal).ToArray();
        object Text(string description) => new { type = "string", description };
        object Choice(string[] values) => new { type = "string", @enum = values };
        object Count(int minimum, int maximum) => new { type = "integer", minimum, maximum };
        object Function(string name, string description, Dictionary<string, object> properties)
            => new { type = "function", function = new { name, description, strict = true,
                parameters = new { type = "object", properties, required = properties.Keys.ToArray(), additionalProperties = false } } };
        var functions = new List<object>
        {
            Function("search_corpus", "Semantic retrieval from the private indexed corpus. Scope to an observed source when needed; empty strings leave optional scopes empty.", new()
            {
                ["query"] = Text("Search wording chosen for the documentary fact."),
                ["sourceKey"] = Choice(sources.Prepend(string.Empty).ToArray()),
                ["category"] = Choice(categories), ["documentHint"] = Text("Empty when sourceKey is used."),
                ["topK"] = Count(1, 60)
            })
        };
        if (sources.Length > 0)
        {
            functions.Add(Function("read_source", "Read a physical page window of an observed revision. Page bounds are inclusive; at most four pages per request. Useful for reading bodies located by contents or headings.", new()
            {
                ["sourceKey"] = Choice(sources), ["pageStart"] = Count(1, int.MaxValue),
                ["pageEnd"] = Count(1, int.MaxValue), ["topK"] = Count(1, 60)
            }));
            functions.Add(Function("find_source_text", "Locate literal words or an observed title inside one observed revision. Case and whitespace normalized; punctuation remains literal. Returns canonical excerpts and pagination diagnostics; no semantic search fallback.", new()
            {
                ["sourceKey"] = Choice(sources), ["query"] = Text("Literal words, between 2 and 512 characters."),
                ["offset"] = Count(0, 10_000), ["topK"] = Count(1, 60)
            }));
        }
        return functions.ToArray();
    }

    private static string NormalizeNativeResearchCalls(JsonElement calls, string userPrompt)
    {
        using var prompt = JsonDocument.Parse(userPrompt);
        var maximum = prompt.RootElement.GetProperty("researchTools").GetProperty("maximumQueries").GetInt32();
        if (calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() < 1
            || calls.GetArrayLength() > maximum || calls.GetRawText().Length > 8_192)
            throw new AdvancedAnalysisProviderException("advanced_native_tool_protocol_invalid");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var queries = new JsonArray();
        foreach (var call in calls.EnumerateArray())
        {
            var id = ReadString(call, "id");
            if (ReadString(call, "type") != "function" || id.Length == 0 || id.Length > 200 || !ids.Add(id)
                || !call.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object)
                throw new AdvancedAnalysisProviderException("advanced_native_tool_protocol_invalid");
            var name = ReadString(function, "name");
            string[] properties = name switch
            {
                "search_corpus" => ["query", "sourceKey", "category", "documentHint", "topK"],
                "read_source" => ["sourceKey", "pageStart", "pageEnd", "topK"],
                "find_source_text" => ["sourceKey", "query", "offset", "topK"],
                _ => throw new AdvancedAnalysisProviderException("advanced_native_tool_protocol_invalid")
            };
            using var arguments = JsonDocument.Parse(ReadString(function, "arguments"));
            if (arguments.RootElement.ValueKind != JsonValueKind.Object)
                throw new AdvancedAnalysisProviderException("advanced_native_tool_protocol_invalid");
            var names = arguments.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Length
                || names.Any(n => !properties.Contains(n, StringComparer.Ordinal))
                || properties.Any(n => !names.Contains(n, StringComparer.Ordinal)))
                throw new AdvancedAnalysisProviderException("advanced_native_tool_protocol_invalid");
            var query = JsonNode.Parse(arguments.RootElement.GetRawText())!.AsObject();
            query["operation"] = name;
            queries.Add(query);
        }
        return new JsonObject { ["outcome"] = "research_required", ["queries"] = queries }.ToJsonString(JsonOptions);
    }

    private static IReadOnlyList<object> BuildNativeToolTurnMessages(NativeResearchTurn? turn,
        IReadOnlyList<PromptEvidenceItem> visible, bool responses = false)
    {
        if (turn is null) return [];
        using var calls = JsonDocument.Parse(turn.CallsJson);
        var messages = new List<object>();
        if (responses && turn.ResponseOutputItemsJson is not null)
        {
            using var items = JsonDocument.Parse(turn.ResponseOutputItemsJson);
            messages.AddRange(items.RootElement.EnumerateArray().Select(item => (object)item.Clone()));
        }
        else messages.Add(new { role = "assistant", content = (string?)null,
            tool_calls = calls.RootElement.Clone() });
        var visibleIds = visible.Select(e => e.EvidenceId).ToHashSet(StringComparer.Ordinal);
        var index = 0;
        foreach (var call in calls.RootElement.EnumerateArray())
        {
            var request = turn.Requests?.ElementAtOrDefault(index++);
            AdvancedAnalysisSearchObservation? observation = null;
            if (request is not null) turn.Results.TryGetValue(BuildSearchIdentity(request), out observation);
            var ids = observation?.Evidence.Select(e => e.Reference.EvidenceId)
                .Where(id => id is not null && visibleIds.Contains(id)).Distinct(StringComparer.Ordinal).ToArray() ?? [];
            var result = new
            {
                status = turn.Rejection is not null ? "batch_rejected_before_execution"
                    : observation is null ? "not_executed_again" : "executed",
                operation = ReadString(call.GetProperty("function"), "name"),
                resolvedSource = request is null ? null : new { request.DocId, request.RevisionId, request.DocPath },
                argumentFeedback = turn.Rejection,
                returnedEvidenceCount = observation?.Evidence.Count,
                visibleEvidenceIds = ids.Take(8).ToArray(), visibleEvidenceIdsOmitted = Math.Max(0, ids.Length - 8),
                readDiagnostic = observation?.ReadDiagnostic, findDiagnostic = observation?.FindDiagnostic,
                instruction = "Bounded operational result. Only current user.evidence excerpts are documentary proof. Historical source handles belong to their original turn; use current user.evidence handles, reidentifying sources through evidence IDs and resolved identity. Unlisted or omitted evidence is not proof of corpus absence."
            };
            var resultJson = JsonSerializer.Serialize(result, JsonOptions);
            messages.Add(responses ? new { type = "function_call_output", call_id = ReadString(call, "id"), output = resultJson }
                : (object)new { role = "tool", tool_call_id = ReadString(call, "id"), content = resultJson });
        }
        if (JsonSerializer.Serialize(messages, JsonOptions).Length > 16_384)
            throw new AdvancedAnalysisProviderException("advanced_native_tool_history_limit_exceeded");
        return messages;
    }
}
