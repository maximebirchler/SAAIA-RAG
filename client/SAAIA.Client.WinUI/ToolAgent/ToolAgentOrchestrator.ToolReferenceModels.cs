using System.Collections.Generic;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{
    private sealed record ResolvedDocRef(
        string DocId,
        string DocPath,
        string DocName,
        string? Category,
        string? CategoryPath,
        int? Pages,
        string? CategoryRef = null,
        string? SourceHash = null,
        string? DocLanguage = null,
        string? ProfileLanguage = null);
    private sealed record SummaryChunk(
        string Text,
        int PageStart,
        int PageEnd,
        int ChunkIndex,
        string DocPath,
        string DocName,
        string? DocId = null,
        string? SourceHash = null,
        string? DocLanguage = null,
        string? ProfileLanguage = null,
        string? Category = null,
        string? CategoryRef = null,
        string? CategoryPath = null,
        string? ChunkId = null,
        string? SectionTitle = null,
        string? HeadingPath = null,
        string? PrevChunkId = null,
        string? NextChunkId = null,
        string? SameSectionChunkId = null,
        string? OriginalChunkType = null,
        int? OffsetStart = null,
        int? OffsetEnd = null,
        string? ExtractionSource = null,
        string? DocumentQualityStatus = null,
        string? PageQualityStatus = null,
        string? TextStatus = null,
        string? ChunkTextStatus = null,
        bool? ChunkTextSparse = null,
        bool? ChunkOcrCandidate = null,
        string? QualityStatus = null,
        double? ExtractionConfidence = null,
        double? DocumentExtractionConfidence = null,
        double? PageExtractionConfidence = null,
        bool ManualReviewRecommended = false,
        bool DocumentManualReviewRecommended = false,
        bool PageManualReviewRecommended = false,
        bool OcrAttempted = false,
        bool OcrApplied = false,
        bool OcrRecommended = false,
        ToolMemory.SourceExtractionDiagnosticRef? ExtractionDiagnosticSummary = null,
        IReadOnlyList<string>? QualitySignals = null,
        IReadOnlyList<string>? ChunkQualitySignals = null,
        IReadOnlyList<ToolMemory.SourceContentCardRef>? MatchedContentCards = null,
        ToolMemory.SourceProfileSignalsRef? ProfileSignals = null,
        string? SelectionHintEvidenceRole = null,
        int? SelectionHintActionabilityScore = null,
        int? SelectionHintSupportScore = null,
        int? SelectionHintFragmentScore = null,
        int? SelectionHintNavigationScore = null,
        int? SelectionHintQualityPenalty = null,
        string? ContentRole = null,
        string? NavigationReason = null,
        double? RetrievalNavigationScore = null,
        double? ContentDensityScore = null);

    private sealed class ExplicitDocumentResolution
    {
        public bool IsResolved { get; init; }
        public bool IsAmbiguous { get; init; }
        public bool IsCategoryReference { get; init; }
        public ResolvedDocRef? Document { get; init; }
    }
}
