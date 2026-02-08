using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using SAAIA.Client.WinUI.Models;

namespace SAAIA.Client.WinUI.Services;

public sealed class ApiClient
{
    private readonly HttpClient _http = new();
    private string _baseUrl = "http://localhost:5122";
    private string _apiKey = "";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public void Configure(string baseUrl, string apiKey)
    {
        _baseUrl = baseUrl.Trim().TrimEnd('/');
        _apiKey = apiKey.Trim();
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string path, string? jsonBody = null)
    {
        var req = new HttpRequestMessage(method, $"{_baseUrl}{path}");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(_apiKey))
            req.Headers.Add("X-Api-Key", _apiKey);

        if (jsonBody is not null)
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        return req;
    }

    public async Task<CreateSessionResponse> CreateSessionAsync(string? title, string? clientUser, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { title, clientUser }, JsonOpts);
        using var req = NewRequest(HttpMethod.Post, "/chat/sessions", body);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<CreateSessionResponse>(json, JsonOpts)
               ?? throw new Exception("Invalid create session response");
    }

    public async Task AddMessageAsync(string sessionId, string role, string content, object? sources, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { role, content, sources }, JsonOpts);
        using var req = NewRequest(HttpMethod.Post, $"/chat/sessions/{sessionId}/messages", body);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<List<ChatMessageItem>> ListMessagesAsync(string sessionId, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Get, $"/chat/sessions/{sessionId}/messages?limit=500");
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        var list = new List<ChatMessageItem>();

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var role = el.GetProperty("Role").GetString() ?? "user";
            var content = el.GetProperty("Content").GetString() ?? "";
            var createdAt = el.GetProperty("CreatedAt").GetDateTime();

            string? sourcesJson = null;
            if (el.TryGetProperty("Sources", out var s) && s.ValueKind != JsonValueKind.Null)
                sourcesJson = s.GetRawText();

            list.Add(new ChatMessageItem
            {
                Role = role,
                Content = content,
                SourcesJson = sourcesJson,
                CreatedAt = createdAt
            });
        }

        return list;
    }

    public async Task<RagSearchResponse> RagSearchAsync(string query, string? category, int topK, string mode, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            query,
            category,
            topK,
            mode
        }, JsonOpts);

        using var req = NewRequest(HttpMethod.Post, "/rag/search", body);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<RagSearchResponse>(json, JsonOpts)
               ?? throw new Exception("Invalid rag search response");
    }
}
