using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private void UpdateLastListedDocumentsFromExtractionQualityResult(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var docs = new List<ToolMemory.DocumentItem>();
        foreach (var entry in items.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var docPath = (TryGetString(entry, "docPath") ?? TryGetString(entry, "DocPath") ?? string.Empty)
                .Replace('\\', '/')
                .TrimStart('/');
            if (string.IsNullOrWhiteSpace(docPath))
                continue;

            var docName =
                TryGetString(entry, "docName")
                ?? TryGetString(entry, "DocName")
                ?? TryGetString(entry, "canonicalName")
                ?? TryGetString(entry, "CanonicalName")
                ?? Path.GetFileName(docPath);
            var categoryPath = (TryGetString(entry, "categoryPath") ?? TryGetString(entry, "CategoryPath") ?? GuessCategoryPath(docPath))
                .Replace('\\', '/')
                .Trim('/');
            var category = TryGetString(entry, "category")
                ?? TryGetString(entry, "Category")
                ?? ExtractTopLevelCategoryFromPath(categoryPath);
            var pdfRef = NormalizePdfRef(TryGetString(entry, "pdfRef") ?? TryGetString(entry, "PdfRef"))
                ?? $"PDF{docs.Count + 1:00}";

            var doc = new ToolMemory.DocumentItem
            {
                PdfRef = pdfRef,
                DocId = TryGetString(entry, "docId") ?? TryGetString(entry, "DocId") ?? string.Empty,
                DocPath = docPath,
                DocName = string.IsNullOrWhiteSpace(docName) ? Path.GetFileName(docPath) : docName,
                Category = category ?? string.Empty,
                CategoryRef = NullIfWhiteSpace(TryGetString(entry, "categoryRef") ?? TryGetString(entry, "CategoryRef")),
                CategoryPath = categoryPath,
                SourceHash = NullIfWhiteSpace(TryGetString(entry, "sourceHash") ?? TryGetString(entry, "SourceHash")),
                DocLanguage = NullIfWhiteSpace(TryGetDocumentLanguage(entry)),
                ProfileLanguage = NullIfWhiteSpace(TryGetString(entry, "profileLanguage") ?? TryGetString(entry, "ProfileLanguage")),
                Pages = TryGetInt(entry, "pageCount") ?? TryGetInt(entry, "PageCount") ?? TryGetInt(entry, "pages") ?? TryGetInt(entry, "Pages")
            };

            docs.Add(doc);
            RegisterDocumentReference(doc.PdfRef, doc);
            RegisterDocumentReference(doc.DocId, doc);
            RegisterDocumentReference(doc.DocPath, doc);
            RegisterDocumentReference(doc.DocName, doc);
        }

        if (docs.Count == 0)
            return;

        _mem.LastListedDocuments = docs;
        _mem.LastListOffset = 0;
        _mem.LastListLimit = TryGetInt(result, "limit") ?? docs.Count;
        _mem.LastListTotal = TryGetInt(result, "total") ?? TryGetInt(TryGetObject(result, "summary") ?? default, "totalDocuments") ?? docs.Count;
        _mem.LastListEndOfList = true;
        _mem.LastListCategoryPath = TryGetString(result, "scopePath");
        _mem.LastListQuery = null;
        _mem.PromoteDocumentsToWorkspace(docs);
    }

    private void RegisterDocumentReference(string? key, ToolMemory.DocumentItem doc)
    {
        key = NullIfWhiteSpace(key);
        if (key is null)
            return;

        _mem.PdfMap[key] = doc;
    }

    private static string? NormalizePdfRef(string? value)
    {
        value = NullIfWhiteSpace(value);
        if (value is null)
            return null;

        var match = Regex.Match(value, @"(?i)^PDF\s*0*(?<n>\d{1,4})$");
        return match.Success && int.TryParse(match.Groups["n"].Value, out var n) && n > 0
            ? $"PDF{n:00}"
            : value;
    }

    private static string ExtractTopLevelCategoryFromPath(string? path)
    {
        var normalized = (path ?? string.Empty).Replace('\\', '/').Trim('/');
        var index = normalized.IndexOf('/');
        return index > 0 ? normalized[..index] : normalized;
    }

}
