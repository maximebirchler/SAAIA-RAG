using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SAAIA.Contracts;

// ---------------------
// Chat-store contracts
// ---------------------
public sealed class CreateSessionResponse
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("clientUser")]
    public string? ClientUser { get; set; }

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; set; }
}

// ---------------------
// RAG contracts
// ---------------------
public sealed class RagMetrics
{
    [JsonPropertyName("tookMs")]
    public long TookMs { get; set; }

    [JsonPropertyName("returned")]
    public int Returned { get; set; }

    [JsonPropertyName("exactMatchReturned")]
    public int ExactMatchReturned { get; set; }

    [JsonPropertyName("denseReturned")]
    public int DenseReturned { get; set; }

    [JsonPropertyName("sparseReturned")]
    public int SparseReturned { get; set; }

    [JsonPropertyName("linkedReturned")]
    public int LinkedReturned { get; set; }

    [JsonPropertyName("retrieversUsed")]
    public List<string>? RetrieversUsed { get; set; }

    [JsonPropertyName("dataHash")]
    public string? DataHash { get; set; }

    [JsonPropertyName("ttlSeconds")]
    public int? TtlSeconds { get; set; }

    [JsonPropertyName("teiMs")]
    public long? TeiMs { get; set; }

    [JsonPropertyName("rerankMs")]
    public long? RerankMs { get; set; }

    [JsonPropertyName("sparseMs")]
    public long? SparseMs { get; set; }

    [JsonPropertyName("qdrantMs")]
    public long? QdrantMs { get; set; }

    [JsonPropertyName("candidatesEvaluated")]
    public int? CandidatesEvaluated { get; set; }

    [JsonPropertyName("degradedRetrievers")]
    public List<string>? DegradedRetrievers { get; set; }
}

public sealed class RagItemProvenance
{
    [JsonPropertyName("channel")]
    public string Channel { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("sourceHash")]
    public string? SourceHash { get; set; }

    [JsonPropertyName("chunkId")]
    public string? ChunkId { get; set; }

    [JsonPropertyName("pageStart")]
    public int? PageStart { get; set; }

    [JsonPropertyName("pageEnd")]
    public int? PageEnd { get; set; }

    [JsonPropertyName("offsetStart")]
    public int? OffsetStart { get; set; }

    [JsonPropertyName("offsetEnd")]
    public int? OffsetEnd { get; set; }
}

public sealed class RagItemContext
{
    [JsonPropertyName("chunkType")]
    public string? ChunkType { get; set; }

    [JsonPropertyName("sectionTitle")]
    public string? SectionTitle { get; set; }

    [JsonPropertyName("headingPath")]
    public string? HeadingPath { get; set; }

    [JsonPropertyName("prevChunkId")]
    public string? PrevChunkId { get; set; }

    [JsonPropertyName("nextChunkId")]
    public string? NextChunkId { get; set; }

    [JsonPropertyName("sameSectionChunkId")]
    public string? SameSectionChunkId { get; set; }

    [JsonPropertyName("contentRole")]
    public string? ContentRole { get; set; }

    [JsonPropertyName("navigationReason")]
    public string? NavigationReason { get; set; }

    [JsonPropertyName("originalChunkType")]
    public string? OriginalChunkType { get; set; }

    [JsonPropertyName("navigationScore")]
    public double? NavigationScore { get; set; }

    [JsonPropertyName("contentDensityScore")]
    public double? ContentDensityScore { get; set; }
}

public sealed class RagItemExtractionQuality
{
    [JsonPropertyName("extractionSource")]
    public string? ExtractionSource { get; set; }

    [JsonPropertyName("ocrAttempted")]
    public bool? OcrAttempted { get; set; }

    [JsonPropertyName("ocrApplied")]
    public bool? OcrApplied { get; set; }

    [JsonPropertyName("documentQualityStatus")]
    public string? DocumentQualityStatus { get; set; }

    [JsonPropertyName("documentExtractionConfidence")]
    public double? DocumentExtractionConfidence { get; set; }

    [JsonPropertyName("documentManualReviewRecommended")]
    public bool? DocumentManualReviewRecommended { get; set; }

    [JsonPropertyName("pageQualityStatus")]
    public string? PageQualityStatus { get; set; }

    [JsonPropertyName("pageExtractionConfidence")]
    public double? PageExtractionConfidence { get; set; }

    [JsonPropertyName("pageManualReviewRecommended")]
    public bool? PageManualReviewRecommended { get; set; }

    [JsonPropertyName("textStatus")]
    public string? TextStatus { get; set; }

