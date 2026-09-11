using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SAAIA.Client.WinUI.Models;
using SAAIA.Client.WinUI.Services.ToolAgent;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services;

public sealed partial class ApiClient
{
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

    // Preferred contract: unified GET /documents for user-safe catalog access.
    // Older backends may still expose the user-safe alias only on /documents/catalog.
    var pathPrimary = "/documents?" + string.Join("&", qs);
    using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Get, pathPrimary), ct).ConfigureAwait(false);

    if (resp.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
    {
        if (!resp.IsSuccessStatusCode)
        {
            var bodyErr = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Documents unified list failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {bodyErr}",
                null,
                resp.StatusCode);
        }

        var jsonOk = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<DocumentsCatalogResponse>(jsonOk, JsonOpts)
               ?? throw new Exception(T("api.error.invalid_documents_catalog_response"));
    }

    // Fallback alias for older backends.
    var pathFallback = "/documents/catalog?" + string.Join("&", qs);
    using var resp2 = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Get, pathFallback), ct).ConfigureAwait(false);

    if (!resp2.IsSuccessStatusCode)
    {
        var bodyErr = await resp2.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new HttpRequestException($"Documents catalog fallback failed: {(int)resp2.StatusCode} {resp2.ReasonPhrase}. Body: {bodyErr}", null, resp2.StatusCode);
    }

    var json = await resp2.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    return JsonSerializer.Deserialize<DocumentsCatalogResponse>(json, JsonOpts)
           ?? throw new Exception(T("api.error.invalid_documents_catalog_response"));
}



    public async Task<bool> DocumentsCatalogIsIndexedAsync(string? docId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(docId))
            return false;
        if (!Guid.TryParse(docId, out var parsed))
            return false;

        using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Get, $"/documents/{parsed}"), ct).ConfigureAwait(false);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            using var fallback = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Get, $"/documents/catalog/{parsed}"), ct).ConfigureAwait(false);
            if (fallback.StatusCode == HttpStatusCode.NotFound)
                return false;

            fallback.EnsureSuccessStatusCode();
            return true;
        }

        if (resp.StatusCode == HttpStatusCode.NotFound)
            return false;

        resp.EnsureSuccessStatusCode();
        return true;
    }




    // ---------------------

    // ---------------------
    // Categories (tool-agent)
    // ---------------------

    /// <summary>
    /// Tool-agent friendly: returns raw JSON as <see cref="JsonElement"/> (cloned) so it can be stored safely.
    /// Expected shape: ["category-a","category-b",...]
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
        => DocumentsListAsync(categoryPath, null, q, changedSince: null, limit, offset, ct);

    public Task<JsonElement> DocumentsListAsync(string? categoryPath, string? categoryRef, string? q, int limit, int offset, CancellationToken ct)
        => DocumentsListAsync(categoryPath, categoryRef, q, changedSince: null, limit, offset, ct);

