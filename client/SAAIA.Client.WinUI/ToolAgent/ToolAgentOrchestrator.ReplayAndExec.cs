using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Models;
using SAAIA.Client.WinUI.Services;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private async Task<(bool ok, string answer, object? sourcesPayload)> TryReplayLastInventoryAnswerWithWriterAsync(
        IReadOnlyList<(string role, string content)> chatHistory,
        string userMessage,
        string language,
        CancellationToken ct,
        Action<string>? onPhase,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        _ = chatHistory;
        _ = userMessage;

        if (_mem.LastDeterministicRender is null
            || string.IsNullOrWhiteSpace(_mem.LastDeterministicRender.Kind)
            || string.IsNullOrWhiteSpace(_mem.LastDeterministicRender.DataJson)
            || !IsInventoryIntent(_mem.LastDeterministicRender.RouterIntent ?? _mem.LastRouterIntent))
        {
            return (false, string.Empty, null);
        }

        try
        {
            var answer = TryRenderLastDeterministicAnswer(language);
            if (string.IsNullOrWhiteSpace(answer))
                return (false, string.Empty, null);

            onPhase?.Invoke(DeterministicAgentText.PhaseWriting(language));
            onProgress?.Invoke(DeterministicAgentText.ProgressDraftFinalAnswer(language));
            await EmitDeterministicTextAsync(answer, onDelta, ct).ConfigureAwait(false);
            return (true, answer.Trim(), null);
        }
        catch
        {
            return (false, string.Empty, null);
        }
    }

    internal static string RenderDeterministicInventoryFromData(string kind, JsonElement data, string language)
    {
        return (kind ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "list" => RenderDocumentsListFromReplayData(data, language),
            "tree" => RenderDocumentsTreeFromReplayData(data, language),
            "stats" => BuildStatsFallbackAnswer(data, language),
            "categories" => RenderCategoriesFromReplayData(data, language),
            "count" => DeterministicAgentText.DocumentsCount(TryGetInt(data, "total") ?? 0, language),
            "empty_count" => DeterministicAgentText.EmptyFoldersCount(TryGetInt(data, "total") ?? 0, language),
            "empty_list" => RenderEmptyFoldersListFromReplayData(data, language),
            "summary_status_count" => RenderSummaryStatusCountFromReplayData(data, language),
            "summary_status_list" => RenderSummaryStatusListFromReplayData(data, language),
            "diagnostic_performance" => RenderDiagnosticPerformanceFromReplayData(data, language),
            _ => string.Empty
        };
    }

    private static string RenderCategoriesFromReplayData(JsonElement data, string language)
    {
        if (!data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return DeterministicAgentText.CategoriesCount(TryGetInt(data, "total") ?? 0, language);

        var rows = new List<string>();
        foreach (var entry in items.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var ordinal = TryGetInt(entry, "ordinal") ?? TryGetInt(entry, "displayOrder") ?? 0;
            var name = TryGetString(entry, "name") ?? string.Empty;
            var totalDocuments = TryGetInt(entry, "totalDocuments") ?? 0;
            rows.Add($"{ordinal}. {name} ({totalDocuments})");
        }

        if (rows.Count == 0)
            return DeterministicAgentText.CategoriesCount(TryGetInt(data, "total") ?? 0, language);

        var sb = new StringBuilder();
        sb.AppendLine(DeterministicAgentText.CategoriesListHeader(language));
        foreach (var row in rows)
            sb.AppendLine(row);

        return sb.ToString().TrimEnd();
    }

    private static string RenderDocumentsTreeFromReplayData(JsonElement data, string language)
    {
        var markdown = TryGetString(data, "markdown") ?? string.Empty;
        return string.IsNullOrWhiteSpace(markdown) ? LocalizedStrings.NoDocumentsFound(language) : markdown.Trim();
    }

    private static string RenderDocumentsListFromReplayData(JsonElement data, string language)
    {
        if (!data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return LocalizedStrings.NoDocumentsFound(language);

        var lines = new List<string>();
        var i = 1;
        foreach (var entry in items.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var docPath = (TryGetString(entry, "docPath") ?? string.Empty).Replace('\\', '/').TrimStart('/');
            var docName = TryGetString(entry, "docName") ?? string.Empty;
            var categoryPath = TryGetString(entry, "categoryPath") ?? string.Empty;
            var mainCat = string.IsNullOrWhiteSpace(categoryPath) ? string.Empty : categoryPath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            var label = string.IsNullOrWhiteSpace(mainCat) ? docName : $"{docName} ({mainCat})";
            label = (label ?? string.Empty).Replace("|", " ").Replace("]", ")");

            if (!string.IsNullOrWhiteSpace(docPath) && !string.IsNullOrWhiteSpace(label))
                lines.Add($"{i++}. [[open|{docPath}|1|{label}]]");
        }

        var explicitScopePath = NormalizeCategoryPathArg(TryGetString(data, "scopePath"));
        var searchQuery = (TryGetString(data, "searchQuery") ?? string.Empty).Trim();
        var header = !string.IsNullOrWhiteSpace(explicitScopePath)
            ? DeterministicAgentText.DocumentsListHeader(language, explicitScopePath)
            : (!string.IsNullOrWhiteSpace(searchQuery)
                ? DeterministicAgentText.DocumentsSearchHeader(language, searchQuery)
                : DeterministicAgentText.DocumentsListHeader(language));

        return lines.Count == 0
            ? LocalizedStrings.NoDocumentsFound(language)
            : $"{header}{Environment.NewLine}{string.Join(Environment.NewLine, lines)}".TrimEnd();
    }

    private static string? TryInferDocumentsScopePath(JsonElement data)
    {
        var explicitScope = NormalizeCategoryPathArg(TryGetString(data, "scopePath"));
        if (!string.IsNullOrWhiteSpace(explicitScope))
            return explicitScope;

        if (!data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return null;

        string? unique = null;
        foreach (var entry in items.EnumerateArray())
        {
            var categoryPath = NormalizeCategoryPathArg(TryGetString(entry, "categoryPath"));
            if (string.IsNullOrWhiteSpace(categoryPath))
                return null;
            if (unique is null)
            {
                unique = categoryPath;
                continue;
            }

            if (!string.Equals(unique, categoryPath, StringComparison.OrdinalIgnoreCase))
                return null;
        }

        return unique;
    }

    private static string RenderEmptyFoldersListFromReplayData(JsonElement data, string language)
    {
        if (!data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return DeterministicAgentText.NoEmptyFoldersFound(language);

        var paths = new List<string>();
        foreach (var entry in items.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var path = TryGetString(entry, "path") ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(path))
                paths.Add(path);
        }

        if (paths.Count == 0)
            return DeterministicAgentText.NoEmptyFoldersFound(language);

        var sb = new StringBuilder();
        sb.AppendLine(DeterministicAgentText.EmptyFoldersHeader(language));
        for (var i = 0; i < paths.Count; i++)
            sb.AppendLine($"{i + 1}. {paths[i]}");

        return sb.ToString().TrimEnd();
    }

    private static string RenderSummaryStatusCountFromReplayData(JsonElement data, string language)
    {
        var total = TryGetInt(data, "total") ?? 0;
        var mode = TryGetString(data, "mode") ?? "missing";
        if (string.Equals(mode, "present", StringComparison.OrdinalIgnoreCase))
        {
            return total <= 0
                ? DeterministicAgentText.NoStoredSummaries(language)
                : DeterministicAgentText.StoredSummariesCount(total, language);
        }

        return total <= 0
            ? DeterministicAgentText.NoMissingSummaries(language)
            : DeterministicAgentText.MissingSummariesCount(total, language);
    }

    private static string RenderSummaryStatusListFromReplayData(JsonElement data, string language)
    {
        if (!data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return string.Equals(TryGetString(data, "mode"), "present", StringComparison.OrdinalIgnoreCase)
                ? DeterministicAgentText.NoStoredSummaries(language)
                : DeterministicAgentText.NoMissingSummaries(language);

        var rows = new List<(string path, string state, string label)>();
        foreach (var entry in items.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var path = (TryGetString(entry, "docPath") ?? string.Empty).Replace('\\', '/').TrimStart('/');
            var state = TryGetString(entry, "summaryState") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(path))
                continue;

            var label = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? path;
            label = (label ?? string.Empty).Replace("|", " ").Replace("]", ")");
            rows.Add((path, state, label));
        }

        var mode = TryGetString(data, "mode") ?? "missing";
        if (rows.Count == 0)
            return string.Equals(mode, "present", StringComparison.OrdinalIgnoreCase)
                ? DeterministicAgentText.NoStoredSummaries(language)
                : DeterministicAgentText.NoMissingSummaries(language);

        var sb = new StringBuilder();
        sb.AppendLine(string.Equals(mode, "present", StringComparison.OrdinalIgnoreCase)
            ? DeterministicAgentText.StoredSummariesHeader(language)
            : DeterministicAgentText.MissingSummariesHeader(language));
        for (var i = 0; i < rows.Count; i++)
        {
            var suffix = rows[i].state.Equals("stale", StringComparison.OrdinalIgnoreCase)
                ? " [stale]"
                : string.Empty;
            sb.AppendLine($"{i + 1}. [[open|{rows[i].path}|1|{rows[i].label}]]{suffix}");
        }

        return sb.ToString().TrimEnd();
    }

    // ---------------- Tools exec helpers ----------------

    private async Task<JsonElement> ExecDocumentsListAsync(JsonElement args, CancellationToken ct)
    {
        var (categoryPath, categoryRef) = await ResolveCategoryScopeArgsAsync(args, ct).ConfigureAwait(false);
        var q = args.TryGetProperty("q", out var qj) && qj.ValueKind != JsonValueKind.Null ? qj.GetString() : null;
        var changedSince = ParseChangedSinceArg(GetStringArg(args, "changedSince"));
        var limit = args.TryGetProperty("limit", out var l) ? l.GetInt32() : 80;
        var offset = args.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;

        var res = await _api.DocumentsListAsync(categoryPath, categoryRef, q, changedSince, limit, offset, ct);
        res = ApplySpecificDocumentQueryGuard(res, q);

        _mem.LastListCategoryPath = categoryPath;
        _mem.LastListQuery = q;

        // Sanitize against filesystem + update PDFxx mapping (robust against moves/renames)
        var sanitized = DocumentListHelper.Sanitize(res, _mem);
        if (sanitized.docs.Count == 0)
        {
            _mem.LastFocusedDocument = null;
            _mem.LastRequestedDocumentRef = null;
        }

        return res;
    }

    private static DateTimeOffset? ParseChangedSinceArg(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return DateTimeOffset.TryParse(raw.Trim(), out var parsed)
            ? parsed
            : null;
    }

    private async Task<JsonElement> ExecDocumentsSearchAsync(JsonElement args, CancellationToken ct)
    {
        var q = args.GetProperty("q").GetString() ?? "";
        var (categoryPath, categoryRef) = await ResolveCategoryScopeArgsAsync(args, ct).ConfigureAwait(false);
        var limit = args.TryGetProperty("limit", out var l) ? l.GetInt32() : 80;
        var offset = args.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;

        var res = await _api.DocumentsSearchAsync(q, categoryPath, categoryRef, limit, offset, ct);
        res = ApplySpecificDocumentQueryGuard(res, q);

        _mem.LastListCategoryPath = categoryPath;
        _mem.LastListQuery = q;

        var sanitized = DocumentListHelper.Sanitize(res, _mem);
        if (sanitized.docs.Count == 0)
        {
            _mem.LastFocusedDocument = null;
            _mem.LastRequestedDocumentRef = null;
        }

        return res;
    }

    private async Task<JsonElement> ExecDocumentsGetAsync(JsonElement args, CancellationToken ct)
    {
        return await ExecDocumentsGetResolvedAsync(args, ct);
    }

    private async Task<JsonElement> ExecRagSearchAsync(JsonElement args, CancellationToken ct)
    {
        var query = args.GetProperty("query").GetString() ?? "";
        var topK = args.TryGetProperty("topK", out var k) ? k.GetInt32() : 8;
        var categoryPath = GetStringArg(args, "categoryPath") ?? GetNestedStringArg(args, "filters", "categoryPath") ?? GetStringArg(args, "category") ?? GetNestedStringArg(args, "filters", "category");
        var category = ExtractTopLevelCategoryForRag(categoryPath);
        var mode = args.TryGetProperty("mode", out var m) && m.ValueKind != JsonValueKind.Null ? m.GetString() : "balanced";

        var raw = await _api.RagSearchToolAsync(query, topK, category, mode, ct);
        return NormalizeRagHits(raw);
    }

    private async Task<JsonElement> ExecRagMultiSearchAsync(JsonElement args, CancellationToken ct)
    {
        // args: { queries: string[], topK: int, category: string|null, mode: ... }
        var topK = args.TryGetProperty("topK", out var k) ? k.GetInt32() : 8;
        var categoryPath = GetStringArg(args, "categoryPath") ?? GetNestedStringArg(args, "filters", "categoryPath") ?? GetStringArg(args, "category") ?? GetNestedStringArg(args, "filters", "category");
        var category = ExtractTopLevelCategoryForRag(categoryPath);
        var mode = args.TryGetProperty("mode", out var m) && m.ValueKind != JsonValueKind.Null ? m.GetString() : "balanced";

        var queries = new List<string>();
        if (args.TryGetProperty("queries", out var qArr) && qArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in qArr.EnumerateArray())
            {
                if (q.ValueKind != JsonValueKind.String) continue;
                var s = (q.GetString() ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(s)) queries.Add(s);
            }
        }

        // fallback: single query
        if (queries.Count == 0 && args.TryGetProperty("query", out var q1) && q1.ValueKind == JsonValueKind.String)
        {
            var s = (q1.GetString() ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(s)) queries.Add(s);
        }

        if (queries.Count == 0)
            return JsonDocument.Parse("{\"hits\":[]}").RootElement;

        var merged = new List<JsonElement>();
        foreach (var q in queries.Take(5))
        {
            var raw = await _api.RagSearchToolAsync(q, topK, category, mode, ct);
            var norm = NormalizeRagHits(raw);
            if (norm.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array)
            {
                foreach (var h in hits.EnumerateArray())
                    merged.Add(h);
            }
        }

        // Dedup by docPath + pageStart + pageEnd (diversity-friendly)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uniq = new List<JsonElement>();
        foreach (var h in merged)
        {
            var dp = h.TryGetProperty("docPath", out var dpEl) ? (dpEl.GetString() ?? "") : "";
            var p1 = h.TryGetProperty("pageStart", out var p1El) && p1El.ValueKind == JsonValueKind.Number ? p1El.GetInt32() : 1;
            var p2 = h.TryGetProperty("pageEnd", out var p2El) && p2El.ValueKind == JsonValueKind.Number ? p2El.GetInt32() : p1;
            var key = $"{dp}|{p1}|{p2}";
            if (!seen.Add(key)) continue;
            uniq.Add(h);
        }

        // Sort by score desc when present
        uniq = uniq
            .OrderByDescending(h => h.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetDouble() : 0.0)
            .Take(Math.Max(10, topK * 2))
            .ToList();

        var payload = new
        {
            hits = uniq,
            meta = new { queries = queries.Take(5).ToArray(), mode = (mode ?? "balanced"), category }
        };

        return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;
    }
private JsonElement ExecExportCreate(JsonElement args)
    {
        var format = args.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String ? (f.GetString() ?? "txt") : "txt";
        var fileName = args.TryGetProperty("fileName", out var n) && n.ValueKind == JsonValueKind.String ? (n.GetString() ?? "export") : args.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? (t.GetString() ?? "export") : "export";
        var content = args.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? (c.GetString() ?? "") : "";

        var path = ExportService.Create(format, fileName, content);
        var payload = new { savedPath = path };
        return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;
    }

    private async Task<JsonElement> ExecSupportBundleAsync(JsonElement args, CancellationToken ct)
    {
        if (_settings is null)
            return JsonDocument.Parse("{\"error\":\"missing_settings\"}").RootElement;

        var include = GetStringArrayArg(args, "include");
        var runtimeSnapshot = BuildAgentRuntimeSnapshot();
        var zip = await SupportBundleBuilder.BuildAsync(_settings, runtimeSnapshot, include).ConfigureAwait(false);
        var payload = new
        {
            zipPath = zip,
            included = include is { Count: > 0 } ? include.ToArray() : new[] { "diagnostics/agent-runtime" }
        };
        return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;
    }

    private async Task<JsonElement> ExecRagDebugScrollAsync(JsonElement args, CancellationToken ct)
    {
        var cursor = args.TryGetProperty("cursor", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var limit = args.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 100;
        string? docPath = null;
        if (args.TryGetProperty("docRef", out var dref) && dref.ValueKind == JsonValueKind.String)
        {
            var resolved = await ResolveDocRefAsync(dref.GetString() ?? string.Empty, ct).ConfigureAwait(false);
            docPath = resolved?.DocPath;
        }
        else if (args.TryGetProperty("docPath", out var dp) && dp.ValueKind == JsonValueKind.String)
        {
            docPath = dp.GetString();
        }

        try
        {
            var raw = await _api.RagDebugScrollAsync(cursor, limit, docPath, ct).ConfigureAwait(false);
            return raw;
        }
        catch
        {
            return JsonDocument.Parse("{\"items\":[],\"nextCursor\":null,\"error\":\"not_supported\"}").RootElement;
        }
    }
}
