using System.Net;
using System.Text;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class TeiEmbeddingIntegrityTests
{
    [Fact]
    public async Task Response_indices_bind_vectors_to_their_original_input()
    {
        using var client = Client("""{"data":[{"index":1,"embedding":[2,0]},{"index":0,"embedding":[1,0]}]}""");
        var vectors = await TeiClient.EmbedAsync(client, "test", ["first", "second"], CancellationToken.None);
        Assert.Equal(new float[] { 1, 0 }, vectors[0]);
        Assert.Equal(new float[] { 2, 0 }, vectors[1]);
    }

    [Theory]
    [InlineData("""{"data":[{"index":0,"embedding":[1,"bad"]}]}""")]
    [InlineData("""{"data":[{"index":0,"embedding":[]}]}""")]
    [InlineData("""{"data":[{"index":0,"embedding":[0,0]}]}""")]
    [InlineData("""{"data":[{"index":0,"embedding":[1e100,1]}]}""")]
    [InlineData("""{"data":[{"index":1,"embedding":[1,0]}]}""")]
    public async Task Invalid_vector_cannot_be_published_as_a_valid_embedding(string body)
    {
        using var client = Client(body);
        await Assert.ThrowsAnyAsync<Exception>(() => TeiClient.EmbedAsync(client, "test", ["source"], CancellationToken.None));
    }

    [Theory]
    [InlineData("""{"data":[{"index":0,"embedding":[1,0]},{"index":0,"embedding":[2,0]}]}""")]
    [InlineData("""{"data":[{"index":0,"embedding":[1,0]},{"embedding":[2,0]}]}""")]
    [InlineData("""{"data":[{"index":0,"embedding":[1,0]},{"index":1,"embedding":[2,0,0]}]}""")]
    public async Task A_batch_requires_unique_input_bindings_and_consistent_dimensions(string body)
    {
        using var client = Client(body);
        await Assert.ThrowsAnyAsync<Exception>(() => TeiClient.EmbedAsync(client, "test", ["first", "second"], CancellationToken.None));
    }

    [Fact]
    public async Task Dimension_cache_is_scoped_to_the_runtime_endpoint()
    {
        var model = "model-" + Guid.NewGuid().ToString("N");
        using var first = Client("""{"data":[{"index":0,"embedding":[1,0]}]}""", "http://127.0.0.1:17621");
        using var second = Client("""{"data":[{"index":0,"embedding":[1,0,0]}]}""", "http://127.0.0.1:17622");
        Assert.Equal(2, await TeiClient.GetVectorDimAsync(first, model, CancellationToken.None));
        Assert.Equal(3, await TeiClient.GetVectorDimAsync(second, model, CancellationToken.None));
    }

    [Fact]
    public async Task Upstream_error_body_is_not_copied_into_the_persisted_exception()
    {
        using var client = new HttpClient(new JsonHandler("private-document-sentinel", HttpStatusCode.BadRequest)) { BaseAddress = new Uri("http://127.0.0.1:1") };
        var error = await Assert.ThrowsAnyAsync<Exception>(() => TeiClient.EmbedAsync(client, "test", ["source"], CancellationToken.None));
        Assert.DoesNotContain("private-document-sentinel", error.Message);
        Assert.Contains("400", error.Message);
    }

    private static HttpClient Client(string json, string endpoint = "http://127.0.0.1:1")
        => new(new JsonHandler(json, HttpStatusCode.OK)) { BaseAddress = new Uri(endpoint) };

    [Fact]
    public async Task Qdrant_error_body_is_not_copied_into_the_persisted_exception()
    {
        using var client = new HttpClient(new JsonHandler("private-document-sentinel", HttpStatusCode.BadRequest)) { BaseAddress = new Uri("http://127.0.0.1:1") };
        var error = await Assert.ThrowsAnyAsync<Exception>(() => QdrantClient.UpsertPointsAsync(client, "test", [], CancellationToken.None));
        Assert.DoesNotContain("private-document-sentinel", error.Message);
        Assert.Contains("400", error.Message);
    }

    private sealed class JsonHandler(string json, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