    [JsonPropertyName("ocrRecommended")]
    public bool? OcrRecommended { get; set; }

    [JsonPropertyName("signals")]
    public List<string>? Signals { get; set; }

    [JsonPropertyName("chunkTextStatus")]
    public string? ChunkTextStatus { get; set; }

    [JsonPropertyName("chunkTextSparse")]
    public bool? ChunkTextSparse { get; set; }

    [JsonPropertyName("chunkOcrCandidate")]
    public bool? ChunkOcrCandidate { get; set; }

    [JsonPropertyName("chunkQualitySignals")]
    public List<string>? ChunkQualitySignals { get; set; }

    [JsonPropertyName("diagnosticSummary")]
    public RagItemExtractionDiagnosticSummary? DiagnosticSummary { get; set; }
}

public sealed class RagItemExtractionDiagnosticSummary
{
    [JsonPropertyName("nativeTextStatus")]
    public string? NativeTextStatus { get; set; }

    [JsonPropertyName("nativeOcrRecommended")]
    public bool? NativeOcrRecommended { get; set; }

    [JsonPropertyName("ocrMode")]
    public string? OcrMode { get; set; }

    [JsonPropertyName("ocrLanguages")]
    public string? OcrLanguages { get; set; }

    [JsonPropertyName("ocrDurationMs")]
    public long? OcrDurationMs { get; set; }

    [JsonPropertyName("ocrFailureReason")]
    public string? OcrFailureReason { get; set; }

    [JsonPropertyName("ocrAppliedReason")]
    public string? OcrAppliedReason { get; set; }

    [JsonPropertyName("ocrTimedOut")]
    public bool? OcrTimedOut { get; set; }

    [JsonPropertyName("ocrAttemptedPageCount")]
    public int? OcrAttemptedPageCount { get; set; }

    [JsonPropertyName("ocrSkippedPageCount")]
    public int? OcrSkippedPageCount { get; set; }

    [JsonPropertyName("ocrPagesWithNovelTextCount")]
    public int? OcrPagesWithNovelTextCount { get; set; }

    [JsonPropertyName("pageCount")]
    public int? PageCount { get; set; }

    [JsonPropertyName("textPageCount")]
    public int? TextPageCount { get; set; }

    [JsonPropertyName("emptyPageCount")]
    public int? EmptyPageCount { get; set; }

    [JsonPropertyName("sparsePageCount")]
    public int? SparsePageCount { get; set; }

    [JsonPropertyName("imagePageCount")]
    public int? ImagePageCount { get; set; }

    [JsonPropertyName("pageWarningCount")]
    public int? PageWarningCount { get; set; }

    [JsonPropertyName("pageReviewRecommendedCount")]
    public int? PageReviewRecommendedCount { get; set; }
}

public sealed class RagItemContentCard
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("contentCardId")]
    public string? ContentCardId { get; set; }

    [JsonPropertyName("pageStart")]
    public int? PageStart { get; set; }

    [JsonPropertyName("pageEnd")]
    public int? PageEnd { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("signals")]
    public List<string>? Signals { get; set; }

    [JsonPropertyName("evidence")]
    public JsonElement? Evidence { get; set; }
}

public sealed class RagItemSelectionHints
{
    [JsonPropertyName("evidenceRole")]
    public string EvidenceRole { get; set; } = "";

    [JsonPropertyName("actionabilityScore")]
    public int ActionabilityScore { get; set; }

    [JsonPropertyName("supportScore")]
    public int SupportScore { get; set; }

    [JsonPropertyName("fragmentScore")]
    public int FragmentScore { get; set; }

    [JsonPropertyName("navigationScore")]
    public int NavigationScore { get; set; }

    [JsonPropertyName("qualityPenalty")]
    public int QualityPenalty { get; set; }
}

public sealed class RagItemProfileSignals
{
    [JsonPropertyName("profileVersion")]
    public string? ProfileVersion { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("keywords")]
    public List<string>? Keywords { get; set; }

    [JsonPropertyName("entities")]
    public List<string>? Entities { get; set; }

    [JsonPropertyName("topics")]
    public List<string>? Topics { get; set; }

    [JsonPropertyName("hypotheticalQuestions")]
    public List<string>? HypotheticalQuestions { get; set; }

    [JsonPropertyName("limits")]
    public List<string>? Limits { get; set; }

    [JsonPropertyName("matchedTerms")]
    public List<string>? MatchedTerms { get; set; }

    [JsonPropertyName("matchCount")]
    public int? MatchCount { get; set; }
}

public sealed class RagItem
{
    [JsonPropertyName("score")]
    public double Score { get; set; }

