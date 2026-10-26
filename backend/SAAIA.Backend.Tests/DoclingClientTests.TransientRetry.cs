using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xunit;

public sealed partial class DoclingClientTests
{
    [Theory]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task ConvertPdfAsync_transient_retry_replays_complete_multipart_then_validates_canonical_response(int status)
    {
        var requests = new List<HttpRequestMessage>();
        var forms = new List<HttpContent>();
        var fileParts = new List<HttpContent>();
        var fileNames = new List<string>();
        using var fixture = new RetryConversionFixture(async (request, token) =>
        {
            requests.Add(request);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/convert/file", request.RequestUri!.AbsolutePath);
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            forms.Add(form);
            var filePart = Assert.Single(form, part => part.Headers.ContentDisposition?.FileName is not null);
            fileParts.Add(filePart);
            Assert.Equal("application/pdf", filePart.Headers.ContentType!.MediaType);
            Assert.Equal(RetryConversionFixture.SourceBytes, await filePart.ReadAsByteArrayAsync(token));
            fileNames.Add(filePart.Headers.ContentDisposition!.FileName!.Trim('"'));
            var fields = new Dictionary<string, string>();
            foreach (var part in form.Where(part => part.Headers.ContentDisposition?.FileName is null))
                fields.Add(part.Headers.ContentDisposition!.Name!.Trim('"'), await part.ReadAsStringAsync(token));
            Assert.Equal("true", fields["force_ocr"]);
            Assert.Equal("json", fields["to_formats"]);
            Assert.Equal("accurate", fields["table_mode"]);
            return requests.Count == 1 ? RetryFailure(status) : RetrySuccess();
        });

        var response = await fixture.Client.ConvertPdfAsync(fixture.Path, CancellationToken.None, forceOcrOverride: true);

        Assert.Equal("success", response.Status);
        Assert.Equal("DoclingDocument", response.Document.JsonContent!.SchemaName);
        Assert.True(response.RequestedForceOcr);
        Assert.Equal(2, requests.Count);
        Assert.All(fileNames, name => Assert.Equal(Path.GetFileName(fixture.Path), name));
        Assert.NotSame(requests[0], requests[1]);
        Assert.NotSame(forms[0], forms[1]);
        Assert.NotSame(fileParts[0], fileParts[1]);
    }

    [Fact]
    public async Task ConvertPdfAsync_transient_retry_recovers_from_network_failure()
    {
        var calls = 0;
        using var fixture = new RetryConversionFixture((_, _) =>
            ++calls == 1
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("PRIVATE_URI_AND_DOCUMENT_SENTINEL"))
                : Task.FromResult(RetrySuccess()));

        var response = await fixture.Client.ConvertPdfAsync(fixture.Path, CancellationToken.None);

