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

public sealed class CanonicalToolExecutionContractTests
{
    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 1)]
    [InlineData(true, false, 2)]
    [InlineData(true, true, 2)]
    public async Task Canonical_tool_routes_keep_query_identity_and_revision_checks_through_the_actual_api_client(
        bool multiSearch, bool reextracted, int queryCount)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "CanonicalContextRevisionContract.json")));
        var entry = fixture.RootElement.GetProperty("cases")[reextracted ? 1 : 0];
        var source = entry.GetProperty("search").GetProperty("items")[0];
        var query = entry.GetProperty("search").GetProperty("query").GetString()!;
        var queries = queryCount == 1 ? new[] { query } : new[] { query, "instrument verification" };
        using var handler = new FixtureHandler(entry, source, queries);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5122") };
        var api = new ApiClient();
        api.Configure("http://localhost:5122", "synthetic-test-key", "synthetic-user");
        typeof(ApiClient).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(api, http);
        var sut = new ToolAgentOrchestrator(api, llm: null!, mem: new ToolMemory());
        var args = JsonSerializer.SerializeToElement(new
        {
            query, queries, topK = 20,
            docId = source.GetProperty("docId").GetString(),
            sourceBackedCanonical = true,
            disableAutomaticCategoryScoping = true
        });
        var method = typeof(ToolAgentOrchestrator).GetMethod(
            multiSearch ? "ExecRagMultiSearchAsync" : "ExecRagSearchAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = await (Task<JsonElement>)method!.Invoke(sut, [args, CancellationToken.None])!;
        Assert.Equal(queryCount, handler.SearchRequests);
        Assert.Equal(1, handler.ContextRequests);
        var normalizedHit = Assert.Single(result.GetProperty("hits").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, normalizedHit.GetProperty("selectionHints").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("guidance").ValueKind);

        var tools = new ToolResults();
        tools.Items.Add(new ToolResults.Item { ToolName = multiSearch ? "rag.multi_search" : "rag.search", Result = result });
        var bundle = EvidenceBundleBuilder.FromToolResults(tools, "Which measurement applies?");
        var evidence = Assert.Single(bundle.Items);
        Assert.Equal(source.GetProperty("docId").GetString(), evidence.DocId);
        Assert.Equal(source.GetProperty("revisionId").GetString(), evidence.RevisionId);
        Assert.Equal(source.GetProperty("sourceHash").GetString(), evidence.SourceHash);
        Assert.Equal(source.GetProperty("chunkId").GetString(), evidence.ChunkId);
        Assert.Equal(source.GetProperty("text").GetString(), evidence.Excerpt);
        Assert.Equal(query, evidence.QueryUsed);
        Assert.Equal(queries, Assert.Single(bundle.RetrievalAttempts).Queries);
        if (multiSearch)
            Assert.Equal(queries, normalizedHit.GetProperty("retrievalQueryContributions").EnumerateArray()
                .Select(item => item.GetProperty("query").GetString()));
        if (reextracted)
        {
            Assert.False(normalizedHit.TryGetProperty("sourceWindow", out _));
            Assert.Equal("source_window_identity_mismatch", evidence.CodeHints["source_window_error"]);
        }
        else
        {
            Assert.Single(normalizedHit.GetProperty("sourceWindow").EnumerateArray());
            Assert.False(evidence.CodeHints.ContainsKey("source_window_error"));
        }
    }

    private sealed class FixtureHandler(JsonElement entry, JsonElement source, string[] queries) : HttpMessageHandler
    {
        public int SearchRequests { get; private set; }
        public int ContextRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            JsonElement response;
            if (request.RequestUri!.AbsolutePath == "/rag/search")
            {
                SearchRequests++;
                Assert.Equal(HttpMethod.Post, request.Method);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var executedQuery = body.RootElement.GetProperty("query").GetString();
                Assert.Contains(executedQuery, queries);
                Assert.Equal(source.GetProperty("docId").GetString(), body.RootElement.GetProperty("docId").GetString());
                Assert.True(body.RootElement.GetProperty("sourceBackedCanonical").GetBoolean());
                Assert.True(body.RootElement.GetProperty("includeContextualSnippet").GetBoolean());
                // The second query reuses the same captured source response;
                // only the echoed request text changes for this transport contract.
                var search = JsonNode.Parse(entry.GetProperty("search").GetRawText())!;
                search["query"] = executedQuery;
                response = JsonSerializer.SerializeToElement(search);
            }
            else
            {
                ContextRequests++;
                Assert.Equal("/documents/context", request.RequestUri.AbsolutePath);
                Assert.Equal(HttpMethod.Get, request.Method);
                var scope = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
                Assert.Equal(source.GetProperty("docId").GetString(), scope["docId"]);
                Assert.Equal(source.GetProperty("chunkId").GetString(), scope["chunkId"]);
                response = entry.GetProperty("context");
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response.GetRawText(), Encoding.UTF8, "application/json")
            };
        }
    }
}
