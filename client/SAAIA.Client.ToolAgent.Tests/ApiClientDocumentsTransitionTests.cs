using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ApiClientDocumentsTransitionTests
{
    [Fact]
    public async Task DocumentsCatalogAsync_falls_back_to_catalog_alias_when_unified_route_is_missing()
    {
        var requestedPaths = new List<string>();
        var handler = new StubHttpHandler(req =>
        {
            requestedPaths.Add(req.RequestUri!.PathAndQuery);
            return req.RequestUri!.AbsolutePath switch
            {
                "/documents" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/documents/catalog" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "items": [
                            {
                              "docId": "11111111-1111-1111-1111-111111111111",
                              "docPath": "ATEX/test.pdf",
                              "docName": "test.pdf",
                              "category": "ATEX",
                              "status": "indexed",
                              "pageCount": 2
                            }
                          ],
                          "limit": 10,
                          "offset": 0
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                },
                _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            };
        });

        var sut = CreateApiClient(handler);
        var response = await sut.DocumentsCatalogAsync("ATEX", "test", 10, 0, CancellationToken.None);

        var item = Assert.Single(response.Items);
        Assert.Equal("ATEX/test.pdf", item.DocPath);
        Assert.Equal("test.pdf", item.DocName);
        Assert.Equal("ATEX", item.Category);
        Assert.Contains("/documents?limit=10&offset=0&category=ATEX&q=test", requestedPaths);
        Assert.Contains("/documents/catalog?limit=10&offset=0&category=ATEX&q=test", requestedPaths);
    }

    [Fact]
    public async Task DocumentsGetAsync_falls_back_to_catalog_alias_when_unified_detail_route_is_missing()
    {
        const string docId = "11111111-1111-1111-1111-111111111111";
        var requestedPaths = new List<string>();
        var handler = new StubHttpHandler(req =>
        {
            requestedPaths.Add(req.RequestUri!.PathAndQuery);
            return req.RequestUri!.AbsolutePath switch
            {
                "/documents/" + docId => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/documents/catalog/" + docId => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "docId": "11111111-1111-1111-1111-111111111111",
                          "docPath": "ATEX/test.pdf",
                          "categoryPath": "ATEX"
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                },
                _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            };
        });

        var sut = CreateApiClient(handler);
        var response = await sut.DocumentsGetAsync(docId, CancellationToken.None);

        Assert.Equal("ATEX/test.pdf", response.GetProperty("docPath").GetString());
        Assert.Equal("ATEX", response.GetProperty("categoryPath").GetString());
        Assert.Contains("/documents/" + docId, requestedPaths);
        Assert.Contains("/documents/catalog/" + docId, requestedPaths);
    }

    [Fact]
    public async Task DocumentsCatalogIsIndexedAsync_uses_catalog_alias_when_unified_detail_is_not_available()
    {
        const string docId = "11111111-1111-1111-1111-111111111111";
        var requestedPaths = new List<string>();
        var handler = new StubHttpHandler(req =>
        {
            requestedPaths.Add(req.RequestUri!.PathAndQuery);
            return req.RequestUri!.AbsolutePath switch
            {
                "/documents/" + docId => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/documents/catalog/" + docId => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"docId\":\"11111111-1111-1111-1111-111111111111\"}", Encoding.UTF8, "application/json")
                },
                _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            };
        });

        var sut = CreateApiClient(handler);
        var indexed = await sut.DocumentsCatalogIsIndexedAsync(docId, CancellationToken.None);

        Assert.True(indexed);
        Assert.Contains("/documents/" + docId, requestedPaths);
        Assert.Contains("/documents/catalog/" + docId, requestedPaths);
    }

    private static ApiClient CreateApiClient(HttpMessageHandler handler)
    {
        var sut = new ApiClient();
        sut.Configure("http://localhost:5122", "test-api-key", "test-user");

        var field = typeof(ApiClient).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(sut, new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5122")
        });

        return sut;
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
