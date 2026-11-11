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
        Assert.Equal("low", planner.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
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

    [Theory]
    [InlineData("responses", "")]
    [InlineData("responses", "medium")]
    [InlineData("chat-completions", "")]
    [InlineData("chat-completions", "medium")]
    public async Task Final_critic_review_preserves_synthesis_reasoning_without_granting_tools(string protocol, string synthesis)
    {
        var bodies = EarlyBodies();
        var gateway = new PostResearchToolGateway((q, n) => new(q.Query, [.. bodies, EarlyLocator(bodies[0])], [], 1, n));
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan),
            EarlyReply(protocol, RepeatableTwentyAnswer()), EarlyReply(protocol, CriticPartialChoice),
            EarlyReply(protocol, RepeatableTwentyAnswer()), EarlyReply(protocol, CriticPartialChoice));
        var options = EarlyOptions(protocol, 8); options.SemanticCriticEnabled = true;
        options.Provider = "openai-dev"; options.LlmLocation = "external-service"; options.LlmBaseUrl = "https://api.example/v1";
        options.ReasoningEffort = "low"; options.SynthesisReasoningEffort = synthesis; options.ExternalMaximumCostPerJobUsd = 2m;
        var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, "synthetic-test-key", logger)
            .ExecuteAsync(SynthesisPurposeRequest(false), gateway, CancellationToken.None);
        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Equal(5, result.ProviderCallCount);
        for (var i = 0; i < factory.Requests.Count; i++)
        {
            using var wire = JsonDocument.Parse(factory.Requests[i].Body);
            var effort = protocol == "responses" ? wire.RootElement.GetProperty("reasoning").GetProperty("effort")
                : wire.RootElement.GetProperty("reasoning_effort");
            Assert.Equal(i == 0 || synthesis.Length == 0 ? "low" : synthesis, effort.GetString());
            if (i == factory.Requests.Count - 1) Assert.False(wire.RootElement.TryGetProperty("tools", out _));
        }
        Assert.DoesNotContain("researchTools", WorkspaceUser(factory.Requests[^1].Body).EnumerateObject().Select(p => p.Name));
        Assert.Contains(logger.Events, e =>
        {
            if (e.Values.GetValueOrDefault("Measurements") is not string measurements) return false;
            using var fields = JsonDocument.Parse(measurements);
            return fields.RootElement.TryGetProperty("role", out var role) && role.GetString() == "critic-final-review"
                && fields.RootElement.GetProperty("reasoning_effort").GetString() == (synthesis.Length == 0 ? "low" : synthesis);
        });
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
