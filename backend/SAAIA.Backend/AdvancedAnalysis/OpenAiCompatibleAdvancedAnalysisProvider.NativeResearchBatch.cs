using System.Text.Json;
using System.Text.Json.Nodes;

namespace SAAIA.Backend.AdvancedAnalysis;

internal sealed partial class OpenAiCompatibleAdvancedAnalysisProvider
{
    private sealed record PreparedNativeResearchBatch(
        string Content,
        string AdmittedCallsJson,
        IReadOnlySet<string> DeferredCallIds,
        int Limit);

    private PreparedNativeResearchBatch PrepareNativeResearchBatch(
        string callsJson,
        string normalizedContent,
        string userPrompt,
        AdvancedAnalysisProviderRequest request,
        IReadOnlyList<string> availableCategories,
        IReadOnlyList<PromptEvidenceItem> observations,
        SynthesisResearchContext context,
        IReadOnlyList<PromptEvidenceItem>? evidenceReadObservations = null)
    {
        using var calls = JsonDocument.Parse(callsJson);
        // CompleteJson already validated every offered argument and cross-call
        // constraint against the final wire prompt. Preserve that normalized
        // representation, including optional capabilities added during projection.
        using var normalized = JsonDocument.Parse(UnwrapJson(normalizedContent));
        using var prompt = JsonDocument.Parse(userPrompt);
        _ = ParseEvidenceReadRequests(normalized.RootElement, evidenceReadObservations ?? observations, context);
        if (normalized.RootElement.TryGetProperty("candidateInventoryUpdates", out var candidateUpdates))
        {
            var updates = ParseCandidateInventoryUpdates(candidateUpdates, prompt.RootElement);
            _ = MergeCandidateInventory(context.CandidateInventory, updates, out _);
        }
        var queries = normalized.RootElement.GetProperty("queries").EnumerateArray().ToArray();
        var queryIndex = 0;
        foreach (var call in calls.RootElement.EnumerateArray())
        {
            var function = call.GetProperty("function");
            var operation = ReadString(function, "name");
            if (operation is not ("search_corpus" or "read_source" or "find_source_text"
                or AdvancedAnalysisSourceTitlePage.Operation))
                continue;
            if (queryIndex >= queries.Length)
                throw new AdvancedAnalysisProviderException("advanced_native_tool_protocol_invalid");
            var parsed = ParseResearchReview(JsonSerializer.Serialize(new
            {
                decision = "search_more",
                queries = new[] { queries[queryIndex++] }
            }, JsonOptions), request, availableCategories, observations);
            if (parsed.Count != 1)
                throw new AdvancedAnalysisProviderException("advanced_native_tool_arguments_invalid");
        }

        if (queryIndex != queries.Length)
            throw new AdvancedAnalysisProviderException("advanced_native_tool_protocol_invalid");
        var limit = Math.Min(ResolveMaximumPlanQueries(request),
            prompt.RootElement.GetProperty("researchTools").GetProperty("maximumQueries").GetInt32());
        if (limit < 1)
            throw new AdvancedAnalysisProviderException("advanced_native_tool_protocol_invalid");
        var admitted = calls.RootElement.EnumerateArray().Take(limit).ToArray();
        var deferred = calls.RootElement.EnumerateArray().Skip(limit)
            .Select(call => ReadString(call, "id")).ToHashSet(StringComparer.Ordinal);
        var admittedJson = JsonSerializer.Serialize(admitted, JsonOptions);
        var admittedNames = admitted.Select(call => ReadString(call.GetProperty("function"), "name")).ToArray();
        var content = normalizedContent;
        if (deferred.Count > 0)
        {
            var prefix = JsonNode.Parse(normalizedContent)!.AsObject();
            var admittedQueries = admittedNames.Count(name => name is "search_corpus" or "read_source"
                or "find_source_text" or AdvancedAnalysisSourceTitlePage.Operation);
            prefix["queries"] = JsonSerializer.SerializeToNode(queries.Take(admittedQueries), JsonOptions);
            if (!admittedNames.Contains("save_research_state", StringComparer.Ordinal))
                prefix.Remove("researchState");
            if (!admittedNames.Contains("save_candidate_inventory", StringComparer.Ordinal))
                prefix.Remove("candidateInventoryUpdates");
            var admittedReads = admittedNames.Count(name => name == "read_evidence");
            if (admittedReads == 0)
                prefix.Remove("evidenceReads");
            else
                prefix["evidenceReads"] = JsonSerializer.SerializeToNode(normalized.RootElement
                    .GetProperty("evidenceReads").EnumerateArray().Take(admittedReads), JsonOptions);
            content = prefix.ToJsonString(JsonOptions);
        }
        if (deferred.Count > 0)
        {
            using var probe = BackendDiagnostics.Start("llm.native_batch_limit", _logger);
            probe.Set("job_id", request.JobId);
            probe.Set("requested_call_count", calls.RootElement.GetArrayLength());
            probe.Set("admitted_call_count", admitted.Length);
            probe.Set("deferred_call_count", deferred.Count);
            probe.Complete("continued", "validated_tail_deferred");
        }
        return new(content, admittedJson, deferred, limit);
    }
}
