using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class SummaryMissingDocumentFlowTests
{
    [Theory]
    [InlineData(false, "fr", "trouvé")]
    [InlineData(true, "fr", "trouvé")]
    [InlineData(false, "en", "found")]
    [InlineData(true, "en", "found")]
    public async Task Missing_document_is_named_and_distinguished_from_failed_summary(
        bool about, string language, string missingWord)
    {
        var handler = new EmptyCatalogHandler();
        var sut = Create(handler);
        var streamed = new StringBuilder();
        var (answer, sources) = await RunAsync(sut, about, language, streamed);

        Assert.Contains("missing-atlas-coverage-7f32.pdf", answer, StringComparison.Ordinal);
        Assert.Contains(missingWord, answer, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(answer, streamed.ToString());
        Assert.Null(sources);
        Assert.NotEmpty(handler.Requests);
        Assert.All(handler.Requests, path => Assert.Equal("/catalog/documents", path));
        Assert.DoesNotContain("[E", answer, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Catalog_transport_failure_does_not_become_document_absence(bool about)
    {
        var handler = new EmptyCatalogHandler { FailTransport = true };
        var streamed = new StringBuilder();
        await Assert.ThrowsAsync<HttpRequestException>(() => RunAsync(Create(handler), about, "fr", streamed));
        Assert.Empty(streamed.ToString());
        Assert.NotEmpty(handler.Requests);
    }

    private static Task<(string, object?)> RunAsync(
        ToolAgentOrchestrator sut, bool about, string language, StringBuilder streamed)
    {
        var method = typeof(ToolAgentOrchestrator).GetMethod(
            about ? "RunDocumentAboutRequestAsync" : "RunDocumentSummaryRequestAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        Action<string> delta = text => streamed.Append(text);
        object?[] arguments = about
            ? ["missing-atlas-coverage-7f32.pdf", language, CancellationToken.None, delta, null]
            : ["missing-atlas-coverage-7f32.pdf", language, "Summarize the named document.", CancellationToken.None, delta, null, null];
        return (Task<(string, object?)>)method!.Invoke(sut, arguments)!;
    }

    private static ToolAgentOrchestrator Create(HttpMessageHandler handler)
    {
        var api = new ApiClient();
        api.Configure("http://localhost:5122", "synthetic-api-key", "synthetic-user");
        var field = typeof(ApiClient).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(api, new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5122") });
        return new ToolAgentOrchestrator(api, llm: null!, mem: new ToolMemory());
    }

    private sealed class EmptyCatalogHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public bool FailTransport { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsolutePath);
            if (FailTransport)
                throw new HttpRequestException("Synthetic catalog transport failure.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"items\":[],\"total\":0}", Encoding.UTF8, "application/json")
            });
        }
    }
}
