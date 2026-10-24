using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Fact]
    public async Task Structured_discovery_diversity_does_not_clip_deliberately_scoped_searches()
    {
        var evidence = BuildEvidence("E1", "Table of contents Item A 3 Item B 8 Item C 12 Item D 18");
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"selectionMode":"distinct_named_items","queries":[{"query":"overview","topK":40}]}"""),
            Completion("""{"decision":"search_more","queries":[{"query":"scoped item","sourceKey":"internal-source-1","topK":40},{"query":"hinted item","documentHint":"Observed manual","topK":40}]}"""),
            Completion(CandidateTerminal));
        var options = CreateOptions();
        options.AdaptiveResearchEnabled = true;
        options.ExternalMaximumCallsPerJob = 3;
        var gateway = new RecordingToolGateway(evidence);
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null)
            .ExecuteAsync(BuildRequest(answerUnitCount: 20, atomicEvidenceMode: "named_item",
                selectionPolicy: "distinct_structured_layout"), gateway, CancellationToken.None);
        Assert.Equal(3, gateway.Searches.Count);
        Assert.Equal(8, gateway.Searches[0].MaxPerDocument);
        Assert.Equal(2, gateway.Searches[0].MaxPerPage);
        foreach (var scoped in gateway.Searches.Skip(1))
        {
            Assert.Equal(40, scoped.TopK);
            Assert.Null(scoped.MaxPerDocument);
            Assert.Null(scoped.MaxPerPage);
        }
        Assert.Equal(evidence.Reference.DocId, gateway.Searches[1].DocId);
        Assert.Equal("Observed manual", gateway.Searches[2].DocumentHint);
    }

    [Fact]
    public async Task Section_locators_reach_the_model_without_becoming_body_or_exact_title()
    {
        var evidence = BuildEvidence("E1", "Hold below 17 bar.") with
        {
            SourceStructure = new("Stage two", "Isolation procedure > Stage two", "previous", "next")
        };
        using var factory = new QueuedHttpClientFactory(
            Completion("""{"queries":[{"query":"overview","topK":12}]}"""), Completion(NativeAnswered));
        await new OpenAiCompatibleAdvancedAnalysisProvider(factory, WorkspaceOptions(), null)
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(evidence), CancellationToken.None);
        var visible = Assert.Single(WorkspaceUser(factory.Requests[1].Body).GetProperty("evidence").EnumerateArray());
        Assert.Equal("Isolation procedure > Stage two", visible.GetProperty("sourceStructure").GetProperty("headingPath").GetString());
        Assert.Equal("Hold below 17 bar.", visible.GetProperty("content").GetString());
        Assert.True(!visible.TryGetProperty("candidateTitle", out var title)
                    || title.ValueKind == System.Text.Json.JsonValueKind.Null);
        Assert.True(visible.GetProperty("sourceStructure").GetProperty("hasPreviousChunk").GetBoolean());
        Assert.DoesNotContain("previous", visible.GetRawText(), StringComparison.Ordinal);
    }
}
