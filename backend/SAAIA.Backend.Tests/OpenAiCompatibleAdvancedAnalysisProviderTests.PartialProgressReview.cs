using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    private static string ProgressPartial(int count) => JsonSerializer.Serialize(new
    {
        outcome = "insufficient_documentation",
        answerText = "Choix documentés : " + string.Join(' ', Enumerable.Range(1, count).Select(i => $"[C{i}]")),
        claims = Enumerable.Range(1, count).Select(i => new
        {
            claimId = "C" + i, selectedItem = "Option" + (char)('A' + i - 1),
            text = "Option" + (char)('A' + i - 1) + " documentée.", evidenceIds = new[] { "E" + i }
        })
    });

    [Theory]
    [InlineData("responses")]
    [InlineData("chat-completions")]
    public async Task PartialProgressReview_continues_growing_supported_choices_but_stops_after_three_reviews(string protocol)
    {
        var responses = new List<HttpResponseMessage> { Completion(RepeatablePlan), EarlyReply(protocol, ProgressPartial(1)) };
        for (var i = 1; i <= 3; i++)
        {
            responses.Add(PostResearchFunction(protocol, "search_corpus", PostResearchSearch.Replace("options observées", "options observées " + i)));
            responses.Add(EarlyReply(protocol, ProgressPartial(i + 1)));
        }
        using var factory = new QueuedHttpClientFactory(responses.ToArray());
        var bodies = EarlyBodies();
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, EarlyOptions(protocol, 12), null)
            .ExecuteAsync(BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"),
                new RecordingToolGateway([.. bodies, EarlyLocator(bodies[0])]), CancellationToken.None);

        Assert.Equal(8, result.ProviderCallCount);
        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Equal(4, result.Claims.Count);
        foreach (var index in new[] { 4, 6 })
        {
            var review = WorkspaceUser(factory.Requests[index].Body).GetProperty("insufficiencyReview");
            Assert.Equal("supported_partial_selection_grew_after_research", review.GetProperty("reasonCode").GetString());
            Assert.False(review.GetProperty("priorDraftSelection").GetProperty("coordinateCoverageKnown").GetBoolean());
        }
    }

    [Theory]
    [InlineData("responses", 1, 12)]
    [InlineData("chat-completions", 1, 12)]
    [InlineData("responses", 2, 4)]
    public async Task PartialProgressReview_accepts_an_honest_plateau_or_exhausted_model_budget(string protocol, int finalCount, int maximumCalls)
    {
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan), EarlyReply(protocol, ProgressPartial(1)),
            PostResearchFunction(protocol, "search_corpus", PostResearchSearch), EarlyReply(protocol, ProgressPartial(finalCount)));
        var bodies = EarlyBodies();
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, EarlyOptions(protocol, maximumCalls), null)
            .ExecuteAsync(BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"),
                new RecordingToolGateway([.. bodies, EarlyLocator(bodies[0])]), CancellationToken.None);
        Assert.Equal(4, result.ProviderCallCount);
        Assert.Equal(finalCount, result.Claims.Count);
        Assert.Equal("insufficient_documentation", result.Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialProgressReview_requires_new_research_and_new_supported_names(bool newResearch)
    {
        var partial = ProgressPartial(2);
        if (newResearch) partial = partial.Replace("OptionB", "Unsupported alternative");
        var responses = new List<HttpResponseMessage> { Completion(RepeatablePlan), Completion(ProgressPartial(1)) };
        if (newResearch) responses.Add(PostResearchFunction("responses", "search_corpus", PostResearchSearch));
        responses.Add(Completion(partial));
        using var factory = new QueuedHttpClientFactory(responses.ToArray());
        var bodies = EarlyBodies();
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, EarlyOptions("responses", 12), null)
            .ExecuteAsync(BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"),
                new RecordingToolGateway([.. bodies, EarlyLocator(bodies[0])]), CancellationToken.None);
        Assert.Equal(newResearch ? 4 : 3, result.ProviderCallCount);
        Assert.Equal("insufficient_documentation", result.Outcome);
        // A larger unverified list or more prose alone cannot reopen the loop.
    }
}
