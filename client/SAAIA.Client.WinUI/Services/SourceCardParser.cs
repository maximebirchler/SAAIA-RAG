using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SAAIA.Client.WinUI.Models;

namespace SAAIA.Client.WinUI.Services;

public static class SourceCardParser
{
    public static List<SourceCard> Parse(string? sourcesJson)
    {
        var result = new List<SourceCard>();
        if (string.IsNullOrWhiteSpace(sourcesJson)) return result;

        try
        {
            using var doc = JsonDocument.Parse(sourcesJson);
            var root = doc.RootElement;

            // ✅ Priorité : notre payload client contient "merged" (voir RagChatAgent.cs)
            var arr =
                TryGetArray(root, "merged") ??
                TryGetArray(root, "items") ??
                TryGetArray(root, "sources") ??
                (root.ValueKind == JsonValueKind.Array ? root : (JsonElement?)null);

            if (arr is not null)
            {
                AddFromArray(result, arr.Value);
            }
            else
            {
                // ✅ Fallback : certains payloads peuvent être { searches: [ { items: [...] }, ... ] }
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("searches", out var searches) &&
                    searches.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in searches.EnumerateArray())
                    {
                        var items = TryGetArray(s, "items");
                        if (items is not null)
                            AddFromArray(result, items.Value);
                    }
                }
            }
        }
        catch
        {
            return new List<SourceCard>();
        }

        // micro-dedup
        return result
            .Where(s => !string.IsNullOrWhiteSpace(s.DocName) || !string.IsNullOrWhiteSpace(s.DocPath))
            .GroupBy(s => $"{s.DocPath}::{s.DocName}::{s.PageStart}::{s.PageEnd}::{s.Snippet}")
            .Select(g => g.First())
            .ToList();
    }

    private static void AddFromArray(List<SourceCard> result, JsonElement arr)
    {
        if (arr.ValueKind != JsonValueKind.Array) return;

        foreach (var el in arr.EnumerateArray())
        {
            var docPath = GetStringAny(el, "doc_path", "docPath", "doc", "path", "file", "source") ?? "";
            var docName = GetStringAny(el, "doc_name", "docName", "title", "name") ?? SafeFileName(docPath);

            var page = GetIntAny(el, "page", "p");
            var pageStart = GetIntAny(el, "page_start", "pageStart", "fromPage", "page_from") ?? page;
            var pageEnd = GetIntAny(el, "page_end", "pageEnd", "toPage", "page_to") ?? page;

            var snippet = GetStringAny(el, "excerpt", "snippet", "text", "chunk", "content") ?? "";
            var score = GetDoubleAny(el, "score", "similarity", "rerankScore");

            result.Add(new SourceCard
            {
                DocPath = docPath,
                DocName = docName,
                PageStart = pageStart,
                PageEnd = pageEnd,
                Snippet = snippet,
                Score = score
            });
        }
    }

    private static JsonElement? TryGetArray(JsonElement root, string name)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(name, out var p) &&
            p.ValueKind == JsonValueKind.Array)
            return p;

        return null;
    }

    private static string? GetStringAny(JsonElement el, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(k, out var p))
            {
                if (p.ValueKind == JsonValueKind.String) return p.GetString();
                if (p.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    return p.ToString();
            }
        }
        return null;
    }

    private static int? GetIntAny(JsonElement el, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(k, out var p))
            {
                if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n)) return n;
                if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out var ns)) return ns;
            }
        }
        return null;
    }

    private static double? GetDoubleAny(JsonElement el, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(k, out var p))
            {
                if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d)) return d;
                if (p.ValueKind == JsonValueKind.String && double.TryParse(p.GetString(), out var ds)) return ds;
            }
        }
        return null;
    }

    private static string SafeFileName(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        path = path.Replace('\\', '/');
        var idx = path.LastIndexOf('/');
        return idx >= 0 ? path[(idx + 1)..] : path;
    }
}
