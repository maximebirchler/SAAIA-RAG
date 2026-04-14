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
/// This remains retrieval-oriented metadata, not yet a full CDC evidence-pack provenance object.
/// </summary>
public sealed record RagItemProvenanceDto(
    string Channel,
    string Label,
    string? SourceHash = null,
    string? ChunkId = null,
    int? PageStart = null,
    int? PageEnd = null
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
    RagItemContextDto? Context = null
);

/// <summary>
/// Aggregate search metrics for CDC v3.0 retrieval diagnostics.
/// </summary>
public sealed record RagMetricsDto(
    long TookMs,
    int Returned,
    int ExactMatchReturned = 0,
    int DenseReturned = 0,
    int LinkedReturned = 0,
    IReadOnlyList<string>? RetrieversUsed = null,
    string? DataHash = null,
    int? TtlSeconds = null
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
    IReadOnlyList<RagItemDto> Items
);
