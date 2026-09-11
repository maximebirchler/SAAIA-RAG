using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed partial class SourceBackedAgentV2Tests
{
    private const string ConsensusDocId =
        "11111111-1111-1111-1111-111111111111";
    private const string ConsensusDocName =
        "Service Bulletin HX-42.pdf";
    private const string ConsensusDocPath =
        "Operations/Service Bulletin HX-42.pdf";
    private const string ConsensusRevisionId =
        "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string ConsensusSourceHash =
        "909173f3549f731efe5bd668747edf9f12963f793233a705a63b83669a818fcf";

    [Fact]
    public async Task Catalog_disagreement_then_agreement_retries_once_and_scopes_original_action()
    {
        var (resolver, requests) = RealConsensusResolver(
            recoverAfterFirstUnifiedObservation: true);
        var retry = Completion(Call(
            "catalog-decision",
            "retry_named_document_catalog",
            new { }));
        var executor = new ScriptedToolExecutor(SearchResult());
        var runner = new SourceBackedAgentV2Runner(
            new ScriptedAgentLlm(retry),
            executor,
            NamedDocumentRunnerOptions(),
            namedDocumentResolver: resolver);

        await runner.RunAsync(
            NamedDocumentIntake(
                ConsensusDocName,
                JsonSerializer.SerializeToElement(new
                {
                    query = "HX-42 inspection interval",
                    topK = 4
                })),
            CancellationToken.None);

        Assert.True(requests.Count(IsUnifiedRequest) >= 3);
        Assert.Equal("rag.search", Assert.Single(executor.ToolNames));
        var arguments = Assert.Single(executor.Arguments);
        Assert.Equal(ConsensusDocId,
            arguments.GetProperty("docId").GetString());
        Assert.Equal("HX-42 inspection interval",
            arguments.GetProperty("query").GetString());
        Assert.Equal(4, arguments.GetProperty("topK").GetInt32());
    }

    [Fact]
    public async Task Persistent_catalog_disagreement_executes_no_content_action()
    {
        var (resolver, requests) = RealConsensusResolver(
            recoverAfterFirstUnifiedObservation: false);
        var retry = Completion(Call(
            "catalog-retry",
            "retry_named_document_catalog",
            new { }));
        var stop = Completion(Call(
            "catalog-stop",
            "declare_named_document_insufficiency",
            new
            {
                reason =
                    "The two current catalog observations still disagree after the single permitted retry."
            }));
        var executor = new ScriptedToolExecutor(SearchResult());
        var runner = new SourceBackedAgentV2Runner(
            new ScriptedAgentLlm(retry, stop),
            executor,
            NamedDocumentRunnerOptions(),
            namedDocumentResolver: resolver);

        var result = await runner.RunAsync(
            NamedDocumentIntake(
                ConsensusDocName,
                JsonSerializer.SerializeToElement(new
                {
                    query = "HX-42 inspection interval",
                    topK = 4
                })),
            CancellationToken.None);

        Assert.Equal(2, requests.Count(IsUnifiedRequest));
        Assert.Empty(executor.ToolNames);
        Assert.Empty(result.EvidenceBundle.Items);
        Assert.Equal(
            SourceBackedDocumentResolutionStatus.Inconclusive,
            result.Intake.RequestedDocumentResolution?.Status);
    }

    private static (
        ISourceBackedNamedDocumentResolver Resolver,
        IReadOnlyList<string> Requests) RealConsensusResolver(
            bool recoverAfterFirstUnifiedObservation)
    {
        var requests = new List<string>();
        var unifiedObservations = 0;
        var handler = new ConsensusStubHttpHandler(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            if (request.RequestUri.AbsolutePath == "/catalog/documents")
                return ConsensusJsonResponse(ConsensusV2Payload());
            if (request.RequestUri.AbsolutePath == "/documents")
            {
                unifiedObservations++;
                var containsTarget = recoverAfterFirstUnifiedObservation
                    && unifiedObservations > 1;
                return ConsensusJsonResponse(
                    ConsensusUnifiedPayload(containsTarget));
            }
            if (request.RequestUri.AbsolutePath == "/documents/context")
                return ConsensusJsonResponse(ConsensusContextPayload());

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var api = new ApiClient();
        api.Configure(
            "http://localhost:5122",
            "test-api-key",
            "test-user");
        var field = typeof(ApiClient).GetField(
            "_http",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(api, new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5122")
        });
        return (
            new SourceBackedNamedDocumentResolver(
                new ApiClientSourceBackedNamedDocumentCatalogClient(api)),
            requests);
    }

    private static bool IsUnifiedRequest(string request)
        => request.StartsWith("/documents?", StringComparison.Ordinal);

    private static string ConsensusV2Payload()
        => $$"""
            {
              "value": [
                {
                  "docId": "{{ConsensusDocId}}",
                  "docPath": "{{ConsensusDocPath}}",
                  "canonicalName": "{{ConsensusDocName}}",
                  "sourceHash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "status": "indexed"
                }
              ],
              "nextLink": null
            }
            """;

    private static string ConsensusUnifiedPayload(bool containsTarget)
        => containsTarget
            ? $$"""
                {
                  "items": [
                    {
                      "docId": "{{ConsensusDocId}}",
                      "docPath": "{{ConsensusDocPath}}",
                      "docName": "{{ConsensusDocName}}",
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

    private static string ConsensusContextPayload()
        => $$"""
            {
              "found": true,
              "document": {
                "docId": "{{ConsensusDocId}}",
                "docPath": "{{ConsensusDocPath}}",
                "docName": "{{ConsensusDocName}}",
                "revisionId": "{{ConsensusRevisionId}}",
                "sourceHash": "{{ConsensusSourceHash}}"
              },
              "items": []
            }
            """;

    private static HttpResponseMessage ConsensusJsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                json,
                Encoding.UTF8,
                "application/json")
        };

    private sealed class ConsensusStubHttpHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
