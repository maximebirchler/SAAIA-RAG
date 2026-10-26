using System.Net;
using System.Text;
using System.Text.Json;
using SAAIA.Backend.Retrieval;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class DenseActiveCandidateSearchTests
{
    [Fact]
    public async Task Expansion_obeys_absolute_ceiling_as_well_as_request_count()
    {
        using var handler = new WindowHandler(2000);
        using var client = Client(handler);
        var degraded = new List<string?>();
        Assert.Empty(await Search(client, 300, 300, (_, _) => Task.FromResult(new List<RagMatch>()), degraded));
        Assert.Equal(new[] { 300, 600, 1024 }, handler.Limits);
        Assert.Equal(new[] { DenseActiveCandidateSearch.IncompleteReason }, degraded);
    }

    [Fact]
    public async Task Missing_collection_retry_consumes_the_same_search_request_budget()
    {
        using var handler = new WindowHandler(2) { FirstSearchNotFound = true };
        using var client = Client(handler);
        var matches = await Search(client, 1, 1,
            (items, _) => Task.FromResult(items.Where(item => item.ChunkId == "chunk-1").ToList()));
        Assert.Equal("chunk-1", Assert.Single(matches).ChunkId);
        Assert.Equal(new[] { 1, 1, 2 }, handler.Limits);
        Assert.Equal(1, handler.CollectionChecks);
    }

    [Fact]
    public async Task Rechecked_windows_do_not_accumulate_content_from_a_former_active_plan()
    {
        using var handler = new WindowHandler(4);
        using var client = Client(handler);
        var filters = 0;
        var matches = await Search(client, 2, 2, (items, _) =>
        {
            filters++;
            return Task.FromResult(new List<RagMatch> { filters == 1 ? items[0] : items[^1] });
        });
        Assert.Equal(3, filters);
        Assert.Equal("chunk-3", Assert.Single(matches).ChunkId);
        Assert.DoesNotContain(matches, match => match.ChunkId == "chunk-0");
    }

    [Fact]
    public async Task Incompatible_embedding_payloads_do_not_make_a_full_qdrant_window_look_exhausted()
    {
        using var handler = new WindowHandler(100);
        using var client = Client(handler);
        var degraded = new List<string?>();
        var matches = await DenseActiveCandidateSearch.ExecuteAsync(client, "test", 4, 1, 1,
            limit => new { limit }, _ => false, (items, _) =>
            {
                Assert.Empty(items);
                return Task.FromResult(new List<RagMatch>());
            }, _ => { }, (_, reason) => degraded.Add(reason), CancellationToken.None);
        Assert.Empty(matches);
        Assert.Equal(new[] { 1, 2, 4 }, handler.Limits);
        Assert.Equal(new[] { DenseActiveCandidateSearch.IncompleteReason }, degraded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Untrusted_error_body_or_parser_path_is_not_copied_into_exception(bool malformedJson)
    {
        const string privateMarker = "PRIVATE_DOCUMENT_MARKER_52913";
        using var handler = new WindowHandler(0)
        {
            ResponseOverride = new HttpResponseMessage(malformedJson ? HttpStatusCode.OK : HttpStatusCode.BadRequest)
            {
                Content = new StringContent(malformedJson ? "{\"" + privateMarker + "\": [}" : privateMarker)
            }
        };
        using var client = Client(handler);
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => Search(client, 1, 1,
            (items, _) => Task.FromResult(items.ToList())));
        Assert.DoesNotContain(privateMarker, exception.ToString());
        Assert.Contains(malformedJson ? "qdrant_dense_search_response_invalid" : "qdrant_dense_search_http_error", exception.Message);
    }

    private static HttpClient Client(HttpMessageHandler handler) => new(handler, disposeHandler: false)
    { BaseAddress = new Uri("http://qdrant.test/") };

    private static Task<List<RagMatch>> Search(HttpClient client, int initial, int desired,
        Func<IReadOnlyList<RagMatch>, CancellationToken, Task<List<RagMatch>>> filter, List<string?>? degraded = null)
        => DenseActiveCandidateSearch.ExecuteAsync(client, "test", 4, initial, desired,
            limit => new { vector = new[] { 0.1f, 0.2f, 0.3f, 0.4f }, limit }, _ => true, filter,
            _ => { }, (_, reason) => degraded?.Add(reason), CancellationToken.None);

    private sealed class WindowHandler(int available) : HttpMessageHandler
    {
        public List<int> Limits { get; } = [];
        public bool FirstSearchNotFound { get; init; }
        public int CollectionChecks { get; private set; }
        public HttpResponseMessage? ResponseOverride { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal("/collections/test", request.RequestUri!.AbsolutePath);
                CollectionChecks++;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var limit = body.RootElement.GetProperty("limit").GetInt32();
            Limits.Add(limit);
            if (ResponseOverride is not null) return ResponseOverride;
            if (FirstSearchNotFound && Limits.Count == 1) return new HttpResponseMessage(HttpStatusCode.NotFound);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    result = Enumerable.Range(0, Math.Min(available, limit))
                        .Select(index => new { score = 0.9, payload = new { chunk_id = "chunk-" + index } }).ToArray()
                }), Encoding.UTF8, "application/json")
            };
        }
    }
}
