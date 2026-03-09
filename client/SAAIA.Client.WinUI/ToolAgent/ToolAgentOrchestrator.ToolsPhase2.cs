using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private sealed record ResolvedDocRef(string DocId, string DocPath, string DocName, string? Category, int? Pages);

    private async Task<JsonElement> ExecDocumentsGetResolvedAsync(JsonElement args, CancellationToken ct)
    {
        var docRef = GetStringArg(args, "docRef") ?? GetStringArg(args, "docId");
        if (string.IsNullOrWhiteSpace(docRef))
            return JsonDocument.Parse("{\"error\":\"missing_doc_ref\"}").RootElement;

        var resolved = await ResolveDocRefAsync(docRef!, ct).ConfigureAwait(false);
        if (resolved is null)
            return JsonDocument.Parse("{\"found\":false,\"error\":\"doc_not_found\"}").RootElement;

        return await _api.DocumentsGetAsync(resolved.DocId, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecDocumentsCountAsync(JsonElement args, CancellationToken ct)
    {
        var categoryPath = GetStringArg(args, "categoryPath") ?? GetStringArg(args, "category");
        var q = GetStringArg(args, "q");
        return await _api.DocumentsCountAsync(categoryPath, q, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecDocumentsTreeAsync(JsonElement args, CancellationToken ct)
    {
        var path = GetStringArg(args, "path") ?? GetStringArg(args, "categoryPath");
        var depth = GetIntArg(args, "depth") ?? 10;
        var format = GetStringArg(args, "format") ?? "markdown";
        return await _api.DocumentsTreeAsync(path, depth, format, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecDocumentsStatsAsync(JsonElement args, CancellationToken ct)
    {
        var path = GetStringArg(args, "path") ?? GetStringArg(args, "categoryPath");
        return await _api.DocumentsStatsAsync(path, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecSummaryGetAsync(JsonElement args, CancellationToken ct)
    {
        var docRef = GetRequiredDocRef(args);
        if (docRef is null)
            return JsonDocument.Parse("{\"error\":\"missing_doc_ref\"}").RootElement;

        var level = GetStringArg(args, "level") ?? "medium";
        var resolved = await ResolveDocRefAsync(docRef, ct).ConfigureAwait(false);
        if (resolved is null)
            return JsonDocument.Parse("{\"found\":false,\"error\":\"doc_not_found\"}").RootElement;

        return await _api.SummaryGetAsync(resolved.DocId, level, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecSummaryExistsAsync(JsonElement args, CancellationToken ct)
    {
        var docRef = GetRequiredDocRef(args);
        if (docRef is null)
            return JsonDocument.Parse("{\"exists\":false,\"error\":\"missing_doc_ref\"}").RootElement;

        var level = GetStringArg(args, "level") ?? "medium";
        var resolved = await ResolveDocRefAsync(docRef, ct).ConfigureAwait(false);
        if (resolved is null)
            return JsonDocument.Parse("{\"exists\":false,\"error\":\"doc_not_found\"}").RootElement;

        return await _api.SummaryExistsAsync(resolved.DocId, level, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecSummarySearchAsync(JsonElement args, CancellationToken ct)
    {
        var q = GetStringArg(args, "q") ?? string.Empty;
        var limit = GetIntArg(args, "limit") ?? 20;
        var offset = GetIntArg(args, "offset") ?? 0;
        return await _api.SummarySearchAsync(q, limit, offset, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecAdminSummaryMissingAsync(JsonElement args, CancellationToken ct)
    {
        var limit = GetIntArg(args, "limit") ?? 100;
        var offset = GetIntArg(args, "offset") ?? 0;
        var categoryPath = GetStringArg(args, "categoryPath") ?? GetStringArg(args, "category");
        return await _api.AdminSummaryMissingAsync(limit, offset, categoryPath, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecAdminSummaryRequestAsync(JsonElement args, CancellationToken ct)
    {
        var docRef = GetRequiredDocRef(args);
        if (docRef is null)
            return JsonDocument.Parse("{\"error\":\"missing_doc_ref\"}").RootElement;

        var resolved = await ResolveDocRefAsync(docRef, ct).ConfigureAwait(false);
        if (resolved is null)
            return JsonDocument.Parse("{\"error\":\"doc_not_found\"}").RootElement;

        var level = GetStringArg(args, "level") ?? "medium";
        return await _api.AdminSummaryRequestAsync(resolved.DocId, level, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecAdminSummaryGenerateAsync(JsonElement args, CancellationToken ct)
    {
        var docRef = GetRequiredDocRef(args);
        if (docRef is null)
            return JsonDocument.Parse("{\"error\":\"missing_doc_ref\"}").RootElement;

        var resolved = await ResolveDocRefAsync(docRef, ct).ConfigureAwait(false);
        if (resolved is null)
            return JsonDocument.Parse("{\"error\":\"doc_not_found\"}").RootElement;

        var level = GetStringArg(args, "level") ?? "medium";
        var force = GetBoolArg(args, "force") ?? false;
        return await _api.AdminSummaryGenerateAsync(resolved.DocId, level, force, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecAdminSummarySubmitAsync(JsonElement args, CancellationToken ct)
    {
        var docRef = GetRequiredDocRef(args);
        if (docRef is null)
            return JsonDocument.Parse("{\"error\":\"missing_doc_ref\"}").RootElement;

        var resolved = await ResolveDocRefAsync(docRef, ct).ConfigureAwait(false);
        if (resolved is null)
            return JsonDocument.Parse("{\"error\":\"doc_not_found\"}").RootElement;

        var level = GetStringArg(args, "level") ?? "medium";
        var docLanguage = GetStringArg(args, "docLanguage") ?? GetStringArg(args, "language") ?? _mem.LastLanguage;
        var sourceHash = GetStringArg(args, "sourceHash") ?? string.Empty;
        var summaryText = GetStringArg(args, "summaryText") ?? GetStringArg(args, "content") ?? string.Empty;
        var jobId = GetStringArg(args, "jobId");
        JsonElement? meta = args.TryGetProperty("meta", out var metaEl) ? metaEl : null;

        return await _api.AdminSummarySubmitAsync(resolved.DocId, level, docLanguage, sourceHash, summaryText, jobId, meta, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecAdminSummaryStatusAsync(JsonElement args, CancellationToken ct)
    {
        var jobId = GetStringArg(args, "jobId");
        if (string.IsNullOrWhiteSpace(jobId))
            return JsonDocument.Parse("{\"error\":\"missing_job_id\"}").RootElement;

        return await _api.AdminSummaryStatusAsync(jobId!, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecAdminSummaryDeleteAsync(JsonElement args, CancellationToken ct)
    {
        var docRef = GetRequiredDocRef(args);
        if (docRef is null)
            return JsonDocument.Parse("{\"error\":\"missing_doc_ref\"}").RootElement;

        var resolved = await ResolveDocRefAsync(docRef, ct).ConfigureAwait(false);
        if (resolved is null)
            return JsonDocument.Parse("{\"error\":\"doc_not_found\"}").RootElement;

        var level = GetStringArg(args, "level") ?? "medium";
        return await _api.AdminSummaryDeleteAsync(resolved.DocId, level, ct).ConfigureAwait(false);
    }

    private Task<JsonElement> ExecAdminCatalogHealthAsync(CancellationToken ct)
        => _api.AdminCatalogHealthAsync(ct);

    private Task<JsonElement> ExecAdminCatalogRescanNowAsync(CancellationToken ct)
        => _api.AdminCatalogRescanNowAsync(ct);

    private async Task<JsonElement> ExecAdminIngestionReindexAsync(JsonElement args, CancellationToken ct)
    {
        var docRef = GetRequiredDocRef(args);
        if (docRef is null)
            return JsonDocument.Parse("{\"error\":\"missing_doc_ref\"}").RootElement;

        var resolved = await ResolveDocRefAsync(docRef, ct).ConfigureAwait(false);
        if (resolved is null)
            return JsonDocument.Parse("{\"error\":\"doc_not_found\"}").RootElement;

        return await _api.AdminIngestionReindexAsync(resolved.DocPath, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecAdminJobsListAsync(JsonElement args, CancellationToken ct)
    {
        var type = GetStringArg(args, "type");
        var limit = GetIntArg(args, "limit") ?? 100;
        var offset = GetIntArg(args, "offset") ?? 0;
        return await _api.AdminJobsListAsync(type, limit, offset, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecAdminJobsCancelAsync(JsonElement args, CancellationToken ct)
    {
        var jobId = GetStringArg(args, "jobId");
        if (string.IsNullOrWhiteSpace(jobId))
            return JsonDocument.Parse("{\"error\":\"missing_job_id\"}").RootElement;

        return await _api.AdminJobsCancelAsync(jobId!, ct).ConfigureAwait(false);
    }

    private JsonElement ExecDiagnosticPerformance(JsonElement args)
    {
        var payload = new
        {
            supported = true,
            routerMs = _lastRouterMs,
            toolsMs = _lastToolsMs,
            writerMs = _lastWriterMs,
            totalMs = _lastTotalMs,
            tools = _lastToolDurations.Select(x => new { tool = x.tool, durationMs = x.durationMs, ok = x.ok }).ToList(),
            language = _mem.LastLanguage
        };

        return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;
    }

    private JsonElement ExecSourcesResolveV2(JsonElement args)
    {
        var rawRef = GetStringArg(args, "ref") ?? GetStringArg(args, "pdfRef");
        if (string.IsNullOrWhiteSpace(rawRef))
            return JsonDocument.Parse("{\"source\":null}").RootElement;

        var source = ResolveSourceRef(rawRef!);
        if (source is null)
            return JsonDocument.Parse("{\"source\":null}").RootElement;

        var payload = new
        {
            source = new
            {
                docPath = source.DocPath,
                pageStart = source.PageStart,
                pageEnd = source.PageEnd,
                label = source.Label
            }
        };

        return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;
    }

    private ToolMemory.SourceRef? ResolveSourceRef(string rawRef)
    {
        var s = (rawRef ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(s))
            return null;

        var mPdf = Regex.Match(s, @"(?i)\bPDF\s*0*(?<n>\d{1,4})\b");
        if (mPdf.Success && int.TryParse(mPdf.Groups["n"].Value, out var nPdf) && nPdf > 0)
        {
            var key = $"PDF{nPdf:00}";
            if (_mem.PdfMap.TryGetValue(key, out var mapped) && mapped is not null && !string.IsNullOrWhiteSpace(mapped.DocPath))
                return BuildSourceFromDocument(mapped);
        }

        var mNum = Regex.Match(s, @"\b(?<n>\d{1,4})\b");
        if (mNum.Success && int.TryParse(mNum.Groups["n"].Value, out var n) && n > 0)
        {
            if (_mem.LastListedDocuments is { Count: > 0 } && n <= _mem.LastListedDocuments.Count)
                return BuildSourceFromDocument(_mem.LastListedDocuments[n - 1]);

            var key = $"PDF{n:00}";
            if (_mem.PdfMap.TryGetValue(key, out var mapped) && mapped is not null && !string.IsNullOrWhiteSpace(mapped.DocPath))
                return BuildSourceFromDocument(mapped);
        }

        foreach (var doc in _mem.LastListedDocuments)
        {
            if (string.Equals(doc.DocPath, s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(doc.DocName, s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(doc.DocId, s, StringComparison.OrdinalIgnoreCase))
            {
                return BuildSourceFromDocument(doc);
            }
        }

        foreach (var kv in _mem.PdfMap.Values)
        {
            if (string.Equals(kv.DocPath, s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(kv.DocName, s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(kv.DocId, s, StringComparison.OrdinalIgnoreCase))
            {
                return BuildSourceFromDocument(kv);
            }
        }

        return null;
    }

    private ToolMemory.SourceRef BuildSourceFromDocument(ToolMemory.DocumentItem doc)
    {
        var pageStart = 1;
        var pageEnd = 1;
        var used = _mem.LastSourcesUsed?
            .FirstOrDefault(s => string.Equals((s.DocPath ?? string.Empty).Replace('\\', '/'), (doc.DocPath ?? string.Empty).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
        if (used is not null)
        {
            pageStart = Math.Max(1, used.PageStart);
            pageEnd = Math.Max(pageStart, used.PageEnd);
        }

        return new ToolMemory.SourceRef
        {
            DocPath = doc.DocPath,
            PageStart = pageStart,
            PageEnd = pageEnd,
            Label = string.IsNullOrWhiteSpace(doc.DocName) ? doc.DocPath : $"{doc.DocName} (p.{pageStart})"
        };
    }

    private async Task<ResolvedDocRef?> ResolveDocRefAsync(string docRef, CancellationToken ct)
    {
        var s = (docRef ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(s))
            return null;

        if (_mem.PdfMap.TryGetValue(s, out var mapped) && mapped is not null)
            return new ResolvedDocRef(mapped.DocId, mapped.DocPath, mapped.DocName, mapped.Category, mapped.Pages);

        var mPdf = Regex.Match(s, @"(?i)\bPDF\s*0*(?<n>\d{1,4})\b");
        if (mPdf.Success && int.TryParse(mPdf.Groups["n"].Value, out var nPdf) && nPdf > 0)
        {
            var key = $"PDF{nPdf:00}";
            if (_mem.PdfMap.TryGetValue(key, out var pdfDoc) && pdfDoc is not null)
                return new ResolvedDocRef(pdfDoc.DocId, pdfDoc.DocPath, pdfDoc.DocName, pdfDoc.Category, pdfDoc.Pages);
        }

        if (int.TryParse(s, out var idx) && idx > 0 && _mem.LastListedDocuments is { Count: > 0 } && idx <= _mem.LastListedDocuments.Count)
        {
            var d = _mem.LastListedDocuments[idx - 1];
            return new ResolvedDocRef(d.DocId, d.DocPath, d.DocName, d.Category, d.Pages);
        }

        foreach (var d in _mem.LastListedDocuments)
        {
            if (string.Equals(d.DocId, s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.DocPath, s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.DocName, s, StringComparison.OrdinalIgnoreCase))
            {
                return new ResolvedDocRef(d.DocId, d.DocPath, d.DocName, d.Category, d.Pages);
            }
        }

        if (Guid.TryParse(s, out _))
        {
            var doc = await _api.DocumentsGetAsync(s, ct).ConfigureAwait(false);
            var gotId = TryGetString(doc, "DocId") ?? TryGetString(doc, "docId") ?? s;
            var path = TryGetString(doc, "DocPath") ?? TryGetString(doc, "docPath") ?? string.Empty;
            var name = TryGetString(doc, "DocName") ?? TryGetString(doc, "docName") ?? path;
            var category = TryGetString(doc, "Category") ?? TryGetString(doc, "category");
            var pages = TryGetInt(doc, "PageCount") ?? TryGetInt(doc, "pageCount") ?? TryGetInt(doc, "pages");
            if (!string.IsNullOrWhiteSpace(path))
                return new ResolvedDocRef(gotId, path, name, category, pages);
        }

        var search = await _api.DocumentsSearchAsync(s, category: null, limit: 20, offset: 0, ct).ConfigureAwait(false);
        if (search.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            JsonElement? best = null;
            foreach (var it in items.EnumerateArray())
            {
                var name = TryGetString(it, "docName") ?? string.Empty;
                var path = TryGetString(it, "docPath") ?? string.Empty;
                if (string.Equals(name, s, StringComparison.OrdinalIgnoreCase) || string.Equals(path, s, StringComparison.OrdinalIgnoreCase))
                {
                    best = it;
                    break;
                }

                best ??= it;
            }

            if (best.HasValue)
            {
                var it = best.Value;
                var docId = TryGetString(it, "docId") ?? string.Empty;
                var docPath = TryGetString(it, "docPath") ?? string.Empty;
                var docName = TryGetString(it, "docName") ?? docPath;
                var category = TryGetString(it, "category");
                var pages = TryGetInt(it, "pages");
                if (!string.IsNullOrWhiteSpace(docId) && !string.IsNullOrWhiteSpace(docPath))
                    return new ResolvedDocRef(docId, docPath, docName, category, pages);
            }
        }

        return null;
    }


    private async Task<JsonElement> ExecRagSummarizeLiveAsync(JsonElement args, CancellationToken ct)
    {
        var docRef = GetRequiredDocRef(args);
        if (docRef is null)
            return JsonDocument.Parse("{\"error\":\"missing_doc_ref\"}").RootElement;

        var resolved = await ResolveDocRefAsync(docRef, ct).ConfigureAwait(false);
        if (resolved is null)
            return JsonDocument.Parse("{\"error\":\"doc_not_found\"}").RootElement;

        var level = (GetStringArg(args, "level") ?? "medium").Trim().ToLowerInvariant();
        var language = (GetStringArg(args, "language") ?? _mem.LastLanguage).Trim().ToLowerInvariant();
        var maxWords = GetIntArg(args, "maxWords") ?? (level == "short" ? 120 : 220);

        JsonElement raw;
        try
        {
            raw = await _api.RagDebugScrollAsync(cursor: null, limit: 64, docPath: resolved.DocPath, ct).ConfigureAwait(false);
        }
        catch
        {
            return JsonDocument.Parse("{\"error\":\"debug_scroll_unavailable\"}").RootElement;
        }

        var chunks = ExtractChunkTexts(raw).Take(24).ToList();
        if (chunks.Count == 0)
            return JsonDocument.Parse("{\"error\":\"no_chunks_found\"}").RootElement;

        var prompt = BuildLiveSummaryPrompt(resolved, chunks, language, level, maxWords);
        var completion = await _llm.CompleteAsync(new[]
        {
            ("system", "You summarize one document only. Return ONLY valid JSON with schema {\"summaryText\":string,\"anchors\":[{\"pageStart\":int,\"pageEnd\":int,\"label\":string}]}. Keep it factual and concise."),
            ("user", prompt)
        }, forceJson: true, ct).ConfigureAwait(false);

        string summaryText = string.Empty;
        object anchors = new[] { new { pageStart = 1, pageEnd = 1, label = $"{resolved.DocName} (live)" } };

        if (TryExtractJsonObject(completion, out var jsonCandidate))
        {
            try
            {
                using var parsed = JsonDocument.Parse(jsonCandidate);
                var root = parsed.RootElement;
                if (root.TryGetProperty("summaryText", out var st) && st.ValueKind == JsonValueKind.String)
                    summaryText = (st.GetString() ?? string.Empty).Trim();
                if (root.TryGetProperty("anchors", out var a) && a.ValueKind == JsonValueKind.Array)
                    anchors = JsonSerializer.Deserialize<object>(a.GetRawText()) ?? anchors;
            }
            catch
            {
            }
        }

        if (string.IsNullOrWhiteSpace(summaryText))
            summaryText = string.Join(" ", chunks).Trim();

        var payload = new
        {
            docId = resolved.DocId,
            docPath = resolved.DocPath,
            docName = resolved.DocName,
            mode = "live",
            level,
            language,
            summaryText,
            anchors
        };
        return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;
    }

    private Task<JsonElement> ExecAdminQdrantHealthAsync(CancellationToken ct)
        => _api.AdminQdrantHealthAsync(ct);

    private static IEnumerable<string> ExtractChunkTexts(JsonElement raw)
    {
        if (!raw.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            yield break;
        if (!result.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in points.EnumerateArray())
        {
            if (!item.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                continue;
            if (!payload.TryGetProperty("text", out var textEl) || textEl.ValueKind != JsonValueKind.String)
                continue;

            var chunkText = (textEl.GetString() ?? string.Empty).Trim();
            if (chunkText.Length > 0)
                yield return chunkText;
        }
    }

    private static string BuildLiveSummaryPrompt(ResolvedDocRef doc, List<string> chunks, string language, string level, int maxWords)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Document: {doc.DocName}");
        sb.AppendLine($"Path: {doc.DocPath}");
        sb.AppendLine($"TargetLanguage: {language}");
        sb.AppendLine($"Level: {level}");
        sb.AppendLine($"MaxWords: {maxWords}");
        sb.AppendLine();
        sb.AppendLine("Summarize only this document. Mention the main purpose, main sections/topics, and any notable constraints. Keep it factual.");
        sb.AppendLine();
        sb.AppendLine("CHUNKS:");
        for (var i = 0; i < chunks.Count; i++)
            sb.AppendLine($"[{i + 1}] {chunks[i]}");
        return sb.ToString();
    }

    private static string? GetRequiredDocRef(JsonElement args)
        => GetStringArg(args, "docRef") ?? GetStringArg(args, "docId") ?? GetStringArg(args, "ref");

    private static string? GetStringArg(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static int? GetIntArg(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n)) return n;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }

    private static bool? GetBoolArg(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        return value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }
}
