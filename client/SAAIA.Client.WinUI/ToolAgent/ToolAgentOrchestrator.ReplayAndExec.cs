using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
    private static string BuildRagHitDedupeKey(JsonElement hit)
    {
        var docPath =
            TryGetString(hit, "docPath")
            ?? TryGetString(hit, "doc_path")
            ?? string.Empty;
        var pageStart =
            TryGetInt(hit, "pageStart")
            ?? TryGetInt(hit, "page_start")
            ?? TryGetInt(hit, "page")
            ?? 1;
        var pageEnd =
            TryGetInt(hit, "pageEnd")
            ?? TryGetInt(hit, "page_end")
            ?? pageStart;
        var chunkId =
            TryGetString(hit, "chunkId")
            ?? TryGetString(hit, "chunk_id")
            ?? TryGetString(hit, "ChunkId");
        var sourceHash =
            TryGetString(hit, "sourceHash")
            ?? TryGetString(hit, "source_hash")
            ?? TryGetString(hit, "SourceHash");
        var contentCardKeys = ExtractRagHitContentCardKeysForDedupe(hit);
        var discriminator = !string.IsNullOrWhiteSpace(chunkId)
            ? $"chunk:{chunkId.Trim()}"
            : contentCardKeys.Count > 0
                ? $"cards:{string.Join(",", contentCardKeys)}"
                : !string.IsNullOrWhiteSpace(sourceHash)
                    ? $"source:{sourceHash.Trim()}"
                    : "page";

        return $"{docPath}|{pageStart}|{pageEnd}|{discriminator}";
    }

    private static IReadOnlyList<string> ExtractRagHitContentCardKeysForDedupe(JsonElement hit)
    {
        var cards = TryGetArray(hit, "matchedContentCards")
                    ?? TryGetArray(hit, "matched_content_cards")
                    ?? TryGetArray(hit, "contentCards")
                    ?? TryGetArray(hit, "content_cards")
                    ?? TryGetArray(hit, "MatchedContentCards")
                    ?? TryGetArray(hit, "ContentCards");
        if (!cards.HasValue)
            return [];

        return cards.Value.EnumerateArray()
            .Where(static card => card.ValueKind == JsonValueKind.Object)
            .Select(BuildRagHitContentCardKeyForDedupe)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
    }

    private static string? BuildRagHitContentCardKeyForDedupe(JsonElement card)
    {
        var id = TryGetString(card, "contentCardId")
                 ?? TryGetString(card, "content_card_id")
                 ?? TryGetString(card, "ContentCardId");
        if (!string.IsNullOrWhiteSpace(id))
            return $"id:{id.Trim()}";

        var title = CollapseWhitespace(
            TryGetString(card, "title")
            ?? TryGetString(card, "Title")
            ?? string.Empty);
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var kind = CollapseWhitespace(
            TryGetString(card, "kind")
            ?? TryGetString(card, "Kind")
            ?? string.Empty);
        var pageStart = TryGetInt(card, "pageStart")
                        ?? TryGetInt(card, "page_start")
                        ?? TryGetInt(card, "PageStart");
        var pageEnd = TryGetInt(card, "pageEnd")
                      ?? TryGetInt(card, "page_end")
                      ?? TryGetInt(card, "PageEnd")
                      ?? pageStart;

        return $"shape:{title.ToLowerInvariant()}|{kind.ToLowerInvariant()}|{pageStart?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""}|{pageEnd?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""}";
    }

    private static double ReadRagHitScore(JsonElement hit)
        => hit.ValueKind == JsonValueKind.Object
           && hit.TryGetProperty("score", out var score)
           && score.ValueKind == JsonValueKind.Number
           && score.TryGetDouble(out var value)
            ? value
            : 0.0;

    private static int ComputeRagHitMetadataRichness(JsonElement hit)
    {
        if (hit.ValueKind != JsonValueKind.Object)
            return 0;

        var richness = 0;
        if (!string.IsNullOrWhiteSpace(TryGetString(hit, "sourceHash") ?? TryGetString(hit, "source_hash")))
            richness += 6;
        if (!string.IsNullOrWhiteSpace(TryGetDocumentLanguage(hit)))
            richness += 4;
        if (!string.IsNullOrWhiteSpace(TryGetString(hit, "profileLanguage") ?? TryGetString(hit, "profile_language")))
            richness += 4;
        if (!string.IsNullOrWhiteSpace(TryGetString(hit, "categoryRef") ?? TryGetString(hit, "category_ref")))
            richness += 3;
        if (!string.IsNullOrWhiteSpace(TryGetString(hit, "categoryPath") ?? TryGetString(hit, "category_path")))
            richness += 3;
        if (!string.IsNullOrWhiteSpace(TryGetString(hit, "chunkId") ?? TryGetString(hit, "chunk_id")))
            richness += 2;
        if (!string.IsNullOrWhiteSpace(TryGetString(hit, "contextualSnippet") ?? TryGetString(hit, "contextual_snippet")))
            richness += 2;

        var quality = TryGetObject(hit, "extractionQuality") ?? TryGetObject(hit, "extraction_quality") ?? TryGetObject(hit, "ExtractionQuality");
        if (quality.HasValue)
        {
            richness += 10;
            if (!string.IsNullOrWhiteSpace(TryGetString(quality.Value, "documentQualityStatus") ?? TryGetString(quality.Value, "document_quality_status")))
                richness += 2;
            if (!string.IsNullOrWhiteSpace(TryGetString(quality.Value, "pageQualityStatus") ?? TryGetString(quality.Value, "page_quality_status")))
                richness += 2;
            richness += Math.Min(8, CountArrayPropertyAny(quality.Value, "signals", "Signals"));
            var diagnostics =
                TryGetObject(quality.Value, "diagnosticSummary")
                ?? TryGetObject(quality.Value, "diagnostic_summary")
                ?? TryGetObject(quality.Value, "DiagnosticSummary");
            if (diagnostics.HasValue)
                richness += 10 + Math.Min(12, CountObjectScalarProperties(diagnostics.Value));
        }

        richness += CountArrayPropertyAny(hit, "matchedContentCards", "matched_content_cards", "contentCards", "content_cards") * 8;
        var hints = TryGetObject(hit, "selectionHints") ?? TryGetObject(hit, "selection_hints") ?? TryGetObject(hit, "SelectionHints");
        if (hints.HasValue)
            richness += 8 + Math.Min(10, CountObjectScalarProperties(hints.Value));

        return richness;
    }

    private static int CountArrayPropertyAny(JsonElement obj, params string[] propertyNames)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return 0;

        foreach (var propertyName in propertyNames)
        {
            if (obj.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Array)
                return value.GetArrayLength();
        }

        return 0;
    }

    private static int CountObjectScalarProperties(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return 0;

        var count = 0;
        foreach (var property in obj.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                count++;
        }

        return count;
    }

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
            "extraction_quality" => RenderExtractionQualityFromReplayData(data, language),
            "extraction_pages" => RenderExtractionPagesFromReplayData(data, language),
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

    private static string RenderExtractionQualityFromReplayData(JsonElement data, string language)
    {
        var summary = data.TryGetProperty("summary", out var summaryElement) && summaryElement.ValueKind == JsonValueKind.Object
            ? summaryElement
            : default;
        var totalDocuments = TryGetInt(summary, "totalDocuments") ?? 0;
        var sb = new StringBuilder();
        sb.AppendLine(DeterministicAgentText.ExtractionQualityHeader(language));
        var scopePath = TryGetString(data, "scopePath") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(scopePath))
            sb.AppendLine(DeterministicAgentText.ExtractionScope(scopePath, language));

        sb.AppendLine(DeterministicAgentText.ExtractionQualitySummary(
            totalDocuments,
            TryGetInt(summary, "okDocuments") ?? 0,
            TryGetInt(summary, "lowTextDocuments") ?? 0,
            TryGetInt(summary, "emptyTextDocuments") ?? 0,
            TryGetInt(summary, "unknownDocuments") ?? 0,
            TryGetInt(summary, "ocrRecommendedDocuments") ?? 0,
            TryGetInt(summary, "ocrAppliedDocuments") ?? 0,
            TryGetInt(summary, "manualReviewRecommendedDocuments") ?? 0,
            TryGetInt(summary, "pageWarningPages") ?? 0,
            language));

        if (data.TryGetProperty("categories", out var categories) && categories.ValueKind == JsonValueKind.Array && categories.GetArrayLength() > 0)
        {
            sb.AppendLine(DeterministicAgentText.ExtractionCategoriesHeader(language));
            foreach (var category in categories.EnumerateArray().Where(static x => x.ValueKind == JsonValueKind.Object).Take(12))
            {
                var categoryPath = TryGetString(category, "categoryPath") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(categoryPath))
                    continue;

                sb.AppendLine("- " + DeterministicAgentText.ExtractionCategorySummary(
                    categoryPath,
                    TryGetInt(category, "totalDocuments") ?? 0,
                    TryGetInt(category, "lowTextDocuments") ?? 0,
                    TryGetInt(category, "emptyTextDocuments") ?? 0,
                    TryGetInt(category, "ocrRecommendedDocuments") ?? 0,
                    TryGetInt(category, "manualReviewRecommendedDocuments") ?? 0,
                    TryGetInt(category, "pageWarningPages") ?? 0,
                    language));
            }
        }

        if (!data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
        {
            sb.AppendLine(DeterministicAgentText.ExtractionNoDocuments(language));
            return sb.ToString().TrimEnd();
        }

        var index = 1;
        foreach (var entry in items.EnumerateArray().Where(static x => x.ValueKind == JsonValueKind.Object).Take(20))
        {
            var path = (TryGetString(entry, "docPath") ?? string.Empty).Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrWhiteSpace(path))
                continue;

            var label = SanitizeOpenTokenLabel(path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? path);
            var parts = new List<string>();
            AddNonEmptyPart(parts, DeterministicAgentText.ExtractionStatusLabel(TryGetString(entry, "qualityStatus"), language));
            AddNonEmptyPart(parts, DeterministicAgentText.ExtractionDocumentStatus(TryGetString(entry, "documentStatus"), language));
            AddNonEmptyPart(parts, DeterministicAgentText.ExtractionProcessingRunStatus(TryGetString(entry, "processingRunStatus"), language));
            if (TryGetBool(entry, "documentIndexable") == false)
                AddNonEmptyPart(parts, DeterministicAgentText.ExtractionDocumentNotIndexable(language));
            AddNonEmptyPart(parts, DeterministicAgentText.ExtractionFailureReason(TryGetString(entry, "failureReason"), language));
            AddNonEmptyPart(parts, DeterministicAgentText.ExtractionOcrFailureReason(TryGetString(entry, "ocrFailureReason"), language));
            AddNonEmptyPart(parts, FormatPercentPart(DeterministicAgentText.ExtractionConfidenceLabel(language), TryGetDouble(entry, "extractionConfidence")));
            AddNonEmptyPart(parts, FormatCodePart(DeterministicAgentText.ExtractionSourceLabel(language), TryGetString(entry, "extractionSource")));
            AddNonEmptyPart(parts, FormatPageRatioPart(TryGetInt(entry, "textPageCount"), TryGetInt(entry, "pageCount"), language));
            AddNonEmptyPart(parts, FormatCodePart(DeterministicAgentText.ExtractionNativeTextLabel(language), TryGetString(entry, "nativeTextStatus")));
            AddNonEmptyPart(parts, FormatCodePart(DeterministicAgentText.ExtractionTextStatusLabel(language), TryGetString(entry, "textStatus")));
            AddNonEmptyPart(parts, FormatCodePart(DeterministicAgentText.ExtractionOcrLanguagesLabel(language), TryGetString(entry, "ocrLanguages")));
            AddNonEmptyPart(parts, FormatDurationPart(DeterministicAgentText.ExtractionOcrDurationLabel(language), TryGetLong(entry, "ocrDurationMs")));

            if (TryGetBool(entry, "ocrApplied") == true)
                AddNonEmptyPart(parts, DeterministicAgentText.ExtractionOcrApplied(language));
            else if (TryGetBool(entry, "ocrRecommended") == true)
                AddNonEmptyPart(parts, DeterministicAgentText.ExtractionOcrRecommended(language));
            if (TryGetBool(entry, "manualReviewRecommended") == true)
                AddNonEmptyPart(parts, DeterministicAgentText.ExtractionReviewRecommended(language));

            var warningPages = TryGetInt(entry, "pageWarningCount") ?? 0;
            var reviewPages = TryGetInt(entry, "pageReviewRecommendedCount") ?? 0;
            if (warningPages > 0 || reviewPages > 0)
                AddNonEmptyPart(parts, DeterministicAgentText.ExtractionPageIssues(warningPages, reviewPages, language));
            AddNonEmptyPart(parts, FormatWordStatsPart(
                TryGetInt(entry, "totalWordCount"),
                TryGetDouble(entry, "averageWordsPerPage"),
                TryGetDouble(entry, "textPageRatio"),
                language));
            AddNonEmptyPart(parts, FormatArrayPart(DeterministicAgentText.ExtractionSignalsLabel(language), entry, "signals", 4, 72));

            var suffix = parts.Count == 0 ? string.Empty : $" - {string.Join("; ", parts)}";
            sb.AppendLine($"{index++}. [[open|{path}|1|{label}]]{suffix}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string RenderExtractionPagesFromReplayData(JsonElement data, string language)
    {
        var docPath = (TryGetString(data, "docPath") ?? string.Empty).Replace('\\', '/').TrimStart('/');
        var docLabel = string.IsNullOrWhiteSpace(docPath)
            ? TryGetString(data, "docId") ?? string.Empty
            : docPath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? docPath;
        var summary = data.TryGetProperty("summary", out var summaryElement) && summaryElement.ValueKind == JsonValueKind.Object
            ? summaryElement
            : default;

        var sb = new StringBuilder();
        sb.AppendLine(DeterministicAgentText.ExtractionPagesHeader(docLabel, language));
        sb.AppendLine(DeterministicAgentText.ExtractionPagesSummary(
            TryGetInt(summary, "pageCount") ?? 0,
            TryGetInt(summary, "manualReviewRecommendedPages") ?? 0,
            TryGetInt(summary, "probableOcrNoisePages") ?? 0,
            TryGetInt(summary, "emptyTextPages") ?? 0,
            TryGetInt(summary, "lowTextPages") ?? 0,
            TryGetInt(summary, "imagePages") ?? 0,
            language));
        var docParts = new List<string>();
        AddNonEmptyPart(docParts, DeterministicAgentText.ExtractionDocumentStatus(TryGetString(data, "documentStatus"), language));
        AddNonEmptyPart(docParts, DeterministicAgentText.ExtractionProcessingRunStatus(TryGetString(data, "processingRunStatus"), language));
        if (TryGetBool(data, "documentIndexable") == false)
            AddNonEmptyPart(docParts, DeterministicAgentText.ExtractionDocumentNotIndexable(language));
        AddNonEmptyPart(docParts, DeterministicAgentText.ExtractionFailureReason(TryGetString(data, "failureReason"), language));
        AddNonEmptyPart(docParts, DeterministicAgentText.ExtractionOcrFailureReason(TryGetString(data, "ocrFailureReason"), language));
        AddNonEmptyPart(docParts, DeterministicAgentText.ExtractionOcrAppliedReason(TryGetString(data, "ocrAppliedReason"), language));
        AddNonEmptyPart(docParts, DeterministicAgentText.ExtractionOcrMode(TryGetString(data, "ocrMode"), language));
        if (docParts.Count > 0)
            sb.AppendLine("- " + string.Join("; ", docParts));

        if (!data.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array || pages.GetArrayLength() == 0)
        {
            sb.AppendLine(DeterministicAgentText.ExtractionNoPages(language));
            return sb.ToString().TrimEnd();
        }

        var selectedPages = pages.EnumerateArray()
            .Where(static x => x.ValueKind == JsonValueKind.Object)
            .OrderByDescending(static page => TryGetBool(page, "manualReviewRecommended") == true)
            .ThenByDescending(static page => TryGetBool(page, "ocrCandidate") == true)
            .ThenByDescending(static page => TryGetInt(page, "suspiciousUnitCount") ?? 0)
            .ThenBy(static page => TryGetInt(page, "pageNumber") ?? int.MaxValue)
            .Take(25)
            .ToArray();

        foreach (var page in selectedPages)
        {
            var pageNumber = Math.Max(1, TryGetInt(page, "pageNumber") ?? 1);
            var pageLabel = $"{SourceBackedPagePrefix(language)}{pageNumber}";
            var label = SanitizeOpenTokenLabel(string.IsNullOrWhiteSpace(docLabel) ? pageLabel : $"{docLabel} {pageLabel}");
            var parts = new List<string>();
            AddNonEmptyPart(parts, DeterministicAgentText.ExtractionStatusLabel(TryGetString(page, "qualityStatus"), language));
            AddNonEmptyPart(parts, FormatPercentPart(DeterministicAgentText.ExtractionConfidenceLabel(language), TryGetDouble(page, "extractionConfidence")));
            AddNonEmptyPart(parts, DeterministicAgentText.ExtractionPageCounters(
                TryGetInt(page, "wordCount") ?? 0,
                TryGetInt(page, "charCount") ?? 0,
                TryGetInt(page, "imageCount") ?? 0,
                TryGetInt(page, "chunkCount") ?? 0,
                language));
            AddNonEmptyPart(parts, FormatCodePart(DeterministicAgentText.ExtractionTextStatusLabel(language), TryGetString(page, "textStatus")));
            AddNonEmptyPart(parts, FormatCodePart(DeterministicAgentText.ExtractionImageOcrStatusLabel(language), TryGetString(page, "imageOcrStatus")));
            AddNonEmptyPart(parts, FormatCodePart(DeterministicAgentText.ExtractionOcrReasonLabel(language), TryGetString(page, "imageOcrReason")));
            AddNonEmptyPart(parts, FormatCodePart(DeterministicAgentText.ExtractionOcrExitCodeLabel(language), TryGetInt(page, "imageOcrExitCode")?.ToString(CultureInfo.InvariantCulture)));
            AddNonEmptyPart(parts, FormatArrayPart(DeterministicAgentText.ExtractionSignalsLabel(language), page, "signals", 5, 80));
            AddNonEmptyPart(parts, FormatArrayPart(DeterministicAgentText.ExtractionPreviewLabel(language), page, "unitPreviews", 2, 96)
                ?? FormatArrayPart(DeterministicAgentText.ExtractionPreviewLabel(language), page, "chunkPreviews", 2, 96));

            if (TryGetBool(page, "imageOcrTimedOut") == true)
                AddNonEmptyPart(parts, DeterministicAgentText.ExtractionOcrTimedOut(language));
            if (TryGetBool(page, "ocrCandidate") == true)
                AddNonEmptyPart(parts, DeterministicAgentText.ExtractionOcrCandidate(language));
            if (TryGetBool(page, "manualReviewRecommended") == true)
                AddNonEmptyPart(parts, DeterministicAgentText.ExtractionReviewRecommended(language));

            var open = string.IsNullOrWhiteSpace(docPath)
                ? $"p.{pageNumber}"
                : $"[[open|{docPath}|{pageNumber}|{label}]]";
            sb.AppendLine($"- {open} - {string.Join("; ", parts.Where(static part => !string.IsNullOrWhiteSpace(part)))}");
        }

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
        var totals = data.TryGetProperty("totals", out var totalsElement) && totalsElement.ValueKind == JsonValueKind.Object
            ? totalsElement
            : default;
        var total = TryGetInt(data, "total") ?? (totals.ValueKind == JsonValueKind.Object ? TryGetInt(totals, "total") : null) ?? 0;
        var profileMissing = TryGetInt(data, "profileMissing") ?? (totals.ValueKind == JsonValueKind.Object ? TryGetInt(totals, "profileMissing") : null) ?? 0;
        var mode = TryGetString(data, "mode") ?? "missing";
        if (string.Equals(mode, "present", StringComparison.OrdinalIgnoreCase))
        {
            return total <= 0
                ? DeterministicAgentText.NoStoredSummaries(language)
                : DeterministicAgentText.StoredSummariesCount(total, language);
        }

        if (profileMissing > 0 && total > 0)
            return DeterministicAgentText.MissingSummariesCount(total, language)
                + Environment.NewLine
                + DeterministicAgentText.BackofficeProfilesMissingCount(profileMissing, language);

        return total <= 0
            ? DeterministicAgentText.NoMissingSummaries(language)
            : DeterministicAgentText.MissingSummariesCount(total, language);
    }

    private static string RenderSummaryStatusListFromReplayData(JsonElement data, string language)
    {
        var hasItems = data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array;
        if (!hasItems)
            hasItems = data.TryGetProperty("value", out items) && items.ValueKind == JsonValueKind.Array;
        if (!hasItems)
            return string.Equals(TryGetString(data, "mode"), "present", StringComparison.OrdinalIgnoreCase)
                ? DeterministicAgentText.NoStoredSummaries(language)
                : DeterministicAgentText.NoMissingSummaries(language);

        var rows = new List<(string path, string label, string suffix)>();
        foreach (var entry in items.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var path = (TryGetString(entry, "docPath") ?? string.Empty).Replace('\\', '/').TrimStart('/');
            var state = TryGetString(entry, "summaryState") ?? string.Empty;
            var profileState = TryGetString(entry, "capabilityBProfileState") ?? TryGetString(entry, "CapabilityBProfileState") ?? string.Empty;
            var hasBackofficeProfile = TryGetBool(entry, "capabilityBHasBackofficeProfile") ?? TryGetBool(entry, "CapabilityBHasBackofficeProfile");
            var profileMissing = string.Equals(profileState, "missing", StringComparison.OrdinalIgnoreCase)
                || (hasBackofficeProfile.HasValue && !hasBackofficeProfile.Value && HasReason(entry, "profile_missing"));
            if (string.IsNullOrWhiteSpace(path))
                continue;

            var label = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? path;
            label = (label ?? string.Empty).Replace("|", " ").Replace("]", ")");
            var suffixes = new List<string>();
            if (state.Equals("stale", StringComparison.OrdinalIgnoreCase))
                suffixes.Add("stale");
            if (profileMissing)
                suffixes.Add(DeterministicAgentText.BackofficeProfileMissingSuffix(language));
            if (TryGetBool(entry, "hasActiveSummaryJob") == true || TryGetBool(entry, "HasActiveSummaryJob") == true)
                suffixes.Add(DeterministicAgentText.SummaryStatusActiveJobSuffix(
                    TryGetString(entry, "activeSummaryJobStatus") ?? TryGetString(entry, "ActiveSummaryJobStatus"),
                    language));
            if (TryGetBool(entry, "capabilityBPolicyBlocked") == true || TryGetBool(entry, "CapabilityBPolicyBlocked") == true)
                suffixes.Add(DeterministicAgentText.SummaryStatusPolicyBlockedSuffix(
                    TryGetString(entry, "capabilityBPolicyBlockReason") ?? TryGetString(entry, "CapabilityBPolicyBlockReason"),
                    language));
            else if (TryGetBool(entry, "capabilityBReadyToEnqueue") == true || TryGetBool(entry, "CapabilityBReadyToEnqueue") == true)
                suffixes.Add(DeterministicAgentText.SummaryStatusCapabilityActionSuffix(
                    TryGetString(entry, "capabilityBRecommendedAction") ?? TryGetString(entry, "CapabilityBRecommendedAction"),
                    language));

            var lastJobStatus = TryGetString(entry, "capabilityBLastJobStatus") ?? TryGetString(entry, "CapabilityBLastJobStatus");
            var lastJobError = TryGetString(entry, "capabilityBLastJobError") ?? TryGetString(entry, "CapabilityBLastJobError");
            if (!string.IsNullOrWhiteSpace(lastJobStatus)
                && (lastJobStatus.Contains("fail", StringComparison.OrdinalIgnoreCase)
                    || lastJobStatus.Contains("error", StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrWhiteSpace(lastJobError)))
            {
                suffixes.Add(DeterministicAgentText.SummaryStatusLastJobIssueSuffix(lastJobStatus, lastJobError, language));
            }

            var priority = TryGetDouble(entry, "capabilityBPriorityScore") ?? TryGetDouble(entry, "CapabilityBPriorityScore");
            if (priority is > 0)
                suffixes.Add(DeterministicAgentText.SummaryStatusPrioritySuffix(priority.Value, language));

            rows.Add((path, label, string.Join(" ", suffixes.Select(static suffix => $"[{suffix}]"))));
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
            var suffix = string.IsNullOrWhiteSpace(rows[i].suffix) ? string.Empty : $" {rows[i].suffix}";
            sb.AppendLine($"{i + 1}. [[open|{rows[i].path}|1|{rows[i].label}]]{suffix}");
        }

        return sb.ToString().TrimEnd();
    }

    private static bool HasReason(JsonElement entry, string reason)
    {
        foreach (var propertyName in new[] { "capabilityBReasons", "CapabilityBReasons" })
        {
            if (!entry.TryGetProperty(propertyName, out var reasons) || reasons.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var item in reasons.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String
                    && string.Equals(item.GetString(), reason, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static void AddNonEmptyPart(List<string> parts, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            parts.Add(value.Trim());
    }

    private static string? FormatPercentPart(string label, double? value)
    {
        if (!value.HasValue)
            return null;

        var percent = value.Value <= 1.0
            ? value.Value * 100.0
            : value.Value;
        return $"{label} {Math.Round(percent)} %";
    }

    private static string? FormatCodePart(string label, string? value)
    {
        value = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(value) ? null : $"{label} {value}";
    }

    private static string? FormatPageRatioPart(int? textPages, int? pages, string? language)
    {
        if (!textPages.HasValue || !pages.HasValue || pages.Value <= 0)
            return null;

        return DeterministicAgentText.ExtractionTextPages(textPages.Value, pages.Value, language);
    }

    private static string? FormatDurationPart(string label, long? milliseconds)
    {
        if (!milliseconds.HasValue || milliseconds.Value <= 0)
            return null;

        var value = milliseconds.Value < 1000
            ? $"{milliseconds.Value.ToString(CultureInfo.InvariantCulture)}ms"
            : $"{(milliseconds.Value / 1000d).ToString(milliseconds.Value < 10000 ? "0.#" : "0", CultureInfo.InvariantCulture)}s";
        return $"{label} {value}";
    }

    private static string? FormatWordStatsPart(int? totalWords, double? averageWordsPerPage, double? textPageRatio, string? language)
    {
        if (!totalWords.HasValue || totalWords.Value <= 0)
            return null;

        return DeterministicAgentText.ExtractionWordStats(totalWords.Value, averageWordsPerPage, textPageRatio, language);
    }

    private static string? FormatArrayPart(string label, JsonElement entry, string propertyName, int maxItems, int maxChars)
    {
        if (entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty(propertyName, out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = array.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString()?.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxItems))
            .Select(value => ShortenInventoryValue(value!, maxChars))
            .ToArray();

        return values.Length == 0 ? null : $"{label} {string.Join(", ", values)}";
    }

    private static string ShortenInventoryValue(string value, int maxChars)
    {
        value = Regex.Replace(value.Trim(), @"\s+", " ");
        return value.Length <= maxChars
            ? value
            : value[..Math.Max(0, maxChars - 3)] + "...";
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
        var query = NormalizeRagQueryForRetrieval(args.GetProperty("query").GetString() ?? "");
        var topK = args.TryGetProperty("topK", out var k) ? k.GetInt32() : 8;
        var categoryScope = GetRagCategoryScopeArg(args);
        var mode = args.TryGetProperty("mode", out var m) && m.ValueKind != JsonValueKind.Null ? m.GetString() : "balanced";

        if (LooksLikeComparativeDocumentaryRequest(query))
        {
            var multiArgs = CreateJsonArgs(new
            {
                queries = BuildComparativeRetrievalQueries(query),
                topK,
                categoryPath = categoryScope,
                mode
            });
            return await ExecRagMultiSearchAsync(multiArgs, ct).ConfigureAwait(false);
        }

        var raw = await _api.RagSearchToolAsync(query, topK, categoryScope, mode, ct);
        var normalized = NormalizeRagHits(raw);
        RememberLastRagDiagnostics(new[] { query }, normalized);
        return normalized;
    }

    private async Task<JsonElement> ExecRagMultiSearchAsync(JsonElement args, CancellationToken ct)
    {
        // args: { queries: string[], topK: int, category: string|null, mode: ... }
        var topK = args.TryGetProperty("topK", out var k) ? k.GetInt32() : 8;
        var categoryScope = GetRagCategoryScopeArg(args);
        var mode = args.TryGetProperty("mode", out var m) && m.ValueKind != JsonValueKind.Null ? m.GetString() : "balanced";

        var queries = new List<string>();
        if (args.TryGetProperty("queries", out var qArr) && qArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in qArr.EnumerateArray())
            {
                if (q.ValueKind != JsonValueKind.String) continue;
                var s = NormalizeRagQueryForRetrieval(q.GetString() ?? "");
                if (!string.IsNullOrWhiteSpace(s)) queries.Add(s);
            }
        }

        // fallback: single query
        if (queries.Count == 0 && args.TryGetProperty("query", out var q1) && q1.ValueKind == JsonValueKind.String)
        {
            var s = NormalizeRagQueryForRetrieval(q1.GetString() ?? "");
            if (!string.IsNullOrWhiteSpace(s)) queries.Add(s);
        }

        queries = queries.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (queries.Count == 0)
            return JsonDocument.Parse("{\"hits\":[]}").RootElement;

        async Task<JsonElement> RunMergedSearchAsync(string? scope, bool categoryInferred)
        {
            var merged = new List<JsonElement>();
            var queryRuns = new List<object?>();
            var degradedRetrievers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            object? selectedGuidance = null;
            string? selectedGuidanceBehavior = null;
            foreach (var q in queries.Take(8))
            {
                var raw = await _api.RagSearchToolAsync(q, topK, scope, mode, ct);
                var norm = NormalizeRagHits(raw);
                CollectRagDegradedRetrievers(norm, degradedRetrievers);
                var guidance = DeserializePromptObject(norm, "guidance");
                var guidanceBehavior = norm.TryGetProperty("guidance", out var guidanceEl) && guidanceEl.ValueKind == JsonValueKind.Object
                    ? TryGetString(guidanceEl, "behavior")
                    : null;
                if (ShouldPreferMultiSearchGuidance(selectedGuidanceBehavior, guidanceBehavior))
                {
                    selectedGuidance = guidance;
                    selectedGuidanceBehavior = guidanceBehavior;
                }

                queryRuns.Add(new
                {
                    query = q,
                    guidance,
                    meta = DeserializePromptObject(norm, "meta")
                });

                if (norm.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array)
                {
                    foreach (var h in hits.EnumerateArray())
                        merged.Add(h);
                }
            }

            // Dedup by docPath + pageStart + pageEnd, keeping the richest metadata variant.
            var uniq = merged
                .GroupBy(BuildRagHitDedupeKey, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group
                    .OrderByDescending(ComputeRagHitMetadataRichness)
                    .ThenByDescending(ReadRagHitScore)
                    .First())
                .ToList();

            // Sort by score desc when present
            uniq = uniq
                .OrderByDescending(h => h.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetDouble() : 0.0)
                .Take(Math.Max(10, topK * 2))
                .ToList();

            var payload = new
            {
                hits = uniq,
                guidance = selectedGuidance,
                meta = new
                {
                    queries = queries.Take(8).ToArray(),
                    mode = (mode ?? "balanced"),
                    category = scope,
                    categoryPath = scope,
                    categoryInferred,
                    degradedRetrievers = degradedRetrievers.Count == 0 ? null : degradedRetrievers.ToArray(),
                    queryRuns
                }
            };

            return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement.Clone();
        }

        var result = await RunMergedSearchAsync(categoryScope, categoryInferred: false).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(categoryScope) && HasRagHits(result))
        {
            var inferenceResults = new ToolResults();
            inferenceResults.Items.Add(new ToolResults.Item
            {
                ToolName = "rag.multi_search",
                Result = result
            });
            var inferredCategoryScope = TryInferDominantTopLevelCategoryScope(inferenceResults, string.Join(' ', queries));
            if (!string.IsNullOrWhiteSpace(inferredCategoryScope))
            {
                var scopedResult = await RunMergedSearchAsync(inferredCategoryScope, categoryInferred: true).ConfigureAwait(false);
                if (HasRagHits(scopedResult))
                    result = scopedResult;
            }
        }

        RememberLastRagDiagnostics(queries.Take(8), result);
        return result;
    }

    private static void CollectRagDegradedRetrievers(JsonElement normalizedRagResult, ISet<string> degradedRetrievers)
    {
        if (degradedRetrievers is null)
            return;

        var meta = TryGetObject(normalizedRagResult, "meta")
                   ?? TryGetObject(normalizedRagResult, "Meta");
        if (!meta.HasValue)
            return;

        var metrics = TryGetObject(meta.Value, "metrics")
                      ?? TryGetObject(meta.Value, "Metrics");
        if (!metrics.HasValue)
            return;

        var values = TryGetArray(metrics.Value, "degradedRetrievers")
                     ?? TryGetArray(metrics.Value, "DegradedRetrievers")
                     ?? TryGetArray(metrics.Value, "degraded_retrievers");
        if (!values.HasValue)
            return;

        foreach (var value in values.Value.EnumerateArray())
        {
            var retriever = value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : value.ToString();
            if (!string.IsNullOrWhiteSpace(retriever))
                degradedRetrievers.Add(retriever.Trim());
        }
    }

    private static bool ShouldPreferMultiSearchGuidance(string? currentBehavior, string? candidateBehavior)
        => GuidancePriority(candidateBehavior) > GuidancePriority(currentBehavior);

    private static int GuidancePriority(string? behavior)
        => (behavior ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "ask_clarification" => 3,
            "answer_with_caveat" => 2,
            "" => 0,
            _ => 1
        };

    private void RememberLastRagDiagnostics(IEnumerable<string> queries, JsonElement result)
    {
        _mem.LastRagQueries = queries
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .Select(CollapseWhitespace)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("hits", out var hits)
            || hits.ValueKind != JsonValueKind.Array)
        {
            _mem.LastRagHitLabels = new();
            return;
        }

        _mem.LastRagHitLabels = hits.EnumerateArray()
            .Where(static hit => hit.ValueKind == JsonValueKind.Object)
            .Take(30)
            .Select(FormatRagDiagnosticHitLabel)
            .Where(static label => !string.IsNullOrWhiteSpace(label))
            .ToList();
    }

    private static string FormatRagDiagnosticHitLabel(JsonElement hit)
    {
        var docName = TryGetString(hit, "docName") ?? Path.GetFileName(TryGetString(hit, "docPath") ?? string.Empty);
        var pageStart = TryGetInt(hit, "pageStart") ?? 1;
        var pageEnd = TryGetInt(hit, "pageEnd") ?? pageStart;
        var score = TryGetDouble(hit, "score") ?? 0.0;
        var scoreText = score.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        return $"{docName} {SourceBackedPagePrefix(string.Empty)}{pageStart}{(pageEnd != pageStart ? $"-{pageEnd}" : string.Empty)} score={scoreText}";
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
