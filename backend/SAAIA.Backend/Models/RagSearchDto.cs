namespace SAAIA.Backend.Models;

/// <summary>
/// Request pour POST /rag/search — CDC v2.7
/// </summary>
public sealed record RagSearchRequestDto(
    string Query,
    string? Category = null,
    int? TopK = null,
    double? MinScore = null,
    int? Candidates = null,
    int? MaxPerDoc = null,
    int? MaxPerPage = null,
    string? Mode = null
);

/// <summary>
/// Item citables dans la réponse /rag/search — CDC v2.7
/// Correspond à un chunk avec contexte document/page
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
    string Text
);

/// <summary>
/// Métriques de la recherche RAG — CDC v2.7
/// </summary>
public sealed record RagMetricsDto(
    long TookMs,
    int Returned
);

/// <summary>
/// Response pour POST /rag/search — CDC v2.7
/// Contient items citables + métriques
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
