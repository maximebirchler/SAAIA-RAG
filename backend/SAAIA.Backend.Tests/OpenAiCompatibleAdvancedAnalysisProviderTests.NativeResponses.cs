using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    private static HttpResponseMessage NativeResponseFunction(string operation, string arguments)
        => new(HttpStatusCode.OK) { Content = JsonContent.Create(new
        {
            status = "completed", output = new object[]
            {
                new { id = "rs_1", type = "reasoning", summary = Array.Empty<object>(), encrypted_content = "synthetic-sealed-reasoning" },
                new { id = "fc_1", type = "function_call", call_id = "call_1", name = operation, arguments, status = "completed" }
            }, usage = new { input_tokens = 100, output_tokens = 50, input_tokens_details = new { cached_tokens = 10 } }
        }) };

    private static HttpResponseMessage NativeResponseAnswer(string answer, string status = "completed")
        => new(HttpStatusCode.OK) { Content = JsonContent.Create(new
        {
            status, output = new[] { new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = answer } } } },
            usage = new { input_tokens = 100, output_tokens = 50, input_tokens_details = new { cached_tokens = 10 } }
        }) };

    [Theory]
    [InlineData("search_corpus", "{\"query\":\"17 bar\",\"sourceKey\":\"internal-source-1\",\"category\":\"\",\"documentHint\":\"\",\"topK\":20}")]
    [InlineData("read_source", "{\"sourceKey\":\"internal-source-1\",\"pageStart\":1,\"pageEnd\":4,\"topK\":20}")]
    [InlineData("find_source_text", "{\"sourceKey\":\"internal-source-1\",\"query\":\"17 bar\",\"offset\":0,\"topK\":20}")]
    public async Task Native_responses_preserve_reasoning_and_linked_results_with_stateless_transport(string operation, string arguments)
    {
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Completion("""{"decision":"ready","queries":[]}"""), NativeResponseFunction(operation, arguments), NativeResponseAnswer(NativeAnswered));
        var options = CreateOptions(); options.AdaptiveResearchEnabled = true; options.NativeResearchToolsEnabled = true;
        options.NativeResearchApiProtocol = "responses"; options.ExternalMaximumCallsPerJob = 5;
        options.Provider = "openai-dev"; options.LlmLocation = "external-service"; options.LlmBaseUrl = "https://api.example/v1";
        options.ReasoningEffort = "low"; options.ExternalMaximumCostPerJobUsd = 2m;
        var gateway = new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar."));
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, "synthetic-test-key").ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        Assert.Equal("answered", result.Outcome); Assert.Equal(4, result.ProviderCallCount);
        Assert.Equal(400, result.InputTokens); Assert.Equal(200, result.OutputTokens); Assert.Equal(40, result.CachedInputTokens);
        Assert.Equal(operation, gateway.Searches[1].Operation);
        Assert.Equal("/v1/chat/completions", factory.Requests[1].Uri.AbsolutePath);
        Assert.Equal("/v1/responses", factory.Requests[2].Uri.AbsolutePath);
        using var first = JsonDocument.Parse(factory.Requests[2].Body);
        Assert.False(first.RootElement.GetProperty("store").GetBoolean());
        Assert.Equal("low", first.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("json_object", first.RootElement.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
        Assert.False(first.RootElement.TryGetProperty("messages", out _));
        Assert.False(first.RootElement.TryGetProperty("max_completion_tokens", out _));
        Assert.Equal("search_corpus", first.RootElement.GetProperty("tools")[0].GetProperty("name").GetString());
        using var next = JsonDocument.Parse(factory.Requests[3].Body);
        var input = next.RootElement.GetProperty("input");
        Assert.Equal("reasoning", input[1].GetProperty("type").GetString());
        Assert.Equal("synthetic-sealed-reasoning", input[1].GetProperty("encrypted_content").GetString());
        Assert.Equal("call_1", input[2].GetProperty("call_id").GetString());
        Assert.Equal("function_call_output", input[3].GetProperty("type").GetString());
        Assert.Equal("call_1", input[3].GetProperty("call_id").GetString());
        using var tool = JsonDocument.Parse(input[3].GetProperty("output").GetString()!);
        Assert.Equal("E1", tool.RootElement.GetProperty("visibleEvidenceIds")[0].GetString());
        Assert.Equal("user", input[4].GetProperty("role").GetString());
    }

    [Theory]
    [InlineData("web_search", "{}", "advanced_native_tool_protocol_invalid")]
    [InlineData("read_source", "{\"sourceKey\":\"unknown-source\",\"pageStart\":1,\"pageEnd\":4,\"topK\":20}", "advanced_synthesis_research_protocol_invalid")]
    public async Task Native_responses_reject_unknown_functions_or_sources_before_tool_execution(string operation, string arguments, string expected)
    {
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Completion("""{"decision":"ready","queries":[]}"""), NativeResponseFunction(operation, arguments));
        var options = CreateOptions(); options.AdaptiveResearchEnabled = true; options.NativeResearchToolsEnabled = true;
        options.NativeResearchApiProtocol = "responses"; options.ExternalMaximumCallsPerJob = 5;
        var gateway = new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar."));
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null).ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None));
        Assert.Equal(expected, error.ErrorCode); Assert.Single(gateway.Searches);
    }

    [Fact]
    public async Task Native_responses_incomplete_output_is_rejected_even_if_partial_answer_json_is_parseable()
    {
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Completion("""{"decision":"ready","queries":[]}"""), NativeResponseAnswer(NativeAnswered, "incomplete"));
        var options = CreateOptions(); options.AdaptiveResearchEnabled = true; options.NativeResearchToolsEnabled = true;
        options.NativeResearchApiProtocol = "responses"; options.ExternalMaximumCallsPerJob = 5;
        var gateway = new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar."));
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null).ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None));
        Assert.Equal("advanced_llm_output_limit", error.ErrorCode); Assert.Single(gateway.Searches);
    }

    [Fact]
    public async Task Native_responses_correction_returns_rejected_call_and_reasoning_without_executing_bad_window()
    {
        const string valid = """{"sourceKey":"internal-source-1","pageStart":1,"pageEnd":4,"topK":20}""";
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Completion("""{"decision":"ready","queries":[]}"""), NativeResponseFunction("read_source", """{"sourceKey":"internal-source-1","pageStart":1,"pageEnd":5,"topK":20}"""),
            NativeResponseFunction("read_source", valid), NativeResponseAnswer(NativeAnswered));
        var options = CreateOptions(); options.AdaptiveResearchEnabled = true; options.NativeResearchToolsEnabled = true;
        options.NativeResearchApiProtocol = "responses"; options.ExternalMaximumCallsPerJob = 6;
        var gateway = new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar."));
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null).ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        Assert.Equal("answered", result.Outcome); Assert.Equal(2, gateway.Searches.Count);
        Assert.Equal(4, gateway.Searches[1].PageEnd);
        using var request = JsonDocument.Parse(factory.Requests[3].Body);
        var input = request.RootElement.GetProperty("input");
        Assert.Equal("reasoning", input[1].GetProperty("type").GetString());
        using var feedback = JsonDocument.Parse(input[3].GetProperty("output").GetString()!);
        Assert.Equal("batch_rejected_before_execution", feedback.RootElement.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("in_progress")]
    [InlineData("queued")]
    public async Task Native_responses_nonterminal_or_failed_envelopes_cannot_publish_parseable_answer(string status)
    {
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Completion("""{"decision":"ready","queries":[]}"""), NativeResponseAnswer(NativeAnswered, status));
        var options = CreateOptions(); options.AdaptiveResearchEnabled = true; options.NativeResearchToolsEnabled = true;
        options.NativeResearchApiProtocol = "responses"; options.ExternalMaximumCallsPerJob = 5;
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null).ExecuteAsync(BuildDirectRequest(),
                new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar.")), CancellationToken.None));
        Assert.Equal("advanced_llm_response_invalid", error.ErrorCode);
    }

    [Fact]
    public async Task Native_http_rejection_trace_preserves_provider_diagnostic_and_exact_request()
    {
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Completion("""{"decision":"ready","queries":[]}"""), new(HttpStatusCode.BadRequest)
            { Content = JsonContent.Create(new { error = new { message = "Unsupported field", param = "field" } }) });
        var options = CreateOptions(); options.AdaptiveResearchEnabled = true; options.NativeResearchToolsEnabled = true;
        options.NativeResearchApiProtocol = "responses"; options.ExternalMaximumCallsPerJob = 5;
        options.DevelopmentTraceDirectory = Path.Combine(Path.GetTempPath(), "saaia-native-http-error-" + Guid.NewGuid().ToString("N"));
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null).ExecuteAsync(BuildDirectRequest(),
                new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar.")), CancellationToken.None));
        Assert.Equal("advanced_llm_http_400", error.ErrorCode);
        var path = Assert.Single(Directory.GetFiles(options.DevelopmentTraceDirectory, "*-writer-*.json"));
        using var trace = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("http.error", trace.RootElement.GetProperty("completionOrigin").GetString());
        Assert.Equal(factory.Requests[2].Body, trace.RootElement.GetProperty("requestJson").GetString());
        using var envelope = JsonDocument.Parse(trace.RootElement.GetProperty("responseEnvelopeJson").GetString()!);
        Assert.Equal("field", envelope.RootElement.GetProperty("error").GetProperty("param").GetString());
    }
}
