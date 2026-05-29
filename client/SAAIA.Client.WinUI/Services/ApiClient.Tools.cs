
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

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                BuildHttpErrorMessage(method, path, resp.StatusCode, body),
                inner: null,
                statusCode: resp.StatusCode);
        }

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            json = "{}";
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string BuildHttpErrorMessage(HttpMethod method, string path, HttpStatusCode statusCode, string? body)
    {
        var detail = ExtractHttpErrorDetail(body);
        var prefix = $"{method.Method} {path} returned HTTP {(int)statusCode} ({statusCode}).";
        return string.IsNullOrWhiteSpace(detail)
            ? prefix
            : $"{prefix} {detail}";
    }

    private static string? ExtractHttpErrorDetail(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        var trimmed = body.Trim();
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                var parts = new List<string>();
                AddErrorPart(parts, root, "error");
                AddErrorPart(parts, root, "message");
                AddErrorPart(parts, root, "detail");
                AddErrorPart(parts, root, "capabilityKey");
                if (parts.Count > 0)
                    return string.Join(" | ", parts.Distinct(StringComparer.OrdinalIgnoreCase));
            }
        }
        catch (JsonException)
        {
        }

        return trimmed.Length <= 500 ? trimmed : trimmed[..497] + "...";
    }

    private static void AddErrorPart(ICollection<string> parts, JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            return;

        var text = value.GetString();
        if (!string.IsNullOrWhiteSpace(text))
            parts.Add($"{propertyName}={text.Trim()}");
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

        var previous = SetAdminSessionKeyTemporary(candidate);

        try
        {
            _ = await AdminCatalogHealthAsync(ct).ConfigureAwait(false);
            return (true, "ok");
        }
        catch (HttpRequestException ex)
        {
            SetAdminSessionKey(previous);
            return (false, ClassifyAdminSessionValidationFailure(ex.StatusCode));
        }
        catch (TaskCanceledException)
        {
            SetAdminSessionKey(previous);
            return (false, ClassifyAdminSessionValidationFailure(null, wasCanceled: true));
        }
        catch
        {
            SetAdminSessionKey(previous);
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

        var snapshot = GetConfigSnapshot();
        using var fallbackDoc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            user = new
            {
                isAuthenticated = !string.IsNullOrWhiteSpace(snapshot.ApiKey) || !string.IsNullOrWhiteSpace(snapshot.AdminKey),
                isAdmin = HasAdminKey,
                displayName = string.IsNullOrWhiteSpace(snapshot.UserId) ? "local" : snapshot.UserId
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
                categoryRef = TryGetString(entry, "categoryRef"),
                categoryPath = TryGetString(entry, "categoryPath"),
                summaryState = TryGetString(entry, "summaryState") ?? "missing",
                capabilityBProfileState = TryGetString(entry, "capabilityBProfileState"),
                capabilityBHasBackofficeProfile = TryGetBool(entry, "capabilityBHasBackofficeProfile") ?? false,
                capabilityBReasons = ReadStringArray(entry, "capabilityBReasons"),
                hasActiveSummaryJob = TryGetBool(entry, "hasActiveSummaryJob") ?? false,
                activeSummaryJobId = TryGetString(entry, "activeSummaryJobId"),
                activeSummaryJobType = TryGetString(entry, "activeSummaryJobType"),
                activeSummaryJobStatus = TryGetString(entry, "activeSummaryJobStatus"),
                activeSummaryJobExecutionMode = TryGetString(entry, "activeSummaryJobExecutionMode"),
                activeSummaryJobRuntimeCapabilityKey = TryGetString(entry, "activeSummaryJobRuntimeCapabilityKey"),
                activeSummaryJobRuntimeCapabilityStatus = TryGetString(entry, "activeSummaryJobRuntimeCapabilityStatus"),
                activeSummaryJobEnqueueSource = TryGetString(entry, "activeSummaryJobEnqueueSource"),
                activeSummaryJobCampaignId = TryGetString(entry, "activeSummaryJobCampaignId"),
                capabilityBReadyToEnqueue = TryGetBool(entry, "capabilityBReadyToEnqueue") ?? false,
                capabilityBRecommendedAction = TryGetString(entry, "capabilityBRecommendedAction"),
                capabilityBPolicyBlocked = TryGetBool(entry, "capabilityBPolicyBlocked") ?? false,
                capabilityBPolicyBlockReason = TryGetString(entry, "capabilityBPolicyBlockReason"),
                capabilityBPriorityScore = TryGetDouble(entry, "capabilityBPriorityScore"),
                capabilityBLastJobStatus = TryGetString(entry, "capabilityBLastJobStatus"),
                capabilityBLastJobFinishedAt = TryGetString(entry, "capabilityBLastJobFinishedAt"),
                capabilityBLastJobError = TryGetString(entry, "capabilityBLastJobError")
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
            profileMissing = root.TryGetProperty("totals", out totals) ? TryGetInt(totals, "profileMissing") ?? 0 : 0,
            scopePath = TryGetString(root, "scopePath") ?? string.Empty,
            level = TryGetString(root, "level") ?? "medium",
            nextLink = TryGetString(root, "nextLink")
        }));
        return normalized.RootElement.Clone();
    }

    private static bool? TryGetBool(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static double? TryGetDouble(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        return null;
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
