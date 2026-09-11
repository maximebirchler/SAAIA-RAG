using System.Text.Json;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class CanonicalBackendEvidenceContractTests
{
    // These responses are exported by the real PostgreSQL integration scenario
    // named in the fixture, after its source identity and isolation assertions pass.
    private static JsonElement ReadResponse(int index)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "CanonicalBackendEvidenceContract.json")));
        Assert.True(fixture.RootElement.GetProperty("syntheticCorpus").GetBoolean());
        Assert.True(fixture.RootElement.GetProperty("postgresVerified").GetBoolean());
        var entry = fixture.RootElement.GetProperty("responses")[index];
        Assert.True(entry.GetProperty("request").GetProperty("SourceBackedCanonical").GetBoolean());
        return entry.GetProperty("response").Clone();
    }

    private static JsonElement Normalize(JsonElement response)
        => ToolAgentOrchestrator.NormalizeRagHitsForTests(response.GetRawText(), sourceBackedCanonical: true);

    private static EvidenceBundle BuildBundle(JsonElement normalized)
    {
        var results = new ToolResults();
        results.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = normalized });
        return EvidenceBundleBuilder.FromToolResults(results, "Which verification records apply to the equipment?");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Canonical_backend_source_identity_survives_normalization_and_bundle_creation(int responseIndex)
    {
        var response = ReadResponse(responseIndex);
        var sourceItems = response.GetProperty("items").EnumerateArray().ToDictionary(
            item => item.GetProperty("chunkId").GetString()!, item => item);
        Assert.NotEmpty(sourceItems);
        var normalized = Normalize(response);
        Assert.Equal(sourceItems.Count, normalized.GetProperty("hits").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, normalized.GetProperty("guidance").ValueKind);
        var bundle = BuildBundle(normalized);
        Assert.Equal(sourceItems.Count, bundle.Items.Count);
        Assert.Equal(bundle.Items.Count, bundle.ById.Count);
        Assert.All(bundle.Items, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.EvidenceId));
            Assert.True(sourceItems.TryGetValue(item.ChunkId!, out var source));
            Assert.Equal(source.GetProperty("docId").GetString(), item.DocId);
            Assert.Equal(source.GetProperty("docPath").GetString(), item.DocPath);
            Assert.Equal(source.GetProperty("sourceHash").GetString(), item.SourceHash);
            Assert.Equal(source.GetProperty("revisionId").GetString(), item.RevisionId);
            Assert.Equal(source.GetProperty("pageStart").GetInt32(), item.PageStart);
            Assert.Equal(source.GetProperty("pageEnd").GetInt32(), item.PageEnd);
            Assert.Equal(source.GetProperty("text").GetString(), item.Excerpt);
            Assert.Contains("tool:rag.search", item.Lineage);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Canonical_normalization_does_not_introduce_semantic_selection_hints(int responseIndex)
    {
        var response = ReadResponse(responseIndex);
        Assert.All(response.GetProperty("items").EnumerateArray(), item =>
            Assert.Equal(JsonValueKind.Null, item.GetProperty("selectionHints").ValueKind));
        var normalized = Normalize(response);
        Assert.All(normalized.GetProperty("hits").EnumerateArray(), hit =>
            Assert.Equal(JsonValueKind.Null, hit.GetProperty("selectionHints").ValueKind));
        var bundle = BuildBundle(normalized);
        Assert.All(bundle.Items, item =>
        {
            foreach (var key in new[] { "evidenceRole", "actionabilityScore", "supportScore", "fragmentScore", "qualityPenalty" })
                Assert.False(item.SelectionHints.ContainsKey(key), $"Client introduced semantic hint '{key}'.");
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Canonical_retrieval_query_is_preserved_for_evidence_and_empty_search_memory(int responseIndex)
    {
        var response = ReadResponse(responseIndex);
        var query = response.GetProperty("query").GetString();
        Assert.False(string.IsNullOrWhiteSpace(query));
        var normalized = Normalize(response);
        var bundle = BuildBundle(normalized);
        var attempt = Assert.Single(bundle.RetrievalAttempts);
        Assert.Equal([query], attempt.Queries);
        Assert.All(bundle.Items, item => Assert.Equal(query, item.QueryUsed));
        if (responseIndex == 3)
            Assert.Empty(bundle.Items);
    }
}
