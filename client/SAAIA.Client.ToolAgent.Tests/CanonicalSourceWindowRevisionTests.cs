using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class CanonicalSourceWindowRevisionTests
{
    private static JsonElement ReadCase(int index)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "CanonicalContextRevisionContract.json")));
        Assert.True(fixture.RootElement.GetProperty("syntheticCorpus").GetBoolean());
        Assert.True(fixture.RootElement.GetProperty("postgresVerified").GetBoolean());
        return fixture.RootElement.GetProperty("cases")[index].Clone();
    }

    private static async Task<JsonElement> EnrichAsync(JsonElement entry, JsonElement? contextOverride = null)
    {
        var response = contextOverride ?? entry.GetProperty("context");
        var normalized = ToolAgentOrchestrator.NormalizeRagHitsForTests(
            entry.GetProperty("search").GetRawText(), sourceBackedCanonical: true);
        var original = normalized.GetProperty("hits")[0];
        using var handler = new ContextHandler(response.GetRawText(), original);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5122") };
        var api = new ApiClient();
        api.Configure("http://localhost:5122", "synthetic-test-key", "synthetic-user");
        typeof(ApiClient).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(api, http);
        var result = await ToolAgentOrchestrator.EnrichSourceBackedEvidenceWindowsForTestsAsync(api, normalized);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(normalized.GetProperty("query").GetString(), result.GetProperty("query").GetString());
        foreach (var property in new[] { "docId", "docPath", "revisionId", "sourceHash", "chunkId", "fullText" })
            Assert.Equal(original.GetProperty(property).GetString(), result.GetProperty("hits")[0].GetProperty(property).GetString());
        return result;
    }

    private static EvidenceBundle BuildBundle(JsonElement result)
    {
        var tools = new ToolResults();
        tools.Items.Add(new ToolResults.Item { ToolName = "rag.search", Result = result });
        return EvidenceBundleBuilder.FromToolResults(tools, "Which verification applies?");
    }

    private static void AssertRejected(JsonElement result)
    {
        var hit = Assert.Single(result.GetProperty("hits").EnumerateArray());
        Assert.False(hit.TryGetProperty("sourceWindow", out _), "Unverified neighboring text must not be attached to a canonical hit.");
        Assert.True(hit.TryGetProperty("sourceWindowError", out var error));
        Assert.False(string.IsNullOrWhiteSpace(error.GetString()));
        var evidence = Assert.Single(BuildBundle(result).Items);
        Assert.Equal(error.GetString(), evidence.CodeHints["source_window_error"]);
        Assert.DoesNotContain(evidence.Lineage, value => value.StartsWith("sourceWindowChunk:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Verified_context_preserves_source_identity_and_actual_neighbor_text(int caseIndex)
    {
        var entry = ReadCase(caseIndex);
        var result = await EnrichAsync(entry);
        var hit = result.GetProperty("hits")[0];
        Assert.False(hit.TryGetProperty("sourceWindowError", out _));
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(entry.GetProperty("context").GetProperty("items").GetRawText()),
            JsonNode.Parse(hit.GetProperty("sourceWindow").GetRawText())));
        var document = entry.GetProperty("context").GetProperty("document");
        Assert.All(BuildBundle(result).Items, item =>
        {
            Assert.Equal(document.GetProperty("docId").GetString(), item.DocId);
            Assert.Equal(document.GetProperty("revisionId").GetString(), item.RevisionId);
            Assert.Equal(document.GetProperty("sourceHash").GetString(), item.SourceHash);
            Assert.Contains(entry.GetProperty("context").GetProperty("items").EnumerateArray(), source =>
                source.GetProperty("chunkId").GetString() == item.ChunkId && source.GetProperty("text").GetString() == item.Excerpt);
        });
    }

    [Fact]
    public async Task Reextraction_between_search_and_context_does_not_relabel_new_text_with_old_revision()
    {
        var entry = ReadCase(1);
        var hit = entry.GetProperty("search").GetProperty("items")[0];
        var document = entry.GetProperty("context").GetProperty("document");
        Assert.Equal(hit.GetProperty("sourceHash").GetString(), document.GetProperty("sourceHash").GetString());
        Assert.NotEqual(hit.GetProperty("revisionId").GetString(), document.GetProperty("revisionId").GetString());
        AssertRejected(await EnrichAsync(entry));
    }

    [Theory]
    [InlineData("docId", false)]
    [InlineData("docPath", false)]
    [InlineData("revisionId", false)]
    [InlineData("sourceHash", false)]
    [InlineData("revisionId", true)]
    [InlineData("sourceHash", true)]
    public async Task Context_identity_must_be_present_and_match_the_parent(string field, bool missing)
    {
        var entry = ReadCase(0);
        var context = JsonNode.Parse(entry.GetProperty("context").GetRawText())!;
        if (missing)
            context["document"]!.AsObject().Remove(field);
        else
            context["document"]![field] = "different-identity";
        AssertRejected(await EnrichAsync(entry, JsonSerializer.SerializeToElement(context)));
    }

    [Theory]
    [InlineData("found")]
    [InlineData("anchorFound")]
    [InlineData("anchorItem")]
    public async Task Context_must_confirm_the_requested_anchor(string absent)
    {
        var entry = ReadCase(0);
        var context = JsonNode.Parse(entry.GetProperty("context").GetRawText())!;
        if (absent == "anchorItem")
            context["items"]![0]!["chunkId"] = Guid.NewGuid().ToString();
        else
            context[absent] = false;
        AssertRejected(await EnrichAsync(entry, JsonSerializer.SerializeToElement(context)));
    }

    private sealed class ContextHandler(string response, JsonElement hit) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Assert.Equal("/documents/context", request.RequestUri!.AbsolutePath);
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            Assert.Equal(hit.GetProperty("docId").GetString(), query["docId"]);
            Assert.Equal(hit.GetProperty("chunkId").GetString(), query["chunkId"]);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            });
        }
    }
}
