using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private bool _ragDebugScrollAccessDenied;

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
        var responseLanguage = (GetStringArg(args, "responseLanguage") ?? GetStringArg(args, "language") ?? _mem.LastLanguage)
            .Trim()
            .ToLowerInvariant();
        if (responseLanguage == "auto")
            responseLanguage = _mem.LastLanguage;
        responseLanguage = NormalizeLanguageCode(responseLanguage);
        var docLanguage = NormalizeDocumentLanguageTag(GetStringArg(args, "docLanguage"));
        var resolvedSourceMetadata = await ResolveLiveSummarySourceMetadataAsync(resolved, ct).ConfigureAwait(false);
        resolvedSourceMetadata ??= ResolveLiveSummaryFallbackSourceMetadata(resolved);
        docLanguage = ResolveLiveSummaryDocumentLanguage(docLanguage, resolvedSourceMetadata);

        var maxWords = GetIntArg(args, "maxWords") ?? (level == "short" ? 90 : level == "long" ? 320 : 220);
        var maxChunks = GetIntArg(args, "maxChunks") ?? (strategy == "about" ? 5 : level == "long" ? 14 : 10);
        var maxBatches = GetIntArg(args, "maxBatches") ?? (strategy == "about" ? 1 : level == "long" ? 4 : 3);
        var maxCharsPerBatch = GetIntArg(args, "maxCharsPerBatch") ?? (strategy == "about" ? 2600 : 5200);

        if (string.Equals(
                strategy,
                "evidence_overview",
                StringComparison.Ordinal))
        {
            return await ExecRagEvidenceOverviewSummaryAsync(
                    resolved,
                    resolvedSourceMetadata,
                    args,
                    responseLanguage,
                    docLanguage,
                    ct)
                .ConfigureAwait(false);
        }

        var sourceChunks = new List<SummaryChunk>();
        var anchors = new List<object>();

        if (ShouldTryLiveSummaryDebugScroll())
        {
            try
            {
                var raw = await _api.RagDebugScrollAsync(cursor: null, limit: 200, docPath: resolved.DocPath, ct).ConfigureAwait(false);
                sourceChunks = ExtractSummaryChunksFromDebugScroll(raw, resolved.DocPath, resolved.DocName, resolvedSourceMetadata)
                    .OrderBy(x => x.PageStart)
                    .ThenBy(x => x.ChunkIndex)
                    .ToList();
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _ragDebugScrollAccessDenied = true;
            }
            catch
            {
            }
        }

        if (sourceChunks.Count == 0)
        {
            try
            {
                var retrievalQuery = BuildSummaryRetrievalQuery(resolved, strategy, docLanguage, level, resolvedSourceMetadata);
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
                    .Select(x => BuildSummaryChunkFromRagItem(x, resolved, resolvedSourceMetadata))
                    .Where(x => x.Text.Length > 0)
                    .ToList();
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
            anchors = BuildSummaryAnchors(selectedChunks, resolved.DocPath, resolved.DocName, responseLanguage);

        var selectedSources = BuildLiveSummarySourceRefs(
            selectedChunks,
            resolvedSourceMetadata,
            resolved.DocPath,
            resolved.DocName,
            responseLanguage);
        var primarySource = selectedSources.FirstOrDefault()
            ?? BuildSummarySourceRef(
                selectedChunks.FirstOrDefault(),
                resolvedSourceMetadata,
                resolved.DocPath,
                resolved.DocName,
                responseLanguage);
        docLanguage = ResolveLiveSummaryDocumentLanguage(docLanguage, primarySource);

        var batches = BuildSummaryBatches(selectedChunks, maxCharsPerBatch, maxBatches);
        var sectionSummaries = new List<string>();

        foreach (var batch in batches)
        {
            var batchSources = BuildLiveSummarySourceRefs(
                batch,
                resolvedSourceMetadata,
                resolved.DocPath,
                resolved.DocName,
                responseLanguage);
            var prompt = BuildLiveSummaryPrompt(resolved, batch, responseLanguage, level, maxWords, strategy, docLanguage, batchSources);
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
            mergePrompt.AppendLine($"TargetLanguage: {responseLanguage}");
            mergePrompt.AppendLine($"Level: {level}");
            mergePrompt.AppendLine($"Strategy: {strategy}");
            mergePrompt.AppendLine($"MaxWords: {maxWords}");
            mergePrompt.AppendLine($"DocumentLanguage: {NormalizeDocumentLanguageTag(docLanguage)}");
            AppendLiveSummarySourceMetadataPrompt(mergePrompt, selectedSources);
            mergePrompt.AppendLine();
            mergePrompt.AppendLine("Merge the partial summaries below into one coherent and useful summary of the document. Keep the most concrete information. Cover the purpose, main topics, important sections, tables, constraints, decisions, examples, values, checks, or steps only when they are actually present. Do not repeat yourself and do not focus on file metadata.");
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
            anchors.Add(BuildSummaryAnchorPayload(BuildSummarySourceRef(
                new SummaryChunk(string.Empty, 1, 1, int.MaxValue, resolved.DocPath, resolved.DocName),
                resolvedSourceMetadata,
                resolved.DocPath,
                resolved.DocName,
                responseLanguage)));
        }

        var selectedSourcePayloads = selectedSources.Select(BuildSummaryAnchorPayload).ToList();
        var payload = new
        {
            docId = resolved.DocId,
            docPath = resolved.DocPath,
            docName = resolved.DocName,
            mode = "live",
            level,
            strategy,
            language = responseLanguage,
            responseLanguage,
            docLanguage,
            profileLanguage = primarySource.ProfileLanguage,
            sourceHash = primarySource.SourceHash,
            category = primarySource.Category,
            categoryRef = primarySource.CategoryRef,
            categoryPath = primarySource.CategoryPath,
            extractionQuality = BuildSourceExtractionQualityPayload(primarySource),
            matchedContentCards = BuildSourceContentCardsPayload(primarySource),
            profileSignals = BuildSourceProfileSignalsPayload(primarySource),
            selectionHints = BuildSourceSelectionHintsPayload(primarySource),
            contentSignals = BuildSourceContentSignalsPayload(primarySource),
            sourceMetadata = selectedSourcePayloads,
            sourceMetadataTotal = selectedSourcePayloads.Count,
            sourceMetadataTruncated = false,
            sourceMetadataSample = selectedSourcePayloads,
            sampling = BuildSamplingMeta(sourceChunks.Count, selectedChunks.Count),
            summaryText = finalSummary,
            anchors
        };
        return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;
    }

    private bool ShouldTryLiveSummaryDebugScroll()
    {
        if (_ragDebugScrollAccessDenied)
            return false;

        if (!_api.HasAdminKey)
            return false;

        if (_mem.CapabilitiesCache is { IsAdmin: false })
            return false;

        return true;
    }

    private static List<List<SummaryChunk>> BuildSummaryBatches(IReadOnlyList<SummaryChunk> chunks, int maxCharsPerBatch, int maxBatches)
    {
        var batches = new List<List<SummaryChunk>>();
        var current = new List<SummaryChunk>();
        var currentChars = 0;

        foreach (var chunk in chunks)
        {
            var chunkChars = chunk.Text.Length + 1;
            if (current.Count > 0 && currentChars + chunkChars > maxCharsPerBatch)
            {
                batches.Add(current);
                if (batches.Count >= maxBatches)
                    break;
                current = new List<SummaryChunk>();
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
}
