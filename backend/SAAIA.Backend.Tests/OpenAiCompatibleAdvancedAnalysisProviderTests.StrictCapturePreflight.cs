using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SAAIA.Backend.AdvancedAnalysis;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Fact]
    public async Task StrictCapturePreflight_trace_disabled_keeps_opt_in_capture_semantics()
    {
        var options = CreateOptions();
        options.DevelopmentTraceStrict = true;
        options.DevelopmentTraceDirectory = string.Empty;
        var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();
        using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[]}"""), Completion(DiagnosticAnswer));
        var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, logger)
            .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(BuildEvidence("E1", "Le seuil est 17 bar.")), CancellationToken.None);
        Assert.Equal("answered", result.Outcome);
        Assert.Equal(2, factory.Requests.Count);
        Assert.DoesNotContain(logger.Events, entry => Equals(entry.Values.GetValueOrDefault("Operation"), "llm.exact_capture_preflight"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StrictCapturePreflight_changed_sink_after_configuration_blocks_paid_http_and_reservation_only_when_strict(bool strict)
    {
        var root = CapturePreflightRoot();
        var sink = Path.Combine(root, "sink");
        Directory.CreateDirectory(sink);
        try
        {
            var options = CapturePreflightOptions(root, sink, strict, external: true);
            var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();
            using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[]}"""), Completion(DiagnosticAnswer));
            var gateway = new CapturePreflightGateway(() =>
            {
                Directory.Delete(sink);
                File.WriteAllText(sink, "synthetic blocker");
            });
            var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, "synthetic-secret", logger);
            if (strict)
            {
                var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
                    provider.ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None));
                Assert.Equal("advanced_development_trace_write_failed", error.ErrorCode);
                Assert.Empty(factory.Requests);
                Assert.Empty(CapturePreflightLines(options.ExternalUsageLedgerPath));
                Assert.Empty(CapturePreflightLines(options.ExternalUsageLedgerPath + ".reservations.jsonl"));
                var rejected = Assert.Single(logger.Events, entry => Equals(entry.Values.GetValueOrDefault("Operation"), "llm.exact_capture_preflight"));
                Assert.Equal("rejected", rejected.Values["Outcome"]);
                Assert.Equal("advanced_development_trace_write_failed", rejected.Values["ReasonCode"]);
                using var measurements = JsonDocument.Parse(rejected.Values["Measurements"]!.ToString()!);
                Assert.Equal(0, measurements.RootElement.GetProperty("provider_http_attempts").GetInt32());
                Assert.False(measurements.RootElement.GetProperty("budget_reservation_created").GetBoolean());
                Assert.Equal(0, measurements.RootElement.GetProperty("probe_bytes").GetInt32());
                Assert.DoesNotContain(sink, rejected.Text);
            }
            else
            {
                var result = await provider.ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None);
                Assert.Equal("answered", result.Outcome);
                Assert.Equal(2, factory.Requests.Count);
                Assert.Equal(2, CapturePreflightLines(options.ExternalUsageLedgerPath).Length);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task StrictCapturePreflight_is_rechecked_before_writer_and_preserves_only_the_completed_planner_charge()
    {
        var root = CapturePreflightRoot();
        var sink = Path.Combine(root, "sink");
        var saved = Path.Combine(root, "saved-planner-trace");
        try
        {
            var options = CapturePreflightOptions(root, sink, strict: true, external: true);
            using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[{"query":"seuil","topK":12}]}"""), Completion(DiagnosticAnswer));
            var gateway = new CapturePreflightGateway(onSearch: () =>
            {
                Directory.Move(sink, saved);
                File.WriteAllText(sink, "synthetic blocker");
            });
            var error = await Assert.ThrowsAsync<AdvancedAnalysisProviderException>(() =>
                new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, "synthetic-secret")
                    .ExecuteAsync(BuildDirectRequest(), gateway, CancellationToken.None));
            Assert.Equal("advanced_development_trace_write_failed", error.ErrorCode);
            Assert.Single(factory.Requests);
            Assert.Single(Directory.GetFiles(saved, "*.json"));
            Assert.Single(CapturePreflightLines(options.ExternalUsageLedgerPath));
            Assert.Single(CapturePreflightLines(options.ExternalUsageLedgerPath + ".reservations.jsonl"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task StrictCapturePreflight_valid_sink_preserves_exact_traces_and_removes_only_its_probe_files()
    {
        var root = CapturePreflightRoot();
        var sink = Path.Combine(root, "sink");
        Directory.CreateDirectory(sink);
        const string foreign = "unrelated-owner-file.txt";
        File.WriteAllText(Path.Combine(sink, foreign), "keep");
        try
        {
            var options = CapturePreflightOptions(root, sink, strict: true);
            var state = new AdvancedAnalysisRuntimeState();
            var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();
            using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[]}"""), Completion(DiagnosticAnswer));
            var result = await new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, "synthetic-secret", logger, state)
                .ExecuteAsync(BuildDirectRequest(), new RecordingToolGateway(BuildEvidence("E1", "PRIVATE_SYNTHETIC_DOCUMENT_TEXT")), CancellationToken.None);
            Assert.Equal("answered", result.Outcome);
            Assert.Equal(2, factory.Requests.Count);
            var captures = Directory.GetFiles(sink, "*.json");
            Assert.Equal(2, captures.Length);
            Assert.Equal(3, Directory.GetFiles(sink).Length);
            Assert.Equal("keep", File.ReadAllText(Path.Combine(sink, foreign)));
            Assert.Empty(Directory.GetFiles(sink, ".saaia-exact-sink-probe-*.tmp"));
            foreach (var path in captures)
            {
                using var trace = JsonDocument.Parse(File.ReadAllText(path));
                Assert.Contains(factory.Requests, request => request.Body == trace.RootElement.GetProperty("requestJson").GetString());
            }
            var health = state.GetSnapshot(TimeSpan.FromSeconds(30));
            Assert.Equal(2, health.CapturesSucceeded);
            Assert.Equal(0, health.CapturesFailed);
            var probes = logger.Events.Where(entry => Equals(entry.Values.GetValueOrDefault("Operation"), "llm.exact_capture_preflight")).ToArray();
            Assert.Equal(2, probes.Length);
            foreach (var probe in probes)
            {
                using var measurements = JsonDocument.Parse(probe.Values["Measurements"]!.ToString()!);
                Assert.Equal(1, measurements.RootElement.GetProperty("probe_bytes").GetInt32());
                Assert.True(measurements.RootElement.GetProperty("probe_file_removed").GetBoolean());
                Assert.Equal(0, measurements.RootElement.GetProperty("provider_http_attempts").GetInt32());
                Assert.False(measurements.RootElement.GetProperty("budget_reservation_created").GetBoolean());
                Assert.DoesNotContain(sink, probe.Text);
                Assert.DoesNotContain("PRIVATE_", probe.Text);
                Assert.DoesNotContain("synthetic-secret", probe.Text);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task StrictCapturePreflight_readiness_fails_without_http_and_recovers_after_local_sink_repair_without_generation()
    {
        var root = CapturePreflightRoot();
        var sink = Path.Combine(root, "sink");
        File.WriteAllText(sink, "synthetic blocker");
        try
        {
            var options = CapturePreflightOptions(root, sink, strict: true);
            options.WorkerEnabled = true;
            var state = new AdvancedAnalysisRuntimeState();
            state.WorkerHeartbeat("idle");
            var factory = new CapturePreflightCatalog(options.LlmModel);
            var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, runtimeState: state);
            var details = new Dictionary<string, object?>();
            var license = new LicenseOptions { AdvancedAnalysisEnabled = true };
            var environment = new CapturePreflightEnvironment(root);
            await AdvancedAnalysisReadiness.ProbeProviderAsync(options, license, provider, factory, environment, details, CancellationToken.None);
            Assert.False(AdvancedAnalysisReadiness.ApplyRuntime(options, state, details));
            Assert.Equal("capture_sink_unavailable", details["advanced_analysis_provider_status"]);
            Assert.Equal(false, details["advanced_analysis_capture_sink_ready"]);
            Assert.Empty(factory.Requests);

            File.Delete(sink);
            Directory.CreateDirectory(sink);
            details = new Dictionary<string, object?>();
            await AdvancedAnalysisReadiness.ProbeProviderAsync(options, license, provider, factory, environment, details, CancellationToken.None);
            Assert.True(AdvancedAnalysisReadiness.ApplyRuntime(options, state, details));
            Assert.Equal(true, details["advanced_analysis_capture_sink_ready"]);
            Assert.Equal("/v1/models", Assert.Single(factory.Requests).AbsolutePath);
            Assert.Empty(Directory.GetFiles(sink));
            Assert.Equal(0, state.GetSnapshot(TimeSpan.FromSeconds(30)).CapturesSucceeded);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task StrictCapturePreflight_cancellation_while_waiting_does_not_release_an_unowned_gate_or_reserve_a_call()
    {
        var root = CapturePreflightRoot();
        var sink = Path.Combine(root, "sink");
        try
        {
            var options = CapturePreflightOptions(root, sink, strict: true, external: true);
            var logger = new DiagnosticRecordingLogger<OpenAiCompatibleAdvancedAnalysisProvider>();
            using var factory = new QueuedHttpClientFactory(Completion("""{"queries":[]}"""), Completion(DiagnosticAnswer));
            var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, "synthetic-secret", logger);
            var gate = (SemaphoreSlim)typeof(OpenAiCompatibleAdvancedAnalysisProvider)
                .GetField("_traceWriteGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(provider)!;
            await gate.WaitAsync();
            using var cancellation = new CancellationTokenSource();
            var pending = provider.ExecuteAsync(BuildDirectRequest(), new CapturePreflightGateway(), cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(0, gate.CurrentCount);
            Assert.Empty(factory.Requests);
            Assert.Empty(CapturePreflightLines(options.ExternalUsageLedgerPath));
            Assert.Empty(CapturePreflightLines(options.ExternalUsageLedgerPath + ".reservations.jsonl"));
            Assert.Empty(Directory.GetFiles(sink));
            var cancelled = Assert.Single(logger.Events, entry => Equals(entry.Values.GetValueOrDefault("Operation"), "llm.exact_capture_preflight"));
            Assert.Equal("cancelled", cancelled.Values["Outcome"]);
            using (var measurements = JsonDocument.Parse(cancelled.Values["Measurements"]!.ToString()!))
            {
                Assert.Equal(0, measurements.RootElement.GetProperty("probe_bytes").GetInt32());
                Assert.False(measurements.RootElement.GetProperty("probe_file_created").GetBoolean());
                Assert.False(measurements.RootElement.GetProperty("budget_reservation_created").GetBoolean());
            }
            gate.Release();
            var result = await provider.ExecuteAsync(BuildDirectRequest(), new CapturePreflightGateway(), CancellationToken.None);
            Assert.Equal("answered", result.Outcome);
            Assert.Equal(2, factory.Requests.Count);
            Assert.Equal(1, gate.CurrentCount);
            Assert.Empty(Directory.GetFiles(sink, ".saaia-exact-sink-probe-*.tmp"));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string CapturePreflightRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "saaia-capture-preflight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static AdvancedAnalysisOptions CapturePreflightOptions(string root, string sink, bool strict, bool external = false)
    {
        var options = CreateOptions();
        options.DevelopmentTraceDirectory = sink;
        options.DevelopmentTraceStrict = strict;
        options.ExternalUsageLedgerPath = Path.Combine(root, "ledger", "usage.jsonl");
        if (external)
        {
            options.Provider = "openai-dev";
            options.LlmLocation = "external-service";
            options.LlmBaseUrl = "https://api.openai.com/v1";
            options.LlmModel = "gpt-5.6-luna";
        }
        return options;
    }

    private static string[] CapturePreflightLines(string path) => File.Exists(path) ? File.ReadAllLines(path) : [];

    private sealed class CapturePreflightGateway(Action? onList = null, Action? onSearch = null) : IAdvancedAnalysisToolGateway
    {
        private readonly RecordingToolGateway _inner = new(BuildEvidence("E1", "Le seuil est 17 bar."));
        public IReadOnlyList<AdvancedAnalysisResolvedEvidence> Evidence => _inner.Evidence;
        public Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onList?.Invoke();
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
        public Task<AdvancedAnalysisSearchObservation> SearchAsync(AdvancedAnalysisSearchRequest request, CancellationToken cancellationToken)
        {
            onSearch?.Invoke();
            return _inner.SearchAsync(request, cancellationToken);
        }
    }

    private sealed class CapturePreflightCatalog(string model) : IHttpClientFactory
    {
        public List<Uri> Requests { get; } = [];
        public HttpClient CreateClient(string name) => new(new Handler(model, Requests));
        private sealed class Handler(string model, List<Uri> requests) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                requests.Add(request.RequestUri!);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(new { data = new[] { new { id = model } } }), Encoding.UTF8, "application/json") });
            }
        }
    }

    private sealed class CapturePreflightEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "CapturePreflightTests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
