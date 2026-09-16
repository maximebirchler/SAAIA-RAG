using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("responses")]
    [InlineData("chat-completions")]
    public async Task Evidence_capacity_refusal_preserves_prior_proofs_and_all_call_results(string protocol)
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            CapacityBatch(protocol),
            protocol == "responses" ? NativeResponseAnswer(NativeAnswered) : Completion(NativeAnswered),
            protocol == "responses" ? NativeResponseAnswer(NativeAnswered) : Completion(NativeAnswered));
        var gateway = new CapacityRefusingGateway(BuildEvidence("E1", "Le seuil est 17 bar."));
        var options = WorkspaceOptions(protocol);
        options.SemanticCriticEnabled = true;
        options.ExternalMaximumCallsPerJob = 5;
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        Assert.Equal("answered", result.Outcome);
        Assert.Equal(4, factory.Requests.Count);
        Assert.Equal(3, gateway.Searches.Count);
        Assert.Equal("E1", Assert.Single(result.Claims).EvidenceIds[0]);
        var inputName = protocol == "responses" ? "input" : "messages";
        using var request = JsonDocument.Parse(factory.Requests[2].Body);
        var input = request.RootElement.GetProperty(inputName).EnumerateArray().ToArray();
        var outputs = input.Where(i => protocol == "responses"
            ? i.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"
            : i.GetProperty("role").GetString() == "tool").ToArray();
        Assert.Equal(3, outputs.Length);
        var statuses = outputs.Select(i => JsonDocument.Parse(i.GetProperty(protocol == "responses"
            ? "output" : "content").GetString()!)).ToArray();
        try
        {
            Assert.Equal("executed", statuses[0].RootElement.GetProperty("status").GetString());
            Assert.Equal("resource_limit_before_evidence_admission", statuses[1].RootElement.GetProperty("status").GetString());
            Assert.Equal("not_executed_after_resource_limit", statuses[2].RootElement.GetProperty("status").GetString());
            Assert.Empty(statuses[1].RootElement.GetProperty("visibleEvidenceIds").EnumerateArray());
            Assert.Empty(statuses[2].RootElement.GetProperty("visibleEvidenceIds").EnumerateArray());
        }
        finally { foreach (var status in statuses) status.Dispose(); }
        foreach (var completion in factory.Requests.Skip(2))
        {
            using var body = JsonDocument.Parse(completion.Body);
            using var prompt = JsonDocument.Parse(body.RootElement.GetProperty(inputName).EnumerateArray()
                .Single(i => i.TryGetProperty("role", out var role) && role.GetString() == "user")
                .GetProperty("content").GetString()!);
            Assert.False(prompt.RootElement.GetProperty("researchTools").GetProperty("researchAllowed").GetBoolean());
            Assert.Equal("accumulated_evidence_limit_exceeded", prompt.RootElement.GetProperty("researchResourceLimit")
                .GetProperty("reasonCode").GetString());
            Assert.Equal(2, prompt.RootElement.GetProperty("researchResourceLimit").GetProperty("maximumEvidenceItems").GetInt32());
            Assert.Equal(1, prompt.RootElement.GetProperty("researchResourceLimit").GetProperty("consumedEvidenceItems").GetInt32());
            Assert.Contains(prompt.RootElement.GetProperty("evidence").EnumerateArray(),
                i => i.GetProperty("evidenceId").GetString() == "E1");
        }
        if (protocol == "responses")
        {
            var state = Assert.Single(input, i => i.TryGetProperty("type", out var type) && type.GetString() == "reasoning");
            Assert.Equal(new string('s', 100), state.GetProperty("encrypted_content").GetString());
        }
    }

    [Theory]
    [InlineData("responses", false)]
    [InlineData("chat-completions", false)]
    [InlineData("responses", true)]
    [InlineData("chat-completions", true)]
    public async Task Security_failures_and_initial_capacity_failures_remain_fatal(string protocol, bool initial)
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""), CapacityBatch(protocol));
        var expected = initial ? "accumulated_evidence_limit_exceeded" : "retrieval_evidence_revalidation_failed";
        var gateway = new CapacityRefusingGateway(BuildEvidence("E1", "Le seuil est 17 bar."),
            initial ? 1 : 3, expected);
        var error = await Assert.ThrowsAsync<AdvancedAnalysisToolException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(protocol), null)
                .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None));
        Assert.Equal(expected, error.ErrorCode);
        Assert.Equal(initial ? 1 : 2, factory.Requests.Count);
        Assert.Equal(initial ? 1 : 3, gateway.Searches.Count);
    }

    private static HttpResponseMessage CapacityBatch(string protocol)
        => protocol == "responses" ? HistoryNativeReadBatch(100, 3) : NativeCompletion(
            ("read_source", """{"sourceKey":"internal-source-1","pageStart":1,"pageEnd":4,"topK":12}"""),
            ("read_source", """{"sourceKey":"internal-source-1","pageStart":5,"pageEnd":8,"topK":12}"""),
            ("read_source", """{"sourceKey":"internal-source-1","pageStart":9,"pageEnd":12,"topK":12}"""));

    [Theory]
    [InlineData("responses")]
    [InlineData("chat-completions")]
    public async Task Local_read_limit_allows_model_correction_on_same_window_and_preserves_other_outputs(string protocol)
    {
        const string corrected = """{"sourceKey":"internal-source-1","pageStart":5,"pageEnd":8,"topK":24}""";
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""), CapacityBatch(protocol),
            protocol == "responses" ? NativeResponseFunction("read_source", corrected) : NativeCompletion(("read_source", corrected)),
            protocol == "responses" ? NativeResponseAnswer(NativeAnswered) : Completion(NativeAnswered),
            protocol == "responses" ? NativeResponseAnswer(NativeAnswered) : Completion(NativeAnswered));
        var gateway = new CapacityRefusingGateway(BuildEvidence("E1", "Le seuil est 17 bar."),
            failureCode: "canonical_read_window_result_limit_exceeded");
        var options = WorkspaceOptions(protocol);
        options.SemanticCriticEnabled = true;
        options.ExternalMaximumCallsPerJob = 6;
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        Assert.Equal("answered", result.Outcome);
        Assert.Equal(5, factory.Requests.Count);
        Assert.Equal(5, gateway.Searches.Count);
        Assert.Equal(24, gateway.Searches[4].TopK);
        Assert.Equal(gateway.Searches[2].PageStart, gateway.Searches[4].PageStart);
        Assert.Equal(gateway.Searches[2].PageEnd, gateway.Searches[4].PageEnd);
        Assert.Equal("E1", Assert.Single(result.Claims).EvidenceIds[0]);
        using var body = JsonDocument.Parse(factory.Requests[2].Body);
        var input = body.RootElement.GetProperty(protocol == "responses" ? "input" : "messages").EnumerateArray().ToArray();
        var outputs = input.Where(i => protocol == "responses"
            ? i.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"
            : i.GetProperty("role").GetString() == "tool").ToArray();
        Assert.Equal(3, outputs.Length);
        var statuses = new List<string?>();
        foreach (var output in outputs)
        {
            using var value = JsonDocument.Parse(output.GetProperty(protocol == "responses" ? "output" : "content").GetString()!);
            statuses.Add(value.RootElement.GetProperty("status").GetString());
        }
        Assert.Equal(new[] { "executed", "resource_limit_before_evidence_admission", "executed" }, statuses);
        using var refused = JsonDocument.Parse(outputs[1].GetProperty(protocol == "responses" ? "output" : "content").GetString()!);
        var refusal = refused.RootElement.GetProperty("resourceLimit");
        Assert.Equal("canonical_read_window_result_limit_exceeded", refusal.GetProperty("reasonCode").GetString());
        Assert.Equal(12, refusal.GetProperty("requestedTopK").GetInt32());
        Assert.False(refusal.GetProperty("stopsResearch").GetBoolean());
        Assert.Equal(JsonValueKind.Null, refusal.GetProperty("observedAtLeastChunkCount").ValueKind);
        Assert.Empty(refused.RootElement.GetProperty("visibleEvidenceIds").EnumerateArray());
        using var prompt = JsonDocument.Parse(input.Single(i => i.TryGetProperty("role", out var role)
            && role.GetString() == "user").GetProperty("content").GetString()!);
        Assert.True(prompt.RootElement.GetProperty("researchTools").GetProperty("researchAllowed").GetBoolean());
        Assert.False(prompt.RootElement.TryGetProperty("researchResourceLimit", out _));
    }

    [Theory]
    [InlineData("responses", true)]
    [InlineData("chat-completions", true)]
    [InlineData("responses", false)]
    [InlineData("chat-completions", false)]
    public async Task Local_read_limit_without_native_call_outputs_remains_fatal(string protocol, bool initial)
    {
        using var factory = initial
            ? new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""))
            : new QueuedHttpClientFactory(
                Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
                protocol == "responses"
                    ? NativeResponseAnswer("""{"outcome":"research_required","queries":[{"query":"next","topK":12}]}""")
                    : Completion("""{"outcome":"research_required","queries":[{"query":"next","topK":12}]}"""));
        var gateway = new CapacityRefusingGateway(BuildEvidence("E1", "Le seuil est 17 bar."),
            initial ? 1 : 2, "canonical_read_window_result_limit_exceeded");
        var error = await Assert.ThrowsAsync<AdvancedAnalysisToolException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(protocol), null)
                .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None));
        Assert.Equal("canonical_read_window_result_limit_exceeded", error.ErrorCode);
        Assert.Equal(initial ? 1 : 2, factory.Requests.Count);
        Assert.Equal(initial ? 1 : 2, gateway.Searches.Count);
    }

    [Theory]
    [InlineData("responses")]
    [InlineData("chat-completions")]
    public async Task Local_read_limit_code_on_non_read_native_operation_remains_fatal(string protocol)
    {
        const string search = """{"query":"next","sourceKey":"","category":"","documentHint":"","topK":12}""";
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            protocol == "responses" ? NativeResponseFunction("search_corpus", search)
                : NativeCompletion(("search_corpus", search)));
        var gateway = new CapacityRefusingGateway(BuildEvidence("E1", "Le seuil est 17 bar."),
            2, "canonical_read_window_result_limit_exceeded");
        var error = await Assert.ThrowsAsync<AdvancedAnalysisToolException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(protocol), null)
                .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None));
        Assert.Equal("canonical_read_window_result_limit_exceeded", error.ErrorCode);
        Assert.Equal(2, factory.Requests.Count);
        Assert.Equal(2, gateway.Searches.Count);
    }

    private sealed class CapacityRefusingGateway(AdvancedAnalysisResolvedEvidence item,
        int failureAt = 3, string failureCode = "accumulated_evidence_limit_exceeded") : IAdvancedAnalysisToolGateway
    {
        public List<AdvancedAnalysisSearchRequest> Searches { get; } = [];
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => Searches.Count == 0 ? [] : [item];
        public AdvancedAnalysisToolBudget Budget => new(32, Searches.Count, 300_000, 0, 2, Searches.Count == 0 ? 0 : 1);
        public Task<AdvancedAnalysisSearchObservation> SearchAsync(AdvancedAnalysisSearchRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Searches.Add(request);
            if (Searches.Count == failureAt) throw new AdvancedAnalysisToolException(failureCode);
            return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, [item], [], 1, Searches.Count));
        }
    }
}
