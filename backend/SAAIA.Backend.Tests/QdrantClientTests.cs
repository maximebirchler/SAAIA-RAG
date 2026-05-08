using System.Net;
using System.Text.Json;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class QdrantClientTests
{
    [Fact]
    public async Task DeleteVersionByDocAsync_deletes_only_requested_document_version()
    {
        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var docId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        using var handler = new CapturingHandler();
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://qdrant.test")
        };

        await QdrantClient.DeleteVersionByDocAsync(client, "knowledge_base", tenantId, docId, 42, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("/collections/knowledge_base/points/delete?wait=true", handler.Request.RequestUri!.PathAndQuery);

        using var doc = JsonDocument.Parse(handler.Body!);
        var must = doc.RootElement
            .GetProperty("filter")
            .GetProperty("must")
            .EnumerateArray()
            .ToArray();

        Assert.Contains(must, item =>
            item.GetProperty("key").GetString() == "tenant_id"
            && item.GetProperty("match").GetProperty("value").GetString() == tenantId.ToString());
        Assert.Contains(must, item =>
            item.GetProperty("key").GetString() == "doc_id"
            && item.GetProperty("match").GetProperty("value").GetString() == docId.ToString());
        Assert.Contains(must, item =>
            item.GetProperty("key").GetString() == "ingestion_version"
            && item.GetProperty("match").GetProperty("value").GetInt32() == 42);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            };
        }
    }
}
