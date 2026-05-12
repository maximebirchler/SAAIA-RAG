using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

using SAAIA.Client.WinUI.Models;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services;

internal sealed class ApiClientBackendBusyException : HttpRequestException
{
    public ApiClientBackendBusyException(int retryAfterSeconds, string? responseBody)
        : base(
            "Backend RAG search is busy. Retry shortly.",
            inner: null,
            statusCode: HttpStatusCode.TooManyRequests)
    {
        RetryAfterSeconds = Math.Clamp(retryAfterSeconds, 1, 300);
        ResponseBody = responseBody;
    }

    public int RetryAfterSeconds { get; }
    public string? ResponseBody { get; }
}

/// <summary>
/// Client HTTP vers le backend SAAIA.
/// </summary>
public sealed partial class ApiClient
{
    private readonly HttpClient _http = new();
    private readonly object _configGate = new();
    private string _uiLanguage = ClientUiText.NormalizeLanguage(AppSettings.Load().UiLanguage);
    private string _baseUrl = "http://localhost:5122";
    private string _apiKey = "";
    private string _adminKey = "";
    private string _userId = "";
    private readonly object _documentsTreeCacheGate = new();
    private readonly Dictionary<string, DocumentsTreeCacheEntry> _documentsTreeCache = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOpts = ClientJson.CamelCase;

    private sealed class DocumentsTreeCacheEntry
    {
        public string ETag { get; init; } = string.Empty;
        public JsonElement Payload { get; init; }
    }

    private readonly record struct ApiClientConfigSnapshot(string BaseUrl, string ApiKey, string AdminKey, string UserId);

    public void Configure(string baseUrl, string apiKey, string userId, string? adminKey = null)
    {
        lock (_configGate)
        {
            _uiLanguage = ClientUiText.NormalizeLanguage(AppSettings.Load().UiLanguage);
            _baseUrl = (baseUrl ?? "").Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(_baseUrl)) _baseUrl = "http://localhost:5122";
            _apiKey = (apiKey ?? "").Trim();
            _userId = (userId ?? "").Trim();

            if (adminKey is not null)
                _adminKey = adminKey.Trim();
        }

