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
    private async Task<string> RenderSummaryForDisplayAsync(string summaryText, string language, string mode, Action<string>? onDelta, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(summaryText))
            return string.Empty;

        if (onDelta is null)
            return summaryText.Trim();

        var system = $@"
You are SAAIA assistant.
Rewrite the provided summary faithfully.
Language: {language}
Mode: {mode}
Rules:
- Keep all concrete facts already present.
- Do not invent any additional information.
- Do not mention internal processing.
- Never output a partial URL, partial file path or visibly truncated token such as ""www"" when the source value is incomplete.
- If a value is incomplete in the source summary, omit it instead of guessing or truncating it.
- If mode=about: keep 2 to 4 short sentences maximum.
- If mode=summary: keep 2 to 4 compact paragraphs maximum.
- Return plain text only.
";

        var user = $@"SOURCE_SUMMARY:
{summaryText}";
        var streamed = new StringBuilder();

        try
        {
            await _llm.StreamAsync(new[]
            {
                ("system", system),
                ("user", user)
            }, forceJson: false, delta =>
            {
                if (string.IsNullOrEmpty(delta))
                    return;
                streamed.Append(delta);
                onDelta(delta);
            }, ct).ConfigureAwait(false);

            var rendered = streamed.ToString().Replace("**", string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(rendered))
                return rendered;
        }
        catch
        {
            if (streamed.Length == 0)
            {
                await EmitDeterministicTextAsync(summaryText.Trim(), onDelta, ct).ConfigureAwait(false);
                return summaryText.Trim();
            }
        }

        if (streamed.Length == 0)
        {
            await EmitDeterministicTextAsync(summaryText.Trim(), onDelta, ct).ConfigureAwait(false);
            return summaryText.Trim();
        }

        return streamed.ToString().Replace("**", string.Empty).Trim();
    }

    private string BuildQuestionsListAnswer(ToolResults toolResults)
    {
        try
        {
            var item = toolResults.Items.LastOrDefault(x => x.ToolName == "meta.list_questions");
            if (item is null) return string.Empty;

            if (item.Result.ValueKind != JsonValueKind.Object || !item.Result.TryGetProperty("questions", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return string.Empty;

            var i = 1;
            var sb = new StringBuilder();
            foreach (var q in arr.EnumerateArray())
            {
                if (q.ValueKind != JsonValueKind.String) continue;
                var s = (q.GetString() ?? "").Trim();
                if (s.Length == 0) continue;
                sb.AppendLine($"{i}. {s}");
                i++;
            }

            return sb.ToString().TrimEnd();
        }
        catch
        {
            return string.Empty;
        }
    }

    private (string answer, object? sourcesPayload, string? docLanguage, string? sourceHash, string? updatedAt) TryBuildSummaryAnswer(ToolResults toolResults)
    {
        try
        {
            var item = toolResults.Items.LastOrDefault(x => x.ToolName is "summary.get" or "rag.summarize_live");
            if (item is null || item.Result.ValueKind != JsonValueKind.Object)
                return (string.Empty, null, null, null, null);

            if (!item.Result.TryGetProperty("summaryText", out var st) || st.ValueKind != JsonValueKind.String)
                return (string.Empty, null, null, null, null);

            var answer = (st.GetString() ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(answer))
                return (string.Empty, null, null, null, null);

            var rootDirectSource = TryBuildSourceRefFromJsonElement(item.Result);
            var sourceElement = TryGetObject(item.Result, "source") ?? TryGetObject(item.Result, "Source");
            var nestedSource = sourceElement.HasValue
                ? TryBuildSourceRefFromJsonElement(sourceElement.Value)
                : null;
            var rootSource = MergeSummarySourceRefs(rootDirectSource, nestedSource);

            var anchors = new List<object>();
            var summaryMemorySources = new List<ToolMemory.SourceRef>();
            if (item.Result.TryGetProperty("anchors", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in arr.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object) continue;
                    var anchorSource = MergeSummarySourceRefs(
                        rootSource,
                        TryBuildSourceRefFromJsonElement(a));
                    var docPath = a.TryGetProperty("docPath", out var dp) && dp.ValueKind == JsonValueKind.String ? (dp.GetString() ?? string.Empty) : anchorSource?.DocPath ?? string.Empty;
                    var pageStart = a.TryGetProperty("pageStart", out var ps) && ps.ValueKind == JsonValueKind.Number ? ps.GetInt32() : anchorSource?.PageStart ?? 1;
                    var pageEnd = a.TryGetProperty("pageEnd", out var pe) && pe.ValueKind == JsonValueKind.Number ? pe.GetInt32() : anchorSource?.PageEnd ?? pageStart;
                    var label = a.TryGetProperty("label", out var lb) && lb.ValueKind == JsonValueKind.String ? (lb.GetString() ?? string.Empty) : anchorSource?.Label ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(docPath))
                    {
                        var anchor = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["evidenceId"] = anchorSource?.EvidenceId ?? TryGetString(a, "evidenceId") ?? TryGetString(a, "EvidenceId"),
                            ["docId"] = anchorSource?.DocId ?? TryGetString(a, "docId") ?? TryGetString(a, "DocId") ?? rootSource?.DocId,
                            ["docPath"] = docPath,
                            ["pageStart"] = pageStart,
                            ["pageEnd"] = pageEnd,
                            ["label"] = label,
                            ["sourceHash"] = anchorSource?.SourceHash ?? TryGetString(a, "sourceHash") ?? TryGetString(a, "SourceHash") ?? rootSource?.SourceHash,
                            ["revisionId"] = anchorSource?.RevisionId ?? TryGetString(a, "revisionId") ?? TryGetString(a, "RevisionId") ?? rootSource?.RevisionId,
                            ["docLanguage"] = anchorSource?.DocLanguage ?? TryGetDocumentLanguage(a) ?? rootSource?.DocLanguage,
                            ["profileLanguage"] = anchorSource?.ProfileLanguage ?? TryGetString(a, "profileLanguage") ?? TryGetString(a, "ProfileLanguage") ?? rootSource?.ProfileLanguage,
                            ["category"] = anchorSource?.Category ?? TryGetString(a, "category") ?? TryGetString(a, "Category") ?? rootSource?.Category,
                            ["categoryRef"] = anchorSource?.CategoryRef ?? TryGetString(a, "categoryRef") ?? TryGetString(a, "CategoryRef") ?? rootSource?.CategoryRef,
                            ["categoryPath"] = anchorSource?.CategoryPath ?? TryGetString(a, "categoryPath") ?? TryGetString(a, "CategoryPath") ?? rootSource?.CategoryPath,
                            ["chunkId"] = anchorSource?.ChunkId ?? TryGetString(a, "chunkId") ?? TryGetString(a, "ChunkId") ?? rootSource?.ChunkId,
                            ["anchorId"] = anchorSource?.AnchorId ?? TryGetString(a, "anchorId") ?? TryGetString(a, "AnchorId") ?? rootSource?.AnchorId,
                            ["extractionQuality"] = CompactExtractionQualityForPrompt(a) ?? (anchorSource is null ? null : BuildSourceExtractionQualityPayload(anchorSource)) ?? (rootSource is null ? null : BuildSourceExtractionQualityPayload(rootSource)),
                            ["matchedContentCards"] = CompactMatchedContentCardsForPrompt(a) ?? (anchorSource is null ? null : BuildSourceContentCardsPayload(anchorSource)) ?? (rootSource is null ? null : BuildSourceContentCardsPayload(rootSource)),
                            ["profileSignals"] = CompactProfileSignalsForPrompt(a) ?? (anchorSource is null ? null : BuildSourceProfileSignalsPayload(anchorSource)) ?? (rootSource is null ? null : BuildSourceProfileSignalsPayload(rootSource)),
                            ["selectionHints"] = CompactSelectionHintsForPrompt(a) ?? (anchorSource is null ? null : BuildSourceSelectionHintsPayload(anchorSource)) ?? (rootSource is null ? null : BuildSourceSelectionHintsPayload(rootSource)),
                            ["contentSignals"] = CompactRetrievalContentSignalsForPrompt(a) ?? (anchorSource is null ? null : BuildSourceContentSignalsPayload(anchorSource)) ?? (rootSource is null ? null : BuildSourceContentSignalsPayload(rootSource))
                        };
                        anchors.Add(anchor.Where(static pair => pair.Value is not null).ToDictionary(static pair => pair.Key, static pair => pair.Value!, StringComparer.Ordinal));
                        if (anchorSource is not null)
                            summaryMemorySources.Add(anchorSource);
                    }
                }
            }

            if (anchors.Count == 0 && rootSource is not null)
            {
                anchors.Add(BuildSummaryAnchorPayload(rootSource));
                summaryMemorySources.Add(rootSource);
            }
            else if (anchors.Count == 0
                && item.Result.TryGetProperty("docPath", out var dp2) && dp2.ValueKind == JsonValueKind.String)
            {
                var docPath = (dp2.GetString() ?? string.Empty).Trim();
                var label = item.Result.TryGetProperty("docName", out var dn) && dn.ValueKind == JsonValueKind.String
                    ? (dn.GetString() ?? string.Empty)
                    : Path.GetFileName(docPath);
                if (!string.IsNullOrWhiteSpace(docPath))
                {
                    var anchor = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["docId"] = TryGetString(item.Result, "docId") ?? TryGetString(item.Result, "DocId"),
                        ["docPath"] = docPath,
                        ["pageStart"] = 1,
                        ["pageEnd"] = 1,
                        ["label"] = label,
                        ["sourceHash"] = TryGetString(item.Result, "sourceHash") ?? TryGetString(item.Result, "SourceHash"),
                        ["docLanguage"] = TryGetDocumentLanguage(item.Result),
                        ["profileLanguage"] = TryGetString(item.Result, "profileLanguage") ?? TryGetString(item.Result, "ProfileLanguage"),
                        ["category"] = TryGetString(item.Result, "category") ?? TryGetString(item.Result, "Category"),
                        ["categoryRef"] = TryGetString(item.Result, "categoryRef") ?? TryGetString(item.Result, "CategoryRef"),
                        ["categoryPath"] = TryGetString(item.Result, "categoryPath") ?? TryGetString(item.Result, "CategoryPath"),
                        ["chunkId"] = TryGetString(item.Result, "chunkId") ?? TryGetString(item.Result, "ChunkId"),
                        ["extractionQuality"] = CompactExtractionQualityForPrompt(item.Result),
                        ["matchedContentCards"] = CompactMatchedContentCardsForPrompt(item.Result),
                        ["profileSignals"] = CompactProfileSignalsForPrompt(item.Result),
                        ["selectionHints"] = CompactSelectionHintsForPrompt(item.Result),
                        ["contentSignals"] = CompactRetrievalContentSignalsForPrompt(item.Result)
                    };
                    anchors.Add(anchor.Where(static pair => pair.Value is not null).ToDictionary(static pair => pair.Key, static pair => pair.Value!, StringComparer.Ordinal));
                }
            }

            var rootDocLanguage = TryGetDocumentLanguage(item.Result);
            var docLanguage = item.ToolName == "summary.get"
                ? rootDocLanguage ?? rootSource?.DocLanguage
                : rootSource?.DocLanguage ?? rootDocLanguage;
            var sourceHash = rootSource?.SourceHash ?? TryGetString(item.Result, "sourceHash") ?? TryGetString(item.Result, "SourceHash");
            var updatedAt = TryGetString(item.Result, "updatedAt") ?? TryGetString(item.Result, "UpdatedAt");
            object? meta = null;
            if (item.Result.TryGetProperty("meta", out var metaEl) && metaEl.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                try
                {
                    meta = JsonSerializer.Deserialize<object>(metaEl.GetRawText());
                }
                catch
                {
                    meta = null;
                }
            }

            object? payload = anchors.Count > 0
                || !string.IsNullOrWhiteSpace(docLanguage)
                || !string.IsNullOrWhiteSpace(sourceHash)
                || !string.IsNullOrWhiteSpace(updatedAt)
                || meta is not null
                ? new
                {
                    sources = anchors,
                    docLanguage,
                    sourceHash,
                    updatedAt,
                    meta
                }
                : null;
            if (summaryMemorySources.Count > 0)
            {
                _mem.LastSourcesUsed = NormalizeVisibleSourceRefsForMemory(
                    summaryMemorySources);
            }
            return (answer, payload, docLanguage, sourceHash, updatedAt);
        }
        catch
        {
            return (string.Empty, null, null, null, null);
        }
    }

    private static List<ToolMemory.SourceRef> DeriveSourcesFromSummarySearch(ToolResults toolResults)
    {
        try
        {
            var sources = new List<ToolMemory.SourceRef>();
            foreach (var item in toolResults.Items.Where(static x => x.ToolName == "summary.search" && string.IsNullOrWhiteSpace(x.Error)))
            {
                if (item.Result.ValueKind != JsonValueKind.Object
                    || !item.Result.TryGetProperty("items", out var items)
                    || items.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var entry in items.EnumerateArray())
                {
                    var source = TryBuildSourceRefFromSummarySearchItem(entry);
                    if (source is null || string.IsNullOrWhiteSpace(source.DocPath))
                        continue;

                    sources.Add(source);
                }
            }

            return MergeSourceRefsByPage(sources)
                .Take(8)
                .ToList();
        }
        catch
        {
            return new List<ToolMemory.SourceRef>();
        }
    }

    private static ToolMemory.SourceRef? TryBuildSourceRefFromSummarySearchItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        var direct = TryBuildSourceRefFromJsonElement(item);
        var sourceElement = TryGetObject(item, "source") ?? TryGetObject(item, "Source");
        var nested = sourceElement.HasValue
            ? TryBuildSourceRefFromJsonElement(sourceElement.Value)
            : null;

        return MergeSummarySourceRefs(direct, nested);
    }

    private static ToolMemory.SourceRef? MergeSummarySourceRefs(
        ToolMemory.SourceRef? fallback,
        ToolMemory.SourceRef? preferred)
    {
        if (fallback is null)
            return preferred;
        if (preferred is null)
            return fallback;

        var cards = preferred.MatchedContentCards.Count > 0
            ? preferred.MatchedContentCards
            : fallback.MatchedContentCards;
        var qualitySignals = preferred.QualitySignals
            .Concat(fallback.QualitySignals)
            .Where(static signal => !string.IsNullOrWhiteSpace(signal))
            .Select(static signal => signal.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
        var chunkQualitySignals = preferred.ChunkQualitySignals
            .Concat(fallback.ChunkQualitySignals)
            .Where(static signal => !string.IsNullOrWhiteSpace(signal))
            .Select(static signal => signal.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        return new ToolMemory.SourceRef
        {
            EvidenceId = NullIfWhiteSpace(preferred.EvidenceId)
                         ?? NullIfWhiteSpace(fallback.EvidenceId),
            DocId = NullIfWhiteSpace(preferred.DocId) ?? NullIfWhiteSpace(fallback.DocId),
            DocPath = NullIfWhiteSpace(preferred.DocPath) ?? fallback.DocPath,
            DocName = NullIfWhiteSpace(preferred.DocName)
                      ?? NullIfWhiteSpace(fallback.DocName),
            PageStart = preferred.PageStart > 0 ? preferred.PageStart : fallback.PageStart,
            PageEnd = preferred.PageEnd > 0 ? Math.Max(preferred.PageStart, preferred.PageEnd) : fallback.PageEnd,
            Label = NullIfWhiteSpace(preferred.Label) ?? fallback.Label,
            SourceHash = NullIfWhiteSpace(preferred.SourceHash) ?? NullIfWhiteSpace(fallback.SourceHash),
            RevisionId = NullIfWhiteSpace(preferred.RevisionId)
                         ?? NullIfWhiteSpace(fallback.RevisionId),
            DocLanguage = NullIfWhiteSpace(preferred.DocLanguage) ?? NullIfWhiteSpace(fallback.DocLanguage),
            ProfileLanguage = NullIfWhiteSpace(preferred.ProfileLanguage) ?? NullIfWhiteSpace(fallback.ProfileLanguage),
            Category = NullIfWhiteSpace(preferred.Category) ?? NullIfWhiteSpace(fallback.Category),
            CategoryRef = NullIfWhiteSpace(preferred.CategoryRef) ?? NullIfWhiteSpace(fallback.CategoryRef),
            CategoryPath = NullIfWhiteSpace(preferred.CategoryPath) ?? NullIfWhiteSpace(fallback.CategoryPath),
            ChunkId = NullIfWhiteSpace(preferred.ChunkId) ?? NullIfWhiteSpace(fallback.ChunkId),
            AnchorId = NullIfWhiteSpace(preferred.AnchorId)
                       ?? NullIfWhiteSpace(fallback.AnchorId),
            ContentCardId = NullIfWhiteSpace(preferred.ContentCardId)
                            ?? NullIfWhiteSpace(fallback.ContentCardId),
            ExtractionSource = NullIfWhiteSpace(preferred.ExtractionSource) ?? NullIfWhiteSpace(fallback.ExtractionSource),
            DocumentQualityStatus = NullIfWhiteSpace(preferred.DocumentQualityStatus) ?? NullIfWhiteSpace(fallback.DocumentQualityStatus),
            PageQualityStatus = NullIfWhiteSpace(preferred.PageQualityStatus) ?? NullIfWhiteSpace(fallback.PageQualityStatus),
            TextStatus = NullIfWhiteSpace(preferred.TextStatus) ?? NullIfWhiteSpace(fallback.TextStatus),
            ChunkTextStatus = NullIfWhiteSpace(preferred.ChunkTextStatus) ?? NullIfWhiteSpace(fallback.ChunkTextStatus),
            ChunkTextSparse = preferred.ChunkTextSparse ?? fallback.ChunkTextSparse,
            ChunkOcrCandidate = preferred.ChunkOcrCandidate ?? fallback.ChunkOcrCandidate,
            QualityStatus = NullIfWhiteSpace(preferred.QualityStatus) ?? NullIfWhiteSpace(fallback.QualityStatus),
            ExtractionConfidence = preferred.ExtractionConfidence ?? fallback.ExtractionConfidence,
            DocumentExtractionConfidence = preferred.DocumentExtractionConfidence ?? fallback.DocumentExtractionConfidence,
            PageExtractionConfidence = preferred.PageExtractionConfidence ?? fallback.PageExtractionConfidence,
            ManualReviewRecommended = preferred.ManualReviewRecommended || fallback.ManualReviewRecommended,
            DocumentManualReviewRecommended = preferred.DocumentManualReviewRecommended || fallback.DocumentManualReviewRecommended,
            PageManualReviewRecommended = preferred.PageManualReviewRecommended || fallback.PageManualReviewRecommended,
            OcrAttempted = preferred.OcrAttempted || fallback.OcrAttempted,
            OcrApplied = preferred.OcrApplied || fallback.OcrApplied,
            OcrRecommended = preferred.OcrRecommended || fallback.OcrRecommended,
            ExtractionDiagnosticSummary = CloneSourceExtractionDiagnostic(preferred.ExtractionDiagnosticSummary ?? fallback.ExtractionDiagnosticSummary),
            QualitySignals = qualitySignals,
            ChunkQualitySignals = chunkQualitySignals,
            ProfileSignals = MergeSourceProfileSignals([preferred, fallback]),
            MatchedContentCards = cards
                .Select(static card => new ToolMemory.SourceContentCardRef
                {
                    Title = card.Title,
                    ContentCardId = card.ContentCardId,
                    PageStart = card.PageStart,
                    PageEnd = card.PageEnd,
                    Kind = card.Kind,
                    Signals = card.Signals.ToList(),
                    Evidence = card.Evidence
                })
                .ToList(),
            SelectionHintEvidenceRole = NullIfWhiteSpace(preferred.SelectionHintEvidenceRole) ?? NullIfWhiteSpace(fallback.SelectionHintEvidenceRole),
            SelectionHintActionabilityScore = preferred.SelectionHintActionabilityScore ?? fallback.SelectionHintActionabilityScore,
            SelectionHintSupportScore = preferred.SelectionHintSupportScore ?? fallback.SelectionHintSupportScore,
            SelectionHintFragmentScore = preferred.SelectionHintFragmentScore ?? fallback.SelectionHintFragmentScore,
            SelectionHintNavigationScore = preferred.SelectionHintNavigationScore ?? fallback.SelectionHintNavigationScore,
            SelectionHintQualityPenalty = preferred.SelectionHintQualityPenalty ?? fallback.SelectionHintQualityPenalty
        };
    }

    private sealed record StoredSummaryHit(string SummaryText, object? SourcesPayload, string SourceLanguage, string? SourceHash, string? UpdatedAt);

    private async Task<(string finalAnswer, object? sourcesPayload)> GetStoredSummaryForDisplayAsync(
        string docRef,
        string targetLanguage,
        Action<string>? onDelta,
        CancellationToken ct)
    {
        var hit = await TryGetStoredSummaryHitAsync(docRef, ct).ConfigureAwait(false);
        if (hit is null || string.IsNullOrWhiteSpace(hit.SummaryText))
            return (string.Empty, null);

        var sourceLanguage = NormalizeDocumentLanguageTag(hit.SourceLanguage);
        var requestedLanguage = NormalizeLanguageCode(targetLanguage);
        if (string.IsNullOrWhiteSpace(requestedLanguage)
            || string.Equals(requestedLanguage, sourceLanguage, StringComparison.OrdinalIgnoreCase))
        {
            await EmitDeterministicTextAsync(hit.SummaryText, onDelta, ct).ConfigureAwait(false);
            return (hit.SummaryText, hit.SourcesPayload);
        }

        var sourceVersion = !string.IsNullOrWhiteSpace(hit.SourceHash)
            ? hit.SourceHash!.Trim()
            : !string.IsNullOrWhiteSpace(hit.UpdatedAt)
                ? hit.UpdatedAt!.Trim()
                : "unknown-source-version";
        var cacheKey = $"{docRef}|{sourceLanguage}|{sourceVersion}|{requestedLanguage}";
        if (_mem.SummaryTranslationCache.TryGetValue(cacheKey, out var cachedTranslation) && !string.IsNullOrWhiteSpace(cachedTranslation))
        {
            await EmitDeterministicTextAsync(cachedTranslation, onDelta, ct).ConfigureAwait(false);
            return (cachedTranslation, hit.SourcesPayload);
        }

        var translated = await TranslateStoredSummaryAsync(hit.SummaryText, sourceLanguage, requestedLanguage, onDelta, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(translated))
        {
            _mem.SummaryTranslationCache[cacheKey] = translated;
            return (translated, hit.SourcesPayload);
        }

        await EmitDeterministicTextAsync(hit.SummaryText, onDelta, ct).ConfigureAwait(false);
        return (hit.SummaryText, hit.SourcesPayload);
    }

    private async Task<string> TranslateStoredSummaryAsync(
        string summaryText,
        string sourceLanguage,
        string targetLanguage,
        Action<string>? onDelta,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(summaryText))
            return string.Empty;

        if (string.Equals(sourceLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            await EmitDeterministicTextAsync(summaryText, onDelta, ct).ConfigureAwait(false);
            return summaryText.Trim();
        }

        var streamed = new StringBuilder();
        try
        {
            var sourceLanguageForPrompt = string.Equals(sourceLanguage, "und", StringComparison.OrdinalIgnoreCase)
                ? "unknown; detect it from the source text"
                : sourceLanguage;
            var system = $@"You are SAAIA assistant.
Translate the stored summary faithfully.
Source language: {sourceLanguageForPrompt}
Target language: {targetLanguage}
Rules:
- Preserve all concrete facts.
- Preserve the structure and level of detail.
- Do not shorten the text.
- Do not add any information.
- Never output a partial URL, partial file path or visibly truncated token such as ""www"".
- If a value is incomplete in the source text, omit it instead of guessing or truncating it.
- Return plain text only.";

            await _llm.StreamAsync(new[]
            {
                ("system", system),
                ("user", summaryText)
            }, forceJson: false, delta =>
            {
                if (string.IsNullOrEmpty(delta))
                    return;
                streamed.Append(delta);
                onDelta?.Invoke(delta);
            }, ct).ConfigureAwait(false);

            var translated = streamed.ToString().Replace("**", string.Empty).Trim();
            return string.IsNullOrWhiteSpace(translated) ? summaryText.Trim() : translated;
        }
        catch
        {
            if (streamed.Length == 0 && onDelta is not null)
                await EmitDeterministicTextAsync(summaryText.Trim(), onDelta, ct).ConfigureAwait(false);
            return streamed.Length == 0 ? summaryText.Trim() : streamed.ToString().Replace("**", string.Empty).Trim();
        }
    }

    private async Task<(string finalAnswer, object? sourcesPayload)> RunKnownDocumentSummaryFlowAsync(
        string userMessage,
        string docRef,
        DocumentSummaryRequestKind requestKind,
        CancellationToken ct,
        Action<string>? onDelta,
        Action<string>? onProgress,
        JsonElement? plannedLiveSummaryArgs = null)
    {
        if (!string.IsNullOrWhiteSpace(docRef))
            _mem.LastRequestedDocumentRef = docRef.Trim();

        var detectedLanguage = NormalizeLanguageCode(ResolveInteractionLanguage(userMessage));
        var language = !string.IsNullOrWhiteSpace(detectedLanguage)
            ? detectedLanguage
            : NormalizeLanguageCode(_mem.LastLanguage);
        _mem.LastLanguage = language;

        return requestKind switch
        {
            DocumentSummaryRequestKind.About => await RunDocumentAboutRequestAsync(docRef, language, ct, onDelta, onProgress).ConfigureAwait(false),
            DocumentSummaryRequestKind.SummaryReadStoredExact => await RunDocumentStoredSummaryReadRequestAsync(docRef, language, ct, onDelta, onProgress).ConfigureAwait(false),
            DocumentSummaryRequestKind.SummaryCheckOnly => await RunDocumentSummaryCheckRequestAsync(docRef, language, ct, onDelta, onProgress).ConfigureAwait(false),
            DocumentSummaryRequestKind.SummaryStore => await RunDocumentSummaryStoreRequestAsync(docRef, language, userMessage, ct, onDelta, onProgress).ConfigureAwait(false),
            _ => await RunDocumentSummaryRequestAsync(
                    docRef,
                    language,
                    userMessage,
                    ct,
                    onDelta,
                    onProgress,
                    plannedLiveSummaryArgs)
                .ConfigureAwait(false)
        };
    }

    private DocumentSummaryRequestKind ResolveDocumentSummaryRequestKind(string userMessage, DocumentRefResolver.AnalysisResult analysis)
    {
        if (analysis.WantsStoredSummaryStore)
            return DocumentSummaryRequestKind.SummaryStore;

        if (IsExplicitStoredSummaryReadRequest(userMessage, analysis))
            return DocumentSummaryRequestKind.SummaryReadStoredExact;

        if (analysis.WantsStoredSummaryCheck)
            return DocumentSummaryRequestKind.SummaryCheckOnly;

        if (analysis.WantsAbout && !analysis.WantsSummary)
            return DocumentSummaryRequestKind.About;

        return DocumentSummaryRequestKind.SummaryReadOrLive;
    }

    private static bool IsStoredSummaryAvailabilityQuestion(string userMessage)
    {
        var s = (userMessage ?? string.Empty).Trim();
        if (s.Length == 0)
            return false;

        return Regex.IsMatch(s, @"\b(?:verify|check|confirm|exists?|available|availability)\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(s, @"\b(?:v[ée]rif(?:ie|ier)|disponible|existe|existence)\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(s, @"\b(?:est-ce\s+que|is\s+there|does\s+the\s+document\s+have|has\s+the\s+document\s+got)\b", RegexOptions.IgnoreCase);
    }

    private static bool IsExplicitStoredSummaryReadRequest(string userMessage, DocumentRefResolver.AnalysisResult analysis)
    {
        if (analysis.WantsStoredSummaryStore)
            return false;

        var s = (userMessage ?? string.Empty).Trim();
        if (s.Length == 0)
            return false;

        var hasStoredCue = Regex.IsMatch(s, @"\b(?:stock[ée]?|stored|saved|cached|enregistr[ée]?|sauvegard[ée]?)\b", RegexOptions.IgnoreCase);
        if (!hasStoredCue)
            return false;

        if (IsStoredSummaryAvailabilityQuestion(s))
            return false;

        var hasReadCue = Regex.IsMatch(s, @"\b(?:donne|give|show|display|montre|affiche|read|get|load|lis|return|renvoie)\b", RegexOptions.IgnoreCase);
        return hasReadCue || analysis.WantsSummary;
    }

    private async Task<(string finalAnswer, object? sourcesPayload)> RunDocumentStoredSummaryReadRequestAsync(
        string docRef,
        string language,
        CancellationToken ct,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        onProgress?.Invoke(DeterministicAgentText.ProgressCheckStoredSummaryAvailable(language));

        var cached = await GetStoredSummaryForDisplayAsync(docRef, language, onDelta, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(cached.finalAnswer))
        {
            onProgress?.Invoke(DeterministicAgentText.ProgressReturnStoredSummary(language));
            return cached;
        }

        var missing = LocalizedStrings.SummaryNotStored(language);
        await EmitDeterministicTextAsync(missing, onDelta, ct).ConfigureAwait(false);
        return (missing, null);
    }

    private async Task<(string finalAnswer, object? sourcesPayload)> RunDocumentAboutRequestAsync(
        string docRef,
        string language,
        CancellationToken ct,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        onProgress?.Invoke(DeterministicAgentText.ProgressRetrieveRepresentativePassages(language));

        var liveArgs = CreateJsonArgs(new
        {
            docRef,
            level = "short",
            strategy = "about",
            language,
            responseLanguage = language,
            maxWords = 90,
            maxChunks = 5,
            maxBatches = 1,
            maxCharsPerBatch = 2800
        });

        var live = await ExecRagSummarizeLiveAsync(liveArgs, ct).ConfigureAwait(false);
        var fast = TryBuildSummaryAnswer(BuildSingleToolResult("rag.summarize_live", live));
        if (!string.IsNullOrWhiteSpace(fast.answer))
        {
            onProgress?.Invoke(DeterministicAgentText.ProgressComposeShortOverview(language));
            var rendered = await RenderSummaryForDisplayAsync(fast.answer, language, "about", onDelta, ct).ConfigureAwait(false);
            return (string.IsNullOrWhiteSpace(rendered) ? fast.answer : rendered, fast.sourcesPayload);
        }

        var fallback = BuildDocumentSummaryFailureMessage(
            live, docRef, language, LocalizedStrings.ShortOverviewUnavailable(language));
        await EmitDeterministicTextAsync(fallback, onDelta, ct).ConfigureAwait(false);
        return (fallback, null);
    }

    private async Task<(string finalAnswer, object? sourcesPayload)> RunDocumentSummaryCheckRequestAsync(
        string docRef,
        string language,
        CancellationToken ct,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        onProgress?.Invoke(DeterministicAgentText.ProgressCheckStoredSummaryAvailable(language));

        var cached = await TryGetStoredSummaryHitAsync(docRef, ct).ConfigureAwait(false);
        if (cached is not null && !string.IsNullOrWhiteSpace(cached.SummaryText))
        {
            var yes = LocalizedStrings.SummaryAlreadyStored(language);
            await EmitDeterministicTextAsync(yes, onDelta, ct).ConfigureAwait(false);
            return (yes, cached.SourcesPayload);
        }

        var missing = LocalizedStrings.SummaryNotStored(language);
        await EmitDeterministicTextAsync(missing, onDelta, ct).ConfigureAwait(false);
        return (missing, null);
    }

    private async Task<(string finalAnswer, object? sourcesPayload)> RunDocumentSummaryRequestAsync(
        string docRef,
        string language,
        string userMessage,
        CancellationToken ct,
        Action<string>? onDelta,
        Action<string>? onProgress,
        JsonElement? plannedLiveSummaryArgs = null)
    {
        onProgress?.Invoke(DeterministicAgentText.ProgressCheckExistingStoredSummary(language));
        var cached = await GetStoredSummaryForDisplayAsync(docRef, language, onDelta, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(cached.finalAnswer))
        {
            onProgress?.Invoke(DeterministicAgentText.ProgressReturnStoredSummary(language));
            return cached;
        }

        onProgress?.Invoke(DeterministicAgentText.ProgressBuildLiveSummaryFromDocument(language));
        var liveArgs = BuildDocumentSummaryLiveArgs(
            docRef,
            language,
            userMessage,
            plannedLiveSummaryArgs);

        var live = await ExecRagSummarizeLiveAsync(liveArgs, ct).ConfigureAwait(false);
        var fast = TryBuildSummaryAnswer(BuildSingleToolResult("rag.summarize_live", live));
        if (!string.IsNullOrWhiteSpace(fast.answer))
        {
            onProgress?.Invoke(DeterministicAgentText.ProgressWriteFinalSummary(language));
            await EmitDeterministicTextAsync(
                    fast.answer,
                    onDelta,
                    ct)
                .ConfigureAwait(false);
            return (fast.answer, fast.sourcesPayload);
        }

        var fallback = BuildDocumentSummaryFailureMessage(
            live, docRef, language, LocalizedStrings.SummaryUnavailable(language));
        await EmitDeterministicTextAsync(fallback, onDelta, ct).ConfigureAwait(false);
        return (fallback, null);
    }

    private static string BuildDocumentSummaryFailureMessage(
        JsonElement result, string docRef, string language, string fallback)
        => string.Equals(TryGetString(result, "error"), "doc_not_found", StringComparison.Ordinal)
            ? $"{DeterministicAgentText.DocumentNotFound(language)} ({docRef})"
            : fallback;

    private static JsonElement BuildDocumentSummaryLiveArgs(
        string docRef,
        string language,
        string userMessage,
        JsonElement? plannedLiveSummaryArgs)
    {
        var planned = plannedLiveSummaryArgs is
        {
            ValueKind: JsonValueKind.Object
        }
            ? plannedLiveSummaryArgs.Value
            : default;
        var overviewFacets = planned.ValueKind == JsonValueKind.Object
            ? NormalizeEvidenceOverviewFacets(planned)
            : [];
        var requestedPointCount = planned.ValueKind == JsonValueKind.Object
            ? GetIntArg(planned, "requestedPointCount")
            : null;
        requestedPointCount ??= TryExtractEvidenceOverviewPointCount(
                                   userMessage)
                               ?? DefaultEvidenceOverviewPointCount;
        requestedPointCount = Math.Clamp(
            requestedPointCount.Value,
            2,
            MaximumEvidenceOverviewPointCount);
        var sampleCount = planned.ValueKind == JsonValueKind.Object
            ? GetIntArg(planned, "sampleCount")
            : null;
        sampleCount = Math.Clamp(
            sampleCount ?? requestedPointCount.Value + 1,
            requestedPointCount.Value,
            MaximumEvidenceOverviewPointCount + 1);
        return CreateJsonArgs(new
        {
            docRef,
            level = "medium",
            strategy = "evidence_overview",
            language,
            responseLanguage = language,
            userRequest = userMessage,
            overviewFacets,
            requestedPointCount,
            sampleCount
        });
    }

    internal static JsonElement BuildDocumentSummaryLiveArgsForTests(
        string docRef,
        string language,
        string userMessage,
        JsonElement? plannedLiveSummaryArgs)
        => BuildDocumentSummaryLiveArgs(
            docRef,
            language,
            userMessage,
            plannedLiveSummaryArgs);

    private async Task<(string finalAnswer, object? sourcesPayload)> RunDocumentSummaryStoreRequestAsync(
        string docRef,
        string language,
        string userMessage,
        CancellationToken ct,
        Action<string>? onDelta,
        Action<string>? onProgress)
    {
        if (!_api.HasAdminKey)
        {
            var denied = LocalizedStrings.SummaryStoreRequiresAdmin(language);
            await EmitDeterministicTextAsync(denied, onDelta, ct).ConfigureAwait(false);
            return (denied, null);
        }

        var forceRefresh = WantsSummaryRefresh(userMessage);

        onProgress?.Invoke(DeterministicAgentText.ProgressCheckReusableSummaryCache(language));

        var cached = await GetStoredSummaryForDisplayAsync(docRef, language, onDelta, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(cached.finalAnswer) && !forceRefresh)
        {
            onProgress?.Invoke(DeterministicAgentText.ProgressReusableSummaryAlreadyAvailable(language));
            return cached;
        }

        onProgress?.Invoke(DeterministicAgentText.ProgressGenerateAndStoreReusableSummary(language));

        var generatedByServer = await TryQueueAdminSummaryGenerationAsync(docRef, ct).ConfigureAwait(false);
        if (generatedByServer.Stored is not null && !string.IsNullOrWhiteSpace(generatedByServer.Stored.SummaryText))
        {
            var sourceLanguage = NormalizeDocumentLanguageTag(generatedByServer.Stored.SourceLanguage);
            var requestedLanguage = NormalizeLanguageCode(language);
            if (string.Equals(sourceLanguage, requestedLanguage, StringComparison.OrdinalIgnoreCase))
            {
                await EmitDeterministicTextAsync(generatedByServer.Stored.SummaryText, onDelta, ct).ConfigureAwait(false);
                return (generatedByServer.Stored.SummaryText, generatedByServer.Stored.SourcesPayload);
            }

            var translated = await TranslateStoredSummaryAsync(generatedByServer.Stored.SummaryText, sourceLanguage, requestedLanguage, onDelta, ct).ConfigureAwait(false);
            return (string.IsNullOrWhiteSpace(translated) ? generatedByServer.Stored.SummaryText : translated, generatedByServer.Stored.SourcesPayload);
        }

        if (generatedByServer.Queued)
        {
            var queued = LocalizedStrings.SummaryStoreQueued(language);
            await EmitDeterministicTextAsync(queued, onDelta, ct).ConfigureAwait(false);
            return (queued, null);
        }

        var failed = string.Equals(generatedByServer.Error, "backoffice_unavailable", StringComparison.OrdinalIgnoreCase)
            ? LocalizedStrings.SummaryStoreBackofficeUnavailable(language)
            : LocalizedStrings.SummaryStoreFailed(language);
        await EmitDeterministicTextAsync(failed, onDelta, ct).ConfigureAwait(false);
        return (failed, null);
    }

    private async Task<(string finalAnswer, object? sourcesPayload)> TryGetStoredSummaryAnswerAsync(string docRef, CancellationToken ct)
    {
        var hit = await TryGetStoredSummaryHitAsync(docRef, ct).ConfigureAwait(false);
        return hit is null || string.IsNullOrWhiteSpace(hit.SummaryText)
            ? (string.Empty, null)
            : (hit.SummaryText, hit.SourcesPayload);
    }

    private async Task<StoredSummaryHit?> TryGetStoredSummaryHitAsync(string docRef, CancellationToken ct)
    {
        try
        {
            var args = CreateJsonArgs(new { docRef, level = "medium" });
            var exists = await ExecSummaryExistsAsync(args, ct).ConfigureAwait(false);
            if (!TryGetBoolProp(exists, "exists").GetValueOrDefault())
                return null;

            var summary = await ExecSummaryGetAsync(args, ct).ConfigureAwait(false);
            var fast = TryBuildSummaryAnswer(BuildSingleToolResult("summary.get", summary));
            if (string.IsNullOrWhiteSpace(fast.answer))
                return null;

            var sourceLanguage = fast.docLanguage ?? TryGetDocumentLanguage(summary) ?? string.Empty;
            var sourceHash = fast.sourceHash ?? TryGetString(summary, "sourceHash") ?? TryGetString(summary, "SourceHash");
            var updatedAt = fast.updatedAt ?? TryGetString(summary, "updatedAt") ?? TryGetString(summary, "UpdatedAt");
            return new StoredSummaryHit(fast.answer, fast.sourcesPayload, sourceLanguage, sourceHash, updatedAt);
        }
        catch
        {
            return null;
        }
    }

    private sealed record AdminSummaryQueueOutcome(StoredSummaryHit? Stored, bool Queued, string? JobId, string? Error = null);

    private async Task<AdminSummaryQueueOutcome> TryQueueAdminSummaryGenerationAsync(
        string docRef,
        CancellationToken ct)
    {
        string? jobId = null;

        try
        {
            ClientLog.Info($"summary.admin.generate:start docRef={docRef}");
            var generateArgs = CreateJsonArgs(new { docRef, level = "medium", force = false });
            var generate = await ExecAdminSummaryGenerateAsync(generateArgs, ct).ConfigureAwait(false);
            jobId = TryGetString(generate, "jobId");
            var queued = TryGetBoolProp(generate, "queued");
            var status = TryGetString(generate, "status");
            var error = TryGetString(generate, "error");

            if (queued == false || !string.IsNullOrWhiteSpace(error))
            {
                var reason = !string.IsNullOrWhiteSpace(error) ? error : status ?? "not_queued";
                ClientLog.Warn($"summary.admin.generate:not_queued docRef={docRef} status={status} error={error}");
                return new AdminSummaryQueueOutcome(null, false, null, reason);
            }

            if (string.IsNullOrWhiteSpace(jobId))
            {
                var reason = status ?? "missing_job_id";
                ClientLog.Warn($"summary.admin.generate:missing_job_id docRef={docRef} status={status}");
                return new AdminSummaryQueueOutcome(null, false, null, reason);
            }

            ClientLog.Info($"summary.admin.generate:queued docRef={docRef} jobId={jobId} status={status}");
        }
        catch (Exception ex)
        {
            ClientLog.Warn($"summary.admin.generate:failed docRef={docRef} error={ex.Message}");
            return new AdminSummaryQueueOutcome(null, false, null, ex.Message);
        }

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var cached = await TryGetStoredSummaryHitAsync(docRef, ct).ConfigureAwait(false);
            if (cached is not null && !string.IsNullOrWhiteSpace(cached.SummaryText))
            {
                ClientLog.Info($"summary.admin.verify:hit docRef={docRef} jobId={jobId}");
                return new AdminSummaryQueueOutcome(cached, true, jobId);
            }

            if (attempt < 3)
                await Task.Delay(TimeSpan.FromMilliseconds(350), ct).ConfigureAwait(false);
        }

        ClientLog.Info($"summary.admin.generate:pending docRef={docRef} jobId={jobId}");
        return new AdminSummaryQueueOutcome(null, true, jobId);
    }

    private static ToolResults BuildSingleToolResult(string toolName, JsonElement result)
    {
        var toolResults = new ToolResults();
        toolResults.Items.Add(new ToolResults.Item
        {
            ToolName = toolName,
            Result = result
        });
        return toolResults;
    }

    private static JsonElement CreateJsonArgs(object payload)
        => JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement.Clone();
    private static bool WantsSummaryRefresh(string userMessage)
    {
        var s = (userMessage ?? string.Empty).Trim();
        if (s.Length == 0)
            return false;

        return Regex.IsMatch(s, @"\b(?:refresh|regenerate|rebuild|update)\b", RegexOptions.IgnoreCase)
               || Regex.IsMatch(s, @"\b(?:regenere|regénère|met\s+a\s+jour|mise\s+a\s+jour|recr[eé]e)\b", RegexOptions.IgnoreCase);
    }

    private static string PrefixSummaryMessage(string prefix, string summaryText)
    {
        if (string.IsNullOrWhiteSpace(summaryText))
            return prefix;
        if (string.IsNullOrWhiteSpace(prefix))
            return summaryText;
        return $"{prefix.Trim()}\n\n{summaryText.Trim()}";
    }
}
