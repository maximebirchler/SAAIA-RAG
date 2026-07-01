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
    public async Task<JsonElement> CatalogSnapshotAsync(CancellationToken ct)
    {
        using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Get, "/catalog/snapshot"), ct).ConfigureAwait(false);
        if (resp.StatusCode != HttpStatusCode.NotFound)
        {
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }

        var categories = await DocumentsCategoriesAsync(null, null, 500, 0, ct).ConfigureAwait(false);
        var count = await DocumentsCountAsync(null, null, null, ct).ConfigureAwait(false);
        var items = categories.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array
            ? itemsEl.EnumerateArray().Select(x => (object)new
            {
                categoryRef = TryGetString(x, "categoryRef") ?? TryGetString(x, "path") ?? TryGetString(x, "name") ?? string.Empty,
                categoryPath = TryGetString(x, "path") ?? string.Empty,
                displayOrder = TryGetInt(x, "displayOrder") ?? TryGetInt(x, "ordinal") ?? 0,
                canonicalName = TryGetString(x, "name") ?? TryGetString(x, "path") ?? string.Empty,
                documentCount = TryGetInt(x, "totalDocuments") ?? 0,
                aliases = ReadStringArray(x, "aliases")
            }).ToList()
            : new List<object>();

        using var fallbackDoc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            snapshotId = $"legacy_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
            catalogVersion = DateTimeOffset.UtcNow,
            categories = items,
            totals = new
            {
                documents = count.TryGetProperty("total", out var totalEl) && totalEl.ValueKind == JsonValueKind.Number ? totalEl.GetInt32() : 0,
                categories = items.Count
            }
        }));
        return fallbackDoc.RootElement.Clone();
    }

    public async Task<JsonElement> DocumentsCountAsync(string? categoryPath, string? categoryRef, string? q, CancellationToken ct)
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(categoryPath))
            qs.Add($"categoryPath={Uri.EscapeDataString(categoryPath.Trim().Replace('\\', '/').Trim('/'))}");
        if (!string.IsNullOrWhiteSpace(categoryRef))
            qs.Add($"categoryRef={Uri.EscapeDataString(categoryRef.Trim())}");
        if (!string.IsNullOrWhiteSpace(q))
            qs.Add($"q={Uri.EscapeDataString(q.Trim())}");

        var path = "/documents/count" + (qs.Count > 0 ? "?" + string.Join("&", qs) : string.Empty);
        return await SendJsonAsync(HttpMethod.Get, path, null, admin: false, ct).ConfigureAwait(false);
    }

    public async Task<JsonElement> DocumentsCategoriesAsync(string? path, string? categoryRef, int limit, int offset, CancellationToken ct)
    {
        var lim = Math.Clamp(limit, 1, 500);
        var off = Math.Max(0, offset);
        var qs = new List<string> { $"pageSize={lim}" };

        if (!string.IsNullOrWhiteSpace(path))
            qs.Add($"path={Uri.EscapeDataString(path.Trim().Replace('\\', '/').Trim('/'))}");
        if (!string.IsNullOrWhiteSpace(categoryRef))
            qs.Add($"categoryRef={Uri.EscapeDataString(categoryRef.Trim())}");
        if (off > 0)
            qs.Add($"cursor={Uri.EscapeDataString(EncodeLegacyCursor(new LegacyCursorState(lim, off, path, categoryRef)))}");

        using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Get, "/catalog/categories?" + string.Join("&", qs)), ct).ConfigureAwait(false);
        if (resp.StatusCode != HttpStatusCode.NotFound)
        {
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return NormalizeCatalogCategories(doc.RootElement, lim, off);
        }

        var legacyQs = new List<string> { $"limit={lim}", $"offset={off}" };
        if (!string.IsNullOrWhiteSpace(path))
            legacyQs.Add($"path={Uri.EscapeDataString(path.Trim().Replace('\\', '/').Trim('/'))}");
        if (!string.IsNullOrWhiteSpace(categoryRef))
            legacyQs.Add($"categoryRef={Uri.EscapeDataString(categoryRef.Trim())}");

        return await SendJsonAsync(HttpMethod.Get, "/documents/categories?" + string.Join("&", legacyQs), null, admin: false, ct).ConfigureAwait(false);
    }

    public async Task<JsonElement> DocumentsStatsAsync(string? path, string? categoryRef, CancellationToken ct)
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(path))
            qs.Add($"path={Uri.EscapeDataString(path.Trim().Replace('\\', '/').Trim('/'))}");
        if (!string.IsNullOrWhiteSpace(categoryRef))
            qs.Add($"categoryRef={Uri.EscapeDataString(categoryRef.Trim())}");

        var suffix = qs.Count == 0 ? string.Empty : "?" + string.Join("&", qs);
        using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Get, "/catalog/stats" + suffix), ct).ConfigureAwait(false);
        if (resp.StatusCode != HttpStatusCode.NotFound)
        {
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }

        return await SendJsonAsync(HttpMethod.Get, "/documents/stats" + suffix, null, admin: false, ct).ConfigureAwait(false);
    }

    public Task<JsonElement> DocumentsTreeAsync(string? path, int depth, string? format, CancellationToken ct)
        => DocumentsTreeAsync(path, null, depth, format, ct);

    public async Task<JsonElement> DocumentsTreeAsync(string? path, string? categoryRef, int depth, string? format, CancellationToken ct)
    {
        var qs = new List<string>
        {
            $"depth={Math.Clamp(depth, 1, 20)}",
            $"format={Uri.EscapeDataString(string.IsNullOrWhiteSpace(format) ? "markdown" : format.Trim().ToLowerInvariant())}"
        };

        if (!string.IsNullOrWhiteSpace(path))
            qs.Add($"path={Uri.EscapeDataString(path.Trim().Replace('\\', '/').Trim('/'))}");
        if (!string.IsNullOrWhiteSpace(categoryRef))
            qs.Add($"categoryRef={Uri.EscapeDataString(categoryRef.Trim())}");

        var url = "/documents/tree?" + string.Join("&", qs);
        string? knownEtag = null;
        JsonElement cachedPayload = default;
        var hasCachedPayload = false;

        lock (_documentsTreeCacheGate)
        {
            if (_documentsTreeCache.TryGetValue(url, out var cached))
            {
                knownEtag = cached.ETag;
                cachedPayload = cached.Payload.Clone();
                hasCachedPayload = true;
            }
        }

        using var resp = await SendWithRateLimitRetryAsync(() =>
        {
            var req = NewRequest(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(knownEtag))
                req.Headers.IfNoneMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue(knownEtag));
            return req;
        }, ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.NotModified && hasCachedPayload)
            return cachedPayload;

        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var payload = doc.RootElement.Clone();
        var etag = resp.Headers.ETag?.Tag;

        if (!string.IsNullOrWhiteSpace(etag))
        {
            lock (_documentsTreeCacheGate)
            {
                _documentsTreeCache[url] = new DocumentsTreeCacheEntry
                {
                    ETag = etag,
                    Payload = payload.Clone()
                };
            }
        }

        return payload;
    }

    public async Task<JsonElement> DocumentsNavigationAsync(
        string? path,
        string? categoryRef,
        string? docId,
        string? docPath,
        string? q,
        int limit,
        int offset,
        CancellationToken ct)
    {
        var lim = Math.Clamp(limit, 1, 200);
        var off = Math.Max(offset, 0);
        var qs = new List<string>
        {
            $"limit={lim}",
            $"offset={off}"
        };

        if (!string.IsNullOrWhiteSpace(path))
            qs.Add($"path={Uri.EscapeDataString(path.Trim().Replace('\\', '/').Trim('/'))}");
        if (!string.IsNullOrWhiteSpace(categoryRef))
            qs.Add($"categoryRef={Uri.EscapeDataString(categoryRef.Trim())}");
        if (!string.IsNullOrWhiteSpace(docId))
            qs.Add($"docId={Uri.EscapeDataString(docId.Trim())}");
        if (!string.IsNullOrWhiteSpace(docPath))
            qs.Add($"docPath={Uri.EscapeDataString(docPath.Trim().Replace('\\', '/').Trim('/'))}");
        if (!string.IsNullOrWhiteSpace(q))
            qs.Add($"q={Uri.EscapeDataString(q.Trim())}");

        return await SendJsonAsync(
                HttpMethod.Get,
                "/documents/navigation?" + string.Join("&", qs),
                null,
                admin: false,
                ct)
            .ConfigureAwait(false);
    }

    public async Task<JsonElement> DocumentsContextAsync(
        string? docId,
        string? docPath,
        string? chunkId,
        int? pageStart,
        int? pageEnd,
        int before,
        int after,
        int limit,
        int offset,
        CancellationToken ct)
    {
        var qs = new List<string>
        {
            $"before={Math.Clamp(before, 0, 20)}",
            $"after={Math.Clamp(after, 0, 30)}",
            $"limit={Math.Clamp(limit, 1, 50)}",
            $"offset={Math.Max(offset, 0)}"
        };

        if (!string.IsNullOrWhiteSpace(docId))
            qs.Add($"docId={Uri.EscapeDataString(docId.Trim())}");
        if (!string.IsNullOrWhiteSpace(docPath))
            qs.Add($"docPath={Uri.EscapeDataString(docPath.Trim().Replace('\\', '/').Trim('/'))}");
        if (!string.IsNullOrWhiteSpace(chunkId))
            qs.Add($"chunkId={Uri.EscapeDataString(chunkId.Trim())}");
        if (pageStart is > 0)
            qs.Add($"pageStart={pageStart.Value}");
        if (pageEnd is > 0)
            qs.Add($"pageEnd={pageEnd.Value}");

        return await SendJsonAsync(
                HttpMethod.Get,
                "/documents/context?" + string.Join("&", qs),
                null,
                admin: false,
                ct)
            .ConfigureAwait(false);
    }

    public async Task<JsonElement> DocumentsEmptyFoldersCountAsync(string? path, CancellationToken ct)
    {
        var qs = string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : $"?path={Uri.EscapeDataString(path.Trim().Replace('\\', '/').Trim('/'))}";

        return await SendJsonAsync(HttpMethod.Get, "/admin/catalog/empty-folders/count" + qs, null, admin: true, ct).ConfigureAwait(false);
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

        return await SendJsonAsync(HttpMethod.Get, "/admin/catalog/empty-folders?" + string.Join("&", qs), null, admin: true, ct).ConfigureAwait(false);
    }

    public async Task<JsonElement> DocumentsExtractionQualityAsync(string? path, string? categoryRef, int limit, CancellationToken ct)
    {
        var lim = Math.Clamp(limit, 1, 2000);
        var qs = new List<string> { $"limit={lim}" };

        if (!string.IsNullOrWhiteSpace(path))
            qs.Add($"path={Uri.EscapeDataString(path.Trim().Replace('\\', '/').Trim('/'))}");
        if (!string.IsNullOrWhiteSpace(categoryRef))
            qs.Add($"categoryRef={Uri.EscapeDataString(categoryRef.Trim())}");

        return await SendJsonAsync(HttpMethod.Get, "/admin/documents/extraction-quality?" + string.Join("&", qs), null, admin: true, ct).ConfigureAwait(false);
    }

    public async Task<JsonElement> DocumentExtractionPagesAsync(string docId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(docId))
            throw new ArgumentException("Document id is required.", nameof(docId));

        return await SendJsonAsync(
            HttpMethod.Get,
            $"/admin/documents/{Uri.EscapeDataString(docId.Trim())}/extraction-pages",
            null,
            admin: true,
            ct).ConfigureAwait(false);
    }

}
