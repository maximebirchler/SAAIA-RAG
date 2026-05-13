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
        resolvedSourceMetadata ??= BuildLiveSummaryFallbackSourceMetadata(resolved);
        docLanguage = ResolveLiveSummaryDocumentLanguage(docLanguage, resolvedSourceMetadata);

        var maxWords = GetIntArg(args, "maxWords") ?? (level == "short" ? 90 : level == "long" ? 320 : 220);
        var maxChunks = GetIntArg(args, "maxChunks") ?? (strategy == "about" ? 5 : level == "long" ? 14 : 10);
        var maxBatches = GetIntArg(args, "maxBatches") ?? (strategy == "about" ? 1 : level == "long" ? 4 : 3);
        var maxCharsPerBatch = GetIntArg(args, "maxCharsPerBatch") ?? (strategy == "about" ? 2600 : 5200);

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

    private async Task<ToolMemory.SourceRef?> ResolveLiveSummarySourceMetadataAsync(ResolvedDocRef resolved, CancellationToken ct)
    {
        try
        {
            var raw = await _api.SourceResolveAsync(
                string.IsNullOrWhiteSpace(resolved.DocId) ? resolved.DocPath : resolved.DocId,
                null,
                ct).ConfigureAwait(false);
            if (raw.ValueKind == JsonValueKind.Object
                && raw.TryGetProperty("source", out var source)
                && source.ValueKind == JsonValueKind.Object)
            {
                var backendSource = TryBuildSourceRefFromJsonElement(source);
                if (backendSource is not null)
                    return MergeSourceResolveMetadata(backendSource, ResolveLiveSummaryFallbackSourceMetadata(resolved));
            }
        }
        catch
        {
        }

        return null;
    }

    private static ToolMemory.SourceRef BuildLiveSummaryFallbackSourceMetadata(ResolvedDocRef resolved)
        => new()
        {
            DocId = NullIfWhiteSpace(resolved.DocId),
            DocPath = (resolved.DocPath ?? string.Empty).Replace('\\', '/'),
            DocName = NullIfWhiteSpace(resolved.DocName),
            PageStart = 1,
            PageEnd = Math.Max(1, resolved.Pages ?? 1),
            Label = string.IsNullOrWhiteSpace(resolved.DocName) ? resolved.DocPath ?? string.Empty : resolved.DocName,
            CategoryRef = NullIfWhiteSpace(resolved.CategoryRef),
            CategoryPath = NullIfWhiteSpace(resolved.CategoryPath) ?? NullIfWhiteSpace(resolved.Category),
            Category = NullIfWhiteSpace(resolved.Category),
            SourceHash = NullIfWhiteSpace(resolved.SourceHash),
            DocLanguage = NullIfWhiteSpace(resolved.DocLanguage),
            ProfileLanguage = NullIfWhiteSpace(resolved.ProfileLanguage)
        };

    private ToolMemory.SourceRef? ResolveLiveSummaryFallbackSourceMetadata(ResolvedDocRef resolved)
        => ResolveSourceRef(resolved.DocId)
           ?? ResolveSourceRef(resolved.DocPath)
           ?? ResolveSourceRef(resolved.DocName)
           ?? BuildLiveSummaryFallbackSourceMetadata(resolved);

    private static string ResolveLiveSummaryDocumentLanguage(string requestedDocLanguage, ToolMemory.SourceRef? source)
    {
        var sourceDocLanguage = NormalizeDocumentLanguageTag(source?.DocLanguage);
        if (!string.Equals(sourceDocLanguage, "und", StringComparison.Ordinal))
            return sourceDocLanguage;

        var profileLanguage = NormalizeDocumentLanguageTag(source?.ProfileLanguage);
        if (!string.Equals(profileLanguage, "und", StringComparison.Ordinal))
            return profileLanguage;

        var requested = NormalizeDocumentLanguageTag(requestedDocLanguage);
        return string.Equals(requested, "und", StringComparison.Ordinal) ? "und" : requested;
    }

    private static IEnumerable<SummaryChunk> ExtractSummaryChunksFromDebugScroll(JsonElement raw, string fallbackDocPath, string fallbackDocName, ToolMemory.SourceRef? sourceMetadata)
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
            var chunk = new SummaryChunk(chunkText, Math.Max(1, pageStart), Math.Max(pageStart, pageEnd), chunkIndex, docPath, docName);
            chunk = ApplySourceMetadataToSummaryChunk(chunk, sourceMetadata);
            chunk = ApplySourceMetadataToSummaryChunk(chunk, TryBuildSourceRefFromJsonElement(payload));
            yield return chunk;
        }
    }

    private static SummaryChunk BuildSummaryChunkFromRagItem(SAAIA.Contracts.RagItem item, ResolvedDocRef resolved, ToolMemory.SourceRef? fallbackSource)
    {
        var text = (item.Text ?? string.Empty).Trim();
        var pageStart = Math.Max(1, item.PageStart ?? 1);
        var pageEnd = Math.Max(pageStart, item.PageEnd ?? pageStart);
        var qualityStatus = item.ExtractionQuality?.PageQualityStatus
            ?? item.ExtractionQuality?.DocumentQualityStatus
            ?? fallbackSource?.QualityStatus;
        var confidence = item.ExtractionQuality?.PageExtractionConfidence
            ?? item.ExtractionQuality?.DocumentExtractionConfidence
            ?? fallbackSource?.ExtractionConfidence;
        var manualReview = item.ExtractionQuality?.PageManualReviewRecommended
            ?? item.ExtractionQuality?.DocumentManualReviewRecommended
            ?? fallbackSource?.ManualReviewRecommended
            ?? false;
        var signals = item.ExtractionQuality?.Signals?
            .Where(static signal => !string.IsNullOrWhiteSpace(signal))
            .Select(static signal => signal.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        var cards = item.MatchedContentCards?
            .Where(static card => !string.IsNullOrWhiteSpace(card.Title))
            .Select(static card => new ToolMemory.SourceContentCardRef
            {
                Title = card.Title.Trim(),
                ContentCardId = card.ContentCardId,
                PageStart = card.PageStart,
                PageEnd = card.PageEnd,
                Kind = NullIfWhiteSpace(card.Kind),
                Signals = card.Signals?
                    .Where(static signal => !string.IsNullOrWhiteSpace(signal))
                    .Select(static signal => signal.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(8)
                    .ToList() ?? new List<string>(),
                Evidence = card.Evidence
            })
            .Take(5)
            .ToList();

        return new SummaryChunk(
            text,
            pageStart,
            pageEnd,
            item.ChunkIndex ?? int.MaxValue,
            string.IsNullOrWhiteSpace(item.DocPath) ? resolved.DocPath : item.DocPath!,
            string.IsNullOrWhiteSpace(item.DocName) ? resolved.DocName : item.DocName,
            DocId: item.DocId ?? fallbackSource?.DocId,
            SourceHash: item.SourceHash ?? fallbackSource?.SourceHash,
            DocLanguage: item.DocLanguage ?? fallbackSource?.DocLanguage,
            ProfileLanguage: item.ProfileLanguage ?? fallbackSource?.ProfileLanguage,
            Category: item.Category ?? fallbackSource?.Category,
            CategoryRef: item.CategoryRef ?? fallbackSource?.CategoryRef,
            CategoryPath: item.CategoryPath ?? fallbackSource?.CategoryPath,
            ChunkId: item.ChunkId ?? fallbackSource?.ChunkId,
            SectionTitle: item.Context?.SectionTitle ?? fallbackSource?.SectionTitle,
            HeadingPath: item.Context?.HeadingPath ?? fallbackSource?.HeadingPath,
            PrevChunkId: item.Context?.PrevChunkId ?? fallbackSource?.PrevChunkId,
            NextChunkId: item.Context?.NextChunkId ?? fallbackSource?.NextChunkId,
            SameSectionChunkId: item.Context?.SameSectionChunkId ?? fallbackSource?.SameSectionChunkId,
            OriginalChunkType: item.Context?.OriginalChunkType ?? fallbackSource?.OriginalChunkType,
            OffsetStart: item.ProvenanceInfo?.OffsetStart ?? fallbackSource?.OffsetStart,
            OffsetEnd: item.ProvenanceInfo?.OffsetEnd ?? fallbackSource?.OffsetEnd,
            ExtractionSource: item.ExtractionQuality?.ExtractionSource ?? fallbackSource?.ExtractionSource,
            DocumentQualityStatus: item.ExtractionQuality?.DocumentQualityStatus ?? fallbackSource?.DocumentQualityStatus,
            PageQualityStatus: item.ExtractionQuality?.PageQualityStatus ?? fallbackSource?.PageQualityStatus,
            TextStatus: item.ExtractionQuality?.TextStatus ?? fallbackSource?.TextStatus,
            QualityStatus: qualityStatus,
            ExtractionConfidence: confidence,
            DocumentExtractionConfidence: item.ExtractionQuality?.DocumentExtractionConfidence ?? fallbackSource?.DocumentExtractionConfidence,
            PageExtractionConfidence: item.ExtractionQuality?.PageExtractionConfidence ?? fallbackSource?.PageExtractionConfidence,
            ManualReviewRecommended: manualReview,
            DocumentManualReviewRecommended: item.ExtractionQuality?.DocumentManualReviewRecommended ?? fallbackSource?.DocumentManualReviewRecommended ?? false,
            PageManualReviewRecommended: item.ExtractionQuality?.PageManualReviewRecommended ?? fallbackSource?.PageManualReviewRecommended ?? false,
            OcrAttempted: item.ExtractionQuality?.OcrAttempted ?? fallbackSource?.OcrAttempted ?? false,
            OcrApplied: item.ExtractionQuality?.OcrApplied ?? fallbackSource?.OcrApplied ?? false,
            OcrRecommended: item.ExtractionQuality?.OcrRecommended ?? fallbackSource?.OcrRecommended ?? false,
            ExtractionDiagnosticSummary: BuildSourceExtractionDiagnosticRef(item.ExtractionQuality?.DiagnosticSummary)
                ?? CloneSourceExtractionDiagnostic(fallbackSource?.ExtractionDiagnosticSummary),
            QualitySignals: signals is { Count: > 0 } ? signals : fallbackSource?.QualitySignals,
            MatchedContentCards: cards is { Count: > 0 } ? cards : fallbackSource?.MatchedContentCards,
            ProfileSignals: CloneSourceProfileSignalsRef(ConvertProfileSignals(item.ProfileSignals) ?? fallbackSource?.ProfileSignals),
            SelectionHintEvidenceRole: NullIfWhiteSpace(item.SelectionHints?.EvidenceRole) ?? fallbackSource?.SelectionHintEvidenceRole,
            SelectionHintActionabilityScore: item.SelectionHints is not null ? item.SelectionHints.ActionabilityScore : fallbackSource?.SelectionHintActionabilityScore,
            SelectionHintSupportScore: item.SelectionHints is not null ? item.SelectionHints.SupportScore : fallbackSource?.SelectionHintSupportScore,
            SelectionHintFragmentScore: item.SelectionHints is not null ? item.SelectionHints.FragmentScore : fallbackSource?.SelectionHintFragmentScore,
            SelectionHintNavigationScore: item.SelectionHints is not null ? item.SelectionHints.NavigationScore : fallbackSource?.SelectionHintNavigationScore,
            SelectionHintQualityPenalty: item.SelectionHints is not null ? item.SelectionHints.QualityPenalty : fallbackSource?.SelectionHintQualityPenalty,
            ContentRole: NullIfWhiteSpace(item.Context?.ContentRole) ?? fallbackSource?.ContentRole,
            NavigationReason: NullIfWhiteSpace(item.Context?.NavigationReason) ?? fallbackSource?.NavigationReason,
            RetrievalNavigationScore: item.Context?.NavigationScore ?? fallbackSource?.RetrievalNavigationScore,
            ContentDensityScore: item.Context?.ContentDensityScore ?? fallbackSource?.ContentDensityScore);
    }

    private static SummaryChunk ApplySourceMetadataToSummaryChunk(SummaryChunk chunk, ToolMemory.SourceRef? source)
    {
        if (source is null)
            return chunk;

        return chunk with
        {
            DocId = NullIfWhiteSpace(source.DocId) ?? chunk.DocId,
            SourceHash = NullIfWhiteSpace(source.SourceHash) ?? chunk.SourceHash,
            DocLanguage = NullIfWhiteSpace(source.DocLanguage) ?? chunk.DocLanguage,
            ProfileLanguage = NullIfWhiteSpace(source.ProfileLanguage) ?? chunk.ProfileLanguage,
            Category = NullIfWhiteSpace(source.Category) ?? chunk.Category,
            CategoryRef = NullIfWhiteSpace(source.CategoryRef) ?? chunk.CategoryRef,
            CategoryPath = NullIfWhiteSpace(source.CategoryPath) ?? chunk.CategoryPath,
            ChunkId = NullIfWhiteSpace(source.ChunkId) ?? chunk.ChunkId,
            SectionTitle = NullIfWhiteSpace(source.SectionTitle) ?? chunk.SectionTitle,
            HeadingPath = NullIfWhiteSpace(source.HeadingPath) ?? chunk.HeadingPath,
            PrevChunkId = NullIfWhiteSpace(source.PrevChunkId) ?? chunk.PrevChunkId,
            NextChunkId = NullIfWhiteSpace(source.NextChunkId) ?? chunk.NextChunkId,
            SameSectionChunkId = NullIfWhiteSpace(source.SameSectionChunkId) ?? chunk.SameSectionChunkId,
            OriginalChunkType = NullIfWhiteSpace(source.OriginalChunkType) ?? chunk.OriginalChunkType,
            OffsetStart = source.OffsetStart ?? chunk.OffsetStart,
            OffsetEnd = source.OffsetEnd ?? chunk.OffsetEnd,
            ExtractionSource = NullIfWhiteSpace(source.ExtractionSource) ?? chunk.ExtractionSource,
            DocumentQualityStatus = NullIfWhiteSpace(source.DocumentQualityStatus) ?? chunk.DocumentQualityStatus,
            PageQualityStatus = NullIfWhiteSpace(source.PageQualityStatus) ?? chunk.PageQualityStatus,
            TextStatus = NullIfWhiteSpace(source.TextStatus) ?? chunk.TextStatus,
            QualityStatus = NullIfWhiteSpace(source.QualityStatus) ?? chunk.QualityStatus,
            ExtractionConfidence = source.ExtractionConfidence ?? chunk.ExtractionConfidence,
            DocumentExtractionConfidence = source.DocumentExtractionConfidence ?? chunk.DocumentExtractionConfidence,
            PageExtractionConfidence = source.PageExtractionConfidence ?? chunk.PageExtractionConfidence,
            ManualReviewRecommended = source.ManualReviewRecommended || chunk.ManualReviewRecommended,
            DocumentManualReviewRecommended = source.DocumentManualReviewRecommended || chunk.DocumentManualReviewRecommended,
            PageManualReviewRecommended = source.PageManualReviewRecommended || chunk.PageManualReviewRecommended,
            OcrAttempted = source.OcrAttempted || chunk.OcrAttempted,
            OcrApplied = source.OcrApplied || chunk.OcrApplied,
            OcrRecommended = source.OcrRecommended || chunk.OcrRecommended,
            ExtractionDiagnosticSummary = CloneSourceExtractionDiagnostic(source.ExtractionDiagnosticSummary ?? chunk.ExtractionDiagnosticSummary),
            QualitySignals = source.QualitySignals.Count > 0 ? source.QualitySignals : chunk.QualitySignals,
            MatchedContentCards = source.MatchedContentCards.Count > 0 ? source.MatchedContentCards : chunk.MatchedContentCards,
            ProfileSignals = CloneSourceProfileSignalsRef(source.ProfileSignals ?? chunk.ProfileSignals),
            SelectionHintEvidenceRole = NullIfWhiteSpace(source.SelectionHintEvidenceRole) ?? chunk.SelectionHintEvidenceRole,
            SelectionHintActionabilityScore = source.SelectionHintActionabilityScore ?? chunk.SelectionHintActionabilityScore,
            SelectionHintSupportScore = source.SelectionHintSupportScore ?? chunk.SelectionHintSupportScore,
            SelectionHintFragmentScore = source.SelectionHintFragmentScore ?? chunk.SelectionHintFragmentScore,
            SelectionHintNavigationScore = source.SelectionHintNavigationScore ?? chunk.SelectionHintNavigationScore,
            SelectionHintQualityPenalty = source.SelectionHintQualityPenalty ?? chunk.SelectionHintQualityPenalty,
            ContentRole = NullIfWhiteSpace(source.ContentRole) ?? chunk.ContentRole,
            NavigationReason = NullIfWhiteSpace(source.NavigationReason) ?? chunk.NavigationReason,
            RetrievalNavigationScore = source.RetrievalNavigationScore ?? chunk.RetrievalNavigationScore,
            ContentDensityScore = source.ContentDensityScore ?? chunk.ContentDensityScore
        };
    }

    private static ToolMemory.SourceProfileSignalsRef? ConvertProfileSignals(SAAIA.Contracts.RagItemProfileSignals? profile)
    {
        if (profile is null)
            return null;

        var converted = new ToolMemory.SourceProfileSignalsRef
        {
            ProfileVersion = NullIfWhiteSpace(profile.ProfileVersion),
            Language = NullIfWhiteSpace(profile.Language),
            Keywords = NormalizeProfileSignalList(profile.Keywords, 8),
            Entities = NormalizeProfileSignalList(profile.Entities, 8),
            Topics = NormalizeProfileSignalList(profile.Topics, 8),
            HypotheticalQuestions = NormalizeProfileSignalList(profile.HypotheticalQuestions, 4),
            Limits = NormalizeProfileSignalList(profile.Limits, 4),
            MatchedTerms = NormalizeProfileSignalList(profile.MatchedTerms, 12),
            MatchCount = profile.MatchCount
        };

        return ComputeSourceProfileSignalsRichness(converted) == 0 ? null : converted;
    }

    private static List<string> NormalizeProfileSignalList(IEnumerable<string>? values, int maxItems)
        => values?
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maxItems, 1, 24))
            .ToList() ?? new List<string>();

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
        var prioritized = unique
            .Select((chunk, index) => new { chunk, index, score = ScoreSummaryChunkForSelection(chunk) })
            .Where(item => item.score > 0)
            .OrderByDescending(item => item.score)
            .ThenBy(item => item.chunk.PageStart)
            .ThenBy(item => item.chunk.ChunkIndex)
            .Take(Math.Max(1, target / 2))
            .ToArray();

        foreach (var item in prioritized)
        {
            if (selected.Count >= target)
                break;
            if (seen.Add(item.index))
                selected.Add(item.chunk);
        }

        for (var i = 0; i < target; i++)
        {
            var idx = target == 1
                ? 0
                : (int)Math.Round(i * (unique.Count - 1d) / (target - 1d));
            idx = Math.Clamp(idx, 0, unique.Count - 1);
            if (seen.Add(idx))
                selected.Add(unique[idx]);
        }

        if (selected.Count < target)
        {
            foreach (var item in unique.Select((chunk, index) => new { chunk, index }))
            {
                if (selected.Count >= target)
                    break;
                if (seen.Add(item.index))
                    selected.Add(item.chunk);
            }
        }

        return selected
            .OrderBy(x => x.PageStart)
            .ThenBy(x => x.ChunkIndex)
            .ToList();
    }

    private static int ScoreSummaryChunkForSelection(SummaryChunk chunk)
    {
        var score = 0;
        if (chunk.MatchedContentCards is { Count: > 0 })
            score += Math.Min(6, chunk.MatchedContentCards.Count * 2);
        if (!string.IsNullOrWhiteSpace(chunk.SelectionHintEvidenceRole))
            score += chunk.SelectionHintEvidenceRole.Equals("navigation", StringComparison.OrdinalIgnoreCase) ? -2 : 2;
        score += Math.Clamp((chunk.SelectionHintSupportScore ?? 0) / 20, 0, 5);
        score += Math.Clamp((chunk.SelectionHintActionabilityScore ?? 0) / 25, 0, 4);
        score -= Math.Clamp((chunk.SelectionHintQualityPenalty ?? 0) / 2, 0, 4);
        if (chunk.OcrRecommended || chunk.ManualReviewRecommended)
            score -= 2;
        if (chunk.OcrApplied)
            score += 1;
        if (!string.IsNullOrWhiteSpace(chunk.QualityStatus)
            && chunk.QualityStatus.Contains("ok", StringComparison.OrdinalIgnoreCase))
        {
            score += 1;
        }

        return score;
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

    private static string BuildLiveSummaryPrompt(
        ResolvedDocRef doc,
        List<SummaryChunk> chunks,
        string language,
        string level,
        int maxWords,
        string strategy,
        string docLanguage,
        IReadOnlyList<ToolMemory.SourceRef> sourceMetadata)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Document: {doc.DocName}");
        sb.AppendLine($"Path: {doc.DocPath}");
        sb.AppendLine($"DocumentLanguage: {NormalizeDocumentLanguageTag(docLanguage)}");
        sb.AppendLine($"TargetLanguage: {language}");
        sb.AppendLine($"Level: {level}");
        sb.AppendLine($"Strategy: {strategy}");
        sb.AppendLine($"MaxWords: {maxWords}");
        AppendLiveSummarySourceMetadataPrompt(sb, sourceMetadata);
        sb.AppendLine();
        sb.AppendLine("Summarize only this document. Give concrete information from the document itself, not just metadata such as path, category or dates.");

        if (strategy == "about")
        {
            sb.AppendLine("Goal: answer the question 'what is this document about?' in 2 to 4 short sentences maximum.");
            sb.AppendLine("Keep only the main purpose, the main topics and the most useful concrete elements found in the chunks. No long explanation, no bullets, no metadata.");
        }
        else if (strategy == "store")
        {
            sb.AppendLine("Goal: produce a clean reusable summary that can be stored and shown later to standard users.");
            sb.AppendLine("Cover purpose, main sections or topics, important tables, constraints, decisions, examples, values, checks, or steps only when they are present.");
            sb.AppendLine("Prefer 2 to 4 compact paragraphs in the target language.");
        }
        else
        {
            sb.AppendLine("Explain the purpose of the document, the main sections or topics, and the most important concrete facts when they appear in the text: tables, conditions, examples, values, checks, warnings, constraints, decisions, or steps.");
            sb.AppendLine("Write a genuinely useful summary, not a one-line description. Prefer 2 to 4 compact paragraphs in the target language.");
        }

        sb.AppendLine("Do not mention context-window limits. Do not invent content. If some sections are unclear, say so briefly but still summarize what is actually present.");
        sb.AppendLine("Do not output partial URLs, incomplete hostnames, truncated identifiers or half-finished values. If such data appears incomplete in the chunks, omit it instead of guessing.");
        sb.AppendLine();
        sb.AppendLine("CHUNKS:");
        for (var i = 0; i < chunks.Count; i++)
            sb.AppendLine($"[{i + 1}] {chunks[i].Text}");
        return sb.ToString();
    }

    private static List<ToolMemory.SourceRef> BuildLiveSummarySourceRefs(
        IReadOnlyList<SummaryChunk> chunks,
        ToolMemory.SourceRef? fallbackSource,
        string fallbackDocPath,
        string fallbackDocName,
        string? uiLanguage)
        => chunks
            .Select(chunk => BuildSummarySourceRef(chunk, fallbackSource, fallbackDocPath, fallbackDocName, uiLanguage))
            .ToList();

    private static void AppendLiveSummarySourceMetadataPrompt(StringBuilder sb, IReadOnlyList<ToolMemory.SourceRef>? sourceMetadata)
    {
        var sources = sourceMetadata?
            .Where(static source => source is not null)
            .Take(16)
            .ToList() ?? [];
        if (sources.Count == 0)
            return;

        if (sources.Count == 1)
        {
            AppendLiveSummarySourceMetadataPrompt(sb, sources[0]);
            return;
        }

        var lines = sources
            .Select((source, index) => FormatLiveSummarySourceMetadataPrompt(source, index + 1))
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        if (lines.Length == 0)
            return;

        sb.AppendLine("IngestionMetadata:");
        foreach (var line in lines)
            sb.AppendLine(line);
        sb.AppendLine("- diagnosticFields: sourceHash/categoryRef/chunkId help trace retrieval; do not include these identifiers in the summary unless explicitly asked.");
    }

    private static string? FormatLiveSummarySourceMetadataPrompt(ToolMemory.SourceRef sourceMetadata, int index)
    {
        var parts = new List<string>();
        var pageStart = Math.Max(1, sourceMetadata.PageStart);
        var pageEnd = Math.Max(pageStart, sourceMetadata.PageEnd);
        parts.Add(pageStart == pageEnd ? $"page=p.{pageStart}" : $"page=p.{pageStart}-{pageEnd}");
        if (!string.IsNullOrWhiteSpace(sourceMetadata.SourceHash))
            parts.Add($"sourceHash={sourceMetadata.SourceHash}");
        if (!string.IsNullOrWhiteSpace(sourceMetadata.DocLanguage))
            parts.Add($"docLanguage={NormalizeDocumentLanguageTag(sourceMetadata.DocLanguage)}");
        if (!string.IsNullOrWhiteSpace(sourceMetadata.ProfileLanguage))
            parts.Add($"profileLanguage={NormalizeDocumentLanguageTag(sourceMetadata.ProfileLanguage)}");
        if (!string.IsNullOrWhiteSpace(sourceMetadata.CategoryRef))
            parts.Add($"categoryRef={sourceMetadata.CategoryRef}");
        if (!string.IsNullOrWhiteSpace(sourceMetadata.CategoryPath))
            parts.Add($"categoryPath={sourceMetadata.CategoryPath}");
        if (!string.IsNullOrWhiteSpace(sourceMetadata.ChunkId))
            parts.Add($"chunkId={sourceMetadata.ChunkId}");
        if (!string.IsNullOrWhiteSpace(sourceMetadata.ExtractionSource))
            parts.Add($"extractionSource={sourceMetadata.ExtractionSource}");

        var quality = sourceMetadata.QualityStatus
            ?? sourceMetadata.DocumentQualityStatus
            ?? sourceMetadata.PageQualityStatus;
        if (!string.IsNullOrWhiteSpace(quality))
            parts.Add($"extractionQuality={quality}");
        if (sourceMetadata.DocumentExtractionConfidence.HasValue)
            parts.Add($"documentExtractionConfidence={sourceMetadata.DocumentExtractionConfidence.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");
        if (sourceMetadata.PageExtractionConfidence.HasValue)
            parts.Add($"pageExtractionConfidence={sourceMetadata.PageExtractionConfidence.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");

        var qualitySignals = sourceMetadata.QualitySignals?
            .Where(static signal => !string.IsNullOrWhiteSpace(signal))
            .Select(static signal => signal.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray() ?? [];
        if (qualitySignals.Length > 0)
            parts.Add("qualitySignals=" + string.Join(",", qualitySignals));

        var profileSignals = FormatLiveSummaryProfileSignalsPrompt(sourceMetadata.ProfileSignals);
        if (!string.IsNullOrWhiteSpace(profileSignals))
            parts.Add(profileSignals);

        if (sourceMetadata.OcrApplied)
            parts.Add("ocr=applied");
        else if (sourceMetadata.OcrRecommended)
            parts.Add("ocr=recommended");
        else if (sourceMetadata.OcrAttempted)
            parts.Add("ocr=attempted");
        if (sourceMetadata.ManualReviewRecommended)
            parts.Add("manualReview=recommended");
        if (!string.IsNullOrWhiteSpace(sourceMetadata.SelectionHintEvidenceRole))
            parts.Add($"evidenceRole={sourceMetadata.SelectionHintEvidenceRole}");
        if (sourceMetadata.SelectionHintActionabilityScore.HasValue
            || sourceMetadata.SelectionHintSupportScore.HasValue
            || sourceMetadata.SelectionHintFragmentScore.HasValue
            || sourceMetadata.SelectionHintNavigationScore.HasValue
            || sourceMetadata.SelectionHintQualityPenalty.HasValue)
        {
            parts.Add(
                "selectionScores="
                + $"actionability:{sourceMetadata.SelectionHintActionabilityScore?.ToString() ?? "n/a"},"
                + $"support:{sourceMetadata.SelectionHintSupportScore?.ToString() ?? "n/a"},"
                + $"fragment:{sourceMetadata.SelectionHintFragmentScore?.ToString() ?? "n/a"},"
                + $"navigation:{sourceMetadata.SelectionHintNavigationScore?.ToString() ?? "n/a"},"
                + $"qualityPenalty:{sourceMetadata.SelectionHintQualityPenalty?.ToString() ?? "n/a"}");
        }

        var cards = sourceMetadata.MatchedContentCards?
            .Where(static card => !string.IsNullOrWhiteSpace(card.Title))
            .Select(FormatLiveSummaryContentCardPrompt)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray() ?? [];
        if (cards.Length > 0)
            parts.Add("contentCards=" + string.Join(" | ", cards));

        return parts.Count == 0 ? null : $"- source[{index}]: " + string.Join("; ", parts);
    }

    private static void AppendLiveSummarySourceMetadataPrompt(StringBuilder sb, ToolMemory.SourceRef? sourceMetadata)
    {
        if (sourceMetadata is null)
            return;

        var cards = sourceMetadata.MatchedContentCards?
            .Where(static card => !string.IsNullOrWhiteSpace(card.Title))
            .Select(FormatLiveSummaryContentCardPrompt)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray() ?? [];

        var quality = sourceMetadata.QualityStatus
            ?? sourceMetadata.DocumentQualityStatus
            ?? sourceMetadata.PageQualityStatus;

        var qualitySignals = sourceMetadata.QualitySignals?
            .Where(static signal => !string.IsNullOrWhiteSpace(signal))
            .Select(static signal => signal.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray() ?? [];

        if (cards.Length == 0
            && qualitySignals.Length == 0
            && string.IsNullOrWhiteSpace(quality)
            && string.IsNullOrWhiteSpace(sourceMetadata.CategoryPath)
            && string.IsNullOrWhiteSpace(sourceMetadata.CategoryRef)
            && string.IsNullOrWhiteSpace(sourceMetadata.SourceHash)
            && string.IsNullOrWhiteSpace(sourceMetadata.DocLanguage)
            && string.IsNullOrWhiteSpace(sourceMetadata.ProfileLanguage)
            && ComputeSourceProfileSignalsRichness(sourceMetadata.ProfileSignals) == 0)
        {
            return;
        }

        sb.AppendLine("IngestionMetadata:");
        if (!string.IsNullOrWhiteSpace(sourceMetadata.SourceHash))
            sb.AppendLine($"- sourceHash: {sourceMetadata.SourceHash}");
        if (!string.IsNullOrWhiteSpace(sourceMetadata.DocLanguage))
            sb.AppendLine($"- docLanguage: {NormalizeDocumentLanguageTag(sourceMetadata.DocLanguage)}");
        if (!string.IsNullOrWhiteSpace(sourceMetadata.ProfileLanguage))
            sb.AppendLine($"- profileLanguage: {NormalizeDocumentLanguageTag(sourceMetadata.ProfileLanguage)}");
        if (!string.IsNullOrWhiteSpace(sourceMetadata.CategoryRef))
            sb.AppendLine($"- categoryRef: {sourceMetadata.CategoryRef}");
        if (!string.IsNullOrWhiteSpace(sourceMetadata.CategoryPath))
            sb.AppendLine($"- categoryPath: {sourceMetadata.CategoryPath}");
        if (!string.IsNullOrWhiteSpace(sourceMetadata.ChunkId))
            sb.AppendLine($"- chunkId: {sourceMetadata.ChunkId}");
        if (!string.IsNullOrWhiteSpace(sourceMetadata.ExtractionSource))
            sb.AppendLine($"- extractionSource: {sourceMetadata.ExtractionSource}");
        if (!string.IsNullOrWhiteSpace(quality))
            sb.AppendLine($"- extractionQuality: {quality}");
        if (sourceMetadata.ExtractionConfidence.HasValue)
            sb.AppendLine($"- extractionConfidence: {sourceMetadata.ExtractionConfidence.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");
        if (sourceMetadata.DocumentExtractionConfidence.HasValue)
            sb.AppendLine($"- documentExtractionConfidence: {sourceMetadata.DocumentExtractionConfidence.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");
        if (sourceMetadata.PageExtractionConfidence.HasValue)
            sb.AppendLine($"- pageExtractionConfidence: {sourceMetadata.PageExtractionConfidence.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");
        if (qualitySignals.Length > 0)
            sb.AppendLine("- qualitySignals: " + string.Join(" | ", qualitySignals));
        var profileSignals = FormatLiveSummaryProfileSignalsPrompt(sourceMetadata.ProfileSignals);
        if (!string.IsNullOrWhiteSpace(profileSignals))
            sb.AppendLine("- " + profileSignals);
        if (sourceMetadata.OcrApplied)
            sb.AppendLine("- ocr: applied");
        else if (sourceMetadata.OcrRecommended)
            sb.AppendLine("- ocr: recommended");
        else if (sourceMetadata.OcrAttempted)
            sb.AppendLine("- ocr: attempted");
        if (sourceMetadata.ManualReviewRecommended)
            sb.AppendLine("- manualReview: recommended");
        if (!string.IsNullOrWhiteSpace(sourceMetadata.SelectionHintEvidenceRole))
            sb.AppendLine($"- evidenceRole: {sourceMetadata.SelectionHintEvidenceRole}");
        if (sourceMetadata.SelectionHintActionabilityScore.HasValue || sourceMetadata.SelectionHintSupportScore.HasValue)
            sb.AppendLine($"- selectionScores: actionability={sourceMetadata.SelectionHintActionabilityScore?.ToString() ?? "n/a"} support={sourceMetadata.SelectionHintSupportScore?.ToString() ?? "n/a"}");
        if (cards.Length > 0)
            sb.AppendLine("- contentCards: " + string.Join(" | ", cards));
        sb.AppendLine("- diagnosticFields: sourceHash/categoryRef/chunkId help trace retrieval; do not include these identifiers in the summary unless explicitly asked.");
    }

    private static string? FormatLiveSummaryProfileSignalsPrompt(ToolMemory.SourceProfileSignalsRef? profile)
    {
        if (profile is null || ComputeSourceProfileSignalsRichness(profile) == 0)
            return null;

        var groups = new List<string>();
        AddProfileSignalGroup(groups, "profileKeywords", profile.Keywords, 4);
        AddProfileSignalGroup(groups, "profileEntities", profile.Entities, 4);
        AddProfileSignalGroup(groups, "profileTopics", profile.Topics, 4);
        AddProfileSignalGroup(groups, "profileQuestions", profile.HypotheticalQuestions, 2);
        AddProfileSignalGroup(groups, "profileLimits", profile.Limits, 2);
        AddProfileSignalGroup(groups, "profileMatchedTerms", profile.MatchedTerms, 4);

        return groups.Count == 0
            ? null
            : "profileSignals=" + string.Join(" | ", groups);
    }

    private static void AddProfileSignalGroup(
        List<string> groups,
        string label,
        IEnumerable<string>? values,
        int maxItems)
    {
        var compact = values?
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maxItems, 1, 8))
            .ToArray() ?? [];
        if (compact.Length == 0)
            return;

        groups.Add($"{label}:{string.Join(", ", compact)}");
    }

    private static string FormatLiveSummaryContentCardPrompt(ToolMemory.SourceContentCardRef card)
    {
        var parts = new List<string> { card.Title.Trim() };
        if (!string.IsNullOrWhiteSpace(card.ContentCardId))
            parts.Add($"id={card.ContentCardId.Trim()}");
        if (!string.IsNullOrWhiteSpace(card.Kind))
            parts.Add($"kind={card.Kind.Trim()}");
        if (card.PageStart.HasValue)
        {
            var start = Math.Max(1, card.PageStart.Value);
            var end = Math.Max(start, card.PageEnd ?? start);
            parts.Add(start == end ? $"p.{start}" : $"p.{start}-{end}");
        }

        var signals = card.Signals?
            .Where(static signal => !string.IsNullOrWhiteSpace(signal))
            .Select(static signal => signal.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray() ?? [];
        if (signals.Length > 0)
            parts.Add("signals=" + string.Join(",", signals));

        var evidence = FormatLiveSummaryContentCardEvidencePrompt(card.Evidence);
        if (!string.IsNullOrWhiteSpace(evidence))
            parts.Add($"evidence={evidence}");

        return string.Join(" ", parts);
    }

    private static string? FormatLiveSummaryContentCardEvidencePrompt(JsonElement? evidence)
    {
        if (!evidence.HasValue || evidence.Value.ValueKind != JsonValueKind.Object)
            return null;

        var root = evidence.Value;
        var parts = new List<string>();
        var schemaVersion = TryGetString(root, "schemaVersion") ?? TryGetString(root, "schema_version") ?? TryGetString(root, "SchemaVersion");
        if (!string.IsNullOrWhiteSpace(schemaVersion))
            parts.Add($"schema={schemaVersion.Trim()}");

        var scaleBasis = TryGetObject(root, "scaleBasis") ?? TryGetObject(root, "scale_basis") ?? TryGetObject(root, "ScaleBasis");
        if (scaleBasis.HasValue)
        {
            var label = TryGetString(scaleBasis.Value, "label") ?? TryGetString(scaleBasis.Value, "Label");
            var count = TryGetDouble(scaleBasis.Value, "count") ?? TryGetDouble(scaleBasis.Value, "value") ?? TryGetDouble(scaleBasis.Value, "Count") ?? TryGetDouble(scaleBasis.Value, "Value");
            if (!string.IsNullOrWhiteSpace(label) || count.HasValue)
            {
                var basis = string.Join(" ", new[]
                {
                    count?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    label?.Trim()
                }.Where(static value => !string.IsNullOrWhiteSpace(value)));
                if (!string.IsNullOrWhiteSpace(basis))
                    parts.Add($"basis={basis}");
            }
        }

        var quantityFacts = TryGetArray(root, "quantityFacts") ?? TryGetArray(root, "quantity_facts") ?? TryGetArray(root, "QuantityFacts");
        if (quantityFacts.HasValue)
        {
            var facts = new List<string>();
            foreach (var fact in quantityFacts.Value.EnumerateArray())
            {
                if (fact.ValueKind != JsonValueKind.Object)
                    continue;

                var value = TryGetDouble(fact, "value") ?? TryGetDouble(fact, "Value");
                var unit = TryGetString(fact, "unit") ?? TryGetString(fact, "Unit");
                var label = TryGetString(fact, "label") ?? TryGetString(fact, "Label");
                var factText = string.Join(" ", new[]
                {
                    value?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    unit?.Trim(),
                    label?.Trim()
                }.Where(static item => !string.IsNullOrWhiteSpace(item)));
                if (!string.IsNullOrWhiteSpace(factText))
                    facts.Add(factText);
                if (facts.Count >= 4)
                    break;
            }

            if (facts.Count > 0)
                parts.Add("facts=" + string.Join(",", facts));
        }

        var confidence = TryGetDouble(root, "confidence") ?? TryGetDouble(root, "Confidence");
        if (confidence.HasValue)
            parts.Add($"confidence={confidence.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");

        foreach (var property in root.EnumerateObject())
        {
            if (parts.Count >= 8)
                break;

            if (property.NameEquals("schemaVersion")
                || property.NameEquals("schema_version")
                || property.NameEquals("SchemaVersion")
                || property.NameEquals("scaleBasis")
                || property.NameEquals("scale_basis")
                || property.NameEquals("ScaleBasis")
                || property.NameEquals("quantityFacts")
                || property.NameEquals("quantity_facts")
                || property.NameEquals("QuantityFacts")
                || property.NameEquals("confidence")
                || property.NameEquals("Confidence"))
            {
                continue;
            }

            var compactValue = CompactLiveSummaryEvidenceValue(property.Value);
            if (!string.IsNullOrWhiteSpace(compactValue))
                parts.Add($"{property.Name}={compactValue}");
        }

        return parts.Count == 0 ? null : string.Join(";", parts);
    }

    private static string? CompactLiveSummaryEvidenceValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => ShortenLiveSummaryEvidenceValue(value.GetString()),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Array => CompactLiveSummaryEvidenceArray(value),
            JsonValueKind.Object => CompactLiveSummaryEvidenceObject(value),
            _ => null
        };
    }

    private static string? CompactLiveSummaryEvidenceArray(JsonElement value)
    {
        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            var compact = CompactLiveSummaryEvidenceValue(item);
            if (!string.IsNullOrWhiteSpace(compact))
                items.Add(compact);
            if (items.Count >= 3)
                break;
        }

        return items.Count == 0 ? null : "[" + string.Join(",", items) + "]";
    }

    private static string? CompactLiveSummaryEvidenceObject(JsonElement value)
    {
        var items = new List<string>();
        foreach (var property in value.EnumerateObject())
        {
            var compact = CompactLiveSummaryEvidenceValue(property.Value);
            if (!string.IsNullOrWhiteSpace(compact))
                items.Add($"{property.Name}:{compact}");
            if (items.Count >= 3)
                break;
        }

        return items.Count == 0 ? null : "{" + string.Join(",", items) + "}";
    }

    private static string? ShortenLiveSummaryEvidenceValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = Regex.Replace(value.Trim(), @"\s+", " ", RegexOptions.CultureInvariant);
        return trimmed.Length <= 80 ? trimmed : trimmed[..77] + "...";
    }

    private static string BuildSummaryRetrievalQuery(
        ResolvedDocRef doc,
        string strategy,
        string language,
        string level,
        ToolMemory.SourceRef? sourceMetadata = null)
    {
        var primaryLanguage = NormalizeDocumentLanguageTag(language).Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        var core = primaryLanguage switch
        {
            "fr" => strategy switch
            {
                "about" => "vue d ensemble objectif sujets principaux portee contexte",
                "store" => "resume objectif sections principales faits importants exemples valeurs contraintes details utiles",
                _ => "resume objectif sections principales sujets faits importants contraintes"
            },
            "es" => strategy switch
            {
                "about" => "vision general objetivo temas principales alcance contexto",
                "store" => "resumen objetivo secciones principales hechos importantes ejemplos valores restricciones detalles utiles",
                _ => "resumen objetivo secciones principales temas hechos importantes restricciones"
            },
            "pt" => strategy switch
            {
                "about" => "visao geral objetivo temas principais ambito contexto",
                "store" => "resumo objetivo secoes principais fatos importantes exemplos valores restricoes detalhes uteis",
                _ => "resumo objetivo secoes principais temas fatos importantes restricoes"
            },
            "de" => strategy switch
            {
                "about" => "uberblick zweck hauptthemen umfang kontext",
                "store" => "zusammenfassung zweck hauptabschnitte wichtige fakten beispiele werte einschrankungen nutzbare details",
                _ => "zusammenfassung zweck hauptabschnitte themen wichtige fakten einschrankungen"
            },
            "it" => strategy switch
            {
                "about" => "panoramica scopo temi principali ambito contesto",
                "store" => "riassunto scopo sezioni principali fatti importanti esempi valori vincoli dettagli utili",
                _ => "riassunto scopo sezioni principali temi fatti importanti vincoli"
            },
            "en" => strategy switch
            {
                "about" => "overview purpose main topics scope context",
                "store" => "summary purpose main sections important facts examples values constraints useful details",
                _ => "summary purpose main sections topics important facts constraints"
            },
            "nl" => strategy switch
            {
                "about" => "overzicht doel hoofdonderwerpen bereik context",
                "store" => "samenvatting doel hoofdsecties belangrijke feiten voorbeelden waarden beperkingen nuttige details",
                _ => "samenvatting doel hoofdsecties onderwerpen belangrijke feiten beperkingen"
            },
            "pl" => strategy switch
            {
                "about" => "przeglad cel glowne tematy zakres kontekst",
                "store" => "streszczenie cel glowne sekcje wazne fakty przyklady wartosci ograniczenia przydatne szczegoly",
                _ => "streszczenie cel glowne sekcje tematy wazne fakty ograniczenia"
            },
            "sv" => strategy switch
            {
                "about" => "oversikt syfte huvudamnen omfattning kontext",
                "store" => "sammanfattning syfte huvudavsnitt viktiga fakta exempel varden begransningar anvandbara detaljer",
                _ => "sammanfattning syfte huvudavsnitt amnen viktiga fakta begransningar"
            },
            "da" => strategy switch
            {
                "about" => "overblik formal hovedemner omfang kontekst",
                "store" => "resume formal hovedafsnit vigtige fakta eksempler vaerdier begraensninger nyttige detaljer",
                _ => "resume formal hovedafsnit emner vigtige fakta begraensninger"
            },
            "no" or "nb" or "nn" => strategy switch
            {
                "about" => "oversikt formal hovedtema omfang kontekst",
                "store" => "sammendrag formal hovedseksjoner viktige fakta eksempler verdier begrensninger nyttige detaljer",
                _ => "sammendrag formal hovedseksjoner emner viktige fakta begrensninger"
            },
            "fi" => strategy switch
            {
                "about" => "yleiskuva tarkoitus paaaiheet laajuus konteksti",
                "store" => "yhteenveto tarkoitus paaosiot tarkeat faktat esimerkit arvot rajoitukset hyodylliset tiedot",
                _ => "yhteenveto tarkoitus paaosiot aiheet tarkeat faktat rajoitukset"
            },
            "cs" => strategy switch
            {
                "about" => "prehled ucel hlavni temata rozsah kontext",
                "store" => "shrnuti ucel hlavni casti dulezita fakta priklady hodnoty omezeni uzitecne detaily",
                _ => "shrnuti ucel hlavni casti temata dulezita fakta omezeni"
            },
            "sk" => strategy switch
            {
                "about" => "prehlad ucel hlavne temy rozsah kontext",
                "store" => "zhrnutie ucel hlavne casti dolezite fakty priklady hodnoty obmedzenia uzitocne detaily",
                _ => "zhrnutie ucel hlavne casti temy dolezite fakty obmedzenia"
            },
            "sl" => strategy switch
            {
                "about" => "pregled namen glavne teme obseg kontekst",
                "store" => "povzetek namen glavni odseki pomembna dejstva primeri vrednosti omejitve uporabne podrobnosti",
                _ => "povzetek namen glavni odseki teme pomembna dejstva omejitve"
            },
            "hr" => strategy switch
            {
                "about" => "pregled svrha glavne teme opseg kontekst",
                "store" => "sazetak svrha glavni odjeljci vazne cinjenice primjeri vrijednosti ogranicenja korisni detalji",
                _ => "sazetak svrha glavni odjeljci teme vazne cinjenice ogranicenja"
            },
            "ro" => strategy switch
            {
                "about" => "prezentare scop subiecte principale domeniu context",
                "store" => "rezumat scop sectiuni principale fapte importante exemple valori constrangeri detalii utile",
                _ => "rezumat scop sectiuni principale subiecte fapte importante constrangeri"
            },
            "hu" => strategy switch
            {
                "about" => "attekintes cel fo temak terjedelem kontextus",
                "store" => "osszefoglalas cel fo szakaszok fontos tenyek peldak ertekek korlatozasok hasznos reszletek",
                _ => "osszefoglalas cel fo szakaszok temak fontos tenyek korlatozasok"
            },
            "tr" => strategy switch
            {
                "about" => "genel bakis amac ana konular kapsam baglam",
                "store" => "ozet amac ana bolumler onemli olgular ornekler degerler kisitlar faydali ayrintilar",
                _ => "ozet amac ana bolumler konular onemli olgular kisitlar"
            },
            "id" => strategy switch
            {
                "about" => "gambaran umum tujuan topik utama cakupan konteks",
                "store" => "ringkasan tujuan bagian utama fakta penting contoh nilai batasan detail berguna",
                _ => "ringkasan tujuan bagian utama topik fakta penting batasan"
            },
            "vi" => strategy switch
            {
                "about" => "tong quan muc dich chu de chinh pham vi boi canh",
                "store" => "tom tat muc dich phan chinh su kien quan trong vi du gia tri rang buoc chi tiet huu ich",
                _ => "tom tat muc dich phan chinh chu de su kien quan trong rang buoc"
            },
            _ => string.Empty
        };

        var levelCue = BuildSummaryRetrievalLevelCue(primaryLanguage, level);

        var metadataTerms = BuildLiveSummaryRetrievalMetadataTerms(doc, sourceMetadata);
        var fallbackTerms = string.IsNullOrWhiteSpace(core) && string.IsNullOrWhiteSpace(levelCue)
            ? BuildLiveSummaryFallbackRetrievalTerms(doc, sourceMetadata)
            : string.Empty;
        return string.Join(' ', new[] { doc.DocName, metadataTerms, core, levelCue, fallbackTerms }
            .Where(static value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string BuildSummaryRetrievalLevelCue(string primaryLanguage, string level)
        => primaryLanguage switch
        {
            "fr" => level switch
            {
                "short" => "court concis",
                "long" => "detaille complet",
                _ => "moyen utile"
            },
            "es" => level switch
            {
                "short" => "breve conciso",
                "long" => "detallado completo",
                _ => "medio util"
            },
            "pt" => level switch
            {
                "short" => "curto conciso",
                "long" => "detalhado completo",
                _ => "medio util"
            },
            "de" => level switch
            {
                "short" => "kurz knapp",
                "long" => "detailliert vollstaendig",
                _ => "mittel nuetzlich"
            },
            "it" => level switch
            {
                "short" => "breve conciso",
                "long" => "dettagliato completo",
                _ => "medio utile"
            },
            "nl" => level switch
            {
                "short" => "kort bondig",
                "long" => "gedetailleerd volledig",
                _ => "gemiddeld nuttig"
            },
            "pl" => level switch
            {
                "short" => "krotkie zwiezle",
                "long" => "szczegolowe pelne",
                _ => "srednie przydatne"
            },
            "sv" => level switch
            {
                "short" => "kort koncis",
                "long" => "detaljerad fullstandig",
                _ => "medel anvandbar"
            },
            "da" => level switch
            {
                "short" => "kort praecis",
                "long" => "detaljeret fuldstaendig",
                _ => "middel nyttig"
            },
            "no" or "nb" or "nn" => level switch
            {
                "short" => "kort konsis",
                "long" => "detaljert fullstendig",
                _ => "middels nyttig"
            },
            "fi" => level switch
            {
                "short" => "lyhyt tiivis",
                "long" => "yksityiskohtainen kattava",
                _ => "keskitaso hyodyllinen"
            },
            "cs" => level switch
            {
                "short" => "kratke strucne",
                "long" => "podrobne uplne",
                _ => "stredni uzitecne"
            },
            "sk" => level switch
            {
                "short" => "kratke strucne",
                "long" => "podrobne uplne",
                _ => "stredne uzitocne"
            },
            "sl" => level switch
            {
                "short" => "kratko jedrnato",
                "long" => "podrobno popolno",
                _ => "srednje uporabno"
            },
            "hr" => level switch
            {
                "short" => "kratko sazeto",
                "long" => "detaljno potpuno",
                _ => "srednje korisno"
            },
            "ro" => level switch
            {
                "short" => "scurt concis",
                "long" => "detaliat complet",
                _ => "mediu util"
            },
            "hu" => level switch
            {
                "short" => "rovid tomor",
                "long" => "reszletes teljes",
                _ => "kozepes hasznos"
            },
            "tr" => level switch
            {
                "short" => "kisa oz",
                "long" => "ayrintili tam",
                _ => "orta yararli"
            },
            "id" => level switch
            {
                "short" => "pendek ringkas",
                "long" => "rinci lengkap",
                _ => "sedang berguna"
            },
            "vi" => level switch
            {
                "short" => "ngan gon",
                "long" => "chi tiet day du",
                _ => "vua huu ich"
            },
            "en" => level switch
            {
                "short" => "short concise",
                "long" => "detailed complete",
                _ => "medium useful"
            },
            _ => string.Empty
        };

    private static string BuildLiveSummaryRetrievalMetadataTerms(ResolvedDocRef doc, ToolMemory.SourceRef? sourceMetadata)
    {
        var terms = new List<string>();
        if (!string.IsNullOrWhiteSpace(doc.CategoryPath))
            terms.Add(doc.CategoryPath!);
        if (!string.IsNullOrWhiteSpace(doc.CategoryRef))
            terms.Add(doc.CategoryRef!);
        if (!string.IsNullOrWhiteSpace(doc.Category))
            terms.Add(doc.Category!);
        if (!string.IsNullOrWhiteSpace(sourceMetadata?.CategoryRef))
            terms.Add(sourceMetadata!.CategoryRef!);
        if (!string.IsNullOrWhiteSpace(sourceMetadata?.CategoryPath))
            terms.Add(sourceMetadata!.CategoryPath!);

        if (sourceMetadata?.ProfileSignals is { } profileSignals)
        {
            terms.AddRange(profileSignals.Keywords);
            terms.AddRange(profileSignals.Entities);
            terms.AddRange(profileSignals.Topics);
            terms.AddRange(profileSignals.HypotheticalQuestions);
            terms.AddRange(profileSignals.Limits);
            terms.AddRange(profileSignals.MatchedTerms);
        }

        foreach (var card in sourceMetadata?.MatchedContentCards ?? [])
        {
            if (!string.IsNullOrWhiteSpace(card.Title))
                terms.Add(card.Title.Trim());
            terms.AddRange(ExtractLiveSummaryEvidenceRetrievalTerms(card.Evidence));
            foreach (var signal in card.Signals ?? [])
            {
                if (!string.IsNullOrWhiteSpace(signal))
                    terms.Add(signal.Trim());
            }
        }

        return JoinLiveSummaryRetrievalTerms(terms, 48);
    }

    private static string BuildLiveSummaryFallbackRetrievalTerms(ResolvedDocRef doc, ToolMemory.SourceRef? sourceMetadata)
    {
        var terms = new List<string?>
        {
            doc.DocName,
            sourceMetadata?.DocName,
            sourceMetadata?.Label
        };
        terms.AddRange(ExtractLiveSummaryPathRetrievalTerms(doc.DocPath));
        terms.AddRange(ExtractLiveSummaryPathRetrievalTerms(sourceMetadata?.DocPath));
        return JoinLiveSummaryRetrievalTerms(terms, 16);
    }

    private static IEnumerable<string> ExtractLiveSummaryPathRetrievalTerms(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            yield break;

        var normalizedPath = path.Replace('\\', '/');
        var fileName = Path.GetFileNameWithoutExtension(normalizedPath);
        if (!string.IsNullOrWhiteSpace(fileName))
            yield return fileName;
    }

    private static IEnumerable<string> ExtractLiveSummaryEvidenceRetrievalTerms(JsonElement? evidence)
    {
        if (!evidence.HasValue || evidence.Value.ValueKind != JsonValueKind.Object)
            yield break;

        var root = evidence.Value;
        var sourceText = TryGetString(root, "sourceText") ?? TryGetString(root, "source_text") ?? TryGetString(root, "SourceText");
        if (!string.IsNullOrWhiteSpace(sourceText))
            yield return sourceText.Trim();

        var scaleBasis = TryGetObject(root, "scaleBasis") ?? TryGetObject(root, "scale_basis") ?? TryGetObject(root, "ScaleBasis");
        if (scaleBasis.HasValue)
        {
            var count = TryGetDouble(scaleBasis.Value, "count") ?? TryGetDouble(scaleBasis.Value, "value") ?? TryGetDouble(scaleBasis.Value, "Count") ?? TryGetDouble(scaleBasis.Value, "Value");
            var label = TryGetString(scaleBasis.Value, "label") ?? TryGetString(scaleBasis.Value, "Label");
            var basisSourceText = TryGetString(scaleBasis.Value, "sourceText") ?? TryGetString(scaleBasis.Value, "source_text") ?? TryGetString(scaleBasis.Value, "SourceText");
            var basisText = BuildLiveSummaryFactRetrievalTerm(count, null, label, basisSourceText);
            if (!string.IsNullOrWhiteSpace(basisText))
                yield return basisText;
        }

        var quantityFacts = TryGetArray(root, "quantityFacts") ?? TryGetArray(root, "quantity_facts") ?? TryGetArray(root, "QuantityFacts");
        if (quantityFacts.HasValue)
        {
            foreach (var term in ExtractLiveSummaryEvidenceFactArrayTerms(quantityFacts.Value, includeKind: false).Take(8))
                yield return term;
        }

        var facts = TryGetArray(root, "facts") ?? TryGetArray(root, "Facts");
        if (facts.HasValue)
        {
            foreach (var term in ExtractLiveSummaryEvidenceFactArrayTerms(facts.Value, includeKind: true).Take(8))
                yield return term;
        }

        var reasons = TryGetArray(root, "nonScalableReasons") ?? TryGetArray(root, "non_scalable_reasons") ?? TryGetArray(root, "NonScalableReasons");
        if (reasons.HasValue)
        {
            foreach (var reason in reasons.Value.EnumerateArray())
            {
                var compact = CompactLiveSummaryEvidenceValue(reason);
                if (!string.IsNullOrWhiteSpace(compact))
                    yield return compact;
            }
        }
    }

    private static IEnumerable<string> ExtractLiveSummaryEvidenceFactArrayTerms(JsonElement facts, bool includeKind)
    {
        if (facts.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var fact in facts.EnumerateArray())
        {
            if (fact.ValueKind == JsonValueKind.String)
            {
                var text = fact.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    yield return text.Trim();
                continue;
            }

            if (fact.ValueKind != JsonValueKind.Object)
                continue;

            var value = TryGetDouble(fact, "value") ?? TryGetDouble(fact, "Value");
            var valueText = value.HasValue
                ? value.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                : TryGetString(fact, "value") ?? TryGetString(fact, "Value");
            var unit = TryGetString(fact, "unit") ?? TryGetString(fact, "Unit");
            var label = TryGetString(fact, "label") ?? TryGetString(fact, "Label");
            var sourceText = TryGetString(fact, "sourceText") ?? TryGetString(fact, "source_text") ?? TryGetString(fact, "SourceText");
            var kind = includeKind ? TryGetString(fact, "kind") ?? TryGetString(fact, "Kind") : null;
            var term = BuildLiveSummaryFactRetrievalTerm(valueText, unit, label, sourceText, kind);
            if (!string.IsNullOrWhiteSpace(term))
                yield return term;
        }
    }

    private static string? BuildLiveSummaryFactRetrievalTerm(double? value, string? unit, string? label, string? sourceText)
        => BuildLiveSummaryFactRetrievalTerm(
            value?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            unit,
            label,
            sourceText);

    private static string? BuildLiveSummaryFactRetrievalTerm(string? value, string? unit, string? label, string? sourceText, string? kind = null)
        => string.Join(" ", new[]
        {
            kind?.Trim(),
            value?.Trim(),
            unit?.Trim(),
            label?.Trim(),
            sourceText?.Trim()
        }.Where(static item => !string.IsNullOrWhiteSpace(item)));

    private static string JoinLiveSummaryRetrievalTerms(IEnumerable<string?> terms, int maxTerms)
        => string.Join(' ', terms
            .Select(NormalizeLiveSummaryRetrievalTerm)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxTerms));

    private static string? NormalizeLiveSummaryRetrievalTerm(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = Regex.Replace(value.Trim(), @"\s+", " ", RegexOptions.CultureInvariant);
        return normalized.Length <= 120 ? normalized : normalized[..117] + "...";
    }

    private static List<object> BuildSummaryAnchors(
        IReadOnlyList<SAAIA.Contracts.RagItem> items,
        string fallbackDocPath,
        string fallbackDocName,
        string? uiLanguage)
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
                ? Path.GetFileName(docPath)
                : labelBase;

            anchors.Add(new { docPath, docName = label, pageStart, pageEnd, label });
            if (anchors.Count >= 3)
                break;
        }

        return anchors;
    }

    private static List<object> BuildSummaryAnchors(
        IReadOnlyList<SummaryChunk> items,
        string fallbackDocPath,
        string fallbackDocName,
        string? uiLanguage)
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

            anchors.Add(BuildSummaryAnchorPayload(BuildSummarySourceRef(item, null, fallbackDocPath, fallbackDocName, uiLanguage)));
            if (anchors.Count >= 3)
                break;
        }

        return anchors;
    }

    private static ToolMemory.SourceRef BuildSummarySourceRef(
        SummaryChunk? item,
        ToolMemory.SourceRef? fallbackSource,
        string fallbackDocPath,
        string fallbackDocName,
        string? uiLanguage)
    {
        var docPath = item is null || string.IsNullOrWhiteSpace(item.DocPath)
            ? fallbackSource?.DocPath ?? fallbackDocPath
            : item.DocPath;
        var docName = item is null || string.IsNullOrWhiteSpace(item.DocName)
            ? fallbackDocName
            : item.DocName;
        var pageStart = Math.Max(1, item?.PageStart ?? fallbackSource?.PageStart ?? 1);
        var pageEnd = Math.Max(pageStart, item?.PageEnd ?? fallbackSource?.PageEnd ?? pageStart);
        var labelBase = string.IsNullOrWhiteSpace(docName) ? Path.GetFileName(docPath) : docName;
        var label = string.IsNullOrWhiteSpace(labelBase)
            ? docPath
            : labelBase;

        return new ToolMemory.SourceRef
        {
            DocId = item?.DocId ?? fallbackSource?.DocId,
            DocPath = (docPath ?? string.Empty).Replace('\\', '/'),
            DocName = label,
            PageStart = pageStart,
            PageEnd = pageEnd,
            Label = label,
            SourceHash = item?.SourceHash ?? fallbackSource?.SourceHash,
            DocLanguage = item?.DocLanguage ?? fallbackSource?.DocLanguage,
            ProfileLanguage = item?.ProfileLanguage ?? fallbackSource?.ProfileLanguage,
            Category = item?.Category ?? fallbackSource?.Category,
            CategoryRef = item?.CategoryRef ?? fallbackSource?.CategoryRef,
            CategoryPath = item?.CategoryPath ?? fallbackSource?.CategoryPath,
            ChunkId = item?.ChunkId ?? fallbackSource?.ChunkId,
            SectionTitle = item?.SectionTitle ?? fallbackSource?.SectionTitle,
            HeadingPath = item?.HeadingPath ?? fallbackSource?.HeadingPath,
            PrevChunkId = item?.PrevChunkId ?? fallbackSource?.PrevChunkId,
            NextChunkId = item?.NextChunkId ?? fallbackSource?.NextChunkId,
            SameSectionChunkId = item?.SameSectionChunkId ?? fallbackSource?.SameSectionChunkId,
            OriginalChunkType = item?.OriginalChunkType ?? fallbackSource?.OriginalChunkType,
            OffsetStart = item?.OffsetStart ?? fallbackSource?.OffsetStart,
            OffsetEnd = item?.OffsetEnd ?? fallbackSource?.OffsetEnd,
            ExtractionSource = item?.ExtractionSource ?? fallbackSource?.ExtractionSource,
            DocumentQualityStatus = item?.DocumentQualityStatus ?? fallbackSource?.DocumentQualityStatus,
            PageQualityStatus = item?.PageQualityStatus ?? fallbackSource?.PageQualityStatus,
            TextStatus = item?.TextStatus ?? fallbackSource?.TextStatus,
            QualityStatus = item?.QualityStatus ?? fallbackSource?.QualityStatus,
            ExtractionConfidence = item?.ExtractionConfidence ?? fallbackSource?.ExtractionConfidence,
            DocumentExtractionConfidence = item?.DocumentExtractionConfidence ?? fallbackSource?.DocumentExtractionConfidence,
            PageExtractionConfidence = item?.PageExtractionConfidence ?? fallbackSource?.PageExtractionConfidence,
            ManualReviewRecommended = item?.ManualReviewRecommended ?? fallbackSource?.ManualReviewRecommended ?? false,
            DocumentManualReviewRecommended = item?.DocumentManualReviewRecommended ?? fallbackSource?.DocumentManualReviewRecommended ?? false,
            PageManualReviewRecommended = item?.PageManualReviewRecommended ?? fallbackSource?.PageManualReviewRecommended ?? false,
            OcrAttempted = item?.OcrAttempted ?? fallbackSource?.OcrAttempted ?? false,
            OcrApplied = item?.OcrApplied ?? fallbackSource?.OcrApplied ?? false,
            OcrRecommended = item?.OcrRecommended ?? fallbackSource?.OcrRecommended ?? false,
            ExtractionDiagnosticSummary = CloneSourceExtractionDiagnostic(item?.ExtractionDiagnosticSummary ?? fallbackSource?.ExtractionDiagnosticSummary),
            QualitySignals = (item?.QualitySignals ?? fallbackSource?.QualitySignals ?? []).ToList(),
            MatchedContentCards = (item?.MatchedContentCards ?? fallbackSource?.MatchedContentCards ?? []).ToList(),
            ProfileSignals = CloneSourceProfileSignalsRef(item?.ProfileSignals ?? fallbackSource?.ProfileSignals),
            SelectionHintEvidenceRole = item?.SelectionHintEvidenceRole ?? fallbackSource?.SelectionHintEvidenceRole,
            SelectionHintActionabilityScore = item?.SelectionHintActionabilityScore ?? fallbackSource?.SelectionHintActionabilityScore,
            SelectionHintSupportScore = item?.SelectionHintSupportScore ?? fallbackSource?.SelectionHintSupportScore,
            SelectionHintFragmentScore = item?.SelectionHintFragmentScore ?? fallbackSource?.SelectionHintFragmentScore,
            SelectionHintNavigationScore = item?.SelectionHintNavigationScore ?? fallbackSource?.SelectionHintNavigationScore,
            SelectionHintQualityPenalty = item?.SelectionHintQualityPenalty ?? fallbackSource?.SelectionHintQualityPenalty,
            ContentRole = item?.ContentRole ?? fallbackSource?.ContentRole,
            NavigationReason = item?.NavigationReason ?? fallbackSource?.NavigationReason,
            RetrievalNavigationScore = item?.RetrievalNavigationScore ?? fallbackSource?.RetrievalNavigationScore,
            ContentDensityScore = item?.ContentDensityScore ?? fallbackSource?.ContentDensityScore
        };
    }

    private static object BuildSummaryAnchorPayload(ToolMemory.SourceRef source)
        => new
        {
            docId = source.DocId,
            docPath = source.DocPath,
            docName = source.DocName,
            pageStart = source.PageStart,
            pageEnd = source.PageEnd,
            label = source.Label,
            sourceHash = source.SourceHash,
            docLanguage = source.DocLanguage,
            profileLanguage = source.ProfileLanguage,
            category = source.Category,
            categoryRef = source.CategoryRef,
            categoryPath = source.CategoryPath,
            chunkId = source.ChunkId,
            sectionTitle = source.SectionTitle,
            headingPath = source.HeadingPath,
            prevChunkId = source.PrevChunkId,
            nextChunkId = source.NextChunkId,
            sameSectionChunkId = source.SameSectionChunkId,
            originalChunkType = source.OriginalChunkType,
            provenanceInfo = BuildSourceProvenancePayload(source),
            extractionQuality = BuildSourceExtractionQualityPayload(source),
            matchedContentCards = BuildSourceContentCardsPayload(source),
            profileSignals = BuildSourceProfileSignalsPayload(source),
            selectionHints = BuildSourceSelectionHintsPayload(source),
            contentSignals = BuildSourceContentSignalsPayload(source)
        };

}
