using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace SAAIA.Backend;

internal static class CapabilityBLiveRuntimeProbe
{
    internal static async Task<CapabilityBLiveRuntimeProbeResult> ProbeAsync(
        IHttpClientFactory? httpFactory,
        ChatOptions? chatOptions,
        CancellationToken ct)
    {
        var measuredAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        try
        {
            if (httpFactory is null)
            {
                sw.Stop();
                return new CapabilityBLiveRuntimeProbeResult(
                    Available: false,
                    Status: "not_configured",
                    Error: "llm runtime is not configured",
                    MeasuredAt: measuredAt,
                    DurationMs: sw.ElapsedMilliseconds,
                    BaseUrl: null,
                    StatusCode: null,
                    HasContent: false,
                    Preview: null,
                    Body: null);
            }

            var llm = httpFactory.CreateClient("llm");
            if (llm.BaseAddress is null)
            {
                sw.Stop();
                return new CapabilityBLiveRuntimeProbeResult(
                    Available: false,
                    Status: "not_configured",
                    Error: "llm runtime is not configured",
                    MeasuredAt: measuredAt,
                    DurationMs: sw.ElapsedMilliseconds,
                    BaseUrl: null,
                    StatusCode: null,
                    HasContent: false,
                    Preview: null,
                    Body: null);
            }

            var effectiveOptions = chatOptions ?? new ChatOptions();
            var outbound = LocalLlmRequestFactory.Create(
                effectiveOptions,
                "Return plain text only.",
                "Reply with: ready",
                temperature: 0.0,
                maxTokens: 16);

            using var request = new HttpRequestMessage(HttpMethod.Post, outbound.RelativeUri)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(outbound.Payload),
                    Encoding.UTF8,
                    "application/json")
            };

            using var response = await llm.SendAsync(request, ct);
            sw.Stop();
            var body = await TryReadBodyAsync(response, ct);
            var hasContent = LocalLlmRequestFactory.TryExtractContent(
                body,
                outbound.ResponseShape,
                out var content);
            var status = response.IsSuccessStatusCode && hasContent
                ? "ok"
                : response.IsSuccessStatusCode
                    ? "empty_response"
                    : "http_error";
            var error = response.IsSuccessStatusCode
                ? hasContent ? null : "llm chat completion returned no content"
                : "llm chat completion check failed";

            return new CapabilityBLiveRuntimeProbeResult(
                Available: response.IsSuccessStatusCode && hasContent,
                Status: status,
                Error: error,
                MeasuredAt: measuredAt,
                DurationMs: sw.ElapsedMilliseconds,
                BaseUrl: llm.BaseAddress.ToString(),
                StatusCode: (int)response.StatusCode,
                HasContent: hasContent,
                Preview: string.IsNullOrWhiteSpace(content)
                    ? null
                    : content!.Length <= 120 ? content : content[..120],
                Body: body);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new CapabilityBLiveRuntimeProbeResult(
                Available: false,
                Status: "exception",
                Error: ex.Message,
                MeasuredAt: measuredAt,
                DurationMs: sw.ElapsedMilliseconds,
                BaseUrl: null,
                StatusCode: null,
                HasContent: false,
                Preview: null,
                Body: null);
        }
    }

    private static async Task<string> TryReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return string.Empty;
        }
    }

}

internal sealed record CapabilityBLiveRuntimeProbeResult(
    bool Available,
    string Status,
    string? Error,
    DateTimeOffset MeasuredAt,
    long DurationMs,
    string? BaseUrl,
    int? StatusCode,
    bool HasContent,
    string? Preview,
    string? Body);
