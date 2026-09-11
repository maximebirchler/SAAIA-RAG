using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class DocumentNavigationContractTests
{
    [Fact]
    public async Task Api_client_forwards_the_typed_navigation_filter()
    {
        var handler = new NavigationHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/documents/navigation", request.RequestUri!.AbsolutePath);
            var query = request.RequestUri.Query;
            Assert.Contains("path=Cuisine", query, StringComparison.Ordinal);
            Assert.Contains("q=recette", query, StringComparison.Ordinal);
            Assert.Contains("kind=navigation_entry", query, StringComparison.Ordinal);
            Assert.Contains("limit=80", query, StringComparison.Ordinal);
            Assert.Contains("offset=20", query, StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"items\":[]}",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var api = CreateApiClient(handler);

        var result = await api.DocumentsNavigationAsync(
            "Cuisine",
            null,
            null,
            null,
            "recette",
            "navigation_entry",
            80,
            20,
            CancellationToken.None);

        Assert.Equal(0, result.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public void Runtime_tool_exposes_only_the_supported_navigation_kinds()
    {
        var navigation = SourceBackedAgentToolCatalog.Build()
            .Single(static tool => tool.Name == "documents_navigation");
        var kindValues = navigation.Parameters
            .GetProperty("properties")
            .GetProperty("kind")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(static item => item.GetString())
            .ToArray();

        Assert.Equal(
            new[] { "navigation_entry", "title_anchor" },
            kindValues);
        Assert.False(
            navigation.Parameters.TryGetProperty("required", out var required)
            && required.EnumerateArray().Any(
                static item => item.GetString() == "kind"));
    }

    [Fact]
    public void Resolved_navigation_pointer_reaches_the_bundle_but_stays_orientation_only()
    {
        using var payload = JsonDocument.Parse(
            """
            {
              "navigationOnly": true,
              "query": "procedure",
              "items": [
                {
                  "docId": "document-1",
                  "revisionId": "revision-7",
                  "sourceHash": "hash-7",
                  "docName": "manual.pdf",
                  "docPath": "Manuals/manual.pdf",
                  "categoryPath": "Manuals",
                  "kind": "navigation_entry",
                  "label": "Calibration procedure",
                  "targetPageStart": 17,
                  "targetPageEnd": 18,
                  "resolutionMethod": "target_chunk",
                  "navigationEntryId": "navigation-3",
                  "targetChunkId": "chunk-17",
                  "targetAnchorId": "anchor-17",
                  "hasTargetChunk": true,
                  "hasTargetAnchor": true,
                  "confidence": 0.97
                }
              ]
            }
            """);
        var results = new ToolResults();
        results.Items.Add(new ToolResults.Item
        {
            ToolName = "documents.navigation",
            Result = payload.RootElement.Clone()
        });

        var bundle = EvidenceBundleBuilder.FromToolResults(
            results,
            "Locate the calibration procedure.");

        var item = Assert.Single(bundle.Items);
        Assert.Equal("revision-7", item.RevisionId);
        Assert.Equal("hash-7", item.SourceHash);
        Assert.Equal("chunk-17", item.ChunkId);
        Assert.Equal(17, item.PageStart);
        Assert.Equal("anchor-17", item.SelectionHints["targetAnchorId"]);
        Assert.Contains("orientation_only", item.RiskFlags);

        using var compact = JsonDocument.Parse(
            SourceBackedAgentObservationCompactor.Build(
                "documents_navigation",
                results.Items,
                bundle,
                1,
                new SourceBackedAgentV2Options(
                    MaximumTurns: 4,
                    MaximumToolCalls: 8,
                    MaximumObservationItems: 4,
                    MaximumObservationExcerptCharacters: 120,
                    MaximumOutputTokens: 256)));
        var observation = Assert.Single(
            compact.RootElement
                .GetProperty("evidence")
                .EnumerateArray()
                .ToArray());
        Assert.True(observation.GetProperty("orientationOnly").GetBoolean());
        Assert.Equal("chunk-17", observation.GetProperty("chunkId").GetString());
        Assert.Equal("revision-7", observation.GetProperty("revisionId").GetString());
        Assert.Equal("anchor-17", observation.GetProperty("targetAnchorId").GetString());
    }

    private static ApiClient CreateApiClient(HttpMessageHandler handler)
    {
        var api = new ApiClient();
        api.Configure("http://localhost:5122", "test-api-key", "test-user", null);
        var field = typeof(ApiClient).GetField(
            "_http",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(api, new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5122")
        });
        return api;
    }

    private sealed class NavigationHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
