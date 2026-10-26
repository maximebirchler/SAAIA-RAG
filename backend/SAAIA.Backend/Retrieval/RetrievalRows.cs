

namespace SAAIA.Backend.Retrieval;

internal static partial class RetrievalRows
{

    internal sealed record DocVersionRow(
        Guid DocId,
        string DocPath,
        string Status,
        int IndexedVersion,
        int? CurrentRevisionIngestionVersion,
        string? ContentHashHex,
        string? VectorPlanFingerprint = null);

    internal sealed record DocProfileContentCardsRow(
        Guid DocId,
        string? ContentCardsJson);

    internal sealed record ExactMatchRow(
        Guid DocId,
        string DocPath,
        string DocName,
        string? Category,
        int PageStart,
        int PageEnd,
        int? OffsetStart,
        int? OffsetEnd,
        Guid ExactMatchEntryId,
        int ChunkIndex,
        string Text,
        int IngestionVersion,
        string? HashDoc,
        string MatchKind,
        string? SectionTitle,
        string? MatchedTerm,
        string? ExtractionTextStatus,
        bool? ExtractionTextSparse,
        bool? ExtractionOcrCandidate,
        string? ExtractionQualitySignalsJson);


    internal sealed record MetadataReferenceRow(
        Guid DocId,
        string DocPath,
        string DocName,
        string? Category,
        int IngestionVersion,
        string? HashDoc);


    internal sealed record TopCategoryOrderRow(
        string Path,
        int DisplayOrder);


    internal sealed class RagExtractionDocumentQualityRow
    {
        public Guid DocId { get; set; }
        public string DocPath { get; set; } = "";
        public string? ExtractionSource { get; set; }
        public bool OcrAttempted { get; set; }
        public bool OcrApplied { get; set; }
        public string QualityStatus { get; set; } = "unknown";
        public double ExtractionConfidence { get; set; } = 0.50;
        public bool ManualReviewRecommended { get; set; }
        public string? OcrLanguages { get; set; }
        public long? OcrDurationMs { get; set; }
        public string? NativeTextStatus { get; set; }
        public bool? NativeOcrRecommended { get; set; }
        public string? OcrDiagnosticsJson { get; set; }
        public int PageCount { get; set; }
        public int TextPageCount { get; set; }
        public int EmptyPageCount { get; set; }
        public int SparsePageCount { get; set; }
        public int ImagePageCount { get; set; }
        public int PageWarningCount { get; set; }
        public int PageReviewRecommendedCount { get; set; }
        public string TextStatus { get; set; } = "unknown";
        public bool OcrRecommended { get; set; }
        public string? SignalsJson { get; set; }
        public string? RetrievalChunkQualityJson { get; set; }
    }


    internal sealed class RagExtractionPageQualityRow
    {
        public Guid DocId { get; set; }
        public string DocPath { get; set; } = "";
        public int PageNumber { get; set; }
        public int CharCount { get; set; }
        public int WordCount { get; set; }
        public int ImageCount { get; set; }
        public int UnitCount { get; set; }
        public string? UnitTextsJson { get; set; }
        public int ChunkCount { get; set; }
        public string? SignalsJson { get; set; }
    }


    internal sealed record RagExtractionPageQuality(
        string QualityStatus,
        double ExtractionConfidence,
        bool ManualReviewRecommended,
        string TextStatus,
        string[] Signals);


    internal sealed record RagOcrDiagnosticsSummary(
        string? Mode = null,
        string? FailureReason = null,
        string? AppliedReason = null,
        bool? TimedOut = null,
        int? AttemptedPageCount = null,
        int? SkippedPageCount = null,
        int? PagesWithNovelTextCount = null);


    internal sealed record RagRetrievalQualityScore(
        int SelectionPenalty,
        int ChunkPenalty,
        double ScoreMultiplier,
        double ScoreOffset,
        bool ManualReviewRecommended,
        bool OcrLikelyNeeded)
    {
        public static RagRetrievalQualityScore Clean { get; } = new(
            SelectionPenalty: 0,
            ChunkPenalty: 0,
            ScoreMultiplier: 1.0,
            ScoreOffset: 0.0,
            ManualReviewRecommended: false,
            OcrLikelyNeeded: false);
    }


