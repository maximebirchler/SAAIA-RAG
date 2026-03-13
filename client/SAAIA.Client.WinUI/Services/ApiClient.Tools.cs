using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

public sealed partial class ApiClient
{
    private static string NormalizeStoredSummaryLevel(string? level)
        => "medium";

    private async Task<JsonElement> SendJsonAsync(HttpMethod method, string path, string? jsonBody, bool admin, CancellationToken ct)
    {
        using var resp = await SendWithRateLimitRetryAsync(
            () => admin ? NewAdminRequest(method, path, jsonBody) : NewRequest(method, path, jsonBody),
            ct).ConfigureAwait(false);

        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public async Task<JsonElement> DocumentsCountAsync(string? categoryPath, string? q, CancellationToken ct)
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(categoryPath))
            qs.Add($"categoryPath={Uri.EscapeDataString(categoryPath.Trim().Replace('\\', '/').Trim('/'))}");
        if (!string.IsNullOrWhiteSpace(q))
            qs.Add($"q={Uri.EscapeDataString(q.Trim())}");

        var path = "/documents/count" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        return await SendJsonAsync(HttpMethod.Get, path, null, admin: false, ct).ConfigureAwait(false);
    }

    public async Task<JsonElement> DocumentsStatsAsync(string? path, CancellationToken ct)
    {
        var qs = string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : $"?path={Uri.EscapeDataString(path.Trim().Replace('\\', '/').Trim('/'))}";

        return await SendJsonAsync(HttpMethod.Get, "/documents/stats" + qs, null, admin: false, ct).ConfigureAwait(false);
    }

    public async Task<JsonElement> DocumentsEmptyFoldersCountAsync(string? path, CancellationToken ct)
    {
        var qs = string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : $"?path={Uri.EscapeDataString(path.Trim().Replace('\\', '/').Trim('/'))}";

        return await SendJsonAsync(HttpMethod.Get, "/documents/empty-folders/count" + qs, null, admin: false, ct).ConfigureAwait(false);
    }

    public async Task<JsonElement> DocumentsEmptyFoldersListAsync(string? path, int limit, int offset, CancellationToken ct)
    {
        var lim = Math.Clamp(limit, 1, 2000);
        var off = Math.Max(0, offset);
        var qs = new List<string>
        {
            $"limit={lim}",
            $"offset={off}"
        };

        if (!string.IsNullOrWhiteSpace(path))
            qs.Add($"path={Uri.EscapeDataString(path.Trim().Replace('\\', '/').Trim('/'))}");

        return await SendJsonAsync(HttpMethod.Get, "/documents/empty-folders?" + string.Join("&", qs), null, admin: false, ct).ConfigureAwait(false);
    }

    public async Task<JsonElement> SummaryGetAsync(string docId, string? level, CancellationToken ct)
    {
        var lvl = NormalizeStoredSummaryLevel(level);
        var path = $"/summaries/{Uri.EscapeDataString(docId)}?level={Uri.EscapeDataString(lvl)}";

        using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Get, path), ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            using var doc = JsonDocument.Parse("{\"found\":false,\"error\":\"not_found\"}");
            return doc.RootElement.Clone();
        }

        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var parsed = JsonDocument.Parse(json);
        return parsed.RootElement.Clone();
    }

    public Task<JsonElement> SummaryExistsAsync(string docId, string? level, CancellationToken ct)
    {
        var lvl = NormalizeStoredSummaryLevel(level);
        var path = $"/summaries/{Uri.EscapeDataString(docId)}/exists?level={Uri.EscapeDataString(lvl)}";
        return SendJsonAsync(HttpMethod.Get, path, null, admin: false, ct);
    }

    public Task<JsonElement> SummarySearchAsync(string q, int limit, int offset, CancellationToken ct)
    {
        var lim = Math.Clamp(limit, 1, 200);
        var off = Math.Max(0, offset);
        var path = $"/summaries/search?q={Uri.EscapeDataString(q)}&limit={lim}&offset={off}";
        return SendJsonAsync(HttpMethod.Get, path, null, admin: false, ct);
    }

    public Task<JsonElement> AdminSummaryMissingAsync(int limit, int offset, string? categoryPath, CancellationToken ct)
    {
        var lim = Math.Clamp(limit, 1, 500);
        var off = Math.Max(0, offset);
        var qs = new List<string> { $"limit={lim}", $"offset={off}" };
        if (!string.IsNullOrWhiteSpace(categoryPath))
            qs.Add($"categoryPath={Uri.EscapeDataString(categoryPath.Trim().Replace('\\', '/').Trim('/'))}");

        return SendJsonAsync(HttpMethod.Get, "/admin/summaries/missing?" + string.Join("&", qs), null, admin: true, ct);
    }

    public Task<JsonElement> AdminSummaryRequestAsync(string docId, string? level, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            docId,
            level = NormalizeStoredSummaryLevel(level)
        }, JsonOpts);

        return SendJsonAsync(HttpMethod.Post, "/admin/summaries/request", body, admin: true, ct);
    }

    public Task<JsonElement> AdminSummaryGenerateAsync(string docId, string? level, bool force, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            docId,
            level = NormalizeStoredSummaryLevel(level),
            force
        }, JsonOpts);

        return SendJsonAsync(HttpMethod.Post, "/admin/summaries/generate", body, admin: true, ct);
    }

    public Task<JsonElement> AdminSummarySubmitAsync(
        string docId,
        string? level,
        string docLanguage,
        string sourceHash,
        string summaryText,
        string? jobId,
        JsonElement? meta,
        CancellationToken ct)
    {
        object? metaObj = null;
        if (meta.HasValue)
        {
            metaObj = JsonSerializer.Deserialize<object>(meta.Value.GetRawText(), JsonOpts);
        }

        var body = JsonSerializer.Serialize(new
        {
            jobId,
            docId,
            level = NormalizeStoredSummaryLevel(level),
            docLanguage,
            sourceHash,
            summaryText,
            meta = metaObj
        }, JsonOpts);

        return SendJsonAsync(HttpMethod.Post, "/admin/summaries/submit", body, admin: true, ct);
    }

    public async Task<JsonElement> AdminSummaryStatusAsync(string jobId, CancellationToken ct)
    {
        var path = $"/admin/summaries/status?jobId={Uri.EscapeDataString(jobId)}";
        using var resp = await SendWithRateLimitRetryAsync(() => NewAdminRequest(HttpMethod.Get, path), ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            using var doc = JsonDocument.Parse("{\"found\":false,\"error\":\"not_found\"}");
            return doc.RootElement.Clone();
        }

        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var parsed = JsonDocument.Parse(json);
        return parsed.RootElement.Clone();
    }

    public Task<JsonElement> AdminSummaryDeleteAsync(string docId, string? level, CancellationToken ct)
    {
        var lvl = NormalizeStoredSummaryLevel(level);
        var path = $"/admin/summaries/{Uri.EscapeDataString(docId)}?level={Uri.EscapeDataString(lvl)}";
        return SendJsonAsync(HttpMethod.Delete, path, null, admin: true, ct);
    }

    public async Task<JsonElement> AdminCatalogHealthAsync(CancellationToken ct)
    {
        using var resp = await SendWithRateLimitRetryAsync(() => NewAdminRequest(HttpMethod.Get, "/admin/catalog/health"), ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            using var legacy = await SendWithRateLimitRetryAsync(() => NewAdminRequest(HttpMethod.Get, "/admin/catalog/snapshot/status"), ct).ConfigureAwait(false);
            legacy.EnsureSuccessStatusCode();
            var legacyJson = await legacy.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var legacyDoc = JsonDocument.Parse(legacyJson);
            return legacyDoc.RootElement.Clone();
        }

        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public async Task<JsonElement> AdminCatalogRescanNowAsync(CancellationToken ct)
    {
        using var resp = await SendWithRateLimitRetryAsync(() => NewAdminRequest(HttpMethod.Post, "/admin/catalog/rescan", "{}"), ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            using var legacy = await SendWithRateLimitRetryAsync(() => NewAdminRequest(HttpMethod.Post, "/admin/catalog/snapshot/refresh", "{}"), ct).ConfigureAwait(false);
            legacy.EnsureSuccessStatusCode();
            var legacyJson = await legacy.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var legacyDoc = JsonDocument.Parse(legacyJson);
            return legacyDoc.RootElement.Clone();
        }

        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public Task<JsonElement> AdminIngestionReindexAsync(string docPath, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            docPath,
            category = (string?)null,
            action = "upsert"
        }, JsonOpts);

        return SendJsonAsync(HttpMethod.Post, "/ingest/enqueue", body, admin: true, ct);
    }

    public Task<JsonElement> AdminJobsListAsync(string? type, int limit, int offset, CancellationToken ct)
    {
        var lim = Math.Clamp(limit, 1, 500);
        var off = Math.Max(0, offset);
        var qs = new List<string> { $"limit={lim}", $"offset={off}" };
        if (!string.IsNullOrWhiteSpace(type))
            qs.Add($"type={Uri.EscapeDataString(type.Trim())}");

        return SendJsonAsync(HttpMethod.Get, "/admin/jobs?" + string.Join("&", qs), null, admin: true, ct);
    }

    public Task<JsonElement> AdminJobsCancelAsync(string jobId, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { jobId }, JsonOpts);
        return SendJsonAsync(HttpMethod.Post, "/admin/jobs/cancel", body, admin: true, ct);
    }

    public Task<JsonElement> AdminQdrantHealthAsync(CancellationToken ct)
        => SendJsonAsync(HttpMethod.Get, "/admin/qdrant/health", null, admin: true, ct);
}
