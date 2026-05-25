using System.ComponentModel;
using System.Text.Json;

namespace SAAIA.Backend.Models;

/// <summary>
/// RAG diversity controls for CDC v3.1 search behavior.
/// When present, overrides the default max-per-doc and max-per-page caps.
/// </summary>
public sealed record RagDiversityDto(
    int? MaxChunksPerDoc = null,
    bool? PreferDistinctPages = null
);

/// <summary>
/// Request contract for POST /rag/search aligned with CDC v3.1.
/// </summary>
public sealed record RagSearchRequestDto(
    string Query,
    string? Category = null,
    int? TopK = null,
    double? MinScore = null,
    int? Candidates = null,
    int? MaxPerDoc = null,
    int? MaxPerPage = null,
    string? Mode = null,
    RagDiversityDto? Diversity = null,
    string? DocId = null,
    string? DocPath = null,
    bool? IncludeContextualSnippet = null,
    string? CategoryPath = null,
    string? CategoryRef = null
);

/// <summary>
/// Structured retrieval provenance returned by POST /rag/search.
/// CDC v3.1 section 11.6: provenance with optional character-level offsets.
/// </summary>
public sealed record RagItemProvenanceDto(
    string Channel,
    string Label,
    string? SourceHash = null,
    string? ChunkId = null,
    int? PageStart = null,
    int? PageEnd = null,
    int? OffsetStart = null,
    int? OffsetEnd = null
);

/// <summary>
/// Structured retrieval context returned by POST /rag/search.
/// Keeps document structure and neighborhood links together for diagnostics and UI tooling.
/// </summary>
public sealed record RagItemContextDto(
    string? ChunkType = null,
    string? SectionTitle = null,
    string? HeadingPath = null,
    string? PrevChunkId = null,
    string? NextChunkId = null,
    string? SameSectionChunkId = null,
    string? ContentRole = null,
    string? NavigationReason = null,
    string? OriginalChunkType = null,
    double? NavigationScore = null,
    double? ContentDensityScore = null
);

/// <summary>
/// Lightweight extraction/OCR quality hints attached to a retrieved item.
/// They qualify evidence confidence without exposing heavy admin diagnostics payloads.
/// </summary>
public sealed record RagItemExtractionDiagnosticSummaryDto(
    string? NativeTextStatus = null,
    bool? NativeOcrRecommended = null,
    string? OcrMode = null,
    string? OcrLanguages = null,
    long? OcrDurationMs = null,
    string? OcrFailureReason = null,
    string? OcrAppliedReason = null,
    bool? OcrTimedOut = null,
    int? OcrAttemptedPageCount = null,
    int? OcrSkippedPageCount = null,
    int? OcrPagesWithNovelTextCount = null,
    int? PageCount = null,
    int? TextPageCount = null,
    int? EmptyPageCount = null,
    int? SparsePageCount = null,
    int? ImagePageCount = null,
    int? PageWarningCount = null,
    int? PageReviewRecommendedCount = null
);

/// <summary>
/// Lightweight extraction/OCR quality hints attached to a retrieved item.
/// They qualify evidence confidence without exposing heavy admin diagnostics payloads.
/// </summary>
public sealed record RagItemExtractionQualityDto(
    string? ExtractionSource = null,
    bool? OcrAttempted = null,
    bool? OcrApplied = null,
    string? DocumentQualityStatus = null,
    double? DocumentExtractionConfidence = null,
    bool? DocumentManualReviewRecommended = null,
    string? PageQualityStatus = null,
    double? PageExtractionConfidence = null,
    bool? PageManualReviewRecommended = null,
    string? TextStatus = null,
    bool? OcrRecommended = null,
    IReadOnlyList<string>? Signals = null,
    RagItemExtractionDiagnosticSummaryDto? DiagnosticSummary = null
);

/// <summary>
/// Structured profile content card evidence selected for a document_profile hit.
/// </summary>
public sealed record RagItemContentCardDto(
    string Title,
    string? ContentCardId = null,
    int? PageStart = null,
    int? PageEnd = null,
    string? Kind = null,
    IReadOnlyList<string>? Signals = null,
    JsonElement? Evidence = null
);

/// <summary>
/// Backend evidence-role hints for downstream writers and deterministic client fallbacks.
/// They are corpus-agnostic and describe how safe a hit is to promote into a concrete answer item.
/// </summary>
public sealed record RagItemSelectionHintsDto(
    string EvidenceRole,
    int ActionabilityScore = 0,
    int SupportScore = 0,
    int FragmentScore = 0,
    int NavigationScore = 0,
    int QualityPenalty = 0
);

