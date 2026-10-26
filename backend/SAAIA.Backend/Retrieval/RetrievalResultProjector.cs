using System.Globalization;
using System.Text.Json;
using Dapper;
using Npgsql;
using static SAAIA.Backend.Retrieval.CanonicalRetrieval;
using static SAAIA.Backend.Retrieval.RetrievalAnswerGuidance;
using static SAAIA.Backend.Retrieval.RetrievalCandidateSignals;
using static SAAIA.Backend.Retrieval.RetrievalMetadata;
using static SAAIA.Backend.Retrieval.RetrievalNavigation;
using static SAAIA.Backend.Retrieval.RetrievalRows;

namespace SAAIA.Backend.Retrieval;

internal static partial class RetrievalResultProjector
{

    internal static async Task<RagSearchResponseDto> BuildSearchResponseDtoAsync(
        RetrievalExecutionContext context,
        NpgsqlDataSource ds,
        RagOptions rag,
        IHttpClientFactory httpFactory,
        RagSearchRequestDto req)
    {
        var resp = await RetrievalEngine.SearchAsync(context, ds, rag, httpFactory, req);
        var tenantId = context.TenantId;
        var ct = context.CancellationToken;
        var categoryRefsTask = LoadTopCategoryRefsAsync(ds, tenantId, resp.Matches, ct);
        var hypQuestionsTask = LoadHypQuestionsMatchedByDocPathAsync(
            ds,
            tenantId,
            resp.Query,
            resp.Matches,
            ct);
        var extractionQualityTask = LoadRagExtractionQualityAsync(ds, tenantId, resp.Matches, ct);
        var documentLanguagesTask = LoadRagDocumentLanguagesAsync(ds, tenantId, resp.Matches, ct);
        var documentSourceIdentitiesTask = req.SourceBackedCanonical == true
            ? LoadRagCanonicalDocumentSourceIdentitiesAsync(ds, tenantId, resp.Matches, ct)
            : LoadRagDocumentSourceIdentitiesAsync(ds, tenantId, resp.Matches, ct);

        await Task.WhenAll(categoryRefsTask, hypQuestionsTask, extractionQualityTask, documentLanguagesTask, documentSourceIdentitiesTask);
        var categoryRefsByTopLevelPath = await categoryRefsTask;
        var hypQuestionsMatchedByDocPath = await hypQuestionsTask;
        var extractionQualityByMatch = await extractionQualityTask;
        var documentLanguagesByDocId = await documentLanguagesTask;
        var documentSourceIdentitiesByDocId = await documentSourceIdentitiesTask;
        if (req.SourceBackedCanonical == true)
            resp = RetainCurrentCanonicalSourceIdentities(resp, documentSourceIdentitiesByDocId);
        var effectiveExtractionQualityByMatch = BuildEffectiveExtractionQualityByMatch(resp.Matches, extractionQualityByMatch);
        var qualityAdjustedMatches = req.SourceBackedCanonical == true
            ? resp.Matches
                .Select(match => new RagQualityAdjustedMatch(
                    match,
                    ResolveExtractionQuality(match, effectiveExtractionQualityByMatch),
                    match.Score))
                .ToList()
            : BuildQualityAdjustedMatchesPreservingRank(
                resp.Matches,
                effectiveExtractionQualityByMatch);

        return new RagSearchResponseDto(
            RequestId: resp.RequestId,
            Query: resp.Query,
            QueryNormalized: resp.QueryNormalized,
            Category: resp.Category,
            TopK: resp.TopK,
            MinScore: resp.MinScore,
            Candidates: resp.Candidates,
            MaxPerDoc: resp.MaxPerDoc,
            MaxPerPage: resp.MaxPerPage,
            Metrics: new RagMetricsDto(
                TookMs: resp.Timings.TotalMs,
                Returned: resp.Matches.Count,
                ExactMatchReturned: resp.Matches.Count(m => string.Equals(m.EmbeddingBasis, "exact_match_v1", StringComparison.Ordinal)),
                DenseReturned: resp.Matches.Count(m => string.Equals(ResolveRetriever(m), "dense_qdrant", StringComparison.Ordinal)),
                SparseReturned: resp.Matches.Count(m => string.Equals(ResolveRetriever(m), "sparse_bm25", StringComparison.Ordinal)),
                LinkedReturned: resp.Matches.Count(m => string.Equals(m.EmbeddingBasis, "linked_context_v1", StringComparison.Ordinal)),
                RetrieversUsed: resp.Matches
                    .Select(ResolveRetriever)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                DataHash: ComputeDataHash(resp.Matches),
                TtlSeconds: 600,
                TeiMs: resp.Timings.TeiMs,
                RerankMs: resp.Timings.RerankMs,
                SparseMs: resp.Timings.SparseMs,
                QdrantMs: resp.Timings.QdrantMs,
                CandidatesEvaluated: resp.Candidates,
                DegradedRetrievers: resp.DegradedRetrievers,
                DegradedRetrieverErrors: resp.DegradedRetrieverErrors,
                ExactMs: resp.Timings.ExactMs,
                QuotedTitleMs: resp.Timings.QuotedTitleMs,
                LocalTitleTokenMs: resp.Timings.LocalTitleTokenMs,
                TitleAnchorRouteMs: resp.Timings.TitleAnchorRouteMs,
                SparsePhaseMs: resp.Timings.SparsePhaseMs,
                DenseMs: resp.Timings.DenseMs,
                ProfileMs: resp.Timings.ProfileMs,
                LinkedMs: resp.Timings.LinkedMs,
                FusionMs: resp.Timings.FusionMs,
                RerankPhaseMs: resp.Timings.RerankPhaseMs,
                SelectionMs: resp.Timings.SelectionMs
            ),
            Items: qualityAdjustedMatches
                .Select(item =>
                {
                    var m = item.Match;
                    var categoryPath = BuildDocumentCategoryPath(m.DocPath);
                    var category = ResolveMatchCategory(m, resp.Category);
                    var languageInfo = ResolveDocumentLanguageInfo(m, documentLanguagesByDocId);
                    var sourceIdentity = ResolveDocumentSourceIdentity(
                        m,
                        documentSourceIdentitiesByDocId);
                    var sourceHash = sourceIdentity?.SourceHash;
                    return new RagItemDto(
                        Score: item.AdjustedScore,
                        DocId: m.DocId,
                        DocName: m.DocName ?? "Unknown",
                        DocPath: m.DocPath,
                        Category: category,
                        CategoryRef: ResolveCategoryRef(categoryPath, categoryRefsByTopLevelPath),
                        DocLanguage: languageInfo.DocLanguage,
                        ProfileLanguage: languageInfo.ProfileLanguage,
                        PageStart: m.PageStart,
                        PageEnd: m.PageEnd,
                        ChunkId: m.ChunkId,
                        ChunkIndex: m.ChunkIndex,
                        Text: m.Text ?? string.Empty,
                        Retriever: ResolveRetriever(m),
                        Provenance: ResolveProvenance(m),
                        ExactMatchHit: string.Equals(m.EmbeddingBasis, "exact_match_v1", StringComparison.Ordinal),
                        SourceHash: sourceHash,
                        RevisionId: sourceIdentity?.RevisionId,
                        EmbeddingBasis: m.EmbeddingBasis,
                        ChunkType: m.ChunkType,
                        SectionTitle: m.SectionTitle,
                        HeadingPath: m.HeadingPath,
                        PrevChunkId: m.PrevChunkId,
                        NextChunkId: m.NextChunkId,
                        SameSectionChunkId: m.SameSectionChunkId,
                        ProvenanceInfo: BuildProvenanceInfo(m, sourceHash, allowLegacyHashFallback: false),
                        Context: BuildContextInfo(m),
                        CategoryPath: categoryPath,
                        Snippet: BuildSnippet(m.Text, query: resp.Query),
                        RerankScore: m.RerankScore,
                        HasTable: DetectHasTable(m.Text),
                        HasWarning: DetectHasWarning(m.Text, m.ChunkType),
                        ContextualSnippet: req.IncludeContextualSnippet == true ? m.EmbedText : null,
                        HypQuestionsMatched: ResolveHypQuestionsMatched(m.DocPath, hypQuestionsMatchedByDocPath),
                        ExtractionQuality: item.ExtractionQuality,
                        MatchedContentCards: req.SourceBackedCanonical == true
                            ? null
                            : BuildMatchedContentCardDtos(m),
                        SelectionHints: req.SourceBackedCanonical == true
                            ? null
                            : BuildSelectionHints(m, item.ExtractionQuality),
                        ProfileSignals: req.SourceBackedCanonical == true
                            ? null
                            : BuildProfileSignalsDto(m.ProfileSignals)
                    );
                })
                .ToList(),
            Guidance: req.SourceBackedCanonical == true
                ? null
                : BuildAnswerGuidance(
                    resp.Query,
                    qualityAdjustedMatches.Select(static item => item.Match).ToList(),
                    effectiveExtractionQualityByMatch),
            Diagnostics: resp.Diagnostics
        );
    }


