using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    private static string ReadbackListing(string key) => JsonSerializer.Serialize(new
    {
        sourceKey = key, offset = 0, limit = 20, titleContains = (string?)null,
        pageStart = (int?)null, pageEnd = (int?)null, cardBackedOnly = true
    });

    [Theory]
    [InlineData("chat-completions", 1)]
    [InlineData("responses", 1)]
    [InlineData("responses", 2)]
    public async Task Locator_readback_mixed_batch_preserves_unread_source_or_page(string protocol, int readSource)
    {
        var calls = new[]
        {
            ("read_source", JsonSerializer.Serialize(new
                { sourceKey = $"internal-source-{readSource}", pageStart = 77, pageCount = 1, topK = 12 })),
            ("list_source_titles", ReadbackListing("internal-source-2"))
        };
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan),
            protocol == "responses" ? BudgetNativeResponseFunctions(calls) : NativeCompletion(calls),
            EarlyReply(protocol, GuidanceInsufficiency), EarlyReply(protocol, GuidanceInsufficiency),
            EarlyReply(protocol, GuidanceInsufficiency));
        var options = EarlyOptions(protocol, 6);
        options.NativeSourceTitleNavigationEnabled = true;
        options.NativeResearchActiveProposalEnabled = false;
        options.SemanticCriticEnabled = false;
        var initial = new[]
        {
            NavigationCard("INITIAL-1", null, 1, 1, card: false),
            NavigationCard("INITIAL-2", null, 2, 1, card: false)
        };
        var explicitBody = NavigationCard("EXPLICIT", null, readSource, 77, card: false);
        var automaticBody = NavigationCard("AUTOMATIC", null, 2, 80, card: false);
        var gateway = new LocatorReadbackGateway(initial, explicitBody, automaticBody);

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"),
                gateway, CancellationToken.None);

        var reads = gateway.Requests.Where(request => request.Operation == "read_source").ToArray();
        Assert.Equal(2, reads.Length);
        Assert.Equal(77, reads[0].PageStart);
        Assert.Equal(80, reads[1].PageStart);
        Assert.Equal(initial[1].Reference.DocId, reads[1].DocId);
        Assert.Equal(initial[1].Reference.RevisionId, reads[1].RevisionId);
        var review = WorkspaceUser(factory.Requests[3].Body);
        Assert.Contains(automaticBody.Content, review.GetProperty("evidence").GetRawText());
        Assert.Equal(1, review.GetProperty("insufficiencyReview").GetProperty("locatorReadback")
            .GetProperty("executedReads").GetInt32());
        Assert.Equal(2, NativeCorrelationOutputs(factory.Requests[3].Body, protocol).Length);
        Assert.Empty(result.Claims);
        Assert.Equal(5, factory.Requests.Count);
        Assert.Single(factory.Requests, call => WorkspaceUser(call.Body).TryGetProperty("discoveryChallenge", out _));
    }

    [Theory]
    [InlineData("succeeded", false)]
    [InlineData("failed_broader", true)]
    [InlineData("failed_lower_cap", true)]
    [InlineData("failed_same", false)]
    [InlineData("failed_again", true)]
    public async Task Locator_readback_rechecks_a_repairable_failed_window_once(string firstRead, bool expectsRepair)
    {
        var pageEnd = firstRead is "failed_broader" or "failed_again" ? 81 : 80;
        var topK = firstRead == "failed_lower_cap" ? 1 : 12;
        var explicitRead = NativeCompletion(("read_source", JsonSerializer.Serialize(new
            { sourceKey = "internal-source-1", pageStart = 80, pageEnd, topK })));
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan), explicitRead,
            NativeCompletion(("list_source_titles", ReadbackListing("internal-source-1"))),
            Completion(GuidanceInsufficiency), Completion(GuidanceInsufficiency), Completion(GuidanceInsufficiency));
        var options = EarlyOptions("chat-completions", 7);
        options.NativeSourceTitleNavigationEnabled = true;
        options.NativeResearchActiveProposalEnabled = false;
        options.SemanticCriticEnabled = false;
        var initial = NavigationCard("INITIAL", null, 1, 1, card: false);
        var body = NavigationCard("BODY", null, 1, 80, card: false);
        var gateway = new LocatorReadbackGateway([initial], body, body, firstRead);

        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"),
                gateway, CancellationToken.None);

        var reads = gateway.Requests.Where(request => request.Operation == "read_source").ToArray();
        Assert.Equal(expectsRepair ? 2 : 1, reads.Length);
        if (expectsRepair)
        {
            Assert.Equal(80, reads[1].PageStart);
            Assert.Equal(80, reads[1].PageEnd);
            Assert.Equal(12, reads[1].TopK);
            var review = WorkspaceUser(factory.Requests[4].Body);
            Assert.Equal(1, review.GetProperty("insufficiencyReview").GetProperty("locatorReadback")
                .GetProperty("executedReads").GetInt32());
            Assert.Equal(firstRead != "failed_again", review.GetProperty("evidence").GetRawText()
                .Contains(body.Content, StringComparison.Ordinal));
        }
        // The indexed overview still exposes unread physical pages. One
        // discovery opportunity can be declined without another automatic read.
        Assert.Equal(6, factory.Requests.Count);
        Assert.Single(factory.Requests, call => WorkspaceUser(call.Body).TryGetProperty("discoveryChallenge", out _));
        Assert.Empty(result.Claims);
    }

    [Theory]
    [InlineData("responses")]
    [InlineData("chat-completions")]
    public async Task Locator_readback_rotates_sources_before_reading_more_pages_from_one_uninspected_source(string protocol)
    {
        var calls = new[]
        {
            ("read_source", JsonSerializer.Serialize(new
                { sourceKey = "internal-source-1", pageStart = 77, pageEnd = 77, topK = 12 })),
            ("list_source_titles", ReadbackListing("internal-source-1")),
            ("list_source_titles", ReadbackListing("internal-source-2"))
        };
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan),
            protocol == "responses" ? BudgetNativeResponseFunctions(calls) : NativeCompletion(calls),
            EarlyReply(protocol, GuidanceInsufficiency), EarlyReply(protocol, GuidanceInsufficiency),
            EarlyReply(protocol, GuidanceInsufficiency));
        var options = EarlyOptions(protocol, 6);
        options.NativeSourceTitleNavigationEnabled = true;
        options.NativeResearchActiveProposalEnabled = false;
        options.SemanticCriticEnabled = false;
        var initial = new[]
        {
            NavigationCard("INITIAL-1", null, 1, 1, card: false),
            NavigationCard("INITIAL-2", null, 2, 1, card: false)
        };
        var gateway = new BalancedLocatorReadbackGateway(initial);

        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"),
                gateway, CancellationToken.None);

        var reads = gateway.Requests.Where(r => r.Operation == "read_source").Skip(1).ToArray();
        Assert.Equal(4, reads.Length);
        Assert.Equal(new[] { initial[1].Reference.DocId, initial[0].Reference.DocId,
            initial[1].Reference.DocId, initial[0].Reference.DocId }, reads.Select(r => r.DocId));
        Assert.Equal(new int?[] { 80, 80, 81, 81 }, reads.Select(r => r.PageStart));
    }

    private sealed class BalancedLocatorReadbackGateway(
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> initial) : IAdvancedAnalysisToolGateway
    {
        private readonly List<AdvancedAnalysisResolvedEvidence> _evidence = [];
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => _evidence;
        public List<AdvancedAnalysisSearchRequest> Requests { get; } = [];

        public Task<AdvancedAnalysisSearchObservation> SearchAsync(
            AdvancedAnalysisSearchRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Operation == "list_source_titles")
                return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, [], [], 0, Requests.Count,
                    SourceTitlePage: new(0, null, Enumerable.Range(80, 4).Select(page =>
                        new AdvancedAnalysisSourceTitle("Checkpoint " + page, "title_anchor", null, page, page)).ToArray(), false)));
            IReadOnlyList<AdvancedAnalysisResolvedEvidence> found = initial;
            if (request.Operation == "read_source")
            {
                var source = request.DocId == initial[0].Reference.DocId ? 1 : 2;
                found = [NavigationCard($"BODY-{source}-{request.PageStart}", null, source, request.PageStart!.Value, card: false)];
            }
            foreach (var item in found)
                if (!_evidence.Contains(item)) _evidence.Add(item);
            return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, found, [], 0, Requests.Count));
        }
    }

    private sealed class LocatorReadbackGateway(
        IReadOnlyList<AdvancedAnalysisResolvedEvidence> initial,
        AdvancedAnalysisResolvedEvidence explicitBody,
        AdvancedAnalysisResolvedEvidence automaticBody,
        string firstRead = "succeeded") : IAdvancedAnalysisToolGateway
    {
        private readonly List<AdvancedAnalysisResolvedEvidence> _evidence = [];
        private int _reads;
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => _evidence;
        public List<AdvancedAnalysisSearchRequest> Requests { get; } = [];

        public Task<AdvancedAnalysisSearchObservation> SearchAsync(
            AdvancedAnalysisSearchRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Operation == "list_source_titles")
                return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, [], [], 0, Requests.Count,
                    SourceTitlePage: new(0, null, [new("Documented checkpoint", "title_anchor", null, 80, 80)], false)));
            var found = initial;
            if (request.Operation == "read_source")
            {
                _reads++;
                if (firstRead == "failed_again" || _reads == 1 && firstRead.StartsWith("failed", StringComparison.Ordinal))
                    throw new AdvancedAnalysisToolException("canonical_read_window_result_limit_exceeded",
                        new("canonical_read_window_result_limit_exceeded", null, null, null,
                            request.TopK, request.TopK + 1, StopsResearch: false));
                found = [_reads == 1 ? explicitBody : automaticBody];
            }
            foreach (var item in found)
                if (!_evidence.Contains(item))
                    _evidence.Add(item);
            return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, found, [], 0, Requests.Count));
        }
    }
}
