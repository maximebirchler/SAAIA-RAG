using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("responses")]
    [InlineData("chat-completions")]
    public async Task Native_read_source_count_is_bounded_by_the_schema_and_binds_the_exact_read(string protocol)
    {
        const string args = """{"sourceKey":"internal-source-1","pageStart":18,"pageCount":4,"topK":60}""";
        var responses = protocol == "responses";
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Completion("""{"decision":"ready","queries":[]}"""),
            responses ? NativeResponseFunction("read_source", args) : NativeCompletion(("read_source", args)),
            responses ? NativeResponseAnswer(NativeAnswered) : Completion(NativeAnswered));
        var options = CreateOptions();
        options.AdaptiveResearchEnabled = options.NativeResearchToolsEnabled = true;
        options.NativeResearchApiProtocol = protocol;
        options.ExternalMaximumCallsPerJob = 5;
        var gateway = new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar."));

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal(4, result.ProviderCallCount);
        var read = Assert.Single(gateway.Searches, search => search.Operation == "read_source");
        Assert.Equal(18, read.PageStart);
        Assert.Equal(21, read.PageEnd);
        Assert.Equal(60, read.TopK);
        using var request = JsonDocument.Parse(factory.Requests[2].Body);
        var functions = request.RootElement.GetProperty("tools").EnumerateArray()
            .Select(tool => responses ? tool : tool.GetProperty("function"));
        var function = Assert.Single(functions, tool => tool.GetProperty("name").GetString() == "read_source");
        var schema = function.GetProperty("parameters");
        Assert.True(function.GetProperty("strict").GetBoolean());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains(schema.GetProperty("required").EnumerateArray(), field => field.GetString() == "pageCount");
        var properties = schema.GetProperty("properties");
        Assert.False(properties.TryGetProperty("pageEnd", out _));
        var count = properties.GetProperty("pageCount");
        Assert.Equal("integer", count.GetProperty("type").GetString());
        Assert.Equal(1, count.GetProperty("minimum").GetInt32());
        Assert.Equal(4, count.GetProperty("maximum").GetInt32());
        using var followup = JsonDocument.Parse(factory.Requests[3].Body);
        var output = Assert.Single(followup.RootElement.GetProperty(responses ? "input" : "messages").EnumerateArray(),
            message => responses
                ? message.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"
                : message.GetProperty("role").GetString() == "tool");
        using var body = JsonDocument.Parse(output.GetProperty(responses ? "output" : "content").GetString()!);
        Assert.Equal("executed", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("E1", body.RootElement.GetProperty("visibleEvidenceIds")[0].GetString());
    }

    [Theory]
    [InlineData(18, 22)]
    [InlineData(30, 40)]
    [InlineData(42, 48)]
    [InlineData(14, 21)]
    public async Task Native_read_source_legacy_ranges_from_the_rejected_batch_still_fail_closed(int start, int end)
    {
        var args = JsonSerializer.Serialize(new { sourceKey = "internal-source-1", pageStart = start, pageEnd = end, topK = 60 });
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Completion("""{"decision":"ready","queries":[]}"""),
            NativeResponseFunction("read_source", args), NativeResponseAnswer(NativeAnswered));
        var options = CreateOptions();
        options.AdaptiveResearchEnabled = options.NativeResearchToolsEnabled = true;
        options.NativeResearchApiProtocol = "responses";
        options.ExternalMaximumCallsPerJob = 6;
        var gateway = new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar."));

        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);

        Assert.Single(gateway.Searches);
        using var followup = JsonDocument.Parse(factory.Requests[3].Body);
        var output = Assert.Single(followup.RootElement.GetProperty("input").EnumerateArray(),
            message => message.TryGetProperty("type", out var type) && type.GetString() == "function_call_output");
        using var body = JsonDocument.Parse(output.GetProperty("output").GetString()!);
        Assert.Equal("batch_rejected_before_execution", body.RootElement.GetProperty("status").GetString());
        var feedback = body.RootElement.GetProperty("argumentFeedback");
        Assert.Equal(start, feedback.GetProperty("requestedPageStart").GetInt32());
        Assert.Equal(end, feedback.GetProperty("requestedPageEnd").GetInt32());
        Assert.Equal(4, feedback.GetProperty("maximumInclusivePages").GetInt32());
    }

    [Theory]
    [InlineData("""{"sourceKey":"internal-source-1","pageStart":18,"pageCount":5,"topK":60}""")]
    [InlineData("""{"sourceKey":"internal-source-1","pageStart":18,"pageCount":0,"topK":60}""")]
    [InlineData("""{"sourceKey":"internal-source-1","pageStart":2147483647,"pageCount":2,"topK":60}""")]
    [InlineData("""{"sourceKey":"internal-source-1","pageStart":18,"pageCount":1,"pageEnd":18,"topK":60}""")]
    public async Task Native_read_source_count_cannot_bypass_parser_bounds(string args)
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Completion("""{"decision":"ready","queries":[]}"""),
            NativeResponseFunction("read_source", args));
        var options = CreateOptions();
        options.AdaptiveResearchEnabled = options.NativeResearchToolsEnabled = true;
        options.NativeResearchApiProtocol = "responses";
        options.ExternalMaximumCallsPerJob = 4;
        var gateway = new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar."));

        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
                .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None));

        Assert.Equal("advanced_synthesis_research_protocol_invalid", error.ErrorCode);
        Assert.Single(gateway.Searches);
    }

    [Fact]
    public async Task Native_read_source_count_error_can_recover_with_a_bounded_read()
    {
        const string invalid = """{"sourceKey":"internal-source-1","pageStart":18,"pageCount":5,"topK":60}""";
        const string corrected = """{"sourceKey":"internal-source-1","pageStart":18,"pageCount":1,"topK":60}""";
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Completion("""{"decision":"ready","queries":[]}"""),
            NativeResponseFunction("read_source", invalid),
            NativeResponseFunction("read_source", corrected),
            NativeResponseAnswer(NativeAnswered));
        var options = CreateOptions();
        options.AdaptiveResearchEnabled = options.NativeResearchToolsEnabled = true;
        options.NativeResearchApiProtocol = "responses";
        options.ExternalMaximumCallsPerJob = 6;
        var gateway = new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar."));

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        var read = Assert.Single(gateway.Searches, search => search.Operation == "read_source");
        Assert.Equal(18, read.PageStart);
        Assert.Equal(18, read.PageEnd);
        using var request = JsonDocument.Parse(factory.Requests[3].Body);
        var output = Assert.Single(request.RootElement.GetProperty("input").EnumerateArray(),
            message => message.TryGetProperty("type", out var type) && type.GetString() == "function_call_output");
        using var body = JsonDocument.Parse(output.GetProperty("output").GetString()!);
        Assert.Equal("advanced_native_read_source_page_count_invalid",
            body.RootElement.GetProperty("argumentFeedback").GetProperty("reasonCode").GetString());
        Assert.Contains("pageCount from 1 to 4", body.RootElement.GetProperty("instruction").GetString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Native_raw_observation_still_requires_exactly_one_physical_page(int pageCount)
    {
        var gateway = new NativeAuditGateway(exposeInitialInventory: true);
        var args = JsonSerializer.Serialize(new
        {
            sourceKey = "internal-source-1", pageStart = 1, pageCount, topK = 20,
            nativeObservationView = "raw", nativeObservationBlockId = "native-audit",
            nativeObservationRawSha256 = gateway.RawSha, nativeObservationOffset = 0, nativeObservationLimit = 2
        });
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            NativeResponseFunction("read_source", args), NativeResponseAnswer(NativeAnswered));
        var options = WorkspaceOptions("responses");
        options.NativeObservationReadEnabled = true;
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);

        var reads = gateway.Searches.Where(search => search.Operation == "read_source").ToArray();
        if (pageCount == 1)
        {
            var read = Assert.Single(reads);
            Assert.Equal(1, read.PageStart);
            Assert.Equal(1, read.PageEnd);
            Assert.Equal("raw", read.NativeObservationView);
        }
        else
        {
            Assert.Empty(reads);
            using var request = JsonDocument.Parse(factory.Requests[2].Body);
            var output = Assert.Single(request.RootElement.GetProperty("input").EnumerateArray(),
                message => message.TryGetProperty("type", out var type) && type.GetString() == "function_call_output");
            using var body = JsonDocument.Parse(output.GetProperty("output").GetString()!);
            Assert.Equal("advanced_native_raw_locator_not_observed",
                body.RootElement.GetProperty("argumentFeedback").GetProperty("reasonCode").GetString());
        }
    }
}
