using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using SAAIA.Contracts;

namespace SAAIA.Client.WinUI.Services.ToolAgent;

public sealed partial class ToolAgentOrchestrator
{

    private sealed record RagHitSummary(
        string DocPath,
        string DocName,
        int PageStart,
        int PageEnd,
        string Excerpt,
        string? SectionTitle = null,
        string? HeadingPath = null,
        string? Retriever = null,
        double Score = 0.0,
        bool ExactMatchHit = false,
        string? FullText = null,
        string? ContextualSnippet = null,
        string? EmbeddingBasis = null,
        string? QualityStatus = null,
        double? ExtractionConfidence = null,
        double? DocumentExtractionConfidence = null,
        double? PageExtractionConfidence = null,
        bool ManualReviewRecommended = false,
        bool DocumentManualReviewRecommended = false,
        bool PageManualReviewRecommended = false,
        bool OcrAttempted = false,
        bool OcrApplied = false,
        string? DocLanguage = null,
        string? ProfileLanguage = null,
        ToolMemory.SourceProfileSignalsRef? ProfileSignals = null,
        IReadOnlyList<RagHitContentCardSummary>? MatchedContentCards = null,
        string? SourceHash = null,
        string? DocId = null,
        string? Category = null,
        string? CategoryRef = null,
        string? CategoryPath = null,
        string? ChunkId = null,
        string? ExtractionSource = null,
        string? DocumentQualityStatus = null,
        string? PageQualityStatus = null,
        string? TextStatus = null,
        string? ChunkTextStatus = null,
        bool? ChunkTextSparse = null,
        bool? ChunkOcrCandidate = null,
        bool OcrRecommended = false,
        IReadOnlyList<string>? QualitySignals = null,
        IReadOnlyList<string>? ChunkQualitySignals = null,
        string? SelectionHintRole = null,
        int? SelectionHintActionabilityScore = null,
        int? SelectionHintSupportScore = null,
        int? SelectionHintFragmentScore = null,
        int? SelectionHintNavigationScore = null,
        int? SelectionHintQualityPenalty = null,
        ToolMemory.SourceExtractionDiagnosticRef? ExtractionDiagnosticSummary = null,
        string? ContentRole = null,
        string? NavigationReason = null,
        double? NavigationScore = null,
        double? ContentDensityScore = null,
        string? PrevChunkId = null,
        string? NextChunkId = null,
        string? SameSectionChunkId = null,
        string? OriginalChunkType = null,
        int? OffsetStart = null,
        int? OffsetEnd = null,
        string? RetrievalQuery = null,
        int? RetrievalQueryIndex = null,
        int? RetrievalHitRank = null,
        int? RetrievalQuerySpecificity = null,
        bool HasTable = false,
        IReadOnlyList<int>? SourceUnitOrdinals = null,
        int? SourceUnitStartOrdinal = null,
        int? SourceUnitEndOrdinal = null,
        int? SourceUnitCount = null,
        string? ChunkComposition = null);

    private sealed record RagHitContentCardSummary(
        string Title,
        string? ContentCardId = null,
        int? PageStart = null,
        int? PageEnd = null,
        string? Kind = null,
        IReadOnlyList<string>? Signals = null,
        RagHitContentCardEvidenceSummary? Evidence = null,
        JsonElement? RawEvidence = null);

    private sealed record RagHitContentCardEvidenceSummary(
        string? SchemaVersion,
        RagHitScaleBasisSummary? ScaleBasis,
        IReadOnlyList<RagHitQuantityFactSummary> QuantityFacts,
        IReadOnlyList<string> NonScalableReasons,
        double? Confidence = null,
        string? Language = null,
        IReadOnlyList<RagHitEvidenceFactSummary>? Facts = null);

    private sealed record RagHitScaleBasisSummary(int Count, string? Label);

    private sealed record RagHitQuantityFactSummary(
        double Value,
        string Unit,
        string Label,
        string? SourceText = null);

    private sealed record RagHitEvidenceFactSummary(
        string Kind,
        string Label,
        string? Value = null,
        string? Unit = null,
        string? SourceText = null,
        int? PageStart = null,
        int? PageEnd = null,
        double? Confidence = null);

    private sealed record RagHitEvidenceProfile(
        string Role,
        int ActionabilityScore,
        int SupportScore,
        int FragmentScore,
        int NavigationScore,
        int QualityPenalty);

}
