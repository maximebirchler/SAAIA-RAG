using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;

using SAAIA.Client.WinUI.Services;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private ToolResults.Item? TryBuildInventoryRenderedItem(ToolResults toolResults, string language, CancellationToken ct)
    {
        _ = ct;

        string kind = string.Empty;
        object? data = null;

        if (toolResults.Items.Any(x => x.ToolName == "documents.tree" && string.IsNullOrWhiteSpace(x.Error)))
        {
            kind = "tree";
            data = BuildTreeInventoryData(toolResults);
        }
        else if (toolResults.Items.Any(x => (x.ToolName is "documents.list" or "documents.search") && string.IsNullOrWhiteSpace(x.Error)))
        {
            kind = "list";
            data = BuildListInventoryData(toolResults);
        }
        else if (toolResults.Items.Any(x => x.ToolName == "documents.stats" && string.IsNullOrWhiteSpace(x.Error)))
        {
            kind = "stats";
            data = BuildStatsInventoryData(toolResults);
        }
        else if (toolResults.Items.Any(x => x.ToolName == "documents.categories" && string.IsNullOrWhiteSpace(x.Error)))
        {
            kind = "categories";
            data = BuildCategoriesInventoryData(toolResults);
        }
        else if (toolResults.Items.Any(x => (x.ToolName is "summary.status.list" or "summary.present.list" or "admin.summary.missing") && string.IsNullOrWhiteSpace(x.Error)))
        {
            kind = "summary_status_list";
            data = BuildSummaryStatusListInventoryData(toolResults);
        }
        else if (toolResults.Items.Any(x => (x.ToolName is "summary.status.count" or "summary.present.count") && string.IsNullOrWhiteSpace(x.Error)))
        {
            kind = "summary_status_count";
            data = BuildSummaryStatusCountInventoryData(toolResults);
        }
        else if (toolResults.Items.LastOrDefault(x => x.ToolName == "documents.extraction_quality" && string.IsNullOrWhiteSpace(x.Error)) is { } extractionQualityItem)
        {
            kind = "extraction_quality";
            data = BuildExtractionQualityInventoryData(toolResults);
            UpdateLastListedDocumentsFromExtractionQualityResult(extractionQualityItem.Result);
        }
        else if (toolResults.Items.Any(x => x.ToolName == "documents.extraction_pages" && string.IsNullOrWhiteSpace(x.Error)))
        {
            kind = "extraction_pages";
            data = BuildExtractionPagesInventoryData(toolResults);
        }
        else if (toolResults.Items.Any(x => x.ToolName == "documents.count" && string.IsNullOrWhiteSpace(x.Error)))
        {
            kind = "count";
            data = BuildCountInventoryData(toolResults, "documents.count");
        }
        else if (toolResults.Items.Any(x => x.ToolName == "documents.empty_count" && string.IsNullOrWhiteSpace(x.Error)))
        {
            kind = "empty_count";
            data = BuildCountInventoryData(toolResults, "documents.empty_count");
        }
        else if (toolResults.Items.Any(x => x.ToolName == "documents.empty_list" && string.IsNullOrWhiteSpace(x.Error)))
        {
            kind = "empty_list";
            data = BuildEmptyFoldersInventoryData(toolResults);
        }
        else if (toolResults.Items.Any(x => x.ToolName == "diagnostic.performance" && string.IsNullOrWhiteSpace(x.Error)))
        {
            kind = "diagnostic_performance";
            data = BuildDiagnosticPerformanceInventoryData(toolResults);
        }

        if (string.IsNullOrWhiteSpace(kind) || data is null)
            return null;

        var payload = new
        {
            kind,
            language,
            authoritative = true,
            data
        };

        if (data is not null)
        {
            _mem.LastDeterministicRender = new ToolMemory.DeterministicRenderState
            {
                Kind = kind,
                DataJson = JsonSerializer.Serialize(data),
                RouterIntent = _mem.LastRouterIntent,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
        }

        return new ToolResults.Item
        {
            ToolName = "inventory.rendered",
            Result = JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement,
            DurationMs = 0
        };
    }


    private object? BuildListInventoryData(ToolResults toolResults)
    {
        var item = toolResults.Items.LastOrDefault(x => (x.ToolName is "documents.list" or "documents.search") && string.IsNullOrWhiteSpace(x.Error));
        if (item is null)
            return null;

        try
        {
            var (docs, limit, offset, total, endOfList, dropped) = DocumentListHelper.Sanitize(item.Result, _mem);
            return new
            {
                scopePath = TryInferDocumentsScopePath(item.Result),
                limit,
                offset,
                total,
                endOfList,
                dropped,
                items = docs.Select(d => new
                {
                    pdfRef = d.PdfRef,
                    docId = d.DocId,
                    docPath = d.DocPath,
                    docName = d.DocName,
                    category = d.Category,
                    categoryPath = d.CategoryPath,
                    pages = d.Pages
                }).ToList()
            };
        }
        catch
        {
            return null;
        }
    }

    private static object? BuildTreeInventoryData(ToolResults toolResults)
    {
        var treeItem = toolResults.Items.LastOrDefault(x => x.ToolName == "documents.tree" && string.IsNullOrWhiteSpace(x.Error));
        if (treeItem is null || treeItem.Result.ValueKind != JsonValueKind.Object)
            return null;

        try
        {
            var markdown = treeItem.Result.TryGetProperty("markdown", out var md) && md.ValueKind == JsonValueKind.String
                ? (md.GetString() ?? string.Empty).Trim()
                : string.Empty;

            return new
            {
                path = TryGetString(treeItem.Result, "path") ?? string.Empty,
                depth = TryGetInt(treeItem.Result, "depth"),
                limit = TryGetInt(treeItem.Result, "limit"),
                offset = TryGetInt(treeItem.Result, "offset"),
                totalNodes = TryGetInt(treeItem.Result, "totalNodes"),
                totalDocuments = TryGetInt(treeItem.Result, "totalDocuments"),
                markdown
            };
        }
        catch
        {
            return null;
        }
    }

    private static object? BuildStatsInventoryData(ToolResults toolResults)
    {
        var item = toolResults.Items.LastOrDefault(x => x.ToolName == "documents.stats" && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return null;

        try
        {
            var rootFolders = new List<object>();
            if (item.Result.TryGetProperty("rootFolders", out var rf) && rf.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in rf.EnumerateArray())
                {
                    if (x.ValueKind != JsonValueKind.Object)
                        continue;

                    rootFolders.Add(new
                    {
                        path = TryGetString(x, "path") ?? string.Empty,
                        name = TryGetString(x, "name") ?? string.Empty,
                        totalDocuments = TryGetInt(x, "totalDocuments") ?? 0,
                        directDocuments = TryGetInt(x, "directDocuments") ?? 0,
                        subfolderCount = TryGetInt(x, "subfolderCount") ?? 0
                    });
                }
            }

            var foldersByDepth = new List<object>();
            if (item.Result.TryGetProperty("foldersByDepth", out var fd) && fd.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in fd.EnumerateArray())
                {
                    if (x.ValueKind != JsonValueKind.Object)
                        continue;

                    foldersByDepth.Add(new
                    {
                        depth = TryGetInt(x, "depth") ?? 0,
                        folderCount = TryGetInt(x, "folderCount") ?? 0
                    });
                }
            }

            return new
            {
                scopePath = TryGetString(item.Result, "scopePath") ?? string.Empty,
                totalDocuments = TryGetInt(item.Result, "totalDocuments") ?? 0,
                maxDepth = TryGetInt(item.Result, "maxDepth") ?? 0,
                totalNonEmptyFolders = TryGetInt(item.Result, "totalNonEmptyFolders") ?? 0,
                topLevelFolderCount = TryGetInt(item.Result, "topLevelFolderCount") ?? 0,
                leafFolderCount = TryGetInt(item.Result, "leafFolderCount") ?? 0,
                includesEmptyFolders = TryGetBoolProp(item.Result, "includesEmptyFolders") ?? false,
                emptyFoldersKnown = TryGetBoolProp(item.Result, "emptyFoldersKnown") ?? false,
                foldersByDepth,
                rootFolders
            };
        }
        catch
        {
            return null;
        }
    }

    private static object? BuildCategoriesInventoryData(ToolResults toolResults)
    {
        var item = toolResults.Items.LastOrDefault(x => x.ToolName == "documents.categories" && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return null;

        try
        {
            var rows = new List<object>();
            if (item.Result.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in items.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                        continue;

                    var aliases = new List<string>();
                    if (entry.TryGetProperty("aliases", out var aliasArray) && aliasArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var alias in aliasArray.EnumerateArray())
                        {
                            if (alias.ValueKind != JsonValueKind.String)
                                continue;

                            var value = alias.GetString() ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(value))
                                aliases.Add(value.Trim());
                        }
                    }

                    rows.Add(new
                    {
                        ordinal = TryGetInt(entry, "ordinal") ?? TryGetInt(entry, "displayOrder") ?? 0,
                        displayOrder = TryGetInt(entry, "displayOrder") ?? TryGetInt(entry, "ordinal") ?? 0,
                        path = TryGetString(entry, "path") ?? string.Empty,
                        name = TryGetString(entry, "name") ?? string.Empty,
                        depth = TryGetInt(entry, "depth") ?? 0,
                        totalDocuments = TryGetInt(entry, "totalDocuments") ?? 0,
                        directDocuments = TryGetInt(entry, "directDocuments") ?? 0,
                        subfolderCount = TryGetInt(entry, "subfolderCount") ?? 0,
                        aliases = aliases
                    });
                }
            }

            return new
            {
                scopePath = TryGetString(item.Result, "scopePath") ?? string.Empty,
                total = TryGetInt(item.Result, "total") ?? rows.Count,
                limit = TryGetInt(item.Result, "limit") ?? rows.Count,
                offset = TryGetInt(item.Result, "offset") ?? 0,
                items = rows
            };
        }
        catch
        {
            return null;
        }
    }

    private static object? BuildSummaryStatusCountInventoryData(ToolResults toolResults)
    {
        var item = toolResults.Items.LastOrDefault(x => (x.ToolName is "summary.status.count" or "summary.present.count") && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return null;

        var totals = item.Result.TryGetProperty("totals", out var totalsElement) && totalsElement.ValueKind == JsonValueKind.Object
            ? totalsElement
            : default;

        return new
        {
            total = TryGetInt(item.Result, "total") ?? (totals.ValueKind == JsonValueKind.Object ? TryGetInt(totals, "total") : null) ?? 0,
            missingStored = TryGetInt(item.Result, "missingStored") ?? (totals.ValueKind == JsonValueKind.Object ? TryGetInt(totals, "missingStored") : null) ?? 0,
            staleStored = TryGetInt(item.Result, "staleStored") ?? (totals.ValueKind == JsonValueKind.Object ? TryGetInt(totals, "staleStored") : null) ?? 0,
            profileMissing = TryGetInt(item.Result, "profileMissing") ?? (totals.ValueKind == JsonValueKind.Object ? TryGetInt(totals, "profileMissing") : null) ?? 0,
            scopePath = TryGetString(item.Result, "scopePath") ?? string.Empty,
            level = TryGetString(item.Result, "level") ?? "medium",
            mode = TryGetString(item.Result, "mode") ?? (string.Equals(item.ToolName, "summary.present.count", StringComparison.OrdinalIgnoreCase) ? "present" : "missing")
        };
    }

    private static object? BuildSummaryStatusListInventoryData(ToolResults toolResults)
    {
        var item = toolResults.Items.LastOrDefault(x => (x.ToolName is "summary.status.list" or "summary.present.list" or "admin.summary.missing") && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return null;

        var rows = new List<object>();
        var hasItems = item.Result.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array;
        if (!hasItems)
            hasItems = item.Result.TryGetProperty("value", out items) && items.ValueKind == JsonValueKind.Array;

        if (hasItems)
        {
            foreach (var entry in items.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;

                rows.Add(new
                {
                    docId = TryGetString(entry, "DocId") ?? TryGetString(entry, "docId") ?? string.Empty,
                    docPath = TryGetString(entry, "DocPath") ?? TryGetString(entry, "docPath") ?? string.Empty,
                    docName = TryGetString(entry, "DocName") ?? TryGetString(entry, "docName") ?? TryGetString(entry, "CanonicalName") ?? TryGetString(entry, "canonicalName") ?? string.Empty,
                    category = TryGetString(entry, "Category") ?? TryGetString(entry, "category") ?? TryGetString(entry, "CategoryCanonicalName") ?? TryGetString(entry, "categoryCanonicalName") ?? string.Empty,
                    categoryRef = TryGetString(entry, "CategoryRef") ?? TryGetString(entry, "categoryRef"),
                    categoryPath = TryGetString(entry, "CategoryPath") ?? TryGetString(entry, "categoryPath"),
                    summaryState = TryGetString(entry, "SummaryState") ?? TryGetString(entry, "summaryState") ?? "missing",
                    capabilityBProfileState = TryGetString(entry, "CapabilityBProfileState") ?? TryGetString(entry, "capabilityBProfileState"),
                    capabilityBHasBackofficeProfile = TryGetBool(entry, "CapabilityBHasBackofficeProfile") ?? TryGetBool(entry, "capabilityBHasBackofficeProfile") ?? false,
                    capabilityBReasons = ExtractCompactSignals(entry, "CapabilityBReasons").Concat(ExtractCompactSignals(entry, "capabilityBReasons")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    hasActiveSummaryJob = TryGetBool(entry, "HasActiveSummaryJob") ?? TryGetBool(entry, "hasActiveSummaryJob") ?? false,
                    activeSummaryJobId = TryGetString(entry, "ActiveSummaryJobId") ?? TryGetString(entry, "activeSummaryJobId"),
                    activeSummaryJobType = TryGetString(entry, "ActiveSummaryJobType") ?? TryGetString(entry, "activeSummaryJobType"),
                    activeSummaryJobStatus = TryGetString(entry, "ActiveSummaryJobStatus") ?? TryGetString(entry, "activeSummaryJobStatus"),
                    activeSummaryJobExecutionMode = TryGetString(entry, "ActiveSummaryJobExecutionMode") ?? TryGetString(entry, "activeSummaryJobExecutionMode"),
                    activeSummaryJobRuntimeCapabilityKey = TryGetString(entry, "ActiveSummaryJobRuntimeCapabilityKey") ?? TryGetString(entry, "activeSummaryJobRuntimeCapabilityKey"),
                    activeSummaryJobRuntimeCapabilityStatus = TryGetString(entry, "ActiveSummaryJobRuntimeCapabilityStatus") ?? TryGetString(entry, "activeSummaryJobRuntimeCapabilityStatus"),
                    activeSummaryJobEnqueueSource = TryGetString(entry, "ActiveSummaryJobEnqueueSource") ?? TryGetString(entry, "activeSummaryJobEnqueueSource"),
                    activeSummaryJobCampaignId = TryGetString(entry, "ActiveSummaryJobCampaignId") ?? TryGetString(entry, "activeSummaryJobCampaignId"),
                    capabilityBReadyToEnqueue = TryGetBool(entry, "CapabilityBReadyToEnqueue") ?? TryGetBool(entry, "capabilityBReadyToEnqueue") ?? false,
                    capabilityBRecommendedAction = TryGetString(entry, "CapabilityBRecommendedAction") ?? TryGetString(entry, "capabilityBRecommendedAction"),
                    capabilityBPolicyBlocked = TryGetBool(entry, "CapabilityBPolicyBlocked") ?? TryGetBool(entry, "capabilityBPolicyBlocked") ?? false,
                    capabilityBPolicyBlockReason = TryGetString(entry, "CapabilityBPolicyBlockReason") ?? TryGetString(entry, "capabilityBPolicyBlockReason"),
                    capabilityBPriorityScore = TryGetDouble(entry, "CapabilityBPriorityScore") ?? TryGetDouble(entry, "capabilityBPriorityScore"),
                    capabilityBLastJobStatus = TryGetString(entry, "CapabilityBLastJobStatus") ?? TryGetString(entry, "capabilityBLastJobStatus"),
                    capabilityBLastJobFinishedAt = TryGetString(entry, "CapabilityBLastJobFinishedAt") ?? TryGetString(entry, "capabilityBLastJobFinishedAt"),
                    capabilityBLastJobError = TryGetString(entry, "CapabilityBLastJobError") ?? TryGetString(entry, "capabilityBLastJobError")
                });
            }
        }

        var totals = item.Result.TryGetProperty("totals", out var totalsElement) && totalsElement.ValueKind == JsonValueKind.Object
            ? totalsElement
            : default;
        var mode = TryGetString(item.Result, "mode") ?? (string.Equals(item.ToolName, "summary.present.list", StringComparison.OrdinalIgnoreCase) ? "present" : "missing");

        return new
        {
            total = TryGetInt(item.Result, "total") ?? (totals.ValueKind == JsonValueKind.Object ? TryGetInt(totals, "total") : null) ?? rows.Count,
            missingStored = TryGetInt(item.Result, "missingStored") ?? (totals.ValueKind == JsonValueKind.Object ? TryGetInt(totals, "missingStored") : null) ?? 0,
            staleStored = TryGetInt(item.Result, "staleStored") ?? (totals.ValueKind == JsonValueKind.Object ? TryGetInt(totals, "staleStored") : null) ?? 0,
            profileMissing = TryGetInt(item.Result, "profileMissing") ?? (totals.ValueKind == JsonValueKind.Object ? TryGetInt(totals, "profileMissing") : null) ?? 0,
            limit = TryGetInt(item.Result, "limit") ?? rows.Count,
            offset = TryGetInt(item.Result, "offset") ?? 0,
            scopePath = TryGetString(item.Result, "scopePath") ?? string.Empty,
            level = TryGetString(item.Result, "level") ?? "medium",
            mode,
            endOfList = TryGetBool(item.Result, "endOfList"),
            nextLink = TryGetString(item.Result, "nextLink"),
            items = rows
        };
    }

    private static object? BuildExtractionQualityInventoryData(ToolResults toolResults)
    {
        var item = toolResults.Items.LastOrDefault(x => x.ToolName == "documents.extraction_quality" && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return null;

        try
        {
            var summary = item.Result.TryGetProperty("summary", out var summaryElement) && summaryElement.ValueKind == JsonValueKind.Object
                ? summaryElement
                : default;
            var rows = new List<object>();
            if (item.Result.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in items.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                        continue;

                    var signals = ExtractCompactSignals(entry, "signals")
                        .Concat(ExtractCompactSignals(entry, "Signals"))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(8)
                        .ToArray();
                    var retrievalQuality = TryGetObject(entry, "retrievalChunkQuality")
                                           ?? TryGetObject(entry, "RetrievalChunkQuality")
                                           ?? TryGetObject(entry, "retrieval_chunk_quality");
                    object? retrievalChunkQuality = null;
                    if (retrievalQuality.HasValue)
                    {
                        var rejectionReasons = ExtractCompactIntMap(retrievalQuality.Value, "rejectionReasons")
                            .Concat(ExtractCompactIntMap(retrievalQuality.Value, "RejectionReasons"))
                            .GroupBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(
                                static group => group.Key,
                                static group => group.Sum(static pair => pair.Value),
                                StringComparer.OrdinalIgnoreCase);
                        retrievalChunkQuality = new
                        {
                            totalChunkCount = TryGetInt(retrievalQuality.Value, "totalChunkCount") ?? TryGetInt(retrievalQuality.Value, "TotalChunkCount"),
                            searchableChunkCount = TryGetInt(retrievalQuality.Value, "searchableChunkCount") ?? TryGetInt(retrievalQuality.Value, "SearchableChunkCount"),
                            rejectedChunkCount = TryGetInt(retrievalQuality.Value, "rejectedChunkCount") ?? TryGetInt(retrievalQuality.Value, "RejectedChunkCount"),
                            manualReviewRecommended = TryGetBool(retrievalQuality.Value, "manualReviewRecommended") ?? TryGetBool(retrievalQuality.Value, "ManualReviewRecommended"),
                            rejectionReasons
                        };
                    }

                    rows.Add(new
                    {
                        docId = TryGetString(entry, "docId") ?? TryGetString(entry, "DocId") ?? string.Empty,
                        docPath = (TryGetString(entry, "docPath") ?? TryGetString(entry, "DocPath") ?? string.Empty).Replace('\\', '/').TrimStart('/'),
                        documentStatus = TryGetString(entry, "documentStatus") ?? TryGetString(entry, "DocumentStatus") ?? string.Empty,
                        processingRunStatus = TryGetString(entry, "processingRunStatus") ?? TryGetString(entry, "ProcessingRunStatus") ?? string.Empty,
                        documentIndexable = TryGetBool(entry, "documentIndexable") ?? TryGetBool(entry, "DocumentIndexable") ?? true,
                        failureReason = TryGetString(entry, "failureReason") ?? TryGetString(entry, "FailureReason") ?? string.Empty,
                        ocrFailureReason = TryGetString(entry, "ocrFailureReason") ?? TryGetString(entry, "OcrFailureReason") ?? TryGetString(entry, "OCRFailureReason") ?? string.Empty,
                        qualityStatus = TryGetString(entry, "qualityStatus") ?? TryGetString(entry, "QualityStatus") ?? string.Empty,
                        extractionConfidence = TryGetDouble(entry, "extractionConfidence") ?? TryGetDouble(entry, "ExtractionConfidence"),
                        manualReviewRecommended = TryGetBool(entry, "manualReviewRecommended") ?? TryGetBool(entry, "ManualReviewRecommended") ?? false,
                        extractionSource = TryGetString(entry, "extractionSource") ?? TryGetString(entry, "ExtractionSource") ?? string.Empty,
                        ocrAttempted = TryGetBool(entry, "ocrAttempted") ?? TryGetBool(entry, "OcrAttempted") ?? false,
                        ocrApplied = TryGetBool(entry, "ocrApplied") ?? TryGetBool(entry, "OcrApplied") ?? false,
                        ocrRecommended = TryGetBool(entry, "ocrRecommended") ?? TryGetBool(entry, "OcrRecommended") ?? false,
                        ocrLanguages = TryGetString(entry, "ocrLanguages") ?? TryGetString(entry, "OcrLanguages") ?? string.Empty,
                        ocrDurationMs = TryGetLong(entry, "ocrDurationMs") ?? TryGetLong(entry, "OcrDurationMs"),
                        nativeTextStatus = TryGetString(entry, "nativeTextStatus") ?? TryGetString(entry, "NativeTextStatus") ?? string.Empty,
                        nativeOcrRecommended = TryGetBool(entry, "nativeOcrRecommended") ?? TryGetBool(entry, "NativeOcrRecommended"),
                        textStatus = TryGetString(entry, "textStatus") ?? TryGetString(entry, "TextStatus") ?? string.Empty,
                        pageCount = TryGetInt(entry, "pageCount") ?? TryGetInt(entry, "PageCount") ?? 0,
                        textPageCount = TryGetInt(entry, "textPageCount") ?? TryGetInt(entry, "TextPageCount") ?? 0,
                        emptyPageCount = TryGetInt(entry, "emptyPageCount") ?? TryGetInt(entry, "EmptyPageCount") ?? 0,
                        sparsePageCount = TryGetInt(entry, "sparsePageCount") ?? TryGetInt(entry, "SparsePageCount") ?? 0,
                        imagePageCount = TryGetInt(entry, "imagePageCount") ?? TryGetInt(entry, "ImagePageCount") ?? 0,
                        pageWarningCount = TryGetInt(entry, "pageWarningCount") ?? TryGetInt(entry, "PageWarningCount") ?? 0,
                        pageReviewRecommendedCount = TryGetInt(entry, "pageReviewRecommendedCount") ?? TryGetInt(entry, "PageReviewRecommendedCount") ?? 0,
                        retrievalChunkQuality,
                        retrievalTotalChunkCount = TryGetInt(entry, "retrievalTotalChunkCount") ?? TryGetInt(entry, "RetrievalTotalChunkCount"),
                        retrievalSearchableChunkCount = TryGetInt(entry, "retrievalSearchableChunkCount") ?? TryGetInt(entry, "RetrievalSearchableChunkCount"),
                        retrievalRejectedChunkCount = TryGetInt(entry, "retrievalRejectedChunkCount") ?? TryGetInt(entry, "RetrievalRejectedChunkCount"),
                        retrievalManualReviewRecommended = TryGetBool(entry, "retrievalManualReviewRecommended") ?? TryGetBool(entry, "RetrievalManualReviewRecommended"),
                        totalWordCount = TryGetInt(entry, "totalWordCount") ?? TryGetInt(entry, "TotalWordCount"),
                        totalCharCount = TryGetInt(entry, "totalCharCount") ?? TryGetInt(entry, "TotalCharCount"),
                        averageWordsPerPage = TryGetDouble(entry, "averageWordsPerPage") ?? TryGetDouble(entry, "AverageWordsPerPage"),
                        textPageRatio = TryGetDouble(entry, "textPageRatio") ?? TryGetDouble(entry, "TextPageRatio"),
                        signals
                    });
                }
            }

            var categories = new List<object>();
            if (item.Result.TryGetProperty("categories", out var categoryItems) && categoryItems.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in categoryItems.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                        continue;

                    var categoryPath = (TryGetString(entry, "categoryPath") ?? TryGetString(entry, "CategoryPath") ?? string.Empty)
                        .Replace('\\', '/')
                        .Trim('/');
                    if (string.IsNullOrWhiteSpace(categoryPath))
                        continue;

                    categories.Add(new
                    {
                        categoryPath,
                        totalDocuments = TryGetInt(entry, "totalDocuments") ?? TryGetInt(entry, "TotalDocuments") ?? 0,
                        okDocuments = TryGetInt(entry, "okDocuments") ?? TryGetInt(entry, "OkDocuments") ?? 0,
                        lowTextDocuments = TryGetInt(entry, "lowTextDocuments") ?? TryGetInt(entry, "LowTextDocuments") ?? 0,
                        emptyTextDocuments = TryGetInt(entry, "emptyTextDocuments") ?? TryGetInt(entry, "EmptyTextDocuments") ?? 0,
                        unknownDocuments = TryGetInt(entry, "unknownDocuments") ?? TryGetInt(entry, "UnknownDocuments") ?? 0,
                        ocrRecommendedDocuments = TryGetInt(entry, "ocrRecommendedDocuments") ?? TryGetInt(entry, "OcrRecommendedDocuments") ?? 0,
                        ocrAppliedDocuments = TryGetInt(entry, "ocrAppliedDocuments") ?? TryGetInt(entry, "OcrAppliedDocuments") ?? 0,
                        manualReviewRecommendedDocuments = TryGetInt(entry, "manualReviewRecommendedDocuments") ?? TryGetInt(entry, "ManualReviewRecommendedDocuments") ?? 0,
                        pageReviewRecommendedPages = TryGetInt(entry, "pageReviewRecommendedPages") ?? TryGetInt(entry, "PageReviewRecommendedPages") ?? 0,
                        pageWarningPages = TryGetInt(entry, "pageWarningPages") ?? TryGetInt(entry, "PageWarningPages") ?? 0,
                        documentsWithRejectedChunks = TryGetInt(entry, "documentsWithRejectedChunks") ?? TryGetInt(entry, "DocumentsWithRejectedChunks") ?? 0,
                        documentsWithNoSearchableChunks = TryGetInt(entry, "documentsWithNoSearchableChunks") ?? TryGetInt(entry, "DocumentsWithNoSearchableChunks") ?? 0,
                        documentsWithRetrievalReviewRecommended = TryGetInt(entry, "documentsWithRetrievalReviewRecommended") ?? TryGetInt(entry, "DocumentsWithRetrievalReviewRecommended") ?? 0
                    });
                }
            }

            return new
            {
                scopePath = TryGetString(item.Result, "scopePath") ?? string.Empty,
                limit = TryGetInt(item.Result, "limit") ?? rows.Count,
                summary = new
                {
                    totalDocuments = TryGetInt(summary, "totalDocuments") ?? 0,
                    okDocuments = TryGetInt(summary, "okDocuments") ?? 0,
                    lowTextDocuments = TryGetInt(summary, "lowTextDocuments") ?? 0,
                    emptyTextDocuments = TryGetInt(summary, "emptyTextDocuments") ?? 0,
                    unknownDocuments = TryGetInt(summary, "unknownDocuments") ?? 0,
                    ocrRecommendedDocuments = TryGetInt(summary, "ocrRecommendedDocuments") ?? 0,
                    ocrAttemptedDocuments = TryGetInt(summary, "ocrAttemptedDocuments") ?? 0,
                    ocrAppliedDocuments = TryGetInt(summary, "ocrAppliedDocuments") ?? 0,
                    manualReviewRecommendedDocuments = TryGetInt(summary, "manualReviewRecommendedDocuments") ?? 0,
                    pageReviewRecommendedDocuments = TryGetInt(summary, "pageReviewRecommendedDocuments") ?? 0,
                    pageReviewRecommendedPages = TryGetInt(summary, "pageReviewRecommendedPages") ?? 0,
                    pageWarningDocuments = TryGetInt(summary, "pageWarningDocuments") ?? 0,
                    pageWarningPages = TryGetInt(summary, "pageWarningPages") ?? 0,
                    documentsWithRejectedChunks = TryGetInt(summary, "documentsWithRejectedChunks") ?? 0,
                    documentsWithNoSearchableChunks = TryGetInt(summary, "documentsWithNoSearchableChunks") ?? 0,
                    documentsWithRetrievalReviewRecommended = TryGetInt(summary, "documentsWithRetrievalReviewRecommended") ?? 0,
                    llmEnrichmentPendingDocuments = TryGetInt(summary, "llmEnrichmentPendingDocuments") ?? 0,
                    summaryEnrichmentPendingDocuments = TryGetInt(summary, "summaryEnrichmentPendingDocuments") ?? 0,
                    profileEnrichmentPendingDocuments = TryGetInt(summary, "profileEnrichmentPendingDocuments") ?? 0,
                    contentCardEvidencePendingDocuments = TryGetInt(summary, "contentCardEvidencePendingDocuments") ?? 0
                },
                categories,
                items = rows
            };
        }
        catch
        {
            return null;
        }
    }

    private static object? BuildExtractionPagesInventoryData(ToolResults toolResults)
    {
        var item = toolResults.Items.LastOrDefault(x => x.ToolName == "documents.extraction_pages" && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return null;

        try
        {
            var summary = item.Result.TryGetProperty("summary", out var summaryElement) && summaryElement.ValueKind == JsonValueKind.Object
                ? summaryElement
                : default;
            var rows = new List<object>();
            if (item.Result.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in pages.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                        continue;

                    var signals = ExtractCompactSignals(entry, "signals")
                        .Concat(ExtractCompactSignals(entry, "Signals"))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(8)
                        .ToArray();
                    var unitPreviews = ExtractCompactSignals(entry, "unitPreviews")
                        .Concat(ExtractCompactSignals(entry, "UnitPreviews"))
                        .Distinct(StringComparer.Ordinal)
                        .Take(3)
                        .ToArray();
                    var chunkPreviews = ExtractCompactSignals(entry, "chunkPreviews")
                        .Concat(ExtractCompactSignals(entry, "ChunkPreviews"))
                        .Distinct(StringComparer.Ordinal)
                        .Take(3)
                        .ToArray();
                    rows.Add(new
                    {
                        pageNumber = TryGetInt(entry, "pageNumber") ?? TryGetInt(entry, "PageNumber") ?? 0,
                        qualityStatus = TryGetString(entry, "qualityStatus") ?? TryGetString(entry, "QualityStatus") ?? string.Empty,
                        extractionConfidence = TryGetDouble(entry, "extractionConfidence") ?? TryGetDouble(entry, "ExtractionConfidence"),
                        manualReviewRecommended = TryGetBool(entry, "manualReviewRecommended") ?? TryGetBool(entry, "ManualReviewRecommended") ?? false,
                        charCount = TryGetInt(entry, "charCount") ?? TryGetInt(entry, "CharCount") ?? 0,
                        wordCount = TryGetInt(entry, "wordCount") ?? TryGetInt(entry, "WordCount") ?? 0,
                        imageCount = TryGetInt(entry, "imageCount") ?? TryGetInt(entry, "ImageCount") ?? 0,
                        unitCount = TryGetInt(entry, "unitCount") ?? TryGetInt(entry, "UnitCount") ?? 0,
                        suspiciousUnitCount = TryGetInt(entry, "suspiciousUnitCount") ?? TryGetInt(entry, "SuspiciousUnitCount") ?? 0,
                        chunkCount = TryGetInt(entry, "chunkCount") ?? TryGetInt(entry, "ChunkCount") ?? 0,
                        textStatus = TryGetString(entry, "textStatus") ?? TryGetString(entry, "TextStatus") ?? string.Empty,
                        textEmpty = TryGetBool(entry, "textEmpty") ?? TryGetBool(entry, "TextEmpty") ?? false,
                        textSparse = TryGetBool(entry, "textSparse") ?? TryGetBool(entry, "TextSparse") ?? false,
                        ocrCandidate = TryGetBool(entry, "ocrCandidate") ?? TryGetBool(entry, "OcrCandidate") ?? false,
                        imageOcrStatus = TryGetString(entry, "imageOcrStatus") ?? TryGetString(entry, "ImageOcrStatus") ?? string.Empty,
                        imageOcrReason = TryGetString(entry, "imageOcrReason") ?? TryGetString(entry, "ImageOcrReason") ?? string.Empty,
                        imageOcrWordCount = TryGetInt(entry, "imageOcrWordCount") ?? TryGetInt(entry, "ImageOcrWordCount"),
                        imageOcrCharCount = TryGetInt(entry, "imageOcrCharCount") ?? TryGetInt(entry, "ImageOcrCharCount"),
                        imageOcrExitCode = TryGetInt(entry, "imageOcrExitCode") ?? TryGetInt(entry, "ImageOcrExitCode"),
                        imageOcrTimedOut = TryGetBool(entry, "imageOcrTimedOut") ?? TryGetBool(entry, "ImageOcrTimedOut"),
                        averageCharsPerWord = TryGetDouble(entry, "averageCharsPerWord") ?? TryGetDouble(entry, "AverageCharsPerWord"),
                        signals,
                        unitPreviews,
                        chunkPreviews
                    });
                }
            }

            return new
            {
                docId = TryGetString(item.Result, "docId") ?? TryGetString(item.Result, "DocId") ?? string.Empty,
                docPath = (TryGetString(item.Result, "docPath") ?? TryGetString(item.Result, "DocPath") ?? string.Empty).Replace('\\', '/').TrimStart('/'),
                documentStatus = TryGetString(item.Result, "documentStatus") ?? TryGetString(item.Result, "DocumentStatus") ?? string.Empty,
                processingRunStatus = TryGetString(item.Result, "processingRunStatus") ?? TryGetString(item.Result, "ProcessingRunStatus") ?? string.Empty,
                documentIndexable = TryGetBool(item.Result, "documentIndexable") ?? TryGetBool(item.Result, "DocumentIndexable") ?? true,
                failureReason = TryGetString(item.Result, "failureReason") ?? TryGetString(item.Result, "FailureReason") ?? string.Empty,
                ocrFailureReason =
                    TryGetString(item.Result, "ocrFailureReason")
                    ?? TryGetString(item.Result, "OcrFailureReason")
                    ?? TryGetString(item.Result, "OCRFailureReason")
                    ?? TryGetOcrDiagnosticsString(item.Result, "failureReason"),
                ocrAppliedReason = TryGetOcrDiagnosticsString(item.Result, "appliedReason"),
                ocrMode = TryGetOcrDiagnosticsString(item.Result, "mode"),
                indexedVersion = TryGetInt(item.Result, "indexedVersion") ?? TryGetInt(item.Result, "IndexedVersion"),
                extractionSource = TryGetString(item.Result, "extractionSource") ?? TryGetString(item.Result, "ExtractionSource") ?? string.Empty,
                ocrAttempted = TryGetBool(item.Result, "ocrAttempted") ?? TryGetBool(item.Result, "OcrAttempted") ?? false,
                ocrApplied = TryGetBool(item.Result, "ocrApplied") ?? TryGetBool(item.Result, "OcrApplied") ?? false,
                ocrLanguages = TryGetString(item.Result, "ocrLanguages") ?? TryGetString(item.Result, "OcrLanguages") ?? string.Empty,
                ocrDurationMs = TryGetLong(item.Result, "ocrDurationMs") ?? TryGetLong(item.Result, "OcrDurationMs"),
                summary = new
                {
                    pageCount = TryGetInt(summary, "pageCount") ?? 0,
                    manualReviewRecommendedPages = TryGetInt(summary, "manualReviewRecommendedPages") ?? 0,
                    probableOcrNoisePages = TryGetInt(summary, "probableOcrNoisePages") ?? 0,
                    emptyTextPages = TryGetInt(summary, "emptyTextPages") ?? 0,
                    lowTextPages = TryGetInt(summary, "lowTextPages") ?? 0,
                    indexedByContextPages = TryGetInt(summary, "indexedByContextPages") ?? 0,
                    imagePages = TryGetInt(summary, "imagePages") ?? 0
                },
                pages = rows
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetOcrDiagnosticsString(JsonElement root, string propertyName)
    {
        var diagnostics = TryGetObject(root, "ocrDiagnostics") ?? TryGetObject(root, "OcrDiagnostics");
        return diagnostics is null
            ? null
            : TryGetString(diagnostics.Value, propertyName);
    }

    private static object? BuildCountInventoryData(ToolResults toolResults, string toolName)
    {
        var item = toolResults.Items.LastOrDefault(x => x.ToolName == toolName && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return null;

        return new
        {
            total = TryGetInt(item.Result, "total") ?? 0
        };
    }

    private static object? BuildEmptyFoldersInventoryData(ToolResults toolResults)
    {
        var item = toolResults.Items.LastOrDefault(x => x.ToolName == "documents.empty_list" && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return null;

        try
        {
            var paths = new List<object>();
            if (item.Result.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in arr.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                        continue;

                    var path = TryGetString(entry, "path") ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(path))
                        paths.Add(new { path });
                }
            }

            return new
            {
                total = paths.Count,
                items = paths
            };
        }
        catch
        {
            return null;
        }
    }

    private static object? BuildDiagnosticPerformanceInventoryData(ToolResults toolResults)
    {
        var item = toolResults.Items.LastOrDefault(x => x.ToolName == "diagnostic.performance" && string.IsNullOrWhiteSpace(x.Error));
        if (item is null || item.Result.ValueKind != JsonValueKind.Object)
            return null;

        try
        {
            var summary = item.Result.TryGetProperty("memorySummary", out var memorySummary) && memorySummary.ValueKind == JsonValueKind.Object
                ? memorySummary
                : item.Result;

            var workspace = summary.TryGetProperty("workspace", out var workspaceEl) && workspaceEl.ValueKind == JsonValueKind.Object
                ? workspaceEl
                : default;
            var session = summary.TryGetProperty("session", out var sessionEl) && sessionEl.ValueKind == JsonValueKind.Object
                ? sessionEl
                : default;
            var execution = summary.TryGetProperty("execution", out var executionEl) && executionEl.ValueKind == JsonValueKind.Object
                ? executionEl
                : default;
            var persistence = summary.TryGetProperty("persistence", out var persistenceEl) && persistenceEl.ValueKind == JsonValueKind.Object
                ? persistenceEl
                : default;
            var resetPolicy = summary.TryGetProperty("resetPolicy", out var resetEl) && resetEl.ValueKind == JsonValueKind.Object
                ? resetEl
                : default;

            return new
            {
                profile = TryGetString(summary, "profile") ?? "unknown",
                schemaVersion = TryGetInt(summary, "schemaVersion") ?? 0,
                cdcAlignment = TryGetString(summary, "cdcAlignment") ?? "v3.1",
                routerMs = TryGetInt(item.Result, "routerMs") ?? 0,
                toolsMs = TryGetInt(item.Result, "toolsMs") ?? 0,
                writerMs = TryGetInt(item.Result, "writerMs") ?? 0,
                totalMs = TryGetInt(item.Result, "totalMs") ?? 0,
                workspace = new
                {
                    catalogCategoriesCount = TryGetInt(workspace, "catalogCategoriesCount") ?? 0,
                    knownDocumentsCount = TryGetInt(workspace, "knownDocumentsCount") ?? 0,
                    hasCapabilitiesSnapshot = TryGetBoolProp(workspace, "hasCapabilitiesSnapshot") ?? false
                },
                session = new
                {
                    hasFocusedDocument = TryGetBoolProp(session, "hasFocusedDocument") ?? false,
                    lastListedDocumentsCount = TryGetInt(session, "lastListedDocumentsCount") ?? 0,
                    hasResolvedCategory = TryGetBoolProp(session, "hasResolvedCategory") ?? false,
                    hasPendingClarification = TryGetBoolProp(session, "hasPendingClarification") ?? false
                },
                execution = new
                {
                    mode = TryGetString(execution, "mode") ?? "auto",
                    hasRouterIntent = TryGetBoolProp(execution, "hasRouterIntent") ?? false,
                    toolNamesCount = TryGetInt(execution, "toolNamesCount") ?? 0,
                    hasAdminOperation = TryGetBoolProp(execution, "hasAdminOperation") ?? false
                },
                persistence = new
                {
                    language = TryGetBoolProp(persistence, "language") ?? false,
                    style = TryGetBoolProp(persistence, "style") ?? false,
                    mode = TryGetBoolProp(persistence, "mode") ?? false,
                    focusedDocument = TryGetBoolProp(persistence, "focusedDocument") ?? false,
                    resolvedCategory = TryGetBoolProp(persistence, "resolvedCategory") ?? false
                },
                resetPolicy = new
                {
                    preservesM1Lite = TryGetBoolProp(resetPolicy, "preservesM1Lite") ?? false,
                    preservesPreferences = TryGetBoolProp(resetPolicy, "preservesPreferences") ?? false,
                    clearsM3 = TryGetBoolProp(resetPolicy, "clearsM3") ?? false,
                    clearsM6 = TryGetBoolProp(resetPolicy, "clearsM6") ?? false,
                    resetsModeToAuto = TryGetBoolProp(resetPolicy, "resetsModeToAuto") ?? false
                }
            };
        }
        catch
        {
            return null;
        }
    }

}
