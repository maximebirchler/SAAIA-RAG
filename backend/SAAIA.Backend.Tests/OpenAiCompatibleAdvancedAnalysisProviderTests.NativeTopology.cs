using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions", false)]
    [InlineData("responses", false)]
    [InlineData("responses", true)]
    public async Task Native_agent_topology_reuses_review_calls_in_synthesis_and_preserves_final_critic(string protocol, bool critic)
    {
        const string arguments = """{"sourceKey":"internal-source-1","pageStart":1,"pageEnd":4,"topK":20}""";
        var responses = new List<HttpResponseMessage> { Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            protocol == "responses" ? NativeResponseFunction("read_source", arguments) : NativeCompletion(("read_source", arguments)),
            protocol == "responses" ? NativeResponseAnswer(NativeAnswered) : Completion(NativeAnswered) };
        if (critic) responses.Add(NativeResponseAnswer(NativeAnswered));
        using var factory = new QueuedHttpClientFactory(responses.ToArray());
        var options = CreateOptions(); options.AdaptiveResearchEnabled = true; options.NativeResearchToolsEnabled = true;
        options.NativeResearchApiProtocol = protocol; options.NativeResearchTopology = "agent";
        options.SemanticCriticEnabled = critic; options.ExternalMaximumCallsPerJob = 4;
        var gateway = new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar."));
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null).ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
        Assert.Equal("answered", result.Outcome); Assert.Equal(critic ? 4 : 3, result.ProviderCallCount);
        Assert.Equal(2, gateway.Searches.Count); Assert.Equal("read_source", gateway.Searches[1].Operation);
        using var second = JsonDocument.Parse(factory.Requests[1].Body);
        Assert.True(second.RootElement.TryGetProperty("tools", out _));
        Assert.Contains("sourceKey", factory.Requests[1].Body);
    }

    [Fact]
    public async Task Non_native_execution_keeps_research_review_even_if_native_topology_is_agent()
    {
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            Completion("""{"decision":"ready","queries":[]}"""), Completion(NativeAnswered));
        var options = CreateOptions(); options.AdaptiveResearchEnabled = true; options.NativeResearchTopology = "agent";
        options.ExternalMaximumCallsPerJob = 4;
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null).ExecuteAsync(BuildDirectRequest(),
            new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar.")), CancellationToken.None);
        Assert.Equal("answered", result.Outcome); Assert.Equal(3, result.ProviderCallCount);
        using var second = JsonDocument.Parse(factory.Requests[1].Body);
        Assert.False(second.RootElement.TryGetProperty("tools", out _));
        Assert.Contains("priorSearches", factory.Requests[1].Body);
    }

    [Fact]
    public async Task Unknown_native_topology_is_rejected_before_any_model_or_tool_call()
    {
        using var factory = new QueuedHttpClientFactory();
        var options = CreateOptions(); options.NativeResearchToolsEnabled = true; options.NativeResearchTopology = "unknown";
        var gateway = new RecordingToolGateway();
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null).ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None));
        Assert.Equal("advanced_native_research_topology_invalid", error.ErrorCode);
        Assert.Empty(factory.Requests); Assert.Empty(gateway.Searches);
    }
}
