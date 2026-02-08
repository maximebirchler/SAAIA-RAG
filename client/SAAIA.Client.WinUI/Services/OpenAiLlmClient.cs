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
    private readonly HttpClient _http = new();

    // Exemple llama.cpp server OpenAI compat : http://127.0.0.1:8080/v1
    private string _baseUrl = "http://127.0.0.1:8080/v1";
    private string _model = "mistral";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

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
        resp.EnsureSuccessStatusCode();

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
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;

            // SSE format: "data: {...}" or "data: [DONE]"
            if (!line.StartsWith("data:")) continue;

            var data = line.Substring("data:".Length).Trim();
            if (data == "[DONE]") break;

            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;

                var delta = root.GetProperty("choices")[0].GetProperty("delta");
                if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    var token = c.GetString();
                    if (!string.IsNullOrEmpty(token))
                        onDelta(token);
                }
            }
            catch
            {
                // ignore malformed chunks
            }
        }
    }
}
