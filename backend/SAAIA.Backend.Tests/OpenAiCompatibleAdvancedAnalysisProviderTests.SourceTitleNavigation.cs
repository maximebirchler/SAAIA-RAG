using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Contracts;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Fact]
    public void Source_title_navigation_schema_is_opt_in_and_scope_and_window_are_enforced()
    {
        using var factory = new QueuedHttpClientFactory();
        var options = WorkspaceOptions();
        var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null);
        var request = NavigationRequest();
        var projection = NavigationProjection(provider, request, [NavigationCard("CARD-1", "PROCEDURE", 1, 3, 4)],
            new Dictionary<string, string>());
        var prompt = NavigationPrompt(provider, request, projection);
        var operation = AdvancedAnalysisSourceTitlePage.Operation;
        string Schema() => JsonSerializer.Serialize(InvokeSourceCatalogue(provider, "BuildNativeResearchFunctions", prompt));
        Assert.DoesNotContain(operation, Schema());
        options.NativeSourceTitleNavigationEnabled = true;
        Assert.Contains(operation, Schema());

        using var promptDocument = JsonDocument.Parse(prompt);
        var sourceKey = promptDocument.RootElement.GetProperty("sourceCatalogue").GetProperty("sources")[0].GetProperty("sourceKey").GetString();
        IReadOnlyList<AdvancedAnalysisSearchRequest> Parse(string? key, int offset, int limit, string? titleContains = null,
            int? pageStart = null, int? pageEnd = null, bool cardBackedOnly = false)
        {
            var review = JsonSerializer.Serialize(new { decision = "search_more", queries = new[]
                { new { operation, sourceKey = key, offset, limit, titleContains, pageStart, pageEnd, cardBackedOnly } } });
            return (IReadOnlyList<AdvancedAnalysisSearchRequest>)InvokeSourceCatalogue(provider, "ParseResearchReview",
                review, request, Array.Empty<string>(), projection)!;
        }
        var resolved = Assert.Single(Parse(sourceKey, 7, 13));
        Assert.Equal(7, resolved.Offset);
        Assert.Equal(13, resolved.TopK);
        Assert.Null(resolved.PageStart);
        var scope = (AdvancedAnalysisResultEvidence)InvokeSourceCatalogue(null, "ResolveObservedSource", projection, sourceKey)!;
        Assert.Equal(scope.SourceHash, resolved.ObservedSourceHash);
        Assert.Equal(sourceKey, InvokeSourceCatalogue(null, "CurrentSearchSourceKey", resolved, projection));
        var filtered = Assert.Single(Parse(sourceKey, 0, 20, "seal", 3, 18, true));
        Assert.Equal("seal", filtered.TitleContains);
        Assert.Equal(3, filtered.PageStart);
        Assert.Equal(18, filtered.PageEnd);
        Assert.True(filtered.CardBackedTitlesOnly);
        Assert.Throws<AdvancedAnalysisProviderException>(() => Parse(sourceKey, 0, 10, "x"));
        Assert.Throws<AdvancedAnalysisProviderException>(() => Parse(sourceKey, 0, 10, pageStart: 8, pageEnd: 7));
        Assert.Throws<AdvancedAnalysisProviderException>(() => Parse("forged", 0, 10));
        Assert.Throws<AdvancedAnalysisProviderException>(() => Parse(null, 0, 10));
        Assert.Throws<AdvancedAnalysisProviderException>(() => Parse(sourceKey, -1, 10));
        Assert.Throws<AdvancedAnalysisProviderException>(() => Parse(sourceKey, 10_001, 10));
        Assert.Throws<AdvancedAnalysisProviderException>(() => Parse(sourceKey, 0, 0));
        Assert.Throws<AdvancedAnalysisProviderException>(() => Parse(sourceKey, 0, 21));
    }

    [Fact]
    public void Source_title_navigation_budget_advances_by_whole_locators_and_reports_a_blocking_oversized_row()
    {
        var locators = Enumerable.Range(0, 21).Select(i => new AdvancedAnalysisSourceTitle(
            $"Index {i} {new string('é', 40)}", "title_anchor", null, i + 1, i + 1)).ToArray();
        var fitted = AdvancedAnalysisSourceTitlePage.Fit(5, 20, locators);
        Assert.InRange(fitted.Items.Count, 1, 19);
        Assert.Equal(locators.Take(fitted.Items.Count), fitted.Items);
        Assert.Equal(5 + fitted.Items.Count, fitted.NextOffset);
        Assert.Equal(0, fitted.OmittedOversizedLocators);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.InRange(JsonSerializer.Serialize(JsonSerializer.Serialize(fitted, json), json).Length,
            1, AdvancedAnalysisSourceTitlePage.MaximumPageCharacters);
        var oversized = locators[0] with { Title = new string('"', 5000) };
        var omitted = AdvancedAnalysisSourceTitlePage.Fit(17, 1, [oversized, locators[1]]);
        Assert.Empty(omitted.Items);
        Assert.Equal(18, omitted.NextOffset);
        Assert.Equal(1, omitted.OmittedOversizedLocators);
        var cap = AdvancedAnalysisSourceTitlePage.Fit(10_000, 1, locators.Take(2).ToArray());
        Assert.True(cap.OffsetLimitReached);
        Assert.Null(cap.NextOffset);
        Assert.Empty(AdvancedAnalysisSourceTitlePage.Fit(0, 1, []).Items);
    }

    [Theory]
    [InlineData("chat-completions")]
    [InlineData("responses")]
    public async Task Source_title_navigation_is_linked_as_locator_only_then_a_separate_read_admits_body(string protocol)
    {
        const string key = "internal-source-1";
        const string locator = "Undiscovered inspection";
        const string body = "CANONICAL_BODY: Verify that the assembly is isolated.";
        var calls = new[]
        {
            ("list_source_titles", JsonSerializer.Serialize(new { sourceKey = key, offset = 0, limit = 10, titleContains = (string?)null, pageStart = (int?)null, pageEnd = (int?)null, cardBackedOnly = false })),
            ("read_source", JsonSerializer.Serialize(new { sourceKey = key, pageStart = 77, pageEnd = 77, topK = 12 }))
        };
        var replies = calls.Select(call => protocol == "responses"
            ? BudgetNativeResponseFunctions([call]) : NativeCompletion(call)).ToArray();
        using var factory = new QueuedHttpClientFactory(Completion(RepeatablePlan), replies[0], replies[1],
            EarlyReply(protocol, """{"outcome":"insufficient_documentation","answerText":"The remaining options are undocumented.","claims":[]}"""));
        var options = WorkspaceOptions(protocol);
        options.NativeSourceTitleNavigationEnabled = true;
        options.SemanticCriticEnabled = false;
        options.NativeResearchWorkspaceEnabled = false;
        options.NativeResearchActiveProposalEnabled = false;
        options.ExternalMaximumCallsPerJob = 4;
        var initial = PreviousProjectionBody("INITIAL", "Locate the inspection in the procedure.", page: 1);
        var read = PreviousProjectionBody("READ", body, page: 77);
        var gateway = new SourceTitleNavigationGateway(initial, read, locator);
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null).ExecuteAsync(
            BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"), gateway, CancellationToken.None);

        Assert.Empty(result.Claims);
        Assert.Equal(new[] { "search_corpus", "list_source_titles", "read_source" },
            gateway.Requests.Select(request => request.Operation));
        Assert.Equal(4, factory.Requests.Count);
        var wire = factory.Requests[2].Body;
        var locatorOutput = Assert.Single(NativeCorrelationOutputs(wire, protocol)).Body;
        Assert.True(locatorOutput.GetProperty("navigationOnly").GetBoolean());
        Assert.Equal(key, locatorOutput.GetProperty("sourceKey").GetString());
        Assert.Equal("executed", locatorOutput.GetProperty("status").GetString());
        var row = locatorOutput.GetProperty("page").GetProperty("items")[0];
        Assert.Equal(locator, row.GetProperty("title").GetString());
        Assert.Equal(77, row.GetProperty("physicalPageStart").GetInt32());
        Assert.DoesNotContain("evidenceId", locatorOutput.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("revisionId", locatorOutput.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var beforeRead = WorkspaceUser(wire).GetProperty("evidence");
        Assert.Single(beforeRead.EnumerateArray());
        Assert.DoesNotContain(locator, beforeRead.GetRawText());
        Assert.DoesNotContain(body, beforeRead.GetRawText());
        var afterRead = WorkspaceUser(factory.Requests[3].Body).GetProperty("evidence");
        Assert.Contains(body, afterRead.GetRawText());
    }

    private sealed class SourceTitleNavigationGateway(AdvancedAnalysisResolvedEvidence initial,
        AdvancedAnalysisResolvedEvidence read, string locator) : IAdvancedAnalysisToolGateway
    {
        private readonly List<AdvancedAnalysisResolvedEvidence> _evidence = [];
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => _evidence;
        public List<AdvancedAnalysisSearchRequest> Requests { get; } = [];

        public Task<AdvancedAnalysisSearchObservation> SearchAsync(AdvancedAnalysisSearchRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Operation == "list_source_titles")
                return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, [], [], 0, Requests.Count,
                    SourceTitlePage: new(0, null, [new(locator, "title_anchor", null, 77, 77)], false)));
            var evidence = request.Operation == "read_source" ? read : initial;
            _evidence.Add(evidence);
            return Task.FromResult(new AdvancedAnalysisSearchObservation(request.Query, [evidence], [], 0, Requests.Count));
        }
    }

}
