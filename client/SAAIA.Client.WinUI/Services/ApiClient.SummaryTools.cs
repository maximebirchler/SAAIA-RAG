using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

public sealed partial class ApiClient
{
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

    public Task<JsonElement> AdminSummaryMissingCountAsync(string? categoryPath, string? categoryRef, CancellationToken ct)
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(categoryPath))
            qs.Add($"categoryPath={Uri.EscapeDataString(categoryPath.Trim().Replace('\\', '/').Trim('/'))}");
        if (!string.IsNullOrWhiteSpace(categoryRef))
            qs.Add($"categoryRef={Uri.EscapeDataString(categoryRef.Trim())}");

        var path = "/admin/summaries/missing_count" + (qs.Count == 0 ? string.Empty : "?" + string.Join("&", qs));
        return SendJsonAsync(HttpMethod.Get, path, null, admin: true, ct);
    }

    public async Task<JsonElement> AdminSummaryMissingAsync(int limit, int offset, string? categoryPath, string? categoryRef, CancellationToken ct)
    {
        var lim = Math.Clamp(limit, 1, 500);
        var off = Math.Max(0, offset);
        var qs = new List<string> { $"pageSize={lim}" };
        if (!string.IsNullOrWhiteSpace(categoryPath))
            qs.Add($"categoryPath={Uri.EscapeDataString(categoryPath.Trim().Replace('\\', '/').Trim('/'))}");
        if (!string.IsNullOrWhiteSpace(categoryRef))
            qs.Add($"categoryRef={Uri.EscapeDataString(categoryRef.Trim())}");
        if (off > 0)
            qs.Add($"cursor={Uri.EscapeDataString(EncodeLegacyCursor(new LegacyCursorState(lim, off, categoryPath, categoryRef)))}");

        using var resp = await SendWithRateLimitRetryAsync(() => NewAdminRequest(HttpMethod.Get, "/catalog/summaries?" + string.Join("&", qs)), ct).ConfigureAwait(false);
        if (resp.StatusCode != HttpStatusCode.NotFound)
        {
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return NormalizeCatalogSummaries(doc.RootElement, lim, off);
        }

        var legacyQs = new List<string> { $"limit={lim}", $"offset={off}" };
        if (!string.IsNullOrWhiteSpace(categoryPath))
            legacyQs.Add($"categoryPath={Uri.EscapeDataString(categoryPath.Trim().Replace('\\', '/').Trim('/'))}");
        if (!string.IsNullOrWhiteSpace(categoryRef))
            legacyQs.Add($"categoryRef={Uri.EscapeDataString(categoryRef.Trim())}");

        return await SendJsonAsync(HttpMethod.Get, "/admin/summaries/missing?" + string.Join("&", legacyQs), null, admin: true, ct).ConfigureAwait(false);
    }

    public Task<JsonElement> AdminSummaryPresentCountAsync(string? categoryPath, string? categoryRef, CancellationToken ct)
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(categoryPath))
            qs.Add($"categoryPath={Uri.EscapeDataString(categoryPath.Trim().Replace('\\', '/').Trim('/'))}");
        if (!string.IsNullOrWhiteSpace(categoryRef))
            qs.Add($"categoryRef={Uri.EscapeDataString(categoryRef.Trim())}");

        var path = "/admin/summaries/present_count" + (qs.Count == 0 ? string.Empty : "?" + string.Join("&", qs));
        return SendJsonAsync(HttpMethod.Get, path, null, admin: true, ct);
    }

    public Task<JsonElement> AdminSummaryPresentAsync(int limit, int offset, string? categoryPath, string? categoryRef, CancellationToken ct)
    {
        var lim = Math.Clamp(limit, 1, 500);
        var off = Math.Max(0, offset);
        var qs = new List<string> { $"limit={lim}", $"offset={off}" };
        if (!string.IsNullOrWhiteSpace(categoryPath))
            qs.Add($"categoryPath={Uri.EscapeDataString(categoryPath.Trim().Replace('\\', '/').Trim('/'))}");
        if (!string.IsNullOrWhiteSpace(categoryRef))
            qs.Add($"categoryRef={Uri.EscapeDataString(categoryRef.Trim())}");

        return SendJsonAsync(HttpMethod.Get, "/admin/summaries/present?" + string.Join("&", qs), null, admin: true, ct);
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
            metaObj = JsonSerializer.Deserialize<object>(meta.Value.GetRawText(), JsonOpts);

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
}
