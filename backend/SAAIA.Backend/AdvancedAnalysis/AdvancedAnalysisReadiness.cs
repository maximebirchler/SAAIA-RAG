using System.Net.Http.Headers;
using System.Text.Json;
using SAAIA.Backend.Security;

namespace SAAIA.Backend.AdvancedAnalysis;

internal static class AdvancedAnalysisReadiness
{
    internal static async Task ProbeProviderAsync(AdvancedAnalysisOptions options, LicenseOptions license,
        IAdvancedAnalysisProvider provider, IHttpClientFactory clients, IHostEnvironment environment,
        Dictionary<string, object?> details, CancellationToken cancellationToken)
    {
        var required = license.AdvancedAnalysisEnabled && options.WorkerEnabled;
        details["advanced_analysis_entitled"] = license.AdvancedAnalysisEnabled;
        details["advanced_analysis_required"] = required;
        details["advanced_analysis_provider"] = provider.ProviderKey;
        details["advanced_analysis_model"] = provider.ModelId;
        details["advanced_analysis_provider_sampled_at_utc"] = DateTimeOffset.UtcNow;
        details["advanced_analysis_provider_ready"] = false;
        if (!required)
        {
            details["advanced_analysis_provider_status"] = "disabled";
            return;
        }
        if (provider.ProviderKey == "disabled" || string.IsNullOrWhiteSpace(provider.ModelId)
            || !Uri.TryCreate(options.LlmBaseUrl, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https"))
        {
            details["advanced_analysis_provider_status"] = "configuration_invalid";
            return;
        }
        if (provider.Location == AdvancedAnalysisProviderLocation.ExternalService)
        {
            // A readiness GET must never transmit corpus or call a billed generation endpoint.
            // External entitlement, budget and model execution are verified at execution time.
            var authorized = options.AllowExternalProviderContent && options.AllowExternalProviderMetadata;
            var key = SecretRefResolver.Resolve(null, options.LlmApiKeyRef, environment.ContentRootPath, out _);
            details["advanced_analysis_provider_ready"] = authorized && !string.IsNullOrWhiteSpace(key);
            details["advanced_analysis_provider_status"] = authorized && !string.IsNullOrWhiteSpace(key)
                ? "configured_not_execution_verified" : "external_authorization_missing";
            details["advanced_analysis_probe_scope"] = "configuration_only";
            return;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var baseUrl = baseUri.AbsoluteUri.TrimEnd('/');
            var modelUri = new Uri(baseUrl + (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? "/models" : "/v1/models"));
            using var request = new HttpRequestMessage(HttpMethod.Get, modelUri);
            var key = SecretRefResolver.Resolve(null, options.LlmApiKeyRef, environment.ContentRootPath, out _);
            if (!string.IsNullOrWhiteSpace(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await clients.CreateClient("advanced-analysis-llm").SendAsync(request, timeout.Token).ConfigureAwait(false);
            details["advanced_analysis_provider_http_status"] = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                details["advanced_analysis_provider_status"] = "unavailable";
                return;
            }
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token).ConfigureAwait(false);
            var present = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                && data.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.String && id.GetString() == provider.ModelId);
            details["advanced_analysis_provider_ready"] = present;
            details["advanced_analysis_provider_status"] = present ? "model_advertised" : "configured_model_not_advertised";
            details["advanced_analysis_probe_scope"] = "model_catalog_no_generation";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            details["advanced_analysis_provider_status"] = exception is OperationCanceledException ? "timeout" : "unavailable";
            details["advanced_analysis_provider_error_type"] = exception.GetType().Name;
        }
    }

    internal static bool ApplyRuntime(AdvancedAnalysisOptions options, AdvancedAnalysisRuntimeState state,
        Dictionary<string, object?> details)
    {
        var maximumAge = TimeSpan.FromMilliseconds(Math.Max(30_000,
            3L * Math.Clamp(options.HeartbeatMilliseconds, 25, 1_800_000)
            + Math.Clamp(options.PollDelayMilliseconds, 100, 60_000)));
        var snapshot = state.GetSnapshot(maximumAge);
        details["advanced_analysis_worker_enabled"] = options.WorkerEnabled;
        details["advanced_analysis_worker_state"] = snapshot.WorkerState;
        details["advanced_analysis_worker_live"] = snapshot.WorkerLive;
        details["advanced_analysis_worker_heartbeat_utc"] = snapshot.WorkerHeartbeatUtc;
        details["advanced_analysis_worker_heartbeat_age_ms"] = snapshot.HeartbeatAgeMilliseconds;
        details["advanced_analysis_capture_enabled"] = !string.IsNullOrWhiteSpace(options.DevelopmentTraceDirectory);
        details["advanced_analysis_capture_strict"] = options.DevelopmentTraceStrict;
        details["advanced_analysis_capture_retention_days"] = Math.Clamp(options.DevelopmentTraceRetentionDays, 1, 365);
        details["advanced_analysis_capture_maximum_files"] = Math.Clamp(options.DevelopmentTraceMaximumFiles, 1, 10_000);
        details["advanced_analysis_capture_maximum_bytes"] = Math.Clamp(options.DevelopmentTraceMaximumDirectoryBytes, 1_024, 4L * 1024 * 1024 * 1024);
        details["advanced_analysis_capture_maximum_characters"] = Math.Clamp(options.DevelopmentTraceMaximumCharacters, 1_024, 16_000_000);
        details["advanced_analysis_capture_succeeded"] = snapshot.CapturesSucceeded;
        details["advanced_analysis_capture_failed"] = snapshot.CapturesFailed;
        details["advanced_analysis_capture_last_failure_code"] = snapshot.LastCaptureFailureCode;
        details["advanced_analysis_capture_last_failure_utc"] = snapshot.LastCaptureFailureUtc;
        var required = details.TryGetValue("advanced_analysis_required", out var value) && value is true;
        var ready = snapshot.WorkerLive && details.TryGetValue("advanced_analysis_provider_ready", out var providerReady) && providerReady is true;
        details["advanced_analysis_ready"] = ready;
        return !required || ready;
    }
}
