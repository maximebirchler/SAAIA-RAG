using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private async Task<JsonElement> ExecRagSummarizeLiveAsync(JsonElement args, CancellationToken ct)
    {
        var docRef = GetPreferredDocRef(args);
        if (docRef is null)
            return JsonDocument.Parse("{\"error\":\"missing_doc_ref\"}").RootElement;

        var resolved = await ResolveDocRefAsync(docRef, ct).ConfigureAwait(false);
        if (resolved is null)
            return JsonDocument.Parse("{\"error\":\"doc_not_found\"}").RootElement;

        var level = (GetStringArg(args, "level") ?? "medium").Trim().ToLowerInvariant();
        var strategy = (GetStringArg(args, "strategy") ?? (level == "short" ? "about" : "summary")).Trim().ToLowerInvariant();
        var language = (GetStringArg(args, "language") ?? _mem.LastLanguage).Trim().ToLowerInvariant();
        if (language == "auto") language = _mem.LastLanguage;

        var maxWords = GetIntArg(args, "maxWords") ?? (level == "short" ? 90 : level == "long" ? 320 : 220);
        var maxChunks = GetIntArg(args, "maxChunks") ?? (strategy == "about" ? 5 : level == "long" ? 14 : 10);
        var maxBatches = GetIntArg(args, "maxBatches") ?? (strategy == "about" ? 1 : level == "long" ? 4 : 3);
        var maxCharsPerBatch = GetIntArg(args, "maxCharsPerBatch") ?? (strategy == "about" ? 2600 : 5200);

        var sourceChunks = new List<SummaryChunk>();
        var anchors = new List<object>();

        try
        {
            var raw = await _api.RagDebugScrollAsync(cursor: null, limit: 200, docPath: resolved.DocPath, ct).ConfigureAwait(false);
            sourceChunks = ExtractSummaryChunksFromDebugScroll(raw, resolved.DocPath, resolved.DocName)
                .OrderBy(x => x.PageStart)
                .ThenBy(x => x.ChunkIndex)
                .ToList();
        }
        catch
        {
        }

        if (sourceChunks.Count == 0)
        {
            try
            {
                var retrievalQuery = BuildSummaryRetrievalQuery(resolved, strategy, language, level);
                var rag = await _api.RagSearchAsync(
                    retrievalQuery,
                    category: null,
                    topK: Math.Max(6, maxChunks * 2),
                    mode: strategy == "about" ? "precise" : "balanced",
                    ct,
                    docId: resolved.DocId,
                    docPath: resolved.DocPath).ConfigureAwait(false);

                var ragItems = (rag.Items ?? new List<SAAIA.Contracts.RagItem>())
                    .Where(x => !string.IsNullOrWhiteSpace(x.Text))
                    .OrderBy(x => x.PageStart ?? int.MaxValue)
                    .ThenBy(x => x.ChunkIndex ?? int.MaxValue)
                    .ToList();

                sourceChunks = ragItems
                    .Select(x => new SummaryChunk(
                        (x.Text ?? string.Empty).Trim(),
                        Math.Max(1, x.PageStart ?? 1),
                        Math.Max(x.PageStart ?? 1, x.PageEnd ?? x.PageStart ?? 1),
                        x.ChunkIndex ?? int.MaxValue,
                        string.IsNullOrWhiteSpace(x.DocPath) ? resolved.DocPath : x.DocPath!,
                        string.IsNullOrWhiteSpace(x.DocName) ? resolved.DocName : x.DocName!))
                    .Where(x => x.Text.Length > 0)
                    .ToList();

                anchors = BuildSummaryAnchors(ragItems, resolved.DocPath, resolved.DocName);
            }
            catch
            {
            }
        }

        if (sourceChunks.Count == 0)
            return JsonDocument.Parse("{\"error\":\"no_chunks_found\"}").RootElement;

        var selectedChunks = SelectRepresentativeSummaryChunks(sourceChunks, strategy, maxChunks);
        var chunkTexts = selectedChunks.Select(x => x.Text).ToList();
        if (chunkTexts.Count == 0)
            return JsonDocument.Parse("{\"error\":\"no_chunks_found\"}").RootElement;

        if (anchors.Count == 0)
            anchors = BuildSummaryAnchors(selectedChunks, resolved.DocPath, resolved.DocName);

        var batches = BuildSummaryBatches(chunkTexts, maxCharsPerBatch, maxBatches);
        var sectionSummaries = new List<string>();

        foreach (var batch in batches)
        {
            var prompt = BuildLiveSummaryPrompt(resolved, batch, language, level, maxWords, strategy);
            var completion = await _llm.CompleteAsync(new[]
            {
                ("system", "You summarize one document only. Return ONLY valid JSON with schema {\"summaryText\":string}. Keep it factual, concrete and useful."),
                ("user", prompt)
            }, forceJson: true, ct).ConfigureAwait(false);

            var summaryText = ExtractSummaryTextFromJson(completion);
            if (!string.IsNullOrWhiteSpace(summaryText) && summaryText.Trim().Length >= 40)
                sectionSummaries.Add(summaryText.Trim());
        }

        string finalSummary;
        if (sectionSummaries.Count == 0)
        {
            finalSummary = string.Join(" ", chunkTexts.Take(strategy == "about" ? 3 : 6)).Trim();
        }
        else if (sectionSummaries.Count == 1)
        {
            finalSummary = sectionSummaries[0];
        }
        else
        {
            var mergePrompt = new StringBuilder();
            mergePrompt.AppendLine($"Document: {resolved.DocName}");
            mergePrompt.AppendLine($"TargetLanguage: {language}");
            mergePrompt.AppendLine($"Level: {level}");
            mergePrompt.AppendLine($"Strategy: {strategy}");
            mergePrompt.AppendLine($"MaxWords: {maxWords}");
            mergePrompt.AppendLine();
            mergePrompt.AppendLine("Merge the partial summaries below into one coherent and useful summary of the document. Keep the most concrete information. Cover purpose, main topics, important procedures/settings/constraints and actionable details when present. Do not repeat yourself and do not focus on file metadata.");
            if (strategy == "about")
                mergePrompt.AppendLine("Return only a very short overview in 2 to 4 short sentences maximum.");
            mergePrompt.AppendLine();
            for (var i = 0; i < sectionSummaries.Count; i++)
                mergePrompt.AppendLine($"[{i + 1}] {sectionSummaries[i]}");

            var completion = await _llm.CompleteAsync(new[]
            {
                ("system", "Return ONLY valid JSON with schema {\"summaryText\":string}."),
                ("user", mergePrompt.ToString())
            }, forceJson: true, ct).ConfigureAwait(false);

            finalSummary = ExtractSummaryTextFromJson(completion);
            if (string.IsNullOrWhiteSpace(finalSummary))
                finalSummary = string.Join(" ", sectionSummaries).Trim();
        }

        if (anchors.Count == 0)
        {
            anchors.Add(new
            {
                docPath = resolved.DocPath,
                pageStart = 1,
                pageEnd = 1,
                label = $"{resolved.DocName} (live)"
            });
        }

        var payload = new
        {
            docId = resolved.DocId,
            docPath = resolved.DocPath,
            docName = resolved.DocName,
            mode = "live",
            level,
            strategy,
            language,
            sampling = BuildSamplingMeta(sourceChunks.Count, selectedChunks.Count),
            summaryText = finalSummary,
            anchors
        };
        return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;
    }

    private static List<List<string>> BuildSummaryBatches(List<string> chunks, int maxCharsPerBatch, int maxBatches)
    {
        var batches = new List<List<string>>();
        var current = new List<string>();
        var currentChars = 0;

        foreach (var chunk in chunks)
        {
            var chunkChars = chunk.Length + 1;
            if (current.Count > 0 && currentChars + chunkChars > maxCharsPerBatch)
            {
                batches.Add(current);
                if (batches.Count >= maxBatches)
                    break;
                current = new List<string>();
                currentChars = 0;
            }

            current.Add(chunk);
            currentChars += chunkChars;
        }

        if (current.Count > 0 && batches.Count < maxBatches)
            batches.Add(current);

        return batches;
    }

    private static string ExtractSummaryTextFromJson(string raw)
    {
        if (TryExtractJsonObject(raw, out var jsonCandidate))
        {
            try
            {
                using var parsed = JsonDocument.Parse(jsonCandidate);
                var root = parsed.RootElement;
                if (root.TryGetProperty("summaryText", out var st) && st.ValueKind == JsonValueKind.String)
                    return (st.GetString() ?? string.Empty).Trim();
            }
            catch
            {
            }
        }

        return string.Empty;
    }

    private Task<JsonElement> ExecAdminQdrantHealthAsync(CancellationToken ct)
        => _api.AdminQdrantHealthAsync(ct);

    private static IEnumerable<SummaryChunk> ExtractSummaryChunksFromDebugScroll(JsonElement raw, string fallbackDocPath, string fallbackDocName)
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
            if (chunkText.Length == 0)
                continue;

            var docPath = TryGetString(payload, "doc_path") ?? fallbackDocPath;
            var docName = TryGetString(payload, "doc_name") ?? fallbackDocName;
            var pageStart = TryGetInt(payload, "page_start") ?? 1;
            var pageEnd = TryGetInt(payload, "page_end") ?? pageStart;
            var chunkIndex = TryGetInt(payload, "chunk_index") ?? int.MaxValue;
            yield return new SummaryChunk(chunkText, Math.Max(1, pageStart), Math.Max(pageStart, pageEnd), chunkIndex, docPath, docName);
        }
    }

    private static List<SummaryChunk> SelectRepresentativeSummaryChunks(IReadOnlyList<SummaryChunk> orderedChunks, string strategy, int maxChunks)
    {
        if (orderedChunks.Count == 0 || maxChunks <= 0)
            return new List<SummaryChunk>();

        var unique = orderedChunks
            .Where(x => !string.IsNullOrWhiteSpace(x.Text))
            .GroupBy(x => x.Text, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(x => x.PageStart)
            .ThenBy(x => x.ChunkIndex)
            .ToList();

        if (unique.Count <= maxChunks)
            return unique;

        var divisor = unique.Count <= 15 ? 3 : unique.Count <= 32 ? 4 : 5;
        var desired = (int)Math.Ceiling(unique.Count / (double)divisor);
        var minTarget = strategy == "about" ? 3 : Math.Min(6, maxChunks);
        var target = Math.Clamp(desired, Math.Min(minTarget, unique.Count), maxChunks);
        target = Math.Min(target, unique.Count);

        var selected = new List<SummaryChunk>();
        var seen = new HashSet<int>();
        for (var i = 0; i < target; i++)
        {
            var idx = target == 1
                ? 0
                : (int)Math.Round(i * (unique.Count - 1d) / (target - 1d));
            idx = Math.Clamp(idx, 0, unique.Count - 1);
            if (seen.Add(idx))
                selected.Add(unique[idx]);
        }

        return selected
            .OrderBy(x => x.PageStart)
            .ThenBy(x => x.ChunkIndex)
            .ToList();
    }

    private static object BuildSamplingMeta(int totalChunks, int selectedChunks)
    {
        var divisor = totalChunks <= 15 ? 3 : totalChunks <= 32 ? 4 : 5;
        return new
        {
            totalChunks,
            selectedChunks,
            divisor
        };
    }

    private static string BuildLiveSummaryPrompt(ResolvedDocRef doc, List<string> chunks, string language, string level, int maxWords, string strategy)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Document: {doc.DocName}");
        sb.AppendLine($"Path: {doc.DocPath}");
        sb.AppendLine($"TargetLanguage: {language}");
        sb.AppendLine($"Level: {level}");
        sb.AppendLine($"Strategy: {strategy}");
        sb.AppendLine($"MaxWords: {maxWords}");
        sb.AppendLine();
        sb.AppendLine("Summarize only this document. Give concrete information from the document itself, not just metadata such as path, category or dates.");

        if (strategy == "about")
        {
            sb.AppendLine("Goal: answer the question 'what is this document about?' in 2 to 4 short sentences maximum.");
            sb.AppendLine("Keep only the main purpose, the main topics and the most useful concrete elements. No long explanation, no bullets, no metadata.");
        }
        else if (strategy == "store")
        {
            sb.AppendLine("Goal: produce a clean reusable summary that can be stored and shown later to standard users.");
            sb.AppendLine("Cover purpose, main sections or topics, important procedures/settings/constraints and actionable details when they are present.");
            sb.AppendLine("Prefer 2 to 4 compact paragraphs in the target language.");
        }
        else
        {
            sb.AppendLine("Explain the purpose of the document, the main sections or topics, the most important procedures, settings, parameters, warnings, constraints and actionable details when they appear in the text.");
            sb.AppendLine("Write a genuinely useful summary, not a one-line description. Prefer 2 to 4 compact paragraphs in the target language.");
        }

        sb.AppendLine("Do not mention context-window limits. Do not invent content. If some sections are unclear, say so briefly but still summarize what is actually present.");
        sb.AppendLine("Do not output partial URLs, incomplete hostnames, truncated identifiers or half-finished values. If such data appears incomplete in the chunks, omit it instead of guessing.");
        sb.AppendLine();
        sb.AppendLine("CHUNKS:");
        for (var i = 0; i < chunks.Count; i++)
            sb.AppendLine($"[{i + 1}] {chunks[i]}");
        return sb.ToString();
    }

    private static string BuildSummaryRetrievalQuery(ResolvedDocRef doc, string strategy, string language, string level)
    {
        _ = language;
        _ = level;

        return strategy switch
        {
            "about" => $"{doc.DocName} overview purpose main topics scope",
            "store" => $"{doc.DocName} summary purpose main sections procedures settings warnings constraints actionable details",
            _ => $"{doc.DocName} summary purpose main sections procedures settings warnings constraints"
        };
    }

    private static List<object> BuildSummaryAnchors(IReadOnlyList<SAAIA.Contracts.RagItem> items, string fallbackDocPath, string fallbackDocName)
    {
        var anchors = new List<object>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            var docPath = (item.DocPath ?? fallbackDocPath ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(docPath))
                continue;

            var pageStart = Math.Max(1, item.PageStart ?? 1);
            var pageEnd = Math.Max(pageStart, item.PageEnd ?? pageStart);
            var key = $"{docPath}|{pageStart}|{pageEnd}";
            if (!seen.Add(key))
                continue;

            var labelBase = string.IsNullOrWhiteSpace(item.DocName) ? fallbackDocName : item.DocName;
            var label = string.IsNullOrWhiteSpace(labelBase)
                ? $"{Path.GetFileName(docPath)} (p.{pageStart})"
                : $"{labelBase} (p.{pageStart})";

            anchors.Add(new { docPath, pageStart, pageEnd, label });
            if (anchors.Count >= 3)
                break;
        }

        return anchors;
    }

    private static List<object> BuildSummaryAnchors(IReadOnlyList<SummaryChunk> items, string fallbackDocPath, string fallbackDocName)
    {
        var anchors = new List<object>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            var docPath = string.IsNullOrWhiteSpace(item.DocPath) ? fallbackDocPath : item.DocPath;
            if (string.IsNullOrWhiteSpace(docPath))
                continue;

            var pageStart = Math.Max(1, item.PageStart);
            var pageEnd = Math.Max(pageStart, item.PageEnd);
            var key = $"{docPath}|{pageStart}|{pageEnd}";
            if (!seen.Add(key))
                continue;

            var labelBase = string.IsNullOrWhiteSpace(item.DocName) ? fallbackDocName : item.DocName;
            var label = string.IsNullOrWhiteSpace(labelBase)
                ? $"{Path.GetFileName(docPath)} (p.{pageStart})"
                : $"{labelBase} (p.{pageStart})";

            anchors.Add(new { docPath, pageStart, pageEnd, label });
            if (anchors.Count >= 3)
                break;
        }

        return anchors;
    }

}
