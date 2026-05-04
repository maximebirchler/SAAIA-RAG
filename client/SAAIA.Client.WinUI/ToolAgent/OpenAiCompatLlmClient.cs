using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed class OpenAiCompatLlmClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _model;
    private const int JsonMaxTokens = 700;
    private const int DefaultAnswerMaxTokens = 850;
    private const int SummaryAnswerMaxTokens = 1100;

    public OpenAiCompatLlmClient(HttpClient http, string baseUrl, string model)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _model = model;
    }

    public async Task<string> CompleteAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, CancellationToken ct)
    {
        var payload = BuildPayload(messages, forceJson);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            if (forceJson && (resp.StatusCode == HttpStatusCode.BadRequest || (int)resp.StatusCode == 422))
            {
                var payloadRetry = BuildPayload(messages, forceJson: false);
                using var req2 = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/chat/completions")
                {
                    Content = new StringContent(JsonSerializer.Serialize(payloadRetry), Encoding.UTF8, "application/json")
                };

                using var resp2 = await _http.SendAsync(req2, ct).ConfigureAwait(false);
                resp2.EnsureSuccessStatusCode();
                var json2 = await resp2.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return ExtractContent(json2);
            }

            resp.EnsureSuccessStatusCode();
        }

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ExtractContent(json);
    }

    public async Task StreamAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, Action<string> onDelta, CancellationToken ct)
    {
        if (onDelta is null)
            return;

        try
        {
            if (await TryStreamInternalAsync(messages, forceJson, onDelta, ct).ConfigureAwait(false))
                return;
        }
        catch when (!ct.IsCancellationRequested)
        {
            // fallback below
        }

        if (forceJson)
        {
            try
            {
                if (await TryStreamInternalAsync(messages, forceJson: false, onDelta, ct).ConfigureAwait(false))
                    return;
            }
            catch when (!ct.IsCancellationRequested)
            {
                // fallback below
            }
        }

        var full = await CompleteAsync(messages, forceJson, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(full))
            return;

        const int chunkSize = 48;
        for (var i = 0; i < full.Length; i += chunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var len = Math.Min(chunkSize, full.Length - i);
            var chunk = full.Substring(i, len);
            if (chunk.Length > 0)
                onDelta(chunk);
            await Task.Yield();
        }
    }

    private async Task<bool> TryStreamInternalAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, Action<string> onDelta, CancellationToken ct)
    {
        var payload = BuildPayload(messages, forceJson, stream: true);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            if (forceJson && (resp.StatusCode == HttpStatusCode.BadRequest || (int)resp.StatusCode == 422))
                return false;

            resp.EnsureSuccessStatusCode();
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        var streamedAny = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null)
                break;

            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line.Substring(5).Trim();
            if (string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
                break;

            var delta = TryExtractStreamDelta(data);
            if (string.IsNullOrEmpty(delta))
                continue;

            streamedAny = true;
            onDelta(delta);
        }

        return streamedAny;
    }

    private Dictionary<string, object?> BuildPayload(IReadOnlyList<(string role, string content)> messages, bool forceJson, bool stream = false)
    {
        var dict = new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["stream"] = stream,
            ["temperature"] = 0.2,
            ["top_p"] = 0.85,
            ["frequency_penalty"] = 0.2,
            ["presence_penalty"] = 0.05,
            ["max_tokens"] = EstimateMaxTokens(messages, forceJson),
            ["messages"] = messages.Select(m => new Dictionary<string, string>
            {
                ["role"] = m.role,
                ["content"] = m.content
            }).ToArray()
        };

        if (forceJson)
            dict["response_format"] = new Dictionary<string, string> { ["type"] = "json_object" };
        else
            dict["stop"] = new[] { "\nUSER_MESSAGE:", "\nTOOL_RESULTS", "\nDRAFT_ANSWER:", "\nCHAT_TAIL:" };

        return dict;
    }

    private static int EstimateMaxTokens(IReadOnlyList<(string role, string content)> messages, bool forceJson)
    {
        if (forceJson)
            return JsonMaxTokens;

        var joined = string.Join('\n', messages.Select(m => m.content ?? string.Empty));
        if (joined.Contains("SOURCE_SUMMARY:", StringComparison.OrdinalIgnoreCase)
            || joined.Contains("Translate the stored summary", StringComparison.OrdinalIgnoreCase)
            || joined.Contains("summary", StringComparison.OrdinalIgnoreCase))
        {
            return SummaryAnswerMaxTokens;
        }

        return DefaultAnswerMaxTokens;
    }


    private static string? TryExtractStreamDelta(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return null;

            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var delta))
            {
                if (delta.ValueKind == JsonValueKind.Object)
                {
                    if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                        return c.GetString();
                }
                else if (delta.ValueKind == JsonValueKind.String)
                {
                    return delta.GetString();
                }
            }

            if (choice.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
            {
                if (message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    return c.GetString();
            }

            if (choice.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                return text.GetString();
        }
        catch
        {
        }

        return null;
    }

    private static string ExtractContent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;
    }
}
