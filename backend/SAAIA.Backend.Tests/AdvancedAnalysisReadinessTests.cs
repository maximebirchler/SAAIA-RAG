using System.Net;
using System.Text;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SAAIA.Backend.AdvancedAnalysis;
using SAAIA.Backend.Endpoints;
using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class AdvancedAnalysisReadinessTests
{
    [Fact]
    public void Cached_readiness_keeps_sample_identity_but_refreshes_the_current_request_envelope()
    {
        var sampled = DateTimeOffset.UtcNow.AddSeconds(-7);
        var details = new Dictionary<string, object?> { ["dependency_sampled_at_utc"] = sampled };
        var original = new ReadyEndpoints.ReadinessPayload(true, sampled, "previous-request", details);
        var now = sampled.AddSeconds(7);
        var refreshed = ReadyEndpoints.RefreshIdentity(original, "current-request", now);
        Assert.Equal("current-request", refreshed.RequestId);
        Assert.Equal(now, refreshed.Ts);
        Assert.Equal(sampled, refreshed.Details["dependency_sampled_at_utc"]);
        Assert.Equal("previous-request", original.RequestId);
    }

    [Fact]
    public void Required_advanced_worker_is_not_ready_until_a_real_worker_heartbeat_exists()
    {
        var state = new AdvancedAnalysisRuntimeState();
        var details = new Dictionary<string, object?> { ["advanced_analysis_required"] = true, ["advanced_analysis_provider_ready"] = true };
        var options = new AdvancedAnalysisOptions { WorkerEnabled = true };
        Assert.False(AdvancedAnalysisReadiness.ApplyRuntime(options, state, details));
        state.WorkerHeartbeat("executing");
        Assert.True(AdvancedAnalysisReadiness.ApplyRuntime(options, state, details));
        state.WorkerHeartbeat("stopped");
        Assert.False(AdvancedAnalysisReadiness.ApplyRuntime(options, state, details));
    }

    [Theory]
    [InlineData("{\"data\":[{\"id\":\"expected\"}]}", true)]
    [InlineData("{\"data\":[{\"id\":\"another\"}]}", false)]
    [InlineData("{\"data\":[\"invalid-item\"]}", false)]
    public async Task Internal_readiness_checks_the_configured_advanced_model_without_generation(string catalog, bool expected)
    {
        var options = new AdvancedAnalysisOptions { WorkerEnabled = true, LlmBaseUrl = "http://127.0.0.1:1234/v1", LlmModel = "expected" };
        var factory = new CatalogFactory(catalog);
        var details = new Dictionary<string, object?>();
        await AdvancedAnalysisReadiness.ProbeProviderAsync(options, new LicenseOptions { AdvancedAnalysisEnabled = true },
            new CatalogProvider(), factory, new TestEnvironment(), details, CancellationToken.None);
        Assert.Equal(expected, details["advanced_analysis_provider_ready"]);
        Assert.Equal("/v1/models", Assert.Single(factory.Requests).AbsolutePath);
        Assert.Equal("model_catalog_no_generation", details["advanced_analysis_probe_scope"]);
    }

    private sealed class CatalogFactory(string catalog) : IHttpClientFactory
    {
        internal List<Uri> Requests { get; } = [];
        public HttpClient CreateClient(string name) => new(new Handler(catalog, Requests));
        private sealed class Handler(string catalog, List<Uri> requests) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Assert.Equal(HttpMethod.Get, request.Method); requests.Add(request.RequestUri!);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(catalog, Encoding.UTF8, "application/json") });
            }
        }
    }

    private sealed class CatalogProvider : IAdvancedAnalysisProvider
    {
        public string ProviderKey => "customer-server";
        public string ModelId => "expected";
        public AdvancedAnalysisProviderLocation Location => AdvancedAnalysisProviderLocation.Internal;
        public Task<AdvancedAnalysisProviderResult> ExecuteAsync(AdvancedAnalysisProviderRequest request, IAdvancedAnalysisToolGateway tools, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Readiness must not generate content.");
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "DiagnosticTests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
