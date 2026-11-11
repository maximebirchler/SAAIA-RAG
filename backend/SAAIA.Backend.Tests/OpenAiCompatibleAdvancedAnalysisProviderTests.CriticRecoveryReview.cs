using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("responses", "partial")]
    [InlineData("responses", "invalid")]
    [InlineData("responses", "output_limit")]
    [InlineData("responses", "call_budget")]
    [InlineData("chat-completions", "partial")]
    [InlineData("chat-completions", "invalid")]
    [InlineData("chat-completions", "output_limit")]
    [InlineData("chat-completions", "call_budget")]
    public async Task CriticRecoveryReview_does_not_publish_a_recovered_complete_proposal_without_final_acceptance(
        string protocol, string verdict)
    {
        var bodies = EarlyBodies();
        var gateway = new PostResearchToolGateway((query, number) =>
            new(query.Query, [.. bodies, EarlyLocator(bodies[0])], [], 1, number));
        var replies = new List<HttpResponseMessage>
        {
            Completion(RepeatablePlan), EarlyReply(protocol, RepeatableTwentyAnswer()),
            EarlyReply(protocol, CriticPartialChoice),
            PostResearchFunction(protocol, "find_source_text",
                """{"sourceKey":"internal-source-1","query":"OptionB","offset":0,"topK":3}"""),
            EarlyReply(protocol, RepeatableTwentyAnswer())
        };
        if (verdict != "call_budget") replies.Add(verdict == "output_limit" ? CriticStoppedReply(protocol)
            : EarlyReply(protocol, verdict == "invalid" ? "{" : CriticPartialChoice));
        using var factory = new QueuedHttpClientFactory(replies.ToArray());
        var options = EarlyOptions(protocol, verdict == "call_budget" ? 5 : 8);
        options.SemanticCriticEnabled = true;
        var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, logger)
            .ExecuteAsync(BuildRequest(20, "named_item", "structured_layout", "documented_option"),
                gateway, CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        var claim = Assert.Single(result.Claims);
        Assert.Equal("C7", claim.ClaimId);
        Assert.Equal("OptionB", claim.SelectedItem);
        Assert.Equal("E2", Assert.Single(claim.EvidenceIds));
        Assert.Equal(replies.Count, result.ProviderCallCount);
        Assert.Equal(100 * replies.Count, result.InputTokens);
        Assert.Equal(50 * replies.Count, result.OutputTokens);
        if (verdict != "call_budget")
        {
            var final = factory.Requests[^1].Body;
            var user = WorkspaceUser(final);
            Assert.Equal("answered", user.GetProperty("candidate").GetProperty("outcome").GetString());
            var prior = user.GetProperty("priorCriticReview");
            Assert.False(prior.GetProperty("documentaryEvidence").GetBoolean());
            Assert.Equal("insufficient_documentation", prior.GetProperty("priorAssessment").GetProperty("outcome").GetString());
            Assert.False(user.TryGetProperty("insufficiencyReview", out _));
            Assert.False(user.TryGetProperty("researchTools", out _));
            using var wire = JsonDocument.Parse(final);
            Assert.False(wire.RootElement.TryGetProperty("tools", out _));
        }
        var reason = verdict switch
        {
            "call_budget" => "final_review_call_budget_unavailable",
            "output_limit" => "final_review_output_limit",
            "invalid" => "final_review_protocol_invalid",
            _ => "recovered_proposal_independently_reviewed"
        };
        Assert.Contains(logger.Events, e => e.Values.GetValueOrDefault("ReasonCode")?.ToString() == reason);
    }

    [Theory]
    [InlineData("responses")]
    [InlineData("chat-completions")]
    public async Task CriticRecoveryReview_does_not_hide_a_provider_filter(string protocol)
    {
        var bodies = EarlyBodies();
        var gateway = new PostResearchToolGateway((q, n) => new(q.Query, [.. bodies, EarlyLocator(bodies[0])], [], 1, n));
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan),
            EarlyReply(protocol, RepeatableTwentyAnswer()), EarlyReply(protocol, CriticPartialChoice),
            EarlyReply(protocol, RepeatableTwentyAnswer()), CriticStoppedReply(protocol, filtered: true));
        var options = EarlyOptions(protocol, 8); options.SemanticCriticEnabled = true;
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null).ExecuteAsync(
                BuildRequest(20, "named_item", "structured_layout", "documented_option"), gateway, CancellationToken.None));
        Assert.Equal("advanced_llm_content_filtered", error.ErrorCode);
        Assert.Equal(5, factory.Requests.Count);
    }
}
