using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;


namespace SAAIA.Client.WinUI.Services;

public sealed class OpenAiLlmClient
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly HttpClient _http;

    public event Action? RuntimeActivityStarted;
    public event Action? RuntimeActivityFinished;

    // Exemple llama.cpp server OpenAI compat : http://127.0.0.1:8080/v1
    private string _baseUrl = "http://127.0.0.1:8080/v1";
    private string _model = "mistral";

    private static readonly JsonSerializerOptions JsonOpts = ClientJson.CamelCase;

    public event Func<CancellationToken, Task>? RuntimeEnsureReady;
    public OpenAiLlmClient()
        : this(httpClient: null)
    {
    }

    internal OpenAiLlmClient(HttpClient? httpClient)
    {
        _http = httpClient ?? SharedHttpClient;
    }


    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;

        string body = "";
        try { body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { ClientLog.Warn($"OpenAI-compatible error body unreadable: {ex.Message}"); }

        var msg = $"LLM request failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {body}";
        throw new HttpRequestException(msg, null, resp.StatusCode);
    }

    public void Configure(string baseUrl, string model)
    {
        _baseUrl = baseUrl.Trim().TrimEnd('/');
        _model = model.Trim();
    }

    private async Task EnsureRuntimeReadyAsync(CancellationToken ct)
    {
        var handlers = RuntimeEnsureReady;
        if (handlers is null)
            return;

        foreach (Func<CancellationToken, Task> handler in handlers.GetInvocationList())
            await handler(ct).ConfigureAwait(false);
    }

    public async Task<string> ChatOnceAsync(
        IReadOnlyList<(string role, string content)> messages,
        double temperature,
        int maxTokens,
        CancellationToken ct)
    {
        await EnsureRuntimeReadyAsync(ct).ConfigureAwait(false);
        RuntimeActivityStarted?.Invoke();
        try
        {
            var payload = new
            {
                model = _model,
                temperature,
                max_tokens = maxTokens,
                stream = false,
                messages = messages.Select(m => new { role = m.role, content = m.content }).ToArray()
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, ct);
            await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return content ?? "";
        }
        finally
        {
            RuntimeActivityFinished?.Invoke();
        }
    }

    public async Task ChatStreamAsync(
        IReadOnlyList<(string role, string content)> messages,
        double temperature,
        int maxTokens,
        Action<string> onDelta,
        CancellationToken ct)
    {
        await EnsureRuntimeReadyAsync(ct).ConfigureAwait(false);
        RuntimeActivityStarted?.Invoke();
        try
        {
            var payload = new
            {
                model = _model,
                temperature,
                max_tokens = maxTokens,
                stream = true,
                messages = messages.Select(m => new { role = m.role, content = m.content }).ToArray()
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            req.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);

            var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";

            // Some OpenAI-compatible servers ignore SSE and return a normal JSON response.
            // Spec requires progressive UX anyway => simulate streaming client-side.
            if (!mediaType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var full = TryExtractChatContent(json);
                if (!string.IsNullOrEmpty(full))
                    await SimulateStreamingAsync(full, onDelta, ct).ConfigureAwait(false);
                return;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            // Robuste: certains serveurs streament du "delta", d'autres du "contenu cumulatif"
            var emittedSoFar = "";
            var sawData = false;

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync().WaitAsync(ct);
                if (line is null) break;                // stream fermé
                if (line.Length == 0) continue;         // ligne vide SSE

                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

                sawData = true;
                var data = line.Substring("data:".Length).Trim();
                if (data == "[DONE]") break;

                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;

                    var delta = root.GetProperty("choices")[0].GetProperty("delta");
                    if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    {
                        var chunk = c.GetString() ?? "";
                        if (chunk.Length == 0) continue;

                        // Si "chunk" est cumulatif et contient déjà ce qu'on a émis, on n'émet que le "diff"
                        if (chunk.StartsWith(emittedSoFar, StringComparison.Ordinal))
                        {
                            var diff = chunk.Substring(emittedSoFar.Length);
                            if (diff.Length > 0)
                            {
                                onDelta(diff);
                                emittedSoFar += diff;
                            }
                        }
                        else
                        {
                            // Sinon, on considère que c'est un vrai delta
                            onDelta(chunk);
                            emittedSoFar += chunk;
                        }
                    }
                }
                catch (JsonException ex)
                {
                    ClientLog.Warn($"OpenAI-compatible SSE chunk ignored: {ex.Message}");
                }
            }

            // Safety net: if server claimed SSE but never sent data lines, fall back to JSON parsing and simulate.
            if (!sawData || string.IsNullOrEmpty(emittedSoFar))
            {
                try
                {
                    // NOTE: at this point the stream might be consumed; we can only do best-effort by reading the remaining buffer.
                    var remaining = await reader.ReadToEndAsync().WaitAsync(ct);
                    var full = TryExtractChatContent(remaining);
                    if (!string.IsNullOrEmpty(full))
                        await SimulateStreamingAsync(full, onDelta, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    ClientLog.Warn($"OpenAI-compatible SSE fallback ignored: {ex.Message}");
                }
            }
        }
        finally
        {
            RuntimeActivityFinished?.Invoke();
        }
    }

    private static string TryExtractChatContent(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var ch0 = choices[0];

                // Non-stream: choices[0].message.content
                if (ch0.TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String)
                    return content.GetString() ?? "";

                // Some servers return "text"
                if (ch0.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                    return txt.GetString() ?? "";

                // Fallback: delta.content
                if (ch0.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("content", out var dc) &&
                    dc.ValueKind == JsonValueKind.String)
                    return dc.GetString() ?? "";
            }
        }
        catch (JsonException ex)
        {
            ClientLog.Warn($"OpenAI-compatible JSON payload ignored: {ex.Message}");
        }

        return "";
    }

    private static async Task SimulateStreamingAsync(string full, Action<string> onDelta, CancellationToken ct)
    {
        // Chunking without artificial slowness; Task.Yield lets the UI breathe between chunks.
        const int chunkSize = 48;

        for (int i = 0; i < full.Length; i += chunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var len = Math.Min(chunkSize, full.Length - i);
            var chunk = full.Substring(i, len);
            if (chunk.Length > 0) onDelta(chunk);
            await Task.Yield();
        }
    }


    public async Task<List<string>> ListModelsAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/models");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await _http.SendAsync(req, ct);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        var list = new List<string>();
        var root = doc.RootElement;

        // Common OpenAI format: { data: [ { id: "..." } ] }
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in data.EnumerateArray())
            {
                if (el.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    var s = id.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s!);
                }
            }
        }

        // llama.cpp may also return { models: [ { name|model: "..." } ] }
        if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in models.EnumerateArray())
            {
                string? v = null;
                if (el.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String) v = name.GetString();
                else if (el.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String) v = model.GetString();

                if (!string.IsNullOrWhiteSpace(v)) list.Add(v!);
            }
        }

        return list
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

}
