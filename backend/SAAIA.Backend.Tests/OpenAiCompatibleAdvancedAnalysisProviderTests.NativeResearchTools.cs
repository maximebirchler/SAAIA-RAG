using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    private const string NativeAnswered = """{"outcome":"answered","answerText":"Le seuil est 17 bar [C1].","claims":[{"claimId":"C1","text":"Le seuil est 17 bar.","evidenceIds":["E1"]}]}""";

    private static HttpResponseMessage NativeCompletion(params (string Name, string Arguments)[] calls)
        => new(HttpStatusCode.OK) { Content = JsonContent.Create(new
        {
            choices = new[] { new { finish_reason = "tool_calls", message = new
            {
                role = "assistant", content = (string?)null,
                tool_calls = calls.Select((c, i) => new { id = "call_" + i, type = "function",
                    function = new { name = c.Name, arguments = c.Arguments } }).ToArray()
            } } }, usage = new { prompt_tokens = 100, completion_tokens = 50 }
        }) };

    [Theory]
    [InlineData("search_corpus", "{\"query\":\"17 bar\",\"sourceKey\":\"internal-source-1\",\"category\":\"\",\"documentHint\":\"\",\"topK\":20}")]
    [InlineData("read_source", "{\"sourceKey\":\"internal-source-1\",\"pageStart\":1,\"pageEnd\":4,\"topK\":20}")]
    [InlineData("find_source_text", "{\"sourceKey\":\"internal-source-1\",\"query\":\"17 bar\",\"offset\":0,\"topK\":20}")]
    public async Task Native_research_functions_execute_canonical_requests_and_return_linked_bounded_results(string operation, string arguments)
    {
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Completion("""{"decision":"ready","queries":[]}"""), NativeCompletion((operation, arguments)), Completion(NativeAnswered));
        var options = CreateOptions(); options.AdaptiveResearchEnabled = true; options.NativeResearchToolsEnabled = true;
        options.ExternalMaximumCallsPerJob = 5;
        var gateway = new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar."));
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null).ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        Assert.Equal("answered", result.Outcome); Assert.Equal(4, result.ProviderCallCount);
        Assert.Equal(operation, gateway.Searches[1].Operation);
        using var first = JsonDocument.Parse(factory.Requests[2].Body);
        Assert.Equal("auto", first.RootElement.GetProperty("tool_choice").GetString());
        Assert.Equal(3, first.RootElement.GetProperty("tools").GetArrayLength());
        foreach (var tool in first.RootElement.GetProperty("tools").EnumerateArray())
        {
            var function = tool.GetProperty("function"); Assert.True(function.GetProperty("strict").GetBoolean());
            Assert.False(function.GetProperty("parameters").GetProperty("additionalProperties").GetBoolean());
        }
        using var next = JsonDocument.Parse(factory.Requests[3].Body);
        var messages = next.RootElement.GetProperty("messages");
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("call_0", messages[1].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("tool", messages[2].GetProperty("role").GetString());
        Assert.Equal("call_0", messages[2].GetProperty("tool_call_id").GetString());
        var toolText = messages[2].GetProperty("content").GetString()!;
        using var response = JsonDocument.Parse(toolText);
        Assert.Equal("executed", response.RootElement.GetProperty("status").GetString());
        Assert.Equal("E1", response.RootElement.GetProperty("visibleEvidenceIds")[0].GetString());
        Assert.DoesNotContain("Le seuil est 17 bar.", toolText);
        Assert.Equal("user", messages[3].GetProperty("role").GetString());
    }

    [Theory]
    [InlineData("web_search", "{}", "advanced_native_tool_protocol_invalid")]
    [InlineData("search_corpus", "{\"query\":\"17 bar\",\"sourceKey\":\"internal-source-1\",\"category\":\"\",\"documentHint\":\"\",\"topK\":20,\"operation\":\"read_source\"}", "advanced_native_tool_protocol_invalid")]
    [InlineData("read_source", "{\"sourceKey\":\"unknown-source\",\"pageStart\":1,\"pageEnd\":4,\"topK\":20}", "advanced_synthesis_research_protocol_invalid")]
    [InlineData("read_source", "[]", "advanced_native_tool_protocol_invalid")]
    [InlineData("read_source", "{\"sourceKey\":\"internal-source-1\"}", "advanced_native_tool_protocol_invalid")]
    public async Task Native_unknown_operations_scopes_and_malformed_arguments_never_execute(string operation, string arguments, string errorCode)
    {
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Completion("""{"decision":"ready","queries":[]}"""), NativeCompletion((operation, arguments)));
        var options = CreateOptions(); options.AdaptiveResearchEnabled = true; options.NativeResearchToolsEnabled = true;
        options.ExternalMaximumCallsPerJob = 5;
        var gateway = new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar."));
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null).ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None));
        Assert.Equal(errorCode, error.ErrorCode); Assert.Single(gateway.Searches); Assert.Equal(3, factory.Requests.Count);
    }

    [Fact]
    public async Task Native_invalid_page_batch_gets_linked_rejection_and_model_correction_without_executing_valid_prefix()
    {
        const string valid = """{"sourceKey":"internal-source-1","pageStart":1,"pageEnd":4,"topK":20}""";
        const string invalid = """{"sourceKey":"internal-source-1","pageStart":5,"pageEnd":9,"topK":20}""";
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Completion("""{"decision":"ready","queries":[]}"""), NativeCompletion(("read_source", valid), ("read_source", invalid)),
            NativeCompletion(("read_source", valid)), Completion(NativeAnswered));
        var options = CreateOptions(); options.AdaptiveResearchEnabled = true; options.NativeResearchToolsEnabled = true;
        options.ExternalMaximumCallsPerJob = 6;
        var gateway = new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar."));
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null).ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        Assert.Equal("answered", result.Outcome); Assert.Equal(5, result.ProviderCallCount);
        Assert.Equal(2, gateway.Searches.Count); Assert.Equal(4, gateway.Searches[1].PageEnd);
        using var next = JsonDocument.Parse(factory.Requests[3].Body);
        var messages = next.RootElement.GetProperty("messages");
        foreach (var index in new[] { 2, 3 })
        {
            using var feedback = JsonDocument.Parse(messages[index].GetProperty("content").GetString()!);
            Assert.Equal("batch_rejected_before_execution", feedback.RootElement.GetProperty("status").GetString());
            Assert.Equal(0, feedback.RootElement.GetProperty("visibleEvidenceIds").GetArrayLength());
            Assert.Equal(4, feedback.RootElement.GetProperty("argumentFeedback").GetProperty("maximumInclusivePages").GetInt32());
        }
    }

    [Fact]
    public async Task Native_function_calls_are_rejected_when_capability_is_disabled()
    {
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Completion("""{"decision":"ready","queries":[]}"""), NativeCompletion(("read_source", "{}")));
        var options = CreateOptions(); options.AdaptiveResearchEnabled = true; options.ExternalMaximumCallsPerJob = 5;
        var gateway = new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar."));
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null).ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None));
        Assert.Equal("advanced_native_tool_not_available", error.ErrorCode); Assert.Single(gateway.Searches);
        using var request = JsonDocument.Parse(factory.Requests[2].Body);
        Assert.False(request.RootElement.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task Native_functions_are_unavailable_when_final_call_has_no_research_budget()
    {
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Completion("""{"decision":"ready","queries":[]}"""), NativeCompletion(("read_source", "{}")));
        var options = CreateOptions(); options.AdaptiveResearchEnabled = true; options.NativeResearchToolsEnabled = true;
        options.ExternalMaximumCallsPerJob = 3;
        var gateway = new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar."));
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null).ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None));
        Assert.Equal("advanced_native_tool_not_available", error.ErrorCode); Assert.Single(gateway.Searches);
        using var request = JsonDocument.Parse(factory.Requests[2].Body);
        Assert.False(request.RootElement.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task Native_batch_larger_than_existing_query_limit_never_executes()
    {
        var calls = Enumerable.Repeat(("read_source", "{\"sourceKey\":\"internal-source-1\",\"pageStart\":1,\"pageEnd\":4,\"topK\":20}"), 9).ToArray();
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Completion("""{"decision":"ready","queries":[]}"""), NativeCompletion(calls));
        var options = CreateOptions(); options.AdaptiveResearchEnabled = true; options.NativeResearchToolsEnabled = true;
        options.MaximumPlanQueries = 8; options.ExternalMaximumCallsPerJob = 5;
        var gateway = new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar."));
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null).ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None));
        Assert.Equal("advanced_native_tool_protocol_invalid", error.ErrorCode); Assert.Single(gateway.Searches);
    }

    [Fact]
    public async Task Native_trace_distinguishes_raw_function_calls_from_normalized_research_json()
    {
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Completion("""{"decision":"ready","queries":[]}"""), NativeCompletion(("read_source", """{"sourceKey":"internal-source-1","pageStart":1,"pageEnd":4,"topK":20}""")), Completion(NativeAnswered));
        var options = CreateOptions(); options.AdaptiveResearchEnabled = true; options.NativeResearchToolsEnabled = true;
        options.ExternalMaximumCallsPerJob = 5;
        options.DevelopmentTraceDirectory = Path.Combine(Path.GetTempPath(), "saaia-native-trace-" + Guid.NewGuid().ToString("N"));
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null).ExecuteAsync(BuildDirectRequest(),
            new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar.")), CancellationToken.None);
        Assert.Equal("answered", result.Outcome);
        var file = Assert.Single(Directory.GetFiles(options.DevelopmentTraceDirectory, "*-writer-*.json"), path =>
        {
            using var candidate = JsonDocument.Parse(File.ReadAllText(path));
            return candidate.RootElement.GetProperty("role").GetString() == "writer";
        });
        using var trace = JsonDocument.Parse(File.ReadAllText(file));
        Assert.Equal("advanced-development-trace.v2", trace.RootElement.GetProperty("schema").GetString());
        Assert.Equal("tool_calls.normalized", trace.RootElement.GetProperty("completionOrigin").GetString());
        using var envelope = JsonDocument.Parse(trace.RootElement.GetProperty("responseEnvelopeJson").GetString()!);
        Assert.Equal(JsonValueKind.Null, envelope.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").ValueKind);
        using var normalized = JsonDocument.Parse(trace.RootElement.GetProperty("completionJson").GetString()!);
        Assert.Equal("research_required", normalized.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("read_source", normalized.RootElement.GetProperty("queries")[0].GetProperty("operation").GetString());
    }

    [Fact]
    public async Task Native_reservation_includes_function_definitions_and_tool_history_when_usage_is_missing()
    {
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Completion("""{"decision":"ready","queries":[]}"""), NativeCompletion(("read_source", """{"sourceKey":"internal-source-1","pageStart":1,"pageEnd":4,"topK":20}""")),
            CompletionStream(new IOException("Synthetic transport failure")));
        var options = CreateOptions(); options.AdaptiveResearchEnabled = true; options.NativeResearchToolsEnabled = true;
        options.ExternalMaximumCallsPerJob = 5; options.LlmMaximumHttpAttempts = 1;
        options.Provider = "runpod-bench"; options.LlmLocation = "external-service"; options.LlmBaseUrl = "https://runpod.example/v1";
        options.ExternalMaximumCostPerJobUsd = 2m;
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, "synthetic-test-key").ExecuteAsync(BuildDirectRequest(),
                new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar.")), CancellationToken.None));
        Assert.Equal("advanced_llm_transport_error", error.ErrorCode);
        var last = File.ReadAllLines(options.ExternalUsageLedgerPath).Last();
        using var charge = JsonDocument.Parse(last);
        Assert.Equal("reserved_upper_bound", charge.RootElement.GetProperty("usageSource").GetString());
        Assert.Equal((int)Math.Ceiling(factory.Requests[3].Body.Length / 4d), charge.RootElement.GetProperty("inputTokens").GetInt32());
        using var request = JsonDocument.Parse(factory.Requests[3].Body);
        Assert.True(request.RootElement.TryGetProperty("tools", out _));
        Assert.Equal(4, request.RootElement.GetProperty("messages").GetArrayLength());
    }
}
