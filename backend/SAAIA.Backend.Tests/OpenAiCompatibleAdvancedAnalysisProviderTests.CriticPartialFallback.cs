using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    private static HttpResponseMessage CriticStoppedReply(string protocol, bool filtered = false)
        => new(HttpStatusCode.OK) { Content = protocol == "responses"
            ? JsonContent.Create(new { status = "incomplete", incomplete_details = new { reason = filtered ? "content_filter" : "max_output_tokens" },
                output = new[] { new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = "{\"outcome\":\"answered\"" } } } },
                usage = new { input_tokens = 100, output_tokens = 50, input_tokens_details = new { cached_tokens = 10, cache_write_tokens = 0 } } })
            : JsonContent.Create(new { choices = new[] { new { finish_reason = filtered ? "content_filter" : "length",
                    message = new { role = "assistant", content = "{\"outcome\":\"answered\"" } } },
                usage = new { prompt_tokens = 100, completion_tokens = 50, prompt_tokens_details = new { cached_tokens = 10, cache_write_tokens = 0 } } }) };

    [Theory]
    [InlineData("responses", false, false)]
    [InlineData("responses", false, true)]
    [InlineData("responses", true, false)]
    [InlineData("responses", true, true)]
    [InlineData("chat-completions", false, false)]
    [InlineData("chat-completions", false, true)]
    [InlineData("chat-completions", true, false)]
    [InlineData("chat-completions", true, true)]
    public async Task CriticPartialFallback_keeps_prior_partial_after_output_limit_and_counts_the_failed_call(
        string protocol, bool external, bool researchFirst)
    {
        var bodies = EarlyBodies();
        var gateway = new PostResearchToolGateway((q, n) => new(q.Query, [.. bodies, EarlyLocator(bodies[0])], [], 1, n));
        var replies = new List<HttpResponseMessage> { Completion(RepeatablePlan), EarlyReply(protocol, RepeatableTwentyAnswer()),
            EarlyReply(protocol, CriticPartialChoice) };
        if (researchFirst) replies.Add(PostResearchFunction(protocol, "find_source_text",
            """{"sourceKey":"internal-source-1","query":"OptionE","offset":0,"topK":3}"""));
        replies.Add(CriticStoppedReply(protocol));
        using var factory = new QueuedHttpClientFactory(replies.ToArray());
        var options = EarlyOptions(protocol, 8); options.SemanticCriticEnabled = true;
        if (external)
        {
            options.Provider = "openai-dev"; options.LlmLocation = "external-service";
            options.LlmBaseUrl = "https://api.openai.com/v1"; options.LlmModel = "gpt-5.6-luna";
            options.ExternalMaximumCostPerJobUsd = 2m;
        }
        var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, "fixture-only", logger)
            .ExecuteAsync(SynthesisPurposeRequest(false), gateway, CancellationToken.None);
        Assert.Equal("insufficient_documentation", result.Outcome);
        var claim = Assert.Single(result.Claims);
        Assert.Equal("C7", claim.ClaimId); Assert.Equal("OptionB", claim.SelectedItem);
        Assert.Equal("E2", Assert.Single(claim.EvidenceIds));
        Assert.Equal(JsonDocument.Parse(CriticPartialChoice).RootElement.GetProperty("answerText").GetString(), result.AnswerText);
        Assert.Equal(replies.Count, result.ProviderCallCount);
        Assert.Equal(100 * replies.Count, result.InputTokens); Assert.Equal(50 * replies.Count, result.OutputTokens);
        // The chat tool-call fixture omits cached usage; the other replies report ten.
        Assert.Equal(10 * (replies.Count - (researchFirst && protocol == "chat-completions" ? 1 : 0)), result.CachedInputTokens);
        Assert.Equal(researchFirst ? 2 : 1, gateway.Searches.Count);
        Assert.Contains(logger.Events, e => e.Values.GetValueOrDefault("ReasonCode")?.ToString() == "critic_review_output_limit");
        if (external)
        {
            var rows = File.ReadAllLines(options.ExternalUsageLedgerPath).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
            Assert.Equal(replies.Count, rows.Length);
            Assert.Equal(rows.Sum(r => r.GetProperty("costUsd").GetDecimal()), result.EstimatedCostUsd);
            Assert.Equal("advanced_llm_output_limit", rows[^1].GetProperty("errorCode").GetString());
            Assert.False(rows[^1].GetProperty("success").GetBoolean());
        }
        else Assert.Null(result.EstimatedCostUsd);
    }

    [Theory]
    [InlineData("responses", false)]
    [InlineData("responses", true)]
    [InlineData("chat-completions", false)]
    [InlineData("chat-completions", true)]
    public async Task CriticPartialFallback_does_not_hide_filters_or_publish_an_unreviewed_writer(string protocol, bool filtered)
    {
        var bodies = EarlyBodies();
        var gateway = new PostResearchToolGateway((q, n) => new(q.Query, [.. bodies, EarlyLocator(bodies[0])], [], 1, n));
        var replies = new List<HttpResponseMessage> { Completion(RepeatablePlan), EarlyReply(protocol, RepeatableTwentyAnswer()) };
        if (filtered) replies.Add(EarlyReply(protocol, CriticPartialChoice));
        replies.Add(CriticStoppedReply(protocol, filtered));
        using var factory = new QueuedHttpClientFactory(replies.ToArray());
        var options = EarlyOptions(protocol, 8); options.SemanticCriticEnabled = true;
        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
                .ExecuteAsync(SynthesisPurposeRequest(false), gateway, CancellationToken.None));
        Assert.Equal(filtered ? "advanced_llm_content_filtered" : "advanced_llm_output_limit", error.ErrorCode);
        Assert.Equal(replies.Count, factory.Requests.Count);
    }
}
