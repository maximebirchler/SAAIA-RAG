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

            var outbound = LocalLlmRequestFactory.Create(
                _options,
                systemPrompt,
                userPrompt,
                temperature ?? _options.LlmTemperature,
                Math.Clamp(maxTokens ?? _options.LlmMaxTokens, 64, 4096));

            using var request = new HttpRequestMessage(HttpMethod.Post, outbound.RelativeUri)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(outbound.Payload, JsonOptions),
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
            if (!LocalLlmRequestFactory.TryExtractContent(
                    json.RootElement,
                    outbound.ResponseShape,
                    out var content,
                    out var contentError))
            {
                return new LocalLlmChatCompletionResult(
                    Content: null,
                    DurationMs: sw.ElapsedMilliseconds,
                    ResponseHeadersMs: responseHeadersMs,
                    FirstByteMs: firstByteMs,
                    StatusCode: (int)response.StatusCode,
                    BytesRead: bytesRead,
                    Error: contentError);
            }

            return new LocalLlmChatCompletionResult(
                Content: content,
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
