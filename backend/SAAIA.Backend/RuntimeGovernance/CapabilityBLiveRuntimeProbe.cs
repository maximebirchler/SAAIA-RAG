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

            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        model = string.IsNullOrWhiteSpace(chatOptions?.LlmModel) ? "local" : chatOptions!.LlmModel,
                        temperature = 0.0,
                        max_tokens = 16,
                        messages = new object[]
                        {
                            new { role = "system", content = "Return plain text only." },
                            new { role = "user", content = "Reply with: ready" }
                        }
                    }),
                    Encoding.UTF8,
                    "application/json")
            };

            using var response = await llm.SendAsync(request, ct);
            sw.Stop();
            var body = await TryReadBodyAsync(response, ct);
            var hasContent = TryExtractChoiceContent(body, out var content);
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

    private static bool TryExtractChoiceContent(string body, out string? content)
    {
        content = null;
        if (string.IsNullOrWhiteSpace(body))
            return false;

        try
        {
            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                return false;

            var first = choices[0];
            if (!first.TryGetProperty("message", out var message)
                || !message.TryGetProperty("content", out var contentElement))
            {
                return false;
            }

            content = contentElement.ValueKind == JsonValueKind.String
                ? contentElement.GetString()
                : contentElement.GetRawText();
            return !string.IsNullOrWhiteSpace(content);
        }
        catch
        {
            return false;
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
