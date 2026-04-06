
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
    private sealed record LegacyCursorState(int PageSize, int Offset, string? CategoryPath = null, string? CategoryRef = null, string? Query = null, string? OrderBy = null);

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


    private static string ClassifyAdminSessionValidationFailure(HttpStatusCode? statusCode, bool wasCanceled = false)
    {
        if (wasCanceled)
            return "unavailable";

        return statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "invalid",
            _ => "unavailable"
        };
    }

    public async Task<(bool isValid, string status)> TryActivateAdminSessionKeyAsync(string? adminKey, CancellationToken ct)
    {
        var candidate = (adminKey ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(candidate))
            return (false, "empty");

        var previous = _adminKey;
        _adminKey = candidate;

        try
        {
            _ = await AdminCatalogHealthAsync(ct).ConfigureAwait(false);
            return (true, "ok");
        }
        catch (HttpRequestException ex)
        {
            _adminKey = previous;
            return (false, ClassifyAdminSessionValidationFailure(ex.StatusCode));
        }
        catch (TaskCanceledException)
        {
            _adminKey = previous;
            return (false, ClassifyAdminSessionValidationFailure(null, wasCanceled: true));
        }
        catch
        {
            _adminKey = previous;
            return (false, "unavailable");
        }
    }

    public async Task<JsonElement> AuthCapabilitiesAsync(CancellationToken ct)
    {
        if (HasAdminKey)
        {
            using var adminResp = await SendWithRateLimitRetryAsync(() => NewAdminRequest(HttpMethod.Get, "/auth/capabilities"), ct).ConfigureAwait(false);
            if (adminResp.StatusCode != HttpStatusCode.NotFound && adminResp.IsSuccessStatusCode)
            {
                var adminJson = await adminResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var adminDoc = JsonDocument.Parse(adminJson);
                return adminDoc.RootElement.Clone();
            }
        }

        using var userResp = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Get, "/auth/capabilities"), ct).ConfigureAwait(false);
        if (userResp.StatusCode != HttpStatusCode.NotFound)
        {
            userResp.EnsureSuccessStatusCode();
            var userJson = await userResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var userDoc = JsonDocument.Parse(userJson);
            return userDoc.RootElement.Clone();
        }

        using var fallbackDoc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            user = new
            {
                isAuthenticated = !string.IsNullOrWhiteSpace(_apiKey) || !string.IsNullOrWhiteSpace(_adminKey),
                isAdmin = HasAdminKey,
                displayName = string.IsNullOrWhiteSpace(_userId) ? "local" : _userId
            },
            ui = new { defaultLocale = "fr-CH" },
            capabilities = new
            {
                directCommands = new object[]
                {
                    new { commandId = "catalog.categories.list" },
                    new { commandId = "catalog.documents.listAll" },
                    new { commandId = "catalog.documents.listByCategory" },
                    new { commandId = "catalog.stats.view" }
                },
                adminCommands = HasAdminKey
                    ? new object[]
                    {
                        new { commandId = "catalog.summaries.status" },
                        new { commandId = "admin.catalog.rescan" },
                        new { commandId = "admin.ingestion.reindexDocument" }
                    }
                    : Array.Empty<object>()
            }
        }));
        return fallbackDoc.RootElement.Clone();
    }

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

        return await SendJsonAsync(HttpMethod.Get, "/documents/tree?" + string.Join("&", qs), null, admin: false, ct).ConfigureAwait(false);
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
        => AdminIngestionReindexAsync(docPath, null, ct);

    public async Task<JsonElement> AdminIngestionReindexAsync(string? docPath, string? docId, CancellationToken ct)
    {
        Guid? parsedDocId = null;
        if (!string.IsNullOrWhiteSpace(docId) && Guid.TryParse(docId, out var guid))
            parsedDocId = guid;

        var adminBody = JsonSerializer.Serialize(new
        {
            docPath,
            docId = parsedDocId,
            level = (string?)null,
            jobId = (Guid?)null,
            docLanguage = (string?)null,
            sourceHash = (string?)null,
            summaryText = (string?)null,
            meta = (object?)null,
            force = (bool?)null
        }, JsonOpts);

        using var adminResp = await SendWithRateLimitRetryAsync(
            () => NewAdminRequest(HttpMethod.Post, "/admin/ingestion/reindex", adminBody),
            ct).ConfigureAwait(false);

        if (adminResp.StatusCode != HttpStatusCode.NotFound)
        {
            if (adminResp.StatusCode == HttpStatusCode.Conflict)
            {
                var conflictJson = await adminResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var conflictDoc = JsonDocument.Parse(conflictJson);
                return conflictDoc.RootElement.Clone();
            }

            adminResp.EnsureSuccessStatusCode();
            var adminJson = await adminResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var adminDoc = JsonDocument.Parse(adminJson);
            return adminDoc.RootElement.Clone();
        }

        var legacyBody = JsonSerializer.Serialize(new
        {
            docPath = docPath ?? string.Empty,
            category = (string?)null,
            action = "upsert"
        }, JsonOpts);

        using var legacyResp = await SendWithRateLimitRetryAsync(
            () => NewAdminRequest(HttpMethod.Post, "/ingest/enqueue", legacyBody),
            ct).ConfigureAwait(false);
        legacyResp.EnsureSuccessStatusCode();
        var legacyJson = await legacyResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var legacyDoc = JsonDocument.Parse(legacyJson);
        return legacyDoc.RootElement.Clone();
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

    public Task<JsonElement> AdminJobGetAsync(string jobId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            throw new ArgumentException("jobId is required.", nameof(jobId));

        return SendJsonAsync(HttpMethod.Get, "/admin/jobs/" + Uri.EscapeDataString(jobId.Trim()), null, admin: true, ct);
    }

    public Task<JsonElement> AdminJobsPurgeAsync(string? scope, string? type, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            scope = string.IsNullOrWhiteSpace(scope) ? null : scope.Trim(),
            type = string.IsNullOrWhiteSpace(type) ? null : type.Trim()
        }, JsonOpts);
        return SendJsonAsync(HttpMethod.Post, "/admin/jobs/purge", body, admin: true, ct);
    }

    public Task<JsonElement> AdminJobsDeleteHistoryAsync(IReadOnlyCollection<string> jobIds, CancellationToken ct)
    {
        var ids = (jobIds ?? Array.Empty<string>())
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var body = JsonSerializer.Serialize(new { jobIds = ids }, JsonOpts);
        return SendJsonAsync(HttpMethod.Post, "/admin/jobs/delete_history", body, admin: true, ct);
    }

    public Task<JsonElement> AdminQdrantHealthAsync(CancellationToken ct)
        => SendJsonAsync(HttpMethod.Get, "/admin/qdrant/health", null, admin: true, ct);

    private JsonElement NormalizeCatalogCategories(JsonElement root, int limit, int offset)
    {
        var items = root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(entry => (object)new
            {
                ordinal = TryGetInt(entry, "displayOrder") ?? 0,
                displayOrder = TryGetInt(entry, "displayOrder") ?? 0,
                path = TryGetString(entry, "categoryPath") ?? string.Empty,
                name = TryGetString(entry, "canonicalName") ?? TryGetString(entry, "categoryPath") ?? string.Empty,
                depth = 1,
                totalDocuments = TryGetInt(entry, "documentCount") ?? 0,
                directDocuments = TryGetInt(entry, "directDocumentCount") ?? 0,
                subfolderCount = TryGetInt(entry, "subfolderCount") ?? 0,
                categoryRef = TryGetString(entry, "categoryRef") ?? string.Empty,
                aliases = ReadStringArray(entry, "aliases")
            }).ToList()
            : new List<object>();

        var total = root.TryGetProperty("totals", out var totals)
            ? (TryGetInt(totals, "categories") ?? items.Count)
            : items.Count;
        var endOfList = !root.TryGetProperty("nextLink", out var nextLink) || nextLink.ValueKind == JsonValueKind.Null || string.IsNullOrWhiteSpace(nextLink.GetString());
        using var normalized = JsonDocument.Parse(JsonSerializer.Serialize(new { items, limit, offset, total, endOfList }));
        return normalized.RootElement.Clone();
    }

    private JsonElement NormalizeCatalogSummaries(JsonElement root, int limit, int offset)
    {
        var items = root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(entry => (object)new
            {
                docId = TryGetString(entry, "docId") ?? string.Empty,
                docPath = TryGetString(entry, "docPath") ?? string.Empty,
                docName = TryGetString(entry, "canonicalName") ?? TryGetString(entry, "docName") ?? string.Empty,
                category = TryGetString(entry, "categoryCanonicalName") ?? TryGetString(entry, "category") ?? string.Empty,
                summaryState = TryGetString(entry, "summaryState") ?? "missing"
            }).ToList()
            : new List<object>();

        var total = root.TryGetProperty("totals", out var totals)
            ? (TryGetInt(totals, "total") ?? items.Count)
            : items.Count;
        var endOfList = !root.TryGetProperty("nextLink", out var nextLink) || nextLink.ValueKind == JsonValueKind.Null || string.IsNullOrWhiteSpace(nextLink.GetString());
        using var normalized = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            items,
            limit,
            offset,
            total,
            endOfList,
            missingStored = root.TryGetProperty("totals", out totals) ? TryGetInt(totals, "missingStored") ?? items.Count : items.Count,
            staleStored = root.TryGetProperty("totals", out totals) ? TryGetInt(totals, "staleStored") ?? 0 : 0,
            level = TryGetString(root, "level") ?? "medium"
        }));
        return normalized.RootElement.Clone();
    }

    private static List<string> ReadStringArray(JsonElement obj, string propertyName)
    {
        var list = new List<string>();
        if (!obj.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                list.Add(item.GetString()!.Trim());
        }

        return list;
    }

    private static string EncodeLegacyCursor(LegacyCursorState state)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state))).TrimEnd('=');
}
