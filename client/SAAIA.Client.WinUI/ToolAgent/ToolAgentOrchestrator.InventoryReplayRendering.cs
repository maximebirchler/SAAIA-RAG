using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Services;

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

        var rejectedChunkDocs = TryGetInt(summary, "documentsWithRejectedChunks") ?? 0;
        var noSearchableChunkDocs = TryGetInt(summary, "documentsWithNoSearchableChunks") ?? 0;
        var retrievalReviewDocs = TryGetInt(summary, "documentsWithRetrievalReviewRecommended") ?? 0;
        if (rejectedChunkDocs > 0 || noSearchableChunkDocs > 0 || retrievalReviewDocs > 0)
        {
            sb.AppendLine(DeterministicAgentText.ExtractionRetrievalChunkSummary(
                rejectedChunkDocs,
                noSearchableChunkDocs,
                retrievalReviewDocs,
                language));
        }

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
            AddNonEmptyPart(parts, FormatRetrievalChunkQualityPart(entry, language));
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

    private static string? FormatRetrievalChunkQualityPart(JsonElement entry, string? language)
    {
        var quality = TryGetObject(entry, "retrievalChunkQuality")
                      ?? TryGetObject(entry, "RetrievalChunkQuality")
                      ?? TryGetObject(entry, "retrieval_chunk_quality");
        var total = quality.HasValue
            ? TryGetInt(quality.Value, "totalChunkCount") ?? TryGetInt(quality.Value, "TotalChunkCount")
            : TryGetInt(entry, "retrievalTotalChunkCount") ?? TryGetInt(entry, "RetrievalTotalChunkCount");
        var searchable = quality.HasValue
            ? TryGetInt(quality.Value, "searchableChunkCount") ?? TryGetInt(quality.Value, "SearchableChunkCount")
            : TryGetInt(entry, "retrievalSearchableChunkCount") ?? TryGetInt(entry, "RetrievalSearchableChunkCount");
        var rejected = quality.HasValue
            ? TryGetInt(quality.Value, "rejectedChunkCount") ?? TryGetInt(quality.Value, "RejectedChunkCount")
            : TryGetInt(entry, "retrievalRejectedChunkCount") ?? TryGetInt(entry, "RetrievalRejectedChunkCount");
        var review = (quality.HasValue
            ? TryGetBool(quality.Value, "manualReviewRecommended") ?? TryGetBool(quality.Value, "ManualReviewRecommended")
            : TryGetBool(entry, "retrievalManualReviewRecommended") ?? TryGetBool(entry, "RetrievalManualReviewRecommended")) == true;

        if (!total.HasValue && !searchable.HasValue && !rejected.HasValue && !review)
            return null;

        var reasonParts = new List<string>();
        if (quality.HasValue)
        {
            foreach (var pair in ExtractCompactIntMap(quality.Value, "rejectionReasons")
                         .Concat(ExtractCompactIntMap(quality.Value, "RejectionReasons"))
                         .GroupBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                         .Select(static group => new { Reason = group.Key, Count = group.Sum(static pair => pair.Value) })
                         .OrderByDescending(static pair => pair.Count)
                         .ThenBy(static pair => pair.Reason, StringComparer.OrdinalIgnoreCase)
                         .Take(3))
            {
                reasonParts.Add($"{ShortenInventoryValue(pair.Reason, 36)}={pair.Count}");
            }
        }

        return DeterministicAgentText.ExtractionRetrievalChunkQuality(
            total,
            searchable,
            rejected,
            review,
            reasonParts.Count == 0 ? null : string.Join(", ", reasonParts),
            language);
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
                suffixes.Add(DeterministicAgentText.SummaryStatusStaleSuffix(language));
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
}
