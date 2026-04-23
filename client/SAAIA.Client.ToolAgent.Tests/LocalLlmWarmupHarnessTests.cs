using System.Net;
using System.Text;
using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LocalLlmWarmupHarnessTests
{
    [Fact]
    public async Task RunOnceAsync_measures_ready_models_and_streaming_first_token()
    {
        var metricsCalls = 0;
        var handler = new StubHttpHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":[{\"id\":\"local\"}]}")
                };
            }

            if (req.RequestUri.AbsolutePath.EndsWith("/metrics", StringComparison.OrdinalIgnoreCase))
            {
                metricsCalls++;
                var value = metricsCalls == 1 ? 10 : 14;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"llamacpp_tokens_predicted_total {value}\nllamacpp_kv_cache_used_bytes 1048576\n")
                };
            }

            Assert.EndsWith("/chat/completions", req.RequestUri.AbsolutePath);
            var stream = "data: {\"choices\":[{\"delta\":{\"content\":\"Bonjour\"}}]}\n\n"
                         + "data: {\"choices\":[{\"delta\":{\"content\":\" SAAIA\"}}]}\n\n"
                         + "data: [DONE]\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(stream, Encoding.UTF8, "text/event-stream")
            };
        });
        var harness = new LocalLlmWarmupHarness(new HttpClient(handler));

        var measurement = await harness.RunOnceAsync(
            "http://127.0.0.1:1234/v1",
            "local",
            new LocalLlmWarmupHarnessOptions(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(1),
                "ping",
                16,
                0));

        Assert.True(measurement.Succeeded);
        Assert.True(measurement.LoadMs >= 0);
        Assert.True(measurement.TtftMs >= 0);
        Assert.True(measurement.TokPerSec > 0);
        Assert.NotNull(measurement.RuntimeMetrics);
        Assert.Equal(14, measurement.RuntimeMetrics!["llamacpp_tokens_predicted_total"]);
        Assert.Equal(4, measurement.RuntimeMetrics["llamacpp_tokens_predicted_total_delta"]);
        Assert.True(measurement.MsPerToken > 0);
    }

    [Fact]
    public async Task RunOnceAsync_returns_failure_when_models_never_becomes_ready()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("Loading model")
        });
        var harness = new LocalLlmWarmupHarness(new HttpClient(handler));

        var measurement = await harness.RunOnceAsync(
            "http://127.0.0.1:1234/v1",
            "local",
            new LocalLlmWarmupHarnessOptions(
                TimeSpan.FromMilliseconds(5),
                TimeSpan.FromMilliseconds(1),
                "ping",
                16,
                0));

        Assert.False(measurement.Succeeded);
        Assert.Equal("models_not_ready", measurement.Error);
    }

    [Fact]
    public async Task RunOnceAsync_handles_non_streaming_json_response()
    {
        var handler = new StubHttpHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.OK);

            if (req.RequestUri.AbsolutePath.EndsWith("/metrics", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"content\":\"Reponse courte stable.\"}}]}",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var harness = new LocalLlmWarmupHarness(new HttpClient(handler));

        var measurement = await harness.RunOnceAsync(
            "http://127.0.0.1:1234/v1",
            "local",
            new LocalLlmWarmupHarnessOptions(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(1),
                "ping",
                16,
                0));

        Assert.True(measurement.Succeeded);
        Assert.True(measurement.TokPerSec > 0);
    }

    [Fact]
    public void ParsePrometheusMetrics_accepts_labels_comments_and_float_values()
    {
        var metrics = LocalLlmWarmupHarness.ParsePrometheusMetrics("""
            # HELP llamacpp metric
            llamacpp_prompt_tokens_total{slot="0"} 12
            llamacpp_decode_ms_sum 42.5
            malformed
            """);

        Assert.Equal(12, metrics["llamacpp_prompt_tokens_total"]);
        Assert.Equal(42.5, metrics["llamacpp_decode_ms_sum"]);
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}