        Assert.Equal("success", response.Status);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ConvertPdfAsync_transient_retry_keeps_original_source_handle_after_path_is_atomically_replaced()
    {
        string? sourcePath = null;
        var calls = 0;
        var replacement = Encoding.UTF8.GetBytes("%PDF-1.7\nDIFFERENT DOCUMENT MUST NOT BE RETRIED\n%%EOF");
        using var fixture = new RetryConversionFixture(async (request, token) =>
        {
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            var filePart = Assert.Single(form, part => part.Headers.ContentDisposition?.FileName is not null);
            Assert.Equal(RetryConversionFixture.SourceBytes, await filePart.ReadAsByteArrayAsync(token));
            if (++calls == 1)
            {
                var temporary = sourcePath + ".replacement";
                try
                {
                    await File.WriteAllBytesAsync(temporary, replacement, token);
                    // Move(overwrite) fails on this Windows read/delete-sharing handle even
                    // though Replace succeeds. Keep write sharing disabled in the product.
                    File.Replace(temporary, sourcePath!, destinationBackupFileName: null);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                return RetryFailure(502);
            }
            return RetrySuccess();
        });
        sourcePath = fixture.Path;

        await fixture.Client.ConvertPdfAsync(fixture.Path, CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal(replacement, await File.ReadAllBytesAsync(fixture.Path));
    }

    [Fact]
    public async Task ConvertPdfAsync_transient_retry_bounds_persistent_network_errors_and_sanitizes_final_error_and_probes()
    {
        const string privateValue = "PRIVATE_URI_AND_DOCUMENT_SENTINEL_8531";
        var calls = 0;
        var logger = new RetryCaptureLogger();
        using var fixture = new RetryConversionFixture((_, _) =>
        {
            if (++calls > 5) return Task.FromResult(RetrySuccess());
            return Task.FromException<HttpResponseMessage>(new HttpRequestException(privateValue,
                new IOException(privateValue)));
        }, logger: logger);
        using var safetyCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.ConvertPdfAsync(fixture.Path, safetyCancellation.Token));

        Assert.Equal(5, calls);
        Assert.Null(error.InnerException);
        Assert.Null(error.StatusCode);
        Assert.DoesNotContain(privateValue, error.ToString(), StringComparison.Ordinal);
        Assert.False(safetyCancellation.IsCancellationRequested);
        var failedLogs = logger.Events.ToArray();
        Assert.Equal(6, failedLogs.Length); // Five attempt records and one logical conversion.
        Assert.All(failedLogs, record => Assert.DoesNotContain(privateValue, record, StringComparison.Ordinal));
        Assert.Contains(failedLogs, record => record.Contains("retry_limit_reached", StringComparison.Ordinal));
        Assert.Contains(failedLogs, record => record.Contains("attempt_count", StringComparison.Ordinal)
            && record.Contains("retry_count", StringComparison.Ordinal));
        Assert.Contains(failedLogs, record => record.Contains("duration_ms=", StringComparison.Ordinal));
        await fixture.Client.ConvertPdfAsync(fixture.Path, CancellationToken.None);
        Assert.Equal(6, calls); // A terminal transport failure released the capacity slot.
    }

    [Theory]
    [InlineData(400, "{\"detail\":\"Docling upstream conversion failed.\"}")]
    [InlineData(401, "{\"detail\":\"Docling upstream conversion failed.\"}")]
    [InlineData(409, "{\"detail\":\"Docling upstream conversion failed.\"}")]
    [InlineData(413, "{\"detail\":\"Docling upstream conversion failed.\"}")]
    [InlineData(502, "{\"detail\":\"Docling returned invalid JSON.\"}")]
    [InlineData(502, "{\"detail\":\"Docling returned an invalid response envelope.\"}")]
    [InlineData(502, "not JSON")]
    [InlineData(502, "{\"detail\":\"Docling upstream conversion failed. \"}")]
    [InlineData(502, "{\"detail\":[\"Docling upstream conversion failed.\"]}")]
    [InlineData(502, "{\"detail\":null}")]
    [InlineData(502, "{\"detail\":502}")]
    [InlineData(502, "[]")]
    [InlineData(502, "{\"detail\":\"Docling upstream conversion failed.\",\"detail\":\"Docling upstream conversion failed.\"}")]
    public async Task ConvertPdfAsync_transient_retry_never_retries_permanent_or_unclassified_gateway_errors(int status, string body)
    {
        var calls = 0;
        using var fixture = new RetryConversionFixture((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        });

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.ConvertPdfAsync(fixture.Path, CancellationToken.None));

        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Equal(1, calls);
        Assert.DoesNotContain(body, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("envelope")]
    [InlineData("schema")]
    public async Task ConvertPdfAsync_transient_retry_does_not_retry_invalid_success_responses(string kind)
    {
        var response = BuildResponse();
        if (kind == "schema") response.Document.JsonContent!.SchemaName = "UnsupportedDocument";
        var body = kind == "json" ? "not JSON" : kind == "envelope" ? "[]" : JsonSerializer.Serialize(response);
        var calls = 0;
        using var fixture = new RetryConversionFixture((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Client.ConvertPdfAsync(fixture.Path, CancellationToken.None));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ConvertPdfAsync_transient_retry_has_four_retries_and_bounded_backoff_when_dependency_stays_unavailable()
    {
        var calls = 0;
        var attemptsAt = new List<TimeSpan>();
        var timer = Stopwatch.StartNew();
        using var fixture = new RetryConversionFixture((_, _) =>
        {
            attemptsAt.Add(timer.Elapsed);
            calls++;
            return Task.FromResult(RetryFailure(503));
        });
        using var safetyCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.ConvertPdfAsync(fixture.Path, safetyCancellation.Token));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Equal(5, calls);
        Assert.False(safetyCancellation.IsCancellationRequested);
        Assert.InRange(timer.Elapsed.TotalSeconds, 14, 24);
        for (var retry = 1; retry < attemptsAt.Count; retry++)
        {
            var delay = (attemptsAt[retry] - attemptsAt[retry - 1]).TotalSeconds;
            Assert.True(delay >= Math.Pow(2, retry - 1) * .9, "A transient retry must wait for its bounded exponential backoff.");
        }
    }

    [Fact]
    public async Task ConvertPdfAsync_transient_retry_preserves_caller_cancellation_during_backoff()
    {
        var calls = 0;
        using var cancellation = new CancellationTokenSource();
        using var fixture = new RetryConversionFixture((_, _) =>
        {
            if (++calls == 1)
            {
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(150));
                return Task.FromResult(RetryFailure(502));
            }
            return Task.FromResult(RetrySuccess());
        });
        var timer = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Client.ConvertPdfAsync(fixture.Path, cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, calls);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3));
        await fixture.Client.ConvertPdfAsync(fixture.Path, CancellationToken.None);
        Assert.Equal(2, calls); // Cancellation during backoff also released the capacity slot.
    }

    [Fact]
    public async Task ConvertPdfAsync_transient_retry_preserves_cancellation_during_a_replayed_http_request()
    {
        var calls = 0;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var fixture = new RetryConversionFixture(async (_, token) =>
        {
            if (++calls == 1) return RetryFailure(503);
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("The replayed request must observe caller cancellation.");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Client.ConvertPdfAsync(fixture.Path, cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ConvertPdfAsync_transient_retry_uses_one_global_options_deadline_including_previous_attempts()
    {
        var calls = 0;
        // ResolveTimeout deliberately clamps to 30 seconds. This witness exercises that real limit,
        // rather than pretending TimeoutSeconds=1 would create a one-second production deadline.
        using var safetyCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(39));
        using var fixture = new RetryConversionFixture(async (_, token) =>
        {
            if (++calls == 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(16), token);
                return RetryFailure(503);
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("The single global deadline must cancel the replayed request.");
        }, timeoutSeconds: 30);
        var timer = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Client.ConvertPdfAsync(fixture.Path, safetyCancellation.Token));

        Assert.Equal(2, calls);
        Assert.False(safetyCancellation.IsCancellationRequested);
        Assert.InRange(timer.Elapsed.TotalSeconds, 28, 36);
    }

    private static HttpResponseMessage RetrySuccess()
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(BuildResponse()), Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage RetryFailure(int status)
        => new((HttpStatusCode)status)
        {
            Content = new StringContent("{\"detail\":\"Docling upstream conversion failed.\"}", Encoding.UTF8, "application/json")
        };

    private sealed class RetryConversionFixture : IDisposable
    {
        internal static readonly byte[] SourceBytes = Encoding.UTF8.GetBytes("%PDF-1.7\nsynthetic multipart replay witness\n%%EOF");
        internal string Path { get; } = System.IO.Path.GetTempFileName();
        internal DoclingClient Client { get; }

        internal RetryConversionFixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder,
            int timeoutSeconds = 30, ILogger<DoclingClient>? logger = null)
        {
            File.WriteAllBytes(Path, SourceBytes);
            Client = new DoclingClient(new RetryHttpClientFactory(responder), new DocumentIntelligenceOptions { TimeoutSeconds = timeoutSeconds }, logger);
        }

        public void Dispose() => File.Delete(Path);
    }

    private sealed class RetryHttpClientFactory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new RetryHttpHandler(responder))
        {
            BaseAddress = new Uri("http://docling.test/"),
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private sealed class RetryHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }

    private sealed class RetryCaptureLogger : ILogger<DoclingClient>
    {
        internal List<string> Events { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Events.Add(formatter(state, exception));
    }
}
