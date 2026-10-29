using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    private const string EarlyInsufficient = """{"outcome":"insufficient_documentation","answerText":"Les observations initiales ne suffisent pas encore.","claims":[]}""";
    private const string RepeatablePlan = """{"selectionMode":"repeatable_named_items","queries":[{"query":"options documentées","topK":12}]}""";

    [Theory]
    [InlineData("chat-completions", "contract")]
    [InlineData("responses", "contract")]
    [InlineData("responses", "agent")]
    public async Task EarlyInsufficiency_repeatable_grid_reopens_native_research_once_with_the_resolved_contract(
        string protocol, string style)
    {
        const string find = """{"sourceKey":"internal-source-1","query":"OptionA","offset":0,"topK":4}""";
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan),
            EarlyReply(protocol, EarlyInsufficient),
            protocol == "responses" ? NativeResponseFunction("find_source_text", find) : NativeCompletion(("find_source_text", find)),
            EarlyReply(protocol, RepeatableTwentyAnswer()));
        var options = EarlyOptions(protocol, 4); options.SynthesisPromptStyle = style;
        var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();
        var bodies = EarlyBodies();
        var gateway = new FeedbackToolGateway((query, number) => new(query.Query,
            number == 1 ? [EarlyLocator(bodies[0])] : bodies, [], 1, number));

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, logger)
            .ExecuteAsync(BuildRequest(20, "named_item", "structured_layout", "documented_option"), gateway, CancellationToken.None);

        Assert.Equal("answered", result.Outcome);
        Assert.Equal(20, result.Claims.Count);
        Assert.Equal(4, result.Claims.Select(c => c.SelectedItem).Distinct().Count());
        Assert.Equal(4, result.ProviderCallCount);
        Assert.Equal(2, gateway.Searches.Count);
        Assert.Equal("find_source_text", gateway.Searches[1].Operation);
        Assert.Equal("OptionA", gateway.Searches[1].Query);
        var first = WorkspaceUser(factory.Requests[1].Body);
        var contract = first.GetProperty("selectionContract");
        Assert.Equal("repeatable_named_items", contract.GetProperty("selectionMode").GetString());
        Assert.Equal(20, contract.GetProperty("requiredClaimCount").GetInt32());
        Assert.False(contract.GetProperty("distinctNamedItemsRequired").GetBoolean());
        Assert.True(contract.GetProperty("namedItemReuseAllowed").GetBoolean());
        var review = WorkspaceUser(factory.Requests[2].Body);
        Assert.True(review.GetProperty("researchTools").GetProperty("researchAllowed").GetBoolean());
        Assert.Equal("initial_insufficiency_before_documentary_research",
            review.GetProperty("insufficiencyReview").GetProperty("reasonCode").GetString());
        Assert.DoesNotContain("enough distinct documented candidates", PayloadAuditSystem(factory.Requests[1].Body));
        var diagnostic = Assert.Single(logger.Events, e => e.Values.GetValueOrDefault("Operation")?.ToString() == "llm.insufficiency_review");
        Assert.Equal("continued", diagnostic.Values["Outcome"]);
        using var measurements = JsonDocument.Parse(diagnostic.Values["Measurements"]!.ToString()!);
        Assert.Equal(2, measurements.RootElement.GetProperty("remaining_model_calls").GetInt32());
        Assert.True(measurements.RootElement.GetProperty("review_scheduled").GetBoolean());
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task EarlyInsufficiency_second_terminal_is_not_retried_or_recovered_again(string protocol)
    {
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan),
            EarlyReply(protocol, EarlyInsufficient), EarlyReply(protocol, EarlyInsufficient));
        var bodies = EarlyBodies();
        var gateway = new FeedbackToolGateway((query, number) => new(query.Query, [EarlyLocator(bodies[0])], [], 1, number));
        var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, EarlyOptions(protocol, 6), null, logger)
            .ExecuteAsync(BuildRequest(20, "named_item", "structured_layout", "documented_option"), gateway, CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Equal(3, result.ProviderCallCount);
        Assert.Single(gateway.Searches);
        Assert.Equal(3, factory.Requests.Count);
        Assert.Contains(logger.Events, e => e.Values.GetValueOrDefault("ReasonCode")?.ToString() == "insufficiency_review_already_used");
    }

    [Fact]
    public async Task EarlyInsufficiency_critic_preserves_the_already_reviewed_writer_terminal()
    {
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan),
            NativeResponseAnswer(EarlyInsufficient), NativeResponseAnswer(EarlyInsufficient),
            NativeResponseAnswer(EarlyInsufficient));
        var bodies = EarlyBodies();
        var options = EarlyOptions("responses", 6); options.SemanticCriticEnabled = true;
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(20, "named_item", "structured_layout", "documented_option"),
                new RecordingToolGateway([EarlyLocator(bodies[0]), .. bodies]), CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Equal(4, result.ProviderCallCount);
        Assert.Equal(4, factory.Requests.Count);
        Assert.Contains("insufficiencyReview", factory.Requests[2].Body);
        Assert.True(WorkspaceUser(factory.Requests[3].Body).TryGetProperty("candidate", out _));
    }

    [Theory]
    [InlineData(2, 32, "model_budget_unavailable")]
    [InlineData(3, 32, "model_budget_unavailable")]
    [InlineData(6, 1, "documentary_budget_unavailable")]
    public async Task EarlyInsufficiency_does_not_schedule_an_unusable_native_review(int calls, int tools, string reason)
    {
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan), NativeResponseAnswer(EarlyInsufficient));
        var bodies = EarlyBodies();
        var gateway = new FeedbackToolGateway((query, number) => new(query.Query, [EarlyLocator(bodies[0])], [], 1, number), tools);
        var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, EarlyOptions("responses", calls), null, logger)
            .ExecuteAsync(BuildRequest(20, "named_item", "structured_layout", "documented_option"), gateway, CancellationToken.None);

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Equal(2, factory.Requests.Count);
        Assert.DoesNotContain(factory.Requests, item => WorkspaceUser(item.Body).TryGetProperty("insufficiencyReview", out _));
        Assert.Contains(logger.Events, e => e.Values.GetValueOrDefault("ReasonCode")?.ToString() == reason);
    }

    [Fact]
    public async Task EarlyInsufficiency_direct_fact_does_not_trigger_structured_selection_research()
    {
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"overview","topK":12}]}"""), NativeResponseAnswer(EarlyInsufficient));
        var body = EarlyBodies()[0];
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, EarlyOptions("responses", 6), null)
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(EarlyLocator(body)), CancellationToken.None);
        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Equal(2, factory.Requests.Count);
    }

    [Fact]
    public async Task EarlyInsufficiency_repeatable_fallback_accepts_four_bodies_for_twenty_coordinates()
    {
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan),
            Completion("""{"decision":"ready","queries":[]}"""), Completion(EarlyInsufficient), Completion(RepeatableTwentyAnswer()));
        var options = CreateOptions(); options.AdaptiveResearchEnabled = true; options.ExternalMaximumCallsPerJob = 4;
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(20, "named_item", "structured_layout", "documented_option"),
                new RecordingToolGateway(EarlyBodies()), CancellationToken.None);
        Assert.Equal("answered", result.Outcome);
        Assert.Equal(4, result.ProviderCallCount);
        Assert.Equal(20, result.Claims.Count);
        Assert.Equal("repeatable_named_items", WorkspaceUser(factory.Requests[3].Body)
            .GetProperty("selectionContract").GetProperty("selectionMode").GetString());
    }

    [Fact]
    public async Task EarlyInsufficiency_distinct_fallback_keeps_the_required_distinct_count()
    {
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"selectionMode":"distinct_named_items","queries":[{"query":"options","topK":12}]}"""),
            Completion("""{"decision":"ready","queries":[]}"""), Completion(EarlyInsufficient));
        var options = CreateOptions(); options.AdaptiveResearchEnabled = true; options.ExternalMaximumCallsPerJob = 4;
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(20), new RecordingToolGateway(EarlyBodies()), CancellationToken.None);
        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Equal(3, factory.Requests.Count);
        var contract = WorkspaceUser(factory.Requests[2].Body).GetProperty("selectionContract");
        Assert.True(contract.GetProperty("distinctNamedItemsRequired").GetBoolean());
        Assert.False(contract.GetProperty("namedItemReuseAllowed").GetBoolean());
    }

    private static AdvancedAnalysisOptions EarlyOptions(string protocol, int calls)
    {
        var options = WorkspaceOptions(protocol); options.NativeResearchWorkspaceEnabled = false;
        options.ExternalMaximumCallsPerJob = calls;
        return options;
    }

    private static HttpResponseMessage EarlyReply(string protocol, string text)
        => protocol == "responses" ? NativeResponseAnswer(text) : Completion(text);

    private static AdvancedAnalysisResolvedEvidence[] EarlyBodies()
    {
        var doc = Guid.NewGuid().ToString("D"); var revision = Guid.NewGuid().ToString("D");
        return Enumerable.Range(1, 4).Select(index => BuildEvidence($"E{index}",
            $"Option{(char)('A' + index - 1)}\nComposition documentée. Préparation : suivre les étapes décrites de cette option.",
            docId: doc, revisionId: revision, pageStart: index + 1, exactTitle: $"Option{(char)('A' + index - 1)}")).ToArray();
    }

    private static AdvancedAnalysisResolvedEvidence EarlyLocator(AdvancedAnalysisResolvedEvidence body)
        => BuildEvidence("E0", "Table of contents\nOptionA 7\nOptionB 8\nOptionC 9\nOptionD 10",
            docId: body.Reference.DocId, revisionId: body.Reference.RevisionId, exactTitle: "Table of contents") with
        { SourceOverview = new(1, 40, 100) };

    private static string RepeatableTwentyAnswer()
    {
        var days = new[] { "lundi", "mardi", "mercredi", "jeudi", "vendredi" };
        var rows = days.Select((day, row) => "|" + day + "|" + string.Join("|", Enumerable.Range(0, 4)
            .Select(column => $"Option{(char)('A' + column)} [C{row * 4 + column + 1}]")) + "|");
        return JsonSerializer.Serialize(new
        {
            outcome = "answered",
            answerText = "|Jour|petit-déjeuner|déjeuner|collation|souper|\n|---|---|---|---|---|\n" + string.Join("\n", rows),
            claims = Enumerable.Range(1, 20).Select(index => new
            {
                claimId = $"C{index}", selectedItem = $"Option{(char)('A' + (index - 1) % 4)}",
                text = $"Option{(char)('A' + (index - 1) % 4)} est documentée.", evidenceIds = new[] { $"E{(index - 1) % 4 + 1}" }
            }).ToArray()
        });
    }
}
