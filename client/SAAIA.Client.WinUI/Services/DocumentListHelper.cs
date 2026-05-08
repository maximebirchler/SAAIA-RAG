using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using SAAIA.Client.WinUI.Services.ToolAgent;

namespace SAAIA.Client.WinUI.Services;

/// <summary>
/// Shared logic to:
/// - sanitize document listings against the local filesystem (avoid ghost docPath after move/rename)
/// - keep the ToolMemory mapping (PDFxx) stable across turns
/// - build a user-friendly list output.
///
/// This intentionally starts from the BACKEND index (documents.list/search) to respect the CDC rule:
/// "list only indexed documents"; then it sanitizes docPath to match the real file path.
/// </summary>
internal static class DocumentListHelper
{
    public static (List<ToolMemory.DocumentItem> docs, int limit, int offset, int? total, bool endOfList, int dropped)
        Sanitize(JsonElement documentsListResponse, ToolMemory mem)
    {
        var docs = new List<ToolMemory.DocumentItem>();
        var dropped = 0;

        var limit = documentsListResponse.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : mem.LastListLimit;
        var offset = documentsListResponse.TryGetProperty("offset", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt32() : mem.LastListOffset;
        var total = documentsListResponse.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : (int?)null;
        var endOfList = documentsListResponse.TryGetProperty("endOfList", out var e) && e.ValueKind == JsonValueKind.True;

        if (!documentsListResponse.TryGetProperty("items", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return (docs, limit, offset, total, endOfList, dropped);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var idx = 0;
        foreach (var it in arr.EnumerateArray())
        {
            if (it.ValueKind != JsonValueKind.Object) { dropped++; idx++; continue; }

            var pdfRef = GetString(it, "pdfRef") ?? "";

            // Normalize PDF refs to a stable format (PDF01, PDF02, ...)
            // to make sources.resolve robust to variants like 'PDF1'.
            var mRef = Regex.Match(pdfRef.Trim(), @"(?i)^PDF\s*0*(?<n>\d{1,4})$");
            if (mRef.Success && int.TryParse(mRef.Groups["n"].Value, out var nRef) && nRef > 0)
                pdfRef = $"PDF{nRef:00}";
            var docId = GetString(it, "docId") ?? "";
            var docPath = GetString(it, "docPath") ?? "";
            var docName = GetString(it, "docName") ?? "";
            var category = GetString(it, "category") ?? "";
            var categoryRef = GetString(it, "categoryRef") ?? "";
            var categoryPath = GetString(it, "categoryPath") ?? "";
            var sourceHash = GetString(it, "sourceHash");
            var docLanguage = GetString(it, "docLanguage");
            var profileLanguage = GetString(it, "profileLanguage");
            var pages = GetInt(it, "pages");

            // If backend doesn't provide a stable ref, synthesize one based on paging.
            if (string.IsNullOrWhiteSpace(pdfRef))
                pdfRef = $"PDF{(offset + idx + 1):00}";

            var sanitized = TryResolveToDisplayPath(docPath, docName);
            if (string.IsNullOrWhiteSpace(sanitized)) { dropped++; idx++; continue; }

            var resolvedDisplayPath = sanitized.Replace('\\', '/').TrimStart('/');
            var backendDisplayPath = NormalizeCategoryPath(DocumentPathResolver.ToDisplayPath(docPath) ?? docPath);
            var resolvedFileName = Path.GetFileName(resolvedDisplayPath);
            var backendFileName = Path.GetFileName((docName ?? string.Empty).Trim());
            var mustRewriteFromResolved = !string.Equals(backendDisplayPath, resolvedDisplayPath, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(resolvedFileName)
                    && !string.IsNullOrWhiteSpace(backendFileName)
                    && !string.Equals(backendFileName, resolvedFileName, StringComparison.OrdinalIgnoreCase));

            // Deduplicate by resolved display path
            if (!seen.Add(resolvedDisplayPath)) { idx++; continue; }

            var normalizedCategoryPath = mustRewriteFromResolved
                ? GuessCategoryPathFromPath(resolvedDisplayPath)
                : NormalizeCategoryPath(!string.IsNullOrWhiteSpace(categoryPath)
                    ? categoryPath
                    : GuessCategoryPathFromPath(resolvedDisplayPath));

            var normalizedCategory = mustRewriteFromResolved
                ? GetMainCategory(normalizedCategoryPath, resolvedDisplayPath)
                : (string.IsNullOrWhiteSpace(category)
                    ? GetMainCategory(normalizedCategoryPath, resolvedDisplayPath)
                    : NormalizeTopLevelCategory(category));

            var d = new ToolMemory.DocumentItem
            {
                PdfRef = pdfRef,
                DocId = docId,
                DocPath = resolvedDisplayPath,
                DocName = mustRewriteFromResolved
                    ? (resolvedFileName ?? string.Empty)
                    : (string.IsNullOrWhiteSpace(docName) ? (resolvedFileName ?? string.Empty) : docName),
                Category = normalizedCategory,
                CategoryRef = string.IsNullOrWhiteSpace(categoryRef) ? null : categoryRef,
                CategoryPath = normalizedCategoryPath,
                SourceHash = string.IsNullOrWhiteSpace(sourceHash) ? null : sourceHash,
                DocLanguage = string.IsNullOrWhiteSpace(docLanguage) ? null : docLanguage,
                ProfileLanguage = string.IsNullOrWhiteSpace(profileLanguage) ? null : profileLanguage,
                Pages = pages
            };

            docs.Add(d);

            // Always keep mapping for sources.resolve (PDFxx) even when backend didn't provide it.
            if (!string.IsNullOrWhiteSpace(pdfRef))
                mem.PdfMap[pdfRef] = d;

            idx++;
        }

        // Update paging memory
        mem.LastListLimit = limit;
        mem.LastListOffset = offset;
        mem.LastListTotal = total;
        mem.LastListEndOfList = endOfList;
        mem.LastListedDocuments = docs;
        mem.PromoteDocumentsToWorkspace(docs);

        return (docs, limit, offset, total, endOfList, dropped);
    }

    public static string BuildUserText(List<ToolMemory.DocumentItem> docs, bool endOfList, int dropped)
    {
        _ = endOfList;
        _ = dropped;
        return BuildTokenizedList(docs);
    }

    public static string BuildTokenizedList(IEnumerable<ToolMemory.DocumentItem> docs)
    {
        var list = docs?.Where(d => d is not null).ToList() ?? new List<ToolMemory.DocumentItem>();
        if (list.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        for (var i = 0; i < list.Count; i++)
        {
            var d = list[i];
            var label = GetStableDisplayLabel(d);
            if (string.IsNullOrWhiteSpace(d.DocPath) || string.IsNullOrWhiteSpace(label))
                continue;

            sb.AppendLine($"{i + 1}. [[open|{d.DocPath}|1|{label}]]");
        }

        return sb.ToString().TrimEnd();
    }

    public static string GetStableDisplayLabel(ToolMemory.DocumentItem doc)
    {
        var normalizedPath = (doc?.DocPath ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');
        var fromPath = Path.GetFileNameWithoutExtension(normalizedPath);
        if (!string.IsNullOrWhiteSpace(fromPath))
            return EscapeTokenLabel(fromPath);

        var fromName = Path.GetFileNameWithoutExtension((doc?.DocName ?? string.Empty).Trim());
        if (!string.IsNullOrWhiteSpace(fromName))
            return EscapeTokenLabel(fromName);

        var fallback = Path.GetFileName(normalizedPath);
        if (!string.IsNullOrWhiteSpace(fallback))
            return EscapeTokenLabel(fallback);

        return EscapeTokenLabel(doc?.DocName ?? string.Empty);
    }

    private static string EscapeTokenLabel(string s)
    {
        // Keep the token format safe.
        return (s ?? string.Empty)
            .Replace("|", " ")
            .Replace("]", ")");
    }

    private static string GetMainCategory(string? categoryPath, string? docPath)
    {
        var c = NormalizeCategoryPath(categoryPath);
        if (string.IsNullOrWhiteSpace(c))
            c = GuessCategoryPathFromPath(docPath ?? string.Empty);

        var idx = c.IndexOf('/');
        return idx > 0 ? c.Substring(0, idx) : c;
    }

    private static string? TryResolveToDisplayPath(string docPath, string docName)
    {
        // 0) If docPath is already a clean relative path, normalize it.
        // IMPORTANT:
        // Do NOT validate it through DocumentPathResolver.Resolve(quick), because Resolve(...) includes
        // a filename-search fallback under the documents root. That behavior is useful for recovering
        // moved files, but it would incorrectly "validate" a ghost path like "Ghost/file.pdf" merely
        // because a file with the same name exists elsewhere. In that case we must return the real path,
        // not preserve the ghost relative path.
        var quick = DocumentPathResolver.ToDisplayPath(docPath);
        if (!string.IsNullOrWhiteSpace(quick) && !quick.Contains("..") && ExistsExactlyUnderDocumentsRoot(quick))
            return quick;

        // 1) try exact docPath (may recover a moved file through filename search and then canonicalize it)
        var abs = DocumentPathResolver.Resolve(docPath);
        if (!string.IsNullOrWhiteSpace(abs) && File.Exists(abs))
            return DocumentPathResolver.ToDisplayPath(abs);

        // 2) try docName as filename
        if (!string.IsNullOrWhiteSpace(docName))
        {
            var matches = DocumentInventory.FindByFileName(docName);
            if (matches.Count == 1)
                return DocumentPathResolver.ToDisplayPath(matches[0]);
        }

        // 3) try file name from docPath
        var fn = Path.GetFileName(docPath);
        if (!string.IsNullOrWhiteSpace(fn))
        {
            var matches = DocumentInventory.FindByFileName(fn);
            if (matches.Count == 1)
                return DocumentPathResolver.ToDisplayPath(matches[0]);
        }

        return null;
    }

    private static bool ExistsExactlyUnderDocumentsRoot(string displayPath)
    {
        try
        {
            var root = DocumentPathResolver.GetDocumentsRoot();
            var rel = displayPath
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(root, rel));
            return File.Exists(candidate);
        }
        catch
        {
            return false;
        }
    }

    private static string GuessCategoryPathFromPath(string rel)
    {
        var s = NormalizeCategoryPath(rel);
        var idx = s.LastIndexOf('/');
        return idx > 0 ? s.Substring(0, idx) : string.Empty;
    }

    private static string NormalizeCategoryPath(string? raw)
        => (raw ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/').TrimEnd('/');

    private static string NormalizeTopLevelCategory(string? raw)
    {
        var normalized = NormalizeCategoryPath(raw);
        var idx = normalized.IndexOf('/');
        return idx > 0 ? normalized.Substring(0, idx) : normalized;
    }

    private static string? GetString(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : v.ToString();
    }

    private static int? GetInt(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var n2)) return n2;
        return null;
    }
}
