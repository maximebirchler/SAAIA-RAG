using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions", true, 1, false, false)]
    [InlineData("responses", true, 1, false, false)]
    [InlineData("chat-completions", false, 1, false, false)]
    [InlineData("responses", false, 1, false, false)]
    [InlineData("chat-completions", true, 0, false, false)]
    [InlineData("responses", true, 0, false, false)]
    [InlineData("responses", true, 2, true, false)]
    [InlineData("responses", true, 1, false, true)]
    public async Task Navigation_actionability_reads_locators_only_in_bounded_insufficiency_review(
        string protocol, bool located, int remainingReads, bool timeLimit, bool critic)
    {
        HttpResponseMessage Call(string operation, string args) => protocol == "responses"
            ? BudgetNativeResponseFunctions([(operation, args)]) : NativeCompletion((operation, args));
        string Listing(int offset) => JsonSerializer.Serialize(new
        {
            sourceKey = "internal-source-1", offset, limit = 20, titleContains = (string?)null,
            pageStart = (int?)null, pageEnd = (int?)null, cardBackedOnly = true
        });
        var replies = new List<HttpResponseMessage>
        {
            Completion(GuidancePlan(8)), Call("list_source_titles", Listing(0)), Call("list_source_titles", Listing(2)),
            EarlyReply(protocol, GuidanceInsufficiency), EarlyReply(protocol, GuidanceInsufficiency)
        };
        var expectDiscoveryChallenge = !located && remainingReads > 0 && !timeLimit && !critic;
        if (expectDiscoveryChallenge) replies.Add(EarlyReply(protocol, GuidanceInsufficiency));
        if (critic) replies.Add(EarlyReply(protocol, GuidanceInsufficiency));
        using var factory = new QueuedHttpClientFactory(replies.ToArray());
        var options = EarlyOptions(protocol, 10);
        options.NativeSourceTitleNavigationEnabled = true;
        options.MaximumSearchTopK = 8;
        options.NativeResearchActiveProposalEnabled = false;
        options.SemanticCriticEnabled = critic;
        var initial = PreviousProjectionBody("INITIAL", "Canonical component inspection.", page: 1)
            with { SourceOverview = new(1, 100, 100) };
        var resultBody = PreviousProjectionBody("READ", "Secure the component before removal.", page: 77);
        var gateway = new NavigationActionabilityGateway(initial, resultBody, located, remainingReads, timeLimit);
        var shouldRead = located && remainingReads > 0;

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"),
                gateway, CancellationToken.None);

        var writer = WorkspaceUser(factory.Requests[1].Body);
        var lexical = writer.GetProperty("researchTools").GetProperty("sourceTitleExploration");
        Assert.Contains("cardBackedOnly=false", lexical.GetProperty("instruction").GetString());
        Assert.False(writer.GetProperty("researchTools").TryGetProperty("sourceTitleBodyRead", out _));
        for (var index = 2; index <= 3; index++)
        {
            var wire = factory.Requests[index].Body;
            var user = WorkspaceUser(wire);
            var research = user.GetProperty("researchTools");
            Assert.False(research.TryGetProperty("sourceTitleExploration", out _));
            var guidanceExpected = located && (index == 2 || remainingReads > 0);
            Assert.Equal(guidanceExpected, research.TryGetProperty("sourceTitleBodyRead", out var guidance));
            Assert.False(user.TryGetProperty("insufficiencyReview", out _));
            if (!guidanceExpected)
                continue;
            Assert.Equal("located_titles_without_body_read", guidance.GetProperty("reasonCode").GetString());
            Assert.Equal(3, guidance.GetProperty("visiblePhysicalLocators").GetInt32());
            Assert.False(guidance.GetProperty("documentaryEvidence").GetBoolean());
            Assert.Contains("pageCount=1", guidance.GetProperty("instruction").GetString());
            Assert.Contains("find_source_text", guidance.GetProperty("instruction").GetString());
            Assert.DoesNotContain("Body locator", guidance.GetRawText());
            using var envelope = JsonDocument.Parse(wire);
            var functions = envelope.RootElement.GetProperty("tools").EnumerateArray()
                .Select(tool => protocol == "responses" ? tool : tool.GetProperty("function"));
            Assert.Contains(functions, tool => tool.GetProperty("name").GetString() == "read_source");
        }
        if (shouldRead)
        {
            var listing = gateway.Requests.Last(request => request.Operation == "list_source_titles");
            var actualRead = Assert.Single(gateway.Requests, request => request.Operation == "read_source");
            Assert.Equal(listing.DocId, actualRead.DocId);
            Assert.Equal(listing.RevisionId, actualRead.RevisionId);
            Assert.Equal(listing.DocPath, actualRead.DocPath);
            Assert.Equal(8, actualRead.TopK);
            Assert.Equal(77, actualRead.PageStart);
            Assert.Equal(77, actualRead.PageEnd);

            var reviewWire = factory.Requests[4].Body;
            var review = WorkspaceUser(reviewWire);
            Assert.False(review.GetProperty("researchTools").TryGetProperty("sourceTitleBodyRead", out _));
            var readback = review.GetProperty("insufficiencyReview").GetProperty("locatorReadback");
            Assert.Equal(1, readback.GetProperty("executedReads").GetInt32());
            Assert.Equal(timeLimit ? 0 : 1, readback.GetProperty("readsWithEvidence").GetInt32());
            Assert.Equal(timeLimit, readback.GetProperty("timeLimitReached").GetBoolean());
            Assert.Equal(!timeLimit, review.GetProperty("evidence").GetRawText()
                .Contains(resultBody.Content, StringComparison.Ordinal));
            var originalTool = Assert.Single(NativeCorrelationOutputs(reviewWire, protocol));
            Assert.Equal("list_source_titles", originalTool.Body.GetProperty("operation").GetString());
            Assert.True(originalTool.Body.GetProperty("navigationOnly").GetBoolean());
            Assert.False(originalTool.Body.TryGetProperty("visibleEvidenceIds", out _));
        }
        else
            Assert.DoesNotContain(gateway.Requests, request => request.Operation == "read_source");
        if (critic)
        {
            var criticUser = WorkspaceUser(factory.Requests[5].Body);
            Assert.False(criticUser.GetProperty("researchTools").TryGetProperty("sourceTitleBodyRead", out _));
        }
        Assert.Equal((remainingReads == 0 ? 4 : critic ? 6 : 5) + (expectDiscoveryChallenge ? 1 : 0), factory.Requests.Count);
        Assert.Equal(expectDiscoveryChallenge ? 1 : 0,
            factory.Requests.Count(call => WorkspaceUser(call.Body).TryGetProperty("discoveryChallenge", out _)));
        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Empty(result.Claims);
    }

    private sealed class NavigationActionabilityGateway(
        AdvancedAnalysisResolvedEvidence initial, AdvancedAnalysisResolvedEvidence read,
        bool located, int remainingReads, bool timeLimit) : IAdvancedAnalysisToolGateway
    {
        private readonly List<AdvancedAnalysisResolvedEvidence> _evidence = [];
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => _evidence;
        public List<AdvancedAnalysisSearchRequest> Requests { get; } = [];
        public AdvancedAnalysisToolBudget Budget => new(10 + remainingReads, Requests.Count, 60_000,
            timeLimit && Requests.Any(request => request.Operation == "read_source") ? 60_000 : 0);

        public Task<AdvancedAnalysisSearchObservation> SearchAsync(
            AdvancedAnalysisSearchRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Operation == "list_source_titles")
            {
                var titles = new AdvancedAnalysisSourceTitle[]
                {
                    new("Contents without a physical target", "contents", 1, null, null),
                    new("Body locator", "title_anchor", null, located ? 77 : null, located ? 77 : null),
                    new("Alternate heading, same page", "title_anchor", null, located ? 77 : null, located ? 77 : null),
                    new("Later heading", "title_anchor", null, located ? 80 : null, located ? 80 : null)
                };
                return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, [], [], 0, Requests.Count,
                    SourceTitlePage: new(request.Offset, request.Offset + titles.Length, titles, false)));
            }
            if (request.Operation == "read_source" && timeLimit)
                throw new AdvancedAnalysisToolException("tool_time_limit_exceeded");
            var body = request.Operation == "read_source" ? read : initial;
            if (!_evidence.Contains(body))
                _evidence.Add(body);
            return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, [body], [], 0, Requests.Count));
        }
    }
}