public async Task<JsonElement> DocumentsListAsync(string? categoryPath, string? categoryRef, string? q, DateTimeOffset? changedSince, int limit, int offset, CancellationToken ct)
    {
        var lim = Math.Clamp(limit, 1, 2000);
        var off = Math.Max(0, offset);
        var changedSinceValue = changedSince?.ToUniversalTime().ToString("O");

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
        if (!string.IsNullOrWhiteSpace(changedSinceValue))
            catalogQs.Add($"changedSince={Uri.EscapeDataString(changedSinceValue)}");
        if (off > 0)
        {
            var cursorPayload = JsonSerializer.Serialize(new { PageSize = Math.Min(lim, 200), Offset = off, CategoryPath = categoryPath, CategoryRef = categoryRef, Query = q, ChangedSince = changedSinceValue, OrderBy = "name_asc" });
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
        if (!string.IsNullOrWhiteSpace(changedSinceValue))
            qs.Add($"changedSince={Uri.EscapeDataString(changedSinceValue)}");

        var pathUnified = "/documents?" + string.Join("&", qs);
        using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Get, pathUnified), ct).ConfigureAwait(false);

        string json;
        if (resp.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
        {
            resp.EnsureSuccessStatusCode();
            json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        else
        {
            var pathFallback = "/documents/catalog?" + string.Join("&", qs);
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
                var status = TryGetString(it, "status") ?? TryGetString(it, "Status") ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(status)
                    && !string.Equals(status, "indexed", StringComparison.OrdinalIgnoreCase))
                {
                    i++;
                    continue;
                }

                var docId = TryGetString(it, "docId") ?? string.Empty;
                var docPath = TryGetString(it, "docPath") ?? string.Empty;
                var docName = TryGetString(it, "canonicalName") ?? TryGetString(it, "docName") ?? string.Empty;
                var categoryRef = TryGetString(it, "categoryRef") ?? TryGetString(it, "CategoryRef");
                var cat = TryGetString(it, "categoryCanonicalName") ?? string.Empty;
                var itemCategoryPath = TryGetString(it, "categoryPath") ?? string.Empty;
                var pages = TryGetInt(it, "pages") ?? TryGetInt(it, "pageCount");
                var sourceHash = TryGetString(it, "sourceHash") ?? TryGetString(it, "SourceHash");
                var docLanguage = TryGetString(it, "docLanguage") ?? TryGetString(it, "DocLanguage");
                var profileLanguage = TryGetString(it, "profileLanguage") ?? TryGetString(it, "ProfileLanguage");
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
                    categoryRef,
                    categoryPath = normalizedCategoryPath,
                    pages,
                    sourceHash,
                    docLanguage,
                    profileLanguage
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
                if (!string.IsNullOrWhiteSpace(status)
                    && !string.Equals(status, "indexed", StringComparison.OrdinalIgnoreCase))
                {
                    i++;
                    continue;
                }

                var docId = TryGetString(it, "docId") ?? TryGetString(it, "DocId") ?? string.Empty;
                var docPath = TryGetString(it, "docPath") ?? TryGetString(it, "DocPath") ?? string.Empty;
                var docName = TryGetString(it, "docName") ?? TryGetString(it, "DocName") ?? string.Empty;
                var cat = TryGetString(it, "category") ?? TryGetString(it, "Category") ?? string.Empty;
                var categoryRef = TryGetString(it, "categoryRef") ?? TryGetString(it, "CategoryRef");
                var itemCategoryPath = TryGetString(it, "categoryPath") ?? TryGetString(it, "CategoryPath") ?? string.Empty;
                var pages = TryGetInt(it, "pages") ?? TryGetInt(it, "pageCount");
                var sourceHash = TryGetString(it, "sourceHash") ?? TryGetString(it, "SourceHash");
                var docLanguage = TryGetString(it, "docLanguage") ?? TryGetString(it, "DocLanguage");
                var profileLanguage = TryGetString(it, "profileLanguage") ?? TryGetString(it, "ProfileLanguage");
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
                    categoryRef,
                    categoryPath = normalizedCategoryPath,
                    pages,
                    sourceHash,
                    docLanguage,
                    profileLanguage
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
            throw new ArgumentException(T("api.error.doc_id_required"), nameof(docId));

        // Prefer unified user-safe detail endpoint.
        var pathUnified = $"/documents/{Uri.EscapeDataString(docId)}";
        using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Get, pathUnified), ct).ConfigureAwait(false);

        string json;
        if (resp.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
        {
            resp.EnsureSuccessStatusCode();
            json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        else
        {
            var pathAlias = $"/documents/catalog/{Uri.EscapeDataString(docId)}";
            using var resp2 = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Get, pathAlias), ct).ConfigureAwait(false);
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
    // ---------------------
    // RAG search (tool-agent)
    // ---------------------

    /// <summary>
    /// Tool-agent friendly RAG search: raw JSON as JsonElement (cloned).
    /// </summary>
    public async Task<JsonElement> RagSearchAsync(string query, int topK, string? category, CancellationToken ct)
    {
        return await RagSearchToolAsync(query, topK, category, mode: "auto", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Tool-agent friendly RAG search with explicit mode.
    /// </summary>
    public async Task<JsonElement> RagSearchToolAsync(
        string query,
        int topK,
        string? category,
        string? mode,
        CancellationToken ct,
        string? docId = null,
        string? docPath = null,
        int? maxPerDoc = null,
        int? maxPerPage = null,
        int? pageStart = null,
        int? pageEnd = null,
        string? researchMode = null,
        bool? includeResearchSurfaces = null,
        bool? sourceBackedCanonical = null)
    {
        var m = NormalizeRagSearchApiMode(mode);
        var categoryScope = BuildRagCategoryScope(category);
        var normalizedDocId = string.IsNullOrWhiteSpace(docId) ? null : docId.Trim();
        var normalizedDocPath = string.IsNullOrWhiteSpace(docPath) ? null : docPath.Trim();

        var body = JsonSerializer.Serialize(new
        {
            query,
            category = categoryScope.LegacyCategory,
            categoryPath = categoryScope.CategoryPath,
            categoryRef = categoryScope.CategoryRef,
            docId = normalizedDocId,
            docPath = normalizedDocPath,
            topK,
            maxPerDoc,
            maxPerPage,
            pageStart,
            pageEnd,
            mode = m,
            researchMode = NormalizeRagSearchResearchMode(researchMode),
            includeResearchSurfaces,
            sourceBackedCanonical,
            includeContextualSnippet = true
        }, JsonOpts);

        using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Post, "/rag/search", body), ct);
        await EnsureSuccessOrThrowBackendBusyAsync(resp, ct).ConfigureAwait(false);

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
            var categoryRef = TryGetString(it, "categoryRef") ?? "";
            var categoryPath = TryGetString(it, "categoryPath") ?? GuessCategoryPath(docPath);
            var pages = TryGetInt(it, "pages");
            var sourceHash = TryGetString(it, "sourceHash") ?? TryGetString(it, "SourceHash");
            var docLanguage = TryGetString(it, "docLanguage") ?? TryGetString(it, "DocLanguage");
            var profileLanguage = TryGetString(it, "profileLanguage") ?? TryGetString(it, "ProfileLanguage");

            items.Add(new ToolMemory.DocumentItem
            {
                PdfRef = pdfRef,
                DocId = docId,
                DocPath = docPath,
                DocName = docName,
                Category = cat,
                CategoryRef = categoryRef,
                CategoryPath = categoryPath,
                Pages = pages,
                SourceHash = sourceHash,
                DocLanguage = docLanguage,
                ProfileLanguage = profileLanguage
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

    private static string? NormalizeRagCategoryScope(string? category)
    {
        var normalized = NormalizeDocPath(category).Trim('/');
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static (string? LegacyCategory, string? CategoryPath, string? CategoryRef) BuildRagCategoryScope(string? category)
    {
        var normalized = NormalizeRagCategoryScope(category);
        if (string.IsNullOrWhiteSpace(normalized))
            return (null, null, null);

        if (LooksLikeCategoryRef(normalized))
            return (null, null, normalized);

        if (normalized.Contains('/', StringComparison.Ordinal))
            return (null, normalized, null);

        return (null, normalized, null);
    }

    private static bool LooksLikeCategoryRef(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Contains('/', StringComparison.Ordinal))
            return false;

        var compact = trimmed
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);

        if (compact.Length > 3
            && compact.StartsWith("cat", StringComparison.OrdinalIgnoreCase)
            && compact[3..].All(char.IsDigit))
        {
            return true;
        }

        if (trimmed.Length <= 4
            || (!trimmed.StartsWith("cat_", StringComparison.OrdinalIgnoreCase)
                && !trimmed.StartsWith("cat-", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var suffix = trimmed[4..];
        return suffix.Any(char.IsLetterOrDigit)
            && suffix.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-');
    }

    private static string GuessCategoryPath(string? docPath)
    {
        var normalized = NormalizeDocPath(docPath).Trim('/');
        var slash = normalized.LastIndexOf('/');
        return slash > 0 ? normalized[..slash] : string.Empty;
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
        var categoryScope = BuildRagCategoryScope(category);
        var body = JsonSerializer.Serialize(new
        {
            query,
            category = categoryScope.LegacyCategory,
            categoryPath = categoryScope.CategoryPath,
            categoryRef = categoryScope.CategoryRef,
            topK,
            mode = NormalizeRagSearchApiMode(mode),
            docId = string.IsNullOrWhiteSpace(docId) ? null : docId.Trim(),
            docPath = string.IsNullOrWhiteSpace(docPath) ? null : NormalizeDocPath(docPath),
            includeContextualSnippet = true
        }, JsonOpts);

        using var resp = await SendWithRateLimitRetryAsync(() => NewRequest(HttpMethod.Post, "/rag/search", body), ct);
        await EnsureSuccessOrThrowBackendBusyAsync(resp, ct).ConfigureAwait(false);

        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<RagSearchResponse>(json, JsonOpts)
               ?? throw new Exception(T("api.error.invalid_rag_search_response"));
    }

    private static string? NormalizeRagSearchApiMode(string? mode)
    {
        var normalized = (mode ?? "auto").Trim().ToLowerInvariant();
        return normalized switch
        {
            "auto" or "" => null,
            "standard" => "balanced",
            "strict" or "precise" or "fast" => "focused",
            "focused" or "balanced" or "broad" => normalized,
            _ => null
        };
    }

    private static string? NormalizeRagSearchResearchMode(string? mode)
    {
        var normalized = (mode ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "" or "none" or "default" => null,
            "research" or "exploration" or "broad_exploration" or "source_exploration" or "evidence_exploration" => normalized,
            _ => null
        };
    }

}
