namespace SAAIA.Backend.Models;

/// <summary>
/// RAG diversity controls for CDC v3.0 search behavior.
/// When present, overrides the default max-per-doc and max-per-page caps.
/// </summary>
public sealed record RagDiversityDto(
    int? MaxChunksPerDoc = null,
    bool? PreferDistinctPages = null
);

/// <summary>
/// Request contract for POST /rag/search aligned with CDC v3.0.
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
    string? DocPath = null
);

/// <summary>
/// Structured retrieval provenance returned by POST /rag/search.
/// CDC v3.0 §11.6: provenance with optional character-level offsets.
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
    string? SameSectionChunkId = null
);

/// <summary>
/// Retrieval item returned by POST /rag/search.
/// Flat fields are kept for compatibility while structured fields progressively align the contract with CDC v3.0.
/// </summary>
public sealed record RagItemDto(
    double Score,
    string? DocId,
    string DocName,
    string? DocPath,
    string? Category,
    string? CategoryRef,
    int? PageStart,
    int? PageEnd,
    string? ChunkId,
    int? ChunkIndex,
    string Text,
    string? Retriever = null,
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
    // --- CDC v3.0 §11.6 enrichment fields ---
    string? CategoryPath = null,
    string? Snippet = null,
    double? RerankScore = null,
    bool? HasTable = null,
    bool? HasWarning = null,
    string? ContextualSnippet = null,
    bool? HypQuestionsMatched = null
);

/// <summary>
/// Aggregate search metrics for CDC v3.0 retrieval diagnostics.
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
    int? CandidatesEvaluated = null
);

/// <summary>
/// Guidance for how a downstream writer or UI should frame the answer for the current question.
/// This is a lightweight bridge toward CDC v3.0 answer-quality behavior without pretending to be full evidence-pack reasoning.
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
/// Response contract for POST /rag/search aligned with CDC v3.0 retrieval output.
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
