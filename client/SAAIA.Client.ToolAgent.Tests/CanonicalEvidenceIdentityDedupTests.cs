using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class CanonicalEvidenceIdentityDedupTests
{
    private static JsonObject ReadCanonicalHit()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "CanonicalBackendEvidenceContract.json")));
        var response = fixture.RootElement.GetProperty("responses")[0].GetProperty("response");
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(response.GetRawText(), sourceBackedCanonical: true);
        return JsonNode.Parse(normalized.GetProperty("hits")[0].GetRawText())!.AsObject();
    }

    private static ToolResults BuildResults(JsonObject first, JsonObject second)
    {
        var results = new ToolResults();
        foreach (var (hit, query) in new[] { (first, "first observed query"), (second, "second observed query") })
            results.Items.Add(new ToolResults.Item
            {
                ToolName = "rag.search",
                Result = JsonSerializer.SerializeToElement(new { query, hits = new[] { hit } })
            });
        return results;
    }

    [Theory]
    [InlineData("docId")]
    [InlineData("revisionId")]
    [InlineData("sourceHash")]
    [InlineData("chunkId")]
    public void Distinct_canonical_identities_are_not_merged_by_identical_path_page_and_text(string changedField)
    {
        var first = ReadCanonicalHit();
        var second = first.DeepClone().AsObject();
        second[changedField] = changedField == "sourceHash" ? new string('f', 64) : Guid.NewGuid().ToString();
        var bundle = EvidenceBundleBuilder.FromToolResults(BuildResults(first, second), "Which source applies?");
        Assert.Equal(2, bundle.Items.Count);
        Assert.Equal(2, bundle.ById.Count);
        Assert.Equal(bundle.Items[0].VisibleSourceKey, bundle.Items[1].VisibleSourceKey);
        Assert.Equal(bundle.Items[0].Excerpt, bundle.Items[1].Excerpt);
    }

    [Fact]
    public void Repeated_identical_canonical_evidence_remains_single_and_keeps_both_tool_observations()
    {
        var first = ReadCanonicalHit();
        var bundle = EvidenceBundleBuilder.FromToolResults(
            BuildResults(first, first.DeepClone().AsObject()), "Which source applies?");
        var item = Assert.Single(bundle.Items);
        Assert.Equal(first["chunkId"]!.GetValue<string>(), item.ChunkId);
        Assert.Equal(first["revisionId"]!.GetValue<string>(), item.RevisionId);
        Assert.Equal(first["fullText"]!.GetValue<string>(), item.Excerpt);
        Assert.Equal(2, bundle.RetrievalAttempts.Count);
        Assert.Equal(new[] { "first observed query", "second observed query" }, bundle.RetrievalAttempts.SelectMany(attempt => attempt.Queries));
        Assert.Contains("toolSequence:1", item.Lineage);
        Assert.Contains("toolSequence:2", item.Lineage);
    }
}
