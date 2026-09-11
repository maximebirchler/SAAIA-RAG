using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent.SourceBackedRag;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class SourceBackedNamedDocumentCanonicalIdentityHydrationTests
{
    private const string RequestedName = "Service Bulletin HX-42.pdf";
    private const string TargetDocId =
        "11111111-1111-1111-1111-111111111111";
    private const string TargetPath =
        "Operations/Service Bulletin HX-42.pdf";
    private const string RevisionId =
        "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string CanonicalSourceHash =
        "909173f3549f731efe5bd668747edf9f12963f793233a705a63b83669a818fcf";
    private const string SummaryFreshnessDigest =
        "c62f25ff806221be4089afc6cec66c9f";
    private const string ForbiddenContextText =
        "THIS CONTEXT TEXT MUST NEVER BECOME IDENTITY EVIDENCE";

    [Fact]
    public async Task Unique_consensus_is_hydrated_with_active_revision_identity()
    {
        var harness = Harness.Exact(ContextPayload());

        var result = await harness.ResolveAsync();

        Assert.Equal(SourceBackedDocumentResolutionStatus.Resolved, result.Status);
        Assert.True(result.CatalogObservationComplete);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(TargetDocId, candidate.DocId);
        Assert.Equal(TargetPath, candidate.DocPath);
        Assert.Equal(RequestedName, candidate.DocName);
        Assert.Equal(RevisionId, candidate.RevisionId);
        Assert.Equal(CanonicalSourceHash, candidate.SourceHash);
        Assert.DoesNotContain(SummaryFreshnessDigest,
            candidate.SourceHash ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, harness.ContextRequestCount);
    }

    [Fact]
    public async Task Hydrated_identity_accepts_matching_canonical_evidence()
    {
        var result = await Harness.Exact(ContextPayload()).ResolveAsync();
        var intake = Intake(result);

        var guarded = SourceBackedAgentV2Runner
            .ApplyNamedDocumentIdentityContractForTests(
                intake,
                Bundle(RevisionId, CanonicalSourceHash));

        Assert.DoesNotContain(
            SourceBackedNamedDocumentIdentity.MismatchRiskFlag,
            Assert.Single(guarded.Items).RiskFlags,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Hydrated_identity_rejects_a_stale_revision()
    {
        var result = await Harness.Exact(ContextPayload()).ResolveAsync();
        var guarded = SourceBackedAgentV2Runner
            .ApplyNamedDocumentIdentityContractForTests(
                Intake(result),
                Bundle(
                    "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                    new string('e', 64)));

        Assert.Contains(
            SourceBackedNamedDocumentIdentity.MismatchRiskFlag,
            Assert.Single(guarded.Items).RiskFlags,
            StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(InconclusiveCanonicalIdentityCases))]
    public async Task Invalid_or_changed_active_identity_is_inconclusive(
        string contextPayload,
        string expectedReason)
    {
        var harness = Harness.Exact(contextPayload);

        var result = await harness.ResolveAsync();

        Assert.Equal(
            SourceBackedDocumentResolutionStatus.Inconclusive,
            result.Status);
        Assert.False(result.CatalogObservationComplete);
        Assert.Equal(expectedReason, result.ReasonCode);
        Assert.Empty(result.Candidates);
        Assert.Equal(1, harness.ContextRequestCount);
    }

    public static IEnumerable<object[]> InconclusiveCanonicalIdentityCases()
    {
        yield return
        [
            ContextPayload(found: false),
            "canonical_identity_not_found"
        ];
        yield return
        [
            ContextPayload(docId: "22222222-2222-2222-2222-222222222222"),
            "canonical_identity_mismatch"
        ];
        yield return
        [
            ContextPayload(docPath: "Archive/Service Bulletin HX-42.pdf"),
            "canonical_identity_mismatch"
        ];
        yield return
        [
            ContextPayload(docName: "Service Bulletin HX-43.pdf"),
            "canonical_identity_mismatch"
        ];
        yield return
        [
            ContextPayload(revisionId: null),
            "canonical_identity_incomplete"
        ];
        yield return
        [
            ContextPayload(revisionId: "not-a-guid"),
            "canonical_identity_incomplete"
        ];
        yield return
        [
            ContextPayload(sourceHash: SummaryFreshnessDigest),
            "canonical_identity_incomplete"
        ];
        yield return
        [
            "{\"found\":true,\"document\":null}",
            "canonical_identity_observation_failed"
        ];
    }

    [Fact]
    public async Task Context_http_failure_is_inconclusive()
    {
        var harness = Harness.Exact(
            ContextPayload(),
            contextStatus: HttpStatusCode.ServiceUnavailable);

        var result = await harness.ResolveAsync();

        Assert.Equal(
            SourceBackedDocumentResolutionStatus.Inconclusive,
            result.Status);
        Assert.Equal(
            "canonical_identity_observation_failed",
            result.ReasonCode);
        Assert.Empty(result.Candidates);
        Assert.Equal(1, harness.ContextRequestCount);
    }

    [Fact]
    public async Task Not_found_does_not_request_canonical_identity()
    {
        var harness = Harness.Empty();

        var result = await harness.ResolveAsync();

        Assert.Equal(SourceBackedDocumentResolutionStatus.NotFound, result.Status);
        Assert.Equal(0, harness.ContextRequestCount);
    }

    [Fact]
    public async Task Ambiguous_catalog_does_not_request_canonical_identity()
    {
        var second = new CatalogDoc(
            "22222222-2222-2222-2222-222222222222",
            "Archive/Service Bulletin HX-42.pdf",
            RequestedName);
        var harness = new Harness(
            [Target, second],
            [Target, second],
            ContextPayload());

        var result = await harness.ResolveAsync();

        Assert.Equal(SourceBackedDocumentResolutionStatus.Ambiguous, result.Status);
        Assert.Equal(0, harness.ContextRequestCount);
    }

    [Fact]
    public async Task Catalog_disagreement_does_not_request_canonical_identity()
    {
        var harness = new Harness([Target], [], ContextPayload());

        var result = await harness.ResolveAsync();

        Assert.Equal(
            SourceBackedDocumentResolutionStatus.Inconclusive,
            result.Status);
        Assert.Equal("catalog_observations_disagree", result.ReasonCode);
        Assert.Equal(0, harness.ContextRequestCount);
    }

    [Fact]
    public async Task Filename_and_stem_queries_hydrate_only_once()
    {
        var harness = Harness.Exact(ContextPayload());

        var result = await harness.ResolveAsync();

        Assert.Equal(SourceBackedDocumentResolutionStatus.Resolved, result.Status);
        Assert.True(harness.V2RequestCount >= 2);
        Assert.True(harness.UnifiedRequestCount >= 2);
        Assert.Equal(1, harness.ContextRequestCount);
    }

    [Fact]
    public async Task Cancellation_during_identity_hydration_is_propagated()
    {
        using var cancellation = new CancellationTokenSource();
        var harness = Harness.Exact(
            ContextPayload(),
            onContext: () => cancellation.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.ResolveAsync(cancellation.Token));
        Assert.Equal(1, harness.ContextRequestCount);
    }

    [Fact]
    public async Task Context_item_text_is_not_exposed_by_resolution()
    {
        var result = await Harness.Exact(ContextPayload()).ResolveAsync();

        Assert.DoesNotContain(
            ForbiddenContextText,
            result.ToString(),
            StringComparison.Ordinal);
        Assert.All(result.Candidates, static candidate =>
        {
            Assert.DoesNotContain(ForbiddenContextText, candidate.DocId);
            Assert.DoesNotContain(ForbiddenContextText, candidate.DocPath);
            Assert.DoesNotContain(ForbiddenContextText, candidate.DocName);
            Assert.DoesNotContain(
                ForbiddenContextText,
                candidate.RevisionId ?? string.Empty);
            Assert.DoesNotContain(
                ForbiddenContextText,
                candidate.SourceHash ?? string.Empty);
        });
    }

    private static readonly CatalogDoc Target = new(
        TargetDocId,
        TargetPath,
        RequestedName);

    private static SourceBackedIntake Intake(
        SourceBackedDocumentResolutionObservation observation)
        => new(
            "Read the named document.",
            "rag.answer",
            Array.Empty<string>(),
            Array.Empty<string>(),
            AllowsPartialAnswer: false,
            Language: "en",
            RequestedDocumentName: RequestedName,
            RequestedDocumentResolution: observation,
            DocumentScope: SourceBackedDocumentScope.RequestedDocument);

    private static EvidenceBundle Bundle(
        string revisionId,
        string sourceHash)
        => new(
            "bundle-canonical-identity",
            "Read the named document.",
            [
                new EvidenceItem(
                    "E1",
                    "rag_hit",
                    "rag.search",
                    "inspection interval",
                    TargetDocId,
                    RequestedName,
                    TargetPath,
                    sourceHash,
                    revisionId,
                    4,
                    4,
                    "chunk-4",
                    "The documented inspection interval is stated here.",
                    "the documented inspection interval is stated here",
                    0.9,
                    1,
                    "Operations",
                    "en",
                    "en",
                    "native_text",
                    (JsonElement?)null,
                    new Dictionary<string, string>(),
                    new Dictionary<string, string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>())
            ],
            Array.Empty<SourceBackedTraceEvent>());

    private static string ContextPayload(
        bool found = true,
        string? docId = TargetDocId,
        string? docPath = TargetPath,
        string? docName = RequestedName,
        string? revisionId = RevisionId,
        string? sourceHash = CanonicalSourceHash)
        => JsonSerializer.Serialize(new
        {
            found,
            anchorFound = found,
            contextKind = "page_window",
            document = found
                ? new
                {
                    docId,
                    docPath,
                    docName,
                    revisionId,
                    sourceHash
                }
                : null,
            total = found ? 1 : 0,
            items = found
                ? new[]
                {
                    new
                    {
                        chunkId = "chunk-1",
                        revisionId,
                        sourceHash,
                        text = ForbiddenContextText
                    }
                }
                : []
        });

    private sealed class Harness
    {
        private readonly CatalogDoc[] _v2;
        private readonly CatalogDoc[] _unified;
        private readonly string _contextPayload;
        private readonly HttpStatusCode _contextStatus;
        private readonly Action? _onContext;

        public Harness(
            CatalogDoc[] v2,
            CatalogDoc[] unified,
            string contextPayload,
            HttpStatusCode contextStatus = HttpStatusCode.OK,
            Action? onContext = null)
        {
            _v2 = v2;
            _unified = unified;
            _contextPayload = contextPayload;
            _contextStatus = contextStatus;
            _onContext = onContext;
        }

        public int V2RequestCount { get; private set; }
        public int UnifiedRequestCount { get; private set; }
        public int ContextRequestCount { get; private set; }

        public static Harness Exact(
            string contextPayload,
            HttpStatusCode contextStatus = HttpStatusCode.OK,
            Action? onContext = null)
            => new(
                [Target],
                [Target],
                contextPayload,
                contextStatus,
                onContext);

        public static Harness Empty()
            => new([], [], ContextPayload());

        public Task<SourceBackedDocumentResolutionObservation> ResolveAsync(
            CancellationToken ct = default)
        {
            var api = CreateApiClient(new StubHttpHandler(RespondAsync));
            var resolver = new SourceBackedNamedDocumentResolver(
                new ApiClientSourceBackedNamedDocumentCatalogClient(api));
            return resolver.ResolveAsync(RequestedName, ct);
        }

        private Task<HttpResponseMessage> RespondAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/catalog/documents")
            {
                V2RequestCount++;
                return Task.FromResult(JsonResponse(V2Payload(_v2)));
            }
            if (path == "/documents")
            {
                UnifiedRequestCount++;
                return Task.FromResult(JsonResponse(UnifiedPayload(_unified)));
            }
            if (path == "/documents/context")
            {
                ContextRequestCount++;
                _onContext?.Invoke();
                if (ct.IsCancellationRequested)
                    return Task.FromCanceled<HttpResponseMessage>(ct);
                return Task.FromResult(new HttpResponseMessage(_contextStatus)
                {
                    Content = new StringContent(
                        _contextStatus == HttpStatusCode.OK
                            ? _contextPayload
                            : "synthetic context failure",
                        Encoding.UTF8,
                        _contextStatus == HttpStatusCode.OK
                            ? "application/json"
                            : "text/plain")
                });
            }
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static string V2Payload(IEnumerable<CatalogDoc> documents)
        => JsonSerializer.Serialize(new
        {
            value = documents.Select(document => new
            {
                docId = document.DocId,
                docPath = document.DocPath,
                canonicalName = document.DocName,
                categoryPath = "Operations",
                sourceHash = SummaryFreshnessDigest,
                status = "indexed"
            }),
            nextLink = (string?)null
        });

    private static string UnifiedPayload(IEnumerable<CatalogDoc> documents)
        => JsonSerializer.Serialize(new
        {
            items = documents.Select(document => new
            {
                docId = document.DocId,
                docPath = document.DocPath,
                docName = document.DocName,
                category = "Operations",
                status = "indexed"
            }),
            limit = 100,
            offset = 0
        });

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

    private sealed record CatalogDoc(
        string DocId,
        string DocPath,
        string DocName);

    private sealed class StubHttpHandler(
        Func<HttpRequestMessage, CancellationToken,
            Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
