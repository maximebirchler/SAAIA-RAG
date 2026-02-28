using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

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
        var payload = new
        {
            model = _model,
            stream = false,
            temperature = 0.2,
            response_format = forceJson ? new { type = "json_object" } : null,
            messages = messages.Select(m => new { role = m.role, content = m.content }).ToArray()
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/chat/completions");
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        // OpenAI schema: choices[0].message.content
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
    }
}