using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

internal interface ILocalLlmWarmupHarness
{
    Task<WarmupMeasurement> RunOnceAsync(
        string llmBaseUrl,
        string model,
        LocalLlmWarmupHarnessOptions? options = null,
        CancellationToken ct = default);
}

internal sealed record LocalLlmWarmupHarnessOptions(
    TimeSpan ReadinessTimeout,
    TimeSpan PollInterval,
    string Prompt,
    int MaxTokens,
    double Temperature)
{
    public static LocalLlmWarmupHarnessOptions Default => new(
        ReadinessTimeout: TimeSpan.FromSeconds(120),
        PollInterval: TimeSpan.FromMilliseconds(750),
        Prompt: "Reponds en une phrase courte: le systeme SAAIA est pret.",
        MaxTokens: 64,
        Temperature: 0.0);
}

internal sealed class LocalLlmWarmupHarness : ILocalLlmWarmupHarness
{
    private readonly HttpClient _http;

    public LocalLlmWarmupHarness(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<WarmupMeasurement> RunOnceAsync(
        string llmBaseUrl,
        string model,
        LocalLlmWarmupHarnessOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= LocalLlmWarmupHarnessOptions.Default;

        if (string.IsNullOrWhiteSpace(llmBaseUrl))
            return new WarmupMeasurement(0, 0, 0, Succeeded: false, Error: "missing_base_url");

        if (string.IsNullOrWhiteSpace(model))
            return new WarmupMeasurement(0, 0, 0, Succeeded: false, Error: "missing_model");

        var baseUrl = llmBaseUrl.TrimEnd('/');
        var loadSw = Stopwatch.StartNew();

        var ready = await WaitReadyAsync(baseUrl, options, ct).ConfigureAwait(false);
        loadSw.Stop();
        var loadMs = (int)Math.Min(int.MaxValue, loadSw.ElapsedMilliseconds);

        if (!ready.Ok)
            return new WarmupMeasurement(loadMs, 0, 0, Succeeded: false, Error: ready.Error);

        return await MeasureChatAsync(baseUrl, model, options, loadMs, ct).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string? Error)> WaitReadyAsync(
        string baseUrl,
        LocalLlmWarmupHarnessOptions options,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + options.ReadinessTimeout;
        var modelsUri = new Uri(new Uri(baseUrl + "/", UriKind.Absolute), "models");

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var resp = await _http.GetAsync(modelsUri, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                    return (true, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // The server can refuse connections while llama.cpp loads the model.
            }

            await Task.Delay(options.PollInterval, ct).ConfigureAwait(false);
        }

        return (false, "models_not_ready");
    }

    private async Task<WarmupMeasurement> MeasureChatAsync(
        string baseUrl,
        string model,
        LocalLlmWarmupHarnessOptions options,
        int loadMs,
        CancellationToken ct)
    {
        var payload = new
        {
            model,
            temperature = options.Temperature,
            max_tokens = options.MaxTokens,
            stream = true,
            messages = new[]
            {
                new { role = "user", content = options.Prompt }
            }
        };

        var chatUri = new Uri(new Uri(baseUrl + "/", UriKind.Absolute), "chat/completions");
        using var req = new HttpRequestMessage(HttpMethod.Post, chatUri);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return new WarmupMeasurement(
                    loadMs,
                    (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds),
                    0,
                    Succeeded: false,
                    Error: $"chat_http_{(int)resp.StatusCode}");
            }

            var mediaType = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var bodyStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(bodyStream, Encoding.UTF8);

            return mediaType.Contains("event-stream", StringComparison.OrdinalIgnoreCase)
                ? await ReadSseMeasurementAsync(reader, sw, loadMs, ct).ConfigureAwait(false)
                : await ReadJsonMeasurementAsync(reader, sw, loadMs, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new WarmupMeasurement(
                loadMs,
                (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds),
                0,
                Succeeded: false,
                Error: ex.GetType().Name);
        }
    }

    private static async Task<WarmupMeasurement> ReadSseMeasurementAsync(
        StreamReader reader,
        Stopwatch sw,
        int loadMs,
        CancellationToken ct)
    {
        var firstTokenMs = 0;
        var generatedText = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync().WaitAsync(ct).ConfigureAwait(false);
            if (line is null)
                break;

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line.Substring("data:".Length).Trim();
            if (data == "[DONE]")
                break;

            var delta = TryExtractDelta(data);
            if (string.IsNullOrEmpty(delta))
                continue;

            if (firstTokenMs == 0)
                firstTokenMs = (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds);

            generatedText.Append(delta);
        }

        sw.Stop();
        if (firstTokenMs == 0)
            return new WarmupMeasurement(loadMs, (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds), 0, Succeeded: false, Error: "no_tokens");

        var tokenCount = EstimateTokenCount(generatedText.ToString());
        var decodeMs = Math.Max(1, sw.ElapsedMilliseconds - firstTokenMs);
        var tokPerSec = tokenCount / (decodeMs / 1000.0);
        return new WarmupMeasurement(loadMs, firstTokenMs, tokPerSec);
    }

    private static async Task<WarmupMeasurement> ReadJsonMeasurementAsync(
        StreamReader reader,
        Stopwatch sw,
        int loadMs,
        CancellationToken ct)
    {
        var json = await reader.ReadToEndAsync().WaitAsync(ct).ConfigureAwait(false);
        sw.Stop();

        var text = TryExtractFullContent(json);
        var tokenCount = EstimateTokenCount(text);
        var elapsedMs = Math.Max(1, (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds));
        return string.IsNullOrWhiteSpace(text)
            ? new WarmupMeasurement(loadMs, elapsedMs, 0, Succeeded: false, Error: "no_content")
            : new WarmupMeasurement(loadMs, elapsedMs, tokenCount / (elapsedMs / 1000.0));
    }

    private static string TryExtractDelta(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var choice = doc.RootElement.GetProperty("choices")[0];
            if (choice.TryGetProperty("delta", out var delta)
                && delta.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
            {
                return content.GetString() ?? string.Empty;
            }
        }
        catch
        {
            // ignore malformed stream chunks
        }

        return string.Empty;
    }

    private static string TryExtractFullContent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var choice = doc.RootElement.GetProperty("choices")[0];
            if (choice.TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
            {
                return content.GetString() ?? string.Empty;
            }
        }
        catch
        {
            // ignore
        }

        return string.Empty;
    }

    private static int EstimateTokenCount(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var wordish = text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Length;

        var charEstimate = (int)Math.Ceiling(text.Length / 4.0);
        return Math.Max(1, Math.Max(wordish, charEstimate));
    }
}
