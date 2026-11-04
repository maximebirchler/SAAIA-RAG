using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Retained_title_locators_review_inspects_a_previously_listed_other_source_after_body_turn(string protocol)
    {
        HttpResponseMessage Native(params (string Name, string Arguments)[] calls)
            => protocol == "responses" ? BudgetNativeResponseFunctions(calls) : NativeCompletion(calls);
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan),
            Native(("list_source_titles", ReadbackListing("internal-source-1")),
                ("list_source_titles", ReadbackListing("internal-source-2"))),
            Native(("read_source", """{"sourceKey":"internal-source-1","pageStart":77,"pageCount":1,"topK":12}""")),
            EarlyReply(protocol, GuidanceInsufficiency), EarlyReply(protocol, GuidanceInsufficiency));
        var options = EarlyOptions(protocol, 7);
        options.NativeSourceTitleNavigationEnabled = true;
        options.NativeResearchActiveProposalEnabled = false;
        options.SemanticCriticEnabled = false;
        var gateway = new RetainedLocatorGateway();

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null).ExecuteAsync(
            BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"),
            gateway, CancellationToken.None);

        var reads = gateway.Requests.Where(request => request.Operation == "read_source").ToArray();
        Assert.True(reads.Length >= 2,
            "A later body turn must not erase an observed unread locator in another current source before insufficiency review.");
        Assert.Equal(gateway.Initial[1].Reference.DocId, reads[1].DocId);
        Assert.Equal(gateway.Initial[1].Reference.RevisionId, reads[1].RevisionId);
        Assert.Equal(80, reads[1].PageStart);
        Assert.Equal(80, reads[1].PageEnd);
        Assert.Equal(12, reads[1].TopK);
        Assert.InRange(reads.Length - 1, 1, 4);
        var review = WorkspaceUser(factory.Requests[4].Body);
        Assert.True(review.GetProperty("insufficiencyReview").GetProperty("locatorReadback")
            .GetProperty("executedReads").GetInt32() >= 1);
        Assert.Contains("Canonical operating guidance for source 2, page 80.",
            review.GetProperty("evidence").GetRawText(), StringComparison.Ordinal);
        Assert.Equal(5, factory.Requests.Count);
        Assert.Empty(result.Claims);
    }

    [Theory]
    [InlineData(4, 0)]
    [InlineData(64, 60_000)]
    public async Task Retained_title_locators_never_bypass_the_shared_call_or_elapsed_budget(int maximumCalls, long elapsedAfterRead)
    {
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan),
            NativeCompletion(("list_source_titles", ReadbackListing("internal-source-1")),
                ("list_source_titles", ReadbackListing("internal-source-2"))),
            NativeCompletion(("read_source", """{"sourceKey":"internal-source-1","pageStart":77,"pageCount":1,"topK":12}""")),
            Completion(GuidanceInsufficiency));
        var options = EarlyOptions("chat-completions", 7);
        options.NativeSourceTitleNavigationEnabled = true;
        options.NativeResearchActiveProposalEnabled = false;
        options.SemanticCriticEnabled = false;
        var gateway = new RetainedLocatorGateway(maximumCalls, elapsedAfterRead);
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null).ExecuteAsync(
            BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"), gateway, CancellationToken.None);
        Assert.Single(gateway.Requests, request => request.Operation == "read_source");
        Assert.Equal(4, factory.Requests.Count);
        Assert.Empty(result.Claims);
    }

    [Fact]
    public async Task Retained_title_locators_never_repeat_an_already_successful_primary_page_read()
    {
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan),
            NativeCompletion(("list_source_titles", ReadbackListing("internal-source-1")),
                ("list_source_titles", ReadbackListing("internal-source-2"))),
            NativeCompletion(("read_source", """{"sourceKey":"internal-source-1","pageStart":80,"pageCount":1,"topK":12}""")),
            Completion(GuidanceInsufficiency), Completion(GuidanceInsufficiency));
        var options = EarlyOptions("chat-completions", 7);
        options.NativeSourceTitleNavigationEnabled = true;
        options.NativeResearchActiveProposalEnabled = false;
        options.SemanticCriticEnabled = false;
        var gateway = new RetainedLocatorGateway();
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null).ExecuteAsync(
            BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"), gateway, CancellationToken.None);
        var reads = gateway.Requests.Where(request => request.Operation == "read_source").ToArray();
        Assert.Equal(2, reads.Length);
        Assert.Equal(gateway.Initial[0].Reference.DocId, reads[0].DocId);
        Assert.Equal(gateway.Initial[1].Reference.DocId, reads[1].DocId);
        Assert.All(reads, read => Assert.Equal(80, read.PageStart));
        Assert.Equal(5, factory.Requests.Count);
    }

    private sealed class RetainedLocatorGateway(int maximumCalls = 64, long elapsedAfterRead = 0) : IAdvancedAnalysisToolGateway
    {
        public AdvancedAnalysisResolvedEvidence[] Initial { get; } =
            [NavigationContinuityBody("INITIAL-1", 1, 1), NavigationContinuityBody("INITIAL-2", 2, 1)];
        private readonly List<AdvancedAnalysisResolvedEvidence> _evidence = [];
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => _evidence;
        public List<AdvancedAnalysisSearchRequest> Requests { get; } = [];
        public AdvancedAnalysisToolBudget Budget => new(maximumCalls, Requests.Count, 60_000,
            Requests.Any(request => request.Operation == "read_source") ? elapsedAfterRead : 0);

        public Task<AdvancedAnalysisSearchObservation> SearchAsync(AdvancedAnalysisSearchRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Operation == "list_source_titles")
                return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, [], [], 0, Requests.Count,
                    SourceTitlePage: new(request.Offset, null,
                        [new("Documented checkpoint", "title_anchor", null, 80, 80)], false)));
            var source = Initial.FirstOrDefault(item => item.Reference.DocId == request.DocId) == Initial[1] ? 2 : 1;
            AdvancedAnalysisResolvedEvidence[] found = request.Operation == "read_source"
                ? [NavigationContinuityBody($"BODY-{Requests.Count}", source, request.PageStart ?? 1)] : Initial;
            foreach (var item in found)
                if (_evidence.All(current => current.Reference.EvidenceId != item.Reference.EvidenceId))
                    _evidence.Add(item);
            return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, found, [], 0, Requests.Count));
        }
    }
}
