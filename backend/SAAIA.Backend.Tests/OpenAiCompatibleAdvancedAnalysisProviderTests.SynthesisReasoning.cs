using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("")]
    [InlineData("medium")]
    public async Task Synthesis_reasoning_override_preserves_planner_effort_and_applies_to_research_returns_and_critic(string synthesis)
    {
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""),
            NativeResponseFunction("read_source", """{"sourceKey":"internal-source-1","pageStart":1,"pageEnd":4,"topK":20}"""),
            NativeResponseAnswer(NativeAnswered), NativeResponseAnswer(NativeAnswered));
        var options = WorkspaceOptions("responses"); options.SemanticCriticEnabled = true;
        options.Provider = "openai-dev"; options.LlmLocation = "external-service"; options.LlmBaseUrl = "https://api.example/v1";
        options.ReasoningEffort = "low"; options.SynthesisReasoningEffort = synthesis; options.ExternalMaximumCostPerJobUsd = 2m;
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, "synthetic-test-key")
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar.")), CancellationToken.None);
        Assert.Equal("answered", result.Outcome); Assert.Equal(4, result.ProviderCallCount);
        using var planner = JsonDocument.Parse(factory.Requests[0].Body);
        Assert.Equal("low", planner.RootElement.GetProperty("reasoning_effort").GetString());
        for (var i = 1; i < factory.Requests.Count; i++)
        {
            using var request = JsonDocument.Parse(factory.Requests[i].Body);
            Assert.Equal(synthesis.Length == 0 ? "low" : synthesis, request.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        }
    }

    [Fact]
    public async Task Internal_transport_does_not_receive_openai_synthesis_reasoning_parameters()
    {
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""), Completion(NativeAnswered));
        var options = WorkspaceOptions(); options.SynthesisReasoningEffort = "medium";
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar.")), CancellationToken.None);
        Assert.Equal("answered", result.Outcome);
        foreach (var actual in factory.Requests)
        {
            using var request = JsonDocument.Parse(actual.Body);
            Assert.False(request.RootElement.TryGetProperty("reasoning_effort", out _));
            Assert.False(request.RootElement.TryGetProperty("reasoning", out _));
        }
    }

    [Fact]
    public async Task Invalid_synthesis_reasoning_is_rejected_before_any_model_or_documentary_operation()
    {
        using var factory = new QueuedHttpClientFactory(); var options = WorkspaceOptions();
        options.SynthesisReasoningEffort = "unrecognized";
        var gateway = new RecordingToolGateway();
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null).ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None));
        Assert.Equal("advanced_synthesis_reasoning_effort_invalid", error.ErrorCode);
        Assert.Empty(factory.Requests); Assert.Empty(gateway.Searches);
    }
}
