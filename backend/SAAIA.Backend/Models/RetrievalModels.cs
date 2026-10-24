namespace SAAIA.Backend.Models;


public sealed record RagDocumentLanguageInfo(string DocLanguage, string? ProfileLanguage);


internal sealed record RagQualityAdjustedMatch(
    RagMatch Match,
    RagItemExtractionQualityDto? ExtractionQuality,
    double AdjustedScore);


public sealed class RagDocumentLanguageRow
{
    public string DocId { get; set; } = "";
    public string? ProfileLanguage { get; set; }
    public string? SummaryLanguage { get; set; }
    public string? RunDocumentLanguage { get; set; }
}


public sealed record RagDocumentSourceIdentity(
    string RevisionId,
    string SourceHash);


public sealed class RagDocumentSourceIdentityRow
{
    public string DocId { get; set; } = "";
    public string DocPath { get; set; } = "";
    public string? RevisionId { get; set; }
    public string? SourceHash { get; set; }
}


public sealed record RagQueryRequest(string Query, string? Category, int? TopK);


public sealed record RagSearchRequest(
    string Query,
    string? Category = null,
    int? TopK = null,
    double? MinScore = null,
    int? Candidates = null,
    int? MaxPerDoc = null,
    int? MaxPerPage = null,
    string? Mode = null
);


public sealed record RagSearchTimings(
    long TotalMs,
    long TeiMs,
    long RerankMs,
    long SparseMs,
    long QdrantMs,
    long ExactMs = 0,
    long QuotedTitleMs = 0,
    long LocalTitleTokenMs = 0,
    long TitleAnchorRouteMs = 0,
    long SparsePhaseMs = 0,
    long DenseMs = 0,
    long ProfileMs = 0,
    long LinkedMs = 0,
    long FusionMs = 0,
    long RerankPhaseMs = 0,
    long SelectionMs = 0);


public sealed record RagSearchResponse(
    string RequestId,
    string Query,
    string QueryNormalized,
    string? Category,
    int TopK,
    double MinScore,
    int Candidates,
    int MaxPerDoc,
    int MaxPerPage,
    int QdrantStatus,
    RagSearchTimings Timings,
    IReadOnlyList<RagMatch> Matches,
    IReadOnlyList<string>? DegradedRetrievers = null,
    IReadOnlyDictionary<string, string>? DegradedRetrieverErrors = null,
    RagRetrievalDiagnosticsDto? Diagnostics = null
);
