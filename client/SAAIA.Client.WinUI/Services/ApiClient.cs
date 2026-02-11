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

/// <summary>
/// Client HTTP vers le backend SAAIA.
///
/// Contrats importants (CDC v2.7) :
/// - /rag/search -> RagSearchResponse (items + metrics)
/// - chat-store -> userId obligatoire (body + query...)
/// </summary>
public sealed class ApiClient
{
    private readonly HttpClient _http = new();
    private string _baseUrl = "http://localhost:5122";
    private string _apiKey = "";
    private string _userId = "";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public void Configure(string baseUrl, string apiKey, string userId)
    {
        _baseUrl = (baseUrl ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(_baseUrl)) _baseUrl = "http://localhost:5122";
        _apiKey = (apiKey ?? "").Trim();
        _userId = (userId ?? "").Trim();
    }

    private string RequireUserId()
    {
        if (string.IsNullOrWhiteSpace(_userId))
            throw new InvalidOperationException("userId not configured");
        return _userId;
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

    // ---------------------
    // Chat-store (CDC v2.7)
    // ---------------------

    public async Task<CreateSessionResponse> CreateSessionAsync(string? title, string? clientUser, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            userId = RequireUserId(),
            title,
            clientUser
        }, JsonOpts);

        using var req = NewRequest(HttpMethod.Post, "/chat/sessions", body);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<CreateSessionResponse>(json, JsonOpts)
               ?? throw new Exception("Invalid create session response");
    }

    public async Task AddMessageAsync(string sessionId, string role, string content, object? sources, CancellationToken ct)
    {
        string? sourcesJson = null;
        if (sources is string s)
            sourcesJson = string.IsNullOrWhiteSpace(s) ? null : s;
        else if (sources is not null)
            sourcesJson = JsonSerializer.Serialize(sources, JsonOpts);

        var body = JsonSerializer.Serialize(new
        {
            userId = RequireUserId(),
            role,
            content,
            sourcesJson
        }, JsonOpts);

        using var req = NewRequest(HttpMethod.Post, $"/chat/sessions/{sessionId}/messages", body);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<List<ChatMessageItem>> ListMessagesAsync(string sessionId, CancellationToken ct, int limit = 500)
    {
        var uid = Uri.EscapeDataString(RequireUserId());
        var lim = Math.Clamp(limit, 1, 1000);

        using var req = NewRequest(HttpMethod.Get, $"/chat/sessions/{sessionId}/messages?userId={uid}&limit={lim}");
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct);

        // Le backend retourne des rows Dapper (keys = Role/Content/CreatedAt/Sources)
        // -> parsing tolérant pour accepter camelCase ou PascalCase.
        using var doc = JsonDocument.Parse(json);
        var list = new List<ChatMessageItem>();

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            string GetString(string a, string b)
            {
                if (el.TryGetProperty(a, out var p) && p.ValueKind == JsonValueKind.String) return p.GetString() ?? "";
                if (el.TryGetProperty(b, out p) && p.ValueKind == JsonValueKind.String) return p.GetString() ?? "";
                return "";
            }

            DateTime GetDateTime(string a, string b)
            {
                if (el.TryGetProperty(a, out var p) && p.ValueKind == JsonValueKind.String && DateTime.TryParse(p.GetString(), out var dt1))
                    return dt1.ToUniversalTime();
                if (el.TryGetProperty(b, out p) && p.ValueKind == JsonValueKind.String && DateTime.TryParse(p.GetString(), out var dt2))
                    return dt2.ToUniversalTime();
                return DateTime.UtcNow;
            }

            var role = GetString("role", "Role");
            if (string.IsNullOrWhiteSpace(role)) role = "user";

            var content = GetString("content", "Content");
            var createdAt = GetDateTime("createdAt", "CreatedAt");

            string? sourcesJson = null;
            if (el.TryGetProperty("sourcesJson", out var sj) && sj.ValueKind != JsonValueKind.Null)
                sourcesJson = sj.GetRawText();
            else if (el.TryGetProperty("SourcesJson", out sj) && sj.ValueKind != JsonValueKind.Null)
                sourcesJson = sj.GetRawText();
            else if (el.TryGetProperty("sources", out var s) && s.ValueKind != JsonValueKind.Null)
                sourcesJson = s.GetRawText();
            else if (el.TryGetProperty("Sources", out s) && s.ValueKind != JsonValueKind.Null)
                sourcesJson = s.GetRawText();

            list.Add(new ChatMessageItem
            {
                Role = role,
                Content = content,
                CreatedAt = createdAt,
                SourcesJson = sourcesJson
            });
        }

        return list;
    }

    // ---------------------
    // RAG (CDC v2.7)
    // ---------------------

    public async Task<RagSearchResponse> RagSearchAsync(string query, string? category, int topK, string? mode, CancellationToken ct)
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
