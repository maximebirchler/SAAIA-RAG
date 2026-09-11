using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class CanonicalPayloadFidelityTests
{
    private static JsonElement ReadResponse(int index)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "CanonicalContextualPayloadContract.json")));
        Assert.True(fixture.RootElement.GetProperty("syntheticCorpus").GetBoolean());
        Assert.True(fixture.RootElement.GetProperty("postgresVerified").GetBoolean());
        return fixture.RootElement.GetProperty("responses")[index].GetProperty("response").Clone();
    }

    private static EvidenceBundle NormalizeAndBuild(JsonElement response)
    {
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(response.GetRawText(), sourceBackedCanonical: true);
        var tools = new ToolResults();
        tools.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized });
        return EvidenceBundleBuilder.FromToolResults(tools, "Which verification applies?");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Production_contextual_projection_does_not_become_text_of_the_cited_chunk(int responseIndex)
    {
        var response = ReadResponse(responseIndex);
        var originals = response.GetProperty("items").EnumerateArray().ToDictionary(
            item => item.GetProperty("chunkId").GetString()!, item => item.GetProperty("text").GetString());
        Assert.All(response.GetProperty("items").EnumerateArray(), item =>
            Assert.Contains("source_metadata:", item.GetProperty("contextualSnippet").GetString()));
        var bundle = NormalizeAndBuild(response);
        Assert.Equal(originals.Count, bundle.Items.Count);
        Assert.All(bundle.Items, item =>
        {
            Assert.Equal(originals[item.ChunkId!], item.Excerpt);
            Assert.DoesNotContain("source_metadata:", item.Excerpt);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Backend_metrics_degradation_remains_visible_in_retrieval_attempts(int responseIndex)
    {
        // Only the declared failure metadata is mutated; item text and identity
        // remain the actual PostgreSQL response. This is not a live timeout test.
        var response = JsonNode.Parse(ReadResponse(responseIndex).GetRawText())!;
        response["metrics"]!["degradedRetrievers"] = new JsonArray("dense");
        response["metrics"]!["degradedRetrieverErrors"] = new JsonObject { ["dense"] = "TimeoutException" };
        var bundle = NormalizeAndBuild(JsonSerializer.SerializeToElement(response));
        var attempt = Assert.Single(bundle.RetrievalAttempts);
        Assert.Equal("degraded", attempt.Outcome);
        Assert.Equal(new[] { "dense" }, attempt.DegradedRetrievers);
        Assert.Equal(new[] { response["query"]!.GetValue<string>() }, attempt.Queries);
    }

    [Fact]
    public void Longer_retrieval_context_is_not_promoted_into_the_cited_chunk_text()
    {
        // A contract mutation represents an embedding excerpt spanning a wider
        // source unit. Its text has no authority to replace this chunk's text.
        var response = JsonNode.Parse(ReadResponse(0).GetRawText())!;
        var hit = response["items"]![0]!;
        var originalText = hit["text"]!.GetValue<string>();
        hit["contextualSnippet"] = "Excerpt:\n" + originalText
            + " A separate recorded measurement belongs to the neighboring indexed source unit.";
        var bundle = NormalizeAndBuild(JsonSerializer.SerializeToElement(response));
        var item = Assert.Single(bundle.Items, item => item.ChunkId == hit["chunkId"]!.GetValue<string>());
        Assert.Equal(originalText, item.Excerpt);
    }

    [Fact]
    public void Healthy_empty_search_remains_completed_without_invented_degradation()
    {
        var bundle = NormalizeAndBuild(ReadResponse(3));
        var attempt = Assert.Single(bundle.RetrievalAttempts);
        Assert.Empty(bundle.Items);
        Assert.Equal("completed", attempt.Outcome);
        Assert.Empty(attempt.DegradedRetrievers);
    }
}
