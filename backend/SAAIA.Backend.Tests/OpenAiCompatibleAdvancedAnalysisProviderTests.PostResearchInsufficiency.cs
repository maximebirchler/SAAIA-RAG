using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    private const string PostResearchSearch = """{"query":"autres options observées","sourceKey":"internal-source-1","category":"","documentHint":"","topK":12}""";
    private const string PostResearchFind = """{"sourceKey":"internal-source-2","query":"OptionA","offset":0,"topK":4}""";

    [Theory]
    [InlineData("chat-completions", "contract")]
    [InlineData("responses", "contract")]
    [InlineData("responses", "agent")]
    public async Task PostResearchInsufficiency_partial_search_keeps_tools_and_other_observed_source_available(
        string protocol, string style)
    {
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan),
            PostResearchFunction(protocol, "search_corpus", PostResearchSearch),
            EarlyReply(protocol, EarlyInsufficient),
            PostResearchFunction(protocol, "find_source_text", PostResearchFind),
            EarlyReply(protocol, RepeatableTwentyAnswer()));
        var options = EarlyOptions(protocol, 7); options.SynthesisPromptStyle = style;
        var bodies = PostResearchBodies();
        var primary = BuildEvidence("PRIMARY", "Option principale\nComposition et utilisation documentées.", exactTitle: "Option principale");
        var otherLocator = BuildEvidence("OTHER-LOCATOR", "Table of contents\nOptionA 7\nOptionB 8\nOptionC 9\nOptionD 10",
            "secondary-options.pdf", bodies[0].Reference.DocId, bodies[0].Reference.RevisionId, exactTitle: "Table of contents") with
        {
            SourceOverview = new(1, 40, 100)
        };
        var gateway = new PostResearchToolGateway((query, number) => new(query.Query,
            number == 1 ? [primary, otherLocator] : number == 2 ? [primary] : bodies, [], 1, number));
        var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, logger)
            .ExecuteAsync(BuildRequest(20, "named_item", "structured_layout", "documented_option"), gateway, CancellationToken.None);

        // On the former path, request4 was a tool-free synthesis recovery. The
        // scripted native find cannot produce this qualified twenty-cell answer
        // there. This proves orchestration, not autonomous culinary judgement.
        Assert.Equal("answered", result.Outcome);
        Assert.Equal(20, result.Claims.Count);
        Assert.Equal(4, result.Claims.Select(claim => claim.SelectedItem).Distinct().Count());
        Assert.Equal(5, result.ProviderCallCount);
        Assert.Equal(3, gateway.Searches.Count);
        Assert.Equal("find_source_text", gateway.Searches[2].Operation);
        Assert.Equal(bodies[0].Reference.DocId, gateway.Searches[2].DocId);
        Assert.NotEqual(gateway.Searches[1].DocId, gateway.Searches[2].DocId);
        var review = WorkspaceUser(factory.Requests[3].Body);
        Assert.Equal("insufficiency_after_partial_documentary_research",
            review.GetProperty("insufficiencyReview").GetProperty("reasonCode").GetString());
        Assert.True(review.GetProperty("researchTools").GetProperty("researchAllowed").GetBoolean());
        var sources = review.GetProperty("sourceCatalogue").GetProperty("sources").EnumerateArray().ToArray();
        Assert.Equal(2, sources.Length);
        Assert.Contains(sources, source => source.GetProperty("sourceKey").GetString() == "internal-source-2"
            && source.GetProperty("displayName").GetString() == "secondary-options.pdf");
        using var wire = JsonDocument.Parse(factory.Requests[3].Body);
        var find = Assert.Single(wire.RootElement.GetProperty("tools").EnumerateArray(), tool =>
            (protocol == "responses" ? tool : tool.GetProperty("function")).GetProperty("name").GetString() == "find_source_text");
        var function = protocol == "responses" ? find : find.GetProperty("function");
        Assert.Contains(function.GetProperty("parameters").GetProperty("properties").GetProperty("sourceKey")
            .GetProperty("enum").EnumerateArray(), value => value.GetString() == "internal-source-2");
        Assert.False(review.GetProperty("selectionContract").GetProperty("distinctNamedItemsRequired").GetBoolean());
        Assert.DoesNotContain(factory.Requests, call => PayloadAuditSystem(call.Body).Contains("final SAAIA synthesis completer", StringComparison.Ordinal));
        var diagnostic = Assert.Single(logger.Events, entry => entry.Values.GetValueOrDefault("Operation")?.ToString() == "llm.insufficiency_review");
        using var measurements = JsonDocument.Parse(diagnostic.Values["Measurements"]!.ToString()!);
        Assert.True(measurements.RootElement.GetProperty("review_scheduled").GetBoolean());
        Assert.Equal(1, measurements.RootElement.GetProperty("prior_documentary_followup_count").GetInt32());
        Assert.True(measurements.RootElement.GetProperty("native_turn_present").GetBoolean());
        Assert.Equal(2, measurements.RootElement.GetProperty("available_source_locator_count").GetInt32());
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task PostResearchInsufficiency_review_is_monotone_and_final_insufficiency_is_not_filled_by_closed_recovery(string protocol)
    {
        const string find = """{"sourceKey":"internal-source-1","query":"OptionA","offset":0,"topK":4}""";
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan),
            PostResearchFunction(protocol, "search_corpus", PostResearchSearch), EarlyReply(protocol, EarlyInsufficient),
            PostResearchFunction(protocol, "find_source_text", find), EarlyReply(protocol, EarlyInsufficient));
        var bodies = EarlyBodies();
        var gateway = new PostResearchToolGateway((query, number) => new(query.Query,
            number == 1 ? [EarlyLocator(bodies[0])] : bodies, [], 1, number));
        var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, EarlyOptions(protocol, 10), null, logger)
            .ExecuteAsync(BuildRequest(20, "named_item", "structured_layout", "documented_option"), gateway, CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Empty(result.Claims);
        Assert.Equal(5, result.ProviderCallCount);
        Assert.Equal(5, factory.Requests.Count);
        Assert.Equal(3, gateway.Searches.Count);
        Assert.Contains(logger.Events, entry => entry.Values.GetValueOrDefault("ReasonCode")?.ToString() == "insufficiency_after_partial_documentary_research");
        Assert.Contains(logger.Events, entry => entry.Values.GetValueOrDefault("ReasonCode")?.ToString() == "insufficiency_review_already_used");
        Assert.DoesNotContain(factory.Requests, call => PayloadAuditSystem(call.Body).Contains("final SAAIA synthesis completer", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(4, 32, 0, "model_budget_unavailable")]
    [InlineData(7, 2, 0, "documentary_budget_unavailable")]
    [InlineData(7, 32, 60_000, "documentary_budget_unavailable")]
    public async Task PostResearchInsufficiency_review_requires_two_model_turns_and_remaining_documentary_calls_and_time(
        int calls, int toolCalls, long elapsedAfterResearch, string reason)
    {
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan),
            NativeResponseFunction("search_corpus", PostResearchSearch), NativeResponseAnswer(EarlyInsufficient), NativeResponseAnswer(EarlyInsufficient));
        var bodies = EarlyBodies();
        var gateway = new PostResearchToolGateway((query, number) => new(query.Query,
            number == 1 ? [EarlyLocator(bodies[0])] : bodies, [], 1, number), toolCalls, elapsedAfterResearch);
        var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, EarlyOptions("responses", calls), null, logger)
            .ExecuteAsync(BuildRequest(20, "named_item", "structured_layout", "documented_option"), gateway, CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.DoesNotContain(factory.Requests, call => WorkspaceUser(call.Body).TryGetProperty("insufficiencyReview", out _));
        Assert.Contains(logger.Events, entry => entry.Values.GetValueOrDefault("ReasonCode")?.ToString() == reason);
        Assert.Equal(2, gateway.Searches.Count);
    }

    [Fact]
    public async Task PostResearchInsufficiency_without_navigation_omission_or_another_source_does_not_fabricate_a_frontier()
    {
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan),
            NativeResponseFunction("search_corpus", PostResearchSearch), NativeResponseAnswer(EarlyInsufficient), NativeResponseAnswer(EarlyInsufficient));
        var body = EarlyBodies()[0];
        var gateway = new PostResearchToolGateway((query, number) => new(query.Query, [body], [], 1, number));
        var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, EarlyOptions("responses", 7), null, logger)
            .ExecuteAsync(BuildRequest(20, "named_item", "structured_layout", "documented_option"), gateway, CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.DoesNotContain(factory.Requests, call => WorkspaceUser(call.Body).TryGetProperty("insufficiencyReview", out _));
        Assert.Contains(logger.Events, entry => entry.Values.GetValueOrDefault("ReasonCode")?.ToString() == "no_current_research_locator");
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task PostResearchInsufficiency_review_does_not_reexecute_duplicates_or_reset_no_progress_recovery(string protocol)
    {
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan),
            PostResearchFunction(protocol, "search_corpus", PostResearchSearch), EarlyReply(protocol, EarlyInsufficient),
            PostResearchFunction(protocol, "search_corpus", PostResearchSearch),
            PostResearchFunction(protocol, "search_corpus", PostResearchSearch));
        var bodies = EarlyBodies();
        var gateway = new PostResearchToolGateway((query, number) => new(query.Query,
            number == 1 ? [EarlyLocator(bodies[0])] : bodies, [], 1, number));

        var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
            new OpenAiCompatibleAdvancedAnalysisProvider(factory, EarlyOptions(protocol, 9), null)
                .ExecuteAsync(BuildRequest(20, "named_item", "structured_layout", "documented_option"), gateway, CancellationToken.None));

        Assert.Equal("advanced_synthesis_research_no_progress", error.ErrorCode);
        Assert.Equal(2, gateway.Searches.Count);
        Assert.Equal(5, factory.Requests.Count);
        Assert.Equal("search_already_executed", WorkspaceUser(factory.Requests[4].Body)
            .GetProperty("researchFeedback").GetProperty("reasonCode").GetString());
    }

    private static HttpResponseMessage PostResearchFunction(string protocol, string name, string arguments)
        => protocol == "responses" ? NativeResponseFunction(name, arguments) : NativeCompletion((name, arguments));

    private static AdvancedAnalysisResolvedEvidence[] PostResearchBodies()
    {
        var doc = Guid.NewGuid().ToString("D"); var revision = Guid.NewGuid().ToString("D");
        return Enumerable.Range(1, 4).Select(index => BuildEvidence($"E{index}",
            $"Option{(char)('A' + index - 1)}\nComposition documentée. Préparation : suivre les étapes décrites de cette option.",
            "secondary-options.pdf", doc, revision, index + 1, $"Option{(char)('A' + index - 1)}")).ToArray();
    }

    private sealed class PostResearchToolGateway(
        Func<AdvancedAnalysisSearchRequest, int, AdvancedAnalysisSearchObservation> respond,
        int maximumCalls = 32, long elapsedAfterResearch = 0) : IAdvancedAnalysisToolGateway
    {
        private readonly List<AdvancedAnalysisResolvedEvidence> _evidence = [];
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => _evidence;
        public List<AdvancedAnalysisSearchRequest> Searches { get; } = [];
        public AdvancedAnalysisToolBudget? Budget => new(maximumCalls, Searches.Count, 60_000,
            Searches.Count >= 2 ? elapsedAfterResearch : 0);
        public Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>(["Corpus"]);
        public Task<AdvancedAnalysisSearchObservation> SearchAsync(AdvancedAnalysisSearchRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Searches.Add(request);
            var observation = respond(request, Searches.Count);
            foreach (var item in observation.Evidence)
                if (_evidence.All(existing => existing.Reference.EvidenceId != item.Reference.EvidenceId)) _evidence.Add(item);
            return Task.FromResult(observation);
        }
    }
}