    [JsonPropertyName("docId")]
    public string? DocId { get; set; }

    [JsonPropertyName("docName")]
    public string DocName { get; set; } = "";

    [JsonPropertyName("docPath")]
    public string? DocPath { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("docLanguage")]
    public string? DocLanguage { get; set; }

    [JsonPropertyName("profileLanguage")]
    public string? ProfileLanguage { get; set; }

    [JsonPropertyName("pageStart")]
    public int? PageStart { get; set; }

    [JsonPropertyName("pageEnd")]
    public int? PageEnd { get; set; }

    [JsonPropertyName("chunkId")]
    public string? ChunkId { get; set; }

    [JsonPropertyName("chunkIndex")]
    public int? ChunkIndex { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("snippet")]
    public string? Snippet { get; set; }

    [JsonPropertyName("contextualSnippet")]
    public string? ContextualSnippet { get; set; }

    [JsonPropertyName("categoryRef")]
    public string? CategoryRef { get; set; }

    [JsonPropertyName("categoryPath")]
    public string? CategoryPath { get; set; }

    [JsonPropertyName("retriever")]
    public string? Retriever { get; set; }

    [JsonPropertyName("provenance")]
    public string? Provenance { get; set; }

    [JsonPropertyName("exactMatchHit")]
    public bool? ExactMatchHit { get; set; }

    [JsonPropertyName("sourceHash")]
    public string? SourceHash { get; set; }

    [JsonPropertyName("embeddingBasis")]
    public string? EmbeddingBasis { get; set; }

    [JsonPropertyName("chunkType")]
    public string? ChunkType { get; set; }

    [JsonPropertyName("sectionTitle")]
    public string? SectionTitle { get; set; }

    [JsonPropertyName("headingPath")]
    public string? HeadingPath { get; set; }

    [JsonPropertyName("prevChunkId")]
    public string? PrevChunkId { get; set; }

    [JsonPropertyName("nextChunkId")]
    public string? NextChunkId { get; set; }

    [JsonPropertyName("sameSectionChunkId")]
    public string? SameSectionChunkId { get; set; }

    [JsonPropertyName("provenanceInfo")]
    public RagItemProvenance? ProvenanceInfo { get; set; }

    [JsonPropertyName("context")]
    public RagItemContext? Context { get; set; }

    [JsonPropertyName("rerankScore")]
    public double? RerankScore { get; set; }

    [JsonPropertyName("hasTable")]
    public bool? HasTable { get; set; }

    [JsonPropertyName("hasWarning")]
    public bool? HasWarning { get; set; }

    [JsonPropertyName("hypQuestionsMatched")]
    public bool? HypQuestionsMatched { get; set; }

    [JsonPropertyName("extractionQuality")]
    public RagItemExtractionQuality? ExtractionQuality { get; set; }

    [JsonPropertyName("matchedContentCards")]
    public List<RagItemContentCard>? MatchedContentCards { get; set; }

    [JsonPropertyName("selectionHints")]
    public RagItemSelectionHints? SelectionHints { get; set; }

    [JsonPropertyName("profileSignals")]
    public RagItemProfileSignals? ProfileSignals { get; set; }
}

public sealed class RagAnswerGuidance
{
    [JsonPropertyName("behavior")]
    public string Behavior { get; set; } = "";

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";

    [JsonPropertyName("responseShape")]
    public string? ResponseShape { get; set; }

    [JsonPropertyName("clarifyingQuestion")]
    public string? ClarifyingQuestion { get; set; }

    [JsonPropertyName("qualificationNote")]
    public string? QualificationNote { get; set; }

    [JsonPropertyName("matchedDocHints")]
    public List<string>? MatchedDocHints { get; set; }
}

public sealed class RagSearchResponse
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = "";

    [JsonPropertyName("query")]
    public string Query { get; set; } = "";

    [JsonPropertyName("queryNormalized")]
    public string? QueryNormalized { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("topK")]
    public int TopK { get; set; }

    [JsonPropertyName("minScore")]
    public double MinScore { get; set; }

    [JsonPropertyName("candidates")]
    public int Candidates { get; set; }

    [JsonPropertyName("maxPerDoc")]
    public int MaxPerDoc { get; set; }

    [JsonPropertyName("maxPerPage")]
    public int MaxPerPage { get; set; }

    [JsonPropertyName("metrics")]
    public RagMetrics Metrics { get; set; } = new();

    [JsonPropertyName("items")]
    public List<RagItem> Items { get; set; } = new();

    [JsonPropertyName("guidance")]
    public RagAnswerGuidance? Guidance { get; set; }
}


