using System.Net;
using System.Reflection;
using System.Text;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class SourceBackedNamedDocumentCatalogConsensusTests
{
    private const string RequestedName = "Service Bulletin HX-42.pdf";
    private const string TargetDocId =
        "11111111-1111-1111-1111-111111111111";
    private const string TargetPath =
        "Operations/Service Bulletin HX-42.pdf";
    private const string TargetRevisionId =
        "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string TargetSourceHash =
        "909173f3549f731efe5bd668747edf9f12963f793233a705a63b83669a818fcf";

    [Fact]
    public async Task V2_empty_but_unified_exact_is_inconclusive_not_not_found()
    {
        var (result, requests) = await ResolveAsync(
            v2ContainsTarget: false,
            unifiedContainsTarget: true);

        Assert.Equal(
            SourceBackedDocumentResolutionStatus.Inconclusive,
            result.Status);
        Assert.False(result.CatalogObservationComplete);
        Assert.Equal("catalog_observations_disagree", result.ReasonCode);
        Assert.Contains(requests, IsUnifiedDocumentsRequest);
    }

    [Fact]
    public async Task V2_exact_but_unified_empty_is_inconclusive_not_resolved()
    {
        var (result, requests) = await ResolveAsync(
            v2ContainsTarget: true,
            unifiedContainsTarget: false);

        Assert.Equal(
            SourceBackedDocumentResolutionStatus.Inconclusive,
            result.Status);
        Assert.False(result.CatalogObservationComplete);
        Assert.Equal("catalog_observations_disagree", result.ReasonCode);
        Assert.Contains(requests, IsUnifiedDocumentsRequest);
    }

    [Fact]
    public async Task Matching_exact_observations_resolve_the_same_identity()
    {
        var (result, requests) = await ResolveAsync(
            v2ContainsTarget: true,
            unifiedContainsTarget: true);

        Assert.Equal(
            SourceBackedDocumentResolutionStatus.Resolved,
            result.Status);
        Assert.True(result.CatalogObservationComplete);
        Assert.Equal(TargetDocId, Assert.Single(result.Candidates).DocId);
        Assert.Contains(requests, IsV2DocumentsRequest);
        Assert.Contains(requests, IsUnifiedDocumentsRequest);
    }

    [Fact]
    public async Task Matching_empty_observations_are_required_for_not_found()
    {
        var (result, requests) = await ResolveAsync(
            v2ContainsTarget: false,
            unifiedContainsTarget: false);

        Assert.Equal(
            SourceBackedDocumentResolutionStatus.NotFound,
            result.Status);
        Assert.True(result.CatalogObservationComplete);
        Assert.Contains(requests, IsV2DocumentsRequest);
        Assert.Contains(requests, IsUnifiedDocumentsRequest);
    }

    private static async Task<(
        SourceBackedDocumentResolutionObservation Result,
        IReadOnlyList<string> Requests)> ResolveAsync(
        bool v2ContainsTarget,
        bool unifiedContainsTarget)
    {
        var requests = new List<string>();
        var handler = new StubHttpHandler(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            return request.RequestUri.AbsolutePath switch
            {
                "/catalog/documents" => JsonResponse(
                    V2Payload(v2ContainsTarget)),
                "/documents" => JsonResponse(
                    UnifiedPayload(unifiedContainsTarget)),
                "/documents/context" => JsonResponse(ContextPayload()),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var api = CreateApiClient(handler);
        var resolver = new SourceBackedNamedDocumentResolver(
            new ApiClientSourceBackedNamedDocumentCatalogClient(api));

        var result = await resolver.ResolveAsync(
            RequestedName,
            CancellationToken.None);
        return (result, requests);
    }

    private static string V2Payload(bool containsTarget)
        => containsTarget
            ? $$"""
                {
                  "value": [
                    {
                      "docId": "{{TargetDocId}}",
                      "docPath": "{{TargetPath}}",
                      "canonicalName": "{{RequestedName}}",
                      "categoryPath": "Operations",
                      "sourceHash": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                      "status": "indexed"
                    }
                  ],
                  "nextLink": null
                }
                """
            : """
                {
                  "value": [],
                  "nextLink": null
                }
                """;

    private static string UnifiedPayload(bool containsTarget)
        => containsTarget
            ? $$"""
                {
                  "items": [
                    {
                      "docId": "{{TargetDocId}}",
                      "docPath": "{{TargetPath}}",
                      "docName": "{{RequestedName}}",
                      "category": "Operations",
                      "status": "indexed"
                    }
                  ],
                  "limit": 100,
                  "offset": 0
                }
                """
            : """
                {
                  "items": [],
                  "limit": 100,
                  "offset": 0
                }
                """;

    private static string ContextPayload()
        => $$"""
            {
              "found": true,
              "document": {
                "docId": "{{TargetDocId}}",
                "docPath": "{{TargetPath}}",
                "docName": "{{RequestedName}}",
                "revisionId": "{{TargetRevisionId}}",
                "sourceHash": "{{TargetSourceHash}}"
              },
              "items": []
            }
            """;

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                json,
                Encoding.UTF8,
                "application/json")
        };

    private static ApiClient CreateApiClient(HttpMessageHandler handler)
    {
        var api = new ApiClient();
        api.Configure(
            "http://localhost:5122",
            "test-api-key",
            "test-user");
        var field = typeof(ApiClient).GetField(
            "_http",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(
            api,
            new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost:5122")
            });
        return api;
    }

    private static bool IsV2DocumentsRequest(string request)
        => request.StartsWith(
            "/catalog/documents?",
            StringComparison.Ordinal);

    private static bool IsUnifiedDocumentsRequest(string request)
        => request.StartsWith(
            "/documents?",
            StringComparison.Ordinal);

    private sealed class StubHttpHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
