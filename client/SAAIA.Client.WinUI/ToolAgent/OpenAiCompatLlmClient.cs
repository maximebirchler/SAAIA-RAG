using System.Net;
using System.Text;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

/// <summary>
/// Minimal OpenAI-compatible chat client.
/// 
/// IMPORTANT: Many local OpenAI-compatible servers do NOT support the "response_format" field.
/// When forceJson=true, we try with response_format=json_object first, then automatically retry
/// without it if the server rejects the request (400/422).
/// </summary>
public sealed class OpenAiCompatLlmClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _model;

    public OpenAiCompatLlmClient(HttpClient http, string baseUrl, string model)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _model = model;
    }

    public async Task<string> CompleteAsync(IReadOnlyList<(string role, string content)> messages, bool forceJson, CancellationToken ct)
    {
        // Build payload as a dictionary to omit unsupported fields cleanly.
        var payload = BuildPayload(messages, forceJson);

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/chat/completions")
        {
            Content = content
        };

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            // Retry without response_format when the server rejects the field.
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

    private Dictionary<string, object?> BuildPayload(IReadOnlyList<(string role, string content)> messages, bool forceJson)
    {
        var dict = new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["stream"] = false,
            ["temperature"] = 0.2,
            ["messages"] = messages.Select(m => new Dictionary<string, string>
            {
                ["role"] = m.role,
                ["content"] = m.content
            }).ToArray()
        };

        if (forceJson)
            dict["response_format"] = new Dictionary<string, string> { ["type"] = "json_object" };

        return dict;
    }

    private static string ExtractContent(string json)
    {
        using var doc = JsonDocument.Parse(json);

        // OpenAI schema: choices[0].message.content
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
    }
}