// ---------------------
// Catalog / capabilities contracts (transition to snapshot + capabilities)
// ---------------------
public sealed class CatalogSnapshotResponse
{
    [JsonPropertyName("snapshotId")]
    public string SnapshotId { get; set; } = "";

    [JsonPropertyName("catalogVersion")]
    public string CatalogVersion { get; set; } = "";

    [JsonPropertyName("etag")]
    public string ETag { get; set; } = "";

    [JsonPropertyName("categories")]
    public List<CatalogSnapshotCategoryItem> Categories { get; set; } = new();

    [JsonPropertyName("totals")]
    public CatalogSnapshotTotals Totals { get; set; } = new();
}

public sealed class CatalogSnapshotCategoryItem
{
    [JsonPropertyName("categoryRef")]
    public string CategoryRef { get; set; } = "";

    [JsonPropertyName("categoryPath")]
    public string CategoryPath { get; set; } = "";

    [JsonPropertyName("displayOrder")]
    public int DisplayOrder { get; set; }

    [JsonPropertyName("canonicalName")]
    public string CanonicalName { get; set; } = "";

    [JsonPropertyName("documentCount")]
    public int DocumentCount { get; set; }

    [JsonPropertyName("lastUpdatedUtc")]
    public DateTimeOffset? LastUpdatedUtc { get; set; }

    [JsonPropertyName("aliases")]
    public List<string> Aliases { get; set; } = new();
}

public sealed class CatalogSnapshotTotals
{
    [JsonPropertyName("documents")]
    public long Documents { get; set; }

    [JsonPropertyName("categories")]
    public int Categories { get; set; }
}

public sealed class CatalogDocumentItem
{
    [JsonPropertyName("documentRef")]
    public string DocumentRef { get; set; } = "";

    [JsonPropertyName("docId")]
    public Guid DocId { get; set; }

    [JsonPropertyName("docPath")]
    public string DocPath { get; set; } = "";

    [JsonPropertyName("canonicalName")]
    public string CanonicalName { get; set; } = "";

    [JsonPropertyName("categoryRef")]
    public string? CategoryRef { get; set; }

    [JsonPropertyName("categoryCanonicalName")]
    public string? CategoryCanonicalName { get; set; }

    [JsonPropertyName("categoryPath")]
    public string? CategoryPath { get; set; }

    [JsonPropertyName("pages")]
    public int? Pages { get; set; }

    [JsonPropertyName("sourceHash")]
    public string? SourceHash { get; set; }

    [JsonPropertyName("docLanguage")]
    public string? DocLanguage { get; set; }

    [JsonPropertyName("profileLanguage")]
    public string? ProfileLanguage { get; set; }

    [JsonPropertyName("lastModifiedUtc")]
    public DateTimeOffset? LastModifiedUtc { get; set; }
}

public sealed class CatalogDocumentListResponse
{
    [JsonPropertyName("value")]
    public List<CatalogDocumentItem> Value { get; set; } = new();

    [JsonPropertyName("nextLink")]
    public string? NextLink { get; set; }
}

public sealed class AuthCapabilitiesResponse
{
    [JsonPropertyName("user")]
    public AuthCapabilitiesUser User { get; set; } = new();

    [JsonPropertyName("ui")]
    public AuthCapabilitiesUi Ui { get; set; } = new();

    [JsonPropertyName("capabilities")]
    public AuthCapabilitiesPayload Capabilities { get; set; } = new();
}

public sealed class AuthCapabilitiesUser
{
    [JsonPropertyName("isAuthenticated")]
    public bool IsAuthenticated { get; set; }

    [JsonPropertyName("isAdmin")]
    public bool IsAdmin { get; set; }

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";
}

public sealed class AuthCapabilitiesUi
{
    [JsonPropertyName("defaultLocale")]
    public string DefaultLocale { get; set; } = "fr-CH";
}

public sealed class AuthCapabilitiesPayload
{
    [JsonPropertyName("directCommands")]
    public List<AuthCapabilityCommand> DirectCommands { get; set; } = new();

    [JsonPropertyName("adminCommands")]
    public List<AuthCapabilityCommand> AdminCommands { get; set; } = new();
}

public sealed class AuthCapabilityCommand
{
    [JsonPropertyName("commandId")]
    public string CommandId { get; set; } = "";

    [JsonPropertyName("visibility")]
    public string Visibility { get; set; } = "always";

    [JsonPropertyName("requiresContext")]
    public List<string> RequiresContext { get; set; } = new();

    [JsonPropertyName("argsSchema")]
    public JsonElement ArgsSchema { get; set; }
}

