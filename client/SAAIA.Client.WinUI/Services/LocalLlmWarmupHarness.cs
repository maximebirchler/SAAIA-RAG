using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Collections.Generic;
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
    double Temperature,
    bool TryReadRuntimeMetrics = true,
    string Scenario = "short_ttft")
{
    public static LocalLlmWarmupHarnessOptions Default => new(
        ReadinessTimeout: TimeSpan.FromSeconds(120),
        PollInterval: TimeSpan.FromMilliseconds(750),
        Prompt: "Reponds en une phrase courte: le systeme SAAIA est pret.",
        MaxTokens: 64,
        Temperature: 0.0);
}

internal sealed record LocalLlmWarmupScenario(
    string Key,
    string Prompt,
    int MaxTokens,
    double Temperature = 0.0);

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
            return new WarmupMeasurement(0, 0, 0, Succeeded: false, Error: "missing_base_url", Scenario: options.Scenario);

        if (string.IsNullOrWhiteSpace(model))
            return new WarmupMeasurement(0, 0, 0, Succeeded: false, Error: "missing_model", Scenario: options.Scenario);

        var baseUrl = llmBaseUrl.TrimEnd('/');
        var loadSw = Stopwatch.StartNew();

        var ready = await WaitReadyAsync(baseUrl, options, ct).ConfigureAwait(false);
        loadSw.Stop();
        var loadMs = (int)Math.Min(int.MaxValue, loadSw.ElapsedMilliseconds);

        if (!ready.Ok)
            return new WarmupMeasurement(loadMs, 0, 0, Succeeded: false, Error: ready.Error, Scenario: options.Scenario);

        var beforeMetrics = options.TryReadRuntimeMetrics
            ? await TryReadMetricsAsync(baseUrl, ct).ConfigureAwait(false)
            : null;

        var measurement = await MeasureChatAsync(baseUrl, model, options, loadMs, ct).ConfigureAwait(false);
        if (!options.TryReadRuntimeMetrics)
            return measurement with { Scenario = options.Scenario };

        var afterMetrics = await TryReadMetricsAsync(baseUrl, ct).ConfigureAwait(false);
        var mergedMetrics = MergeMetrics(beforeMetrics, afterMetrics);
        return measurement with
        {
            RuntimeMetrics = mergedMetrics.Count == 0 ? null : mergedMetrics,
            Scenario = options.Scenario
        };
    }

    public async Task<IReadOnlyList<WarmupMeasurement>> RunContractScenariosAsync(
        string llmBaseUrl,
        string model,
        IReadOnlyList<LocalLlmWarmupScenario>? scenarios = null,
        LocalLlmWarmupHarnessOptions? baseOptions = null,
        CancellationToken ct = default)
    {
        var resolvedScenarios = scenarios is { Count: > 0 }
            ? scenarios
            : CreateContractScenarios();
        var options = baseOptions ?? LocalLlmWarmupHarnessOptions.Default;
        var measurements = new List<WarmupMeasurement>(resolvedScenarios.Count);

        foreach (var scenario in resolvedScenarios)
        {
            ct.ThrowIfCancellationRequested();
            var scenarioOptions = options with
            {
                Prompt = scenario.Prompt,
                MaxTokens = scenario.MaxTokens,
                Temperature = scenario.Temperature,
                Scenario = scenario.Key
            };

            var measurement = await RunOnceAsync(
                llmBaseUrl,
                model,
                scenarioOptions,
                ct).ConfigureAwait(false);

            measurements.Add(measurement with { Scenario = scenario.Key });
        }

        return measurements;
    }

    internal static IReadOnlyList<LocalLlmWarmupScenario> CreateContractScenarios()
    {
        var longContext = string.Join(
            "\n",
            Enumerable.Repeat(
                "Contexte SAAIA: document, section, unite, preuve, categorie, source et avertissement doivent rester coherents.",
                18));

        return new[]
        {
            new LocalLlmWarmupScenario(
                "short_ttft",
                "Reponds uniquement par: pret.",
                16),
            new LocalLlmWarmupScenario(
                "long_prefill",
                "Lis ce contexte et reponds par une phrase courte qui confirme la coherence.\n" + longContext,
                48),
            new LocalLlmWarmupScenario(
                "decode_stable",
                "Redige exactement huit points courts numerotes sur les controles de qualite d'un assistant RAG local.",
                128)
        };
    }

    internal static WarmupMeasurement AggregateScenarioMeasurements(IReadOnlyList<WarmupMeasurement> measurements)
    {
        if (measurements.Count == 0)
            return new WarmupMeasurement(0, 0, 0, Succeeded: false, Error: "no_scenarios", Scenario: "contract_suite");

        var loadMs = measurements.Max(static item => item.LoadMs);
        var ttftMs = measurements.Max(static item => item.TtftMs);
        var successful = measurements.Where(static item => item.Succeeded).ToArray();
        var allSucceeded = successful.Length == measurements.Count;
        var tokPerSec = allSucceeded ? successful.Min(static item => item.TokPerSec) : 0;
        var msPerToken = allSucceeded ? successful.Max(static item => item.MsPerToken) : null;

        var metrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var measurement in measurements)
        {
            var scenario = string.IsNullOrWhiteSpace(measurement.Scenario)
                ? "unknown"
                : measurement.Scenario;
            var prefix = "scenario." + scenario + ".";
            metrics[prefix + "load_ms"] = measurement.LoadMs;
            metrics[prefix + "ttft_ms"] = measurement.TtftMs;
            metrics[prefix + "tok_per_sec"] = measurement.TokPerSec;
            if (measurement.MsPerToken is { } ms)
                metrics[prefix + "ms_per_token"] = ms;

            if (measurement.RuntimeMetrics is null)
                continue;

            foreach (var item in measurement.RuntimeMetrics)
                metrics[prefix + item.Key] = item.Value;
        }

        var error = allSucceeded
            ? null
            : string.Join(
                ";",
                measurements
                    .Where(static item => !item.Succeeded)
                    .Select(static item => $"{item.Scenario ?? "unknown"}:{item.Error ?? "failed"}"));

        return new WarmupMeasurement(
            loadMs,
            ttftMs,
            tokPerSec,
            allSucceeded,
            error,
            PeakRamMiB: measurements.Max(static item => item.PeakRamMiB),
            PeakVramMiB: measurements.Max(static item => item.PeakVramMiB),
            MsPerToken: msPerToken,
            RuntimeMetrics: metrics.Count == 0 ? null : metrics,
            Scenario: "contract_suite");
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
        int? firstTokenMs = null;
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

            if (firstTokenMs is null)
                firstTokenMs = (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds);

            generatedText.Append(delta);
        }

        sw.Stop();
        if (firstTokenMs is null)
            return new WarmupMeasurement(loadMs, (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds), 0, Succeeded: false, Error: "no_tokens");

        var tokenCount = EstimateTokenCount(generatedText.ToString());
        var decodeMs = Math.Max(1, sw.ElapsedMilliseconds - firstTokenMs.Value);
        var tokPerSec = tokenCount / (decodeMs / 1000.0);
        return new WarmupMeasurement(
            loadMs,
            firstTokenMs.Value,
            tokPerSec,
            MsPerToken: decodeMs / (double)Math.Max(1, tokenCount));
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
            : new WarmupMeasurement(
                loadMs,
                elapsedMs,
                tokenCount / (elapsedMs / 1000.0),
                MsPerToken: elapsedMs / (double)Math.Max(1, tokenCount));
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

    private async Task<IReadOnlyDictionary<string, double>?> TryReadMetricsAsync(
        string baseUrl,
        CancellationToken ct)
    {
        try
        {
            var metricsUri = new Uri(new Uri(baseUrl + "/", UriKind.Absolute), "metrics");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));

            using var resp = await _http.GetAsync(metricsUri, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;

            var text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return ParsePrometheusMetrics(text);
        }
        catch
        {
            return null;
        }
    }

    internal static IReadOnlyDictionary<string, double> ParsePrometheusMetrics(string text)
    {
        var metrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
            return metrics;

        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                continue;

            var split = line.LastIndexOf(' ');
            if (split <= 0 || split >= line.Length - 1)
                continue;

            var key = line[..split].Trim();
            var valueText = line[(split + 1)..].Trim();
            var labelStart = key.IndexOf('{');
            if (labelStart >= 0)
                key = key[..labelStart];

            if (double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                metrics[key] = value;
        }

        return metrics;
    }

    private static IReadOnlyDictionary<string, double> MergeMetrics(
        IReadOnlyDictionary<string, double>? before,
        IReadOnlyDictionary<string, double>? after)
    {
        var merged = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (after is null || after.Count == 0)
            return merged;

        foreach (var item in after)
        {
            merged[item.Key] = item.Value;
            if (before is not null && before.TryGetValue(item.Key, out var previous))
                merged[item.Key + "_delta"] = item.Value - previous;
        }

        AddCanonicalRuntimeMetrics(merged, before, after);
        return merged;
    }

    private static void AddCanonicalRuntimeMetrics(
        Dictionary<string, double> merged,
        IReadOnlyDictionary<string, double>? before,
        IReadOnlyDictionary<string, double> after)
    {
        AddCanonicalMetric(
            merged,
            before,
            after,
            "runtime.tokens_predicted_total",
            "runtime.tokens_predicted_delta",
            "llamacpp_tokens_predicted_total",
            "llamacpp_decode_tokens_total",
            "llamacpp_tokens_generated_total");

        AddCanonicalMetric(
            merged,
            before,
            after,
            "runtime.prompt_tokens_total",
            "runtime.prompt_tokens_delta",
            "llamacpp_prompt_tokens_total",
            "llamacpp_tokens_prompt_total",
            "llamacpp_tokens_processed_total");

        AddCanonicalMetric(
            merged,
            before,
            after,
            "runtime.kv_cache_used_bytes",
            "runtime.kv_cache_used_bytes_delta",
            "llamacpp_kv_cache_used_bytes",
            "llamacpp_kv_cache_used");

        AddCanonicalMetric(
            merged,
            before,
            after,
            "runtime.kv_cache_used_cells",
            "runtime.kv_cache_used_cells_delta",
            "llamacpp_kv_cache_used_cells",
            "llamacpp_kv_cache_tokens");

        AddCanonicalMetric(
            merged,
            before,
            after,
            "runtime.kv_cache_total_cells",
            null,
            "llamacpp_kv_cache_total_cells",
            "llamacpp_kv_cache_cell_max");

        AddCanonicalMetric(
            merged,
            before,
            after,
            "runtime.threads",
            null,
            "llamacpp_threads",
            "llamacpp_server_threads");

        AddCanonicalMetric(
            merged,
            before,
            after,
            "runtime.threads_batch",
            null,
            "llamacpp_threads_batch",
            "llamacpp_server_threads_batch");

        AddCanonicalMetric(
            merged,
            before,
            after,
            "runtime.slots_processing",
            null,
            "llamacpp_server_slots_processing",
            "llamacpp_slots_processing");

        if (merged.TryGetValue("runtime.kv_cache_used_bytes", out var kvBytes))
            merged["runtime.kv_cache_used_mib"] = kvBytes / 1024d / 1024d;

        if (merged.TryGetValue("runtime.kv_cache_used_cells", out var usedCells)
            && merged.TryGetValue("runtime.kv_cache_total_cells", out var totalCells)
            && totalCells > 0)
        {
            merged["runtime.kv_cache_used_percent"] = usedCells / totalCells * 100d;
        }
    }

    private static void AddCanonicalMetric(
        Dictionary<string, double> merged,
        IReadOnlyDictionary<string, double>? before,
        IReadOnlyDictionary<string, double> after,
        string targetKey,
        string? deltaKey,
        params string[] sourceKeys)
    {
        if (!TryGetMetric(after, out var value, sourceKeys))
            return;

        merged[targetKey] = value;
        if (deltaKey is not null && before is not null && TryGetMetric(before, out var previous, sourceKeys))
            merged[deltaKey] = value - previous;
    }

    private static bool TryGetMetric(
        IReadOnlyDictionary<string, double> metrics,
        out double value,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (metrics.TryGetValue(key, out value))
                return true;
        }

        value = 0;
        return false;
    }
}
