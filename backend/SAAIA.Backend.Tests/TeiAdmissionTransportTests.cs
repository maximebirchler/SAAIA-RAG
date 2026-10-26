using System.Net;
using System.Text;
using Xunit;

public sealed class TeiAdmissionTransportTests
{
    [Theory]
    [InlineData("/info", "info")]
    [InlineData("/tokenize", "tokenize")]
    public async Task Admission_retries_transient_failure_with_identical_payload_and_disposes_response(string path, string operation)
    {
        var requests = new List<string>();
        var delays = 0;
        var content = new TrackedContent("temporary upstream body");
        using var client = Client(async request =>
        {
            requests.Add(await request.Content!.ReadAsStringAsync());
            return requests.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = content }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        });
        using var response = await TeiClient.SendAdmissionRequestAsync(client, HttpMethod.Post, path,
            "exact-input", operation, CancellationToken.None, (_, _) => { delays++; return Task.CompletedTask; });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "exact-input", "exact-input" }, requests);
        Assert.Equal(1, delays);
        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task Admission_retry_budget_is_finite_and_upstream_error_body_is_private()
    {
        var requests = 0;
        var delays = 0;
        using var client = Client(_ =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
                { Content = new StringContent("private-source-sentinel") });
        });
        var error = await Assert.ThrowsAsync<TeiRequestException>(() => TeiClient.SendAdmissionRequestAsync(
            client, HttpMethod.Get, "/info", null, "info", CancellationToken.None,
            (_, _) => { delays++; return Task.CompletedTask; }));
        Assert.Equal(6, requests);
        Assert.Equal(5, delays);
        Assert.Equal("tei_info_http_error", error.ErrorCode);
        Assert.DoesNotContain("private-source-sentinel", error.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task Admission_does_not_retry_capability_or_input_refusals(HttpStatusCode status)
    {
        var requests = 0;
        using var client = Client(_ =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
        });
        await Assert.ThrowsAsync<TeiRequestException>(() => TeiClient.SendAdmissionRequestAsync(client,
            HttpMethod.Post, "/tokenize", "input", "tokenize", CancellationToken.None,
            (_, _) => throw new InvalidOperationException("Refusals must not be retried")));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task Cancellation_during_backoff_prevents_another_request()
    {
        using var cancellation = new CancellationTokenSource();
        var requests = 0;
        using var client = Client(_ =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TeiClient.SendAdmissionRequestAsync(client,
            HttpMethod.Get, "/info", null, "info", cancellation.Token,
            (_, ct) => { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task A_transient_network_failure_can_recover_with_the_same_admission_contract()
    {
        var requests = 0;
        using var client = Client(_ => ++requests == 1
            ? throw new HttpRequestException("connection reset")
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var response = await TeiClient.SendAdmissionRequestAsync(client, HttpMethod.Get, "/info", null,
            "info", CancellationToken.None, (_, _) => Task.CompletedTask);
        Assert.Equal(2, requests);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static HttpClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply)
        => new(new Handler(reply)) { BaseAddress = new Uri("http://127.0.0.1:1") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => reply(request);
    }
    private sealed class TrackedContent(string text) : StringContent(text, Encoding.UTF8, "application/json")
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