/// <summary>
/// Compact document-profile signals that explain a profile match without exposing full backend-only search text.
/// </summary>
public sealed record RagItemProfileSignalsDto(
    string? ProfileVersion = null,
    string? Language = null,
    IReadOnlyList<string>? Keywords = null,
    IReadOnlyList<string>? Entities = null,
    IReadOnlyList<string>? Topics = null,
    IReadOnlyList<string>? HypotheticalQuestions = null,
    IReadOnlyList<string>? Limits = null,
    IReadOnlyList<string>? MatchedTerms = null,
    int? MatchCount = null
);

/// <summary>
/// Retrieval item returned by POST /rag/search.
/// Flat fields are kept for compatibility while structured fields progressively align the contract with CDC v3.1.
/// </summary>
public sealed record RagItemDto(
    double Score,
    string? DocId,
    string DocName,
    string? DocPath,
    string? Category,
    string? CategoryRef,
    string? DocLanguage,
    string? ProfileLanguage,
    int? PageStart,
    int? PageEnd,
    string? ChunkId,
    int? ChunkIndex,
    string Text,
    string? Retriever = null,
    [property: Obsolete("Use ProvenanceInfo instead. This legacy flat field is retained only for backward compatibility.")]
    [property: EditorBrowsable(EditorBrowsableState.Never)]
    string? Provenance = null,
    bool? ExactMatchHit = null,
    string? SourceHash = null,
    string? EmbeddingBasis = null,
    string? ChunkType = null,
    string? SectionTitle = null,
    string? HeadingPath = null,
    string? PrevChunkId = null,
    string? NextChunkId = null,
    string? SameSectionChunkId = null,
    RagItemProvenanceDto? ProvenanceInfo = null,
    RagItemContextDto? Context = null,
    // --- CDC v3.1 section 11.6 enrichment fields ---
    string? CategoryPath = null,
    string? Snippet = null,
    double? RerankScore = null,
    bool? HasTable = null,
    bool? HasWarning = null,
    string? ContextualSnippet = null,
    bool? HypQuestionsMatched = null,
    RagItemExtractionQualityDto? ExtractionQuality = null,
    IReadOnlyList<RagItemContentCardDto>? MatchedContentCards = null,
    RagItemSelectionHintsDto? SelectionHints = null,
    RagItemProfileSignalsDto? ProfileSignals = null
);

/// <summary>
/// Aggregate search metrics for CDC v3.1 retrieval diagnostics.
/// </summary>
public sealed record RagMetricsDto(
    long TookMs,
    int Returned,
    int ExactMatchReturned = 0,
    int DenseReturned = 0,
    int SparseReturned = 0,
    int LinkedReturned = 0,
    IReadOnlyList<string>? RetrieversUsed = null,
    string? DataHash = null,
    int? TtlSeconds = 600,
    long? TeiMs = null,
    long? RerankMs = null,
    long? SparseMs = null,
    long? QdrantMs = null,
    int? CandidatesEvaluated = null,
    IReadOnlyList<string>? DegradedRetrievers = null,
    IReadOnlyDictionary<string, string>? DegradedRetrieverErrors = null,
    long? ExactMs = null,
    long? QuotedTitleMs = null,
    long? LocalTitleTokenMs = null,
    long? TitleAnchorRouteMs = null,
    long? SparsePhaseMs = null,
    long? DenseMs = null,
    long? ProfileMs = null,
    long? LinkedMs = null,
    long? FusionMs = null,
    long? RerankPhaseMs = null,
    long? SelectionMs = null
);

/// <summary>
/// Guidance for how a downstream writer or UI should frame the answer for the current question.
/// This is a lightweight bridge toward CDC v3.1 answer-quality behavior without pretending to be full evidence-pack reasoning.
/// </summary>
public sealed record RagAnswerGuidanceDto(
    string Behavior,
    string Reason,
    string? ResponseShape = null,
    string? ClarifyingQuestion = null,
    string? QualificationNote = null,
    IReadOnlyList<string>? MatchedDocHints = null
);

/// <summary>
/// Response contract for POST /rag/search aligned with CDC v3.1 retrieval output.
/// </summary>
public sealed record RagSearchResponseDto(
    string RequestId,
    string Query,
    string QueryNormalized,
    string? Category,
    int TopK,
    double MinScore,
    int Candidates,
    int MaxPerDoc,
    int MaxPerPage,
    RagMetricsDto Metrics,
    IReadOnlyList<RagItemDto> Items,
    RagAnswerGuidanceDto? Guidance = null
);
