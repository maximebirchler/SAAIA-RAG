using System.Text.Json;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData("chat-completions", 4, true, true)]
    [InlineData("responses", 4, true, true)]
    [InlineData("responses", 3, true, false)]
    [InlineData("responses", 4, false, false)]
    public async Task Source_title_guidance_reports_successful_lexical_streak_only_when_navigation_is_offered(
        string protocol, int searchCount, bool navigationEnabled, bool expected)
    {
        using var factory = new QueuedHttpClientFactory(Completion(GuidancePlan(searchCount)),
            EarlyReply(protocol, GuidanceInsufficiency));
        var options = WorkspaceOptions(protocol);
        options.NativeSourceTitleNavigationEnabled = navigationEnabled;
        options.SemanticCriticEnabled = false;
        options.NativeResearchWorkspaceEnabled = false;
        var gateway = new RecordingToolGateway(PreviousProjectionBody("INITIAL", "Canonical component inspection.", page: 1));
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"),
                gateway, CancellationToken.None);

        Assert.Empty(result.Claims);
        Assert.Equal(searchCount, gateway.Searches.Count);
        var wire = factory.Requests[1].Body;
        using var envelope = JsonDocument.Parse(wire);
        var functions = envelope.RootElement.GetProperty("tools").EnumerateArray()
            .Select(tool => protocol == "responses" ? tool : tool.GetProperty("function"));
        Assert.Equal(navigationEnabled, functions.Any(tool => tool.GetProperty("name").GetString() == "list_source_titles"));
        var research = WorkspaceUser(wire).GetProperty("researchTools");
        Assert.Equal(expected, research.TryGetProperty("sourceTitleExploration", out var guidance));
        if (!expected) return;
        Assert.Equal("lexical_only_documentary_route", guidance.GetProperty("reasonCode").GetString());
        Assert.Equal(searchCount, guidance.GetProperty("consecutiveCorpusSearches").GetInt32());
        Assert.Equal(1, guidance.GetProperty("observedSourceCount").GetInt32());
        Assert.Equal(0, guidance.GetProperty("successfulTitleListings").GetInt32());
        Assert.False(guidance.GetProperty("documentaryEvidence").GetBoolean());
        Assert.Contains("list_source_titles", guidance.GetProperty("instruction").GetString());
    }

    [Fact]
    public async Task Source_title_guidance_stops_when_the_model_chooses_a_different_documentary_route()
    {
        const string protocol = "responses";
        var args = JsonSerializer.Serialize(new { sourceKey = "internal-source-1", pageStart = 3, pageEnd = 3, topK = 12 });
        using var factory = new QueuedHttpClientFactory(Completion(GuidancePlan(4)),
            BudgetNativeResponseFunctions([("read_source", args)]),
            EarlyReply(protocol, GuidanceInsufficiency), EarlyReply(protocol, GuidanceInsufficiency));
        var options = WorkspaceOptions(protocol);
        options.NativeSourceTitleNavigationEnabled = true;
        options.SemanticCriticEnabled = false;
        options.NativeResearchWorkspaceEnabled = false;
        var gateway = new RecordingToolGateway(PreviousProjectionBody("INITIAL", "Canonical component inspection.", page: 3));
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(20, "named_item", "distinct_structured_layout", "documented_option"),
                gateway, CancellationToken.None);

        Assert.Equal("read_source", gateway.Searches[^1].Operation);
        Assert.True(WorkspaceUser(factory.Requests[1].Body).GetProperty("researchTools").TryGetProperty("sourceTitleExploration", out _));
        Assert.False(WorkspaceUser(factory.Requests[2].Body).GetProperty("researchTools").TryGetProperty("sourceTitleExploration", out _));
    }

    private const string GuidanceInsufficiency =
        """{"outcome":"insufficient_documentation","answerText":"The remaining options are undocumented.","claims":[]}""";

    private static string GuidancePlan(int searchCount) => JsonSerializer.Serialize(new
    {
        selectionMode = "distinct_named_items",
        queries = Enumerable.Range(1, searchCount).Select(index => new { query = $"documented inspection option {index}", topK = 4 })
    });
}
