using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace SAAIA.Backend;

internal sealed class LocalLlmChatClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpFactory;
    private readonly ChatOptions _options;
    private readonly RuntimeLlmCapacityPlanService? _capacityPlanService;
    private readonly RuntimeLlmQueueManager? _queueManager;

    public LocalLlmChatClient(
        IHttpClientFactory httpFactory,
        ChatOptions options,
        RuntimeLlmCapacityPlanService? capacityPlanService = null,
        RuntimeLlmQueueManager? queueManager = null)
    {
        _httpFactory = httpFactory;
        _options = options;
        _capacityPlanService = capacityPlanService;
        _queueManager = queueManager;
    }

    internal bool IsConfigured
        => !string.IsNullOrWhiteSpace(_options.LlmBaseUrl)
            && !string.IsNullOrWhiteSpace(_options.LlmModel);

    internal async Task<string?> TryCompleteAsync(
        string systemPrompt,
        string userPrompt,
        int? maxTokens,
        double? temperature,
        CancellationToken ct)
        => (await TryCompleteWithTelemetryAsync(systemPrompt, userPrompt, maxTokens, temperature, ct)).Content;

    internal async Task<LocalLlmChatCompletionResult> TryCompleteWithTelemetryAsync(
        string systemPrompt,
        string userPrompt,
        int? maxTokens,
        double? temperature,
        CancellationToken ct)
    {
        if (!IsConfigured)
            return LocalLlmChatCompletionResult.NotConfigured;

        try
        {
            using var queueLease = await TryAcquireQueueSlotAsync(ct).ConfigureAwait(false);
            if (_queueManager is not null && queueLease is null)
            {
                return new LocalLlmChatCompletionResult(
                    Content: null,
                    DurationMs: null,
                    ResponseHeadersMs: null,
                    FirstByteMs: null,
                    StatusCode: StatusCodes.Status429TooManyRequests,
                    BytesRead: 0,
                    Error: "llm_queue_full");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        model = _options.LlmModel,
                        temperature = temperature ?? _options.LlmTemperature,
                        max_tokens = Math.Clamp(maxTokens ?? _options.LlmMaxTokens, 64, 4096),
                        messages = new object[]
                        {
                            new { role = "system", content = systemPrompt },
                            new { role = "user", content = userPrompt }
                        }
                    }, JsonOptions),
                    Encoding.UTF8,
                    "application/json")
            };

            var sw = Stopwatch.StartNew();
            var http = _httpFactory.CreateClient("llm");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var responseHeadersMs = sw.ElapsedMilliseconds;
            if (!response.IsSuccessStatusCode)
                return new LocalLlmChatCompletionResult(
                    Content: null,
                    DurationMs: sw.ElapsedMilliseconds,
                    ResponseHeadersMs: responseHeadersMs,
                    FirstByteMs: null,
                    StatusCode: (int)response.StatusCode,
                    BytesRead: 0,
                    Error: $"http_{(int)response.StatusCode}");

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await using var payload = new MemoryStream();
            var buffer = new byte[4096];
            long? firstByteMs = null;
            long bytesRead = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read <= 0)
                    break;

                if (!firstByteMs.HasValue)
                    firstByteMs = sw.ElapsedMilliseconds;

                bytesRead += read;
                await payload.WriteAsync(buffer.AsMemory(0, read), ct);
            }

            if (bytesRead == 0)
                return new LocalLlmChatCompletionResult(
                    Content: null,
                    DurationMs: sw.ElapsedMilliseconds,
                    ResponseHeadersMs: responseHeadersMs,
                    FirstByteMs: firstByteMs,
                    StatusCode: (int)response.StatusCode,
                    BytesRead: 0,
                    Error: "empty_body");

            payload.Position = 0;
            using var json = await JsonDocument.ParseAsync(payload, cancellationToken: ct);
            if (!json.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                return new LocalLlmChatCompletionResult(
                    Content: null,
                    DurationMs: sw.ElapsedMilliseconds,
                    ResponseHeadersMs: responseHeadersMs,
                    FirstByteMs: firstByteMs,
                    StatusCode: (int)response.StatusCode,
                    BytesRead: bytesRead,
                    Error: "choices_missing");

            var first = choices[0];
            if (!first.TryGetProperty("message", out var message)
                || !message.TryGetProperty("content", out var content))
                return new LocalLlmChatCompletionResult(
                    Content: null,
                    DurationMs: sw.ElapsedMilliseconds,
                    ResponseHeadersMs: responseHeadersMs,
                    FirstByteMs: firstByteMs,
                    StatusCode: (int)response.StatusCode,
                    BytesRead: bytesRead,
                    Error: "content_missing");

            return new LocalLlmChatCompletionResult(
                Content: ReadContent(content),
                DurationMs: sw.ElapsedMilliseconds,
                ResponseHeadersMs: responseHeadersMs,
                FirstByteMs: firstByteMs,
                StatusCode: (int)response.StatusCode,
                BytesRead: bytesRead,
                Error: null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return new LocalLlmChatCompletionResult(
                Content: null,
                DurationMs: null,
                ResponseHeadersMs: null,
                FirstByteMs: null,
                StatusCode: null,
                BytesRead: 0,
                Error: "llm_timeout");
        }
        catch (TimeoutException)
        {
            return new LocalLlmChatCompletionResult(
                Content: null,
                DurationMs: null,
                ResponseHeadersMs: null,
                FirstByteMs: null,
                StatusCode: null,
                BytesRead: 0,
                Error: "llm_timeout");
        }
        catch (HttpRequestException)
        {
            return new LocalLlmChatCompletionResult(
                Content: null,
                DurationMs: null,
                ResponseHeadersMs: null,
                FirstByteMs: null,
                StatusCode: null,
                BytesRead: 0,
                Error: "llm_transport_error");
        }
        catch
        {
            return new LocalLlmChatCompletionResult(
                Content: null,
                DurationMs: null,
                ResponseHeadersMs: null,
                FirstByteMs: null,
                StatusCode: null,
                BytesRead: 0,
                Error: "exception");
        }
    }

    private async Task<RuntimeLlmQueueManager.RuntimeLlmQueueLease?> TryAcquireQueueSlotAsync(CancellationToken ct)
    {
        if (_capacityPlanService is null || _queueManager is null)
            return null;

        var capacityPlan = await _capacityPlanService.GetQueuePlanAsync(ct).ConfigureAwait(false);
        return await _queueManager
            .AcquireOrQueueAsync("backend-llm", capacityPlan, TimeSpan.FromSeconds(15), ct)
            .ConfigureAwait(false);
    }

    private static string? ReadContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString();

        if (content.ValueKind != JsonValueKind.Array)
            return null;

        var builder = new StringBuilder();
        foreach (var item in content.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var type)
                || !string.Equals(type.GetString(), "text", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!item.TryGetProperty("text", out var text))
                continue;

            var value = text.GetString();
            if (string.IsNullOrWhiteSpace(value))
                continue;

            if (builder.Length > 0)
                builder.AppendLine();
            builder.Append(value.Trim());
        }

        return builder.Length == 0 ? null : builder.ToString();
    }
}

internal sealed record LocalLlmChatCompletionResult(
    string? Content,
    long? DurationMs,
    long? ResponseHeadersMs,
    long? FirstByteMs,
    int? StatusCode,
    long BytesRead,
    string? Error)
{
    internal static LocalLlmChatCompletionResult NotConfigured { get; } = new(
        Content: null,
        DurationMs: null,
        ResponseHeadersMs: null,
        FirstByteMs: null,
        StatusCode: null,
        BytesRead: 0,
        Error: "not_configured");
}
