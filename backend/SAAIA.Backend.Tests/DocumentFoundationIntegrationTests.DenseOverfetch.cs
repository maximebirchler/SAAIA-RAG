using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class DocumentFoundationIntegrationTests
{
    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Dense_overfetch_reaches_current_points_hidden_after_obsolete_top_hits_with_one_embedding()
    {
        await using var context = await DenseOverfetchContext.CreateAsync(staleCount: 2, includeCurrent: true);
        using var probes = new DenseOverfetchProbes();
        var matches = await context.SearchAsync();

        var match = Assert.Single(matches);
        Assert.Equal(2, match.IngestionVersion);
        Assert.Equal(context.ActiveChunkId.ToString(), match.ChunkId);
        Assert.Equal(new[] { 1, 2, 4 }, context.Transport.Limits);
        Assert.Equal(1, context.Transport.EmbeddingCalls);
        Assert.Single(context.Transport.Filters.Distinct(StringComparer.Ordinal));
        Assert.Empty(context.Degraded);
        var probe = Assert.Single(probes.Completed);
        Assert.Equal(3, probe["saaia.attempts"]);
        Assert.Equal(3, probe["saaia.raw_candidates"]);
        Assert.Equal(1, probe["saaia.active_candidates"]);
        Assert.Equal(2, probe["saaia.rejected_candidates"]);
        Assert.Equal(4, probe["saaia.final_limit"]);
        Assert.Equal("success", probe["saaia.outcome"]);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Dense_overfetch_reports_incomplete_channel_when_obsolete_points_exhaust_bounded_candidate_budget()
    {
        await using var context = await DenseOverfetchContext.CreateAsync(staleCount: 100, includeCurrent: false);
        using var probes = new DenseOverfetchProbes();
        Assert.Empty(await context.SearchAsync());

        Assert.Equal(new[] { 1, 2, 4 }, context.Transport.Limits);
        Assert.Equal(1, context.Transport.EmbeddingCalls);
        Assert.Contains(("dense_qdrant", "dense_active_plan_candidate_budget_exhausted"), context.Degraded);
        var probe = Assert.Single(probes.Completed);
        Assert.Equal(3, probe["saaia.attempts"]);
        Assert.Equal(4, probe["saaia.raw_candidates"]);
        Assert.Equal(0, probe["saaia.active_candidates"]);
        Assert.Equal(4, probe["saaia.rejected_candidates"]);
        Assert.Equal("degraded", probe["saaia.outcome"]);
        Assert.Equal("dense_active_plan_candidate_budget_exhausted", probe["saaia.reason_code"]);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Dense_overfetch_stops_when_qdrant_is_exhausted_without_claiming_a_candidate_budget_failure()
    {
        await using var context = await DenseOverfetchContext.CreateAsync(staleCount: 0, includeCurrent: false);
        using var probes = new DenseOverfetchProbes();
        Assert.Empty(await context.SearchAsync());
        Assert.Equal(new[] { 1 }, context.Transport.Limits);
        Assert.Empty(context.Degraded);
        var probe = Assert.Single(probes.Completed);
        Assert.Equal(1, probe["saaia.attempts"]);
        Assert.Equal(0, probe["saaia.raw_candidates"]);
        Assert.Equal("success", probe["saaia.outcome"]);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Dense_overfetch_cancellation_during_retry_is_not_reported_as_exhaustive_empty_search()
    {
        await using var context = await DenseOverfetchContext.CreateAsync(staleCount: 2, includeCurrent: true);
        using var cancellation = new CancellationTokenSource();
        context.Transport.BeforeSearch = count => { if (count == 2) cancellation.Cancel(); };
        using var probes = new DenseOverfetchProbes();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.SearchAsync(cancellation.Token));
        Assert.Equal(new[] { 1, 2 }, context.Transport.Limits);
        Assert.Empty(context.Degraded);
        Assert.Equal("cancelled", Assert.Single(probes.Completed)["saaia.outcome"]);
    }

    [Trait("Profile", "IntegrationPg")]
    [ProfileFact("IntegrationPg")]
    public async Task Dense_overfetch_candidate_cap_is_visible_in_canonical_response_metrics()
    {
        await using var context = await DenseOverfetchContext.CreateAsync(staleCount: 100, includeCurrent: false);
        var response = await context.SearchApiAsync();
        var metrics = response.GetProperty("metrics");
        Assert.Contains("dense_qdrant", metrics.GetProperty("degradedRetrievers").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("dense_active_plan_candidate_budget_exhausted",
            metrics.GetProperty("degradedRetrieverErrors").GetProperty("dense_qdrant").GetString());
        Assert.Equal(new[] { 1, 2, 4 }, context.Transport.Limits);
    }

    private sealed class DenseOverfetchContext(PostgresIntegrationDb db, NpgsqlDataSource dataSource,
        Guid tenant, Guid doc, DenseOverfetchTransport transport) : IAsyncDisposable
    {
        private const string Path = "Knowledge/dense-overfetch.pdf";
        public DenseOverfetchTransport Transport => transport;
        public Guid ActiveChunkId => DocumentFoundationRepo.BuildStableRetrievalChunkId(doc, 2, 0);
        public List<(string Retriever, string? Reason)> Degraded { get; } = [];

        public static async Task<DenseOverfetchContext> CreateAsync(int staleCount, bool includeCurrent)
        {
            var db = await PostgresIntegrationDb.CreateAsync()
                ?? throw new InvalidOperationException("IntegrationPg requires its isolated database fixture.");
            var tenant = Guid.NewGuid();
            var doc = Guid.NewGuid();
            await PublishIndexedDocumentAsync(db, tenant, doc, Guid.NewGuid(), Path, 2,
                [new RuntimeSeedSection("Instrument", "The verified instrument setting is documented with its physical operating boundaries and the current calibration record.")]);
            return new(db, NpgsqlDataSource.Create(db.ConnectionString), tenant, doc,
                new DenseOverfetchTransport(tenant, doc, Path, staleCount, includeCurrent));
        }

        public Task<List<RagMatch>> SearchAsync(CancellationToken ct = default)
            => RetrievalChannels.SearchDenseMatchesAsync(dataSource, transport, CreateTestRagOptions(), tenant,
                "instrument calibration", null, doc.ToString(), Path, 1, ct, _ => { }, _ => { }, _ => { },
                degradedRetrieverRef: (retriever, reason) => Degraded.Add((retriever, reason)), attachContentCards: false);

        public async Task<JsonElement> SearchApiAsync()
        {
            var context = BuildRagHttpContext(tenant);
            var result = await InvokeRagSearchAsync(context, dataSource, Options.Create(CreateTestRagOptions()), transport,
                new RagSearchRequestDto("instrument calibration", DocId: doc.ToString(), DocPath: Path,
                    TopK: 1, Candidates: 1, SourceBackedCanonical: true));
            await result.ExecuteAsync(context);
            Assert.Equal(200, context.Response.StatusCode);
            return JsonSerializer.Deserialize<JsonElement>(ReadResponseBody(context));
        }

        public async ValueTask DisposeAsync()
        {
            await dataSource.DisposeAsync();
            await db.DisposeAsync();
        }
    }

    private sealed class DenseOverfetchTransport(Guid tenant, Guid doc, string path, int staleCount, bool includeCurrent)
        : HttpMessageHandler, IHttpClientFactory
    {
        public int EmbeddingCalls { get; private set; }
        public List<int> Limits { get; } = [];
        public List<string> Filters { get; } = [];
        public Action<int>? BeforeSearch { get; set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/v1/embeddings", StringComparison.Ordinal))
            {
                EmbeddingCalls++;
                return Json(new { data = new[] { new { embedding = new[] { 0.1, 0.2, 0.3, 0.4 } } } });
            }
            Assert.EndsWith("/points/search", request.RequestUri.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var limit = body.RootElement.GetProperty("limit").GetInt32();
            Limits.Add(limit);
            Filters.Add(body.RootElement.GetProperty("filter").GetRawText());
            BeforeSearch?.Invoke(Limits.Count);
            ct.ThrowIfCancellationRequested();
            var available = staleCount + (includeCurrent ? 1 : 0);
            return Json(new
            {
                result = Enumerable.Range(0, Math.Min(limit, available)).Select(index =>
                {
                    var current = includeCurrent && index == staleCount;
                    var version = current ? 2 : 1;
                    return new
                    {
                        id = Guid.NewGuid(), score = 0.99 - index * 0.001,
                        payload = new
                        {
                            tenant_id = tenant, doc_id = doc.ToString(), doc_path = path, doc_name = "dense-overfetch.pdf",
                            chunk_id = DocumentFoundationRepo.BuildStableRetrievalChunkId(doc, version, current ? 0 : index).ToString(),
                            chunk_index = current ? 0 : index, page_start = 1, page_end = 1,
                            text = "The current instrument calibration record.", ingestion_version = version, hash_doc = "080808",
                            embedding_model = "intfloat/multilingual-e5-base", embedding_input_format = "e5_passage_v1",
                            chunk_type = "unit_exact_v1", content_role = "content"
                        }
                    };
                }).ToArray()
            });
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }

    private sealed class DenseOverfetchProbes : IDisposable
    {
        private readonly ActivityListener _listener;
        public List<Dictionary<string, object?>> Completed { get; } = [];
        public DenseOverfetchProbes()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == BackendDiagnostics.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    if (activity.OperationName == "retrieval.dense_active_candidates")
                        Completed.Add(activity.TagObjects.ToDictionary(pair => pair.Key, pair => pair.Value));
                }
            };
            ActivitySource.AddActivityListener(_listener);
        }
        public void Dispose() => _listener.Dispose();
    }
}