    internal static async Task<IReadOnlyDictionary<string, RagItemExtractionQualityDto>> LoadRagExtractionQualityAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        IReadOnlyList<RagMatch> matches,
        CancellationToken ct)
    {
        var docIds = matches
            .Select(static match => Guid.TryParse(match.DocId, out var docId) ? docId : (Guid?)null)
            .Where(static docId => docId.HasValue)
            .Select(static docId => docId!.Value)
            .Distinct()
            .ToArray();
        var docPaths = matches
            .Select(static match => NormalizeRagDocPath(match.DocPath))
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (docIds.Length == 0 && docPaths.Length == 0)
            return new Dictionary<string, RagItemExtractionQualityDto>(StringComparer.OrdinalIgnoreCase);

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string docSql = """
WITH scoped_docs AS (
  SELECT
    d.doc_id,
    d.tenant_id,
    d.doc_path,
    d.indexed_version,
    rev.revision_id
  FROM documents d
  LEFT JOIN LATERAL (
    SELECT r.revision_id
    FROM document_revisions r
    WHERE r.tenant_id = d.tenant_id
      AND r.doc_id = d.doc_id
      AND r.indexed_version = d.indexed_version
    ORDER BY r.published_at DESC NULLS LAST
    LIMIT 1
  ) rev ON true
  WHERE d.tenant_id=@tenant
    AND (
      d.doc_id = ANY(@docIds)
      OR d.doc_path = ANY(@docPaths)
    )
),
latest_runs AS (
  SELECT
    sd.doc_id,
    r.payload
  FROM scoped_docs sd
  LEFT JOIN LATERAL (
    SELECT payload
    FROM document_processing_runs pr
    WHERE pr.tenant_id=sd.tenant_id
      AND pr.doc_id=sd.doc_id
      AND pr.action='upsert'
      AND pr.status='done'
      AND (
        (sd.revision_id IS NOT NULL AND pr.revision_id = sd.revision_id)
        OR (sd.revision_id IS NULL AND pr.indexed_version_after = sd.indexed_version)
      )
    ORDER BY pr.finished_at DESC NULLS LAST, pr.started_at DESC NULLS LAST
    LIMIT 1
  ) r ON true
),
page_rows AS (
  SELECT
    sd.doc_id,
    sd.tenant_id,
    rev.revision_id,
    pi.page_number,
    COALESCE(pi.char_count, 0)::int AS char_count,
    CASE
      WHEN COALESCE(pi.metadata ->> 'wordCount', '') ~ '^[0-9]{1,9}$' THEN (pi.metadata ->> 'wordCount')::int
      ELSE 0
    END AS word_count,
    CASE
      WHEN COALESCE(pi.metadata ->> 'imageCount', '') ~ '^[0-9]{1,9}$' THEN (pi.metadata ->> 'imageCount')::int
      ELSE 0
    END AS image_count,
    LOWER(COALESCE(pi.metadata -> 'extractionQuality' ->> 'manualReviewRecommended', ''))='true' AS published_manual_review
  FROM scoped_docs sd
  LEFT JOIN document_revisions rev
    ON rev.tenant_id=sd.tenant_id
   AND rev.doc_id=sd.doc_id
   AND rev.indexed_version=sd.indexed_version
  LEFT JOIN document_page_index pi
    ON pi.tenant_id=sd.tenant_id
   AND pi.revision_id=rev.revision_id
),
page_projection AS (
  SELECT
    pr.*,
    COALESCE(uc.unit_count, 0) AS unit_count,
    COALESCE(cc.chunk_count, 0) AS chunk_count
  FROM page_rows pr
  LEFT JOIN LATERAL (
    SELECT COUNT(*)::int AS unit_count
    FROM document_units u
    WHERE u.tenant_id=pr.tenant_id
      AND u.revision_id=pr.revision_id
      AND pr.page_number IS NOT NULL
      AND u.page_start <= pr.page_number
      AND pr.page_number <= u.page_end
  ) uc ON true
  LEFT JOIN LATERAL (
    SELECT COUNT(*)::int AS chunk_count
    FROM retrieval_chunks rc
    WHERE rc.tenant_id=pr.tenant_id
      AND rc.revision_id=pr.revision_id
      AND pr.page_number IS NOT NULL
      AND rc.page_start <= pr.page_number
      AND pr.page_number <= rc.page_end
  ) cc ON true
),
page_quality AS (
  SELECT
    doc_id,
    COUNT(page_number)::int AS page_count,
    COUNT(*) FILTER (WHERE word_count > 0 AND char_count > 0)::int AS text_page_count,
    COUNT(*) FILTER (WHERE page_number IS NOT NULL AND (word_count <= 0 OR char_count <= 0))::int AS empty_page_count,
    COUNT(*) FILTER (
      WHERE page_number IS NOT NULL
        AND word_count > 0
        AND char_count > 0
        AND (word_count < 12 OR char_count < 80)
    )::int AS sparse_page_count,
    COUNT(*) FILTER (WHERE image_count > 0)::int AS image_page_count,
    COUNT(*) FILTER (
      WHERE page_number IS NOT NULL
        AND (
          published_manual_review
          OR ((word_count <= 0 OR char_count <= 0) AND chunk_count <= 0)
          OR (word_count > 0 AND char_count > 0 AND (word_count < 12 OR char_count < 80) AND chunk_count <= 0)
          OR (unit_count <= 0 AND chunk_count <= 0 AND (word_count >= 30 OR char_count >= 200))
        )
    )::int AS page_warning_count,
    COUNT(*) FILTER (
      WHERE page_number IS NOT NULL
        AND (
          published_manual_review
          OR (image_count > 0 AND (word_count <= 0 OR char_count <= 0))
          OR (image_count > 0 AND unit_count <= 0 AND chunk_count <= 0 AND word_count > 0 AND char_count > 0 AND (word_count < 12 OR char_count < 80))
          OR (unit_count <= 0 AND chunk_count <= 0 AND (word_count >= 30 OR char_count >= 200))
        )
    )::int AS page_review_recommended_count,
    COALESCE(SUM(word_count), 0)::int AS total_word_count,
    COALESCE(SUM(char_count), 0)::int AS total_char_count
  FROM page_projection
  GROUP BY doc_id
),
doc_quality_base AS (
  SELECT
    sd.doc_id AS "DocId",
    sd.doc_path AS "DocPath",
    lr.payload ->> 'extractionSource' AS "ExtractionSource",
    CASE
      WHEN LOWER(COALESCE(lr.payload ->> 'ocrAttempted', '')) IN ('true', 'false')
        THEN (lr.payload ->> 'ocrAttempted')::boolean
      ELSE false
    END AS "OcrAttempted",
    CASE
      WHEN LOWER(COALESCE(lr.payload ->> 'ocrApplied', '')) IN ('true', 'false')
        THEN (lr.payload ->> 'ocrApplied')::boolean
      ELSE false
    END AS "OcrApplied",
    NULLIF(lr.payload ->> 'ocrLanguages', '') AS "OcrLanguages",
    CASE
      WHEN COALESCE(lr.payload ->> 'ocrDurationMs', '') ~ '^[0-9]{1,18}$'
        THEN (lr.payload ->> 'ocrDurationMs')::bigint
      ELSE NULL
    END AS "OcrDurationMs",
    lr.payload #>> '{nativeExtractionQuality,textStatus}' AS "NativeTextStatus",
    CASE
      WHEN LOWER(COALESCE(lr.payload #>> '{nativeExtractionQuality,ocrRecommended}', '')) IN ('true', 'false')
        THEN (lr.payload #>> '{nativeExtractionQuality,ocrRecommended}')::boolean
      ELSE NULL
    END AS "NativeOcrRecommended",
    (lr.payload -> 'ocrDiagnostics')::text AS "OcrDiagnosticsJson",
    lr.payload #>> '{extractionQuality,textStatus}' AS "RunTextStatus",
    CASE
      WHEN LOWER(COALESCE(lr.payload #>> '{extractionQuality,ocrRecommended}', '')) IN ('true', 'false')
        THEN (lr.payload #>> '{extractionQuality,ocrRecommended}')::boolean
      ELSE NULL
    END AS "RunOcrRecommended",
    COALESCE(
      CASE WHEN COALESCE(lr.payload #>> '{extractionQuality,pageCount}', '') ~ '^[0-9]{1,9}$' THEN (lr.payload #>> '{extractionQuality,pageCount}')::int ELSE NULL END,
      pq.page_count,
      0) AS "PageCount",
    COALESCE(
      CASE WHEN COALESCE(lr.payload #>> '{extractionQuality,textPageCount}', '') ~ '^[0-9]{1,9}$' THEN (lr.payload #>> '{extractionQuality,textPageCount}')::int ELSE NULL END,
      pq.text_page_count,
      0) AS "TextPageCount",
    COALESCE(
      CASE WHEN COALESCE(lr.payload #>> '{extractionQuality,emptyPageCount}', '') ~ '^[0-9]{1,9}$' THEN (lr.payload #>> '{extractionQuality,emptyPageCount}')::int ELSE NULL END,
      pq.empty_page_count,
      0) AS "EmptyPageCount",
    COALESCE(
      CASE WHEN COALESCE(lr.payload #>> '{extractionQuality,sparsePageCount}', '') ~ '^[0-9]{1,9}$' THEN (lr.payload #>> '{extractionQuality,sparsePageCount}')::int ELSE NULL END,
      pq.sparse_page_count,
      0) AS "SparsePageCount",
    COALESCE(pq.image_page_count, 0) AS "ImagePageCount",
    COALESCE(pq.page_warning_count, 0) AS "PageWarningCount",
    COALESCE(pq.page_review_recommended_count, 0) AS "PageReviewRecommendedCount",
    COALESCE(
      CASE WHEN COALESCE(lr.payload #>> '{extractionQuality,totalWordCount}', '') ~ '^[0-9]{1,9}$' THEN (lr.payload #>> '{extractionQuality,totalWordCount}')::int ELSE NULL END,
      pq.total_word_count,
      0) AS "TotalWordCount",
    COALESCE(
      CASE WHEN COALESCE(lr.payload #>> '{extractionQuality,totalCharCount}', '') ~ '^[0-9]{1,9}$' THEN (lr.payload #>> '{extractionQuality,totalCharCount}')::int ELSE NULL END,
      pq.total_char_count,
      0) AS "TotalCharCount",
    CASE
      WHEN COALESCE(pq.page_count, 0) <= 0 THEN 'unknown'
      WHEN COALESCE(pq.total_word_count, 0) <= 0 THEN 'empty_text'
      WHEN (COALESCE(pq.empty_page_count, 0)::double precision / GREATEST(COALESCE(pq.page_count, 0), 1)) >= 0.6 THEN 'low_text'
      WHEN (COALESCE(pq.sparse_page_count, 0)::double precision / GREATEST(COALESCE(pq.page_count, 0), 1)) >= 0.6
           AND (COALESCE(pq.total_word_count, 0)::double precision / GREATEST(COALESCE(pq.page_count, 0), 1)) < 30 THEN 'low_text'
      WHEN (COALESCE(pq.total_word_count, 0)::double precision / GREATEST(COALESCE(pq.page_count, 0), 1)) < 10 THEN 'low_text'
      ELSE 'ok'
    END AS "ComputedTextStatus",
    lr.payload #>> '{extractionQuality,signals}' AS "RunSignalsJson",
    (lr.payload -> 'retrievalChunkQuality')::text AS "RetrievalChunkQualityJson"
  FROM scoped_docs sd
  LEFT JOIN latest_runs lr ON lr.doc_id=sd.doc_id
  LEFT JOIN page_quality pq ON pq.doc_id=sd.doc_id
),
doc_quality AS (
  SELECT
    *,
    COALESCE("RunTextStatus", "ComputedTextStatus") AS "TextStatus",
    COALESCE("RunOcrRecommended", "ComputedTextStatus" IN ('empty_text', 'low_text')) AS "OcrRecommended",
    COALESCE(
      "RunSignalsJson",
      CASE "ComputedTextStatus"
        WHEN 'empty_text' THEN '["no_text_extracted","ocr_recommended"]'
        WHEN 'low_text' THEN '["low_text_extraction","ocr_recommended"]'
        WHEN 'ok' THEN '["text_extraction_ok"]'
        ELSE '[]'
      END
    ) AS "SignalsJson"
  FROM doc_quality_base
),
doc_quality_scored AS (
  SELECT
    *,
    CASE
      WHEN "OcrApplied" AND "TextStatus"='ok' AND COALESCE("ExtractionSource", '')='pdf_text_plus_image_ocr' THEN 'image_ocr_applied_ok'
      WHEN "OcrApplied" AND "TextStatus"='ok' AND ("PageWarningCount" > 0 OR "PageReviewRecommendedCount" > 0) THEN 'ocr_applied_ok_with_page_warnings'
      WHEN "OcrApplied" AND "TextStatus"='ok' THEN 'ocr_applied_ok'
      WHEN "OcrApplied" AND "TextStatus" <> 'ok' THEN 'ocr_applied_low_confidence'
      WHEN "OcrAttempted" AND NOT "OcrApplied" AND "OcrRecommended" THEN 'ocr_failed_or_insufficient'
      WHEN "TextStatus"='empty_text' THEN 'manual_review_empty_text'
      WHEN "TextStatus"='low_text' THEN 'manual_review_low_text'
      WHEN "TextStatus"='unknown' THEN 'unknown'
      WHEN "TextStatus"='ok' AND "PageReviewRecommendedCount" > 0 THEN 'extraction_ok_with_page_review'
      WHEN "TextStatus"='ok' AND "PageWarningCount" > 0 THEN 'extraction_ok_with_page_warnings'
      WHEN "ImagePageCount" > 0 AND NOT "OcrApplied" THEN 'text_extraction_ok_with_images'
      ELSE 'extraction_ok'
    END AS "QualityStatus",
    CASE
      WHEN "OcrApplied" AND "TextStatus"='ok' AND COALESCE("ExtractionSource", '')='pdf_text_plus_image_ocr' THEN 0.92::double precision
      WHEN "OcrApplied" AND "TextStatus"='ok' AND ("PageWarningCount" > 0 OR "PageReviewRecommendedCount" > 0) THEN 0.86::double precision
      WHEN "OcrApplied" AND "TextStatus"='ok' THEN 0.90::double precision
      WHEN "TextStatus"='ok' AND "PageReviewRecommendedCount" > 0 THEN 0.82::double precision
      WHEN "TextStatus"='ok' AND "PageWarningCount" > 0 THEN 0.88::double precision
      WHEN "TextStatus"='ok' AND "ImagePageCount" > 0 AND NOT "OcrApplied" THEN 0.85::double precision
      WHEN "TextStatus"='ok' THEN 1.00::double precision
      WHEN "OcrApplied" AND "TextStatus" <> 'ok' THEN 0.45::double precision
      WHEN "OcrAttempted" AND NOT "OcrApplied" AND "OcrRecommended" THEN 0.30::double precision
      WHEN "TextStatus"='low_text' THEN 0.35::double precision
      WHEN "TextStatus"='empty_text' THEN 0.15::double precision
      ELSE 0.50::double precision
    END AS "ExtractionConfidence",
    (
      "TextStatus" IN ('empty_text','low_text','unknown')
      OR ("OcrAttempted" AND NOT "OcrApplied" AND "OcrRecommended")
      OR ("OcrApplied" AND "TextStatus" <> 'ok')
      OR "PageReviewRecommendedCount" > 0
    ) AS "ManualReviewRecommended"
  FROM doc_quality
)
SELECT
  "DocId",
  "DocPath",
  "ExtractionSource",
  "OcrAttempted",
  "OcrApplied",
  "QualityStatus",
  "ExtractionConfidence",
  "ManualReviewRecommended",
  "OcrLanguages",
  "OcrDurationMs",
  "NativeTextStatus",
  "NativeOcrRecommended",
  "OcrDiagnosticsJson",
  "PageCount",
  "TextPageCount",
  "EmptyPageCount",
  "SparsePageCount",
  "ImagePageCount",
  "PageWarningCount",
  "PageReviewRecommendedCount",
  "TextStatus",
  "OcrRecommended",
  "SignalsJson",
  "RetrievalChunkQualityJson"
FROM doc_quality_scored;
""";

        const string pageSql = """
WITH scoped_docs AS (
  SELECT
    d.doc_id,
    d.tenant_id,
    d.doc_path,
    d.indexed_version,
    rev.revision_id
  FROM documents d
  LEFT JOIN document_revisions rev
    ON rev.tenant_id=d.tenant_id
   AND rev.doc_id=d.doc_id
   AND rev.indexed_version=d.indexed_version
  WHERE d.tenant_id=@tenant
    AND (
      d.doc_id = ANY(@docIds)
      OR d.doc_path = ANY(@docPaths)
    )
)
SELECT
  sd.doc_id AS "DocId",
  sd.doc_path AS "DocPath",
  pi.page_number AS "PageNumber",
  COALESCE(pi.char_count, 0)::int AS "CharCount",
  CASE
    WHEN COALESCE(pi.metadata ->> 'wordCount', '') ~ '^[0-9]{1,9}$' THEN (pi.metadata ->> 'wordCount')::int
    ELSE 0
  END AS "WordCount",
  CASE
    WHEN COALESCE(pi.metadata ->> 'imageCount', '') ~ '^[0-9]{1,9}$' THEN (pi.metadata ->> 'imageCount')::int
    ELSE 0
  END AS "ImageCount",
  COALESCE(uc.unit_count, 0) AS "UnitCount",
  COALESCE(uc.unit_texts_json, '[]') AS "UnitTextsJson",
  COALESCE(cc.chunk_count, 0) AS "ChunkCount",
  pi.metadata #>> '{extractionQuality,signals}' AS "SignalsJson"
FROM scoped_docs sd
JOIN document_page_index pi
  ON pi.tenant_id=sd.tenant_id
 AND pi.revision_id=sd.revision_id
LEFT JOIN LATERAL (
  SELECT
    COUNT(*)::int AS unit_count,
    COALESCE(to_jsonb(array_agg(LEFT(u.text_content, 1000) ORDER BY u.ordinal))::text, '[]') AS unit_texts_json
  FROM document_units u
  WHERE u.tenant_id=sd.tenant_id
    AND u.revision_id=sd.revision_id
    AND u.page_start <= pi.page_number
    AND pi.page_number <= u.page_end
) uc ON true
LEFT JOIN LATERAL (
  SELECT COUNT(*)::int AS chunk_count
  FROM retrieval_chunks rc
  WHERE rc.tenant_id=sd.tenant_id
    AND rc.revision_id=sd.revision_id
    AND rc.page_start <= pi.page_number
    AND pi.page_number <= rc.page_end
) cc ON true
ORDER BY sd.doc_path, pi.page_number;
""";

        var parameters = new { tenant = tenantId, docIds, docPaths };
        var docRows = (await conn.QueryAsync<RagExtractionDocumentQualityRow>(
            new CommandDefinition(docSql, parameters, cancellationToken: ct))).ToArray();
        var pageRows = (await conn.QueryAsync<RagExtractionPageQualityRow>(
            new CommandDefinition(pageSql, parameters, cancellationToken: ct))).ToArray();

        var docById = docRows.ToDictionary(static row => row.DocId, static row => row);
        var docByPath = docRows
            .Where(static row => !string.IsNullOrWhiteSpace(row.DocPath))
            .GroupBy(static row => NormalizeRagDocPath(row.DocPath) ?? "", StringComparer.OrdinalIgnoreCase)
            .Where(static group => !string.IsNullOrWhiteSpace(group.Key))
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        var pagesById = pageRows
            .GroupBy(static row => row.DocId)
            .ToDictionary(static group => group.Key, static group => group.OrderBy(static row => row.PageNumber).ToArray());
        var pagesByPath = pageRows
            .Where(static row => !string.IsNullOrWhiteSpace(row.DocPath))
            .GroupBy(static row => NormalizeRagDocPath(row.DocPath) ?? "", StringComparer.OrdinalIgnoreCase)
            .Where(static group => !string.IsNullOrWhiteSpace(group.Key))
            .ToDictionary(static group => group.Key, static group => group.OrderBy(static row => row.PageNumber).ToArray(), StringComparer.OrdinalIgnoreCase);

        var byMatch = new Dictionary<string, RagItemExtractionQualityDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in matches)
        {
            var doc = ResolveExtractionDocumentQuality(match, docById, docByPath);
            if (doc is null)
                continue;

            var pages = ResolveExtractionPageRows(match, pagesById, pagesByPath);
            var pageQuality = BuildPageExtractionQuality(match, pages);
            var documentObservation = ObserveExtractionQualitySignalsJson(doc.SignalsJson);
            var documentSignals = doc.PageReviewRecommendedCount > 0
                ? BoundQualitySignals(documentObservation.Signals.Append("page_review_recommended").ToArray(), documentObservation.Metadata)
                : documentObservation;
            var signals = pageQuality is null ? MergeQualitySignals(documentSignals)
                : MergeQualitySignals(documentSignals, BoundQualitySignals(pageQuality.Signals, pageQuality.SignalsMetadata));
            var diagnosticSummary = BuildRagExtractionDiagnosticSummary(doc);
            var item = new RagItemExtractionQualityDto(
                ExtractionSource: doc.ExtractionSource,
                OcrAttempted: doc.OcrAttempted,
                OcrApplied: doc.OcrApplied,
                DocumentQualityStatus: doc.QualityStatus,
                DocumentExtractionConfidence: doc.ExtractionConfidence,
                DocumentManualReviewRecommended: doc.ManualReviewRecommended,
                PageQualityStatus: pageQuality?.QualityStatus,
                PageExtractionConfidence: pageQuality?.ExtractionConfidence,
                PageManualReviewRecommended: pageQuality?.ManualReviewRecommended,
                TextStatus: pageQuality?.TextStatus ?? doc.TextStatus,
                OcrRecommended: doc.OcrRecommended,
                Signals: signals.Signals.Count == 0 ? null : signals.Signals,
                DiagnosticSummary: diagnosticSummary,
                SignalsMetadata: signals.Metadata);

            byMatch[BuildExtractionQualityMatchKey(match)] = item;
        }

        return byMatch;
    }


    internal static RagItemExtractionQualityDto? ResolveExtractionQuality(
        RagMatch match,
        IReadOnlyDictionary<string, RagItemExtractionQualityDto> extractionQualityByMatch)
        => BuildEffectiveExtractionQuality(
            match,
            extractionQualityByMatch.TryGetValue(BuildExtractionQualityMatchKey(match), out var quality)
                ? quality
                : null);


    internal static IReadOnlyDictionary<string, RagItemExtractionQualityDto> BuildEffectiveExtractionQualityByMatch(
        IReadOnlyList<RagMatch> matches,
        IReadOnlyDictionary<string, RagItemExtractionQualityDto> extractionQualityByMatch)
    {
        var result = new Dictionary<string, RagItemExtractionQualityDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in matches)
        {
            var effective = ResolveExtractionQuality(match, extractionQualityByMatch);
            if (effective is null)
                continue;

            var key = BuildExtractionQualityMatchKey(match);
            if (!result.TryGetValue(key, out var existing)
                || ComputeSelectionQualityPenalty(effective) > ComputeSelectionQualityPenalty(existing))
            {
                result[key] = effective;
            }
        }

        return result;
    }


    internal static List<RagQualityAdjustedMatch> BuildQualityAdjustedMatchesPreservingRank(
        IReadOnlyList<RagMatch> matches,
        IReadOnlyDictionary<string, RagItemExtractionQualityDto> extractionQualityByMatch)
    {
        var adjusted = new List<RagQualityAdjustedMatch>(matches.Count);
        foreach (var match in matches)
        {
            var extractionQuality = ResolveExtractionQuality(match, extractionQualityByMatch);
            adjusted.Add(new RagQualityAdjustedMatch(
                match,
                extractionQuality,
                ApplyExtractionQualityScorePenalty(match.Score, extractionQuality)));
        }

        return adjusted;
    }


    internal static async Task<IReadOnlyDictionary<string, RagDocumentLanguageInfo>> LoadRagDocumentLanguagesAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        IReadOnlyList<RagMatch> matches,
        CancellationToken ct)
    {
        var docIds = matches
            .Select(static match => Guid.TryParse(match.DocId, out var docId) ? docId : (Guid?)null)
            .Where(static docId => docId.HasValue)
            .Select(static docId => docId!.Value)
            .Distinct()
            .ToArray();

        if (docIds.Length == 0)
            return new Dictionary<string, RagDocumentLanguageInfo>(StringComparer.OrdinalIgnoreCase);

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
SELECT
  d.doc_id::text AS "DocId",
  profile.language AS "ProfileLanguage",
  summary.doc_language AS "SummaryLanguage",
  run.payload ->> 'documentLanguage' AS "RunDocumentLanguage"
FROM documents d
LEFT JOIN LATERAL (
  SELECT NULLIF(BTRIM(p.language), '') AS language
  FROM document_profiles p
  JOIN document_revisions r
    ON r.revision_id = p.revision_id
   AND r.tenant_id = p.tenant_id
   AND r.doc_id = p.doc_id
  WHERE p.tenant_id = d.tenant_id
    AND p.doc_id = d.doc_id
    AND r.indexed_version = COALESCE(d.indexed_version, 0)
  ORDER BY
    (NULLIF(BTRIM(p.language), 'und') IS NULL) ASC,
    CASE p.profile_version
      WHEN 'llm_backoffice_v1' THEN 0
      WHEN 'foundation_v1' THEN 1
      ELSE 2
    END,
    r.published_at DESC NULLS LAST,
    p.profile_version ASC
  LIMIT 1
) profile ON true
LEFT JOIN LATERAL (
  SELECT NULLIF(BTRIM(s.doc_language), '') AS doc_language
  FROM document_summaries s
  WHERE s.tenant_id = d.tenant_id
    AND s.doc_id = d.doc_id
    AND s.level = 'medium'
    AND s.source_hash = saaia_document_summary_source_hash(d.content_hash, d.doc_path, d.file_size, d.file_mtime, d.indexed_version)
  ORDER BY s.updated_at DESC NULLS LAST, s.created_at DESC NULLS LAST
  LIMIT 1
) summary ON true
LEFT JOIN LATERAL (
  SELECT pr.payload
  FROM document_processing_runs pr
  WHERE pr.tenant_id=d.tenant_id
    AND pr.doc_id=d.doc_id
    AND pr.action='upsert'
    AND pr.status='done'
    AND (
      pr.revision_id IN (
        SELECT r.revision_id
        FROM document_revisions r
        WHERE r.tenant_id=d.tenant_id
          AND r.doc_id=d.doc_id
          AND r.indexed_version = COALESCE(d.indexed_version, 0)
      )
      OR pr.indexed_version_after = COALESCE(d.indexed_version, 0)
    )
  ORDER BY
    (pr.indexed_version_after = COALESCE(d.indexed_version, 0)) DESC,
    pr.finished_at DESC NULLS LAST,
    pr.started_at DESC NULLS LAST
  LIMIT 1
) run ON true
WHERE d.tenant_id=@tenant
  AND d.doc_id = ANY(@docIds);
""";

        var rows = await conn.QueryAsync<RagDocumentLanguageRow>(new CommandDefinition(
            sql,
            new { tenant = tenantId, docIds },
            cancellationToken: ct));

        return rows.ToDictionary(
            static row => row.DocId,
            static row =>
            {
                var profileLanguage = NormalizeRagLanguageTag(row.ProfileLanguage);
                var summaryLanguage = NormalizeRagLanguageTag(row.SummaryLanguage);
                var runLanguage = NormalizeRagLanguageTag(row.RunDocumentLanguage);
                var docLanguage = !string.Equals(profileLanguage, "und", StringComparison.Ordinal)
                    ? profileLanguage
                    : !string.Equals(summaryLanguage, "und", StringComparison.Ordinal)
                        ? summaryLanguage
                        : runLanguage;
                return new RagDocumentLanguageInfo(
                    DocLanguage: docLanguage,
                    ProfileLanguage: string.Equals(profileLanguage, "und", StringComparison.Ordinal) ? null : profileLanguage);
            },
            StringComparer.OrdinalIgnoreCase);
    }


    private static RagDocumentLanguageInfo ResolveDocumentLanguageInfo(
        RagMatch match,
        IReadOnlyDictionary<string, RagDocumentLanguageInfo> languagesByDocId)
    {
        if (Guid.TryParse(match.DocId, out var docId)
            && languagesByDocId.TryGetValue(docId.ToString(), out var byDashedId))
            return byDashedId;
        if (Guid.TryParse(match.DocId, out docId)
            && languagesByDocId.TryGetValue(docId.ToString("N"), out var byCompactId))
            return byCompactId;
        return new RagDocumentLanguageInfo("und", null);
    }


    internal static async Task<IReadOnlyDictionary<string, RagDocumentSourceIdentity>> LoadRagDocumentSourceIdentitiesAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        IReadOnlyList<RagMatch> matches,
        CancellationToken ct)
    {
        var docIds = matches
            .Select(static match => Guid.TryParse(match.DocId, out var docId) ? docId : (Guid?)null)
            .Where(static docId => docId.HasValue)
            .Select(static docId => docId!.Value)
            .Distinct()
            .ToArray();
        var docPaths = matches
            .Select(static match => NormalizeDocumentSourceHashPathKey(match.DocPath))
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (docIds.Length == 0 && docPaths.Length == 0)
            return new Dictionary<string, RagDocumentSourceIdentity>(StringComparer.OrdinalIgnoreCase);

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
SELECT
  d.doc_id::text AS "DocId",
  d.doc_path     AS "DocPath",
  r.revision_id::text AS "RevisionId",
  encode(r.source_hash, 'hex') AS "SourceHash"
FROM documents d
JOIN document_revisions r
  ON r.tenant_id = d.tenant_id
 AND r.doc_id = d.doc_id
 AND r.indexed_version = d.indexed_version
WHERE d.tenant_id=@tenant
  AND d.status = 'indexed'
  AND d.indexed_version > 0
  AND (d.doc_id = ANY(@docIds) OR d.doc_path = ANY(@docPaths));
""";

        var rows = await conn.QueryAsync<RagDocumentSourceIdentityRow>(new CommandDefinition(
            sql,
            new { tenant = tenantId, docIds, docPaths },
            cancellationToken: ct));

        var result = new Dictionary<string, RagDocumentSourceIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Where(static row =>
                     !string.IsNullOrWhiteSpace(row.RevisionId)
                     && !string.IsNullOrWhiteSpace(row.SourceHash)))
        {
            var identity = new RagDocumentSourceIdentity(
                row.RevisionId!,
                row.SourceHash!);
            void Add(string? key)
            {
                if (!string.IsNullOrWhiteSpace(key) && !result.ContainsKey(key))
                    result[key] = identity;
            }

            Add(row.DocId);
            if (Guid.TryParse(row.DocId, out var docId))
                Add(docId.ToString("N"));
            Add(NormalizeDocumentSourceHashPathKey(row.DocPath));
        }

        return result;
    }


    internal static string NormalizeDocumentSourceHashPathKey(string? docPath)
        => string.IsNullOrWhiteSpace(docPath)
            ? string.Empty
            : docPath.Trim().Replace('\\', '/').TrimStart('/');


    private static string NormalizeRagLanguageTag(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return "und";

        var normalized = language.Trim().Replace('_', '-').ToLowerInvariant();
        if (normalized.Contains(',', StringComparison.Ordinal))
            normalized = normalized.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        if (normalized.Contains('+', StringComparison.Ordinal))
            normalized = normalized.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;

        if (string.Equals(normalized, "und", StringComparison.Ordinal))
            return "und";

        var parts = normalized.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Length > 5)
            return "und";
        if (parts[0].Length is < 2 or > 8 || !parts[0].All(char.IsLetter))
            return "und";

        return parts.Skip(1).All(static part =>
            part.Length is >= 2 and <= 8 && part.All(static ch => char.IsLetterOrDigit(ch)))
            ? normalized
            : "und";
    }


    internal static double ApplyExtractionQualityScorePenalty(double score, RagItemExtractionQualityDto? quality)
    {
        var qualityScore = ComputeRetrievalQualityScore(quality);
        return Math.Round(Math.Clamp((score * qualityScore.ScoreMultiplier) - qualityScore.ScoreOffset, 0.0, 1.02), 6);
    }


    internal static double ApplyChunkExtractionQualityScorePenalty(double score, RagMatch match)
    {
        var qualityScore = ComputeRetrievalQualityScore(match);
        if (qualityScore.ScoreOffset <= 0)
            return Math.Round(Math.Clamp(score, 0.0, 1.02), 6);

        return Math.Round(Math.Clamp(score - qualityScore.ScoreOffset, 0.0, 1.02), 6);
    }


    internal static RagRetrievalQualityScore ComputeRetrievalQualityScore(RagMatch match)
    {
        var chunkPenalty = ComputeChunkExtractionQualityPenalty(match);
        return new RagRetrievalQualityScore(
            SelectionPenalty: Math.Min(10, chunkPenalty),
            ChunkPenalty: chunkPenalty,
            ScoreMultiplier: 1.0,
            ScoreOffset: Math.Min(0.30, chunkPenalty * 0.015),
            ManualReviewRecommended: chunkPenalty >= 10,
            OcrLikelyNeeded: ResolveChunkOcrRecommended(
                NormalizeExtractionQualityToken(match.ExtractionTextStatus),
                match.ExtractionOcrCandidate) == true);
    }


    internal static RagRetrievalQualityScore ComputeRetrievalQualityScore(RagItemExtractionQualityDto? quality)
    {
        if (quality is null)
            return RagRetrievalQualityScore.Clean;

        var selectionPenalty = 0;
        var multiplier = 1.0;

        if (quality.DocumentManualReviewRecommended == true)
        {
            selectionPenalty += 4;
            multiplier *= 0.90;
        }

        if (quality.PageManualReviewRecommended == true)
        {
            selectionPenalty += 6;
            multiplier *= 0.82;
        }

        var ocrLikelyNeeded = quality.OcrRecommended == true && quality.OcrApplied != true;
        if (ocrLikelyNeeded)
            selectionPenalty += 5;

        var confidence = quality.PageExtractionConfidence ?? quality.DocumentExtractionConfidence;
        if (confidence is <= 0.35)
        {
            selectionPenalty += 8;
            multiplier *= 0.78;
        }
        else if (confidence is <= 0.50)
        {
            selectionPenalty += 5;
            multiplier *= 0.85;
        }
        else if (confidence is <= 0.70)
        {
            selectionPenalty += 2;
            multiplier *= 0.93;
        }

        var status = string.Join(' ', quality.PageQualityStatus, quality.DocumentQualityStatus, quality.TextStatus)
            .ToLowerInvariant();
        if (status.Contains("ocr_failed", StringComparison.Ordinal)
            || status.Contains("low_confidence", StringComparison.Ordinal))
        {
            selectionPenalty += 6;
            multiplier *= 0.90;
        }

        var manualReviewStatus =
            status.Contains("manual_review", StringComparison.Ordinal)
            || status.Contains("low_text", StringComparison.Ordinal)
            || status.Contains("empty_text", StringComparison.Ordinal);
        if (manualReviewStatus)
        {
            selectionPenalty += 4;
            multiplier *= 0.90;
        }

        var chunkPenalty = ComputeChunkExtractionQualityPenalty(quality);
        if (chunkPenalty > 0)
            multiplier *= Math.Clamp(1.0 - (chunkPenalty * 0.0125), 0.70, 1.0);
        selectionPenalty += Math.Min(10, chunkPenalty);

        var retrievalChunkQuality = quality.DiagnosticSummary?.RetrievalChunkQuality;
        var retrievalHasNoSearchableChunks =
            retrievalChunkQuality is not null
            && retrievalChunkQuality.SearchableChunkCount == 0
            && (retrievalChunkQuality.TotalChunkCount.GetValueOrDefault() > 0
                || retrievalChunkQuality.RejectedChunkCount.GetValueOrDefault() > 0);
        var retrievalReviewRecommended =
            retrievalChunkQuality?.ManualReviewRecommended == true
            || retrievalHasNoSearchableChunks;
        if (retrievalReviewRecommended)
        {
            selectionPenalty += 3;
            multiplier *= 0.94;
        }

        return new RagRetrievalQualityScore(
            SelectionPenalty: Math.Clamp(selectionPenalty, 0, 24),
            ChunkPenalty: chunkPenalty,
            ScoreMultiplier: multiplier,
            ScoreOffset: 0.0,
            ManualReviewRecommended: quality.DocumentManualReviewRecommended == true
                || quality.PageManualReviewRecommended == true
                || manualReviewStatus
                || retrievalReviewRecommended,
            OcrLikelyNeeded: ocrLikelyNeeded);
    }


    internal static int ComputeChunkExtractionQualityPenalty(RagMatch match)
        => ComputeChunkExtractionQualityPenalty(
            match.ExtractionTextStatus,
            match.ExtractionTextSparse,
            match.ExtractionOcrCandidate,
            match.ExtractionQualitySignals);


    internal static int ComputeChunkExtractionQualityPenalty(RagItemExtractionQualityDto? quality)
    {
        if (quality is null)
            return 0;

        return ComputeChunkExtractionQualityPenalty(
            quality.ChunkTextStatus ?? quality.TextStatus,
            quality.ChunkTextSparse,
            quality.ChunkOcrCandidate,
            quality.ChunkQualitySignals ?? quality.Signals);
    }


    internal static int ComputeChunkExtractionQualityPenalty(
        string? textStatus,
        bool? textSparse,
        bool? ocrCandidate,
        IReadOnlyList<string>? qualitySignals)
    {
        var penalty = 0;
        var normalizedStatus = NormalizeExtractionQualityToken(textStatus);
        penalty += normalizedStatus switch
        {
            "empty_text" => 12,
            "low_text" => 5,
            "unknown" => 2,
            _ => 0
        };

        if (textSparse == true)
            penalty += 4;
        if (ocrCandidate == true)
            penalty += 3;

        foreach (var signal in NormalizeExtractionQualitySignals(qualitySignals))
        {
            penalty += signal switch
            {
                "replacement_chars_remaining" => 10,
                "probable_ocr_noise" => 8,
                "ocr_noise" => 8,
                "noise_text" => 8,
                "no_text_extracted" => 8,
                "empty_text" => 8,
                "low_text_extraction" => 5,
                "sparse_text_on_page" => 4,
                "ocr_candidate_text" => 3,
                "ocr_recommended" => 2,
                _ => 0
            };
        }

        return Math.Clamp(penalty, 0, 24);
    }


    private static RagExtractionDocumentQualityRow? ResolveExtractionDocumentQuality(
        RagMatch match,
        IReadOnlyDictionary<Guid, RagExtractionDocumentQualityRow> docById,
        IReadOnlyDictionary<string, RagExtractionDocumentQualityRow> docByPath)
    {
        if (Guid.TryParse(match.DocId, out var docId) && docById.TryGetValue(docId, out var byId))
            return byId;

        var docPath = NormalizeRagDocPath(match.DocPath);
        return !string.IsNullOrWhiteSpace(docPath) && docByPath.TryGetValue(docPath, out var byPath)
            ? byPath
            : null;
    }


    private static IReadOnlyList<RagExtractionPageQualityRow> ResolveExtractionPageRows(
        RagMatch match,
        IReadOnlyDictionary<Guid, RagExtractionPageQualityRow[]> pagesById,
        IReadOnlyDictionary<string, RagExtractionPageQualityRow[]> pagesByPath)
    {
        if (Guid.TryParse(match.DocId, out var docId) && pagesById.TryGetValue(docId, out var byId))
            return byId;

        var docPath = NormalizeRagDocPath(match.DocPath);
        return !string.IsNullOrWhiteSpace(docPath) && pagesByPath.TryGetValue(docPath, out var byPath)
            ? byPath
            : [];
    }


    private static RagExtractionPageQuality? BuildPageExtractionQuality(
        RagMatch match,
        IReadOnlyList<RagExtractionPageQualityRow> pages)
    {
        if (!match.PageStart.HasValue || pages.Count == 0)
            return null;

        var start = Math.Max(1, match.PageStart.Value);
        var end = Math.Max(start, match.PageEnd ?? start);
        var pageSources = pages
            .Where(page => page.PageNumber >= start && page.PageNumber <= end)
            .Select(page => (Page: page, Observation: ObserveExtractionQualitySignalsJson(page.SignalsJson)))
            .ToArray();
        var reviews = pageSources
            .Select(source => ExtractionQualityDiagnostics.AssessPage(
                source.Page.WordCount,
                source.Page.CharCount,
                source.Page.ImageCount,
                source.Page.UnitCount,
                CountSuspiciousExtractionUnits(source.Page.UnitTextsJson),
                source.Page.ChunkCount,
                source.Observation.Signals))
            .ToArray();
        if (reviews.Length == 0)
            return null;

        var selected = reviews
            .OrderBy(static review => review.ExtractionConfidence)
            .ThenByDescending(static review => review.ManualReviewRecommended)
            .First();
        var signals = MergeQualitySignals(reviews.Select((review, index) =>
            BoundQualitySignals(review.Signals, pageSources[index].Observation.Metadata)).ToArray());

        return new RagExtractionPageQuality(
            selected.Status,
            selected.ExtractionConfidence,
            selected.ManualReviewRecommended,
            selected.TextStatus,
            signals.Signals.ToArray(), signals.Metadata);
    }


    private static int CountSuspiciousExtractionUnits(string? unitTextsJson)
        => ParseJsonStringArray(unitTextsJson).Count(OcrNoiseFilter.LooksLikeProbableNoiseText);


    private static RagItemExtractionQualityDto? BuildEffectiveExtractionQuality(
        RagMatch match,
        RagItemExtractionQualityDto? quality)
    {
        var chunkStatus = NormalizeExtractionQualityToken(match.ExtractionTextStatus);
        var chunkObservation = MatchQualitySignals(match);
        var chunkSignals = chunkObservation.Signals;
        var hasChunkQuality =
            !string.IsNullOrWhiteSpace(chunkStatus)
            || match.ExtractionTextSparse.HasValue
            || match.ExtractionOcrCandidate.HasValue
            || chunkSignals.Count > 0 || chunkObservation.Metadata.Status != "unavailable";

        if (quality is null && !hasChunkQuality)
            return null;

        var mergedSignals = quality is null ? MergeQualitySignals(chunkObservation)
            : MergeQualitySignals(BoundQualitySignals(quality.Signals, quality.SignalsMetadata), chunkObservation);

        return new RagItemExtractionQualityDto(
            ExtractionSource: quality?.ExtractionSource,
            OcrAttempted: quality?.OcrAttempted,
            OcrApplied: quality?.OcrApplied,
            DocumentQualityStatus: quality?.DocumentQualityStatus,
            DocumentExtractionConfidence: quality?.DocumentExtractionConfidence,
            DocumentManualReviewRecommended: quality?.DocumentManualReviewRecommended,
            PageQualityStatus: quality?.PageQualityStatus,
            PageExtractionConfidence: quality?.PageExtractionConfidence,
            PageManualReviewRecommended: quality?.PageManualReviewRecommended,
            TextStatus: quality?.TextStatus ?? chunkStatus,
            OcrRecommended: quality?.OcrRecommended ?? ResolveChunkOcrRecommended(chunkStatus, match.ExtractionOcrCandidate),
            Signals: mergedSignals.Signals.Count == 0 ? null : mergedSignals.Signals,
            ChunkTextStatus: chunkStatus,
            ChunkTextSparse: match.ExtractionTextSparse,
            ChunkOcrCandidate: match.ExtractionOcrCandidate,
            ChunkQualitySignals: chunkSignals.Count == 0 ? null : chunkSignals,
            DiagnosticSummary: quality?.DiagnosticSummary,
            SignalsMetadata: mergedSignals.Metadata,
            ChunkQualitySignalsMetadata: chunkObservation.Metadata);
    }


    private static bool? ResolveChunkOcrRecommended(string? chunkStatus, bool? chunkOcrCandidate)
        => chunkOcrCandidate == true
           || string.Equals(chunkStatus, "empty_text", StringComparison.Ordinal)
           || string.Equals(chunkStatus, "low_text", StringComparison.Ordinal)
            ? true
            : null;


    private static string? NormalizeExtractionQualityToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim().Replace('-', '_').ToLowerInvariant();
        return normalized.Length is > 0 and <= 80
            && normalized.All(static ch => char.IsLetterOrDigit(ch) || ch == '_')
                ? normalized
                : null;
    }


    internal static string[] NormalizeExtractionQualitySignals(IReadOnlyList<string>? signals)
        => signals is null
            ? Array.Empty<string>()
            : signals
                .Select(NormalizeExtractionQualityToken)
                .Where(static signal => !string.IsNullOrWhiteSpace(signal))
                .Select(static signal => signal!)
                .Distinct(StringComparer.Ordinal)
                .Take(8)
                .ToArray();


    private static RagItemExtractionDiagnosticSummaryDto? BuildRagExtractionDiagnosticSummary(RagExtractionDocumentQualityRow row)
    {
        var ocrDiagnostics = ParseRagOcrDiagnostics(row.OcrDiagnosticsJson);
        var retrievalChunkQuality = BuildRagRetrievalChunkQuality(row.RetrievalChunkQualityJson);
        var summary = new RagItemExtractionDiagnosticSummaryDto(
            NativeTextStatus: NullIfWhiteSpace(row.NativeTextStatus),
            NativeOcrRecommended: row.NativeOcrRecommended,
            OcrMode: ocrDiagnostics.Mode,
            OcrLanguages: NullIfWhiteSpace(row.OcrLanguages),
            OcrDurationMs: row.OcrDurationMs,
            OcrFailureReason: ocrDiagnostics.FailureReason,
            OcrAppliedReason: ocrDiagnostics.AppliedReason,
            OcrTimedOut: ocrDiagnostics.TimedOut == true ? true : null,
            OcrAttemptedPageCount: PositiveOrNull(ocrDiagnostics.AttemptedPageCount.GetValueOrDefault()),
            OcrSkippedPageCount: PositiveOrNull(ocrDiagnostics.SkippedPageCount.GetValueOrDefault()),
            OcrPagesWithNovelTextCount: PositiveOrNull(ocrDiagnostics.PagesWithNovelTextCount.GetValueOrDefault()),
            PageCount: PositiveOrNull(row.PageCount),
            TextPageCount: row.PageCount > 0 ? row.TextPageCount : null,
            EmptyPageCount: row.PageCount > 0 ? row.EmptyPageCount : null,
            SparsePageCount: row.PageCount > 0 ? row.SparsePageCount : null,
            ImagePageCount: PositiveOrNull(row.ImagePageCount),
            PageWarningCount: PositiveOrNull(row.PageWarningCount),
            PageReviewRecommendedCount: PositiveOrNull(row.PageReviewRecommendedCount),
            RetrievalChunkQuality: retrievalChunkQuality);

        return HasRagExtractionDiagnosticValue(summary) ? summary : null;
    }


    private static bool HasRagExtractionDiagnosticValue(RagItemExtractionDiagnosticSummaryDto summary)
        => !string.IsNullOrWhiteSpace(summary.NativeTextStatus)
           || summary.NativeOcrRecommended is not null
           || !string.IsNullOrWhiteSpace(summary.OcrMode)
           || !string.IsNullOrWhiteSpace(summary.OcrLanguages)
           || summary.OcrDurationMs is not null
           || !string.IsNullOrWhiteSpace(summary.OcrFailureReason)
           || !string.IsNullOrWhiteSpace(summary.OcrAppliedReason)
           || summary.OcrTimedOut is not null
           || summary.OcrAttemptedPageCount is not null
           || summary.OcrSkippedPageCount is not null
           || summary.OcrPagesWithNovelTextCount is not null
           || summary.PageCount is not null
           || summary.ImagePageCount is not null
           || summary.PageWarningCount is not null
           || summary.PageReviewRecommendedCount is not null
           || summary.RetrievalChunkQuality is not null;


    private static RagItemRetrievalChunkQualityDto? BuildRagRetrievalChunkQuality(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || string.Equals(json, "null", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var reasons = ReadRagRetrievalChunkRejectionReasons(root);
            var summary = new RagItemRetrievalChunkQualityDto(
                TotalChunkCount: NonNegativeOrNull(GetRagJsonInt(root, "totalChunkCount")),
                SearchableChunkCount: NonNegativeOrNull(GetRagJsonInt(root, "searchableChunkCount")),
                RejectedChunkCount: NonNegativeOrNull(GetRagJsonInt(root, "rejectedChunkCount")),
                ManualReviewRecommended: GetRagJsonBool(root, "manualReviewRecommended"),
                RejectionReasons: reasons.Count == 0 ? null : reasons);

            return HasRagRetrievalChunkQualityValue(summary) ? summary : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }


    private static Dictionary<string, int> ReadRagRetrievalChunkRejectionReasons(JsonElement root)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!root.TryGetProperty("rejectionReasons", out var reasons) || reasons.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var property in reasons.EnumerateObject())
        {
            var value = property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var count)
                ? count
                : property.Value.ValueKind == JsonValueKind.String && int.TryParse(property.Value.GetString(), out count)
                    ? count
                    : 0;
            if (value > 0 && property.Name.Length <= 80)
                result[property.Name] = value;
        }

        return result;
    }


    internal static RagDocumentSourceIdentity? ResolveDocumentSourceIdentity(
        RagMatch match,
        IReadOnlyDictionary<string, RagDocumentSourceIdentity> sourceIdentities)
    {
        var chunkKey = BuildCanonicalChunkIdentityKey(match.DocId, match.DocPath, match.ChunkId);
        if (chunkKey is not null && sourceIdentities.TryGetValue(chunkKey, out var byChunk))
            return byChunk;
        if (Guid.TryParse(match.DocId, out var docId)
            && sourceIdentities.TryGetValue(docId.ToString(), out var byDashedId))
            return byDashedId;
        if (Guid.TryParse(match.DocId, out docId)
            && sourceIdentities.TryGetValue(docId.ToString("N"), out var byCompactId))
            return byCompactId;
        var pathKey = NormalizeDocumentSourceHashPathKey(match.DocPath);
        if (!string.IsNullOrWhiteSpace(pathKey)
            && sourceIdentities.TryGetValue(pathKey, out var byPath))
            return byPath;
        return null;
    }


    private static bool HasRagRetrievalChunkQualityValue(RagItemRetrievalChunkQualityDto summary)
        => summary.TotalChunkCount.HasValue
           || summary.SearchableChunkCount.HasValue
           || summary.RejectedChunkCount.HasValue
           || summary.ManualReviewRecommended.HasValue
           || summary.RejectionReasons is { Count: > 0 };


    private static int? NonNegativeOrNull(int? value)
        => value is >= 0 ? value : null;


    private static RagOcrDiagnosticsSummary ParseRagOcrDiagnostics(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || string.Equals(json, "null", StringComparison.OrdinalIgnoreCase))
            return new RagOcrDiagnosticsSummary();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new RagOcrDiagnosticsSummary();

            var failureReason = GetRagJsonString(root, "failureReason");
            var timedOut = GetRagJsonBool(root, "timedOut");
            var imageFailureReason = default(string);
            var imageTimedOut = false;
            if (root.TryGetProperty("imagePageDiagnostics", out var imageDiagnostics)
                && imageDiagnostics.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in imageDiagnostics.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;

                    imageTimedOut |= GetRagJsonBool(item, "timedOut") == true;
                    var status = GetRagJsonString(item, "status");
                    var reason = GetRagJsonString(item, "reason");
                    if (imageFailureReason is null
                        && (ContainsRagOcrFailure(status) || ContainsRagOcrFailure(reason) || GetRagJsonBool(item, "timedOut") == true))
                    {
                        imageFailureReason = NullIfWhiteSpace(reason) ?? NullIfWhiteSpace(status) ?? "ocr_failed";
                    }
                }
            }

            return new RagOcrDiagnosticsSummary(
                Mode: NullIfWhiteSpace(GetRagJsonString(root, "mode")),
                FailureReason: NullIfWhiteSpace(failureReason) ?? imageFailureReason,
                AppliedReason: NullIfWhiteSpace(GetRagJsonString(root, "appliedReason")),
                TimedOut: timedOut == true || imageTimedOut,
                AttemptedPageCount: GetRagJsonInt(root, "attemptedPageCount"),
                SkippedPageCount: GetRagJsonInt(root, "skippedPageCount"),
                PagesWithNovelTextCount: CountRagJsonArray(root, "pagesWithNovelText"));
        }
        catch (JsonException)
        {
            return new RagOcrDiagnosticsSummary();
        }
    }


    private static bool ContainsRagOcrFailure(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && (value.Contains("fail", StringComparison.OrdinalIgnoreCase)
               || value.Contains("timeout", StringComparison.OrdinalIgnoreCase)
               || value.Contains("timed_out", StringComparison.OrdinalIgnoreCase));


    private static string? GetRagJsonString(JsonElement source, string propertyName)
        => source.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;


    private static int? GetRagJsonInt(JsonElement source, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var property))
            return null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
            return value;
        return property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out value)
            ? value
            : null;
    }


    private static bool? GetRagJsonBool(JsonElement source, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var property))
            return null;
        if (property.ValueKind == JsonValueKind.True)
            return true;
        if (property.ValueKind == JsonValueKind.False)
            return false;
        return property.ValueKind == JsonValueKind.String && bool.TryParse(property.GetString(), out var value)
            ? value
            : null;
    }


    private static int? CountRagJsonArray(JsonElement source, string propertyName)
        => source.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Array
            ? property.GetArrayLength()
            : null;


    private static int? PositiveOrNull(int value)
        => value > 0 ? value : null;


    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();


    internal static string BuildExtractionQualityMatchKey(RagMatch match)
    {
        var identity = Guid.TryParse(match.DocId, out var docId)
            ? docId.ToString("N")
            : NormalizeRagDocPath(match.DocPath) ?? string.Empty;
        return $"{identity}|{match.PageStart?.ToString(CultureInfo.InvariantCulture) ?? ""}|{match.PageEnd?.ToString(CultureInfo.InvariantCulture) ?? ""}";
    }


    internal static string? NormalizeRagDocPath(string? docPath)
    {
        if (string.IsNullOrWhiteSpace(docPath))
            return null;

        var normalized = docPath.Trim().Replace('\\', '/').TrimStart('/');
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }


    internal static async Task<IReadOnlyDictionary<string, bool?>> LoadHypQuestionsMatchedByDocPathAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        string query,
        IReadOnlyList<RagMatch> matches,
        CancellationToken ct)
    {
        var docPaths = matches
            .Select(static match => NormalizeRagDocPath(match.DocPath))
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (docPaths.Length == 0)
            return new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);

        await using var conn = await ds.OpenConnectionAsync(ct);
        var docs = (await conn.QueryAsync<CapabilityAHypQuestionDocRow>(new CommandDefinition(
            """
SELECT d.doc_id AS "DocId",
       d.doc_path AS "DocPath",
       d.doc_name AS "DocName",
       d.indexed_version AS "IndexedVersion",
       COALESCE(to_jsonb(profile.hypothetical_questions)::text, '[]') AS "HypotheticalQuestionsJson"
FROM documents d
LEFT JOIN document_revisions r
  ON r.tenant_id = d.tenant_id
 AND r.doc_id = d.doc_id
 AND r.indexed_version = d.indexed_version
LEFT JOIN LATERAL (
    SELECT COALESCE(
        ARRAY_AGG(DISTINCT NULLIF(BTRIM(question.text), ''))
            FILTER (WHERE NULLIF(BTRIM(question.text), '') IS NOT NULL),
        ARRAY[]::text[]) AS hypothetical_questions
    FROM document_profiles p
    CROSS JOIN LATERAL unnest(COALESCE(p.hypothetical_questions, ARRAY[]::text[])) AS question(text)
    WHERE p.tenant_id = r.tenant_id
      AND p.revision_id = r.revision_id
) profile ON TRUE
WHERE d.tenant_id=@tenant
  AND ltrim(replace(d.doc_path, chr(92), '/'), '/') = ANY(@docPaths)
  AND d.indexed_version > 0
ORDER BY d.doc_path;
""",
            new
            {
                tenant = tenantId,
                docPaths
            },
            cancellationToken: ct)))
            .Select(static row => new CapabilityAHypQuestionDoc(
                row.DocId,
                row.DocPath,
                row.DocName,
                row.IndexedVersion,
                ParseJsonStringArray(row.HypotheticalQuestionsJson)))
            .ToArray();

        var docsMissingStoredQuestions = docs
            .Where(static doc => doc.HypotheticalQuestions.Length == 0)
            .ToArray();
        var missingDocVersions = docsMissingStoredQuestions
            .Select(static doc => (doc.DocId, doc.IndexedVersion))
            .ToArray();
        IReadOnlyDictionary<Guid, string[]> sectionTitlesByDocId = new Dictionary<Guid, string[]>();
        IReadOnlyDictionary<Guid, string[]> excerptsByDocId = new Dictionary<Guid, string[]>();

        if (missingDocVersions.Length > 0)
        {
            sectionTitlesByDocId = await RuntimeGovernanceService.LoadCapabilityBSectionTitlesBatchAsync(
                conn,
                tenantId,
                missingDocVersions,
                limit: 3,
                ct);
            excerptsByDocId = await RuntimeGovernanceService.LoadCapabilityBUnitExcerptsBatchAsync(
                conn,
                tenantId,
                missingDocVersions,
                limit: 2,
                ct);
        }

        var result = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in docs)
        {
            IReadOnlyList<string> hypotheticalQuestions = doc.HypotheticalQuestions;
            if (hypotheticalQuestions.Count == 0)
            {
                sectionTitlesByDocId.TryGetValue(doc.DocId, out var sectionTitles);
                excerptsByDocId.TryGetValue(doc.DocId, out var excerpts);
                hypotheticalQuestions = RuntimeGovernanceService.BuildCapabilityAHypotheticalQuestions(
                    doc.DocName,
                    sectionTitles ?? Array.Empty<string>(),
                    excerpts ?? Array.Empty<string>());
            }

            var matched = hypotheticalQuestions.Count == 0
                ? (bool?)null
                : ComputeHypQuestionsMatched(query, hypotheticalQuestions);
            var normalizedDocPath = NormalizeRagDocPath(doc.DocPath);
            if (!string.IsNullOrWhiteSpace(normalizedDocPath))
                result[normalizedDocPath] = matched;
            if (!string.IsNullOrWhiteSpace(doc.DocPath))
                result[doc.DocPath] = matched;
        }

        return result;
    }

}
