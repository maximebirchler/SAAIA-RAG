using System.Collections.Generic;
using System.Text.Json;

namespace SAAIA.Client.WinUI.Models;

public sealed class SourceCard
{
    public string? DocId { get; set; }
    public string DocPath { get; set; } = "";
    public string DocName { get; set; } = "";
    public int? PageStart { get; set; }
    public int? PageEnd { get; set; }
    public string Snippet { get; set; } = "";
    public double? Score { get; set; }
    public string? SourceHash { get; set; }
    public string? DocLanguage { get; set; }
    public string? ProfileLanguage { get; set; }
    public string? Category { get; set; }
    public string? CategoryRef { get; set; }
    public string? CategoryPath { get; set; }
    public string? ChunkId { get; set; }
    public string? SectionTitle { get; set; }
    public string? HeadingPath { get; set; }
    public string? PrevChunkId { get; set; }
    public string? NextChunkId { get; set; }
    public string? SameSectionChunkId { get; set; }
    public string? OriginalChunkType { get; set; }
    public int? OffsetStart { get; set; }
    public int? OffsetEnd { get; set; }
    public string? ExtractionSource { get; set; }
    public string? DocumentQualityStatus { get; set; }
    public string? PageQualityStatus { get; set; }
    public string? TextStatus { get; set; }
    public string? ChunkTextStatus { get; set; }
    public bool? ChunkTextSparse { get; set; }
    public bool? ChunkOcrCandidate { get; set; }
    public string? QualityStatus { get; set; }
    public double? ExtractionConfidence { get; set; }
    public double? DocumentExtractionConfidence { get; set; }
    public double? PageExtractionConfidence { get; set; }
    public bool ManualReviewRecommended { get; set; }
    public bool DocumentManualReviewRecommended { get; set; }
    public bool PageManualReviewRecommended { get; set; }
    public bool OcrAttempted { get; set; }
    public bool OcrApplied { get; set; }
    public bool OcrRecommended { get; set; }
    public SourceExtractionDiagnosticSummary? ExtractionDiagnosticSummary { get; set; }
    public List<string> QualitySignals { get; set; } = new();
    public List<string> ChunkQualitySignals { get; set; } = new();
    public List<SourceContentCard> MatchedContentCards { get; set; } = new();
    public SourceProfileSignals? ProfileSignals { get; set; }
    public string? SelectionHintEvidenceRole { get; set; }
    public int? SelectionHintActionabilityScore { get; set; }
    public int? SelectionHintSupportScore { get; set; }
    public int? SelectionHintFragmentScore { get; set; }
    public int? SelectionHintNavigationScore { get; set; }
    public int? SelectionHintQualityPenalty { get; set; }
    public string? ContentRole { get; set; }
    public string? NavigationReason { get; set; }
    public double? RetrievalNavigationScore { get; set; }
    public double? ContentDensityScore { get; set; }

    public string PagesLabel { get; set; } = "";
    public string ScoreLabel { get; set; } = "";
    public string MetadataLabel { get; set; } = "";
}

public sealed class SourceExtractionDiagnosticSummary
{
    public string? NativeTextStatus { get; set; }
    public bool? NativeOcrRecommended { get; set; }
    public string? OcrMode { get; set; }
    public string? OcrLanguages { get; set; }
    public long? OcrDurationMs { get; set; }
    public string? OcrFailureReason { get; set; }
    public string? OcrAppliedReason { get; set; }
    public bool? OcrTimedOut { get; set; }
    public int? OcrAttemptedPageCount { get; set; }
    public int? OcrSkippedPageCount { get; set; }
    public int? OcrPagesWithNovelTextCount { get; set; }
    public int? PageCount { get; set; }
    public int? TextPageCount { get; set; }
    public int? EmptyPageCount { get; set; }
    public int? SparsePageCount { get; set; }
    public int? ImagePageCount { get; set; }
    public int? PageWarningCount { get; set; }
    public int? PageReviewRecommendedCount { get; set; }
}

public sealed class SourceContentCard
{
    public string Title { get; set; } = "";
    public string? ContentCardId { get; set; }
    public int? PageStart { get; set; }
    public int? PageEnd { get; set; }
    public string? Kind { get; set; }
    public List<string> Signals { get; set; } = new();
    public JsonElement? Evidence { get; set; }
}

public sealed class SourceProfileSignals
{
    public string? ProfileVersion { get; set; }
    public string? Language { get; set; }
    public List<string> Keywords { get; set; } = new();
    public List<string> Entities { get; set; } = new();
    public List<string> Topics { get; set; } = new();
    public List<string> HypotheticalQuestions { get; set; } = new();
    public List<string> Limits { get; set; } = new();
    public List<string> MatchedTerms { get; set; } = new();
    public int? MatchCount { get; set; }
}
