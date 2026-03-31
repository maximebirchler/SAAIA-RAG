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

/// <summary>
/// Client HTTP vers le backend SAAIA.
/// </summary>
public sealed partial class ApiClient
{
    private readonly HttpClient _http = new();
    private string _baseUrl = "http://localhost:5122";
    private string _apiKey = "";
    private string _adminKey = "";
    private string _userId = "";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public void Configure(string baseUrl, string apiKey, string userId, string? adminKey = null)
    {
        _baseUrl = (baseUrl ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(_baseUrl)) _baseUrl = "http://localhost:5122";
        _apiKey = (apiKey ?? "").Trim();
        _userId = (userId ?? "").Trim();

        if (adminKey is not null)
            _adminKey = (adminKey ?? "").Trim();
    }

    public void SetAdminSessionKey(string? adminKey)
        => _adminKey = (adminKey ?? "").Trim();

    public void ClearAdminSessionKey()
        => _adminKey = string.Empty;

    public bool HasAdminKey => !string.IsNullOrWhiteSpace(_adminKey);

    public bool HasAdminSessionKey => HasAdminKey;

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

    private HttpRequestMessage NewAdminRequest(HttpMethod method, string path, string? jsonBody = null)
    {
        var req = new HttpRequestMessage(method, $"{_baseUrl}{path}");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(_adminKey))
            req.Headers.Add("X-Admin-Key", _adminKey);

        if (jsonBody is not null)
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        return req;
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

