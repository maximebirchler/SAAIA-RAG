using System.Text.Json;
using System.Text.Json.Nodes;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private sealed record NativeResearchTurn(string CallsJson,
        IReadOnlyList<AdvancedAnalysisSearchRequest>? Requests,
        IReadOnlyDictionary<string, AdvancedAnalysisSearchObservation> Results,
        AdvancedAnalysisResearchArgumentFeedback? Rejection = null,
        string? ResponseOutputItemsJson = null,
        bool WorkspaceStored = false,
        bool CandidateInventoryStored = false,
        bool EvidenceReadsStored = false);

    private const string NativeResearchContract = """
        If further documentary facts are needed and research is allowed, choose
        the appropriate declared function. Function results are bounded; current
        source excerpts and opaque source handles are in the user evidence array.
        A contents entry locates an item; it does not establish its own body.
        A limit or a missing visible excerpt does not prove absence in the corpus.
        Do not publish claims while calling functions. When ready, return the
        terminal JSON result required by the answer contract. Tool history is
        operational memory, never documentary evidence. Cite only visible evidence.
        documentResolution is lookup metadata, not an admissible sourceKey or
        evidence. Its candidateCount counts a bounded sample, not an exhaustive
        inventory. Canonical reads still require a currently observed sourceKey.
        When an excerpt reports contentTruncated and nextContentOffset, use
        read_evidence with that evidenceId and character offset to inspect another
        exact window of its already revalidated canonical text. This does not
        retrieve another document or establish the missing text without reading it.
        The current evidence.content window and explicitly offset
        supportingContentWindows are the only visible text for that evidence ID.
        They can be cited together, but separate windows do not establish text
        in the gaps between them. An earlier window absent from the current
        evidence projection is not documentary proof retained by tool history.
        sourceStructure contains ingested section/heading and neighboring-chunk
        locators. Use them to orient research; they are metadata, not substantive
        body proof or model-approved item identities.
        """;

    private object[]? BuildNativeResearchFunctions(string userPrompt)
    {
        using var document = JsonDocument.Parse(userPrompt);
        var root = document.RootElement;
        if (!root.TryGetProperty("researchTools", out var research)) return null;
        var researchAllowed = research.TryGetProperty("researchAllowed", out var allowed)
                              && allowed.ValueKind == JsonValueKind.True;
        var evidenceReadingAllowed = researchAllowed
            || research.TryGetProperty("evidenceReadingAllowed", out var readingAllowed)
            && readingAllowed.ValueKind == JsonValueKind.True;
        if (!researchAllowed && !evidenceReadingAllowed) return null;
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
        var functions = new List<object>();
        if (researchAllowed)
            functions.Add(Function("search_corpus", "Semantic retrieval from the private indexed corpus. Scope to an observed source when needed; empty strings leave optional scopes empty.", new()
            {
                ["query"] = Text("Search wording chosen for the documentary fact."),
                ["sourceKey"] = Choice(sources.Prepend(string.Empty).ToArray()),
                ["category"] = Choice(categories), ["documentHint"] = Text("Empty when sourceKey is used."),
                ["topK"] = Count(1, 60)
            }));
        var readableIds = root.GetProperty("evidence").EnumerateArray()
            .Where(item => item.TryGetProperty("originalContentLength", out var size)
                           && size.TryGetInt32(out var length) && length > 0)
            .Select(item => ReadString(item, "evidenceId"))
            .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        if (readableIds.Length > 0)
            functions.Add(Function("read_evidence", "Read an exact character window of an already revalidated canonical excerpt. Use nextContentOffset to inspect text omitted from its current projection. At most one requested window per evidenceId in a batch. Evidence identity, source revision and original text remain unchanged. Earlier windows are proof only when explicitly present in the current supportingContentWindows; never infer the gaps between windows. This consumes a model turn but no additional corpus retrieval.", new()
            {
                ["evidenceId"] = Choice(readableIds), ["offset"] = Count(0, int.MaxValue),
                ["length"] = Count(256, 8_000)
            }));
        if (researchAllowed && sources.Length > 0)
        {
            functions.Add(Function("read_source", "Read a physical page window of an observed revision. Page bounds are inclusive; at most four pages per request. topK bounds every chunk intersecting the window: if the bounded result reports canonical_read_window_result_limit_exceeded, retry with a smaller window or a larger permitted topK instead of treating the source as absent. Useful for reading bodies located by contents or headings.", new()
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
        if (researchAllowed && root.GetProperty("evidence").GetArrayLength() > 0 && root.TryGetProperty("researchWorkspace", out var workspace)
            && workspace.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True)
            functions.Add(BuildResearchWorkspaceFunction(root));
        if (researchAllowed && root.GetProperty("evidence").GetArrayLength() > 0
            && root.TryGetProperty("candidateInventory", out var inventory)
            && inventory.TryGetProperty("enabled", out var inventoryEnabled)
            && inventoryEnabled.ValueKind == JsonValueKind.True)
            functions.Add(BuildCandidateInventoryFunction(root));
        return functions.Count == 0 ? null : functions.ToArray();
    }

    private HashSet<string> OfferedNativeResearchFunctionNames(string userPrompt)
    {
        var functions = BuildNativeResearchFunctions(userPrompt);
        if (functions is null) return new(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(functions, JsonOptions));
        return document.RootElement.EnumerateArray()
            .Select(item => ReadString(item.GetProperty("function"), "name"))
            .ToHashSet(StringComparer.Ordinal);
    }

    private bool TryBuildNativeArgumentRejection(JsonElement calls, string userPrompt,
        Exception error, out AdvancedAnalysisResearchArgumentFeedback? feedback)
    {
        feedback = null;
        // Only arguments of a genuine, offered call can be corrected. An unknown
        // operation or malformed call identity must never become an executable turn.
        if (calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() is < 1 or > 64
            || calls.GetRawText().Length > _options.NativeResearchMaximumHistoryCharacters)
            return false;
        var offered = OfferedNativeResearchFunctionNames(userPrompt);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var call in calls.EnumerateArray())
        {
            var id = ReadString(call, "id");
            if (ReadString(call, "type") != "function" || id.Length is < 1 or > 200 || !ids.Add(id)
                || !call.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object
                || !offered.Contains(ReadString(function, "name"))
                || !function.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.String)
                return false;
        }
        var reason = error is AdvancedAnalysisProviderException providerError
            ? providerError.ErrorCode : "native_function_arguments_json_invalid";
        feedback = new(reason, "native_function_batch", string.Empty, null, null, 4);
        return true;
    }

    private string NormalizeNativeResearchCalls(JsonElement calls, string userPrompt)
    {
        using var prompt = JsonDocument.Parse(userPrompt);
        var maximum = prompt.RootElement.GetProperty("researchTools").GetProperty("maximumQueries").GetInt32();
        if (calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() < 1
            || calls.GetArrayLength() > maximum
            || calls.GetRawText().Length > _options.NativeResearchMaximumHistoryCharacters)
            throw new AdvancedAnalysisProviderException("advanced_native_tool_protocol_invalid");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var offered = OfferedNativeResearchFunctionNames(userPrompt);
        var queries = new JsonArray();
        JsonNode? state = null;
        JsonNode? candidateInventoryUpdates = null;
        var evidenceReads = new JsonArray();
        foreach (var call in calls.EnumerateArray())
        {
            var id = ReadString(call, "id");
            if (ReadString(call, "type") != "function" || id.Length == 0 || id.Length > 200 || !ids.Add(id)
                || !call.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object)
                throw new AdvancedAnalysisProviderException("advanced_native_tool_protocol_invalid");
            var name = ReadString(function, "name");
            if (name is not ("search_corpus" or "read_source" or "find_source_text"
                or "read_evidence" or "save_research_state" or "save_candidate_inventory"))
                throw new AdvancedAnalysisProviderException("advanced_native_tool_protocol_invalid");
            if (!offered.Contains(name))
                throw new AdvancedAnalysisProviderException("advanced_native_tool_not_available");
            if (name == "read_evidence")
            {
                using var value = JsonDocument.Parse(ReadString(function, "arguments"));
                _ = ParseEvidenceReadArguments(value.RootElement, prompt.RootElement, out _);
                if (evidenceReads.Count >= 8)
                    throw new AdvancedAnalysisProviderException("advanced_native_evidence_read_invalid");
                evidenceReads.Add(JsonNode.Parse(value.RootElement.GetRawText()));
                continue;
            }
            if (name == "save_research_state")
            {
                if (state is not null) throw new AdvancedAnalysisProviderException("advanced_native_workspace_invalid");
                using var value = JsonDocument.Parse(ReadString(function, "arguments"));
                var items = ParseResearchWorkspace(value.RootElement, prompt.RootElement);
                state = JsonSerializer.SerializeToNode(new { items }, JsonOptions);
                continue;
            }
            if (name == "save_candidate_inventory")
            {
                if (candidateInventoryUpdates is not null)
                    throw new AdvancedAnalysisProviderException(
                        "advanced_native_candidate_inventory_invalid");
                using var value = JsonDocument.Parse(ReadString(function, "arguments"));
                var items = ParseCandidateInventoryUpdates(value.RootElement, prompt.RootElement);
                candidateInventoryUpdates = JsonSerializer.SerializeToNode(new { items }, JsonOptions);
                continue;
            }
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
        var normalized = new JsonObject { ["outcome"] = "research_required", ["queries"] = queries };
        if (state is not null) normalized["researchState"] = state;
        if (candidateInventoryUpdates is not null)
            normalized["candidateInventoryUpdates"] = candidateInventoryUpdates;
        if (evidenceReads.Count > 0) normalized["evidenceReads"] = evidenceReads;
        return normalized.ToJsonString(JsonOptions);
    }

    private IReadOnlyList<object> BuildNativeToolTurnMessages(NativeResearchTurn? turn,
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
        var documentaryInstructionWritten = false;
        foreach (var call in calls.RootElement.EnumerateArray())
        {
            if (turn.Rejection is not null)
            {
                var rejected = JsonSerializer.Serialize(new
                {
                    status = "batch_rejected_before_execution",
                    operation = ReadString(call.GetProperty("function"), "name"),
                    argumentFeedback = turn.Rejection,
                    visibleEvidenceIds = Array.Empty<string>(),
                    instruction = "No call in this batch executed and no state or evidence window changed. Correct the complete batch using the currently declared function schemas and current visible evidence."
                }, JsonOptions);
                messages.Add(responses ? new { type = "function_call_output", call_id = ReadString(call, "id"), output = rejected }
                    : (object)new { role = "tool", tool_call_id = ReadString(call, "id"), content = rejected });
                continue;
            }
            if (ReadString(call.GetProperty("function"), "name") == "read_evidence")
            {
                using var arguments = JsonDocument.Parse(ReadString(call.GetProperty("function"), "arguments"));
                var id = ReadString(arguments.RootElement, "evidenceId");
                var excerpt = visible.FirstOrDefault(item => item.EvidenceId == id);
                var output = JsonSerializer.Serialize(new
                {
                    status = turn.Rejection is not null ? "batch_rejected_before_execution"
                        : turn.EvidenceReadsStored ? "projected" : "not_changed",
                    operation = "read_evidence", evidenceId = id,
                    requestedWindow = arguments.RootElement.Clone(),
                    currentContentOffset = excerpt?.ContentOffset,
                    nextContentOffset = excerpt?.NextContentOffset,
                    supportingWindowOffsets = excerpt?.SupportingContentWindows
                        .Select(window => window.ContentOffset).ToArray() ?? [],
                    argumentFeedback = turn.Rejection,
                    instruction = "Exact canonical text is in current user.evidence under this unchanged evidence ID. Only current content and explicitly offset supportingContentWindows are proof; separate windows do not establish the text in their gaps."
                }, JsonOptions);
                messages.Add(responses ? new { type = "function_call_output", call_id = ReadString(call, "id"), output }
                    : (object)new { role = "tool", tool_call_id = ReadString(call, "id"), content = output });
                continue;
            }
            if (ReadString(call.GetProperty("function"), "name") == "save_research_state")
            {
                var output = JsonSerializer.Serialize(new { status = turn.Rejection is not null ? "batch_rejected_before_execution"
                    : turn.WorkspaceStored ? "stored" : "not_changed", operation = "save_research_state",
                    argumentFeedback = turn.Rejection, instruction = "Research memory only; it establishes no documentary facts. See current user.researchWorkspace and user.evidence." }, JsonOptions);
                messages.Add(responses ? new { type = "function_call_output", call_id = ReadString(call, "id"), output }
                    : (object)new { role = "tool", tool_call_id = ReadString(call, "id"), content = output });
                continue;
            }
            if (ReadString(call.GetProperty("function"), "name") == "save_candidate_inventory")
            {
                var output = JsonSerializer.Serialize(new
                {
                    status = turn.Rejection is not null
                        ? "batch_rejected_before_execution"
                        : turn.CandidateInventoryStored ? "stored" : "not_changed",
                    operation = "save_candidate_inventory",
                    argumentFeedback = turn.Rejection,
                    instruction = "Operational candidate memory only; it establishes no documentary facts. See current user.candidateInventory and user.evidence."
                }, JsonOptions);
                messages.Add(responses
                    ? new { type = "function_call_output", call_id = ReadString(call, "id"), output }
                    : (object)new
                    {
                        role = "tool",
                        tool_call_id = ReadString(call, "id"),
                        content = output
                    });
                continue;
            }
            var request = turn.Requests?.ElementAtOrDefault(index++);
            AdvancedAnalysisSearchObservation? observation = null;
            if (request is not null) turn.Results.TryGetValue(BuildSearchIdentity(request), out observation);
            var ids = observation?.Evidence.Select(e => e.Reference.EvidenceId)
                .Where(id => id is not null && visibleIds.Contains(id)).Distinct(StringComparer.Ordinal).ToArray() ?? [];
            var result = new
            {
                status = turn.Rejection is not null ? "batch_rejected_before_execution"
                    : observation?.NotExecutedAfterResourceLimit == true ? "not_executed_after_resource_limit"
                    : observation?.ResourceLimit is not null ? "resource_limit_before_evidence_admission"
                    : observation is null ? "not_executed_again" : "executed",
                operation = ReadString(call.GetProperty("function"), "name"),
                resolvedSource = request is null ? null : new { request.DocId, request.RevisionId, request.DocPath },
                argumentFeedback = turn.Rejection,
                returnedEvidenceCount = observation?.Evidence.Count,
                visibleEvidenceIds = ids.Take(8).ToArray(), visibleEvidenceIdsOmitted = Math.Max(0, ids.Length - 8),
                readDiagnostic = observation?.ReadDiagnostic, findDiagnostic = observation?.FindDiagnostic,
                 retrievalDiagnostic = BuildRetrievalDiagnosticForPrompt(observation?.RetrievalDiagnostic),
                 documentResolution = BuildDocumentResolutionForPrompt(observation?.DocumentResolution),
                 degradedRetrievers = BuildDegradedRetrieversForPrompt(observation?.DegradedRetrievers),
                resourceLimit = observation?.ResourceLimit,
                instruction = "Bounded operational result. Only current user.evidence excerpts are documentary proof. Historical source handles belong to their original turn; use current user.evidence handles, reidentifying sources through evidence IDs and resolved identity. Unlisted or omitted evidence is not proof of corpus absence."
            };
            var outputNode = JsonSerializer.SerializeToNode(result, JsonOptions)!.AsObject();
            foreach (var property in outputNode.Where(property => property.Value is null).ToArray())
                outputNode.Remove(property.Key);
            if (documentaryInstructionWritten) outputNode.Remove("instruction");
            documentaryInstructionWritten = true;
            var resultJson = outputNode.ToJsonString(JsonOptions);
            messages.Add(responses ? new { type = "function_call_output", call_id = ReadString(call, "id"), output = resultJson }
                : (object)new { role = "tool", tool_call_id = ReadString(call, "id"), content = resultJson });
        }
        var historyCharacters = JsonSerializer.Serialize(messages, JsonOptions).Length;
        if (historyCharacters > _options.NativeResearchMaximumHistoryCharacters)
        {
            var error = new AdvancedAnalysisProviderException("advanced_native_tool_history_limit_exceeded");
            error.Data["historyCharacters"] = historyCharacters;
            error.Data["maximumHistoryCharacters"] = _options.NativeResearchMaximumHistoryCharacters;
            throw error;
        }
        return messages;
    }
}