        lock (_documentsTreeCacheGate)
        {
            _documentsTreeCache.Clear();
        }
    }

    public void SetAdminSessionKey(string? adminKey)
    {
        lock (_configGate)
        {
            _adminKey = (adminKey ?? "").Trim();
        }
    }

    public void ClearAdminSessionKey()
    {
        lock (_configGate)
        {
            _adminKey = string.Empty;
        }
    }

    public bool HasAdminKey
    {
        get
        {
            lock (_configGate)
            {
                return !string.IsNullOrWhiteSpace(_adminKey);
            }
        }
    }

    private string RequireUserId()
        => RequireUserId(GetConfigSnapshot());

    private string RequireUserId(ApiClientConfigSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.UserId))
            throw new InvalidOperationException(T("api.error.user_id_not_configured"));
        return snapshot.UserId;
    }

    private string T(string key)
    {
        lock (_configGate)
        {
            return ClientUiText.Get(key, _uiLanguage);
        }
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string path, string? jsonBody = null)
        => NewRequest(GetConfigSnapshot(), method, path, jsonBody);

    private static HttpRequestMessage NewRequest(ApiClientConfigSnapshot snapshot, HttpMethod method, string path, string? jsonBody = null)
    {
        var req = new HttpRequestMessage(method, $"{snapshot.BaseUrl}{path}");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(snapshot.ApiKey))
            req.Headers.Add("X-Api-Key", snapshot.ApiKey);

        if (jsonBody is not null)
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        return req;
    }

    private HttpRequestMessage NewAdminRequest(HttpMethod method, string path, string? jsonBody = null)
        => NewAdminRequest(GetConfigSnapshot(), method, path, jsonBody);

    private static HttpRequestMessage NewAdminRequest(ApiClientConfigSnapshot snapshot, HttpMethod method, string path, string? jsonBody = null)
    {
        var req = new HttpRequestMessage(method, $"{snapshot.BaseUrl}{path}");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(snapshot.AdminKey))
            req.Headers.Add("X-Admin-Key", snapshot.AdminKey);

        if (jsonBody is not null)
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        return req;
    }

    private ApiClientConfigSnapshot GetConfigSnapshot()
    {
        lock (_configGate)
        {
            return new ApiClientConfigSnapshot(_baseUrl, _apiKey, _adminKey, _userId);
        }
    }

    private string SetAdminSessionKeyTemporary(string? adminKey)
    {
        lock (_configGate)
        {
            var previous = _adminKey;
            _adminKey = (adminKey ?? string.Empty).Trim();
            return previous;
        }
    }

    private async Task<HttpResponseMessage> SendWithRateLimitRetryAsync(Func<HttpRequestMessage> reqFactory, CancellationToken ct)
    {
        // Retry once on 429 (rate limit), honoring Retry-After when present.
        const int maxAttempts = 2;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var req = reqFactory();
            var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

            if (resp.StatusCode != (HttpStatusCode)429 || attempt == maxAttempts)
                return resp;

            if (IsRagSearchBusyBody(await TryReadResponseBodyAsync(resp, ct).ConfigureAwait(false)))
                return resp;

            var delay = GetRetryAfterDelay(resp);
            resp.Dispose();

            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct).ConfigureAwait(false);
        }

        // unreachable
        throw new Exception(T("api.error.retry_loop_unexpected_end"));
    }

    private static async Task<string?> TryReadResponseBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static async Task EnsureSuccessOrThrowBackendBusyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode)
            return;

        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var body = await TryReadResponseBodyAsync(resp, ct).ConfigureAwait(false);

            if (IsRagSearchBusyBody(body))
            {
                var retryAfter = GetRetryAfterDelay(resp);
                var retryAfterSeconds = (int)Math.Ceiling(Math.Max(1, retryAfter.TotalSeconds));
                throw new ApiClientBackendBusyException(retryAfterSeconds, body);
            }
        }

        resp.EnsureSuccessStatusCode();
    }

    private static bool IsRagSearchBusyBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            return HasRagSearchBusyProperty(doc.RootElement, "error")
                   || HasRagSearchBusyProperty(doc.RootElement, "code");
        }
        catch
        {
            return false;
        }
    }

    private static bool HasRagSearchBusyProperty(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
           && string.Equals(value.GetString(), "rag_search_busy", StringComparison.OrdinalIgnoreCase);

    private static TimeSpan GetRetryAfterDelay(HttpResponseMessage resp)
    {
        var ra = resp.Headers.RetryAfter;
        var delay = TimeSpan.FromSeconds(2);

        if (ra?.Delta is TimeSpan d)
        {
            delay = d;
        }
        else if (ra?.Date is DateTimeOffset dto)
        {
            var computed = dto - DateTimeOffset.UtcNow;
            if (computed > TimeSpan.Zero)
                delay = computed;
        }

        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        if (delay > TimeSpan.FromSeconds(60)) delay = TimeSpan.FromSeconds(60);
        return delay;
    }


    // ---------------------
    // Diagnostics
    // ---------------------

    public async Task<(bool ok, string raw)> ReadyAsync(CancellationToken ct)
    {
        using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Get, "/ready"), ct).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            return (false, raw);

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("ok", out var p) && p.ValueKind == JsonValueKind.True)
                return (true, raw);
        }
        catch { }

        // If parsing fails, still treat 2xx as OK.
        return (true, raw);
    }


    // ---------------------
    // Chat-store (CDC v2.7)
    // ---------------------

    public async Task<CreateSessionResponse> CreateSessionAsync(string? title, string? clientUser, CancellationToken ct)
    {
        var snapshot = GetConfigSnapshot();
        var body = JsonSerializer.Serialize(new
        {
            userId = RequireUserId(snapshot),
            title,
            clientUser
        }, JsonOpts);

        using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(snapshot, HttpMethod.Post, "/chat/sessions", body), ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<CreateSessionResponse>(json, JsonOpts)
                ?? throw new Exception(T("api.error.invalid_create_session_response"));
    }

    public async Task<List<ChatSessionItem>> ListSessionsAsync(CancellationToken ct, int limit = 100, int offset = 0)
    {
        var snapshot = GetConfigSnapshot();
        var uid = Uri.EscapeDataString(RequireUserId(snapshot));
        var lim = Math.Clamp(limit, 1, 200);
        var off = Math.Max(0, offset);

        using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(snapshot, HttpMethod.Get, $"/chat/sessions?userId={uid}&limit={lim}&offset={off}"), ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        var list = new List<ChatSessionItem>();

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
                return DateTime.MinValue;
            }

            DateTime? GetDateTimeNullable(string a, string b)
            {
                if (el.TryGetProperty(a, out var p) && p.ValueKind == JsonValueKind.String && DateTime.TryParse(p.GetString(), out var dt1))
                    return dt1.ToUniversalTime();
                if (el.TryGetProperty(b, out p) && p.ValueKind == JsonValueKind.String && DateTime.TryParse(p.GetString(), out var dt2))
                    return dt2.ToUniversalTime();
                if (el.TryGetProperty(a, out p) && p.ValueKind == JsonValueKind.Null) return null;
                if (el.TryGetProperty(b, out p) && p.ValueKind == JsonValueKind.Null) return null;
                return null;
            }

            var sid = GetString("sessionId", "SessionId");
            if (string.IsNullOrWhiteSpace(sid)) continue;

            var title = GetString("title", "Title");
            if (string.IsNullOrWhiteSpace(title)) title = null;

            var clientUser = GetString("clientUser", "ClientUser");
            if (string.IsNullOrWhiteSpace(clientUser)) clientUser = null;

            var createdAt = GetDateTime("createdAt", "CreatedAt");
            var updatedAt = GetDateTime("updatedAt", "UpdatedAt");
            var lastMsg = GetDateTimeNullable("lastMessageAt", "LastMessageAt");

            list.Add(new ChatSessionItem
            {
                SessionId = sid,
                Title = title,
                ClientUser = clientUser,
                CreatedAtUtc = createdAt,
                UpdatedAtUtc = updatedAt,
                LastMessageAtUtc = lastMsg
            });
        }

        return list;
    }

    public async Task UpdateSessionTitleAsync(string sessionId, string? title, CancellationToken ct)
    {
        var snapshot = GetConfigSnapshot();
        var uid = Uri.EscapeDataString(RequireUserId(snapshot));

        var body = JsonSerializer.Serialize(new
        {
            title
        }, JsonOpts);

        using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(snapshot, HttpMethod.Patch, $"/chat/sessions/{sessionId}?userId={uid}", body), ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken ct)
    {
        var snapshot = GetConfigSnapshot();
        var uid = Uri.EscapeDataString(RequireUserId(snapshot));

        using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(snapshot, HttpMethod.Delete, $"/chat/sessions/{sessionId}?userId={uid}"), ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<ChatMessageItem?> AddMessageAsync(string sessionId, string role, string content, object? sources, CancellationToken ct, string? statusNote = null, string? progressText = null, ChatTrackingMeta? trackingMeta = null)
    {
        var snapshot = GetConfigSnapshot();
        string? sourcesJson = null;
        if (sources is string s)
            sourcesJson = string.IsNullOrWhiteSpace(s) ? null : s;
        else if (sources is not null)
            sourcesJson = JsonSerializer.Serialize(sources, JsonOpts);

        var trackingMetaJson = trackingMeta is null ? null : JsonSerializer.Serialize(trackingMeta, JsonOpts);

        var body = JsonSerializer.Serialize(new
        {
            userId = RequireUserId(snapshot),
            role,
            content,
            sourcesJson,
            statusNote,
            progressText,
            trackingMetaJson
        }, JsonOpts);

        using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(snapshot, HttpMethod.Post, $"/chat/sessions/{sessionId}/messages", body), ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        using var doc = JsonDocument.Parse(json);
        return ParseChatMessage(doc.RootElement);
    }

    public async Task<ChatMessageItem?> PatchMessageAsync(string messageId, string? content, string? statusNote, string? progressText, ChatTrackingMeta? trackingMeta, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            throw new ArgumentException(T("api.error.message_id_required"), nameof(messageId));

        var snapshot = GetConfigSnapshot();
        var body = JsonSerializer.Serialize(new
        {
            userId = RequireUserId(snapshot),
            content,
            statusNote,
            progressText,
            trackingMetaJson = trackingMeta is null ? null : JsonSerializer.Serialize(trackingMeta, JsonOpts)
        }, JsonOpts);

        using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(snapshot, HttpMethod.Patch, $"/chat/messages/{messageId}" , body), ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        using var doc = JsonDocument.Parse(json);
        return ParseChatMessage(doc.RootElement);
    }

    public async Task<JsonElement> ChatMessageTrackingAsync(string messageId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            throw new ArgumentException(T("api.error.message_id_required"), nameof(messageId));

        var snapshot = GetConfigSnapshot();
        var uid = Uri.EscapeDataString(RequireUserId(snapshot));
        using var resp = await SendWithRateLimitRetryAsync(
            () => NewRequest(snapshot, HttpMethod.Get, $"/chat/messages/{Uri.EscapeDataString(messageId)}/tracking?userId={uid}"),
            ct).ConfigureAwait(false);

        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public async Task<List<ChatMessageItem>> ListMessagesAsync(string sessionId, CancellationToken ct, int limit = 500)
    {
        var snapshot = GetConfigSnapshot();
        var uid = Uri.EscapeDataString(RequireUserId(snapshot));
        var lim = Math.Clamp(limit, 1, 1000);

        using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(snapshot, HttpMethod.Get, $"/chat/sessions/{sessionId}/messages?userId={uid}&limit={lim}"), ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        var list = new List<ChatMessageItem>();

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var item = ParseChatMessage(el);
            if (item is not null)
                list.Add(item);
        }

        return list;
    }

    private static ChatMessageItem? ParseChatMessage(JsonElement el)
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
        var messageId = GetString("messageId", "MessageId");
        if (string.IsNullOrWhiteSpace(messageId)) messageId = null;

        var statusNote = GetString("statusNote", "StatusNote");
        if (string.IsNullOrWhiteSpace(statusNote)) statusNote = null;

        var progressText = GetString("progressText", "ProgressText");
        if (string.IsNullOrWhiteSpace(progressText)) progressText = null;

        string? sourcesJson = null;
        if (TryReadSources(el, out var src))
            sourcesJson = src;

        ChatTrackingMeta? trackingMeta = null;
        if (TryGetAny(el, out var tm, "trackingMetaJson", "TrackingMetaJson"))
        {
            var raw = ReadJsonStringOrRaw(tm);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                try { trackingMeta = JsonSerializer.Deserialize<ChatTrackingMeta>(raw, JsonOpts); }
                catch { }
            }
        }

        return new ChatMessageItem
        {
            MessageId = messageId,
            Role = role,
            Content = content,
            CreatedAt = createdAt,
            SourcesJson = sourcesJson,
            StatusNote = statusNote,
            ProgressText = progressText,
            TrackingMeta = trackingMeta
        };

        static bool TryReadSources(JsonElement el, out string? sourcesJson)
        {
            sourcesJson = null;

            if (TryGetAny(el, out var sj, "sourcesJson", "SourcesJson"))
            {
                sourcesJson = ReadJsonStringOrRaw(sj);
                return !string.IsNullOrWhiteSpace(sourcesJson);
            }

            if (TryGetAny(el, out var s, "sources", "Sources"))
            {
                sourcesJson = ReadJsonStringOrRaw(s);
                return !string.IsNullOrWhiteSpace(sourcesJson);
            }

            return false;
        }

        static bool TryGetAny(JsonElement el, out JsonElement value, params string[] names)
        {
            foreach (var n in names)
            {
                if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(n, out value))
                    return true;
            }
            value = default;
            return false;
        }

        static string? ReadJsonStringOrRaw(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Null) return null;

            // Cas principal : le backend renvoie une string qui contient du JSON.
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }

            // Sinon : objet/array => raw json.
            if (el.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                return el.GetRawText();

            // fallback: number/bool => string
            return el.ToString();
        }
    }
    /// <summary>
    /// Optional admin endpoint (may not exist on older builds): list chunks for debug.
    /// When not supported, returns { items:[], nextCursor:null, error:"not_supported" }.
    /// </summary>
    public async Task<JsonElement> RagDebugScrollAsync(string? cursor, int limit, string? docPath, CancellationToken ct)
    {
        var lim = Math.Clamp(limit, 1, 1000);
        var qs = new List<string> { $"limit={lim}" };
        if (!string.IsNullOrWhiteSpace(cursor))
            qs.Add($"cursor={Uri.EscapeDataString(cursor)}");
        if (!string.IsNullOrWhiteSpace(docPath))
            qs.Add($"docPath={Uri.EscapeDataString(docPath.Trim().Replace('\\', '/').TrimStart('/'))}");

        var path = "/rag/debug/scroll?" + string.Join("&", qs);
        using var resp = await SendWithRateLimitRetryAsync(() => NewAdminRequest(HttpMethod.Get, path), ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            using var docNF = JsonDocument.Parse("{\"items\":[],\"nextCursor\":null,\"error\":\"not_supported\"}");
            return docNF.RootElement.Clone();
        }

        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
