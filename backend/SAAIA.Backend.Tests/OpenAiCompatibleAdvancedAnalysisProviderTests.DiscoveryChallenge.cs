using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions", 16)]
    [InlineData("responses", 16)]
    [InlineData("chat-completions", 9)]
    [InlineData("responses", 9)]
    public async Task Empty_distinct_insufficiency_after_cache_only_review_offers_one_model_chosen_discovery_before_critic(string protocol, int maximumCalls)
    {
        var wires = new List<string>();
        var gateway = new DiscoveryChallengeGateway();
        using var factory = new OmittedHandleHttpFactory(async request =>
        {
            var wire = await request.Content!.ReadAsStringAsync();
            wires.Add(wire);
            var response = wires.Count switch
            {
                1 => Completion("""{"selectionMode":"distinct_named_items","queries":[{"query":"initial documented options","topK":4}]}"""),
                2 => PostResearchFunction(protocol, "read_source", """{"sourceKey":"internal-source-1","pageStart":2,"pageCount":1,"topK":4}"""),
                3 => EarlyReply(protocol, EarlyInsufficient),
                4 => PostResearchFunction(protocol, "read_evidence", """{"evidenceId":"E1","offset":0,"length":512}"""),
                5 => PostResearchFunction(protocol, "read_evidence", """{"evidenceId":"E2","offset":0,"length":512}"""),
                6 => EarlyReply(protocol, EarlyInsufficient),
                _ => null
            };
            if (response is not null)
                return await AdaptProtocolAgnosticFixtureAsync(request, response, CancellationToken.None);
            var user = WorkspaceUser(wire);
            if (wires.Count == 7)
            {
                // Parent a329 passes this empty Writer draft directly to Critic.
                // Neither the automatic review reads nor the two cache turns
                // tested a new model-chosen documentary hypothesis.
                Assert.True(user.TryGetProperty("discoveryChallenge", out var challenge),
                    "An empty reviewed Writer draft must receive its bounded discovery opportunity before Critic.");
                Assert.False(user.TryGetProperty("candidate", out _));
                Assert.False(challenge.GetProperty("documentaryEvidence").GetBoolean());
                Assert.True(user.GetProperty("researchTools").GetProperty("researchAllowed").GetBoolean());
                return await AdaptProtocolAgnosticFixtureAsync(request,
                    PostResearchFunction(protocol, "search_corpus", """{"category":"","query":"a different justified documentary check","sourceKey":"","documentHint":"","topK":1}"""), CancellationToken.None);
            }
            Assert.InRange(wires.Count, 8, 9);
            if (wires.Count == 9) Assert.True(user.TryGetProperty("candidate", out _));
            return await AdaptProtocolAgnosticFixtureAsync(request, EarlyReply(protocol, EarlyInsufficient), CancellationToken.None);
        });
        var options = EarlyOptions(protocol, maximumCalls);
        options.SemanticCriticEnabled = true;
        options.NativeResearchActiveProposalEnabled = false;
        options.MaximumEvidencePromptCharacters = options.NativeResearchMaximumHistoryCharacters = 98_304;
        var logger = new NativeCorrelationLogger();
        AdvancedAnalysisProviderResult result;
        try
        {
            result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, logger).ExecuteAsync(
                BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"), gateway, CancellationToken.None);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException("HTTP count=" + wires.Count + " " + string.Join(" / ", logger.Entries.Where(entry => entry.Contains("rejection") || entry.Contains("invalid"))), error);
        }

        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Empty(result.Claims);
        Assert.Equal(9, result.ProviderCallCount);
        Assert.Equal(9, wires.Count);
        Assert.Single(wires, wire => WorkspaceUser(wire).TryGetProperty("discoveryChallenge", out _));
        Assert.Single(gateway.Requests, query => query.Query == "a different justified documentary check");
        Assert.All(wires.Skip(1), wire => Assert.InRange(
            OpenAiCompatibleAdvancedAnalysisProvider.MeasureSecondaryDocumentaryWireCharacters(wire), 1, 98_304));
        Assert.InRange(gateway.Requests.Count, 3, 7);
    }

    [Theory]
    [InlineData("responses")]
    [InlineData("chat-completions")]
    public async Task Discovery_challenge_does_not_reset_an_already_used_no_progress_recovery(string protocol)
    {
        const string read = """{"sourceKey":"internal-source-1","pageStart":2,"pageCount":1,"topK":4}""";
        var gateway = new DiscoveryChallengeGateway();
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"selectionMode":"distinct_named_items","queries":[{"query":"initial documented options","topK":4}]}"""),
            PostResearchFunction(protocol, "read_source", read), EarlyReply(protocol, EarlyInsufficient),
            PostResearchFunction(protocol, "read_source", read), EarlyReply(protocol, EarlyInsufficient),
            PostResearchFunction(protocol, "read_source", read),
            EarlyReply(protocol, EarlyInsufficient), EarlyReply(protocol, EarlyInsufficient));
        var options = EarlyOptions(protocol, 16); options.SemanticCriticEnabled = true;
        options.NativeResearchActiveProposalEnabled = false;
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null).ExecuteAsync(
            BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"), gateway, CancellationToken.None);
        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Equal(8, factory.Requests.Count);
        using var final = JsonDocument.Parse(factory.Requests[6].Body);
        Assert.False(final.RootElement.TryGetProperty("tools", out _));
        Assert.True(WorkspaceUser(factory.Requests[4].Body).TryGetProperty("researchFeedback", out _));
        Assert.True(WorkspaceUser(factory.Requests[5].Body).TryGetProperty("discoveryChallenge", out _));
        Assert.Single(gateway.Requests, query => query.Operation == "read_source" && query.PageStart == 2);
    }

    [Theory]
    [InlineData("responses")]
    [InlineData("chat-completions")]
    public async Task Discovery_challenge_does_not_reopen_present_locators_whose_only_known_primary_pages_already_succeeded(string protocol)
    {
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan),
            PostResearchFunction(protocol, "list_source_titles", ReadbackListing("internal-source-1")),
            PostResearchFunction(protocol, "read_source", """{"sourceKey":"internal-source-1","pageStart":80,"pageCount":1,"topK":12}"""),
            EarlyReply(protocol, GuidanceInsufficiency), EarlyReply(protocol, GuidanceInsufficiency));
        var options = EarlyOptions(protocol, 9);
        options.NativeSourceTitleNavigationEnabled = true;
        options.NativeResearchActiveProposalEnabled = false;
        options.SemanticCriticEnabled = false;
        var gateway = new RetainedLocatorGateway();
        var logger = new NativeCorrelationLogger();
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, logger).ExecuteAsync(
            BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"), gateway, CancellationToken.None);
        Assert.Empty(result.Claims);
        Assert.Equal(5, factory.Requests.Count);
        Assert.DoesNotContain(factory.Requests, call => WorkspaceUser(call.Body).TryGetProperty("discoveryChallenge", out _));
        Assert.True(WorkspaceUser(factory.Requests[4].Body).GetProperty("sourceCatalogue")
            .GetProperty("titleNavigation").GetProperty("retainedCursorCount").GetInt32() > 0);
        Assert.Single(gateway.Requests, query => query.Operation == "read_source");
        Assert.Contains(logger.Entries, entry => entry.Contains("operation=llm.discovery_challenge outcome=skipped reason=no_current_research_locator", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("primary", false)]
    [InlineData("native", true)]
    [InlineData("table", true)]
    [InlineData("failed", true)]
    [InlineData("other-source", true)]
    [InlineData("other-revision", true)]
    [InlineData("other-page", true)]
    public void Discovery_challenge_primary_locator_coverage_requires_the_same_successful_primary_scope(string kind, bool expected)
    {
        var body = NavigationContinuityBody("CURRENT", 1, 1);
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions(); options.NativeSourceTitleNavigationEnabled = true;
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var context = AccumulatedReadContext(new AccumulatedReadGateway([body]));
        RememberRetainedLocatorPage(provider, context, body.Reference, 80);
        var search = new AdvancedAnalysisSearchRequest("Canonical physical page", DocId: body.Reference.DocId,
            DocPath: body.Reference.DocPath, PageStart: 80, PageEnd: 80, Operation: "read_source", RevisionId: body.Reference.RevisionId);
        if (kind == "native") search = search with { NativeObservationView = "layout" };
        if (kind == "table") search = search with { TableOrdinal = 1 };
        if (kind == "other-source") search = search with { DocId = "other" };
        if (kind == "other-revision") search = search with { RevisionId = "other" };
        if (kind == "other-page") search = search with { PageStart = 81, PageEnd = 81 };
        var searches = (List<AdvancedAnalysisSearchRequest>)context.GetType().GetProperty("PriorSearches")!.GetValue(context)!;
        var statuses = (Dictionary<int, string>)context.GetType().GetProperty("PriorSearchExecutionStatuses")!.GetValue(context)!;
        statuses[searches.Count] = kind == "failed" ? "failed" : "succeeded";
        searches.Add(search);
        var locators = context.GetType().GetProperty("RetainedTitleLocators")!.GetValue(context);
        Assert.Equal(expected, (bool)InvokeSourceCatalogue(null, "HasUnexaminedDiscoveryLocator", context, locators)!);
        Assert.Empty(factory.Requests);
    }

    [Theory]
    [InlineData("responses", "cache", true)]
    [InlineData("chat-completions", "cache", true)]
    [InlineData("responses", "repeat", true)]
    [InlineData("chat-completions", "repeat", true)]
    [InlineData("responses", "failed", true)]
    [InlineData("chat-completions", "failed", true)]
    [InlineData("responses", "empty_success", false)]
    [InlineData("chat-completions", "empty_success", false)]
    [InlineData("responses", "partial", false)]
    [InlineData("chat-completions", "partial", false)]
    [InlineData("responses", "reuse", false)]
    [InlineData("chat-completions", "reuse", false)]
    [InlineData("responses", "model_budget", false)]
    [InlineData("chat-completions", "model_budget", false)]
    [InlineData("responses", "tool_budget", false)]
    [InlineData("chat-completions", "tool_budget", false)]
    [InlineData("responses", "no_sources", false)]
    [InlineData("chat-completions", "no_sources", false)]
    public async Task Discovery_challenge_is_bounded_honest_and_requires_an_unexamined_model_route(
        string protocol, string mode, bool expectChallenge)
    {
        var gateway = new DiscoveryChallengeGateway { RefusePageNine = mode == "failed", NoLocators = mode == "no_sources" };
        var partial = JsonSerializer.Serialize(new { outcome = "insufficient_documentation", answerText = "Only this observed item is supported.",
            claims = new[] { new { claimId = "C1", selectedItem = "OptionA", text = "OptionA documentée.", evidenceIds = new[] { "E1" } } } });
        var terminal = mode == "partial" ? partial : EarlyInsufficient;
        var wires = new List<string>();
        using var factory = new OmittedHandleHttpFactory(async request =>
        {
            var wire = await request.Content!.ReadAsStringAsync(); wires.Add(wire);
            HttpResponseMessage reply;
            if (wires.Count == 1)
                reply = Completion(mode == "reuse" ? RepeatablePlan : """{"selectionMode":"distinct_named_items","queries":[{"query":"initial documented options","topK":4}]}""");
            else if (wires.Count == 2)
                reply = PostResearchFunction(protocol, "read_source", """{"sourceKey":"internal-source-1","pageStart":2,"pageCount":1,"topK":4}""");
            else
            {
                var user = WorkspaceUser(wire);
                if (user.TryGetProperty("discoveryChallenge", out var challenge))
                {
                    Assert.True(expectChallenge);
                    Assert.False(challenge.GetProperty("documentaryEvidence").GetBoolean());
                    // A precise nonanswer remains legitimate. An opportunity is
                    // not a successful search and must not manufacture a result.
                    reply = EarlyReply(protocol, EarlyInsufficient);
                }
                else if (user.TryGetProperty("insufficiencyReview", out _))
                    reply = mode switch
                    {
                        "empty_success" => PostResearchFunction(protocol, "search_corpus", """{"category":"","query":"new empty documentary check","sourceKey":"","documentHint":"","topK":1}"""),
                        "repeat" => PostResearchFunction(protocol, "read_source", """{"sourceKey":"internal-source-1","pageStart":2,"pageCount":1,"topK":4}"""),
                        "failed" => PostResearchFunction(protocol, "read_source", """{"sourceKey":"internal-source-1","pageStart":9,"pageCount":1,"topK":4}"""),
                        _ => PostResearchFunction(protocol, "read_evidence", """{"evidenceId":"E1","offset":0,"length":512}""")
                    };
                else
                {
                    if (mode == "tool_budget" && wires.Count >= 5) gateway.MaximumCalls = gateway.Requests.Count;
                    reply = EarlyReply(protocol, terminal);
                }
            }
            return await AdaptProtocolAgnosticFixtureAsync(request, reply, CancellationToken.None);
        });
        var options = EarlyOptions(protocol, mode == "model_budget" ? 7 : 16);
        options.SemanticCriticEnabled = true;
        options.NativeResearchActiveProposalEnabled = false;
        var logger = new NativeCorrelationLogger();
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, logger).ExecuteAsync(
            BuildRequest(20, "named_item", mode == "reuse" ? "structured_layout" : "distinct_structured_layout", "documented_option"), gateway, CancellationToken.None);
        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Equal(mode == "partial" ? 1 : 0, result.Claims.Count);
        var challenges = wires.Count(wire => WorkspaceUser(wire).TryGetProperty("discoveryChallenge", out _));
        Assert.Equal(expectChallenge ? 1 : 0, challenges);
        Assert.InRange(wires.Count, 4, 7);
        Assert.InRange(gateway.Requests.Count, 2, 7);
        if (mode == "empty_success") Assert.Single(gateway.Requests, query => query.Query == "new empty documentary check");
        if (mode == "repeat") Assert.Single(gateway.Requests, query => query.Operation == "read_source" && query.PageStart == 2);
        if (mode == "failed")
        {
            Assert.Single(gateway.Requests, query => query.PageStart == 9);
            Assert.Contains(logger.Entries, entry => entry.Contains("operation=llm.discovery_challenge outcome=attempted", StringComparison.Ordinal));
        }
        Assert.DoesNotContain(logger.Entries, entry => entry.Contains("query\":", StringComparison.Ordinal)
            && entry.Contains("operation=llm.discovery_challenge", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{\"outcome\":\"insufficient_documentation\",\"answerText\":\"Gap\",\"claims\":null}")]
    [InlineData("{\"outcome\":\"insufficient_documentation\",\"answerText\":\"Gap\",\"claims\":{}}")]
    [InlineData("{\"outcome\":\"insufficient_documentation\",\"answerText\":\"Gap\",\"claims\":[{}]}")]
    [InlineData("{\"outcome\":\"insufficient_documentation\",\"answerText\":\"Gap\",\"claims\":[],\"claims\":[]}")]
    [InlineData("{\"outcome\":\"answered\",\"outcome\":\"insufficient_documentation\",\"answerText\":\"Gap\",\"claims\":[]}")]
    [InlineData("{\"outcome\":\"insufficient_documentation\",\"claims\":[]}")]
    public void Discovery_challenge_never_treats_malformed_or_nonempty_claims_as_an_empty_draft(string raw)
    {
        Assert.False((bool)InvokeSourceCatalogue(null, "IsEmptyDiscoveryInsufficiency", raw, EarlyBodies(),
            BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"))!);
    }

    private sealed class DiscoveryChallengeGateway : IAdvancedAnalysisToolGateway
    {
        private readonly AdvancedAnalysisResolvedEvidence[] _bodies = EarlyBodies();
        private readonly List<AdvancedAnalysisResolvedEvidence> _evidence = [];
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => _evidence;
        public List<AdvancedAnalysisSearchRequest> Requests { get; } = [];
        public bool RefusePageNine { get; init; }
        public bool NoLocators { get; init; }
        public int MaximumCalls { get; set; } = 64;
        public AdvancedAnalysisToolBudget Budget => new(MaximumCalls, Requests.Count, 300_000, 0,
            128, _evidence.Count, true, 52);

        public Task<AdvancedAnalysisSearchObservation> SearchAsync(AdvancedAnalysisSearchRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (RefusePageNine && request.PageStart == 9)
                throw new AdvancedAnalysisToolException("canonical_read_window_result_limit_exceeded",
                    resourceLimit: new("canonical_read_window_result_limit_exceeded", null, null, null, StopsResearch: false));
            var found = NoLocators ? _bodies.Take(1).ToArray()
                : request.Query == "new empty documentary check" ? []
                : Requests.Count == 1 ? _bodies.Append(EarlyLocator(_bodies[0])).ToArray() : _bodies;
            foreach (var item in found)
                if (_evidence.All(current => current.Reference.EvidenceId != item.Reference.EvidenceId)) _evidence.Add(item);
            return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, found, [], 0, Requests.Count));
        }
    }
}
