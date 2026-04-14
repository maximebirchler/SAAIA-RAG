
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
