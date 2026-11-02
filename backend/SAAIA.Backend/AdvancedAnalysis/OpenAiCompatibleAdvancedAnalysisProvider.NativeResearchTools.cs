using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Contracts;

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
        bool EvidenceReadsStored = false)
    {
        // Only admitted request metadata, captured before working-set rotation.
        // Original call order/IDs may contain aliases or duplicate operations.
        public IReadOnlyDictionary<string, AdvancedAnalysisSearchRequest> CallRequests { get; init; }
            = new Dictionary<string, AdvancedAnalysisSearchRequest>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, AdvancedAnalysisResultEvidence> CallSourceScopes { get; init; }
            = new Dictionary<string, AdvancedAnalysisResultEvidence>(StringComparer.Ordinal);
    }

    private sealed record NativeCallBindings(
        IReadOnlyDictionary<string, AdvancedAnalysisSearchRequest> Requests,
        IReadOnlyDictionary<string, AdvancedAnalysisResultEvidence> Sources);

    private const string NativeResearchContract = """
        If further documentary facts are needed and research is allowed, choose
        the appropriate declared function. Function results are bounded; current
        source excerpts are in user.evidence; active opaque source handles remain
        available in user.sourceCatalogue even when their bodies are not visible.
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
        The current evidence.content window, explicitly offset
        supportingContentWindows and current structuredTables rows bound to that
        evidenceId are the visible documentary text for that evidence ID.
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
        var sources = SourceKeysForNativeTools(root);
        var categories = research.GetProperty("availableCategories").EnumerateArray()
            .Select(e => e.GetString() ?? string.Empty).Prepend(string.Empty)
            .Distinct(StringComparer.Ordinal).ToArray();
        object Text(string description) => new { type = "string", description };
        object Choice(string[] values) => new { type = "string", @enum = values };
        object Count(int minimum, int maximum) => new { type = "integer", minimum, maximum };
        object OptionalCount(int minimum,int maximum) => new { type=new[] {"integer","null"},minimum,maximum };
        object Function(string name, string description, Dictionary<string, object> properties)
            => new { type = "function", function = new { name, description, strict = true,
                parameters = new { type = "object", properties, required = properties.Keys.ToArray(), additionalProperties = false } } };
        var functions = new List<object>();
        if (researchAllowed)
            functions.Add(Function("search_corpus", CorpusSearchQueryPolicy
                + " Scope to an observed source when needed; empty strings leave optional scopes empty.", new()
            {
                ["query"] = Text("One distinctive concept or an exact observed title; alternative names belong in separate queries."),
                ["sourceKey"] = Choice(sources.Prepend(string.Empty).ToArray()),
                ["category"] = Choice(categories), ["documentHint"] = Text("Empty when sourceKey is used."),
                ["topK"] = Count(1, 60)
            }));
        var readableIds = root.GetProperty("evidence").EnumerateArray()
            .Where(item => !item.TryGetProperty("sourceObservation", out var secondary) || secondary.ValueKind == JsonValueKind.Null)
            .Where(item => !item.TryGetProperty("nativeObservation", out var native) || native.ValueKind == JsonValueKind.Null)
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
            var readProperties = new Dictionary<string, object>
            {
                ["sourceKey"] = Choice(sources), ["pageStart"] = Count(1, int.MaxValue),
                ["pageEnd"] = Count(1, int.MaxValue), ["topK"] = Count(1, 60),
                ["tableOrdinal"] = OptionalCount(0,int.MaxValue),
                ["tableRowOffset"] = OptionalCount(0,AdvancedAnalysisTableFragment.MaximumRows-1),
                ["tableRowLimit"] = OptionalCount(1,AdvancedAnalysisStructuredTableAssembler.MaximumRowLimit)
            };
            if (HasSecondaryTableReadings(root))
            {
                readProperties["tableObservationId"] = new { type = new[] { "string", "null" }, description = "Exact observed ID; null for inventory or primary read." };
                readProperties["tableObservationView"] = new { type = new[] { "string", "null" }, @enum = new string?[] { null, "inventory", "raw", "physical_band", "unassigned" } };
                readProperties["tableObservationBandOrdinal"] = OptionalCount(0, 31);
                readProperties["tableObservationLineOffset"] = OptionalCount(0, 5_000);
                readProperties["tableObservationLineLimit"] = OptionalCount(1, 64);
            }
            if (HasVisualSourceCitations(root))
            {
                readProperties["includeVisualSources"] = new
                {
                    type = "boolean",
                    description = "Immutable image metadata only; supplies no pixels to the model."
                };
                readProperties["visualSourceOffset"] = OptionalCount(0, CanonicalVisualAssetRepo.MaximumDocumentAssets);
                readProperties["visualSourceLimit"] = OptionalCount(1, 64);
            }
            if (HasNativeObservationRead(root))
            {
                readProperties["nativeObservationView"] = new { type = new[] { "string", "null" }, @enum = new string?[] { null, "inventory", "raw" } };
                readProperties["nativeObservationBlockId"] = new { type = new[] { "string", "null" }, description = "Observed blockId for raw only; null for inventory or primary read." };
                readProperties["nativeObservationRawSha256"] = new { type = new[] { "string", "null" }, description = "Observed rawTextSha256 for raw only; null for inventory or primary read." };
                readProperties["nativeObservationOffset"] = OptionalCount(0, int.MaxValue);
                readProperties["nativeObservationLimit"] = OptionalCount(1, 64);
            }
            functions.Add(Function("read_source", HasSecondaryTableReadings(root)
                ? "Read an observed physical source window. Primary reads use up to four pages. Secondary observation reads require exactly one page and the explicit observation view; do not mix primary row parameters. A secondary line is indivisible and may be refused when it does not fit. Raw remains readable when physical bands are unavailable."
                : "Read a physical page window of an observed revision. Page bounds are inclusive; at most four pages per request. topK bounds every chunk intersecting the window: if the bounded result reports canonical_read_window_result_limit_exceeded, retry with a smaller window or a larger permitted topK instead of treating the source as absent. Useful for reading bodies located by contents or headings.", readProperties));
            functions.Add(Function("find_source_text", "Locate literal words or an observed title inside one observed revision. Case and whitespace normalized; punctuation remains literal. Results follow physical page order, so contents or index matches may precede substantive bodies. topK counts chunks, not distinct items. Inspect contentRole and use pagination or an observed physical-page read when a body is still needed. Returns canonical excerpts and pagination diagnostics; no semantic search fallback.", new()
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
            var optionalProperties=name=="read_source" ? new[] { "tableOrdinal","tableRowOffset","tableRowLimit" }
                .Concat(HasSecondaryTableReadings(prompt.RootElement) ? new[] { "tableObservationId", "tableObservationView",
                    "tableObservationBandOrdinal", "tableObservationLineOffset", "tableObservationLineLimit" } : [])
                .Concat(HasVisualSourceCitations(prompt.RootElement)
                    ? new[] { "includeVisualSources", "visualSourceOffset", "visualSourceLimit" } : [])
                .Concat(HasNativeObservationRead(prompt.RootElement)
                    ? new[] { "nativeObservationView", "nativeObservationBlockId", "nativeObservationRawSha256", "nativeObservationOffset", "nativeObservationLimit" } : []).ToArray() : [];
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Length
                || names.Any(n => !properties.Contains(n, StringComparer.Ordinal) && !optionalProperties.Contains(n,StringComparer.Ordinal))
                || properties.Any(n => !names.Contains(n, StringComparer.Ordinal)))
                throw new AdvancedAnalysisProviderException("advanced_native_tool_protocol_invalid");
            if (name == "read_source")
                RequireObservedNativeRawLocator(arguments.RootElement, prompt.RootElement);
            queries.Add(BuildNormalizedNativeQuery(arguments.RootElement, name));
        }
        var normalized = new JsonObject { ["outcome"] = "research_required", ["queries"] = queries };
        if (state is not null) normalized["researchState"] = state;
        if (candidateInventoryUpdates is not null)
            normalized["candidateInventoryUpdates"] = candidateInventoryUpdates;
        if (evidenceReads.Count > 0) normalized["evidenceReads"] = evidenceReads;
        return normalized.ToJsonString(JsonOptions);
    }

    private static JsonObject BuildNormalizedNativeQuery(JsonElement arguments, string operation)
    {
        var query = JsonNode.Parse(arguments.GetRawText())!.AsObject();
        query["operation"] = operation;
        return query;
    }

    private NativeCallBindings BuildNativeCallRequestBindings(
        string callsJson, AdvancedAnalysisProviderRequest request,
        IReadOnlyList<string> availableCategories, IReadOnlyList<PromptEvidenceItem> observations,
        IReadOnlyList<AdvancedAnalysisSearchRequest> admittedRequests)
    {
        var admitted = admittedRequests.GroupBy(BuildSearchIdentity, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var bindings = new Dictionary<string, AdvancedAnalysisSearchRequest>(StringComparer.Ordinal);
        var sources = new Dictionary<string, AdvancedAnalysisResultEvidence>(StringComparer.Ordinal);
        using var calls = JsonDocument.Parse(callsJson);
        foreach (var call in calls.RootElement.EnumerateArray())
        {
            var operation = ReadString(call.GetProperty("function"), "name");
            if (operation is not ("search_corpus" or "read_source" or "find_source_text")) continue;
            try
            {
                // The whole native batch already passed its offered schemas.
                // Reuse its query construction and the exact admission parser,
                // including defaults and requested-document scoping. Intersect
                // afterward: this step cannot admit or execute another call.
                using var arguments = JsonDocument.Parse(ReadString(call.GetProperty("function"), "arguments"));
                var parsed = ParseResearchReview(JsonSerializer.Serialize(new
                {
                    decision = "search_more", queries = new[] { BuildNormalizedNativeQuery(arguments.RootElement, operation) }
                }, JsonOptions), request, availableCategories, observations);
                if (parsed.Count == 1 && admitted.TryGetValue(BuildSearchIdentity(parsed[0]), out var canonical))
                {
                    var id = ReadString(call, "id");
                    bindings.Add(id, canonical);
                    var sourceKey = ReadString(arguments.RootElement, "sourceKey");
                    if (sourceKey.Length > 0 && ResolveObservedSource(observations, sourceKey) is { } source)
                        sources.Add(id, source);
                }
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or AdvancedAnalysisProviderException)
            {
                // An invalid, truncated-away or unadmitted call gets no proof;
                // another call's position can never become its result binding.
            }
        }
        return new(bindings, sources);
    }

    private IReadOnlyList<object> BuildNativeToolTurnMessages(NativeResearchTurn? turn,
        IReadOnlyList<PromptEvidenceItem> visible, bool responses = false)
        => BuildNativeToolTurnMessagesCore(turn, visible, responses,
            emitDiagnostics: visible is PromptEvidenceProjection);

    private IReadOnlyList<object> BuildNativeToolTurnMessagesCore(NativeResearchTurn? turn,
        IReadOnlyList<PromptEvidenceItem> visible, bool responses, bool emitDiagnostics)
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
        var matchedCalls = 0; var unmatchedCalls = 0; var replayedCalls = 0;
        var requestIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var returnedIds = new HashSet<string>(StringComparer.Ordinal);
        var projectedIds = new HashSet<string>(StringComparer.Ordinal);
        var partialIds = new HashSet<string>(StringComparer.Ordinal);
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
            var callId = ReadString(call, "id");
            turn.CallRequests.TryGetValue(callId, out var request);
            AdvancedAnalysisSearchObservation? observation = null;
            if (request is not null) turn.Results.TryGetValue(BuildSearchIdentity(request), out observation);
            if (observation is not null && turn.CallSourceScopes.TryGetValue(callId, out var source)
                && observation.Evidence.Any(item => !string.Equals(item.Reference.DocId, source.DocId, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(item.Reference.RevisionId, source.RevisionId, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(item.Reference.SourceHash, source.SourceHash, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(AdvancedAnalysisToolGateway.NormalizePath(item.Reference.DocPath),
                        AdvancedAnalysisToolGateway.NormalizePath(source.DocPath), StringComparison.Ordinal)))
            {
                request = null; observation = null;
            }
            var identity = request is null ? null : BuildSearchIdentity(request);
            var reusedResult = identity is not null && !requestIdentities.Add(identity);
            if (request is null) unmatchedCalls++; else matchedCalls++;
            if (reusedResult && observation is not null) replayedCalls++;
            var ids = observation?.Evidence.Select(e => e.Reference.EvidenceId)
                .Where(id => !string.IsNullOrWhiteSpace(id) && visibleIds.Contains(id)).Cast<string>().Distinct(StringComparer.Ordinal).ToArray() ?? [];
            var allIds = observation?.Evidence.Select(item => item.Reference.EvidenceId)
                .Where(id => !string.IsNullOrWhiteSpace(id)).Cast<string>().Distinct(StringComparer.Ordinal).ToArray() ?? [];
            var omittedIds = allIds.Where(id => !visibleIds.Contains(id)).ToArray();
            var partial = visible.Where(item => allIds.Contains(item.EvidenceId, StringComparer.Ordinal) && item.ContentTruncated).ToArray();
            returnedIds.UnionWith(allIds); projectedIds.UnionWith(ids.Cast<string>());
            partialIds.UnionWith(partial.Select(item => item.EvidenceId!).Where(id => id is not null));
            var result = new
            {
                status = turn.Rejection is not null ? "batch_rejected_before_execution"
                    : request is null ? "result_correlation_unavailable"
                    : observation?.NotExecutedAfterResourceLimit == true ? "not_executed_after_resource_limit"
                    : observation?.ResourceLimit is not null ? "resource_limit_before_evidence_admission"
                    : observation is null ? "not_executed_again" : "executed",
                operation = ReadString(call.GetProperty("function"), "name"),
                resolvedSource = request is null ? null : new { request.DocId, request.RevisionId, request.DocPath },
                argumentFeedback = turn.Rejection,
                returnedEvidenceCount = observation?.Evidence.Count,
                resultReusedWithinBatch = reusedResult && observation is not null ? true : (bool?)null,
                visibleEvidenceIds = ids.Take(8).ToArray(), visibleEvidenceIdsOmitted = Math.Max(0, ids.Length - 8),
                readDiagnostic = observation?.ReadDiagnostic, findDiagnostic = observation?.FindDiagnostic,
                 retrievalDiagnostic = BuildRetrievalDiagnosticForPrompt(observation?.RetrievalDiagnostic, observation?.DegradedRetrievers),
                 documentResolution = BuildDocumentResolutionForPrompt(observation?.DocumentResolution),
                 degradedRetrievers = BuildDegradedRetrieversForPrompt(observation?.DegradedRetrievers),
                resourceLimit = observation?.ResourceLimit,
                structuredTableCount=observation?.StructuredTables?.Count,
                instruction = "Operational metadata only, not documentary facts or item counts. Cite current user.evidence/structuredTables only. For omitted bodies request a smaller batch or read_source at an observed page with a current sourceKey; for shortened visible bodies use read_evidence at nextContentOffset. Navigation is a locator, not a body; gaps do not prove absence."
            };
            var outputNode = JsonSerializer.SerializeToNode(result, JsonOptions)!.AsObject();
            // Native/secondary windows already carry exact line/cursor and
            // omission diagnostics below. Do not duplicate that bounded wire.
            var primaryResult = request?.NativeObservationView is null && request?.TableObservationView is null
                && observation?.AlternativeNativeReadings is null && observation?.AlternativeTableReadings is null;
            if (primaryResult && observation is not null)
            {
                JsonObject? feedback = null;
                if (omittedIds.Length > 0)
                {
                    feedback = new JsonObject
                    {
                        ["status"] = "some_returned_evidence_omitted",
                        ["visibleEvidenceIdCount"] = ids.Length,
                        ["omittedEvidenceIdCount"] = omittedIds.Length,
                        ["omittedEvidenceIds"] = JsonSerializer.SerializeToNode(omittedIds.Take(8), JsonOptions),
                        ["omittedPhysicalLocators"] = JsonSerializer.SerializeToNode(observation.Evidence
                            .Where(item => omittedIds.Contains(item.Reference.EvidenceId, StringComparer.Ordinal))
                            .DistinctBy(item => item.Reference.EvidenceId).Take(8).Select(item => new
                            { item.Reference.EvidenceId, item.Reference.PageStart, item.Reference.PageEnd }), JsonOptions)
                    };
                    if (omittedIds.Length > 8) feedback["omittedEvidenceIdsOmittedCount"] = omittedIds.Length - 8;
                }
                else if (allIds.Length == 0 && request?.Operation == "find_source_text")
                    feedback = new JsonObject { ["status"] = "no_evidence_returned" };
                var primaryPartialCount = partial.Count(item => item.SourceObservation is null && item.NativeObservation is null);
                // Existing evidence.contentTruncated/nextContentOffset already
                // describe visible windows. Add this count only beside a real
                // omission alert, not to every otherwise complete tool result.
                if (feedback is not null && primaryPartialCount > 0)
                    feedback["partiallyVisibleEvidenceCount"] = primaryPartialCount;
                if (feedback is not null) outputNode["projectionFeedback"] = feedback;
            }
            if (_options.NativeObservationReadEnabled && request?.NativeObservationView is not null)
                outputNode["instruction"] = "Native audit proof uses whole text in current user.evidence; metadata references below point into this same request. Historical or omitted lines are not proof.";
            var alternative = BuildAlternativeTableReadingsForPrompt(visible.Where(item =>
                ids.Contains(item.EvidenceId, StringComparer.Ordinal)).ToArray());
            if (alternative is not null)
                outputNode["alternativeTableReadings"] = TableReadingsForNativeToolResult(alternative, request, visible);
            var nativeAlternative = BuildAlternativeNativeReadingsForPrompt(visible.Where(item =>
                ids.Contains(item.EvidenceId, StringComparer.Ordinal)).ToArray());
            if (nativeAlternative is not null)
                outputNode["alternativeNativeReadings"] = NativeReadingsForToolResult(nativeAlternative, visible, request);
            if (observation?.AlternativeNativeReadings is { Inventory: not null } nativeInventory)
                outputNode["nativeInventory"] = NativeInventoryForToolResult(nativeInventory, request, visible);
            if (observation?.AlternativeNativeReadings?.View is { Lines.Count: 0 } nativeWindow)
                outputNode["nativeReadWindow"] = JsonSerializer.SerializeToNode(NativeWindowMetadata(nativeWindow), JsonOptions);
            if (observation?.AlternativeTableReadings?.Inventory is { } secondaryInventory)
                outputNode["secondaryInventory"] = JsonSerializer.SerializeToNode(secondaryInventory, JsonOptions);
            if (observation?.VisualSources is { } visualSources)
                outputNode["visualSources"] = JsonSerializer.SerializeToNode(visualSources, JsonOptions);
            if (observation?.AlternativeTableReadings?.View is { } emptyView && emptyView.Lines is not { Count: > 0 })
                outputNode["secondaryReadWindow"] = JsonSerializer.SerializeToNode(new
                {
                    emptyView.ObservationId, emptyView.TableId, emptyView.PhysicalPage, emptyView.View,
                    emptyView.BandOrdinal, emptyView.LineOffset, emptyView.LineLimit, emptyView.RawLineCount,
                    emptyView.ViewLineCount, emptyView.ReturnedLineCount, emptyView.NextLineOffset,
                    emptyView.Status, emptyView.PhysicalReadingAvailability, emptyView.QualitySignals,
                    emptyView.SemanticAccuracy, emptyView.PdfCoverageVerified, emptyView.CanonicalGridRepaired
                }, JsonOptions);
            foreach (var property in outputNode.Where(property => property.Value is null).ToArray())
                outputNode.Remove(property.Key);
            if (documentaryInstructionWritten)
            {
                outputNode.Remove("instruction");
            }
            documentaryInstructionWritten = true;
            var resultJson = outputNode.ToJsonString(JsonOptions);
            messages.Add(responses ? new { type = "function_call_output", call_id = ReadString(call, "id"), output = resultJson }
                : (object)new { role = "tool", tool_call_id = ReadString(call, "id"), content = resultJson });
        }
        var historyCharacters = JsonSerializer.Serialize(messages, JsonOptions).Length;
        // Budget fitting renders temporary arrays repeatedly. Only the actual
        // canonical projection emits one content-free correlation observation.
        if (emitDiagnostics)
        {
            using var probe = BackendDiagnostics.Start("advanced.native_tool_results", _logger);
            probe.Set("native_call_count", calls.RootElement.GetArrayLength());
            probe.Set("batch_rejected_before_execution", turn.Rejection is not null);
            probe.Set("matched_retrieval_call_count", matchedCalls); probe.Set("unmatched_retrieval_call_count", unmatchedCalls);
            probe.Set("unique_normalized_request_count", requestIdentities.Count); probe.Set("replayed_result_call_count", replayedCalls);
            probe.Set("returned_evidence_unique_count", returnedIds.Count); probe.Set("visible_evidence_unique_count", projectedIds.Count);
            probe.Set("omitted_evidence_unique_count", returnedIds.Count - projectedIds.Count); probe.Set("partially_visible_evidence_unique_count", partialIds.Count);
            // Report the real outer HTTP encoding, while retaining the
            // existing conservative count for fitting, rejection and outcome.
            probe.Set("history_characters", JsonSerializer.Serialize(messages, WireJsonOptions.Value).Length);
            probe.Set("history_fitting_characters", historyCharacters);
            probe.Set("history_maximum_characters", _options.NativeResearchMaximumHistoryCharacters);
            probe.Set("tool_execution_performed", false);
            probe.Complete(turn.Rejection is not null || historyCharacters > _options.NativeResearchMaximumHistoryCharacters || unmatchedCalls > 0 ? "incomplete" : "success",
                turn.Rejection is not null ? "native_batch_rejected_before_execution"
                    : historyCharacters > _options.NativeResearchMaximumHistoryCharacters ? "native_history_budget_exceeded"
                    : unmatchedCalls > 0 ? "native_result_correlation_unavailable" : null);
        }
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