// ---------------------
// Resolve contracts
// ---------------------

public sealed class SourceResolveRequest
{
    [JsonPropertyName("ref")]
    public string? Ref { get; set; }

    [JsonPropertyName("pdfRef")]
    public string? PdfRef { get; set; }
}

public sealed class ResolvedSourceDto
{
    [JsonPropertyName("docId")]
    public Guid? DocId { get; set; }

    [JsonPropertyName("docPath")]
    public string DocPath { get; set; } = "";

    [JsonPropertyName("docName")]
    public string DocName { get; set; } = "";

    [JsonPropertyName("pageStart")]
    public int PageStart { get; set; } = 1;

    [JsonPropertyName("pageEnd")]
    public int PageEnd { get; set; } = 1;

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("sourceHash")]
    public string? SourceHash { get; set; }

    [JsonPropertyName("docLanguage")]
    public string? DocLanguage { get; set; }

    [JsonPropertyName("profileLanguage")]
    public string? ProfileLanguage { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("categoryRef")]
    public string? CategoryRef { get; set; }

    [JsonPropertyName("categoryPath")]
    public string? CategoryPath { get; set; }

    [JsonPropertyName("chunkId")]
    public string? ChunkId { get; set; }

    [JsonPropertyName("extractionQuality")]
    public RagItemExtractionQuality? ExtractionQuality { get; set; }

    [JsonPropertyName("matchedContentCards")]
    public List<RagItemContentCard>? MatchedContentCards { get; set; }

    [JsonPropertyName("selectionHints")]
    public RagItemSelectionHints? SelectionHints { get; set; }

    [JsonPropertyName("profileSignals")]
    public RagItemProfileSignals? ProfileSignals { get; set; }
}

public sealed class SourceResolveResponse
{
    [JsonPropertyName("source")]
    public ResolvedSourceDto? Source { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("requestedRef")]
    public string? RequestedRef { get; set; }
}

public sealed class SummaryGetResponse
{
    [JsonPropertyName("docId")]
    public Guid DocId { get; set; }

    [JsonPropertyName("docPath")]
    public string? DocPath { get; set; }

    [JsonPropertyName("docName")]
    public string? DocName { get; set; }

    [JsonPropertyName("level")]
    public string Level { get; set; } = "medium";

    [JsonPropertyName("docLanguage")]
    public string? DocLanguage { get; set; }

    [JsonPropertyName("sourceHash")]
    public string? SourceHash { get; set; }

    [JsonPropertyName("pageStart")]
    public int? PageStart { get; set; }

    [JsonPropertyName("pageEnd")]
    public int? PageEnd { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("profileLanguage")]
    public string? ProfileLanguage { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("categoryRef")]
    public string? CategoryRef { get; set; }

    [JsonPropertyName("categoryPath")]
    public string? CategoryPath { get; set; }

    [JsonPropertyName("chunkId")]
    public string? ChunkId { get; set; }

    [JsonPropertyName("extractionQuality")]
    public RagItemExtractionQuality? ExtractionQuality { get; set; }

    [JsonPropertyName("matchedContentCards")]
    public List<RagItemContentCard>? MatchedContentCards { get; set; }

    [JsonPropertyName("selectionHints")]
    public RagItemSelectionHints? SelectionHints { get; set; }

    [JsonPropertyName("profileSignals")]
    public RagItemProfileSignals? ProfileSignals { get; set; }

    [JsonPropertyName("source")]
    public ResolvedSourceDto? Source { get; set; }

    [JsonPropertyName("summaryText")]
    public string SummaryText { get; set; } = "";

    [JsonPropertyName("meta")]
    public object? Meta { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("isFresh")]
    public bool IsFresh { get; set; }
}

public sealed class ResolveCategoryRequest
{
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("categoryPath")]
    public string? CategoryPath { get; set; }

    [JsonPropertyName("categoryRef")]
    public string? CategoryRef { get; set; }
}

public sealed class ResolvedCategoryItem
{
    [JsonPropertyName("categoryRef")]
    public string CategoryRef { get; set; } = "";

    [JsonPropertyName("categoryPath")]
    public string CategoryPath { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("ordinal")]
    public int? Ordinal { get; set; }

    [JsonPropertyName("totalDocuments")]
    public int? TotalDocuments { get; set; }

    [JsonPropertyName("aliases")]
    public List<string> Aliases { get; set; } = new();
}

public sealed class ResolveCategoryResponse
{
    [JsonPropertyName("items")]
    public List<ResolvedCategoryItem> Items { get; set; } = new();
}
