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
                var promptValue = metricsCalls == 1 ? 20 : 23;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"""
                        llamacpp_tokens_predicted_total {value}
                        llamacpp_prompt_tokens_total {promptValue}
                        llamacpp_kv_cache_used_bytes 1048576
                        llamacpp_kv_cache_used_cells 128
                        llamacpp_kv_cache_total_cells 512
                        llamacpp_threads 5
                        llamacpp_threads_batch 3

                        """)
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
        Assert.Equal(14, measurement.RuntimeMetrics["runtime.tokens_predicted_total"]);
        Assert.Equal(4, measurement.RuntimeMetrics["runtime.tokens_predicted_delta"]);
        Assert.Equal(23, measurement.RuntimeMetrics["runtime.prompt_tokens_total"]);
        Assert.Equal(3, measurement.RuntimeMetrics["runtime.prompt_tokens_delta"]);
        Assert.Equal(1, measurement.RuntimeMetrics["runtime.kv_cache_used_mib"]);
        Assert.Equal(25, measurement.RuntimeMetrics["runtime.kv_cache_used_percent"]);
        Assert.Equal(5, measurement.RuntimeMetrics["runtime.threads"]);
        Assert.Equal(3, measurement.RuntimeMetrics["runtime.threads_batch"]);
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

    [Fact]
    public async Task RunOnceAsync_maps_llamacpp_metric_aliases_to_canonical_runtime_metrics()
    {
        var metricsCalls = 0;
        var handler = new StubHttpHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.OK);

            if (req.RequestUri.AbsolutePath.EndsWith("/metrics", StringComparison.OrdinalIgnoreCase))
            {
                metricsCalls++;
                var predicted = metricsCalls == 1 ? 30 : 42;
                var processed = metricsCalls == 1 ? 100 : 116;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"""
                        llamacpp_decode_tokens_total {predicted}
                        llamacpp_tokens_processed_total {processed}
                        llamacpp_kv_cache_tokens 64
                        llamacpp_kv_cache_cell_max 256
                        llamacpp_server_threads 4
                        llamacpp_server_threads_batch 2
                        llamacpp_server_slots_processing 1

                        """)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"content\":\"Reponse stable.\"}}]}",
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
        Assert.NotNull(measurement.RuntimeMetrics);
        Assert.Equal(42, measurement.RuntimeMetrics!["runtime.tokens_predicted_total"]);
        Assert.Equal(12, measurement.RuntimeMetrics["runtime.tokens_predicted_delta"]);
        Assert.Equal(116, measurement.RuntimeMetrics["runtime.prompt_tokens_total"]);
        Assert.Equal(16, measurement.RuntimeMetrics["runtime.prompt_tokens_delta"]);
        Assert.Equal(64, measurement.RuntimeMetrics["runtime.kv_cache_used_cells"]);
        Assert.Equal(256, measurement.RuntimeMetrics["runtime.kv_cache_total_cells"]);
        Assert.Equal(25, measurement.RuntimeMetrics["runtime.kv_cache_used_percent"]);
        Assert.Equal(4, measurement.RuntimeMetrics["runtime.threads"]);
        Assert.Equal(2, measurement.RuntimeMetrics["runtime.threads_batch"]);
        Assert.Equal(1, measurement.RuntimeMetrics["runtime.slots_processing"]);
    }

    [Fact]
    public async Task RunContractScenariosAsync_runs_all_contract_prompts_and_aggregates_worst_case()
    {
        var chatCalls = 0;
        var requestBodies = new List<string>();
        var handler = new StubHttpHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.OK);

            if (req.RequestUri.AbsolutePath.EndsWith("/metrics", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            Assert.EndsWith("/chat/completions", req.RequestUri.AbsolutePath);
            chatCalls++;
            requestBodies.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"content\":\"token suite stable\"}}]}",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var harness = new LocalLlmWarmupHarness(new HttpClient(handler));

        var runs = await harness.RunContractScenariosAsync(
            "http://127.0.0.1:1234/v1",
            "local",
            baseOptions: new LocalLlmWarmupHarnessOptions(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(1),
                "unused",
                16,
                0,
                TryReadRuntimeMetrics: false));

        Assert.Equal(3, runs.Count);
        Assert.Equal(3, chatCalls);
        Assert.Equal(new[] { "short_ttft", "long_prefill", "decode_stable" }, runs.Select(run => run.Scenario));
        Assert.Contains(requestBodies, body => body.Contains("Contexte SAAIA", StringComparison.Ordinal));
        Assert.Contains(requestBodies, body => body.Contains("huit points courts", StringComparison.Ordinal));
        Assert.All(runs, run => Assert.True(run.Succeeded));

        var aggregate = LocalLlmWarmupHarness.AggregateScenarioMeasurements(runs);

        Assert.True(aggregate.Succeeded);
        Assert.Equal("contract_suite", aggregate.Scenario);
        Assert.NotNull(aggregate.RuntimeMetrics);
        Assert.Contains("scenario.short_ttft.ttft_ms", aggregate.RuntimeMetrics!.Keys);
        Assert.Contains("scenario.long_prefill.tok_per_sec", aggregate.RuntimeMetrics.Keys);
        Assert.Contains("scenario.decode_stable.ms_per_token", aggregate.RuntimeMetrics.Keys);
    }

    [Fact]
    public void AggregateScenarioMeasurements_fails_if_one_contract_prompt_fails()
    {
        var aggregate = LocalLlmWarmupHarness.AggregateScenarioMeasurements(new[]
        {
            new WarmupMeasurement(10, 20, 8, Scenario: "short_ttft"),
            new WarmupMeasurement(10, 0, 0, Succeeded: false, Error: "no_tokens", Scenario: "decode_stable")
        });

        Assert.False(aggregate.Succeeded);
        Assert.Equal("contract_suite", aggregate.Scenario);
        Assert.Equal(0, aggregate.TokPerSec);
        Assert.Contains("decode_stable:no_tokens", aggregate.Error);
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