            var delay = GetRetryAfterDelay(resp);
            resp.Dispose();

            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct).ConfigureAwait(false);
        }

        // unreachable
        throw new Exception("Unexpected send retry loop termination.");
    }

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
        var body = JsonSerializer.Serialize(new
        {
            userId = RequireUserId(),
            title,
            clientUser
        }, JsonOpts);

        using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Post, "/chat/sessions", body), ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<CreateSessionResponse>(json, JsonOpts)
               ?? throw new Exception("Invalid create session response");
    }

    public async Task<List<ChatSessionItem>> ListSessionsAsync(CancellationToken ct, int limit = 100, int offset = 0)
    {
        var uid = Uri.EscapeDataString(RequireUserId());
        var lim = Math.Clamp(limit, 1, 200);
        var off = Math.Max(0, offset);

        using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Get, $"/chat/sessions?userId={uid}&limit={lim}&offset={off}"), ct);
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
        var uid = Uri.EscapeDataString(RequireUserId());

        var body = JsonSerializer.Serialize(new
        {
            title
        }, JsonOpts);

        using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Patch, $"/chat/sessions/{sessionId}?userId={uid}", body), ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken ct)
    {
        var uid = Uri.EscapeDataString(RequireUserId());

        using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Delete, $"/chat/sessions/{sessionId}?userId={uid}"), ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<ChatMessageItem?> AddMessageAsync(string sessionId, string role, string content, object? sources, CancellationToken ct, string? statusNote = null, string? progressText = null, ChatTrackingMeta? trackingMeta = null)
    {
        string? sourcesJson = null;
        if (sources is string s)
            sourcesJson = string.IsNullOrWhiteSpace(s) ? null : s;
        else if (sources is not null)
            sourcesJson = JsonSerializer.Serialize(sources, JsonOpts);

        var trackingMetaJson = trackingMeta is null ? null : JsonSerializer.Serialize(trackingMeta, JsonOpts);

        var body = JsonSerializer.Serialize(new
        {
            userId = RequireUserId(),
            role,
            content,
            sourcesJson,
            statusNote,
            progressText,
            trackingMetaJson
        }, JsonOpts);

        using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Post, $"/chat/sessions/{sessionId}/messages", body), ct);
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
            throw new ArgumentException("messageId is required", nameof(messageId));

        var body = JsonSerializer.Serialize(new
        {
            userId = RequireUserId(),
            content,
            statusNote,
            progressText,
            trackingMetaJson = trackingMeta is null ? null : JsonSerializer.Serialize(trackingMeta, JsonOpts)
        }, JsonOpts);

        using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Patch, $"/chat/messages/{messageId}" , body), ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        using var doc = JsonDocument.Parse(json);
        return ParseChatMessage(doc.RootElement);
    }

    public async Task<List<ChatMessageItem>> ListMessagesAsync(string sessionId, CancellationToken ct, int limit = 500)
    {
        var uid = Uri.EscapeDataString(RequireUserId());
        var lim = Math.Clamp(limit, 1, 1000);

        using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Get, $"/chat/sessions/{sessionId}/messages?userId={uid}&limit={lim}"), ct);
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

            // ✅ cas principal : le backend renvoie une string qui contient du JSON
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }

            // ✅ sinon: objet/array => raw json
            if (el.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                return el.GetRawText();

            // fallback: number/bool => string
            return el.ToString();
        }
    }


    // ---------------------
    // Documents catalog (read-only)
    // ---------------------

    public async Task<DocumentsCatalogResponse> DocumentsCatalogAsync(string? category, string? q, int limit, int offset, CancellationToken ct)
{
    var lim = Math.Clamp(limit, 1, 2000);
    var off = Math.Max(0, offset);

    var qs = new List<string>
    {
        $"limit={lim}",
        $"offset={off}"
    };

    if (!string.IsNullOrWhiteSpace(category))
        qs.Add($"category={Uri.EscapeDataString(category.Trim())}");

    if (!string.IsNullOrWhiteSpace(q))
        qs.Add($"q={Uri.EscapeDataString(q.Trim())}");

    // Spec tool: documents.list/catalog. Some older backends expose only GET /documents (admin) and not /documents/catalog (user).
    // We try /documents/catalog first, then fallback to /documents to avoid hard failure.
    var pathCatalog = "/documents/catalog?" + string.Join("&", qs);
    using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Get, pathCatalog), ct).ConfigureAwait(false);

    if (resp.StatusCode != HttpStatusCode.NotFound)
    {
        if (!resp.IsSuccessStatusCode)
        {
            var bodyErr = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"DocumentsCatalog failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {bodyErr}",
                null,
                resp.StatusCode);
        }

        var jsonOk = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<DocumentsCatalogResponse>(jsonOk, JsonOpts)
               ?? throw new Exception("Invalid documents catalog response");
    }

    // Fallback: GET /documents (often admin-only)
    var pathFallback = "/documents?" + string.Join("&", qs);
    using var resp2 = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Get, pathFallback), ct).ConfigureAwait(false);

    if (!resp2.IsSuccessStatusCode)
    {
        var bodyErr = await resp2.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new HttpRequestException(
            $"Documents fallback failed: {(int)resp2.StatusCode} {resp2.ReasonPhrase}. Body: {bodyErr}",
            null,
            resp2.StatusCode);
    }

    var json = await resp2.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    return JsonSerializer.Deserialize<DocumentsCatalogResponse>(json, JsonOpts)
           ?? throw new Exception("Invalid documents catalog response");
}



    // ---------------------

    // ---------------------
    // Categories (tool-agent)
    // ---------------------

    /// <summary>
    /// Tool-agent friendly: returns raw JSON as <see cref="JsonElement"/> (cloned) so it can be stored safely.
    /// Expected shape: ["atex","general",...]
    /// </summary>
    public async Task<JsonElement> RagCategoriesAsync(CancellationToken ct)
    {
        using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Get, "/rag/categories"), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Convenience for call-sites that want a list of strings.
    /// </summary>
    public async Task<List<string>> RagCategoriesListAsync(CancellationToken ct)
    {
        var el = await RagCategoriesAsync(ct).ConfigureAwait(false);
        if (el.ValueKind != JsonValueKind.Array) return new List<string>();

        var list = new List<string>();
        foreach (var it in el.EnumerateArray())
        {
            if (it.ValueKind == JsonValueKind.String)
            {
                var s = (it.GetString() ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            }
        }

        return list
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---------------------
    // Documents (tool-agent)
    // ---------------------

    /// <summary>
    /// Tool-agent friendly documents list: returns JSON object with items + paging.
    /// Adds a stable pdfRef per item (PDF01, PDF02, ...), based on offset.
    /// </summary>
    public Task<JsonElement> DocumentsListAsync(string? categoryPath, string? q, int limit, int offset, CancellationToken ct)
        => DocumentsListAsync(categoryPath, null, q, limit, offset, ct);

    
public async Task<JsonElement> DocumentsListAsync(string? categoryPath, string? categoryRef, string? q, int limit, int offset, CancellationToken ct)
    {
        var lim = Math.Clamp(limit, 1, 2000);
        var off = Math.Max(0, offset);

        var catalogQs = new List<string>
        {
            $"pageSize={Math.Min(lim, 200)}",
            "orderby=name_asc"
        };

        if (!string.IsNullOrWhiteSpace(categoryPath))
            catalogQs.Add($"categoryPath={Uri.EscapeDataString(categoryPath.Trim().Replace('\\', '/').Trim('/'))}");
        if (!string.IsNullOrWhiteSpace(categoryRef))
            catalogQs.Add($"categoryRef={Uri.EscapeDataString(categoryRef.Trim())}");
        if (!string.IsNullOrWhiteSpace(q))
            catalogQs.Add($"q={Uri.EscapeDataString(q.Trim())}");
        if (off > 0)
        {
            var cursorPayload = JsonSerializer.Serialize(new { PageSize = Math.Min(lim, 200), Offset = off, CategoryPath = categoryPath, CategoryRef = categoryRef, Query = q, OrderBy = "name_asc" });
            var cursor = Convert.ToBase64String(Encoding.UTF8.GetBytes(cursorPayload)).TrimEnd('=');
            catalogQs.Add($"cursor={Uri.EscapeDataString(cursor)}");
        }

        using var catalogResp = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Get, "/catalog/documents?" + string.Join("&", catalogQs)), ct).ConfigureAwait(false);
        if (catalogResp.StatusCode != HttpStatusCode.NotFound)
        {
            catalogResp.EnsureSuccessStatusCode();
            var catalogJson = await catalogResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var catalogDoc = JsonDocument.Parse(catalogJson);
            return NormalizeCatalogDocumentsResponse(catalogDoc.RootElement, lim, off);
        }

        var qs = new List<string>
        {
            $"limit={lim}",
            $"offset={off}"
        };

        if (!string.IsNullOrWhiteSpace(categoryPath))
            qs.Add($"categoryPath={Uri.EscapeDataString(categoryPath.Trim().Replace('\\', '/').Trim('/'))}");
        if (!string.IsNullOrWhiteSpace(categoryRef))
            qs.Add($"categoryRef={Uri.EscapeDataString(categoryRef.Trim())}");
        if (!string.IsNullOrWhiteSpace(q))
            qs.Add($"q={Uri.EscapeDataString(q.Trim())}");

        var pathCatalog = "/documents/catalog?" + string.Join("&", qs);
        using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Get, pathCatalog), ct).ConfigureAwait(false);

        string json;
        if (resp.StatusCode != HttpStatusCode.NotFound)
        {
            resp.EnsureSuccessStatusCode();
            json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        else
        {
            var pathFallback = "/documents?" + string.Join("&", qs);
            using var resp2 = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Get, pathFallback), ct).ConfigureAwait(false);
            resp2.EnsureSuccessStatusCode();
            json = await resp2.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }

        using var doc = JsonDocument.Parse(json);
        return NormalizeLegacyDocumentsResponse(doc.RootElement, lim, off);
    }

    private JsonElement NormalizeCatalogDocumentsResponse(JsonElement root, int limit, int offset)
    {
        var value = root.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array ? arr : default;
        var newItems = new List<object>();
        if (value.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var it in value.EnumerateArray())
            {
                var docId = TryGetString(it, "docId") ?? string.Empty;
                var docPath = TryGetString(it, "docPath") ?? string.Empty;
                var docName = TryGetString(it, "canonicalName") ?? TryGetString(it, "docName") ?? string.Empty;
                var cat = TryGetString(it, "categoryCanonicalName") ?? string.Empty;
                var itemCategoryPath = TryGetString(it, "categoryPath") ?? string.Empty;
                var pages = TryGetInt(it, "pages") ?? TryGetInt(it, "pageCount");
                var pdfIndex = offset + i + 1;
                var pdfRef = $"PDF{pdfIndex:00}";

                var normalizedDocPath = NormalizeDocPath(docPath);
                var normalizedCategoryPath = NormalizeCategoryPathForDocuments(itemCategoryPath, normalizedDocPath);
                var normalizedCategory = !string.IsNullOrWhiteSpace(cat)
                    ? NormalizeTopLevelCategoryForDocuments(cat)
                    : NormalizeTopLevelCategoryForDocuments(normalizedCategoryPath);

                newItems.Add(new
                {
                    pdfRef,
                    docId,
                    docPath = normalizedDocPath,
                    docName,
                    category = normalizedCategory,
                    categoryPath = normalizedCategoryPath,
                    pages
                });
                i++;
            }
        }

        int? total = null;
        if (root.TryGetProperty("totals", out var totals))
            total = TryGetInt(totals, "total") ?? TryGetInt(totals, "documents");

        var endOfList = !root.TryGetProperty("nextLink", out var nextLink) || nextLink.ValueKind == JsonValueKind.Null || string.IsNullOrWhiteSpace(nextLink.GetString());
        var normalized = new { items = newItems, limit, offset, total, endOfList };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(normalized, JsonOpts));
        return doc.RootElement.Clone();
    }

    private JsonElement NormalizeLegacyDocumentsResponse(JsonElement root, int limit, int offset)
    {
        var itemsEl = root.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array ? arr : default;
        var total = root.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : (int?)null;
        var newItems = new List<object>();
        if (itemsEl.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var it in itemsEl.EnumerateArray())
            {
                var status = TryGetString(it, "status") ?? string.Empty;
                if (string.Equals(status, "missing", StringComparison.OrdinalIgnoreCase)) { i++; continue; }

                var docId = TryGetString(it, "docId") ?? TryGetString(it, "DocId") ?? string.Empty;
                var docPath = TryGetString(it, "docPath") ?? TryGetString(it, "DocPath") ?? string.Empty;
                var docName = TryGetString(it, "docName") ?? TryGetString(it, "DocName") ?? string.Empty;
                var cat = TryGetString(it, "category") ?? TryGetString(it, "Category") ?? string.Empty;
                var itemCategoryPath = TryGetString(it, "categoryPath") ?? TryGetString(it, "CategoryPath") ?? string.Empty;
                var pages = TryGetInt(it, "pages") ?? TryGetInt(it, "pageCount");
                var pdfIndex = offset + i + 1;
                var pdfRef = $"PDF{pdfIndex:00}";

                var normalizedDocPath = NormalizeDocPath(docPath);
                var normalizedCategoryPath = NormalizeCategoryPathForDocuments(itemCategoryPath, normalizedDocPath);
                var normalizedCategory = !string.IsNullOrWhiteSpace(cat)
                    ? NormalizeTopLevelCategoryForDocuments(cat)
                    : NormalizeTopLevelCategoryForDocuments(normalizedCategoryPath);

                newItems.Add(new
                {
                    pdfRef,
                    docId,
                    docPath = normalizedDocPath,
                    docName,
                    category = normalizedCategory,
                    categoryPath = normalizedCategoryPath,
                    pages
                });
                i++;
            }
        }

        var endOfList = newItems.Count < limit;
        if (total.HasValue)
            endOfList = (offset + newItems.Count) >= total.Value;

        var normalized = new { items = newItems, limit, offset, total, endOfList };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(normalized, JsonOpts));
        return doc.RootElement.Clone();
    }

    public Task<JsonElement> DocumentsSearchAsync(string q, string? categoryPath, int limit, int offset, CancellationToken ct)
        => DocumentsListAsync(categoryPath, null, q, limit, offset, ct);

    public Task<JsonElement> DocumentsSearchAsync(string q, string? categoryPath, string? categoryRef, int limit, int offset, CancellationToken ct)
        => DocumentsListAsync(categoryPath, categoryRef, q, limit, offset, ct);

    private static string NormalizeCategoryPathForDocuments(string? categoryPath, string? docPath)
    {
        var normalized = (categoryPath ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/').TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(normalized))
            return normalized;

        var path = NormalizeDocPath(docPath ?? string.Empty);
        var idx = path.LastIndexOf('/');
        return idx > 0 ? path.Substring(0, idx) : string.Empty;
    }

    private static string NormalizeTopLevelCategoryForDocuments(string? categoryPath)
    {
        var normalized = (categoryPath ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/').TrimEnd('/');
        var idx = normalized.IndexOf('/');
        return idx > 0 ? normalized.Substring(0, idx) : normalized;
    }

    public async Task<JsonElement> DocumentsGetAsync(string docId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(docId))
            throw new ArgumentException("docId is required", nameof(docId));

        // Prefer user-safe catalog detail endpoint.
        var pathCatalog = $"/documents/catalog/{Uri.EscapeDataString(docId)}";
        using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Get, pathCatalog), ct).ConfigureAwait(false);

        string json;
        if (resp.StatusCode != HttpStatusCode.NotFound)
        {
            resp.EnsureSuccessStatusCode();
            json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        else
        {
            // Fallback: admin-only endpoint (support / older builds)
            var pathAdmin = $"/documents/{Uri.EscapeDataString(docId)}";
            using var resp2 = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Get, pathAdmin), ct).ConfigureAwait(false);
            resp2.EnsureSuccessStatusCode();
            json = await resp2.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Tool-agent friendly documents tree (categories multi-niveaux).
    /// Expected shape: { path: "...", markdown: "...", source: "snapshot"|"documents" }
    /// </summary>
    public async Task<JsonElement> DocumentsTreeAsync(string? path, int depth, string? format, CancellationToken ct)
    {
        var d = Math.Clamp(depth, 1, 50);
        var fmt = (format ?? "markdown").Trim().ToLowerInvariant();
        if (fmt is not ("markdown" or "json")) fmt = "markdown";

        var qs = new List<string>
        {
            $"depth={d}",
            $"format={Uri.EscapeDataString(fmt)}"
        };

        if (!string.IsNullOrWhiteSpace(path))
        {
            var p = (path ?? "").Replace('\\', '/').Trim().TrimStart('/');
            if (!string.IsNullOrWhiteSpace(p))
                qs.Add($"path={Uri.EscapeDataString(p)}");
        }

        var url = "/documents/tree?" + string.Join("&", qs);
        using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Get, url), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }


    // ---------------------
    // RAG search (tool-agent)
    // ---------------------

    /// <summary>
    /// Tool-agent friendly RAG search: raw JSON as JsonElement (cloned).
    /// </summary>
    public async Task<JsonElement> RagSearchAsync(string query, int topK, string? category, CancellationToken ct)
    {
        return await RagSearchToolAsync(query, topK, category, mode: "balanced", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Tool-agent friendly RAG search with explicit mode (balanced|precise|fast).
    /// </summary>
    public async Task<JsonElement> RagSearchToolAsync(string query, int topK, string? category, string? mode, CancellationToken ct)
    {
        var m = (mode ?? "balanced").Trim().ToLowerInvariant();
        if (m is not ("balanced" or "precise" or "fast"))
            m = "balanced";

        var body = JsonSerializer.Serialize(new
        {
            query,
            category,
            topK,
            mode = m
        }, JsonOpts);

        using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Post, "/rag/search", body), ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Helper for ToolAgent: parse the last documents listing into a stable in-memory mapping.
    /// </summary>
    public List<ToolMemory.DocumentItem> ParseDocumentItems(JsonElement response)
    {
        var items = new List<ToolMemory.DocumentItem>();

        if (!response.TryGetProperty("items", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return items;

        foreach (var it in arr.EnumerateArray())
        {
            var pdfRef = TryGetString(it, "pdfRef") ?? "";
            var docId = TryGetString(it, "docId") ?? "";
            var docPath = TryGetString(it, "docPath") ?? "";
            var docName = TryGetString(it, "docName") ?? "";
            var cat = TryGetString(it, "category") ?? "";
            var pages = TryGetInt(it, "pages");

            items.Add(new ToolMemory.DocumentItem
            {
                PdfRef = pdfRef,
                DocId = docId,
                DocPath = docPath,
                DocName = docName,
                Category = cat,
                Pages = pages
            });
        }

        return items;
    }

    private static string NormalizeDocPath(string? p)
    {
        var s = (p ?? "").Trim().Replace('\\', '/');
        while (s.Contains("//", StringComparison.Ordinal)) s = s.Replace("//", "/", StringComparison.Ordinal);
        return s;
    }

    private static string? TryGetString(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
    }

    private static int? TryGetInt(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var n2)) return n2;
        return null;
    }

    // RAG (CDC v2.7)
    // ---------------------

    public async Task<RagSearchResponse> RagSearchAsync(
        string query,
        string? category,
        int topK,
        string? mode,
        CancellationToken ct,
        string? docId = null,
        string? docPath = null)
    {
        var body = JsonSerializer.Serialize(new
        {
            query,
            category,
            topK,
            mode,
            docId = string.IsNullOrWhiteSpace(docId) ? null : docId.Trim(),
            docPath = string.IsNullOrWhiteSpace(docPath) ? null : NormalizeDocPath(docPath)
        }, JsonOpts);

        using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Post, "/rag/search", body), ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<RagSearchResponse>(json, JsonOpts)
               ?? throw new Exception("Invalid rag search response");
    }

    // ---------------------
    // Admin / debug
    // ---------------------

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
