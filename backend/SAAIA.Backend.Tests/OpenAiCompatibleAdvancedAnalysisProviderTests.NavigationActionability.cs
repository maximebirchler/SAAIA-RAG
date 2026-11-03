using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions", true)]
    [InlineData("responses", true)]
    [InlineData("chat-completions", false)]
    [InlineData("responses", false)]
    public async Task Navigation_actionability_follows_paid27_listing_and_existing_review_without_auto_read(
        string protocol, bool located)
    {
        HttpResponseMessage Call(string operation, string args) => protocol == "responses"
            ? BudgetNativeResponseFunctions([(operation, args)]) : NativeCompletion((operation, args));
        string Listing(int offset) => JsonSerializer.Serialize(new
        {
            sourceKey = "internal-source-1", offset, limit = 20, titleContains = (string?)null,
            pageStart = (int?)null, pageEnd = (int?)null, cardBackedOnly = false
        });
        const string read = """{"sourceKey":"internal-source-1","pageStart":77,"pageCount":1,"topK":12}""";
        var queued = new List<HttpResponseMessage>
        {
            Completion(GuidancePlan(8)), Call("list_source_titles", Listing(0)), Call("list_source_titles", Listing(2)),
            EarlyReply(protocol, GuidanceInsufficiency)
        };
        if (located)
            queued.Add(Call("read_source", read));
        queued.Add(EarlyReply(protocol, GuidanceInsufficiency));
        using var factory = new QueuedHttpClientFactory(queued.ToArray());
        var options = EarlyOptions(protocol, 10);
        options.NativeSourceTitleNavigationEnabled = true;
        options.NativeResearchActiveProposalEnabled = false;
        options.SemanticCriticEnabled = false;
        var initial = PreviousProjectionBody("INITIAL", "Canonical component inspection.", page: 1)
            with { SourceOverview = new(1, 100, 100) };
        var resultBody = PreviousProjectionBody("READ", "Secure the component before removal.", page: 77);
        var gateway = new NavigationActionabilityGateway(initial, resultBody, located);

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"),
                gateway, CancellationToken.None);

        var writer = WorkspaceUser(factory.Requests[1].Body);
        var lexical = writer.GetProperty("researchTools").GetProperty("sourceTitleExploration");
        Assert.Contains("cardBackedOnly=true", lexical.GetProperty("instruction").GetString());
        Assert.False(writer.GetProperty("researchTools").TryGetProperty("sourceTitleBodyRead", out _));
        for (var index = 2; index <= 4; index++)
        {
            var wire = factory.Requests[index].Body;
            var user = WorkspaceUser(wire);
            var research = user.GetProperty("researchTools");
            Assert.False(research.TryGetProperty("sourceTitleExploration", out _));
            Assert.Equal(located, research.TryGetProperty("sourceTitleBodyRead", out var guidance));
            Assert.Equal(index == 4, user.TryGetProperty("insufficiencyReview", out _));
            if (!located)
                continue;
            Assert.Equal("located_titles_without_body_read", guidance.GetProperty("reasonCode").GetString());
            Assert.Equal(1, guidance.GetProperty("visiblePhysicalLocators").GetInt32());
            Assert.False(guidance.GetProperty("documentaryEvidence").GetBoolean());
            Assert.Contains("pageCount=1", guidance.GetProperty("instruction").GetString());
            Assert.Contains("find_source_text", guidance.GetProperty("instruction").GetString());
            Assert.DoesNotContain("Body locator", guidance.GetRawText());
            using var envelope = JsonDocument.Parse(wire);
            var functions = envelope.RootElement.GetProperty("tools").EnumerateArray()
                .Select(tool => protocol == "responses" ? tool : tool.GetProperty("function"));
            Assert.Contains(functions, tool => tool.GetProperty("name").GetString() == "read_source");
        }
        if (located)
        {
            var actualRead = Assert.Single(gateway.Requests, request => request.Operation == "read_source");
            Assert.Equal(77, actualRead.PageStart);
            Assert.Equal(77, actualRead.PageEnd);
            Assert.False(WorkspaceUser(factory.Requests[5].Body).GetProperty("researchTools")
                .TryGetProperty("sourceTitleBodyRead", out _));
        }
        else
            Assert.DoesNotContain(gateway.Requests, request => request.Operation == "read_source");
        Assert.Equal(located ? 6 : 5, factory.Requests.Count);
        Assert.Equal("insufficient_documentation", result.Outcome);
        Assert.Empty(result.Claims);
    }

    private sealed class NavigationActionabilityGateway(
        AdvancedAnalysisResolvedEvidence initial, AdvancedAnalysisResolvedEvidence read, bool located) : IAdvancedAnalysisToolGateway
    {
        private readonly List<AdvancedAnalysisResolvedEvidence> _evidence = [];
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => _evidence;
        public List<AdvancedAnalysisSearchRequest> Requests { get; } = [];

        public Task<AdvancedAnalysisSearchObservation> SearchAsync(
            AdvancedAnalysisSearchRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Operation == "list_source_titles")
            {
                var titles = new AdvancedAnalysisSourceTitle[]
                {
                    new("Contents without a physical target", "contents", 1, null, null),
                    new("Body locator", "title_anchor", null, located ? 77 : null, located ? 77 : null)
                };
                return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, [], [], 0, Requests.Count,
                    SourceTitlePage: new(request.Offset, request.Offset + titles.Length, titles, false)));
            }
            var body = request.Operation == "read_source" ? read : initial;
            if (!_evidence.Contains(body))
                _evidence.Add(body);
            return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, [body], [], 0, Requests.Count));
        }
    }
}
