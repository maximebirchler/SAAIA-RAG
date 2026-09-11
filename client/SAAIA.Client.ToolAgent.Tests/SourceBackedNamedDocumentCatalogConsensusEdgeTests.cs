using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class SourceBackedNamedDocumentCatalogConsensusEdgeTests
{
    private const string RequestedName = "Service Bulletin HX-42.pdf";
    private const string TargetDocId =
        "11111111-1111-1111-1111-111111111111";
    private const string SecondDocId =
        "22222222-2222-2222-2222-222222222222";
    private const string ThirdDocId =
        "33333333-3333-3333-3333-333333333333";
    private const string SourceHash =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string CanonicalRevisionId =
        "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string CanonicalSourceHash =
        "909173f3549f731efe5bd668747edf9f12963f793233a705a63b83669a818fcf";

    private static readonly CatalogDoc Target = new(
        TargetDocId,
        "Operations/Service Bulletin HX-42.pdf",
        RequestedName,
        SourceHash);

    private static readonly CatalogDoc Second = new(
        SecondDocId,
        "Archive/Service Bulletin HX-42.pdf",
        RequestedName,
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc");

    private static readonly CatalogDoc Third = new(
        ThirdDocId,
        "Legacy/Service Bulletin HX-42.pdf",
        RequestedName,
        "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd");

    [Fact]
    public async Task Distinct_exact_identities_are_inconclusive()
    {
        var result = await ResolveAsync(
            _ => V2Payload([Target]),
            _ => UnifiedPayload([Target with { DocId = SecondDocId }]));

        AssertDisagreement(result);
    }

    [Fact]
    public async Task Matching_ambiguous_sets_ignore_order()
    {
        var result = await ResolveAsync(
            _ => V2Payload([Target, Second]),
            _ => UnifiedPayload([Second, Target]));

        Assert.Equal(SourceBackedDocumentResolutionStatus.Ambiguous,
            result.Status);
        Assert.True(result.CatalogObservationComplete);
        Assert.Equal(2, result.ExactMatchCount);
        Assert.Equal(
            [TargetDocId, SecondDocId],
            result.Candidates.Select(candidate => candidate.DocId)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public async Task Different_ambiguous_sets_are_inconclusive()
    {
        var result = await ResolveAsync(
            _ => V2Payload([Target, Second]),
            _ => UnifiedPayload([Target, Third]));

        AssertDisagreement(result);
    }

    [Fact]
    public async Task Duplicate_rows_of_the_same_identity_are_deduplicated()
    {
        var result = await ResolveAsync(
            _ => V2Payload([Target, Target]),
            _ => UnifiedPayload([Target]));

        Assert.Equal(SourceBackedDocumentResolutionStatus.Resolved,
            result.Status);
        Assert.Equal(TargetDocId, Assert.Single(result.Candidates).DocId);
        Assert.Equal(1, result.ExactMatchCount);
    }

    [Fact]
    public async Task Same_doc_id_with_incompatible_paths_is_inconclusive()
    {
        var incompatible = Target with
        {
            DocPath = "Legacy/Service Bulletin HX-42.pdf"
        };
        var result = await ResolveAsync(
            _ => V2Payload([Target]),
            _ => UnifiedPayload([incompatible]));

        AssertDisagreement(result);
    }

    [Fact]
    public async Task V2_error_is_inconclusive()
    {
        var result = await ResolveAsync(
            _ => ErrorResponse(HttpStatusCode.BadRequest),
            _ => UnifiedPayload([Target]));

        AssertObservationFailure(result);
    }

    [Fact]
    public async Task Unified_error_is_inconclusive()
    {
        var result = await ResolveAsync(
            _ => V2Payload([Target]),
            _ => ErrorResponse(HttpStatusCode.BadRequest));

        AssertObservationFailure(result);
    }

    [Fact]
    public async Task Incomplete_V2_pagination_is_inconclusive()
    {
        var result = await ResolveAsync(
            request => request.RequestUri!.Query.Contains(
                    "cursor=",
                    StringComparison.Ordinal)
                ? V2Payload([], hasNext: true)
                : V2Payload([Target], hasNext: true),
            _ => UnifiedPayload([Target]));

        AssertObservationFailure(result);
    }

    [Fact]
    public async Task Incomplete_unified_pagination_is_inconclusive()
    {
        var fullPage = Enumerable.Range(1, 100)
            .Select(index => index == 1
                ? Target
                : Filler(index))
            .ToArray();
        var result = await ResolveAsync(
            _ => V2Payload([Target]),
            request => request.RequestUri!.Query.Contains(
                    "offset=100",
                    StringComparison.Ordinal)
                ? UnifiedPayload([Target], offset: 0)
                : UnifiedPayload(fullPage, offset: 0));

        AssertObservationFailure(result);
    }

    [Fact]
    public async Task Extensionless_reference_resolves_the_exact_stem()
    {
        var result = await ResolveAsync(
            _ => V2Payload([Target]),
            _ => UnifiedPayload([Target]),
            "Service Bulletin HX-42");

        Assert.Equal(SourceBackedDocumentResolutionStatus.Resolved,
            result.Status);
        Assert.Equal(TargetDocId, Assert.Single(result.Candidates).DocId);
    }

    [Fact]
    public async Task Catalog_adapter_preserves_a_longer_identity_prefix_candidate()
    {
        var longer = Target with
        {
            DocPath = "Operations/Service Bulletin HX-42 Installation Guide.pdf",
            DocName = "Service Bulletin HX-42 Installation Guide.pdf"
        };
        var handler = new StubHttpHandler(request =>
            request.RequestUri!.AbsolutePath switch
            {
                "/catalog/documents" => V2Payload([longer]),
                "/documents" => UnifiedPayload([longer]),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            });
        var catalog = new ApiClientSourceBackedNamedDocumentCatalogClient(
            CreateApiClient(handler));

        var page = await catalog.SearchAsync(
            "Service Bulletin HX-42",
            100,
            0,
            CancellationToken.None);

        Assert.Equal(TargetDocId, Assert.Single(page.Items).DocId);
    }

    [Fact]
    public async Task Catalog_adapter_preserves_a_formal_designator_candidate()
    {
        var formal = Target with
        {
            DocPath = "Standards/FD CEN TR 15281 2023 Inerting guidance.pdf",
            DocName = "FD CEN TR 15281 2023 Inerting guidance.pdf"
        };
        var handler = new StubHttpHandler(request =>
            request.RequestUri!.AbsolutePath switch
            {
                "/catalog/documents" => V2Payload([formal]),
                "/documents" => UnifiedPayload([formal]),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            });
        var catalog = new ApiClientSourceBackedNamedDocumentCatalogClient(
            CreateApiClient(handler));

        var page = await catalog.SearchAsync(
            "15281",
            100,
            0,
            CancellationToken.None);

        Assert.Equal(TargetDocId, Assert.Single(page.Items).DocId);
    }

    [Fact]
    public async Task Fuzzy_neighbor_only_is_not_promoted()
    {
        var result = await ResolveAsync(
            _ => V2Payload([Target]),
            _ => UnifiedPayload([Target]),
            "Service Bulletin HX-4.pdf");

        Assert.Equal(SourceBackedDocumentResolutionStatus.NotFound,
            result.Status);
        Assert.True(result.CatalogObservationComplete);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task Catalog_digest_is_replaced_by_canonical_revision_hash()
    {
        var result = await ResolveAsync(
            _ => V2Payload([Target]),
            _ => UnifiedPayload([Target]));

        Assert.Equal(SourceBackedDocumentResolutionStatus.Resolved,
            result.Status);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(CanonicalRevisionId, candidate.RevisionId);
        Assert.Equal(CanonicalSourceHash, candidate.SourceHash);
    }

    private static async Task<SourceBackedDocumentResolutionObservation>
        ResolveAsync(
            Func<HttpRequestMessage, HttpResponseMessage> v2Response,
            Func<HttpRequestMessage, HttpResponseMessage> unifiedResponse,
            string requestedName = RequestedName)
    {
        var handler = new StubHttpHandler(request =>
            request.RequestUri!.AbsolutePath switch
            {
                "/catalog/documents" => v2Response(request),
                "/documents" => unifiedResponse(request),
                "/documents/context" => ContextPayload(),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            });
        var resolver = new SourceBackedNamedDocumentResolver(
            new ApiClientSourceBackedNamedDocumentCatalogClient(
                CreateApiClient(handler)));

        return await resolver.ResolveAsync(
            requestedName,
            CancellationToken.None);
    }

    private static HttpResponseMessage V2Payload(
        IEnumerable<CatalogDoc> documents,
        bool hasNext = false)
        => JsonResponse(JsonSerializer.Serialize(new
        {
            value = documents.Select(document => new
            {
                docId = document.DocId,
                docPath = document.DocPath,
                canonicalName = document.DocName,
                categoryPath = "Operations",
                sourceHash = document.SourceHash,
                status = "indexed"
            }),
            nextLink = hasNext ? "next" : null
        }));

    private static HttpResponseMessage UnifiedPayload(
        IEnumerable<CatalogDoc> documents,
        int offset = 0)
    {
        var materialized = documents.ToArray();
        return JsonResponse(JsonSerializer.Serialize(new
        {
            items = materialized.Select(document => new
            {
                docId = document.DocId,
                docPath = document.DocPath,
                docName = document.DocName,
                category = "Operations",
                status = "indexed"
            }),
            limit = 100,
            offset
        }));
    }

    private static HttpResponseMessage ContextPayload()
        => JsonResponse(JsonSerializer.Serialize(new
        {
            found = true,
            document = new
            {
                docId = TargetDocId,
                docPath = Target.DocPath,
                docName = Target.DocName,
                revisionId = CanonicalRevisionId,
                sourceHash = CanonicalSourceHash
            },
            items = Array.Empty<object>()
        }));

    private static CatalogDoc Filler(int index)
        => new(
            $"00000000-0000-0000-0000-{index:000000000000}",
            $"Other/Filler {index:000}.pdf",
            $"Filler {index:000}.pdf",
            null);

    private static HttpResponseMessage ErrorResponse(HttpStatusCode status)
        => new(status)
        {
            Content = new StringContent("error", Encoding.UTF8, "text/plain")
        };

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static ApiClient CreateApiClient(HttpMessageHandler handler)
    {
        var api = new ApiClient();
        api.Configure("http://localhost:5122", "test-api-key", "test-user");
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

    private static void AssertDisagreement(
        SourceBackedDocumentResolutionObservation result)
    {
        Assert.Equal(SourceBackedDocumentResolutionStatus.Inconclusive,
            result.Status);
        Assert.False(result.CatalogObservationComplete);
        Assert.Equal("catalog_observations_disagree", result.ReasonCode);
    }

    private static void AssertObservationFailure(
        SourceBackedDocumentResolutionObservation result)
    {
        Assert.Equal(SourceBackedDocumentResolutionStatus.Inconclusive,
            result.Status);
        Assert.False(result.CatalogObservationComplete);
        Assert.Equal("catalog_observation_failed", result.ReasonCode);
        Assert.False(string.IsNullOrWhiteSpace(result.TechnicalError));
    }

    private sealed record CatalogDoc(
        string DocId,
        string DocPath,
        string DocName,
        string? SourceHash);

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
