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
    private readonly HttpClient _http = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    // Exemple llama.cpp server OpenAI compat : http://127.0.0.1:8080/v1
    private string _baseUrl = "http://127.0.0.1:8080/v1";
    private string _model = "mistral";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };


    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;

        string body = "";
        try { body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { /* ignore */ }

        var msg = $"LLM request failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {body}";
        throw new HttpRequestException(msg, null, resp.StatusCode);
    }

    public void Configure(string baseUrl, string model)
    {
        _baseUrl = baseUrl.Trim().TrimEnd('/');
        _model = model.Trim();
    }

    public async Task<string> ChatOnceAsync(
        IReadOnlyList<(string role, string content)> messages,
        double temperature,
        int maxTokens,
        CancellationToken ct)
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

    public async Task ChatStreamAsync(
        IReadOnlyList<(string role, string content)> messages,
        double temperature,
        int maxTokens,
        Action<string> onDelta,
        CancellationToken ct)
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

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        // Robuste: certains serveurs streament du "delta", d'autres du "contenu cumulatif"
        var emittedSoFar = "";

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync().WaitAsync(ct);
            if (line is null) break;                // stream fermé
            if (line.Length == 0) continue;         // ligne vide SSE


            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

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
            catch
            {
                // ignore malformed chunks
            }
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
