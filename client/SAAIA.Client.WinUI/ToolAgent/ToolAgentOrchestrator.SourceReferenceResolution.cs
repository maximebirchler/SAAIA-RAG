using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private ToolMemory.SourceRef? ResolveSourceRef(string rawRef)
    {
        var s = (rawRef ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(s))
            return null;

        var explicitSourceOrdinal = Regex.Match(s, @"(?i)^\s*(?:source|citation|reference|r[eÃ©]f(?:[eÃ©]rence)?\.?)\s*(?:n[Â°o]\s*)?#?\s*0*(?<n>\d{1,4})\s*$");
        if (explicitSourceOrdinal.Success
            && int.TryParse(explicitSourceOrdinal.Groups["n"].Value, out var sourceOrdinal)
            && sourceOrdinal > 0
            && _mem.LastSourcesUsed is { Count: > 0 }
            && sourceOrdinal <= _mem.LastSourcesUsed.Count)
        {
            return _mem.LastSourcesUsed[sourceOrdinal - 1];
        }

        var mPdf = Regex.Match(s, @"(?i)\bPDF\s*0*(?<n>\d{1,4})\b");
        if (mPdf.Success && int.TryParse(mPdf.Groups["n"].Value, out var nPdf) && nPdf > 0)
        {
            var key = $"PDF{nPdf:00}";
            if (_mem.PdfMap.TryGetValue(key, out var mapped) && mapped is not null && !string.IsNullOrWhiteSpace(mapped.DocPath))
                return BuildSourceFromDocument(mapped);
        }

        var mNum = Regex.Match(s, @"\b(?<n>\d{1,4})\b");
        if (mNum.Success && int.TryParse(mNum.Groups["n"].Value, out var n) && n > 0)
        {
            if (_mem.LastListedDocuments is { Count: > 0 } && n <= _mem.LastListedDocuments.Count)
                return BuildSourceFromDocument(_mem.LastListedDocuments[n - 1]);

            var key = $"PDF{n:00}";
            if (_mem.PdfMap.TryGetValue(key, out var mapped) && mapped is not null && !string.IsNullOrWhiteSpace(mapped.DocPath))
                return BuildSourceFromDocument(mapped);
        }

        foreach (var used in _mem.LastSourcesUsed)
        {
            if (string.Equals(used.DocPath, s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(used.Label, s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(used.DocId, s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(used.SourceHash, s, StringComparison.OrdinalIgnoreCase))
            {
                return used;
            }
        }

        foreach (var doc in _mem.LastListedDocuments)
        {
            if (string.Equals(doc.DocPath, s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(doc.DocName, s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(doc.DocId, s, StringComparison.OrdinalIgnoreCase))
            {
                return BuildSourceFromDocument(doc);
            }
        }

        foreach (var kv in _mem.PdfMap.Values)
        {
            if (string.Equals(kv.DocPath, s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(kv.DocName, s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(kv.DocId, s, StringComparison.OrdinalIgnoreCase))
            {
                return BuildSourceFromDocument(kv);
            }
        }

        return null;
    }

    private ToolMemory.SourceRef BuildSourceFromDocument(ToolMemory.DocumentItem doc)
    {
        var pageStart = 1;
        var pageEnd = 1;
        var used = _mem.LastSourcesUsed?
            .FirstOrDefault(s => string.Equals((s.DocPath ?? string.Empty).Replace('\\', '/'), (doc.DocPath ?? string.Empty).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
        if (used is not null)
        {
            pageStart = Math.Max(1, used.PageStart);
            pageEnd = Math.Max(pageStart, used.PageEnd);
        }

        return new ToolMemory.SourceRef
        {
            DocId = NullIfWhiteSpace(doc.DocId) ?? used?.DocId,
            DocPath = doc.DocPath,
            DocName = NullIfWhiteSpace(doc.DocName) ?? used?.DocName,
            PageStart = pageStart,
            PageEnd = pageEnd,
            Label = string.IsNullOrWhiteSpace(doc.DocName) ? doc.DocPath : doc.DocName,
            SourceHash = NullIfWhiteSpace(used?.SourceHash) ?? NullIfWhiteSpace(doc.SourceHash),
            DocLanguage = NullIfWhiteSpace(used?.DocLanguage) ?? NullIfWhiteSpace(doc.DocLanguage),
            ProfileLanguage = NullIfWhiteSpace(used?.ProfileLanguage) ?? NullIfWhiteSpace(doc.ProfileLanguage),
            Category = NullIfWhiteSpace(used?.Category) ?? NullIfWhiteSpace(doc.Category),
            CategoryRef = NullIfWhiteSpace(used?.CategoryRef) ?? NullIfWhiteSpace(doc.CategoryRef),
            CategoryPath = NullIfWhiteSpace(used?.CategoryPath) ?? NullIfWhiteSpace(doc.CategoryPath) ?? NullIfWhiteSpace(doc.Category),
            ChunkId = used?.ChunkId,
            SectionTitle = used?.SectionTitle,
            HeadingPath = used?.HeadingPath,
            PrevChunkId = used?.PrevChunkId,
            NextChunkId = used?.NextChunkId,
            SameSectionChunkId = used?.SameSectionChunkId,
            OriginalChunkType = used?.OriginalChunkType,
            OffsetStart = used?.OffsetStart,
            OffsetEnd = used?.OffsetEnd,
            ExtractionSource = used?.ExtractionSource,
            DocumentQualityStatus = used?.DocumentQualityStatus,
            PageQualityStatus = used?.PageQualityStatus,
            TextStatus = used?.TextStatus,
            ChunkTextStatus = used?.ChunkTextStatus,
            ChunkTextSparse = used?.ChunkTextSparse,
            ChunkOcrCandidate = used?.ChunkOcrCandidate,
            QualityStatus = used?.QualityStatus,
            ExtractionConfidence = used?.ExtractionConfidence,
            DocumentExtractionConfidence = used?.DocumentExtractionConfidence,
            PageExtractionConfidence = used?.PageExtractionConfidence,
            ManualReviewRecommended = used?.ManualReviewRecommended ?? false,
            DocumentManualReviewRecommended = used?.DocumentManualReviewRecommended ?? false,
            PageManualReviewRecommended = used?.PageManualReviewRecommended ?? false,
            OcrAttempted = used?.OcrAttempted ?? false,
            OcrApplied = used?.OcrApplied ?? false,
            OcrRecommended = used?.OcrRecommended ?? false,
            ExtractionDiagnosticSummary = CloneSourceExtractionDiagnostic(used?.ExtractionDiagnosticSummary),
            QualitySignals = used?.QualitySignals.ToList() ?? new List<string>(),
            ChunkQualitySignals = used?.ChunkQualitySignals.ToList() ?? new List<string>(),
            MatchedContentCards = used?.MatchedContentCards.ToList() ?? new List<ToolMemory.SourceContentCardRef>(),
            ProfileSignals = CloneSourceProfileSignalsRef(used?.ProfileSignals),
            SelectionHintEvidenceRole = used?.SelectionHintEvidenceRole,
            SelectionHintActionabilityScore = used?.SelectionHintActionabilityScore,
            SelectionHintSupportScore = used?.SelectionHintSupportScore,
            SelectionHintFragmentScore = used?.SelectionHintFragmentScore,
            SelectionHintNavigationScore = used?.SelectionHintNavigationScore,
            SelectionHintQualityPenalty = used?.SelectionHintQualityPenalty,
            ContentRole = used?.ContentRole,
            NavigationReason = used?.NavigationReason,
            RetrievalNavigationScore = used?.RetrievalNavigationScore,
            ContentDensityScore = used?.ContentDensityScore
        };
    }
}