    // Must match the SELECT column order/names/types in SearchSparseMatchesAsync exactly —
    // Dapper materialises records by positional constructor binding, so a missing column
    // (e.g. SectionOrdinalPlaceholder, which we don't use but must declare) or a type
    // mismatch (ts_rank_cd returns float/Single, not double) makes the whole endpoint
    // throw "no matching constructor" at runtime.
    internal sealed class SparseMatchRow
    {
        public Guid DocId { get; set; }
        public string DocPath { get; set; } = string.Empty;
        public string DocName { get; set; } = string.Empty;
        public string? Category { get; set; }
        public int PageStart { get; set; }
        public int PageEnd { get; set; }
        public int? OffsetStart { get; set; }
        public int? OffsetEnd { get; set; }
        public Guid ChunkId { get; set; }
        public int ChunkIndex { get; set; }
        public string Text { get; set; } = string.Empty;
        public int IngestionVersion { get; set; }
        public string? HashDoc { get; set; }
        public string EmbedText { get; set; } = string.Empty;
        public Guid? SectionOrdinalPlaceholder { get; set; }
        public string? SectionTitle { get; set; }
        public string? HeadingPath { get; set; }
        public string ChunkType { get; set; } = "contextual_text_v1";
        public string? ContentRole { get; set; }
        public string? NavigationReason { get; set; }
        public string? OriginalChunkType { get; set; }
        public double? NavigationScore { get; set; }
        public double? ContentDensityScore { get; set; }
        public string? ExtractionTextStatus { get; set; }
        public bool? ExtractionTextSparse { get; set; }
        public bool? ExtractionOcrCandidate { get; set; }
        public string? ExtractionQualitySignalsJson { get; set; }
        public string? PrevChunkId { get; set; }
        public string? NextChunkId { get; set; }
        public string? SameSectionChunkId { get; set; }
        public string? MatchedContentCardsJson { get; set; }
        public float SparseRank { get; set; }
    }


    // PostgreSQL text[] columns are exposed to Dapper's positional record binder as
    // System.Array here. Normalize them to strings only when building profile signals.
    internal sealed record DocumentProfileMatchRow(
        Guid DocId,
        string DocPath,
        string DocName,
        string? Category,
        Guid ProfileId,
        int IngestionVersion,
        string? HashDoc,
        string Text,
        string SearchText,
        string? MetadataJson,
        string? Language,
        string? ProfileVersion,
        Array? Keywords,
        Array? Entities,
        Array? Topics,
        Array? HypotheticalQuestions,
        Array? Limits,
        double SparseRank,
        long MatchCount);


    internal sealed record ScoredDocumentProfileMatchRow(
        DocumentProfileMatchRow Row,
        double Score,
        double SpecificityBoost,
        double BaseScore);


    internal sealed class LinkedMatchRow
    {
        public Guid DocId { get; set; }
        public string DocPath { get; set; } = string.Empty;
        public string DocName { get; set; } = string.Empty;
        public string? Category { get; set; }
        public int PageStart { get; set; }
        public int PageEnd { get; set; }
        public int? OffsetStart { get; set; }
        public int? OffsetEnd { get; set; }
        public Guid ChunkId { get; set; }
        public int ChunkIndex { get; set; }
        public string Text { get; set; } = string.Empty;
        public int IngestionVersion { get; set; }
        public string? HashDoc { get; set; }
        public string ChunkType { get; set; } = "linked_context_v1";
        public string? ContentRole { get; set; }
        public string? NavigationReason { get; set; }
        public string? OriginalChunkType { get; set; }
        public double? NavigationScore { get; set; }
        public double? ContentDensityScore { get; set; }
        public string? ExtractionTextStatus { get; set; }
        public bool? ExtractionTextSparse { get; set; }
        public bool? ExtractionOcrCandidate { get; set; }
        public string? ExtractionQualitySignalsJson { get; set; }
        public string? SectionTitle { get; set; }
        public string? HeadingPath { get; set; }
        public string LinkType { get; set; } = string.Empty;
        public Guid AnchorSourceId { get; set; }
        public Guid AnchorChunkId { get; set; }
        public string? PrevChunkId { get; set; }
        public string? NextChunkId { get; set; }
        public string? SameSectionChunkId { get; set; }
    }

}
