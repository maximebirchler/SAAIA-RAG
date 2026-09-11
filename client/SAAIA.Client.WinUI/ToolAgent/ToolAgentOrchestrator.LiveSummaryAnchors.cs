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
            ChunkTextStatus = item?.ChunkTextStatus ?? fallbackSource?.ChunkTextStatus,
            ChunkTextSparse = item?.ChunkTextSparse ?? fallbackSource?.ChunkTextSparse,
            ChunkOcrCandidate = item?.ChunkOcrCandidate ?? fallbackSource?.ChunkOcrCandidate,
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
            ChunkQualitySignals = (item?.ChunkQualitySignals ?? fallbackSource?.ChunkQualitySignals ?? []).ToList(),
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
            evidenceId = source.EvidenceId,
            docId = source.DocId,
            docPath = source.DocPath,
            docName = source.DocName,
            pageStart = source.PageStart,
            pageEnd = source.PageEnd,
            label = source.Label,
            sourceHash = source.SourceHash,
            revisionId = source.RevisionId,
            docLanguage = source.DocLanguage,
            profileLanguage = source.ProfileLanguage,
            category = source.Category,
            categoryRef = source.CategoryRef,
            categoryPath = source.CategoryPath,
            chunkId = source.ChunkId,
            anchorId = source.AnchorId,
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
