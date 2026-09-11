using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private static ToolMemory.SourceExtractionDiagnosticRef? TryBuildSourceExtractionDiagnosticRef(JsonElement root, JsonElement? qualityElement)
    {
        var diagnostic = TryGetExtractionDiagnosticSummaryObject(root, qualityElement);
        if (!diagnostic.HasValue)
            return null;

        return BuildSourceExtractionDiagnosticRefFromElement(diagnostic.Value);
    }

    private static ToolMemory.SourceExtractionDiagnosticRef? BuildSourceExtractionDiagnosticRefFromElement(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return null;

        var summary = new ToolMemory.SourceExtractionDiagnosticRef
        {
            NativeTextStatus = NullIfWhiteSpace(TryGetString(value, "nativeTextStatus") ?? TryGetString(value, "native_text_status") ?? TryGetString(value, "NativeTextStatus")),
            NativeOcrRecommended = TryGetBool(value, "nativeOcrRecommended") ?? TryGetBool(value, "native_ocr_recommended") ?? TryGetBool(value, "NativeOcrRecommended"),
            OcrMode = NullIfWhiteSpace(TryGetString(value, "ocrMode") ?? TryGetString(value, "ocr_mode") ?? TryGetString(value, "OcrMode") ?? TryGetString(value, "OCRMode")),
            OcrLanguages = NullIfWhiteSpace(TryGetString(value, "ocrLanguages") ?? TryGetString(value, "ocr_languages") ?? TryGetString(value, "OcrLanguages") ?? TryGetString(value, "OCRLanguages")),
            OcrDurationMs = TryGetLong(value, "ocrDurationMs") ?? TryGetLong(value, "ocr_duration_ms") ?? TryGetLong(value, "OcrDurationMs") ?? TryGetLong(value, "OCRDurationMs"),
            OcrFailureReason = NullIfWhiteSpace(TryGetString(value, "ocrFailureReason") ?? TryGetString(value, "ocr_failure_reason") ?? TryGetString(value, "OcrFailureReason") ?? TryGetString(value, "OCRFailureReason")),
            OcrAppliedReason = NullIfWhiteSpace(TryGetString(value, "ocrAppliedReason") ?? TryGetString(value, "ocr_applied_reason") ?? TryGetString(value, "OcrAppliedReason") ?? TryGetString(value, "OCRAppliedReason")),
            OcrTimedOut = TryGetBool(value, "ocrTimedOut") ?? TryGetBool(value, "ocr_timed_out") ?? TryGetBool(value, "OcrTimedOut") ?? TryGetBool(value, "OCRTimedOut"),
            OcrAttemptedPageCount = TryGetInt(value, "ocrAttemptedPageCount") ?? TryGetInt(value, "ocr_attempted_page_count") ?? TryGetInt(value, "OcrAttemptedPageCount") ?? TryGetInt(value, "OCRAttemptedPageCount"),
            OcrSkippedPageCount = TryGetInt(value, "ocrSkippedPageCount") ?? TryGetInt(value, "ocr_skipped_page_count") ?? TryGetInt(value, "OcrSkippedPageCount") ?? TryGetInt(value, "OCRSkippedPageCount"),
            OcrPagesWithNovelTextCount = TryGetInt(value, "ocrPagesWithNovelTextCount") ?? TryGetInt(value, "ocr_pages_with_novel_text_count") ?? TryGetInt(value, "OcrPagesWithNovelTextCount") ?? TryGetInt(value, "OCRPagesWithNovelTextCount"),
            PageCount = TryGetInt(value, "pageCount") ?? TryGetInt(value, "page_count") ?? TryGetInt(value, "PageCount"),
            TextPageCount = TryGetInt(value, "textPageCount") ?? TryGetInt(value, "text_page_count") ?? TryGetInt(value, "TextPageCount"),
            EmptyPageCount = TryGetInt(value, "emptyPageCount") ?? TryGetInt(value, "empty_page_count") ?? TryGetInt(value, "EmptyPageCount"),
            SparsePageCount = TryGetInt(value, "sparsePageCount") ?? TryGetInt(value, "sparse_page_count") ?? TryGetInt(value, "SparsePageCount"),
            ImagePageCount = TryGetInt(value, "imagePageCount") ?? TryGetInt(value, "image_page_count") ?? TryGetInt(value, "ImagePageCount"),
            PageWarningCount = TryGetInt(value, "pageWarningCount") ?? TryGetInt(value, "page_warning_count") ?? TryGetInt(value, "PageWarningCount"),
            PageReviewRecommendedCount = TryGetInt(value, "pageReviewRecommendedCount") ?? TryGetInt(value, "page_review_recommended_count") ?? TryGetInt(value, "PageReviewRecommendedCount"),
            RetrievalChunkQuality = TryBuildSourceRetrievalChunkQualityRef(value)
        };

        return HasSourceExtractionDiagnosticValue(summary) ? summary : null;
    }

    private static ToolMemory.SourceRetrievalChunkQualityRef? TryBuildSourceRetrievalChunkQualityRef(JsonElement value)
    {
        var quality = TryGetObject(value, "retrievalChunkQuality")
                      ?? TryGetObject(value, "retrieval_chunk_quality")
                      ?? TryGetObject(value, "RetrievalChunkQuality");
        if (!quality.HasValue)
            return null;

        var root = quality.Value;
        var summary = new ToolMemory.SourceRetrievalChunkQualityRef
        {
            TotalChunkCount = NonNegativeOrNull(TryGetInt(root, "totalChunkCount") ?? TryGetInt(root, "total_chunk_count") ?? TryGetInt(root, "TotalChunkCount")),
            SearchableChunkCount = NonNegativeOrNull(TryGetInt(root, "searchableChunkCount") ?? TryGetInt(root, "searchable_chunk_count") ?? TryGetInt(root, "SearchableChunkCount")),
            RejectedChunkCount = NonNegativeOrNull(TryGetInt(root, "rejectedChunkCount") ?? TryGetInt(root, "rejected_chunk_count") ?? TryGetInt(root, "RejectedChunkCount")),
            ManualReviewRecommended = TryGetBool(root, "manualReviewRecommended") ?? TryGetBool(root, "manual_review_recommended") ?? TryGetBool(root, "ManualReviewRecommended"),
            RejectionReasons = ReadSourceRetrievalChunkRejectionReasons(root)
        };

        return HasSourceRetrievalChunkQualityValue(summary) ? summary : null;
    }

    private static Dictionary<string, int> ReadSourceRetrievalChunkRejectionReasons(JsonElement root)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var reasons = TryGetObject(root, "rejectionReasons")
                      ?? TryGetObject(root, "rejection_reasons")
                      ?? TryGetObject(root, "RejectionReasons");
        if (!reasons.HasValue)
            return result;

        foreach (var property in reasons.Value.EnumerateObject())
        {
            var value = property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var number)
                ? number
                : property.Value.ValueKind == JsonValueKind.String && int.TryParse(property.Value.GetString(), out number)
                    ? number
                    : 0;
            if (value > 0 && property.Name.Length <= 80)
                result[property.Name] = value;
        }

        return result;
    }

    private static bool HasSourceRetrievalChunkQualityValue(ToolMemory.SourceRetrievalChunkQualityRef summary)
        => summary.TotalChunkCount.HasValue
           || summary.SearchableChunkCount.HasValue
           || summary.RejectedChunkCount.HasValue
           || summary.ManualReviewRecommended.HasValue
           || summary.RejectionReasons.Count > 0;

    private static int? NonNegativeOrNull(int? value)
        => value is >= 0 ? value : null;

    private static ToolMemory.SourceExtractionDiagnosticRef? CloneSourceExtractionDiagnostic(ToolMemory.SourceExtractionDiagnosticRef? summary)
        => summary is null
            ? null
            : new ToolMemory.SourceExtractionDiagnosticRef
            {
                NativeTextStatus = summary.NativeTextStatus,
                NativeOcrRecommended = summary.NativeOcrRecommended,
                OcrMode = summary.OcrMode,
                OcrLanguages = summary.OcrLanguages,
                OcrDurationMs = summary.OcrDurationMs,
                OcrFailureReason = summary.OcrFailureReason,
                OcrAppliedReason = summary.OcrAppliedReason,
                OcrTimedOut = summary.OcrTimedOut,
                OcrAttemptedPageCount = summary.OcrAttemptedPageCount,
                OcrSkippedPageCount = summary.OcrSkippedPageCount,
                OcrPagesWithNovelTextCount = summary.OcrPagesWithNovelTextCount,
                PageCount = summary.PageCount,
                TextPageCount = summary.TextPageCount,
                EmptyPageCount = summary.EmptyPageCount,
                SparsePageCount = summary.SparsePageCount,
                ImagePageCount = summary.ImagePageCount,
                PageWarningCount = summary.PageWarningCount,
                PageReviewRecommendedCount = summary.PageReviewRecommendedCount,
                RetrievalChunkQuality = CloneSourceRetrievalChunkQuality(summary.RetrievalChunkQuality)
            };

    private static ToolMemory.SourceRetrievalChunkQualityRef? CloneSourceRetrievalChunkQuality(ToolMemory.SourceRetrievalChunkQualityRef? summary)
        => summary is null
            ? null
            : new ToolMemory.SourceRetrievalChunkQualityRef
            {
                TotalChunkCount = summary.TotalChunkCount,
                SearchableChunkCount = summary.SearchableChunkCount,
                RejectedChunkCount = summary.RejectedChunkCount,
                ManualReviewRecommended = summary.ManualReviewRecommended,
                RejectionReasons = summary.RejectionReasons
                    .Where(static pair => pair.Value > 0)
                    .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            };

    private static ToolMemory.SourceExtractionDiagnosticRef? BuildSourceExtractionDiagnosticRef(SAAIA.Contracts.RagItemExtractionDiagnosticSummary? summary)
        => summary is null
            ? null
            : new ToolMemory.SourceExtractionDiagnosticRef
            {
                NativeTextStatus = NullIfWhiteSpace(summary.NativeTextStatus),
                NativeOcrRecommended = summary.NativeOcrRecommended,
                OcrMode = NullIfWhiteSpace(summary.OcrMode),
                OcrLanguages = NullIfWhiteSpace(summary.OcrLanguages),
                OcrDurationMs = summary.OcrDurationMs,
                OcrFailureReason = NullIfWhiteSpace(summary.OcrFailureReason),
                OcrAppliedReason = NullIfWhiteSpace(summary.OcrAppliedReason),
                OcrTimedOut = summary.OcrTimedOut,
                OcrAttemptedPageCount = summary.OcrAttemptedPageCount,
                OcrSkippedPageCount = summary.OcrSkippedPageCount,
                OcrPagesWithNovelTextCount = summary.OcrPagesWithNovelTextCount,
                PageCount = summary.PageCount,
                TextPageCount = summary.TextPageCount,
                EmptyPageCount = summary.EmptyPageCount,
                SparsePageCount = summary.SparsePageCount,
                ImagePageCount = summary.ImagePageCount,
                PageWarningCount = summary.PageWarningCount,
                PageReviewRecommendedCount = summary.PageReviewRecommendedCount,
                RetrievalChunkQuality = BuildSourceRetrievalChunkQualityRef(summary.RetrievalChunkQuality)
            };

    private static ToolMemory.SourceRetrievalChunkQualityRef? BuildSourceRetrievalChunkQualityRef(SAAIA.Contracts.RagItemRetrievalChunkQuality? summary)
        => summary is null
            ? null
            : new ToolMemory.SourceRetrievalChunkQualityRef
            {
                TotalChunkCount = summary.TotalChunkCount,
                SearchableChunkCount = summary.SearchableChunkCount,
                RejectedChunkCount = summary.RejectedChunkCount,
                ManualReviewRecommended = summary.ManualReviewRecommended,
                RejectionReasons = summary.RejectionReasons?
                    .Where(static pair => pair.Value > 0)
                    .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase) ?? new()
            };

    private static bool HasSourceExtractionDiagnosticValue(ToolMemory.SourceExtractionDiagnosticRef summary)
        => !string.IsNullOrWhiteSpace(summary.NativeTextStatus)
           || summary.NativeOcrRecommended.HasValue
           || !string.IsNullOrWhiteSpace(summary.OcrMode)
           || !string.IsNullOrWhiteSpace(summary.OcrLanguages)
           || summary.OcrDurationMs.HasValue
           || !string.IsNullOrWhiteSpace(summary.OcrFailureReason)
           || !string.IsNullOrWhiteSpace(summary.OcrAppliedReason)
           || summary.OcrTimedOut.HasValue
           || summary.OcrAttemptedPageCount.HasValue
           || summary.OcrSkippedPageCount.HasValue
           || summary.OcrPagesWithNovelTextCount.HasValue
           || summary.PageCount.HasValue
           || summary.TextPageCount.HasValue
           || summary.EmptyPageCount.HasValue
           || summary.SparsePageCount.HasValue
           || summary.ImagePageCount.HasValue
           || summary.PageWarningCount.HasValue
           || summary.PageReviewRecommendedCount.HasValue
           || summary.RetrievalChunkQuality is not null;

    private static JsonElement? TryGetExtractionDiagnosticSummaryObject(JsonElement root, JsonElement? qualityElement)
    {
        var fromQuality = qualityElement.HasValue
            ? TryGetObject(qualityElement.Value, "diagnosticSummary")
              ?? TryGetObject(qualityElement.Value, "diagnostic_summary")
              ?? TryGetObject(qualityElement.Value, "DiagnosticSummary")
              ?? TryGetObject(qualityElement.Value, "diagnostics")
              ?? TryGetObject(qualityElement.Value, "Diagnostics")
            : null;
        if (fromQuality.HasValue)
            return fromQuality;

        return TryGetObject(root, "diagnosticSummary")
               ?? TryGetObject(root, "diagnostic_summary")
               ?? TryGetObject(root, "DiagnosticSummary")
               ?? TryGetObject(root, "diagnostics")
               ?? TryGetObject(root, "Diagnostics");
    }

    private static object? CompactExtractionDiagnosticSummaryForPrompt(JsonElement quality)
    {
        var diagnostic = TryGetExtractionDiagnosticSummaryObject(default, quality);
        if (!diagnostic.HasValue)
            return null;

        var summary = BuildSourceExtractionDiagnosticRefFromElement(diagnostic.Value);
        return BuildSourceExtractionDiagnosticPayload(summary);
    }

    private static object? CompactExtractionQualityForPrompt(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || (!item.TryGetProperty("extractionQuality", out var quality)
                && !item.TryGetProperty("extraction_quality", out quality)
                && !item.TryGetProperty("ExtractionQuality", out quality))
            || quality.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        var compact = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["extractionSource"] = TryGetString(quality, "extractionSource") ?? TryGetString(quality, "extraction_source") ?? TryGetString(quality, "ExtractionSource"),
            ["ocrAttempted"] = TryGetBool(quality, "ocrAttempted") ?? TryGetBool(quality, "ocr_attempted") ?? TryGetBool(quality, "OcrAttempted") ?? TryGetBool(quality, "OCRAttempted"),
            ["ocrApplied"] = TryGetBool(quality, "ocrApplied") ?? TryGetBool(quality, "ocr_applied") ?? TryGetBool(quality, "OcrApplied"),
            ["documentQualityStatus"] = TryGetString(quality, "documentQualityStatus") ?? TryGetString(quality, "document_quality_status") ?? TryGetString(quality, "DocumentQualityStatus"),
            ["documentExtractionConfidence"] = TryGetDouble(quality, "documentExtractionConfidence") ?? TryGetDouble(quality, "document_extraction_confidence") ?? TryGetDouble(quality, "DocumentExtractionConfidence"),
            ["documentManualReviewRecommended"] = TryGetBool(quality, "documentManualReviewRecommended") ?? TryGetBool(quality, "document_manual_review_recommended") ?? TryGetBool(quality, "DocumentManualReviewRecommended"),
            ["pageQualityStatus"] = TryGetString(quality, "pageQualityStatus") ?? TryGetString(quality, "page_quality_status") ?? TryGetString(quality, "PageQualityStatus"),
            ["pageExtractionConfidence"] = TryGetDouble(quality, "pageExtractionConfidence") ?? TryGetDouble(quality, "page_extraction_confidence") ?? TryGetDouble(quality, "PageExtractionConfidence"),
            ["pageManualReviewRecommended"] = TryGetBool(quality, "pageManualReviewRecommended") ?? TryGetBool(quality, "page_manual_review_recommended") ?? TryGetBool(quality, "PageManualReviewRecommended"),
            ["textStatus"] = TryGetString(quality, "textStatus") ?? TryGetString(quality, "text_status") ?? TryGetString(quality, "TextStatus"),
            ["ocrRecommended"] = TryGetBool(quality, "ocrRecommended") ?? TryGetBool(quality, "ocr_recommended") ?? TryGetBool(quality, "OcrRecommended"),
            ["chunkTextStatus"] = TryGetString(quality, "chunkTextStatus") ?? TryGetString(quality, "chunk_text_status") ?? TryGetString(quality, "ChunkTextStatus"),
            ["chunkTextSparse"] = TryGetBool(quality, "chunkTextSparse") ?? TryGetBool(quality, "chunk_text_sparse") ?? TryGetBool(quality, "ChunkTextSparse"),
            ["chunkOcrCandidate"] = TryGetBool(quality, "chunkOcrCandidate") ?? TryGetBool(quality, "chunk_ocr_candidate") ?? TryGetBool(quality, "ChunkOcrCandidate")
        };

        var diagnosticSummary = CompactExtractionDiagnosticSummaryForPrompt(quality);
        if (diagnosticSummary is not null)
            compact["diagnosticSummary"] = diagnosticSummary;

        var signals = ExtractCompactSignals(quality, "signals")
            .Concat(ExtractCompactSignals(quality, "Signals"))
            .Concat(ExtractCompactSignals(quality, "chunkQualitySignals"))
            .Concat(ExtractCompactSignals(quality, "chunk_quality_signals"))
            .Concat(ExtractCompactSignals(quality, "ChunkQualitySignals"))
            .Where(static signal => !string.IsNullOrWhiteSpace(signal))
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToArray();
        if (signals.Length > 0)
            compact["signals"] = signals;

        var nonEmpty = compact
            .Where(static pair => pair.Value is not null)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value!, StringComparer.Ordinal);
        return nonEmpty.Count == 0 ? null : nonEmpty;
    }

    private static IEnumerable<string> ExtractCompactSignals(JsonElement quality, string propertyName)
    {
        if (quality.ValueKind != JsonValueKind.Object
            || !quality.TryGetProperty(propertyName, out var signals)
            || signals.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var signal in signals.EnumerateArray())
        {
            if (signal.ValueKind == JsonValueKind.String)
            {
                var value = signal.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    yield return value.Trim();
            }
        }
    }

    private static IEnumerable<KeyValuePair<string, int>> ExtractCompactIntMap(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(propertyName, out var map)
            || map.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var property in map.EnumerateObject())
        {
            var key = property.Name.Trim();
            if (key.Length == 0 || key.Length > 80)
                continue;

            var value = 0;
            if (property.Value.ValueKind == JsonValueKind.Number)
                property.Value.TryGetInt32(out value);
            else if (property.Value.ValueKind == JsonValueKind.String)
                int.TryParse(property.Value.GetString(), out value);
            if (value > 0)
                yield return new KeyValuePair<string, int>(key, value);
        }
    }
}
