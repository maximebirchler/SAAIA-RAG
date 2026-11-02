using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Backend.Endpoints;
using SAAIA.Backend.Middleware;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed partial class OpenAiCompatibleAdvancedAnalysisProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StrictCapturePreflight_cached_ready_rechecks_sink_loss_and_repair_without_waiting_for_dependency_cache(bool secondCacheBranch)
    {
        var root = CapturePreflightRoot();
        var sink = Path.Combine(root, "sink");
        try
        {
            var options = CapturePreflightOptions(root, sink, strict: true);
            options.WorkerEnabled = true;
            var state = new AdvancedAnalysisRuntimeState();
            state.WorkerHeartbeat("idle");
            var factory = new CapturePreflightCatalog(options.LlmModel);
            var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, runtimeState: state);
            using var scenario = new CachedCaptureReadinessScenario(root, options, state, provider, factory);
            var sampled = DateTimeOffset.UtcNow.AddSeconds(-7);
            var details = await scenario.ProviderSampleAsync();
            Assert.True(AdvancedAnalysisReadiness.ApplyRuntime(options, state, details));
            details["dependency_sampled_at_utc"] = sampled;
            details["advanced_analysis_provider_sampled_at_utc"] = sampled;
            details["advanced_analysis_provider_error_type"] = "OldError";
            var original = new Dictionary<string, object?>(details);
            scenario.Seed(details, dependenciesOk: true, payloadOk: true);

            var healthy = await scenario.RequestAsync();
            Assert.Equal(200, healthy.Status);
            Directory.Delete(sink, recursive: true);
            File.WriteAllText(sink, "synthetic blocker");
            var requestsBeforeBlocked = factory.Requests.Count;
            if (secondCacheBranch) scenario.Clear();

            var blocked = await scenario.RequestAsync(secondCacheBranch, details, dependenciesOk: true, payloadOk: true);
            Assert.Equal(503, blocked.Status);
            Assert.False(blocked.Payload.Ok);
            Assert.Equal(false, blocked.Payload.Details["advanced_analysis_capture_sink_ready"]);
            Assert.Equal("capture_sink_unavailable", blocked.Payload.Details["advanced_analysis_provider_status"]);
            Assert.Equal("capture_sink_no_generation", blocked.Payload.Details["advanced_analysis_probe_scope"]);
            Assert.False(blocked.Payload.Details.ContainsKey("advanced_analysis_provider_http_status"));
            Assert.False(blocked.Payload.Details.ContainsKey("advanced_analysis_provider_error_type"));
            Assert.Equal(requestsBeforeBlocked, factory.Requests.Count);
            Assert.Equal(original, details);
            var blockedOriginal = new Dictionary<string, object?>(blocked.Payload.Details);
            scenario.Seed(blocked.Payload.Details, dependenciesOk: true, payloadOk: false);

            File.Delete(sink);
            Directory.CreateDirectory(sink);
            var repaired = await scenario.RequestAsync();
            Assert.Equal(200, repaired.Status);
            Assert.True(repaired.Payload.Ok);
            Assert.Equal(true, repaired.Payload.Details["advanced_analysis_capture_sink_ready"]);
            Assert.Equal("model_catalog_no_generation", repaired.Payload.Details["advanced_analysis_probe_scope"]);
            Assert.Equal(requestsBeforeBlocked + 1, factory.Requests.Count);
            Assert.Equal(sampled, blocked.Payload.Details["dependency_sampled_at_utc"]);
            Assert.Equal(sampled, repaired.Payload.Details["dependency_sampled_at_utc"]);
            Assert.NotEqual(sampled, repaired.Payload.Details["advanced_analysis_provider_sampled_at_utc"]);
            Assert.Equal(original, details);
            Assert.Equal(blockedOriginal, blocked.Payload.Details);
            Assert.Empty(Directory.GetFiles(sink));
            Assert.Empty(CapturePreflightLines(options.ExternalUsageLedgerPath));
            Assert.Empty(CapturePreflightLines(options.ExternalUsageLedgerPath + ".reservations.jsonl"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task StrictCapturePreflight_cached_dependency_failure_stays_failed_when_the_provider_is_ready()
    {
        var root = CapturePreflightRoot();
        try
        {
            var options = CapturePreflightOptions(root, Path.Combine(root, "sink"), strict: true);
            options.WorkerEnabled = true;
            var state = new AdvancedAnalysisRuntimeState();
            state.WorkerHeartbeat("idle");
            var factory = new CapturePreflightCatalog(options.LlmModel);
            var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, runtimeState: state);
            using var scenario = new CachedCaptureReadinessScenario(root, options, state, provider, factory);
            var details = await scenario.ProviderSampleAsync();
            Assert.True(AdvancedAnalysisReadiness.ApplyRuntime(options, state, details));
            var sampled = DateTimeOffset.UtcNow.AddSeconds(-7);
            details["dependency_sampled_at_utc"] = sampled;
            details["db"] = false;
            var original = new Dictionary<string, object?>(details);
            scenario.Seed(details, dependenciesOk: false, payloadOk: false);
            var result = await scenario.RequestAsync();
            Assert.Equal(503, result.Status);
            Assert.False(result.Payload.Ok);
            Assert.Equal(true, result.Payload.Details["advanced_analysis_ready"]);
            Assert.Equal(false, result.Payload.Details["db"]);
            Assert.Equal(sampled, result.Payload.Details["dependency_sampled_at_utc"]);
            Assert.Equal(original, details);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task StrictCapturePreflight_non_strict_cached_readiness_keeps_provider_sample_and_issues_no_http()
    {
        var root = CapturePreflightRoot();
        try
        {
            var options = CapturePreflightOptions(root, Path.Combine(root, "sink"), strict: false);
            options.WorkerEnabled = true;
            var state = new AdvancedAnalysisRuntimeState();
            state.WorkerHeartbeat("idle");
            var factory = new CapturePreflightCatalog(options.LlmModel);
            var provider = new OpenAiCompatibleAdvancedAnalysisProvider(factory, options, null, runtimeState: state);
            using var scenario = new CachedCaptureReadinessScenario(root, options, state, provider, factory);
            var sampled = DateTimeOffset.UtcNow.AddSeconds(-7);
            var details = new Dictionary<string, object?>
            {
                ["advanced_analysis_required"] = true,
                ["advanced_analysis_provider_ready"] = true,
                ["advanced_analysis_provider_sampled_at_utc"] = sampled,
                ["dependency_sampled_at_utc"] = sampled
            };
            var original = new Dictionary<string, object?>(details);
            scenario.Seed(details, dependenciesOk: true, payloadOk: true);
            var result = await scenario.RequestAsync();
            Assert.Equal(200, result.Status);
            Assert.Empty(factory.Requests);
            Assert.Equal(sampled, result.Payload.Details["advanced_analysis_provider_sampled_at_utc"]);
            Assert.Equal(sampled, result.Payload.Details["dependency_sampled_at_utc"]);
            Assert.Equal(original, details);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class CachedCaptureReadinessScenario : IDisposable
    {
        private static readonly Type SnapshotType = typeof(ReadyEndpoints).GetNestedType("ReadinessSnapshot", BindingFlags.NonPublic)!;
        private static readonly MethodInfo Handle = typeof(ReadyEndpoints).GetMethod("HandleAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly string CacheKey = (string)typeof(ReadyEndpoints).GetField("ReadyCacheKey", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        private readonly MemoryCache _cache = new(new MemoryCacheOptions());
        private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();
        private readonly CapturePreflightEnvironment _environment;
        private readonly AdvancedAnalysisOptions _options;
        private readonly AdvancedAnalysisRuntimeState _state;
        private readonly IAdvancedAnalysisProvider _provider;
        private readonly IHttpClientFactory _factory;
        private readonly RagSearchBulkhead _bulkhead = new(Options.Create(new RagOptions()), NullLogger<RagSearchBulkhead>.Instance);
        private readonly TeiWorkloadGovernor _governor = new();
        private int _request;

        internal CachedCaptureReadinessScenario(string root, AdvancedAnalysisOptions options, AdvancedAnalysisRuntimeState state,
            IAdvancedAnalysisProvider provider, IHttpClientFactory factory)
        {
            _environment = new CapturePreflightEnvironment(root);
            _options = options;
            _state = state;
            _provider = provider;
            _factory = factory;
        }

        internal async Task<Dictionary<string, object?>> ProviderSampleAsync()
        {
            var details = new Dictionary<string, object?>();
            await AdvancedAnalysisReadiness.ProbeProviderAsync(_options, new LicenseOptions { AdvancedAnalysisEnabled = true },
                _provider, _factory, _environment, details, CancellationToken.None);
            return details;
        }

        internal void Seed(Dictionary<string, object?> details, bool dependenciesOk, bool payloadOk)
        {
            var payload = new ReadyEndpoints.ReadinessPayload(payloadOk, DateTimeOffset.UtcNow.AddSeconds(-7), "cached-request", details);
            var snapshot = Activator.CreateInstance(SnapshotType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, args: [dependenciesOk, payload], culture: null)!;
            _cache.Set(CacheKey, snapshot, TimeSpan.FromMinutes(1));
        }

        internal void Clear() => _cache.Remove(CacheKey);

        internal async Task<(int Status, ReadyEndpoints.ReadinessPayload Payload)> RequestAsync(bool secondCacheBranch = false,
            Dictionary<string, object?>? seed = null, bool dependenciesOk = true, bool payloadOk = true)
        {
            Task<IResult> pending;
            if (secondCacheBranch)
            {
                var gate = (SemaphoreSlim)typeof(ReadyEndpoints).GetField("_gate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                await gate.WaitAsync();
                try
                {
                    pending = Invoke();
                    Assert.False(pending.IsCompleted);
                    Seed(seed!, dependenciesOk, payloadOk);
                }
                finally { gate.Release(); }
            }
            else pending = Invoke();
            var result = await pending;
            return (Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode!.Value,
                Assert.IsType<ReadyEndpoints.ReadinessPayload>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value));
        }

        private Task<IResult> Invoke()
        {
            var context = new DefaultHttpContext { RequestServices = _services };
            context.Items[RequestIdMiddleware.RequestIdItemKey] = "cached-capture-" + ++_request;
            return (Task<IResult>)Handle.Invoke(null,
            [
                context, _environment, null!, _factory, Options.Create(new RagOptions()), Options.Create(new ChatOptions()),
                Options.Create(new IngestionOptions()), Options.Create(new DocumentIntelligenceOptions()), Options.Create(_options),
                Options.Create(new LicenseOptions { AdvancedAnalysisEnabled = true }), Options.Create(new OpenTelemetryOptions()),
                _provider, _state, null!, _cache, _bulkhead, _governor, CancellationToken.None
            ])!;
        }

        public void Dispose()
        {
            _cache.Dispose();
            _services.Dispose();
        }
    }
}
