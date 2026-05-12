using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Middleware;
using SAAIA.Backend.Models;

namespace SAAIA.Backend.Endpoints;

public static class RagEndpoints
{
    internal const double NavigationRouteMinimumConfidence = 0.699;
    internal const double FuzzyTitleSimilarityMinimum = 0.68;
    internal const double FuzzyTitleLeadSimilarityMinimum = 0.66;

    public static void Map(WebApplication app)
    {
        app.MapGet("/rag/categories", CategoriesAsync);
        app.MapPost("/rag/search", SearchAsync);
        app.MapPost("/rag/query", QueryAsync);
        app.MapGet("/rag/debug/scroll", ScrollAsync).RequireAdminKey();
    }

    private static async Task<IResult> CategoriesAsync(HttpContext ctx, NpgsqlDataSource ds)
    {
        var tenantId = ctx.GetTenantId();
        await using var conn = await ds.OpenConnectionAsync(ctx.RequestAborted);

        const string sql = """
WITH snapshot AS (
  SELECT
    path AS category,
    display_order,
    name
  FROM documents_catalog_categories
  WHERE tenant_id=@tenant_id
    AND path <> ''
    AND position('/' in path) = 0
),
dynamic AS (
  SELECT DISTINCT
    split_part(replace(doc_path, chr(92), '/'), '/', 1) AS category
  FROM documents
  WHERE tenant_id=@tenant_id
    AND status='indexed'
    AND position('/' in replace(doc_path, chr(92), '/')) > 0
)
SELECT category
FROM (
  SELECT category, display_order, name FROM snapshot
  UNION ALL
  SELECT category, 2147483647 AS display_order, category AS name
  FROM dynamic d
  WHERE NOT EXISTS (
    SELECT 1
    FROM snapshot s
    WHERE LOWER(s.category) = LOWER(d.category)
  )
) categories
WHERE category <> ''
ORDER BY display_order, name;
""";

        var cats = (await conn.QueryAsync<string>(
            new CommandDefinition(sql, new { tenant_id = tenantId }, cancellationToken: ctx.RequestAborted)
        )).ToArray();

        return Results.Ok(cats);
    }

    private static async Task<IResult> QueryAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RagOptions> ragOpt,
        IHttpClientFactory httpFactory,
        RagSearchBulkhead searchBulkhead,
        RagSearchRequestDto req)
    {
        using var admission = await searchBulkhead.AcquireAsync(ctx.RequestAborted);
        if (admission is null)
            return BuildRagSearchBusyResult(ctx, ragOpt.Value, searchBulkhead);

        AddRagSearchAdmissionHeaders(ctx, admission, searchBulkhead.GetSnapshot());
        using var interactiveRetrieval = RuntimeCapabilityBRagIdleCoordinator.BeginInteractiveRetrieval(
            ctx.RequestServices.GetService<IOptions<RuntimeGovernanceOptions>>()?.Value);
        var responseDto = await BuildSearchResponseDtoAsync(ctx, ds, ragOpt.Value, httpFactory, req);
        return Results.Ok(new
        {
            query = responseDto.Query,
            queryNormalized = responseDto.QueryNormalized,
            category = responseDto.Category,
            topK = responseDto.TopK,
            matches = responseDto.Items,
            metrics = responseDto.Metrics,
            guidance = responseDto.Guidance
        });
    }

    private static async Task<IResult> SearchAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RagOptions> ragOpt,
        IHttpClientFactory httpFactory,
        RagSearchBulkhead searchBulkhead,
        RagSearchRequestDto req)
    {
        using var admission = await searchBulkhead.AcquireAsync(ctx.RequestAborted);
        if (admission is null)
            return BuildRagSearchBusyResult(ctx, ragOpt.Value, searchBulkhead);

        AddRagSearchAdmissionHeaders(ctx, admission, searchBulkhead.GetSnapshot());
        using var interactiveRetrieval = RuntimeCapabilityBRagIdleCoordinator.BeginInteractiveRetrieval(
            ctx.RequestServices.GetService<IOptions<RuntimeGovernanceOptions>>()?.Value);
        var responseDto = await BuildSearchResponseDtoAsync(ctx, ds, ragOpt.Value, httpFactory, req);
        return Results.Ok(responseDto);
    }

    private static IResult BuildRagSearchBusyResult(HttpContext ctx, RagOptions rag, RagSearchBulkhead searchBulkhead)
    {
        var retryAfterSeconds = Math.Clamp(rag.SearchRetryAfterSeconds, 1, 300);
        var snapshot = searchBulkhead.GetSnapshot();
        ctx.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        ctx.Response.Headers["X-SAAIA-RAG-Active"] = snapshot.Active.ToString(CultureInfo.InvariantCulture);
        ctx.Response.Headers["X-SAAIA-RAG-Queued"] = snapshot.Queued.ToString(CultureInfo.InvariantCulture);
        ctx.Response.Headers["X-SAAIA-RAG-Max-Concurrency"] = snapshot.MaxConcurrency.ToString(CultureInfo.InvariantCulture);

        return Results.Json(new
        {
            error = "rag_search_busy",
            requestId = ctx.GetRequestId(),
            retryAfterSeconds,
            active = snapshot.Active,
            queued = snapshot.Queued,
            maxConcurrency = snapshot.MaxConcurrency,
            queueLimit = snapshot.QueueLimit,
            detail = "The RAG search service is busy. Retry shortly."
        }, statusCode: StatusCodes.Status429TooManyRequests);
    }

    private static void AddRagSearchAdmissionHeaders(
        HttpContext ctx,
        RagSearchBulkhead.RagSearchBulkheadLease admission,
        RagSearchBulkheadSnapshot snapshot)
    {
        ctx.Response.Headers["X-SAAIA-RAG-Queue-Wait-Ms"] = admission.WaitMs.ToString(CultureInfo.InvariantCulture);
        ctx.Response.Headers["X-SAAIA-RAG-Waited-Queued"] = admission.WaitedQueued ? "true" : "false";
        ctx.Response.Headers["X-SAAIA-RAG-Active"] = snapshot.Active.ToString(CultureInfo.InvariantCulture);
        ctx.Response.Headers["X-SAAIA-RAG-Queued"] = snapshot.Queued.ToString(CultureInfo.InvariantCulture);
        ctx.Response.Headers["X-SAAIA-RAG-Max-Concurrency"] = snapshot.MaxConcurrency.ToString(CultureInfo.InvariantCulture);
    }

    private static async Task<RagSearchResponseDto> BuildSearchResponseDtoAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        RagOptions rag,
        IHttpClientFactory httpFactory,
        RagSearchRequestDto req)
    {
        var resp = await SearchCoreAsync(ctx, ds, rag, httpFactory, req);
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;
        var categoryRefsTask = LoadTopCategoryRefsAsync(ds, tenantId, resp.Matches, ct);
        var hypQuestionsTask = LoadHypQuestionsMatchedByDocPathAsync(
            ds,
            tenantId,
            resp.Query,
            resp.Matches,
            ct);
        var extractionQualityTask = LoadRagExtractionQualityAsync(ds, tenantId, resp.Matches, ct);
        var documentLanguagesTask = LoadRagDocumentLanguagesAsync(ds, tenantId, resp.Matches, ct);
        var documentSourceHashesTask = LoadRagDocumentSourceHashesAsync(ds, tenantId, resp.Matches, ct);

        await Task.WhenAll(categoryRefsTask, hypQuestionsTask, extractionQualityTask, documentLanguagesTask, documentSourceHashesTask);
        var categoryRefsByTopLevelPath = await categoryRefsTask;
        var hypQuestionsMatchedByDocPath = await hypQuestionsTask;
        var extractionQualityByMatch = await extractionQualityTask;
        var documentLanguagesByDocId = await documentLanguagesTask;
        var documentSourceHashesByDocId = await documentSourceHashesTask;
        var qualityAdjustedMatches = BuildQualityAdjustedMatchesPreservingRank(resp.Matches, extractionQualityByMatch);

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
                    var sourceHash = ResolveDocumentSourceHash(m, documentSourceHashesByDocId);
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
                        MatchedContentCards: BuildMatchedContentCardDtos(m),
                        SelectionHints: BuildSelectionHints(m, item.ExtractionQuality)
                    );
                })
                .ToList(),
            Guidance: BuildAnswerGuidance(
                resp.Query,
                qualityAdjustedMatches.Select(static item => item.Match).ToList(),
                extractionQualityByMatch)
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
      WHEN COALESCE(pi.metadata ->> 'wordCount', '') ~ '^[0-9]+$' THEN (pi.metadata ->> 'wordCount')::int
      ELSE 0
    END AS word_count,
    CASE
      WHEN COALESCE(pi.metadata ->> 'imageCount', '') ~ '^[0-9]+$' THEN (pi.metadata ->> 'imageCount')::int
      ELSE 0
    END AS image_count
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
          ((word_count <= 0 OR char_count <= 0) AND chunk_count <= 0)
          OR (word_count > 0 AND char_count > 0 AND (word_count < 12 OR char_count < 80) AND chunk_count <= 0)
          OR (unit_count <= 0 AND chunk_count <= 0 AND (word_count >= 30 OR char_count >= 200))
        )
    )::int AS page_warning_count,
    COUNT(*) FILTER (
      WHERE page_number IS NOT NULL
        AND (
          (image_count > 0 AND (word_count <= 0 OR char_count <= 0))
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
      WHEN COALESCE(lr.payload ->> 'ocrDurationMs', '') ~ '^[0-9]+$'
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
      CASE WHEN COALESCE(lr.payload #>> '{extractionQuality,pageCount}', '') ~ '^[0-9]+$' THEN (lr.payload #>> '{extractionQuality,pageCount}')::int ELSE NULL END,
      pq.page_count,
      0) AS "PageCount",
    COALESCE(
      CASE WHEN COALESCE(lr.payload #>> '{extractionQuality,textPageCount}', '') ~ '^[0-9]+$' THEN (lr.payload #>> '{extractionQuality,textPageCount}')::int ELSE NULL END,
      pq.text_page_count,
      0) AS "TextPageCount",
    COALESCE(
      CASE WHEN COALESCE(lr.payload #>> '{extractionQuality,emptyPageCount}', '') ~ '^[0-9]+$' THEN (lr.payload #>> '{extractionQuality,emptyPageCount}')::int ELSE NULL END,
      pq.empty_page_count,
      0) AS "EmptyPageCount",
    COALESCE(
      CASE WHEN COALESCE(lr.payload #>> '{extractionQuality,sparsePageCount}', '') ~ '^[0-9]+$' THEN (lr.payload #>> '{extractionQuality,sparsePageCount}')::int ELSE NULL END,
      pq.sparse_page_count,
      0) AS "SparsePageCount",
    COALESCE(pq.image_page_count, 0) AS "ImagePageCount",
    COALESCE(pq.page_warning_count, 0) AS "PageWarningCount",
    COALESCE(pq.page_review_recommended_count, 0) AS "PageReviewRecommendedCount",
    COALESCE(
      CASE WHEN COALESCE(lr.payload #>> '{extractionQuality,totalWordCount}', '') ~ '^[0-9]+$' THEN (lr.payload #>> '{extractionQuality,totalWordCount}')::int ELSE NULL END,
      pq.total_word_count,
      0) AS "TotalWordCount",
    COALESCE(
      CASE WHEN COALESCE(lr.payload #>> '{extractionQuality,totalCharCount}', '') ~ '^[0-9]+$' THEN (lr.payload #>> '{extractionQuality,totalCharCount}')::int ELSE NULL END,
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
    lr.payload #>> '{extractionQuality,signals}' AS "RunSignalsJson"
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
  "SignalsJson"
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
    WHEN COALESCE(pi.metadata ->> 'wordCount', '') ~ '^[0-9]+$' THEN (pi.metadata ->> 'wordCount')::int
    ELSE 0
  END AS "WordCount",
  CASE
    WHEN COALESCE(pi.metadata ->> 'imageCount', '') ~ '^[0-9]+$' THEN (pi.metadata ->> 'imageCount')::int
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
            var documentSignals = doc.PageReviewRecommendedCount > 0
                ? ParseJsonStringArray(doc.SignalsJson).Append("page_review_recommended").ToArray()
                : ParseJsonStringArray(doc.SignalsJson);
            var signals = MergeExtractionSignals(documentSignals, pageQuality?.Signals);
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
                Signals: signals.Length == 0 ? null : signals,
                DiagnosticSummary: diagnosticSummary);

            byMatch[BuildExtractionQualityMatchKey(match)] = item;
        }

        return byMatch;
    }

    private static RagItemExtractionQualityDto? ResolveExtractionQuality(
        RagMatch match,
        IReadOnlyDictionary<string, RagItemExtractionQualityDto> extractionQualityByMatch)
        => extractionQualityByMatch.TryGetValue(BuildExtractionQualityMatchKey(match), out var quality)
            ? quality
            : null;

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

    internal static async Task<IReadOnlyDictionary<string, string>> LoadRagDocumentSourceHashesAsync(
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
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
SELECT
  d.doc_id::text AS "DocId",
  d.doc_path     AS "DocPath",
  saaia_document_summary_source_hash(d.content_hash, d.doc_path, d.file_size, d.file_mtime, d.indexed_version) AS "SourceHash"
FROM documents d
WHERE d.tenant_id=@tenant
  AND (d.doc_id = ANY(@docIds) OR d.doc_path = ANY(@docPaths));
""";

        var rows = await conn.QueryAsync<RagDocumentSourceHashRow>(new CommandDefinition(
            sql,
            new { tenant = tenantId, docIds, docPaths },
            cancellationToken: ct));

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Where(static row => !string.IsNullOrWhiteSpace(row.SourceHash)))
        {
            void Add(string? key)
            {
                if (!string.IsNullOrWhiteSpace(key) && !result.ContainsKey(key))
                    result[key] = row.SourceHash!;
            }

            Add(row.DocId);
            if (Guid.TryParse(row.DocId, out var docId))
                Add(docId.ToString("N"));
            Add(NormalizeDocumentSourceHashPathKey(row.DocPath));
        }

        return result;
    }

    internal static string? ResolveDocumentSourceHash(
        RagMatch match,
        IReadOnlyDictionary<string, string> sourceHashesByDocId)
    {
        if (Guid.TryParse(match.DocId, out var docId)
            && sourceHashesByDocId.TryGetValue(docId.ToString(), out var byDashedId))
            return byDashedId;
        if (Guid.TryParse(match.DocId, out docId)
            && sourceHashesByDocId.TryGetValue(docId.ToString("N"), out var byCompactId))
            return byCompactId;
        var pathKey = NormalizeDocumentSourceHashPathKey(match.DocPath);
        if (!string.IsNullOrWhiteSpace(pathKey)
            && sourceHashesByDocId.TryGetValue(pathKey, out var byPath))
            return byPath;
        return null;
    }

    private static string NormalizeDocumentSourceHashPathKey(string? docPath)
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
        if (quality is null)
            return score;

        var multiplier = 1.0;
        if (quality.DocumentManualReviewRecommended == true)
            multiplier *= 0.90;
        if (quality.PageManualReviewRecommended == true)
            multiplier *= 0.82;

        var confidence = quality.PageExtractionConfidence ?? quality.DocumentExtractionConfidence;
        if (confidence is <= 0.35)
            multiplier *= 0.78;
        else if (confidence is <= 0.50)
            multiplier *= 0.85;
        else if (confidence is <= 0.70)
            multiplier *= 0.93;

        var status = string.Join(' ', quality.PageQualityStatus, quality.DocumentQualityStatus, quality.TextStatus)
            .ToLowerInvariant();
        if (status.Contains("ocr_failed", StringComparison.Ordinal)
            || status.Contains("low_confidence", StringComparison.Ordinal)
            || status.Contains("manual_review", StringComparison.Ordinal))
        {
            multiplier *= 0.90;
        }

        return Math.Round(Math.Clamp(score * multiplier, 0.0, 1.02), 6);
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
        var reviews = pages
            .Where(page => page.PageNumber >= start && page.PageNumber <= end)
            .Select(page => ExtractionQualityDiagnostics.AssessPage(
                page.WordCount,
                page.CharCount,
                page.ImageCount,
                page.UnitCount,
                CountSuspiciousExtractionUnits(page.UnitTextsJson),
                page.ChunkCount,
                ParseJsonStringArray(page.SignalsJson)))
            .ToArray();
        if (reviews.Length == 0)
            return null;

        var selected = reviews
            .OrderBy(static review => review.ExtractionConfidence)
            .ThenByDescending(static review => review.ManualReviewRecommended)
            .First();
        var signals = reviews
            .SelectMany(static review => review.Signals)
            .Where(static signal => !string.IsNullOrWhiteSpace(signal))
            .Select(static signal => signal.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();

        return new RagExtractionPageQuality(
            selected.Status,
            selected.ExtractionConfidence,
            selected.ManualReviewRecommended,
            selected.TextStatus,
            signals);
    }

    private static int CountSuspiciousExtractionUnits(string? unitTextsJson)
        => ParseJsonStringArray(unitTextsJson).Count(OcrNoiseFilter.LooksLikeProbableNoiseText);

    private static string[] MergeExtractionSignals(params IReadOnlyList<string>?[] signalGroups)
        => signalGroups
            .Where(static group => group is not null)
            .SelectMany(static group => group!)
            .Where(static signal => !string.IsNullOrWhiteSpace(signal))
            .Select(static signal => signal.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();

    private static RagItemExtractionDiagnosticSummaryDto? BuildRagExtractionDiagnosticSummary(RagExtractionDocumentQualityRow row)
    {
        var ocrDiagnostics = ParseRagOcrDiagnostics(row.OcrDiagnosticsJson);
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
            PageReviewRecommendedCount: PositiveOrNull(row.PageReviewRecommendedCount));

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
           || summary.PageReviewRecommendedCount is not null;

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

    private static string? NormalizeRagDocPath(string? docPath)
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

    internal static async Task<RagSearchResponse> SearchCoreAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        RagOptions rag,
        IHttpClientFactory httpFactory,
        RagSearchRequestDto req)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        if (string.IsNullOrWhiteSpace(req.Query))
            throw new BadHttpRequestException("query is required");

        var topK = req.TopK ?? rag.DefaultTopK;
        topK = Math.Clamp(topK, 1, rag.MaxTopK);

        var category = string.IsNullOrWhiteSpace(req.Category)
            ? null
            : req.Category.Trim().ToLowerInvariant();
        var categoryPath = await ResolveRagCategoryPathAsync(ds, tenantId, req.CategoryPath, req.CategoryRef, ct);

        var retrievalQuery = ResolvePrimaryRetrievalQuery(req.Query, category);
        var queryNorm = NormalizeQuery(req.Query);
        var retrievalQueryNorm = NormalizeQuery(retrievalQuery);

        var mode = ResolveEffectiveSearchMode(req.Mode, req.Query);
        var preferDocumentDiversity = ShouldPreferDocumentDiversity(req.Query) && mode != "focused";
        double defMinScore = mode switch
        {
            "focused" => 0.35,
            "broad" => 0.15,
            _ => 0.25
        };
        int defCandidates = ResolveDefaultCandidateCount(mode, preferDocumentDiversity, topK);
        // CDC v3.1 §11.3: max 3 chunks per document default
        int defMaxPerDoc = ResolveDefaultMaxPerDoc(mode, preferDocumentDiversity, topK);

        var minScore = Math.Clamp(req.MinScore ?? defMinScore, 0.0, 1.0);
        var candidates = Math.Clamp(req.Candidates ?? defCandidates, topK, Math.Max(topK, rag.MaxTopK * 20));
        var maxPerDoc = Math.Clamp(req.MaxPerDoc ?? defMaxPerDoc, 1, topK);
        var maxPerPage = Math.Clamp(req.MaxPerPage ?? 1, 1, topK);
        var hasExplicitMaxPerDoc = req.MaxPerDoc.HasValue;
        var hasExplicitMaxPerPage = req.MaxPerPage.HasValue;

        if (req.Diversity != null)
        {
            if (req.Diversity.MaxChunksPerDoc.HasValue)
            {
                maxPerDoc = Math.Clamp(req.Diversity.MaxChunksPerDoc.Value, 1, topK);
                hasExplicitMaxPerDoc = true;
            }
            if (req.Diversity.PreferDistinctPages == true)
            {
                maxPerPage = 1;
                hasExplicitMaxPerPage = true;
            }
        }

        if (!hasExplicitMaxPerDoc && ShouldConstrainPreciseTitleLookup(req.Query))
            maxPerDoc = Math.Min(topK, Math.Max(maxPerDoc, 2));
        if (!hasExplicitMaxPerPage && ShouldConstrainPreciseTitleLookup(req.Query))
            maxPerPage = Math.Min(topK, Math.Max(maxPerPage, 2));

        var swTotal = Stopwatch.StartNew();
        var hasCategoryFilter = !string.IsNullOrWhiteSpace(category) || !string.IsNullOrWhiteSpace(categoryPath);
        var hasDocScope = !string.IsNullOrWhiteSpace(req.DocId) || !string.IsNullOrWhiteSpace(req.DocPath);
        var skipChunkRetrieversForDocumentOverview = ShouldSkipChunkRetrieversForDocumentOverview(req.Query, hasDocScope, mode);
        var preferUnquotedTitleAnchorRoute = !hasDocScope
            && ShouldProbeUnquotedTitleAnchorRoute(
                req.Query,
                skipChunkRetrieversForDocumentOverview,
                useScopedProfileFallback: false);
        var useScopedProfileFallback = !hasDocScope
            && !preferUnquotedTitleAnchorRoute
            && ShouldUseScopedProfileFallback(req.Query, hasCategoryFilter, mode);
        var allowSparseAssistForScopedProfileFallback =
            useScopedProfileFallback && ShouldAllowSparseAssistForScopedProfileFallback(req.Query);
        var skipSparseRetrieverForBroadDiversity = !hasDocScope
            && (ShouldSkipSparseRetrieverForBroadDiversity(req.Query, mode)
                || ShouldSkipSparseRetrieverForQuantityLookup(req.Query, mode));
        var skipSparseProfileCardAssist = !hasDocScope
            && ShouldSkipSparseProfileCardAssist(req.Query, mode);
        var requireDocumentOverviewProfileMatch = skipChunkRetrieversForDocumentOverview
            && ShouldRequireDocumentOverviewProfileMatch(req.Query);
        using var searchActivity = RetrievalTelemetry.StartSearchActivity(mode, hasCategoryFilter, hasDocScope, topK, candidates, req.Query);

        async Task<(T Result, long DurationMs)> MeasurePhaseAsync<T>(
            string phaseName,
            string? retriever,
            Func<Task<T>> action,
            Func<T, int>? getReturnedCount = null)
        {
            using var phaseActivity = RetrievalTelemetry.StartPhaseActivity(phaseName);
            var swPhase = Stopwatch.StartNew();

            try
            {
                var result = await action();
                swPhase.Stop();
                RetrievalTelemetry.CompletePhase(phaseActivity, getReturnedCount?.Invoke(result) ?? 0, swPhase.ElapsedMilliseconds, retriever);
                return (result, swPhase.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                swPhase.Stop();
                RetrievalTelemetry.MarkPhaseError(phaseActivity, ex);
                throw;
            }
        }

        var (exactMatches, exactMs) = await MeasurePhaseAsync(
            phaseName: "retrieval_exact_match",
            retriever: "exact_match",
            action: () => SearchExactMatchesAsync(ds, tenantId, req.Query, category, req.DocId, req.DocPath, topK, ct, categoryPath),
            getReturnedCount: static matches => matches.Count);
        var (quotedTitleMatches, quotedTitleMs) = await MeasurePhaseAsync(
            phaseName: "retrieval_quoted_title",
            retriever: "quoted_title",
            action: () => SearchQuotedTitleMatchesAsync(ds, tenantId, req.Query, category, req.DocId, req.DocPath, topK, ct, categoryPath),
            getReturnedCount: static matches => matches.Count);
        var shortCircuitAfterExact = ShouldShortCircuitAfterExact(exactMatches);
        var shortCircuitAfterQuotedTitle = !shortCircuitAfterExact
            && ShouldShortCircuitAfterQuotedTitle(req.Query, quotedTitleMatches);
        var selected = new List<RagMatch>(capacity: topK);
        var selectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        long teiMs = 0;
        long rerankMs = 0;
        long sparseMs = 0;
        long qdrantMs = 0;
        long sparsePhaseMs = 0;
        long densePhaseMs = 0;
        long linkedPhaseMs = 0;
        long profilePhaseMs = 0;
        long titleAnchorRoutePhaseMs = 0;
        long fusionMs = 0;
        long rerankPhaseMs = 0;
        long selectionMs = 0;
        int qdrantStatus = 0;
        var degradedRetrievers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var degradedRetrieverErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        object degradedRetrieversLock = new();

        void MarkRetrieverDegraded(string retriever, string? reason = null)
        {
            if (string.IsNullOrWhiteSpace(retriever))
                return;

            var normalized = retriever.Trim().ToLowerInvariant();
            lock (degradedRetrieversLock)
            {
                degradedRetrievers.Add(normalized);
                if (!string.IsNullOrWhiteSpace(reason))
                    degradedRetrieverErrors[normalized] = reason.Trim();
            }
        }

        List<RagMatch>? earlyTitleAnchorRouteMatches = null;
        long earlyTitleAnchorRouteMs = 0;
        var shortCircuitAfterTitleAnchorRoute = false;
        if (!shortCircuitAfterExact
            && !shortCircuitAfterQuotedTitle
            && ShouldProbeUnquotedTitleAnchorRoute(req.Query, skipChunkRetrieversForDocumentOverview, useScopedProfileFallback))
        {
            var (probedTitleAnchorRouteMatches, measuredTitleAnchorRouteMs) = await MeasurePhaseAsync(
                phaseName: "retrieval_title_anchor_route",
                retriever: "title_anchor_route",
                action: () => SearchTitleAnchorRouteMatchesAsync(
                    ds,
                    tenantId,
                    req.Query,
                    category,
                    req.DocId,
                    req.DocPath,
                    Math.Min(candidates, Math.Max(topK, 16)),
                    ct,
                    categoryPath,
                    degradedRetrieverRef: MarkRetrieverDegraded),
                getReturnedCount: static matches => matches.Count);
            earlyTitleAnchorRouteMatches = probedTitleAnchorRouteMatches;
            earlyTitleAnchorRouteMs = measuredTitleAnchorRouteMs;
            shortCircuitAfterTitleAnchorRoute = ShouldShortCircuitAfterTitleAnchorRoute(req.Query, probedTitleAnchorRouteMatches);
        }

        if (shortCircuitAfterExact)
        {
            AddRankedMatches(selected, selectedKeys, exactMatches, topK, minScore: 0.0, maxPerDoc, maxPerPage);
        }
        else if (shortCircuitAfterQuotedTitle)
        {
            AddRankedMatches(
                selected,
                selectedKeys,
                exactMatches.Concat(quotedTitleMatches),
                topK,
                minScore: 0.0,
                maxPerDoc,
                maxPerPage);
        }
        else if (shortCircuitAfterTitleAnchorRoute && earlyTitleAnchorRouteMatches is not null)
        {
            titleAnchorRoutePhaseMs = earlyTitleAnchorRouteMs;
            AddRankedMatches(
                selected,
                selectedKeys,
                exactMatches
                    .Concat(quotedTitleMatches)
                    .Concat(RankTitleAnchorRouteShortCircuitMatches(earlyTitleAnchorRouteMatches)),
                topK,
                minScore: 0.0,
                maxPerDoc,
                maxPerPage);
        }
        else
        {
            Task<(List<RagMatch> Result, long DurationMs)> titleAnchorRouteMatchesTask = earlyTitleAnchorRouteMatches is not null
                ? Task.FromResult((earlyTitleAnchorRouteMatches, earlyTitleAnchorRouteMs))
                : skipChunkRetrieversForDocumentOverview || useScopedProfileFallback
                ? Task.FromResult((new List<RagMatch>(), 0L))
                : MeasurePhaseAsync(
                    phaseName: "retrieval_title_anchor_route",
                    retriever: "title_anchor_route",
                    action: () => SearchTitleAnchorRouteMatchesAsync(
                        ds,
                        tenantId,
                        req.Query,
                        category,
                        req.DocId,
                        req.DocPath,
                        Math.Min(candidates, Math.Max(topK, 16)),
                        ct,
                        categoryPath,
                        degradedRetrieverRef: MarkRetrieverDegraded),
                    getReturnedCount: static matches => matches.Count);
            Task<(List<RagMatch> Result, long DurationMs)> sparseMatchesTask = skipChunkRetrieversForDocumentOverview
                || (useScopedProfileFallback && !allowSparseAssistForScopedProfileFallback)
                || (skipSparseRetrieverForBroadDiversity && !allowSparseAssistForScopedProfileFallback)
                ? Task.FromResult((new List<RagMatch>(), 0L))
                : MeasurePhaseAsync(
                    phaseName: "retrieval_sparse",
                    retriever: "sparse_bm25",
                    action: () => SearchSparseMatchesAsync(
                        ds,
                        tenantId,
                        req.Query,
                        category,
                        req.DocId,
                        req.DocPath,
                        candidates,
                        ct,
                        sparseMsRef: value => sparseMs = value,
                        lexicalExpansionQuery: retrievalQuery,
                        categoryPath: categoryPath,
                        includeProfileCardMatches: !skipSparseProfileCardAssist,
                        degradedRetrieverRef: MarkRetrieverDegraded),
                    getReturnedCount: static matches => matches.Count);
            Task<(List<RagMatch> Result, long DurationMs)> denseMatchesTask = skipChunkRetrieversForDocumentOverview || useScopedProfileFallback
                ? Task.FromResult((new List<RagMatch>(), 0L))
                : MeasurePhaseAsync(
                    phaseName: "retrieval_dense",
                    retriever: "dense_qdrant",
                    action: () => SearchDenseMatchesAsync(
                        ds,
                        httpFactory,
                        rag,
                        tenantId,
                        retrievalQueryNorm,
                        category,
                        req.DocId,
                        req.DocPath,
                        candidates,
                        ct,
                        teiMsRef: value => teiMs = value,
                        qdrantMsRef: value => qdrantMs = value,
                        qdrantStatusRef: value => qdrantStatus = value,
                        categoryPath: categoryPath),
                    getReturnedCount: static matches => matches.Count);
            var skipDocumentProfileSearchForPreciseLookup =
                !skipChunkRetrieversForDocumentOverview
                && !useScopedProfileFallback
                && (ShouldSkipDocumentProfileSearchForPreciseLookup(req.Query)
                    || ShouldSkipDocumentProfileSearchForComparativeLookup(req.Query));
            var documentProfileCandidateCount = ResolveDocumentProfileCandidateCount(
                candidates,
                topK,
                skipChunkRetrieversForDocumentOverview || useScopedProfileFallback);
            var canSearchDocumentProfiles = !skipDocumentProfileSearchForPreciseLookup
                && !ShouldSkipDocumentProfileSearchForQuantityLookup(req.Query)
                && (skipChunkRetrieversForDocumentOverview
                    || useScopedProfileFallback
                    || !ShouldSkipDocumentProfileSearchForLowCostBroadQuery(req.Query, mode));
            var deferDocumentProfileSearch = ShouldDeferDocumentProfileSearch(
                canSearchDocumentProfiles,
                skipChunkRetrieversForDocumentOverview,
                useScopedProfileFallback,
                allowSparseAssistForScopedProfileFallback);
            Task<(List<RagMatch> Result, long DurationMs)> profileMatchesTask = !canSearchDocumentProfiles || deferDocumentProfileSearch
                ? Task.FromResult((new List<RagMatch>(), 0L))
                : SearchDocumentProfilesMeasuredAsync();

            Task<(List<RagMatch> Result, long DurationMs)> SearchDocumentProfilesMeasuredAsync()
                => MeasurePhaseAsync(
                    phaseName: "retrieval_document_profile",
                    retriever: "document_profile",
                    action: () => skipChunkRetrieversForDocumentOverview || useScopedProfileFallback
                        ? SearchDocumentOverviewProfileMatchesAsync(
                            ds,
                            tenantId,
                            retrievalQuery,
                            category,
                            req.DocId,
                            req.DocPath,
                            documentProfileCandidateCount,
                            ct,
                            categoryPath,
                            requireDocumentOverviewProfileMatch,
                            degradedRetrieverRef: MarkRetrieverDegraded)
                        : SearchDocumentProfileMatchesAsync(
                            ds,
                            tenantId,
                            retrievalQuery,
                            category,
                            req.DocId,
                            req.DocPath,
                            documentProfileCandidateCount,
                            ct,
                            categoryPath,
                            degradedRetrieverRef: MarkRetrieverDegraded),
                    getReturnedCount: static matches => matches.Count);

            await Task.WhenAll(titleAnchorRouteMatchesTask, sparseMatchesTask, denseMatchesTask, profileMatchesTask);

            var (titleAnchorRouteMatches, measuredTitleAnchorRoutePhaseMs) = await titleAnchorRouteMatchesTask;
            var (sparseMatches, measuredSparsePhaseMs) = await sparseMatchesTask;
            var (denseMatches, measuredDensePhaseMs) = await denseMatchesTask;
            var (profileMatches, measuredProfilePhaseMs) = await profileMatchesTask;
            titleAnchorRoutePhaseMs = measuredTitleAnchorRoutePhaseMs;
            sparsePhaseMs = measuredSparsePhaseMs;
            densePhaseMs = measuredDensePhaseMs;
            profilePhaseMs = measuredProfilePhaseMs;

            var fusionSw = Stopwatch.StartNew();
            var exactAndQuotedMatches = quotedTitleMatches.Count == 0
                ? exactMatches
                : exactMatches.Concat(quotedTitleMatches).ToList();
            var fusedMatches = FuseWithRrf(exactAndQuotedMatches, sparseMatches, denseMatches, profileMatches, titleAnchorRouteMatches);
            fusedMatches = CalibrateFusedMatches(retrievalQuery, fusedMatches, req.Query);
            fusedMatches = SuppressNavigationalNoise(req.Query, fusedMatches);
            var suppressUnanchoredSpecificResults = !useScopedProfileFallback && ShouldSuppressUnanchoredSpecificResults(retrievalQuery, fusedMatches);
            fusionSw.Stop();
            fusionMs += fusionSw.ElapsedMilliseconds;

            if (suppressUnanchoredSpecificResults)
            {
                fusedMatches = [];
            }
            else
            {
                var (rerankAttempt, measuredRerankPhaseMs) = await MeasurePhaseAsync(
                    phaseName: "retrieval_rerank",
                    retriever: "tei_rerank",
                    action: () => TryRerankWithTeiAsync(
                        httpFactory,
                        rag,
                        retrievalQuery,
                        fusedMatches,
                        ct,
                        rerankMsRef: value => rerankMs = value),
                    getReturnedCount: static attempt => attempt.Matches.Count);
                rerankPhaseMs += measuredRerankPhaseMs;
                if (rerankAttempt.Applied)
                {
                    fusionSw.Restart();
                    fusedMatches = rerankAttempt.Matches;
                    fusedMatches = CalibrateFusedMatches(retrievalQuery, fusedMatches, req.Query);
                    fusedMatches = SuppressNavigationalNoise(req.Query, fusedMatches);
                    fusionSw.Stop();
                    fusionMs += fusionSw.ElapsedMilliseconds;
                }
                else
                {
                    fusedMatches = rerankAttempt.Matches;
                }
            }

            var selectionSw = Stopwatch.StartNew();
            AddRankedMatches(
                selected,
                selectedKeys,
                fusedMatches,
                topK,
                useScopedProfileFallback ? 0.0 : minScore,
                maxPerDoc,
                maxPerPage,
                prioritizeDocumentProfiles: ShouldPrioritizeDocumentProfilesForSelection(
                    preferDocumentDiversity,
                    allowSparseAssistForScopedProfileFallback));
            selectionSw.Stop();
            selectionMs += selectionSw.ElapsedMilliseconds;

            if (deferDocumentProfileSearch
                && ShouldRunDeferredDocumentProfileSearch(
                    selected,
                    topK,
                    preferDocumentDiversity,
                    useScopedProfileFallback,
                    allowSparseAssistForScopedProfileFallback))
            {
                var (deferredProfileMatches, deferredProfileMs) = await SearchDocumentProfilesMeasuredAsync();
                profilePhaseMs += deferredProfileMs;

                var selectionSwDeferred = Stopwatch.StartNew();
                AddRankedMatches(
                    selected,
                    selectedKeys,
                    RankDeferredDocumentProfileMatches(deferredProfileMatches),
                    topK,
                    minScore: 0.0,
                    maxPerDoc,
                    maxPerPage,
                    prioritizeDocumentProfiles: ShouldPrioritizeDocumentProfilesForSelection(
                        preferDocumentDiversity,
                        allowSparseAssistForScopedProfileFallback));
                selectionSwDeferred.Stop();
                selectionMs += selectionSwDeferred.ElapsedMilliseconds;
            }

            if (!useScopedProfileFallback && selected.Count < topK)
            {
                var (linkedMatches, linkedDurationMs) = await MeasurePhaseAsync(
                    phaseName: "retrieval_linked_context",
                    retriever: "linked_context",
                    action: () => SearchLinkedMatchesAsync(
                        ds,
                        tenantId,
                        selected,
                        category,
                        req.DocId,
                        req.DocPath,
                        topK - selected.Count,
                        ct,
                        categoryPath),
                    getReturnedCount: static matches => matches.Count);
                linkedPhaseMs += linkedDurationMs;

                AddRankedMatches(selected, selectedKeys, linkedMatches, topK, minScore: 0.0, Math.Max(maxPerDoc, 2), Math.Max(maxPerPage, 2));
            }

            if (!useScopedProfileFallback && selected.Count < topK)
            {
                var (secondWaveLinkedMatches, secondWaveLinkedMs) = await MeasurePhaseAsync(
                    phaseName: "retrieval_linked_context",
                    retriever: "linked_context",
                    action: () => SearchLinkedMatchesAsync(
                        ds,
                        tenantId,
                        selected,
                        category,
                        req.DocId,
                        req.DocPath,
                        topK - selected.Count,
                        ct,
                        categoryPath),
                    getReturnedCount: static matches => matches.Count);
                linkedPhaseMs += secondWaveLinkedMs;

                AddRankedMatches(selected, selectedKeys, secondWaveLinkedMatches, topK, minScore: 0.0, Math.Max(maxPerDoc, 2), Math.Max(maxPerPage, 2));
            }
        }

        // CDC v3.1 §11.3: autocut - remove trailing results after largest relative score drop
        var selectionRankingQuery = string.Equals(retrievalQuery.Trim(), req.Query.Trim(), StringComparison.OrdinalIgnoreCase)
            ? req.Query
            : retrievalQuery;

        PrioritizeExactTitleSelections(selectionRankingQuery, selected);
        PrioritizeQuotedTitleSelections(req.Query, selected);
        if (!useScopedProfileFallback)
        {
            var selectionSw = Stopwatch.StartNew();
            PruneWeakTitleExpansionSelections(selectionRankingQuery, selected);
            PruneWeakAdjacentSiblingSelections(selectionRankingQuery, selected);
            PrunePreciseTitleTailSelections(selectionRankingQuery, selected);
            PruneUnmatchedPreciseTitleSelections(selectionRankingQuery, selected);
            PruneUnpagedProfileSelectionsForPreciseLookup(selectionRankingQuery, selected);
            selectionSw.Stop();
            selectionMs += selectionSw.ElapsedMilliseconds;
        }
        var finalSelectionSw = Stopwatch.StartNew();
        PruneNavigationalSelections(req.Query, selected);
        if (!skipChunkRetrieversForDocumentOverview)
            ApplyAutocut(selected, minScore);
        RebuildSelectedKeys(selected, selectedKeys);
        finalSelectionSw.Stop();
        selectionMs += finalSelectionSw.ElapsedMilliseconds;

        if (!skipChunkRetrieversForDocumentOverview
            && ShouldBackfillFuzzyTitleLead(req.Query, selected))
        {
            var focusedFuzzyQuery = BuildFocusedLexicalBackfillQuery(req.Query);
            if (!string.IsNullOrWhiteSpace(focusedFuzzyQuery))
            {
                var (fuzzyTitleLeadMatches, fuzzyTitleLeadMs) = await MeasurePhaseAsync(
                    phaseName: "retrieval_fuzzy_title_lead",
                    retriever: "fuzzy_title_lead",
                    action: () => SearchFuzzyTitleLeadMatchesAsync(
                        ds,
                        tenantId,
                        focusedFuzzyQuery,
                        category,
                        req.DocId,
                        req.DocPath,
                        Math.Max(topK, 8),
                        ct,
                        categoryPath,
                        degradedRetrieverRef: MarkRetrieverDegraded),
                    getReturnedCount: static matches => matches.Count);
                sparsePhaseMs += fuzzyTitleLeadMs;

                var calibratedFuzzyMatches = CalibrateFusedMatches(focusedFuzzyQuery, fuzzyTitleLeadMatches, req.Query);
                calibratedFuzzyMatches = SuppressNavigationalNoise(req.Query, calibratedFuzzyMatches);
                var selectionSw = Stopwatch.StartNew();
                AddRankedMatches(
                    selected,
                    selectedKeys,
                    calibratedFuzzyMatches,
                    topK,
                    minScore: 0.0,
                    maxPerDoc,
                    Math.Max(maxPerPage, 2));
                PruneNavigationalSelections(req.Query, selected);
                if (!skipChunkRetrieversForDocumentOverview)
                    ApplyAutocut(selected, minScore);
                RebuildSelectedKeys(selected, selectedKeys);
                selectionSw.Stop();
                selectionMs += selectionSw.ElapsedMilliseconds;
            }
        }

        if (!skipChunkRetrieversForDocumentOverview
            && ShouldBackfillEnumerativeSearch(req.Query, selected.Count, topK))
        {
            var focusedLexicalQuery = BuildFocusedLexicalBackfillQuery(req.Query);
            if (!string.IsNullOrWhiteSpace(focusedLexicalQuery)
                && !string.Equals(focusedLexicalQuery.Trim(), req.Query.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                var (backfillMatches, backfillDurationMs) = await MeasurePhaseAsync(
                    phaseName: "retrieval_lexical_backfill",
                    retriever: "lexical_backfill",
                    action: () => SearchSparseMatchesAsync(
                        ds,
                        tenantId,
                        focusedLexicalQuery,
                        category,
                        req.DocId,
                        req.DocPath,
                        Math.Max(candidates, topK * 4),
                        ct,
                        sparseMsRef: value => sparseMs += value,
                        lexicalExpansionQuery: focusedLexicalQuery,
                        categoryPath: categoryPath,
                        includeProfileCardMatches: false,
                        degradedRetrieverRef: MarkRetrieverDegraded),
                    getReturnedCount: static matches => matches.Count);
                sparsePhaseMs += backfillDurationMs;

                var calibratedBackfill = CalibrateFusedMatches(focusedLexicalQuery, backfillMatches, req.Query);
                calibratedBackfill = SuppressNavigationalNoise(req.Query, calibratedBackfill);
                var selectionSw = Stopwatch.StartNew();
                AddRankedMatches(
                    selected,
                    selectedKeys,
                    calibratedBackfill,
                    topK,
                    minScore: 0.0,
                    maxPerDoc,
                    Math.Max(maxPerPage, 2));
                PruneNavigationalSelections(req.Query, selected);
                RebuildSelectedKeys(selected, selectedKeys);
                selectionSw.Stop();
                selectionMs += selectionSw.ElapsedMilliseconds;
            }
        }

        if (!skipChunkRetrieversForDocumentOverview
            && selected.Count == 0
            && ShouldUseScopedProfileFallback(req.Query, hasCategoryFilter, mode))
        {
            var (fallbackProfiles, fallbackProfilesMs) = await MeasurePhaseAsync(
                phaseName: "retrieval_scoped_profile_fallback",
                retriever: "document_profile_fallback",
                action: () => SearchDocumentOverviewProfileMatchesAsync(
                    ds,
                    tenantId,
                    req.Query,
                    category,
                    req.DocId,
                    req.DocPath,
                    Math.Min(topK, 6),
                    ct,
                    categoryPath,
                    requireLexicalMatch: false,
                    degradedRetrieverRef: MarkRetrieverDegraded),
                getReturnedCount: static matches => matches.Count);
            profilePhaseMs += fallbackProfilesMs;

            var selectionSw = Stopwatch.StartNew();
            AddRankedMatches(
                selected,
                selectedKeys,
                fallbackProfiles,
                topK,
                minScore: 0.0,
                maxPerDoc: 1,
                maxPerPage: Math.Max(maxPerPage, 1));
            selectionSw.Stop();
            selectionMs += selectionSw.ElapsedMilliseconds;
        }

        swTotal.Stop();

        var response = new RagSearchResponse(
            RequestId: ctx.GetRequestId(),
            Query: req.Query,
            QueryNormalized: queryNorm,
            Category: category,
            TopK: topK,
            MinScore: minScore,
            Candidates: candidates,
            MaxPerDoc: maxPerDoc,
            MaxPerPage: maxPerPage,
            QdrantStatus: qdrantStatus,
            Timings: new RagSearchTimings(
                TotalMs: swTotal.ElapsedMilliseconds,
                TeiMs: teiMs,
                RerankMs: rerankMs,
                SparseMs: sparseMs,
                QdrantMs: qdrantMs,
                ExactMs: exactMs,
                QuotedTitleMs: quotedTitleMs,
                TitleAnchorRouteMs: titleAnchorRoutePhaseMs,
                SparsePhaseMs: sparsePhaseMs,
                DenseMs: densePhaseMs,
                ProfileMs: profilePhaseMs,
                LinkedMs: linkedPhaseMs,
                FusionMs: fusionMs,
                RerankPhaseMs: rerankPhaseMs,
                SelectionMs: selectionMs),
            Matches: selected,
            DegradedRetrievers: degradedRetrievers.Count == 0
                ? null
                : degradedRetrievers.OrderBy(static retriever => retriever, StringComparer.Ordinal).ToArray(),
            DegradedRetrieverErrors: degradedRetrieverErrors.Count == 0
                ? null
                : degradedRetrieverErrors
                    .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal));

        RetrievalTelemetry.CompleteSearch(searchActivity, response, mode, hasCategoryFilter, hasDocScope);
        RetrievalTelemetry.RecordSearch(response, mode, hasCategoryFilter, hasDocScope, exactMs, sparsePhaseMs + titleAnchorRoutePhaseMs + profilePhaseMs, densePhaseMs, linkedPhaseMs);

        return response;
    }

    private static async Task<string?> ResolveRagCategoryPathAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        string? categoryPath,
        string? categoryRef,
        CancellationToken ct)
    {
        var normalizedPath = DocumentsCategoryScopeResolver.NormalizeCategoryPathOrNull(categoryPath);
        var normalizedRef = DocumentsCategoryScopeResolver.NormalizeCategoryRefOrNull(categoryRef);
        if (string.IsNullOrWhiteSpace(normalizedPath) && string.IsNullOrWhiteSpace(normalizedRef))
            return null;

        await using var conn = await ds.OpenConnectionAsync(ct);
        var resolved = await DocumentsCategoryScopeResolver.ResolveCategoryScopeAsync(
            conn,
            tenantId,
            normalizedPath,
            normalizedRef,
            ct);

        return DocumentsCategoryScopeResolver.NormalizeCategoryPathOrNull(resolved ?? normalizedPath);
    }

    private static string? NormalizeRagCategoryPathForSql(string? categoryPath)
        => DocumentsCategoryScopeResolver.NormalizeCategoryPathOrNull(categoryPath);

    private static bool RagDocPathMatchesCategoryPath(string? docPath, string? categoryPath)
    {
        if (string.IsNullOrWhiteSpace(categoryPath))
            return true;
        if (string.IsNullOrWhiteSpace(docPath))
            return false;

        var normalizedDocPath = docPath.Trim().Replace('\\', '/').Trim('/');
        return string.Equals(normalizedDocPath, categoryPath, StringComparison.OrdinalIgnoreCase)
            || normalizedDocPath.StartsWith(categoryPath + "/", StringComparison.OrdinalIgnoreCase);
    }

    internal static string ResolveEffectiveSearchMode(string? requestedMode, string query)
    {
        var mode = (requestedMode ?? string.Empty).Trim().ToLowerInvariant();
        if (string.Equals(mode, "focused", StringComparison.Ordinal)
            && ShouldPromoteFocusedSearchForBroadIntent(query))
        {
            return ShouldPreferDocumentDiversity(query) ? "broad" : "balanced";
        }

        if (mode is "focused" or "balanced" or "broad")
            return mode;

        return ShouldPreferDocumentDiversity(query)
            ? "broad"
            : "balanced";
    }

    internal static bool ShouldPreferDocumentDiversity(string query)
        => ShouldPreferComparativeDocumentDiversity(query)
           || ContainsDocumentOverviewIntent(query);

    internal static bool ShouldSkipChunkRetrieversForDocumentOverview(string query, bool hasDocScope, string mode)
        => !hasDocScope
           && !string.Equals(mode, "focused", StringComparison.Ordinal)
           && ContainsDocumentOverviewIntent(query)
           && !ShouldRequireDocumentOverviewProfileMatch(query);

    internal static bool ShouldSkipSparseRetrieverForBroadDiversity(string query, string mode)
        => !string.Equals(mode, "focused", StringComparison.Ordinal)
           && ExtractQuotedLookupPhrases(query).Count == 0
           && ShouldTreatAsBroadDiversityQuery(query);

    internal static bool ShouldSkipSparseRetrieverForQuantityLookup(string query, string mode)
        => false;

    internal static bool ShouldSkipSparseProfileCardAssist(string query, string mode)
    {
        if (string.IsNullOrWhiteSpace(query)
            || ExtractQuotedLookupPhrases(query).Count > 0
            || HasReferenceLikeQueryToken(query))
        {
            return false;
        }

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        return ContainsDocumentOverviewIntent(query)
            || ContainsExplicitBroadScopedSynthesisIntent(normalized)
            || ContainsBroadScopedSynthesisIntent(normalized)
            || ContainsSituationalBroadSelectionIntent(normalized)
            || ContainsQuantityComputationIntent(normalized)
            || (string.Equals(mode, "broad", StringComparison.Ordinal) && ShouldPreferDocumentDiversity(query));
    }

    internal static bool ShouldSkipDocumentProfileSearchForLowCostBroadQuery(string query, string mode)
    {
        if (string.IsNullOrWhiteSpace(query)
            || ExtractQuotedLookupPhrases(query).Count > 0
            || HasReferenceLikeQueryToken(query)
            || ContainsDocumentOverviewIntent(query))
        {
            return false;
        }

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        return ContainsExplicitBroadScopedSynthesisIntent(normalized)
            || ContainsBroadScopedSynthesisIntent(normalized)
            || ContainsSituationalBroadSelectionIntent(normalized)
            || ContainsQuantityComputationIntent(normalized)
            || (string.Equals(mode, "broad", StringComparison.Ordinal) && ShouldPreferDocumentDiversity(query));
    }

    internal static bool ShouldTreatAsBroadDiversityQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        return ShouldTreatAsBroadDiversityQuery(query, normalized);
    }

    private static bool ShouldTreatAsBroadDiversityQuery(string query, string normalized)
    {
        if (!ShouldPreferComparativeDocumentDiversity(query)
            || ExtractQuotedLookupPhrases(query).Count > 0
            || HasReferenceLikeQueryToken(query))
        {
            return false;
        }

        var hasComparisonMarker = ContainsAny(normalized,
            " compare ",
            " comparer ",
            " compares ",
            " comparing ",
            " comparative ",
            " comparaison ",
            " comparatif ",
            " compara ",
            " comparar ",
            " comparacion ",
            " comparacao ",
            " confronto ",
            " choisir entre ",
            " choisis entre ",
            " choose between ",
            " vergleiche ",
            " vergleichen ");
        if (ContainsGuidanceOrAdviceSelectionIntent(normalized)
            && !hasComparisonMarker)
        {
            return false;
        }

        var hasExplicitDiversityMarker = ContainsAny(normalized,
                " deux ",
                " trois ",
                " plusieurs ",
                " multiple ",
                " several ",
                " versions ",
                " variantes ",
                " styles ",
                " familles ",
                " entre ",
                " between ",
                " corpus ",
                " documents ",
                " docs ",
                " pdf ",
                " pdfs ");
        return hasExplicitDiversityMarker
            || (hasComparisonMarker && HasSpecificComparativeSubject(query))
            || (ContainsAny(normalized, " le plus ", " la plus ", " les plus ", " most ", " mas ", " mais ", " am meisten ")
                && ContainsAny(normalized, " quel ", " quelle ", " quels ", " quelles ", " which ", " what ", " cual ", " qual ", " quale ", " welches ", " welche "));
    }

    private static bool HasSpecificComparativeSubject(string query)
        => ExtractComparativeLookupPhrases(query)
            .SelectMany(ExtractLexicalQueryTokens)
            .Any(static token => token.Length >= 5 && !BroadDiversityGenericSubjectTokens.Contains(token));

    private static readonly HashSet<string> BroadDiversityGenericSubjectTokens = new(StringComparer.Ordinal)
    {
        "option",
        "options",
        "available",
        "disponible",
        "disponibles",
        "choix",
        "choice",
        "choices"
    };

    internal static bool ShouldUseScopedProfileFallback(string query, bool hasCategoryFilter, string mode)
    {
        if (!hasCategoryFilter
            || string.IsNullOrWhiteSpace(query)
            || string.Equals(mode, "focused", StringComparison.Ordinal))
        {
            return false;
        }

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        if (ContainsSituationalBroadSelectionIntent(normalized)
            && ExtractQuotedLookupPhrases(query).Count == 0
            && !HasReferenceLikeQueryToken(query))
        {
            return true;
        }

        if (ExtractFocusedLookupPhrases(query).Count > 0
            && !ContainsExplicitBroadScopedSynthesisIntent(normalized))
        {
            return false;
        }
        if (HasReferenceLikeQueryToken(query)
            && !ContainsExplicitBroadScopedSynthesisIntent(normalized))
        {
            return false;
        }

        return ContainsExplicitBroadScopedSynthesisIntent(normalized)
            || ContainsBroadScopedSynthesisIntent(normalized)
            || ContainsSituationalBroadSelectionIntent(normalized);
    }

    internal static bool ShouldPromoteFocusedSearchForBroadIntent(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        if (ContainsDocumentOverviewIntent(query))
            return true;

        if (ContainsSituationalBroadSelectionIntent(normalized)
            && ExtractQuotedLookupPhrases(query).Count == 0
            && !HasReferenceLikeQueryToken(query))
        {
            return true;
        }

        if (ExtractQuotedLookupPhrases(query).Count > 0
            && !ContainsExplicitBroadScopedSynthesisIntent(normalized))
        {
            return false;
        }

        if (ExtractFocusedLookupPhrases(query).Count > 0
            && !ContainsExplicitBroadScopedSynthesisIntent(normalized))
        {
            return false;
        }
        if (HasReferenceLikeQueryToken(query)
            && !ContainsExplicitBroadScopedSynthesisIntent(normalized))
        {
            return false;
        }

        if (ShouldPreferComparativeDocumentDiversity(query))
            return ShouldTreatAsBroadDiversityQuery(query, normalized);

        return ContainsExplicitBroadScopedSynthesisIntent(normalized)
            || ContainsBroadScopedSynthesisIntent(normalized)
            || ContainsSituationalBroadSelectionIntent(normalized);
    }

    private static bool ContainsExplicitBroadScopedSynthesisIntent(string normalized)
        => ContainsAny(normalized,
               " plusieurs ",
               " multiple ",
               " several ",
               " options ",
               " versions ",
               " versiones ",
               " versoes ",
               " versioni ",
               " versionen ",
               " menu ",
               " menus ",
               " selection ",
               " selectionne ",
               " selectionner ",
               " select ",
               " selected ",
               " liste ",
               " list ",
               " json ",
               " markdown ",
               " checklist ",
               " questions de verification ",
               " verification questions ",
               " familles ",
               " families ",
               " manquent ",
               " manque ",
               " missing ",
               " inventaire ",
               " inventory ",
               " pour chaque ",
               " chaque ",
               " for each ",
               " each ",
               " tableau ",
               " table ",
               " matrice ",
               " matrix ",
               " format ",
               " formatte ",
               " formatted ",
               " complet utilisant ",
               " complete using ",
               " complete with ",
               " cout ",
               " couts ",
               " cost ",
               " costs ",
               " budget ",
               " chf ",
               " rends ",
               " rendre ",
               " transforme ",
               " transformer ",
               " convertis ",
               " convertir ",
               " planning ",
               " planifier ",
               " organiser ",
               " organise ",
               " faq ",
               " retroplanning ",
               " synthese ",
               " synthetise ",
               " synthetiser ",
               " a partir de ",
               " depuis le corpus ",
               " dans le corpus ",
               " depuis les documents ",
               " dans les documents ",
               " from this corpus ",
               " from the corpus ",
               " from documents ",
               " from the documents ",
               " aus dem corpus ",
               " aus den dokumenten ")
           || Regex.IsMatch(
               normalized,
               @"\b(?:fais moi|fais nous|cree|creer|make me|create|build|draft|hazme|crea|crie|criar|erstelle|mach mir)\s+\d+\b",
               RegexOptions.CultureInvariant);

    private static bool ContainsBroadScopedSynthesisIntent(string normalized)
        => ContainsAny(normalized,
            " propose ",
            " proposer ",
            " proposes ",
            " cherche ",
            " chercher ",
            " search ",
            " searching ",
            " busca ",
            " buscar ",
            " busco ",
            " procura ",
            " procurar ",
            " procuro ",
            " cerca ",
            " cercare ",
            " cerco ",
            " suche ",
            " suchen ",
            " suggere ",
            " suggerer ",
            " recommande ",
            " recommander ",
            " conseille ",
            " conseiller ",
            " aide moi ",
            " aider ",
            " selectionne ",
            " selectionner ",
            " selection ",
            " versions ",
            " versiones ",
            " versoes ",
            " versioni ",
            " versionen ",
            " select ",
            " selected ",
            " menu ",
            " menus ",
            " adapte ",
            " adapter ",
            " adaptes ",
            " adaptation ",
            " rends ",
            " rendre ",
            " transforme ",
            " transformer ",
            " priorise ",
            " prioriser ",
            " privilegie ",
            " privilegier ",
            " favorise ",
            " favoriser ",
            " evite ",
            " eviter ",
            " attention ",
            " risque ",
            " risques ",
            " budget ",
            " cout ",
            " couts ",
            " chf ",
            " manquent ",
            " manque ",
            " missing ",
            " prepare ",
            " preparer ",
            " plan ",
            " planning ",
            " planifier ",
            " organiser ",
            " organise ",
            " pour chaque ",
            " chaque ",
            " fais moi ",
            " fais nous ",
            " fais un ",
            " fais une ",
            " fais des ",
            " cree ",
            " creer ",
            " cree un ",
            " cree une ",
            " cree des ",
            " construis ",
            " construire ",
            " elabore ",
            " elaborer ",
            " compose ",
            " composer ",
            " complet utilisant ",
            " complete using ",
            " complete with ",
            " synthese ",
            " synthetise ",
            " synthetiser ",
            " retroplanning ",
            " faq ",
            " questions de verification ",
            " verification questions ",
            " a partir de ",
            " depuis le corpus ",
            " dans le corpus ",
            " depuis les documents ",
            " dans les documents ",
            " il me faut ",
            " suggest ",
            " suggests ",
            " recommend ",
            " recommends ",
            " help me ",
            " adapt ",
            " adapted ",
            " adaptation ",
            " transform ",
            " transform into ",
            " for each ",
            " each ",
            " prioritize ",
            " prioritise ",
            " prefer ",
            " favor ",
            " favour ",
            " make me ",
            " create ",
            " build ",
            " draft ",
            " synthesize ",
            " synthesis ",
            " from this corpus ",
            " from the corpus ",
            " from documents ",
            " from the documents ",
            " prepare ",
            " plan ",
            " planning ",
            " organize ",
            " organise ",
            " need ",
            " propone ",
            " proponer ",
            " sugiere ",
            " sugerir ",
            " recomienda ",
            " recomendar ",
            " ayudame ",
            " hazme ",
            " crea ",
            " crear ",
            " elabora ",
            " elaborar ",
            " sintetiza ",
            " preparar ",
            " planificar ",
            " organizar ",
            " preciso ",
            " sugere ",
            " sugerir ",
            " recomenda ",
            " recomendar ",
            " ajuda me ",
            " crie ",
            " criar ",
            " elabore ",
            " elaborar ",
            " sintetiza ",
            " sintetizar ",
            " preparar ",
            " planear ",
            " organizar ",
            " consiglia ",
            " consigliare ",
            " suggerisci ",
            " suggerire ",
            " aiutami ",
            " adatta ",
            " adattare ",
            " preferisci ",
            " crea ",
            " creare ",
            " componi ",
            " comporre ",
            " sintetizza ",
            " preparare ",
            " pianifica ",
            " organizzare ",
            " vorschlag ",
            " vorschlagen ",
            " empfiehl ",
            " empfehlen ",
            " hilf mir ",
            " anpassen ",
            " bevorzuge ",
            " bevorzugen ",
            " erstelle ",
            " erstellen ",
            " mach mir ",
            " aus dem corpus ",
            " aus den dokumenten ",
            " vorbereiten ",
            " plane ",
            " planen ",
            " organisieren ");

    private static bool ContainsSituationalBroadSelectionIntent(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Regex.IsMatch(
                normalized,
                @"\b(?:je\s+(?:veux|dois|voudrais)|j\s+aimerais|i\s+(?:want|need|would\s+like|have\s+to)|quiero|necesito|gostaria|preciso|quero|voglio|ich\s+(?:mochte|muss|brauche))\b.{0,120}\b(?:avec|pour|con|com|per|mit|adapte|adapter|adaptes|adapted|adapt|adaptar|adequad\w*|adattare|adatt\w*|anpassen|geeignet|suitable|options?|choisir|choix|choose|select|selection|recommande|recommend|plan|planning|organis|prepare|preparer|preparar|priorise|prioriser|privilegie|privilegier|prioritize|prioritise|prefer|favor|favour|bevorzugen|vorbereiten)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:quels?|quelle|quelles|which|what|que|cuales?|quais|quali|welche)\b.{0,100}\b(?:choisir|choix|choose|select|selection|recommande|recommend|adapte|adapter|adapt|adaptar|adequad\w*|adattare|adatt\w*|anpassen|geeignet|suitable|priorise|prioriser|privilegie|privilegier|prioritize|prioritise|prefer|favor|favour|bevorzugen)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:quels?|quelle|quelles|which|what|que|cuales?|quais|quali|welche)\b.{0,120}\b(?:peuvent|peut|can|could|possono|pueden|podem|konnen)\b.{0,120}\b(?:prepar\w*|prepared|advance|avance|adapt|adapte|adapter|adequad\w*|adatt\w*|geeignet|suitable|priorise|prioriser|privilegie|privilegier|prefer|favor|favour)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:quels?|quelle|quelles|which|what|que|cuales?|quais|quali|welche)\b.{0,120}\b(?:utilisent|utilise|uses?|contiennent|contient|contains?|mentionnent|mentionne|mentions?)\b.{0,120}\b(?:gerer|gere|handle|manage|eviter|avoid|sans|without)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:regroupe|regrouper|group|group\s+by|combine|traduis|traduire|translate|ubersetze|traduce|traduz)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:ajoute|ajouter|liste|lister|extrais|extraire|resume|resumer|add|list|extract|summarize)\b.{0,80}\b(?:points?\s+critiques?|critical\s+points?|a\s+surveiller|to\s+watch|watch\s+outs?|risques?|risks?)\b",
                RegexOptions.CultureInvariant)
            || Regex.IsMatch(
                normalized,
                @"\b(?:points?\s+critiques?|critical\s+points?|a\s+surveiller|to\s+watch|watch\s+outs?)\b.{0,80}\b(?:eviter|avoid|rater|ratage|failure|fail)\b",
                RegexOptions.CultureInvariant);
    }

    internal static bool ShouldAllowSparseAssistForScopedProfileFallback(string query)
    {
        var tokens = BuildDocumentProfileSpecificityTokens(query)
            .Where(static token => !DocumentOverviewTopicStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return tokens.Length >= 2
            && tokens.Any(static token => SpecificAnchorStopwords.Contains(token));
    }

    internal static bool ShouldRequireDocumentOverviewProfileMatch(string query)
    {
        if (!ContainsDocumentOverviewIntent(query))
            return false;

        var topicTokens = ExtractLexicalQueryTokens(query)
            .Where(static token => !DocumentOverviewTopicStopwords.Contains(token))
            .ToArray();
        if (topicTokens.Length == 0)
            return false;

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        return ContainsAny(normalized,
            " qui parle ",
            " qui parlent ",
            " parle de ",
            " parle d ",
            " parlent de ",
            " parlent d ",
            " mentionne ",
            " mentionnent ",
            " contient ",
            " contiennent ",
            " traite de ",
            " traite d ",
            " traitent de ",
            " traitent d ",
            " au sujet de ",
            " en lien avec ",
            " concernant ",
            " sur ",
            " about ",
            " related to ",
            " concerning ",
            " mention ",
            " mentions ",
            " mentioning ",
            " contain ",
            " contains ",
            " containing ",
            " cover ",
            " covers ",
            " covering ",
            " hablan de ",
            " habla de ",
            " mencionan ",
            " menciona ",
            " contienen ",
            " contiene ",
            " sobre ",
            " falam de ",
            " fala de ",
            " mencionam ",
            " menciona ",
            " contem ",
            " sobre ",
            " parlano di ",
            " parla di ",
            " menzionano ",
            " menziona ",
            " contengono ",
            " contiene ",
            " riguard ",
            " su ",
            " sprechen uber ",
            " sprechen daruber ",
            " sprechen darueber ",
            " sprechen ueber ",
            " erwahnt ",
            " erwahnen ",
            " enthalt ",
            " enthalten ",
            " uber ",
            " ueber ");
    }

    internal static bool ShouldPreferComparativeDocumentDiversity(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        if (ContainsAny(normalized,
                " compare ",
                " comparer ",
                " compares ",
                " comparing ",
                " comparative ",
                " comparaison ",
                " comparatif ",
                " compara ",
                " comparar ",
                " comparacion ",
                " comparacao ",
                " confronto ",
                " confronta ",
                " confronto ",
                " vergleich ",
                " vergleichen ",
                " choisir ",
                " choisis ",
                " choix entre ",
                " choose ",
                " choose between ",
                " escoge ",
                " escolher ",
                " scegliere ",
                " wahlen "))
            return true;

        if (ContainsAny(normalized, " le plus ", " la plus ", " les plus ", " most ", " mas ", " mais ", " am meisten "))
            return ContainsAny(normalized,
                " quel ",
                " quelle ",
                " quels ",
                " quelles ",
                " lequel ",
                " laquelle ",
                " which ",
                " what ",
                " cual ",
                " qual ",
                " quale ",
                " welches ",
                " welcher ",
                " welche ");

        return false;
    }

    internal static bool ContainsDocumentOverviewIntent(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        if (ContainsAny(normalized,
                " quels documents ",
                " quelles sources ",
                " quels fichiers ",
                " quels pdf ",
                " quels livres ",
                " quels rapports ",
                " quels manuels ",
                " quels documents sont ",
                " quels documents tu ",
                " quels livres tu ",
                " quels pdf tu ",
                " which documents ",
                " which sources ",
                " which files ",
                " which pdfs ",
                " which books ",
                " which reports ",
                " which manuals ",
                " what documents ",
                " what sources ",
                " what files ",
                " que documentos ",
                " que fuentes ",
                " que archivos ",
                " que pdf ",
                " que libros ",
                " quais documentos ",
                " quais fontes ",
                " quais ficheiros ",
                " quais arquivos ",
                " quais pdf ",
                " quais livros ",
                " welche dokumente ",
                " welche quellen ",
                " welche dateien ",
                " welche pdf ",
                " welche bucher ",
                " welche berichte ",
                " welche handbucher ",
                " quali documenti ",
                " quali fonti ",
                " quali file ",
                " quali pdf ",
                " quali libri ",
                " quali manuali "))
        {
            return true;
        }

        var hasOverviewIntent = ContainsAny(normalized,
            " vue d ensemble ",
            " vue globale ",
            " inventaire ",
            " liste ",
            " disponibles ",
            " disponible ",
            " a quoi ils servent ",
            " a quoi elles servent ",
            " par grands themes ",
            " overview ",
            " inventory ",
            " available ",
            " what they are for ",
            " what each is for ",
            " high level view ",
            " vista general ",
            " panorama ",
            " inventario ",
            " disponibles ",
            " visao geral ",
            " inventario ",
            " disponiveis ",
            " uberblick ",
            " verfugbar ",
            " panoramica ",
            " disponibili ");
        if (!hasOverviewIntent)
            return false;

        if (ContainsAny(normalized,
            " vue d ensemble ",
            " vue globale ",
            " par grands themes ",
            " high level view ",
            " overview ",
            " inventory ",
            " panorama ",
            " uberblick ",
            " panoramica "))
        {
            return true;
        }

        return ContainsAny(normalized,
            " document ",
            " documents ",
            " source ",
            " sources ",
            " fichier ",
            " fichiers ",
            " file ",
            " files ",
            " pdf ",
            " livre ",
            " livres ",
            " book ",
            " books ",
            " rapport ",
            " rapports ",
            " report ",
            " reports ",
            " manuel ",
            " manuels ",
            " manual ",
            " manuals ",
            " corpus ",
            " base ",
            " knowledge base ",
            " documentos ",
            " fuentes ",
            " archivos ",
            " libros ",
            " ficheiros ",
            " arquivos ",
            " livros ",
            " dokumente ",
            " quellen ",
            " dateien ",
            " bucher ",
            " berichte ",
            " handbucher ",
            " documenti ",
            " fonti ",
            " libri ",
            " manuali ");
    }

    internal static int ResolveDefaultCandidateCount(string mode, bool preferComparativeDiversity, int topK)
        => mode switch
        {
            "focused" => topK * 3,
            "broad" => topK * (preferComparativeDiversity ? 15 : 12),
            _ => topK * 10
        };

    internal static int ResolveDocumentProfileCandidateCount(int candidates, int topK, bool profileOnly)
    {
        if (topK <= 0 || candidates <= 0)
            return 0;

        var desired = Math.Max(topK, 12);
        return Math.Min(candidates, desired);
    }

    internal static int ResolveDefaultMaxPerDoc(string mode, bool preferComparativeDiversity, int topK)
        => preferComparativeDiversity && mode != "focused"
            ? 1
            : mode switch
            {
                "focused" => Math.Min(topK, 3),
                "broad" => 2,
                _ => Math.Min(3, Math.Max(2, topK / 2))
            };

    internal static bool ShouldConstrainPreciseTitleLookup(string query)
    {
        var tokens = ExtractTitlePruneTokens(query);
        if (tokens.Length is < 2 or > 6)
            return false;
        if (ExtractComparativeSubjectAnchorTokens(query).Count > 0)
            return false;
        if (ExtractQuotedLookupPhrases(query).Count == 0 && ContainsExactTitleActionMarker(query))
            return false;

        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query))} ";
        if (IsRecommendationSelectionQuery(normalized))
            return false;
        if (ContainsEnumerativeLookupIntent(normalized))
            return false;

        return !ContainsAny(
            normalized,
            " compare ",
            " comparer ",
            " comparison ",
            " comparaison ",
            " difference ",
            " differences ",
            " differents ",
            " differentes ");
    }

    internal static bool ShouldSkipDocumentProfileSearchForPreciseLookup(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;
        var hasFocusedLookup = ExtractFocusedLookupPhrases(query).Count > 0;
        var hasPreciseTitleShape = ShouldConstrainPreciseTitleLookup(query);
        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query))} ";
        var hasParameterLookup = ContainsParameterDetailLookupIntent(normalized);
        if (!hasFocusedLookup && !hasPreciseTitleShape && !hasParameterLookup)
            return false;
        if (ContainsDocumentOverviewIntent(query))
            return false;
        if (ShouldUseScopedProfileFallback(query, hasCategoryFilter: true, mode: "balanced"))
            return false;

        if (ContainsEnumerativeLookupIntent(normalized) && !hasParameterLookup)
            return false;
        if (IsRecommendationSelectionQuery(normalized))
            return false;
        if (ContainsGuidanceOrAdviceSelectionIntent(normalized))
            return false;

        return true;
    }

    private static bool ContainsParameterDetailLookupIntent(string normalized)
        => ContainsAny(normalized,
            " composant ",
            " composants ",
            " component ",
            " components ",
            " material ",
            " materials ",
            " materiel ",
            " materiaux ",
            " parametre ",
            " parametres ",
            " parameter ",
            " parameters ",
            " reglage ",
            " reglages ",
            " setting ",
            " settings ",
            " temperature ",
            " temperatures ",
            " vitesse ",
            " vitesses ",
            " speed ",
            " speeds ",
            " duree ",
            " duration ",
            " temps ",
            " time ");

    internal static bool ShouldSkipDocumentProfileSearchForComparativeLookup(string query)
        => ShouldPreferComparativeDocumentDiversity(query)
           && ExtractComparativeLookupPhrases(query).Count > 0;

    private static bool ContainsGuidanceOrAdviceSelectionIntent(string normalized)
        => ContainsAny(normalized,
            " comment choisir ",
            " comment selectionner ",
            " comment decider ",
            " que choisir ",
            " lequel choisir ",
            " laquelle choisir ",
            " lesquels choisir ",
            " lesquelles choisir ",
            " conseille ",
            " conseiller ",
            " recommande ",
            " recommander ",
            " how to choose ",
            " how should ",
            " which should ",
            " what should ",
            " choose between ",
            " recommend ",
            " recommendation ",
            " seleccionar ",
            " elegir ",
            " escoger ",
            " scegliere ",
            " scegliere tra ",
            " escolher ",
            " escolher entre ",
            " auswahl ",
            " auswahlen ",
            " waehlen ",
            " wahlen ");

    private static bool ContainsAny(string value, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (value.Contains(needle, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static IReadOnlyList<string> ExtractDocumentHintTokens(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        var hints = new List<string>();
        foreach (Match match in DocumentHintPattern.Matches(normalized))
        {
            var rawHint = match.Groups["hint"].Value;
            var tokens = ExtractLexicalQueryTokens(rawHint)
                .Where(IsDocumentHintSignalToken)
                .Take(4);
            hints.AddRange(tokens);
        }

        return hints
            .Distinct(StringComparer.Ordinal)
            .Take(6)
            .ToArray();
    }

    private static readonly Regex DocumentHintPattern = new(
        @"\b(?:dans|depuis|from|in|aus|im|von|nel|nella|del|della|do|da|em)\s+(?:(?:le|la|les|l|un|une|des|du|de\s+la|the|a|an|some|el|la|los|las|o|a|os|as|il|lo|gli|die|der|das)\s+)?(?:document|documents|pdf|fichier|fichiers|file|files|source|sources|livre|livres|book|books|manuel|manuels|manual|manuals|guide|guides|rapport|rapports|report|reports|libro|libros|arquivo|arquivos|documento|documentos|handbuch|handbucher|buch|bucher|manuale|manuali)\s+(?<hint>[\p{L}\p{Nd}][\p{L}\p{Nd}\s\-]{1,60}?)(?=\s+(?:avec|with|com|con|mit|pour|for|para|per|sur|about|source|sources|page|pages|et|and|y|e|und)\b|[\?:;,\.\r\n]|$)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static bool IsDocumentHintSignalToken(string token)
        => token.Length >= 3
           && token.Any(char.IsLetterOrDigit)
           && !LexicalStopwords.Contains(token)
           && !PrimaryAnchorStopwords.Contains(token)
           && !SpecificAnchorStopwords.Contains(token);

    private static bool DocumentMatchesHint(IReadOnlyList<string> documentHintTokens, RagMatch match)
    {
        if (documentHintTokens.Count == 0)
            return false;

        var docSignal = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(
            string.Join(' ', new[] { match.DocName, match.DocPath }.Where(static value => !string.IsNullOrWhiteSpace(value)))));
        if (string.IsNullOrWhiteSpace(docSignal))
            return false;

        var compactDocSignal = docSignal.Replace(" ", string.Empty, StringComparison.Ordinal);
        return documentHintTokens.Any(token =>
        {
            var folded = FoldDiacritics(token);
            return docSignal.Contains(folded, StringComparison.Ordinal)
                || (folded.Length >= 4 && compactDocSignal.Contains(folded.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal));
        });
    }

    internal static async Task<List<RagMatch>> SearchExactMatchesAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        string query,
        string? category,
        string? docId,
        string? docPath,
        int topK,
        CancellationToken ct,
        string? categoryPath = null)
    {
        var normalizedTerms = ExactMatchEntryExtractor.ExtractLookupTerms(query);
        if (normalizedTerms.Count == 0)
            return [];

        var referenceKeys = ExactMatchEntryExtractor.ExtractReferenceKeys(query);

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
WITH lookup_terms AS (
    SELECT DISTINCT term
    FROM unnest(@normalized_terms::text[]) AS term
    WHERE term IS NOT NULL AND term <> ''
)
SELECT
    d.doc_id AS "DocId",
    d.doc_path AS "DocPath",
    d.doc_name AS "DocName",
    d.category AS "Category",
    e.page_start AS "PageStart",
    e.page_end AS "PageEnd",
    CASE
        WHEN NULLIF(e.metadata->>'offsetStart', '') ~ '^[-+]?[0-9]+$'
            THEN CASE
                WHEN (e.metadata->>'offsetStart')::numeric BETWEEN -2147483648 AND 2147483647
                    THEN (e.metadata->>'offsetStart')::int
                ELSE NULL
            END
        ELSE NULL
    END AS "OffsetStart",
    CASE
        WHEN NULLIF(e.metadata->>'offsetEnd', '') ~ '^[-+]?[0-9]+$'
            THEN CASE
                WHEN (e.metadata->>'offsetEnd')::numeric BETWEEN -2147483648 AND 2147483647
                    THEN (e.metadata->>'offsetEnd')::int
                ELSE NULL
            END
        ELSE NULL
    END AS "OffsetEnd",
    e.exact_match_entry_id AS "ExactMatchEntryId",
    e.entry_index AS "ChunkIndex",
    e.text_content AS "Text",
    d.indexed_version AS "IngestionVersion",
    LOWER(ENCODE(d.content_hash, 'hex')) AS "HashDoc",
    COALESCE(e.metadata->>'kind', 'verbatim_excerpt') AS "MatchKind",
    s.title AS "SectionTitle",
    lookup_terms.term AS "MatchedTerm"
FROM documents d
JOIN document_revisions r
  ON r.tenant_id = d.tenant_id
 AND r.doc_id = d.doc_id
 AND r.indexed_version = d.indexed_version
JOIN exact_match_entries e
  ON e.tenant_id = r.tenant_id
 AND e.revision_id = r.revision_id
JOIN lookup_terms
  ON lookup_terms.term = e.normalized_text
LEFT JOIN document_sections s
  ON s.section_id = e.section_id
WHERE d.tenant_id = @tenant_id
  AND d.status = 'indexed'
  AND d.indexed_version > 0
  AND (@category IS NULL OR LOWER(d.category) = @category)
  AND (@category_path IS NULL OR d.doc_path = @category_path OR d.doc_path LIKE (@category_path || '/%'))
  AND (@doc_id IS NULL OR d.doc_id = @doc_id)
  AND (@doc_path IS NULL OR d.doc_path = @doc_path)
ORDER BY
    CASE COALESCE(e.metadata->>'kind', 'verbatim_excerpt')
        WHEN 'standard_ref' THEN 0
        WHEN 'code_ref' THEN 1
        ELSE 2
    END,
    LENGTH(lookup_terms.term) DESC,
    d.updated_at DESC,
    e.page_start ASC,
    e.entry_index ASC
LIMIT @top_k;
""";

        var normalizedDocPath = string.IsNullOrWhiteSpace(docPath)
            ? null
            : docPath.Trim().Replace('\\', '/').TrimStart('/');
        var normalizedCategoryPath = NormalizeRagCategoryPathForSql(categoryPath);
        Guid? normalizedDocId = Guid.TryParse(docId, out var parsedDocId) ? parsedDocId : null;

        var rows = await conn.QueryAsync<ExactMatchRow>(new CommandDefinition(sql, new
        {
            tenant_id = tenantId,
            normalized_terms = normalizedTerms.ToArray(),
            category,
            category_path = normalizedCategoryPath,
            doc_id = normalizedDocId,
            doc_path = normalizedDocPath,
            top_k = topK
        }, cancellationToken: ct));

        var matches = rows.Select(row => new RagMatch(
            Score: ComputeExactMatchScore(row.MatchKind, row.MatchedTerm, row.Text),
            DocId: row.DocId.ToString(),
            DocPath: row.DocPath,
            DocName: row.DocName,
            Category: row.Category,
            PageStart: row.PageStart,
            PageEnd: row.PageEnd,
            OffsetStart: row.OffsetStart,
            OffsetEnd: row.OffsetEnd,
            ChunkId: row.ExactMatchEntryId.ToString(),
            ChunkIndex: row.ChunkIndex,
            Text: row.Text,
            IngestionVersion: row.IngestionVersion,
            HashDoc: row.HashDoc,
            EmbedText: row.Text,
            EmbeddingBasis: "exact_match_v1",
            SectionOrdinal: null,
            UnitOrdinal: null,
            SectionTitle: row.SectionTitle,
            HeadingPath: row.SectionTitle,
            ChunkType: "exact_match_entry",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null)).ToList();

        if (matches.Count < topK)
        {
            var metadataMatches = await SearchDocumentMetadataMatchesAsync(
                conn,
                tenantId,
                normalizedTerms,
                referenceKeys,
                category,
                normalizedDocId,
                normalizedDocPath,
                topK - matches.Count,
                ct,
                normalizedCategoryPath);

            foreach (var metadataMatch in metadataMatches)
            {
                if (matches.Count >= topK)
                    break;

                if (!matches.Any(existing =>
                        string.Equals(existing.DocId, metadataMatch.DocId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(existing.Text, metadataMatch.Text, StringComparison.Ordinal)))
                {
                    matches.Add(metadataMatch);
                }
            }
        }

        return await AttachDocumentProfileContentCardsAsync(
            ds,
            tenantId,
            matches,
            string.Join(' ', normalizedTerms),
            ct);
    }

    internal static async Task<List<RagMatch>> SearchQuotedTitleMatchesAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        string query,
        string? category,
        string? docId,
        string? docPath,
        int topK,
        CancellationToken ct,
        string? categoryPath = null)
    {
        var quotedTerms = ExtractQuotedLookupPhrases(query);
        if (quotedTerms.Count == 0 || topK <= 0)
            return [];

        var candidateLimit = Math.Clamp(topK * 4, topK, 64);
        var normalizedDocPath = string.IsNullOrWhiteSpace(docPath)
            ? null
            : docPath.Trim().Replace('\\', '/').TrimStart('/');
        var normalizedCategoryPath = NormalizeRagCategoryPathForSql(categoryPath);
        Guid? normalizedDocId = Guid.TryParse(docId, out var parsedDocId) ? parsedDocId : null;

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
WITH quoted_terms AS (
    SELECT DISTINCT LOWER(term) AS term
    FROM unnest(@quoted_terms::text[]) AS term
    WHERE term IS NOT NULL AND length(term) >= 4
),
profile_card_matches AS (
    SELECT
        r.revision_id,
        GREATEST(1, COALESCE(pcc.page_start, 1)) AS card_page_start,
        GREATEST(
            GREATEST(1, COALESCE(pcc.page_start, 1)),
            COALESCE(pcc.page_end, GREATEST(1, COALESCE(pcc.page_start, 1)))
        ) AS card_page_end,
        COUNT(*) AS match_count,
        LEFT(string_agg(DISTINCT pcc.title, '; '), 500) AS card_titles,
        jsonb_build_object(
            'contentCards',
            jsonb_agg(DISTINCT jsonb_build_object(
                'title', pcc.title,
                'contentCardId', pcc.content_card_id,
                'pageStart', pcc.page_start,
                'pageEnd', pcc.page_end,
                'kind', pcc.kind,
                'signals', pcc.signals,
                'evidence', pcc.metadata->'evidence')))::text AS matched_content_cards_json,
        SUM(CASE
            WHEN LOWER(pcc.search_text) LIKE '%' || quoted_terms.term || '%'
              OR pcc.normalized_title LIKE '%' || quoted_terms.term || '%'
                THEN 10.0
            ELSE 0.0
        END) AS match_weight
    FROM documents d
    JOIN document_revisions r
      ON r.tenant_id = d.tenant_id
     AND r.doc_id = d.doc_id
     AND r.indexed_version = d.indexed_version
    JOIN LATERAL (
        SELECT DISTINCT ON (
            card.normalized_title,
            GREATEST(1, COALESCE(card.page_start, 1)),
            GREATEST(
                GREATEST(1, COALESCE(card.page_start, 1)),
                COALESCE(card.page_end, GREATEST(1, COALESCE(card.page_start, 1)))
            ))
            card.*
        FROM document_profile_content_cards card
        WHERE card.tenant_id = r.tenant_id
          AND card.revision_id = r.revision_id
        ORDER BY
            card.normalized_title,
            GREATEST(1, COALESCE(card.page_start, 1)),
            GREATEST(
                GREATEST(1, COALESCE(card.page_start, 1)),
                COALESCE(card.page_end, GREATEST(1, COALESCE(card.page_start, 1)))
            ),
            CASE card.profile_version
                WHEN 'llm_backoffice_v1' THEN 0
                WHEN 'deterministic_v1' THEN 1
                ELSE 2
            END,
            card.updated_at DESC,
            card.card_index ASC
    ) pcc ON TRUE
    CROSS JOIN quoted_terms
    WHERE d.tenant_id = @tenant_id
      AND d.status = 'indexed'
      AND d.indexed_version > 0
      AND (@category IS NULL OR LOWER(d.category) = @category)
      AND (@category_path IS NULL OR d.doc_path = @category_path OR d.doc_path LIKE (@category_path || '/%'))
      AND (@doc_id IS NULL OR d.doc_id = @doc_id)
      AND (@doc_path IS NULL OR d.doc_path = @doc_path)
      AND pcc.page_start IS NOT NULL
      AND (
          LOWER(pcc.search_text) LIKE '%' || quoted_terms.term || '%'
          OR pcc.normalized_title LIKE '%' || quoted_terms.term || '%'
      )
    GROUP BY
        r.revision_id,
        GREATEST(1, COALESCE(pcc.page_start, 1)),
        GREATEST(
            GREATEST(1, COALESCE(pcc.page_start, 1)),
            COALESCE(pcc.page_end, GREATEST(1, COALESCE(pcc.page_start, 1)))
        )
)
SELECT
    d.doc_id AS "DocId",
    d.doc_path AS "DocPath",
    d.doc_name AS "DocName",
    d.category AS "Category",
    rc.page_start AS "PageStart",
    rc.page_end AS "PageEnd",
    CASE
        WHEN NULLIF(rc.metadata->>'offsetStart', '') ~ '^[-+]?[0-9]+$'
            THEN CASE
                WHEN (rc.metadata->>'offsetStart')::numeric BETWEEN -2147483648 AND 2147483647
                    THEN (rc.metadata->>'offsetStart')::int
                ELSE NULL
            END
        ELSE NULL
    END AS "OffsetStart",
    CASE
        WHEN NULLIF(rc.metadata->>'offsetEnd', '') ~ '^[-+]?[0-9]+$'
            THEN CASE
                WHEN (rc.metadata->>'offsetEnd')::numeric BETWEEN -2147483648 AND 2147483647
                    THEN (rc.metadata->>'offsetEnd')::int
                ELSE NULL
            END
        ELSE NULL
    END AS "OffsetEnd",
    rc.retrieval_chunk_id AS "ChunkId",
    rc.chunk_index AS "ChunkIndex",
    rc.text_content AS "Text",
    d.indexed_version AS "IngestionVersion",
    LOWER(ENCODE(d.content_hash, 'hex')) AS "HashDoc",
    CASE
        WHEN COALESCE(pcm.card_titles, '') <> ''
            THEN 'Matched quoted title: ' || pcm.card_titles || E'\n' || cte.text_content
        WHEN COALESCE(tm.matched_phrases, '') <> ''
            THEN 'Matched quoted title: ' || tm.matched_phrases || E'\n' || cte.text_content
        ELSE cte.text_content
    END AS "EmbedText",
    rc.section_id AS "SectionOrdinalPlaceholder",
    COALESCE(rc.metadata->>'sectionTitle', s.title) AS "SectionTitle",
    COALESCE(rc.metadata->>'headingPath', s.title) AS "HeadingPath",
    COALESCE(rc.metadata->>'chunkType', 'contextual_text_v1') AS "ChunkType",
    COALESCE(rc.metadata->>'contentRole', 'content') AS "ContentRole",
    rc.metadata->>'navigationReason' AS "NavigationReason",
    rc.metadata->>'originalChunkType' AS "OriginalChunkType",
    CASE
        WHEN NULLIF(rc.metadata->>'navigationScore', '') ~ '^[-+]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?:[eE][-+]?[0-9]+)?$'
            THEN (rc.metadata->>'navigationScore')::double precision
        ELSE NULL
    END AS "NavigationScore",
    CASE
        WHEN NULLIF(rc.metadata->>'contentDensityScore', '') ~ '^[-+]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?:[eE][-+]?[0-9]+)?$'
            THEN (rc.metadata->>'contentDensityScore')::double precision
        ELSE NULL
    END AS "ContentDensityScore",
    rc.metadata->>'prevChunkId' AS "PrevChunkId",
    rc.metadata->>'nextChunkId' AS "NextChunkId",
    rc.metadata->>'sameSectionChunkId' AS "SameSectionChunkId",
    pcm.matched_content_cards_json AS "MatchedContentCardsJson",
    (tm.match_weight + (COALESCE(pcm.match_weight, 0.0) * 2.2))::real AS "SparseRank"
FROM documents d
JOIN document_revisions r
  ON r.tenant_id = d.tenant_id
 AND r.doc_id = d.doc_id
 AND r.indexed_version = d.indexed_version
JOIN contextual_text_entries cte
  ON cte.tenant_id = r.tenant_id
 AND cte.revision_id = r.revision_id
JOIN retrieval_chunks rc
  ON rc.retrieval_chunk_id = cte.retrieval_chunk_id
LEFT JOIN document_sections s
  ON s.section_id = rc.section_id
LEFT JOIN profile_card_matches pcm
  ON pcm.revision_id = r.revision_id
 AND rc.page_start <= pcm.card_page_end
 AND rc.page_end >= pcm.card_page_start
CROSS JOIN LATERAL (
    SELECT LOWER(CONCAT_WS(
        E'\n',
        rc.text_content,
        cte.text_content,
        COALESCE(rc.metadata->>'sectionTitle', s.title),
        COALESCE(rc.metadata->>'headingPath', s.title)
    )) AS text_lc
) normalized_chunk
CROSS JOIN LATERAL (
    SELECT
        COUNT(*) AS match_count,
        LEFT(string_agg(DISTINCT quoted_terms.term, '; '), 500) AS matched_phrases,
        COALESCE(SUM(CASE WHEN normalized_chunk.text_lc LIKE '%' || quoted_terms.term || '%' THEN 10.0 ELSE 0.0 END), 0.0) AS match_weight
    FROM quoted_terms
    WHERE normalized_chunk.text_lc LIKE '%' || quoted_terms.term || '%'
) tm
WHERE d.tenant_id = @tenant_id
  AND d.status = 'indexed'
  AND d.indexed_version > 0
  AND (tm.match_count > 0 OR COALESCE(pcm.match_count, 0) > 0)
  AND (@category IS NULL OR LOWER(d.category) = @category)
  AND (@category_path IS NULL OR d.doc_path = @category_path OR d.doc_path LIKE (@category_path || '/%'))
  AND (@doc_id IS NULL OR d.doc_id = @doc_id)
  AND (@doc_path IS NULL OR d.doc_path = @doc_path)
ORDER BY
    (tm.match_weight + (COALESCE(pcm.match_weight, 0.0) * 2.2)) DESC,
    COALESCE(pcm.match_count, 0) DESC,
    tm.match_count DESC,
    rc.chunk_index ASC
LIMIT @candidate_limit;
""";

        var rows = await conn.QueryAsync<SparseMatchRow>(new CommandDefinition(sql, new
        {
            tenant_id = tenantId,
            quoted_terms = quotedTerms.ToArray(),
            category,
            category_path = normalizedCategoryPath,
            doc_id = normalizedDocId,
            doc_path = normalizedDocPath,
            candidate_limit = candidateLimit
        }, cancellationToken: ct));

        return rows
            .Select(row => new
            {
                Row = row,
                QuotedScore = ComputeQuotedLookupCandidateScore(
                    quotedTerms,
                    string.Join("\n", new[]
                    {
                        row.EmbedText,
                        row.Text,
                        row.SectionTitle,
                        row.HeadingPath
                    }.Where(static value => !string.IsNullOrWhiteSpace(value))))
            })
            .Where(static item => item.QuotedScore > 0.0)
            .OrderByDescending(static item => item.QuotedScore)
            .ThenByDescending(static item => item.Row.SparseRank)
            .ThenBy(static item => item.Row.DocPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Row.ChunkIndex)
            .Take(topK)
            .Select(item =>
            {
                var row = item.Row;
                return new RagMatch(
            Score: Math.Clamp(0.88 + Math.Min(0.14, item.QuotedScore * 0.01), 0.0, 1.02),
            DocId: row.DocId.ToString(),
            DocPath: row.DocPath,
            DocName: row.DocName,
            Category: row.Category,
            PageStart: row.PageStart,
            PageEnd: row.PageEnd,
            OffsetStart: row.OffsetStart,
            OffsetEnd: row.OffsetEnd,
            ChunkId: row.ChunkId.ToString(),
            ChunkIndex: row.ChunkIndex,
            Text: row.Text,
            IngestionVersion: row.IngestionVersion,
            HashDoc: row.HashDoc,
            EmbedText: row.EmbedText,
            EmbeddingBasis: "sparse_bm25_v1",
            SectionOrdinal: null,
            UnitOrdinal: null,
            SectionTitle: row.SectionTitle,
            HeadingPath: row.HeadingPath,
            ChunkType: row.ChunkType,
            ContentRole: row.ContentRole,
            NavigationReason: row.NavigationReason,
            OriginalChunkType: row.OriginalChunkType,
            NavigationScore: row.NavigationScore,
            ContentDensityScore: row.ContentDensityScore,
            PrevChunkId: row.PrevChunkId,
            NextChunkId: row.NextChunkId,
                    SameSectionChunkId: row.SameSectionChunkId,
                    MatchedContentCards: BuildSparseMatchedContentCards(row, query));
            })
            .ToList();
    }

    internal static async Task<List<RagMatch>> SearchTitleAnchorRouteMatchesAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        string query,
        string? category,
        string? docId,
        string? docPath,
        int topK,
        CancellationToken ct,
        string? categoryPath = null,
        Action<string, string?>? degradedRetrieverRef = null)
    {
        if (topK <= 0)
            return [];

        var focusedPhrases = ExtractFocusedLookupPhrases(query)
            .Concat(ExtractComparativeLookupPhrases(query))
            .Select(TitleAnchorNormalizer.NormalizeTitle)
            .Where(static phrase => phrase.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();
        var hasExplicitFocusedLookupPhrase = focusedPhrases.Length > 0;
        if (focusedPhrases.Length == 0)
        {
            var focusedBackfillPhrase = ShouldProbeUnquotedTitleAnchorRoute(
                    query,
                    skipChunkRetrieversForDocumentOverview: false,
                    useScopedProfileFallback: false)
                ? TitleAnchorNormalizer.NormalizeTitle(BuildFocusedLexicalBackfillQuery(query))
                : string.Empty;
            if (focusedBackfillPhrase.Length >= 4)
            {
                focusedPhrases = [focusedBackfillPhrase];
                hasExplicitFocusedLookupPhrase = true;
            }
            else
            {
                var normalizedQueryPhrase = TitleAnchorNormalizer.NormalizeTitle(query);
                if (normalizedQueryPhrase.Length >= 4)
                    focusedPhrases = [normalizedQueryPhrase];
            }
        }

        var tokenSource = focusedPhrases.Length > 0 ? string.Join(' ', focusedPhrases) : query;
        var queryTokens = TitleAnchorNormalizer.BuildTitleTokens(tokenSource, maxTokens: 12)
            .Where(static token => !LexicalStopwords.Contains(token))
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Where(static token => !SpecificAnchorStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .Take(10)
            .ToArray();

        if (focusedPhrases.Length == 0 && queryTokens.Length < 2)
            return [];

        var normalizedDocPath = string.IsNullOrWhiteSpace(docPath)
            ? null
            : docPath.Trim().Replace('\\', '/').TrimStart('/');
        var normalizedCategoryPath = NormalizeRagCategoryPathForSql(categoryPath);
        Guid? normalizedDocId = Guid.TryParse(docId, out var parsedDocId) ? parsedDocId : null;
        var candidateLimit = Math.Clamp(topK * 4, topK, 80);
        var minOverlap = focusedPhrases.Length > 0
            ? (queryTokens.Length <= 2 ? queryTokens.Length : 2)
            : Math.Min(queryTokens.Length, 3);
        var directChunkRouteEnabled = hasExplicitFocusedLookupPhrase
            && (ShouldSkipDocumentProfileSearchForPreciseLookup(query)
                || ShouldSkipDocumentProfileSearchForComparativeLookup(query)
                || ShouldProbeUnquotedTitleAnchorRoute(
                    query,
                    skipChunkRetrieversForDocumentOverview: false,
                    useScopedProfileFallback: false));
        var directMinOverlap = directChunkRouteEnabled
            ? ResolveDirectTitleTokenRouteMinimumOverlap(queryTokens.Length)
            : Math.Max(1, minOverlap);
        var directRouteTitle = focusedPhrases.FirstOrDefault() ?? tokenSource;

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
WITH query_phrases AS (
    SELECT DISTINCT phrase
    FROM unnest(@query_phrases::text[]) AS phrase
    WHERE phrase IS NOT NULL AND length(phrase) >= 4
),
scoped_docs AS (
    SELECT
        d.doc_id,
        d.doc_path,
        d.doc_name,
        d.category,
        d.indexed_version,
        d.content_hash,
        r.revision_id,
        r.tenant_id
    FROM documents d
    JOIN document_revisions r
      ON r.tenant_id = d.tenant_id
     AND r.doc_id = d.doc_id
     AND r.indexed_version = d.indexed_version
    WHERE d.tenant_id = @tenant_id
      AND d.status = 'indexed'
      AND d.indexed_version > 0
      AND (@category IS NULL OR LOWER(d.category) = @category)
      AND (@category_path IS NULL OR d.doc_path = @category_path OR d.doc_path LIKE (@category_path || '/%'))
      AND (@doc_id IS NULL OR d.doc_id = @doc_id)
      AND (@doc_path IS NULL OR d.doc_path = @doc_path)
),
anchor_routes AS (
    SELECT
        d.doc_id,
        d.doc_path,
        d.doc_name,
        d.category,
        d.indexed_version,
        d.content_hash,
        d.revision_id,
        a.title AS route_title,
        'title_anchor_route'::text AS route_source,
        target.retrieval_chunk_id AS route_chunk_id,
        LEAST(
            1.02,
            0.54
            + COALESCE(phrase_match.phrase_score, 0.0)
            + CASE
                WHEN @query_token_count > 0 THEN LEAST(0.28, (token_match.overlap_count::double precision / @query_token_count::double precision) * 0.28)
                ELSE 0.0
              END
            + LEAST(0.12, COALESCE(a.confidence, 0.0) * 0.12)
        )::real AS route_rank
    FROM scoped_docs d
    JOIN document_title_anchors a
      ON a.tenant_id = d.tenant_id
     AND a.revision_id = d.revision_id
    CROSS JOIN LATERAL (
        SELECT replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(
            a.normalized_title,
            'œ', 'oe'), 'Œ', 'oe'), 'æ', 'ae'), 'Æ', 'ae'), 'ß', 'ss'), 'ø', 'o'), 'Ø', 'o'), 'ł', 'l'), 'Ł', 'l'), 'đ', 'd'), 'Đ', 'd') AS normalized_title
    ) title_search
    CROSS JOIN LATERAL (
        SELECT COUNT(*)::int AS overlap_count
        FROM unnest(@query_tokens::text[]) AS token
        WHERE token = ANY(a.title_tokens)
    ) token_match
    CROSS JOIN LATERAL (
        SELECT MAX(CASE
            WHEN title_search.normalized_title = qp.phrase THEN 0.34
            WHEN title_search.normalized_title LIKE '%' || qp.phrase || '%' THEN 0.24
            WHEN qp.phrase LIKE '%' || title_search.normalized_title || '%' THEN 0.20
            WHEN similarity(title_search.normalized_title, qp.phrase) >= @fuzzy_title_similarity_min THEN 0.26
            ELSE 0.0
        END) AS phrase_score
        FROM query_phrases qp
        WHERE title_search.normalized_title = qp.phrase
           OR title_search.normalized_title LIKE '%' || qp.phrase || '%'
           OR qp.phrase LIKE '%' || title_search.normalized_title || '%'
           OR similarity(title_search.normalized_title, qp.phrase) >= @fuzzy_title_similarity_min
    ) phrase_match
    JOIN LATERAL (
        SELECT rc.retrieval_chunk_id
        FROM retrieval_chunks rc
        CROSS JOIN LATERAL (
            SELECT
                replace(replace(replace(replace(replace(replace(
                    lower(translate(
                        COALESCE(rc.text_content, '') || ' ' || COALESCE(rc.metadata->>'sectionTitle', '') || ' ' || COALESCE(rc.metadata->>'headingPath', ''),
                        'ÀÁÂÃÄÅàáâãäåÇçÈÉÊËèéêëÌÍÎÏìíîïÑñÒÓÔÕÖØòóôõöøÙÚÛÜùúûüÝýÿ',
                        'AAAAAAaaaaaaCcEEEEeeeeIIIIiiiiNnOOOOOOooooooUUUUuuuuYyy'
                    )),
                    'œ', 'oe'), 'æ', 'ae'), 'ß', 'ss'), 'ø', 'o'), 'ł', 'l'), 'đ', 'd') AS searchable_text,
                ' ' || regexp_replace(replace(replace(replace(replace(replace(replace(
                    lower(translate(
                        COALESCE(rc.text_content, '') || ' ' || COALESCE(rc.metadata->>'sectionTitle', '') || ' ' || COALESCE(rc.metadata->>'headingPath', ''),
                        'ÀÁÂÃÄÅàáâãäåÇçÈÉÊËèéêëÌÍÎÏìíîïÑñÒÓÔÕÖØòóôõöøÙÚÛÜùúûüÝýÿ',
                        'AAAAAAaaaaaaCcEEEEeeeeIIIIiiiiNnOOOOOOooooooUUUUuuuuYyy'
                    )),
                    'œ', 'oe'), 'æ', 'ae'), 'ß', 'ss'), 'ø', 'o'), 'ł', 'l'), 'đ', 'd'), '[^[:alnum:]]+', ' ', 'g') || ' ' AS searchable_words,
                CASE
                    WHEN NULLIF(rc.metadata->>'contentDensityScore', '') ~ '^[-+]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?:[eE][-+]?[0-9]+)?$'
                        THEN (rc.metadata->>'contentDensityScore')::double precision
                    ELSE 0.0
                END AS content_density_score
        ) candidate_stats
        CROSS JOIN LATERAL (
            SELECT
                CASE
                    WHEN length(title_search.normalized_title) >= 5
                     AND candidate_stats.searchable_text LIKE ('%' || title_search.normalized_title || '%')
                        THEN 1
                    ELSE 0
                END AS full_title_hit,
                CASE
                    WHEN length(title_search.normalized_title) >= 5
                        THEN strpos(candidate_stats.searchable_text, title_search.normalized_title)
                    ELSE 0
                END AS full_title_position,
                (
                    SELECT COUNT(*)::int
                    FROM unnest(a.title_tokens) AS title_token
                    WHERE length(title_token) >= 3
                      AND (
                            (length(title_token) >= 5 AND candidate_stats.searchable_text LIKE ('%' || title_token || '%'))
                         OR (length(title_token) < 5 AND candidate_stats.searchable_words LIKE ('% ' || title_token || ' %'))
                      )
                ) AS title_token_hits
        ) candidate_title_match
        WHERE rc.tenant_id = d.tenant_id
          AND rc.revision_id = d.revision_id
          AND (
                (a.retrieval_chunk_id IS NOT NULL AND rc.retrieval_chunk_id = a.retrieval_chunk_id)
             OR (a.retrieval_chunk_id IS NULL
                 AND a.page_start IS NOT NULL
                 AND rc.page_start <= COALESCE(a.page_end, a.page_start) + 1
                 AND rc.page_end >= GREATEST(1, a.page_start - 1))
          )
          AND COALESCE(rc.metadata->>'contentRole', 'content') <> 'navigation'
        ORDER BY
            CASE
                WHEN a.retrieval_chunk_id IS NOT NULL AND rc.retrieval_chunk_id = a.retrieval_chunk_id THEN 0
                WHEN a.source_kind = 'content_card'
                 AND a.page_start IS NOT NULL
                 AND rc.page_start <= COALESCE(a.page_end, a.page_start)
                 AND rc.page_end >= a.page_start
                 AND candidate_title_match.full_title_hit = 1
                 AND candidate_title_match.full_title_position BETWEEN 1 AND 32
                    THEN 1
                WHEN a.source_kind = 'content_card'
                 AND a.page_start IS NOT NULL
                 AND rc.page_start <= COALESCE(a.page_end, a.page_start)
                 AND rc.page_end >= a.page_start
                 AND candidate_title_match.title_token_hits >= 1
                    THEN 2
                WHEN a.source_kind = 'content_card'
                 AND candidate_title_match.full_title_hit = 1
                    THEN 3
                WHEN a.source_kind = 'content_card'
                 AND a.page_start IS NOT NULL
                 AND rc.page_start BETWEEN a.page_start AND COALESCE(a.page_end, a.page_start) + 1
                 AND candidate_title_match.title_token_hits >= 2
                    THEN 4
                WHEN a.source_kind = 'content_card'
                 AND a.page_start IS NOT NULL
                 AND rc.page_start BETWEEN a.page_start + 1 AND COALESCE(a.page_end, a.page_start) + 1
                 AND COALESCE(rc.metadata->>'chunkType', '') IN ('section_window_v1', 'unit_exact_v1')
                    THEN 5
                ELSE 6
            END,
            CASE
                WHEN COALESCE(rc.metadata->>'contentRole', 'content') = 'content' THEN 0
                WHEN COALESCE(rc.metadata->>'contentRole', 'content') = 'mixed_navigation_content' THEN 1
                ELSE 2
            END,
            CASE
                WHEN a.retrieval_chunk_id IS NULL
                 AND a.page_start IS NOT NULL
                 AND rc.page_start <= COALESCE(a.page_end, a.page_start)
                 AND rc.page_end >= a.page_start THEN 0
                ELSE 1
            END,
            CASE
                WHEN candidate_title_match.full_title_position > 0 THEN candidate_title_match.full_title_position
                ELSE 2147483647
            END,
            CASE
                WHEN a.retrieval_chunk_id IS NULL AND a.page_start IS NOT NULL
                    THEN LEAST(ABS(rc.page_start - a.page_start), ABS(rc.page_end - a.page_start))
                ELSE 0
            END,
            candidate_title_match.title_token_hits DESC,
            candidate_stats.content_density_score DESC,
            rc.chunk_index ASC
        LIMIT 1
    ) target ON TRUE
    WHERE COALESCE(phrase_match.phrase_score, 0.0) > 0.0
       OR token_match.overlap_count >= @min_overlap
),
navigation_routes AS (
    SELECT
        d.doc_id,
        d.doc_path,
        d.doc_name,
        d.category,
        d.indexed_version,
        d.content_hash,
        d.revision_id,
        ne.label AS route_title,
        'navigation_route'::text AS route_source,
        target.retrieval_chunk_id AS route_chunk_id,
        LEAST(
            1.02,
            0.50
            + COALESCE(phrase_match.phrase_score, 0.0)
            + CASE
                WHEN @query_token_count > 0 THEN LEAST(0.30, (token_match.overlap_count::double precision / @query_token_count::double precision) * 0.30)
                ELSE 0.0
              END
            + LEAST(0.12, COALESCE(ne.confidence, 0.0) * 0.12)
        )::real AS route_rank
    FROM scoped_docs d
    JOIN document_navigation_entries ne
      ON ne.tenant_id = d.tenant_id
     AND ne.revision_id = d.revision_id
     AND ne.confidence >= @navigation_route_min_confidence
     AND ne.resolution_method <> 'page_unresolved'
     AND (ne.target_chunk_id IS NOT NULL OR ne.target_anchor_id IS NOT NULL)
    CROSS JOIN LATERAL (
        SELECT replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(
            ne.normalized_label,
            'œ', 'oe'), 'Œ', 'oe'), 'æ', 'ae'), 'Æ', 'ae'), 'ß', 'ss'), 'ø', 'o'), 'Ø', 'o'), 'ł', 'l'), 'Ł', 'l'), 'đ', 'd'), 'Đ', 'd') AS normalized_label
    ) label_search
    CROSS JOIN LATERAL (
        SELECT COUNT(*)::int AS overlap_count
        FROM unnest(@query_tokens::text[]) AS token
        WHERE token = ANY(ne.label_tokens)
    ) token_match
    CROSS JOIN LATERAL (
        SELECT MAX(CASE
            WHEN label_search.normalized_label = qp.phrase THEN 0.34
            WHEN label_search.normalized_label LIKE '%' || qp.phrase || '%' THEN 0.24
            WHEN qp.phrase LIKE '%' || label_search.normalized_label || '%' THEN 0.20
            WHEN similarity(label_search.normalized_label, qp.phrase) >= @fuzzy_title_similarity_min THEN 0.26
            ELSE 0.0
        END) AS phrase_score
        FROM query_phrases qp
        WHERE label_search.normalized_label = qp.phrase
           OR label_search.normalized_label LIKE '%' || qp.phrase || '%'
           OR qp.phrase LIKE '%' || label_search.normalized_label || '%'
           OR similarity(label_search.normalized_label, qp.phrase) >= @fuzzy_title_similarity_min
    ) phrase_match
    JOIN LATERAL (
        SELECT rc.retrieval_chunk_id
        FROM retrieval_chunks rc
        WHERE rc.tenant_id = d.tenant_id
          AND rc.revision_id = d.revision_id
          AND (
                (ne.target_chunk_id IS NOT NULL AND rc.retrieval_chunk_id = ne.target_chunk_id)
             OR (ne.target_chunk_id IS NULL
                 AND ne.target_page_start IS NOT NULL
                 AND rc.page_start <= COALESCE(ne.target_page_end, ne.target_page_start) + 1
                 AND rc.page_end >= GREATEST(1, ne.target_page_start - 1))
          )
          AND COALESCE(rc.metadata->>'contentRole', 'content') <> 'navigation'
        ORDER BY
            CASE WHEN ne.target_chunk_id IS NOT NULL AND rc.retrieval_chunk_id = ne.target_chunk_id THEN 0 ELSE 1 END,
            CASE
                WHEN ne.target_chunk_id IS NULL
                 AND ne.target_page_start IS NOT NULL
                 AND rc.page_start <= COALESCE(ne.target_page_end, ne.target_page_start)
                 AND rc.page_end >= ne.target_page_start THEN 0
                ELSE 1
            END,
            CASE
                WHEN ne.target_chunk_id IS NULL AND ne.target_page_start IS NOT NULL
                    THEN LEAST(ABS(rc.page_start - ne.target_page_start), ABS(rc.page_end - ne.target_page_start))
                ELSE 0
            END,
            CASE
                WHEN NULLIF(rc.metadata->>'contentDensityScore', '') ~ '^[-+]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?:[eE][-+]?[0-9]+)?$'
                    THEN (rc.metadata->>'contentDensityScore')::double precision
                ELSE 0.0
            END DESC,
            rc.chunk_index ASC
        LIMIT 1
    ) target ON TRUE
    WHERE COALESCE(phrase_match.phrase_score, 0.0) > 0.0
       OR token_match.overlap_count >= @min_overlap
),
direct_chunk_routes AS (
    SELECT
        d.doc_id,
        d.doc_path,
        d.doc_name,
        d.category,
        d.indexed_version,
        d.content_hash,
        d.revision_id,
        @direct_route_title AS route_title,
        'direct_title_token_route'::text AS route_source,
        rc.retrieval_chunk_id AS route_chunk_id,
        LEAST(
            1.00,
            0.50
            + CASE
                WHEN @query_token_count > 0 THEN LEAST(0.34, (direct_match.overlap_count::double precision / @query_token_count::double precision) * 0.34)
                ELSE 0.0
              END
            + CASE
                WHEN direct_match.overlap_count >= @query_token_count THEN 0.08
                WHEN direct_match.overlap_count >= GREATEST(1, @query_token_count - 1) THEN 0.04
                ELSE 0.0
              END
            + CASE
                WHEN COALESCE(rc.metadata->>'chunkType', '') = 'unit_exact_v1' THEN 0.05
                WHEN COALESCE(rc.metadata->>'chunkType', '') = 'section_window_v1' THEN 0.04
                ELSE 0.0
              END
            + LEAST(0.05, direct_stats.content_density_score * 0.05)
        )::real AS route_rank
    FROM scoped_docs d
    JOIN retrieval_chunks rc
      ON rc.tenant_id = d.tenant_id
     AND rc.revision_id = d.revision_id
    CROSS JOIN LATERAL (
        SELECT
            lower(translate(
                COALESCE(rc.text_content, '') || ' ' || COALESCE(rc.metadata->>'sectionTitle', '') || ' ' || COALESCE(rc.metadata->>'headingPath', ''),
                'ÀÁÂÃÄÅàáâãäåÇçÈÉÊËèéêëÌÍÎÏìíîïÑñÒÓÔÕÖØòóôõöøÙÚÛÜùúûüÝýÿ',
                'AAAAAAaaaaaaCcEEEEeeeeIIIIiiiiNnOOOOOOooooooUUUUuuuuYyy'
            )) AS searchable_text,
            ' ' || regexp_replace(lower(translate(
                COALESCE(rc.text_content, '') || ' ' || COALESCE(rc.metadata->>'sectionTitle', '') || ' ' || COALESCE(rc.metadata->>'headingPath', ''),
                'ÀÁÂÃÄÅàáâãäåÇçÈÉÊËèéêëÌÍÎÏìíîïÑñÒÓÔÕÖØòóôõöøÙÚÛÜùúûüÝýÿ',
                'AAAAAAaaaaaaCcEEEEeeeeIIIIiiiiNnOOOOOOooooooUUUUuuuuYyy'
            )), '[^[:alnum:]]+', ' ', 'g') || ' ' AS searchable_words,
            CASE
                WHEN NULLIF(rc.metadata->>'contentDensityScore', '') ~ '^[-+]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?:[eE][-+]?[0-9]+)?$'
                    THEN (rc.metadata->>'contentDensityScore')::double precision
                ELSE 0.0
            END AS content_density_score
    ) direct_stats
    CROSS JOIN LATERAL (
        SELECT COUNT(*)::int AS overlap_count
        FROM unnest(@query_tokens::text[]) AS token
        WHERE length(token) >= 3
          AND (
                (length(token) >= 5 AND direct_stats.searchable_text LIKE ('%' || token || '%'))
             OR (length(token) < 5 AND direct_stats.searchable_words LIKE ('% ' || token || ' %'))
          )
    ) direct_match
    WHERE @direct_chunk_route_enabled
      AND @query_token_count >= 2
      AND EXISTS (
          SELECT 1
          FROM unnest(@query_tokens::text[]) AS token
          WHERE length(token) >= 5
            AND lower(translate(
                COALESCE(rc.text_content, ''),
                'ÀÁÂÃÄÅàáâãäåÇçÈÉÊËèéêëÌÍÎÏìíîïÑñÒÓÔÕÖØòóôõöøÙÚÛÜùúûüÝýÿ',
                'AAAAAAaaaaaaCcEEEEeeeeIIIIiiiiNnOOOOOOooooooUUUUuuuuYyy'
            )) LIKE ('%' || token || '%')
      )
      AND direct_match.overlap_count >= @direct_min_overlap
      AND COALESCE(rc.metadata->>'contentRole', 'content') <> 'navigation'
      AND COALESCE(rc.metadata->>'chunkType', '') <> 'navigation_index_v1'
      AND (
            NULLIF(rc.metadata->>'navigationScore', '') IS NULL
         OR NULLIF(rc.metadata->>'navigationScore', '') !~ '^[-+]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?:[eE][-+]?[0-9]+)?$'
         OR (rc.metadata->>'navigationScore')::double precision < 0.82
         OR direct_stats.content_density_score >= 0.50
      )
      AND (
            direct_stats.content_density_score >= 0.25
         OR COALESCE(rc.metadata->>'chunkType', '') IN ('section_window_v1', 'unit_exact_v1')
      )
),
routes AS (
    SELECT * FROM anchor_routes
    UNION ALL
    SELECT * FROM navigation_routes
    UNION ALL
    SELECT * FROM direct_chunk_routes
),
ranked_routes AS (
    SELECT
        routes.*,
        ROW_NUMBER() OVER (
            PARTITION BY routes.route_chunk_id
            ORDER BY
                routes.route_rank DESC,
                CASE routes.route_source WHEN 'title_anchor_route' THEN 0 ELSE 1 END,
                routes.route_title ASC
        ) AS route_rn
    FROM routes
)
SELECT
    d.doc_id AS "DocId",
    d.doc_path AS "DocPath",
    d.doc_name AS "DocName",
    d.category AS "Category",
    rc.page_start AS "PageStart",
    rc.page_end AS "PageEnd",
    CASE
        WHEN NULLIF(rc.metadata->>'offsetStart', '') ~ '^[-+]?[0-9]+$'
            THEN CASE
                WHEN (rc.metadata->>'offsetStart')::numeric BETWEEN -2147483648 AND 2147483647
                    THEN (rc.metadata->>'offsetStart')::int
                ELSE NULL
            END
        ELSE NULL
    END AS "OffsetStart",
    CASE
        WHEN NULLIF(rc.metadata->>'offsetEnd', '') ~ '^[-+]?[0-9]+$'
            THEN CASE
                WHEN (rc.metadata->>'offsetEnd')::numeric BETWEEN -2147483648 AND 2147483647
                    THEN (rc.metadata->>'offsetEnd')::int
                ELSE NULL
            END
        ELSE NULL
    END AS "OffsetEnd",
    rc.retrieval_chunk_id AS "ChunkId",
    rc.chunk_index AS "ChunkIndex",
    rc.text_content AS "Text",
    d.indexed_version AS "IngestionVersion",
    LOWER(ENCODE(d.content_hash, 'hex')) AS "HashDoc",
    ('Matched ' || routes.route_source || ': ' || routes.route_title || E'\n' || rc.text_content) AS "EmbedText",
    rc.section_id AS "SectionOrdinalPlaceholder",
    COALESCE(rc.metadata->>'sectionTitle', s.title) AS "SectionTitle",
    COALESCE(rc.metadata->>'headingPath', s.title) AS "HeadingPath",
    COALESCE(rc.metadata->>'chunkType', 'title_anchor_route_v1') AS "ChunkType",
    COALESCE(rc.metadata->>'contentRole', 'content') AS "ContentRole",
    rc.metadata->>'navigationReason' AS "NavigationReason",
    rc.metadata->>'originalChunkType' AS "OriginalChunkType",
    CASE
        WHEN NULLIF(rc.metadata->>'navigationScore', '') ~ '^[-+]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?:[eE][-+]?[0-9]+)?$'
            THEN (rc.metadata->>'navigationScore')::double precision
        ELSE NULL
    END AS "NavigationScore",
    CASE
        WHEN NULLIF(rc.metadata->>'contentDensityScore', '') ~ '^[-+]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?:[eE][-+]?[0-9]+)?$'
            THEN (rc.metadata->>'contentDensityScore')::double precision
        ELSE NULL
    END AS "ContentDensityScore",
    rc.metadata->>'prevChunkId' AS "PrevChunkId",
    rc.metadata->>'nextChunkId' AS "NextChunkId",
    rc.metadata->>'sameSectionChunkId' AS "SameSectionChunkId",
    NULL::text AS "MatchedContentCardsJson",
    MAX(routes.route_rank)::real AS "SparseRank"
FROM ranked_routes routes
JOIN scoped_docs d
  ON d.revision_id = routes.revision_id
JOIN retrieval_chunks rc
  ON rc.retrieval_chunk_id = routes.route_chunk_id
LEFT JOIN document_sections s
  ON s.section_id = rc.section_id
WHERE routes.route_rn = 1
GROUP BY
    d.doc_id,
    d.doc_path,
    d.doc_name,
    d.category,
    d.indexed_version,
    d.content_hash,
    rc.retrieval_chunk_id,
    rc.chunk_index,
    rc.page_start,
    rc.page_end,
    rc.text_content,
    rc.metadata,
    rc.section_id,
    s.title,
    routes.route_source,
    routes.route_title
ORDER BY
    MAX(routes.route_rank) DESC,
    rc.chunk_index ASC
LIMIT @candidate_limit;
""";

        try
        {
            var rows = await conn.QueryAsync<SparseMatchRow>(new CommandDefinition(sql, new
            {
                tenant_id = tenantId,
                query_phrases = focusedPhrases,
                query_tokens = queryTokens,
                query_token_count = Math.Max(1, queryTokens.Length),
                min_overlap = Math.Max(1, minOverlap),
                direct_min_overlap = directMinOverlap,
                direct_route_title = directRouteTitle,
                navigation_route_min_confidence = NavigationRouteMinimumConfidence,
                fuzzy_title_similarity_min = FuzzyTitleSimilarityMinimum,
                direct_chunk_route_enabled = directChunkRouteEnabled,
                category,
                category_path = normalizedCategoryPath,
                doc_id = normalizedDocId,
                doc_path = normalizedDocPath,
                candidate_limit = candidateLimit
            }, cancellationToken: ct));

            var matches = rows
                .OrderByDescending(static row => row.SparseRank)
                .ThenBy(static row => row.DocPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static row => row.ChunkIndex)
                .GroupBy(static row => row.ChunkId)
                .Select(static group => group.First())
                .Take(topK)
                .Select(row => new RagMatch(
                    Score: Math.Clamp(row.SparseRank, 0.0, 1.02),
                    DocId: row.DocId.ToString(),
                    DocPath: row.DocPath,
                    DocName: row.DocName,
                    Category: row.Category,
                    PageStart: row.PageStart,
                    PageEnd: row.PageEnd,
                    OffsetStart: row.OffsetStart,
                    OffsetEnd: row.OffsetEnd,
                    ChunkId: row.ChunkId.ToString(),
                    ChunkIndex: row.ChunkIndex,
                    Text: row.Text,
                    IngestionVersion: row.IngestionVersion,
                    HashDoc: row.HashDoc,
                    EmbedText: row.EmbedText,
                    EmbeddingBasis: row.EmbedText.StartsWith("Matched navigation_route", StringComparison.Ordinal)
                        ? "navigation_route_v1"
                        : row.EmbedText.StartsWith("Matched direct_title_token_route", StringComparison.Ordinal)
                            ? "direct_title_token_route_v1"
                        : "title_anchor_route_v1",
                    SectionOrdinal: null,
                    UnitOrdinal: null,
                    SectionTitle: row.SectionTitle,
                    HeadingPath: row.HeadingPath,
                    ChunkType: row.ChunkType,
                    ContentRole: row.ContentRole,
                    NavigationReason: row.NavigationReason,
                    OriginalChunkType: row.OriginalChunkType,
                    NavigationScore: row.NavigationScore,
                    ContentDensityScore: row.ContentDensityScore,
                    PrevChunkId: row.PrevChunkId,
                    NextChunkId: row.NextChunkId,
                    SameSectionChunkId: row.SameSectionChunkId))
                .Where(static match => !IsNavigationRouteMatch(match) || NavigationRouteHasTargetTitleEvidence(match))
                .ToList();

            return await AttachDocumentProfileContentCardsAsync(ds, tenantId, matches, tokenSource, ct);
        }
        catch (PostgresException ex)
        {
            RetrievalTelemetry.RecordRetrieverDegraded("title_anchor_route", ex);
            degradedRetrieverRef?.Invoke("title_anchor_route", FormatPostgresRetrieverError(ex));
            return [];
        }
    }

    internal static async Task<List<RagMatch>> SearchFuzzyTitleLeadMatchesAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        string query,
        string? category,
        string? docId,
        string? docPath,
        int topK,
        CancellationToken ct,
        string? categoryPath = null,
        Action<string, string?>? degradedRetrieverRef = null)
    {
        if (topK <= 0)
            return [];

        var focusedPhrases = ExtractFocusedLookupPhrases(query)
            .Select(TitleAnchorNormalizer.NormalizeTitle)
            .Where(static phrase => phrase.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();
        if (focusedPhrases.Length == 0)
        {
            var normalizedQueryPhrase = TitleAnchorNormalizer.NormalizeTitle(query);
            if (normalizedQueryPhrase.Length >= 4)
                focusedPhrases = [normalizedQueryPhrase];
        }

        if (focusedPhrases.Length == 0)
            return [];

        var normalizedDocPath = string.IsNullOrWhiteSpace(docPath)
            ? null
            : docPath.Trim().Replace('\\', '/').TrimStart('/');
        var normalizedCategoryPath = NormalizeRagCategoryPathForSql(categoryPath);
        Guid? normalizedDocId = Guid.TryParse(docId, out var parsedDocId) ? parsedDocId : null;
        var candidateLimit = Math.Clamp(topK * 4, topK, 80);

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
WITH query_phrases AS (
    SELECT DISTINCT phrase
    FROM unnest(@query_phrases::text[]) AS phrase
    WHERE phrase IS NOT NULL AND length(phrase) >= 4
),
scoped_docs AS (
    SELECT
        d.doc_id,
        d.doc_path,
        d.doc_name,
        d.category,
        d.indexed_version,
        d.content_hash,
        r.revision_id,
        r.tenant_id
    FROM documents d
    JOIN document_revisions r
      ON r.tenant_id = d.tenant_id
     AND r.doc_id = d.doc_id
     AND r.indexed_version = d.indexed_version
    WHERE d.tenant_id = @tenant_id
      AND d.status = 'indexed'
      AND d.indexed_version > 0
      AND (@category IS NULL OR LOWER(d.category) = @category)
      AND (@category_path IS NULL OR d.doc_path = @category_path OR d.doc_path LIKE (@category_path || '/%'))
      AND (@doc_id IS NULL OR d.doc_id = @doc_id)
      AND (@doc_path IS NULL OR d.doc_path = @doc_path)
),
candidate_chunks AS (
    SELECT
        d.doc_id,
        d.doc_path,
        d.doc_name,
        d.category,
        d.indexed_version,
        d.content_hash,
        rc.retrieval_chunk_id,
        rc.chunk_index,
        rc.page_start,
        rc.page_end,
        rc.text_content,
        rc.metadata,
        rc.section_id,
        qp.phrase,
        fuzzy.fuzzy_score,
        CASE
            WHEN COALESCE(rc.metadata->>'chunkType', '') = 'unit_exact_v1' THEN 2
            WHEN COALESCE(rc.metadata->>'chunkType', '') = 'section_window_v1' THEN 1
            ELSE 0
        END AS structured_priority
    FROM scoped_docs d
    JOIN retrieval_chunks rc
      ON rc.tenant_id = d.tenant_id
     AND rc.revision_id = d.revision_id
    CROSS JOIN query_phrases qp
    CROSS JOIN LATERAL (
        SELECT LOWER(regexp_replace(
            translate(
                replace(
                    replace(
                        replace(
                            replace(LOWER(LEFT(rc.text_content, @lead_chars)), U&'\0153', 'oe'),
                            U&'\0152',
                            'oe'),
                        U&'\00E6',
                        'ae'),
                    U&'\00C6',
                    'ae'),
                'àáâãäåçèéêëìíîïñòóôõöùúûüýÿ',
                'aaaaaaceeeeiiiinooooouuuuyy'),
            '[^[:alnum:]]+',
            ' ',
            'g')) AS lead_text
    ) lead
    CROSS JOIN LATERAL (
        SELECT GREATEST(
            word_similarity(qp.phrase, lead.lead_text),
            strict_word_similarity(qp.phrase, lead.lead_text)) AS fuzzy_score
    ) fuzzy
    WHERE COALESCE(rc.metadata->>'contentRole', 'content') <> 'navigation'
      AND COALESCE(rc.metadata->>'chunkType', 'contextual_text_v1') <> 'navigation_index_v1'
      AND fuzzy.fuzzy_score >= @fuzzy_title_lead_similarity_min
)
SELECT
    cc.doc_id AS "DocId",
    cc.doc_path AS "DocPath",
    cc.doc_name AS "DocName",
    cc.category AS "Category",
    cc.page_start AS "PageStart",
    cc.page_end AS "PageEnd",
    CASE
        WHEN NULLIF(cc.metadata->>'offsetStart', '') ~ '^[-+]?[0-9]+$'
            THEN CASE
                WHEN (cc.metadata->>'offsetStart')::numeric BETWEEN -2147483648 AND 2147483647
                    THEN (cc.metadata->>'offsetStart')::int
                ELSE NULL
            END
        ELSE NULL
    END AS "OffsetStart",
    CASE
        WHEN NULLIF(cc.metadata->>'offsetEnd', '') ~ '^[-+]?[0-9]+$'
            THEN CASE
                WHEN (cc.metadata->>'offsetEnd')::numeric BETWEEN -2147483648 AND 2147483647
                    THEN (cc.metadata->>'offsetEnd')::int
                ELSE NULL
            END
        ELSE NULL
    END AS "OffsetEnd",
    cc.retrieval_chunk_id AS "ChunkId",
    cc.chunk_index AS "ChunkIndex",
    cc.text_content AS "Text",
    cc.indexed_version AS "IngestionVersion",
    LOWER(ENCODE(cc.content_hash, 'hex')) AS "HashDoc",
    ('Matched fuzzy_title_lead: ' || cc.phrase || E'\n' || cc.text_content) AS "EmbedText",
    cc.section_id AS "SectionOrdinalPlaceholder",
    COALESCE(cc.metadata->>'sectionTitle', s.title) AS "SectionTitle",
    COALESCE(cc.metadata->>'headingPath', s.title) AS "HeadingPath",
    COALESCE(cc.metadata->>'chunkType', 'contextual_text_v1') AS "ChunkType",
    COALESCE(cc.metadata->>'contentRole', 'content') AS "ContentRole",
    cc.metadata->>'navigationReason' AS "NavigationReason",
    cc.metadata->>'originalChunkType' AS "OriginalChunkType",
    CASE
        WHEN NULLIF(cc.metadata->>'navigationScore', '') ~ '^[-+]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?:[eE][-+]?[0-9]+)?$'
            THEN (cc.metadata->>'navigationScore')::double precision
        ELSE NULL
    END AS "NavigationScore",
    CASE
        WHEN NULLIF(cc.metadata->>'contentDensityScore', '') ~ '^[-+]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?:[eE][-+]?[0-9]+)?$'
            THEN (cc.metadata->>'contentDensityScore')::double precision
        ELSE NULL
    END AS "ContentDensityScore",
    cc.metadata->>'prevChunkId' AS "PrevChunkId",
    cc.metadata->>'nextChunkId' AS "NextChunkId",
    cc.metadata->>'sameSectionChunkId' AS "SameSectionChunkId",
    NULL::text AS "MatchedContentCardsJson",
    LEAST(0.98, 0.66 + (cc.fuzzy_score * 0.24) + (cc.structured_priority * 0.03))::real AS "SparseRank"
FROM candidate_chunks cc
LEFT JOIN document_sections s
  ON s.section_id = cc.section_id
ORDER BY
    "SparseRank" DESC,
    cc.structured_priority DESC,
    cc.doc_path ASC,
    cc.chunk_index ASC
LIMIT @candidate_limit;
""";

        try
        {
            var rows = await conn.QueryAsync<SparseMatchRow>(new CommandDefinition(sql, new
            {
                tenant_id = tenantId,
                query_phrases = focusedPhrases,
                lead_chars = 260,
                fuzzy_title_lead_similarity_min = FuzzyTitleLeadSimilarityMinimum,
                category,
                category_path = normalizedCategoryPath,
                doc_id = normalizedDocId,
                doc_path = normalizedDocPath,
                candidate_limit = candidateLimit
            }, cancellationToken: ct));

            var matches = rows
                .OrderByDescending(static row => row.SparseRank)
                .ThenBy(static row => row.DocPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static row => row.ChunkIndex)
                .GroupBy(static row => row.ChunkId)
                .Select(static group => group.First())
                .Take(topK)
                .Select(row => new RagMatch(
                    Score: Math.Clamp(row.SparseRank, 0.0, 0.98),
                    DocId: row.DocId.ToString(),
                    DocPath: row.DocPath,
                    DocName: row.DocName,
                    Category: row.Category,
                    PageStart: row.PageStart,
                    PageEnd: row.PageEnd,
                    OffsetStart: row.OffsetStart,
                    OffsetEnd: row.OffsetEnd,
                    ChunkId: row.ChunkId.ToString(),
                    ChunkIndex: row.ChunkIndex,
                    Text: row.Text,
                    IngestionVersion: row.IngestionVersion,
                    HashDoc: row.HashDoc,
                    EmbedText: row.EmbedText,
                    EmbeddingBasis: "fuzzy_title_lead_v1",
                    SectionOrdinal: null,
                    UnitOrdinal: null,
                    SectionTitle: row.SectionTitle,
                    HeadingPath: row.HeadingPath,
                    ChunkType: row.ChunkType,
                    ContentRole: row.ContentRole,
                    NavigationReason: row.NavigationReason,
                    OriginalChunkType: row.OriginalChunkType,
                    NavigationScore: row.NavigationScore,
                    ContentDensityScore: row.ContentDensityScore,
                    PrevChunkId: row.PrevChunkId,
                    NextChunkId: row.NextChunkId,
                    SameSectionChunkId: row.SameSectionChunkId))
                .ToList();

            return await AttachDocumentProfileContentCardsAsync(ds, tenantId, matches, query, ct);
        }
        catch (PostgresException ex)
        {
            RetrievalTelemetry.RecordRetrieverDegraded("fuzzy_title_lead", ex);
            degradedRetrieverRef?.Invoke("fuzzy_title_lead", FormatPostgresRetrieverError(ex));
            return [];
        }
    }

    private static async Task<List<RagMatch>> SearchDenseMatchesAsync(
        NpgsqlDataSource ds,
        IHttpClientFactory httpFactory,
        RagOptions rag,
        Guid tenantId,
        string queryNorm,
        string? category,
        string? docId,
        string? docPath,
        int candidates,
        CancellationToken ct,
        Action<long> teiMsRef,
        Action<long> qdrantMsRef,
        Action<int> qdrantStatusRef,
        string? categoryPath = null)
    {
        var tei = httpFactory.CreateClient("tei");
        tei.BaseAddress = new Uri(rag.EmbeddingsBaseUrl);

        var swTei = Stopwatch.StartNew();
        var queryEmbeddingInput = TeiClient.FormatEmbeddingInput(
            rag.EmbeddingsModel,
            queryNorm,
            TeiClient.EmbeddingInputKind.Query);
        var emb = await TeiClient.EmbedAsync(tei, rag.EmbeddingsModel, [queryEmbeddingInput], ct);
        swTei.Stop();
        teiMsRef(swTei.ElapsedMilliseconds);

        var qvec = emb[0];
        var qdrant = httpFactory.CreateClient("qdrant");
        qdrant.BaseAddress = new Uri(rag.QdrantBaseUrl);

        var filterMust = new List<object>
        {
            new { key = "tenant_id", match = new { value = tenantId.ToString() } }
        };
        var qdrantCategory = NormalizeRagCategory(category);
        if (!string.IsNullOrWhiteSpace(qdrantCategory))
            filterMust.Add(new { key = "category", match = new { value = qdrantCategory } });
        if (!string.IsNullOrWhiteSpace(docId))
            filterMust.Add(new { key = "doc_id", match = new { value = docId.Trim() } });
        if (!string.IsNullOrWhiteSpace(docPath))
            filterMust.Add(new { key = "doc_path", match = new { value = docPath.Trim().Replace('\\', '/') } });
        var expectedEmbeddingInputFormat = TeiClient.RequiresE5InstructionPrefix(rag.EmbeddingsModel)
            ? "e5_passage_v1"
            : "raw_passage_v1";
        filterMust.Add(new { key = "embedding_input_format", match = new { value = expectedEmbeddingInputFormat } });
        if (!string.IsNullOrWhiteSpace(rag.EmbeddingsModel))
            filterMust.Add(new { key = "embedding_model", match = new { value = rag.EmbeddingsModel.Trim() } });

        var qdrantLimit = string.IsNullOrWhiteSpace(categoryPath)
            ? candidates
            : Math.Clamp(candidates * 3, candidates, Math.Max(candidates, 256));
        var payload = new
        {
            vector = qvec,
            limit = qdrantLimit,
            with_payload = true,
            filter = new { must = filterMust }
        };

        var url = $"/collections/{rag.QdrantCollection}/points/search";
        HttpResponseMessage? resp = null;

        var swQ = Stopwatch.StartNew();
        try
        {
            using (var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"))
                resp = await qdrant.PostAsync(url, content, ct);

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                resp.Dispose();
                resp = null;
                await QdrantClient.EnsureCollectionAsync(qdrant, rag.QdrantCollection, qvec.Length, ct);

                using var content2 = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                resp = await qdrant.PostAsync(url, content2, ct);
            }

            qdrantStatusRef((int)resp.StatusCode);

            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                throw new Exception($"Qdrant search failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {errBody}");
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var rawMatches = QdrantClient.ParseSearchResults(doc);
            var embeddingCompatibleMatches = rawMatches
                .Where(match => IsDenseMatchEmbeddingCompatible(match, rag.EmbeddingsModel))
                .ToList();
            var filtered = await FilterMatchesAgainstActiveDocumentVersionsAsync(ds, tenantId, embeddingCompatibleMatches, ct, categoryPath);
            var enriched = await AttachDocumentProfileContentCardsAsync(ds, tenantId, filtered, queryNorm, ct);
            return RerankDenseMatches(enriched).Take(candidates).ToList();
        }
        finally
        {
            swQ.Stop();
            qdrantMsRef(swQ.ElapsedMilliseconds);
            resp?.Dispose();
        }
    }

    private sealed record RerankAttempt(List<RagMatch> Matches, bool Applied);

    internal static bool IsDenseMatchEmbeddingCompatible(RagMatch match, string? embeddingsModel)
    {
        var expectedFormat = TeiClient.RequiresE5InstructionPrefix(embeddingsModel)
            ? "e5_passage_v1"
            : "raw_passage_v1";

        if (!string.Equals(match.EmbeddingInputFormat, expectedFormat, StringComparison.OrdinalIgnoreCase))
            return false;

        return string.IsNullOrWhiteSpace(embeddingsModel)
            || (!string.IsNullOrWhiteSpace(match.EmbeddingModel)
                && string.Equals(match.EmbeddingModel.Trim(), embeddingsModel.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<RerankAttempt> TryRerankWithTeiAsync(
        IHttpClientFactory httpFactory,
        RagOptions rag,
        string query,
        IReadOnlyList<RagMatch> candidates,
        CancellationToken ct,
        Action<long> rerankMsRef)
    {
        if (!rag.EnableRerank || candidates.Count <= 1)
        {
            rerankMsRef(0);
            return new RerankAttempt(candidates.ToList(), Applied: false);
        }

        var rerankBaseUrl = string.IsNullOrWhiteSpace(rag.RerankBaseUrl)
            ? rag.EmbeddingsBaseUrl
            : rag.RerankBaseUrl!;
        var maxCandidates = Math.Clamp(rag.RerankMaxCandidates, 2, Math.Max(2, candidates.Count));
        var rerankSlice = candidates.Take(maxCandidates).ToList();
        var texts = rerankSlice
            .Select(match => string.IsNullOrWhiteSpace(match.EmbedText) ? (match.Text ?? string.Empty) : match.EmbedText!)
            .ToArray();

        var tei = httpFactory.CreateClient("tei");
        tei.BaseAddress = new Uri(rerankBaseUrl);

        var sw = Stopwatch.StartNew();
        try
        {
            var reranked = await TeiClient.RerankAsync(tei, rag.RerankModel, query, texts, ct);
            return new RerankAttempt(ApplyRerankScores(candidates, reranked, rerankSlice.Count), Applied: true);
        }
        catch
        {
            return new RerankAttempt(candidates.ToList(), Applied: false);
        }
        finally
        {
            sw.Stop();
            rerankMsRef(sw.ElapsedMilliseconds);
        }
    }

    internal static async Task<List<RagMatch>> SearchSparseMatchesAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        string query,
        string? category,
        string? docId,
        string? docPath,
        int topK,
        CancellationToken ct,
        Action<long> sparseMsRef,
        string? lexicalExpansionQuery = null,
        string? categoryPath = null,
        bool includeProfileCardMatches = true,
        Action<string, string?>? degradedRetrieverRef = null)
    {
        if (string.IsNullOrWhiteSpace(query) || topK <= 0)
        {
            sparseMsRef(0);
            return [];
        }

        var normalizedDocPath = string.IsNullOrWhiteSpace(docPath)
            ? null
            : docPath.Trim().Replace('\\', '/').TrimStart('/');
        var normalizedCategoryPath = NormalizeRagCategoryPathForSql(categoryPath);
        Guid? normalizedDocId = Guid.TryParse(docId, out var parsedDocId) ? parsedDocId : null;
        var sparseQueryText = string.IsNullOrWhiteSpace(lexicalExpansionQuery)
            ? query.Trim()
            : lexicalExpansionQuery.Trim();

        var lexicalTerms = BuildLexicalContentFallbackTerms(sparseQueryText);

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
WITH sparse_query AS (
    SELECT websearch_to_tsquery('simple', @query_text) AS q
),
lexical_terms AS (
    SELECT DISTINCT LOWER(term) AS term
    FROM unnest(@lexical_terms::text[]) AS term
    WHERE term IS NOT NULL AND term <> ''
),
profile_card_matches AS (
    SELECT
        r.revision_id,
        GREATEST(1, COALESCE(pcc.page_start, 1)) AS card_page_start,
        GREATEST(
            GREATEST(1, COALESCE(pcc.page_start, 1)),
            COALESCE(pcc.page_end, GREATEST(1, COALESCE(pcc.page_start, 1)))
        ) AS card_page_end,
        COUNT(*) AS match_count,
        LEFT(string_agg(DISTINCT pcc.title, '; '), 500) AS card_titles,
        jsonb_build_object(
            'contentCards',
            jsonb_agg(DISTINCT jsonb_build_object(
                'title', pcc.title,
                'contentCardId', pcc.content_card_id,
                'pageStart', pcc.page_start,
                'pageEnd', pcc.page_end,
                'kind', pcc.kind,
                'signals', pcc.signals,
                'evidence', pcc.metadata->'evidence')))::text AS matched_content_cards_json,
        COALESCE(SUM(
            CASE
                WHEN lexical_terms.term LIKE '% %' AND length(lexical_terms.term) >= 18 THEN 8.0
                WHEN lexical_terms.term LIKE '% %' THEN 6.0
                WHEN length(lexical_terms.term) >= 10 THEN 3.0
                WHEN length(lexical_terms.term) >= 7 THEN 2.0
                ELSE 1.1
            END), 0.0) AS match_weight
    FROM documents d
    JOIN document_revisions r
      ON r.tenant_id = d.tenant_id
     AND r.doc_id = d.doc_id
     AND r.indexed_version = d.indexed_version
    JOIN LATERAL (
        SELECT DISTINCT ON (
            card.normalized_title,
            GREATEST(1, COALESCE(card.page_start, 1)),
            GREATEST(
                GREATEST(1, COALESCE(card.page_start, 1)),
                COALESCE(card.page_end, GREATEST(1, COALESCE(card.page_start, 1)))
            ))
            card.*
        FROM document_profile_content_cards card
        WHERE card.tenant_id = r.tenant_id
          AND card.revision_id = r.revision_id
        ORDER BY
            card.normalized_title,
            GREATEST(1, COALESCE(card.page_start, 1)),
            GREATEST(
                GREATEST(1, COALESCE(card.page_start, 1)),
                COALESCE(card.page_end, GREATEST(1, COALESCE(card.page_start, 1)))
            ),
            CASE card.profile_version
                WHEN 'llm_backoffice_v1' THEN 0
                WHEN 'deterministic_v1' THEN 1
                ELSE 2
            END,
            card.updated_at DESC,
            card.card_index ASC
    ) pcc ON TRUE
    CROSS JOIN lexical_terms
    WHERE d.tenant_id = @tenant_id
      AND @include_profile_card_matches
      AND d.status = 'indexed'
      AND d.indexed_version > 0
      AND (@category IS NULL OR LOWER(d.category) = @category)
      AND (@category_path IS NULL OR d.doc_path = @category_path OR d.doc_path LIKE (@category_path || '/%'))
      AND (@doc_id IS NULL OR d.doc_id = @doc_id)
      AND (@doc_path IS NULL OR d.doc_path = @doc_path)
      AND pcc.page_start IS NOT NULL
      AND CASE
          WHEN lexical_terms.term LIKE '% %'
              THEN LOWER(pcc.search_text) ~ REPLACE(lexical_terms.term, ' ', '([^[:alnum:]]+[[:alnum:]]{1,3}){0,2}[^[:alnum:]]+')
                   OR pcc.normalized_title ~ REPLACE(lexical_terms.term, ' ', '([^[:alnum:]]+[[:alnum:]]{1,3}){0,2}[^[:alnum:]]+')
          ELSE LOWER(pcc.search_text) LIKE '%' || lexical_terms.term || '%'
               OR pcc.normalized_title LIKE '%' || lexical_terms.term || '%'
      END
    GROUP BY
        r.revision_id,
        GREATEST(1, COALESCE(pcc.page_start, 1)),
        GREATEST(
            GREATEST(1, COALESCE(pcc.page_start, 1)),
            COALESCE(pcc.page_end, GREATEST(1, COALESCE(pcc.page_start, 1)))
        )
)
SELECT
    d.doc_id AS "DocId",
    d.doc_path AS "DocPath",
    d.doc_name AS "DocName",
    d.category AS "Category",
    rc.page_start AS "PageStart",
    rc.page_end AS "PageEnd",
    CASE
        WHEN NULLIF(rc.metadata->>'offsetStart', '') ~ '^[-+]?[0-9]+$'
            THEN CASE
                WHEN (rc.metadata->>'offsetStart')::numeric BETWEEN -2147483648 AND 2147483647
                    THEN (rc.metadata->>'offsetStart')::int
                ELSE NULL
            END
        ELSE NULL
    END AS "OffsetStart",
    CASE
        WHEN NULLIF(rc.metadata->>'offsetEnd', '') ~ '^[-+]?[0-9]+$'
            THEN CASE
                WHEN (rc.metadata->>'offsetEnd')::numeric BETWEEN -2147483648 AND 2147483647
                    THEN (rc.metadata->>'offsetEnd')::int
                ELSE NULL
            END
        ELSE NULL
    END AS "OffsetEnd",
    rc.retrieval_chunk_id AS "ChunkId",
    rc.chunk_index AS "ChunkIndex",
    rc.text_content AS "Text",
    d.indexed_version AS "IngestionVersion",
    LOWER(ENCODE(d.content_hash, 'hex')) AS "HashDoc",
    CASE
        WHEN COALESCE(pcm.card_titles, '') <> ''
            THEN 'Matched profile title: ' || pcm.card_titles || E'\n' || cte.text_content
        ELSE cte.text_content
    END AS "EmbedText",
    rc.section_id AS "SectionOrdinalPlaceholder",
    COALESCE(rc.metadata->>'sectionTitle', s.title) AS "SectionTitle",
    COALESCE(rc.metadata->>'headingPath', s.title) AS "HeadingPath",
    COALESCE(rc.metadata->>'chunkType', 'contextual_text_v1') AS "ChunkType",
    COALESCE(rc.metadata->>'contentRole', 'content') AS "ContentRole",
    rc.metadata->>'navigationReason' AS "NavigationReason",
    rc.metadata->>'originalChunkType' AS "OriginalChunkType",
    CASE
        WHEN NULLIF(rc.metadata->>'navigationScore', '') ~ '^[-+]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?:[eE][-+]?[0-9]+)?$'
            THEN (rc.metadata->>'navigationScore')::double precision
        ELSE NULL
    END AS "NavigationScore",
    CASE
        WHEN NULLIF(rc.metadata->>'contentDensityScore', '') ~ '^[-+]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?:[eE][-+]?[0-9]+)?$'
            THEN (rc.metadata->>'contentDensityScore')::double precision
        ELSE NULL
    END AS "ContentDensityScore",
    rc.metadata->>'prevChunkId' AS "PrevChunkId",
    rc.metadata->>'nextChunkId' AS "NextChunkId",
    rc.metadata->>'sameSectionChunkId' AS "SameSectionChunkId",
    pcm.matched_content_cards_json AS "MatchedContentCardsJson",
    (
        ts_rank_cd(
            to_tsvector('simple', cte.text_content),
            sparse_query.q,
            32
        ) + ((COALESCE(pcm.match_weight, 0.0) * 0.035)::real)
    )::real AS "SparseRank"
FROM sparse_query
JOIN documents d
  ON d.tenant_id = @tenant_id
 AND d.status = 'indexed'
 AND d.indexed_version > 0
JOIN document_revisions r
  ON r.tenant_id = d.tenant_id
 AND r.doc_id = d.doc_id
 AND r.indexed_version = d.indexed_version
JOIN contextual_text_entries cte
  ON cte.tenant_id = r.tenant_id
 AND cte.revision_id = r.revision_id
JOIN retrieval_chunks rc
  ON rc.retrieval_chunk_id = cte.retrieval_chunk_id
LEFT JOIN document_sections s
  ON s.section_id = rc.section_id
LEFT JOIN profile_card_matches pcm
  ON pcm.revision_id = r.revision_id
 AND rc.page_start <= pcm.card_page_end
 AND rc.page_end >= pcm.card_page_start
WHERE (to_tsvector('simple', cte.text_content) @@ sparse_query.q OR COALESCE(pcm.match_count, 0) > 0)
  AND (@category IS NULL OR LOWER(d.category) = @category)
  AND (@category_path IS NULL OR d.doc_path = @category_path OR d.doc_path LIKE (@category_path || '/%'))
  AND (@doc_id IS NULL OR d.doc_id = @doc_id)
  AND (@doc_path IS NULL OR d.doc_path = @doc_path)
ORDER BY "SparseRank" DESC,
         COALESCE(pcm.match_count, 0) DESC,
         rc.chunk_index ASC
LIMIT @top_k;
""";

        var swSparse = Stopwatch.StartNew();
        try
        {
            var rows = (await conn.QueryAsync<SparseMatchRow>(new CommandDefinition(sql, new
            {
                tenant_id = tenantId,
                query_text = sparseQueryText,
                lexical_terms = lexicalTerms.ToArray(),
                include_profile_card_matches = includeProfileCardMatches,
                category,
                category_path = normalizedCategoryPath,
                doc_id = normalizedDocId,
                doc_path = normalizedDocPath,
                top_k = topK
            }, cancellationToken: ct))).ToList();

            var hasScopedSparseSearch = !string.IsNullOrWhiteSpace(category)
                || !string.IsNullOrWhiteSpace(normalizedCategoryPath)
                || normalizedDocId.HasValue
                || !string.IsNullOrWhiteSpace(normalizedDocPath);
            if (lexicalTerms.Count > 0
                && (rows.Count == 0
                    || ShouldSupplementSparseWithLexicalFallback(hasScopedSparseSearch, sparseQueryText)))
            {
                var fallbackRows = (await conn.QueryAsync<SparseMatchRow>(new CommandDefinition(LexicalContentFallbackSql, new
                {
                    tenant_id = tenantId,
                    lexical_terms = lexicalTerms.ToArray(),
                    category,
                    category_path = normalizedCategoryPath,
                    doc_id = normalizedDocId,
                    doc_path = normalizedDocPath,
                    top_k = topK
                }, cancellationToken: ct))).ToList();

                rows = rows.Count == 0
                    ? fallbackRows
                    : MergeSparseRows(fallbackRows, rows, topK);
            }

            return rows.Select(row => new RagMatch(
                Score: NormalizeSparseScore(row.SparseRank),
                DocId: row.DocId.ToString(),
                DocPath: row.DocPath,
                DocName: row.DocName,
                Category: row.Category,
                PageStart: row.PageStart,
                PageEnd: row.PageEnd,
                OffsetStart: row.OffsetStart,
                OffsetEnd: row.OffsetEnd,
                ChunkId: row.ChunkId.ToString(),
                ChunkIndex: row.ChunkIndex,
                Text: row.Text,
                IngestionVersion: row.IngestionVersion,
                HashDoc: row.HashDoc,
                EmbedText: row.EmbedText,
                EmbeddingBasis: "sparse_bm25_v1",
                SectionOrdinal: null,
                UnitOrdinal: null,
                SectionTitle: row.SectionTitle,
                HeadingPath: row.HeadingPath,
                ChunkType: row.ChunkType,
                ContentRole: row.ContentRole,
                NavigationReason: row.NavigationReason,
                OriginalChunkType: row.OriginalChunkType,
                NavigationScore: row.NavigationScore,
                ContentDensityScore: row.ContentDensityScore,
                PrevChunkId: row.PrevChunkId,
                NextChunkId: row.NextChunkId,
                SameSectionChunkId: row.SameSectionChunkId,
                MatchedContentCards: BuildSparseMatchedContentCards(row, query))).ToList();
        }
        catch (PostgresException ex)
        {
            RetrievalTelemetry.RecordRetrieverDegraded("sparse_bm25", ex);
            degradedRetrieverRef?.Invoke("sparse_bm25", FormatPostgresRetrieverError(ex));
            return [];
        }
        finally
        {
            swSparse.Stop();
            sparseMsRef(swSparse.ElapsedMilliseconds);
        }
    }

private const string LexicalContentFallbackSql = """
WITH lexical_terms AS (
    SELECT DISTINCT LOWER(term) AS term
    FROM unnest(@lexical_terms::text[]) AS term
    WHERE term IS NOT NULL AND term <> ''
),
single_terms AS (
    SELECT term
    FROM lexical_terms
    WHERE term NOT LIKE '% %'
),
phrase_terms AS (
    SELECT term
    FROM lexical_terms
    WHERE term LIKE '% %'
),
scoped_docs AS (
    SELECT
        d.tenant_id,
        d.doc_id,
        d.doc_path,
        d.doc_name,
        d.category,
        d.indexed_version,
        d.content_hash,
        r.revision_id
    FROM documents d
    JOIN document_revisions r
      ON r.tenant_id = d.tenant_id
     AND r.doc_id = d.doc_id
     AND r.indexed_version = d.indexed_version
    WHERE d.tenant_id = @tenant_id
      AND d.status = 'indexed'
      AND d.indexed_version > 0
      AND (@category IS NULL OR LOWER(d.category) = @category)
      AND (@category_path IS NULL OR d.doc_path = @category_path OR d.doc_path LIKE (@category_path || '/%'))
      AND (@doc_id IS NULL OR d.doc_id = @doc_id)
      AND (@doc_path IS NULL OR d.doc_path = @doc_path)
),
single_chunk_term_matches AS (
    SELECT
        rc.retrieval_chunk_id,
        single_terms.term,
        CASE
            WHEN length(single_terms.term) >= 10 THEN 2.5
            WHEN length(single_terms.term) >= 7 THEN 1.6
            ELSE 1.0
        END AS match_weight
    FROM scoped_docs d
    JOIN retrieval_chunks rc
      ON rc.tenant_id = d.tenant_id
     AND rc.revision_id = d.revision_id
    JOIN single_terms
      ON LOWER(rc.text_content) LIKE '%' || single_terms.term || '%'
),
single_chunk_candidates AS (
    SELECT DISTINCT retrieval_chunk_id
    FROM single_chunk_term_matches
),
phrase_chunk_term_matches AS (
    SELECT
        rc.retrieval_chunk_id,
        phrase_terms.term,
        CASE
            WHEN length(phrase_terms.term) >= 18 THEN 7.0
            ELSE 5.0
        END AS match_weight
    FROM scoped_docs d
    JOIN retrieval_chunks rc
      ON rc.tenant_id = d.tenant_id
     AND rc.revision_id = d.revision_id
    JOIN phrase_terms
      ON (
        NOT EXISTS (SELECT 1 FROM single_terms)
        OR EXISTS (
            SELECT 1
            FROM single_chunk_candidates candidate
            WHERE candidate.retrieval_chunk_id = rc.retrieval_chunk_id
        )
     )
     AND LOWER(rc.text_content) ~ REPLACE(phrase_terms.term, ' ', '([^[:alnum:]]+[[:alnum:]]{1,3}){0,2}[^[:alnum:]]+')
),
chunk_term_matches AS (
    SELECT retrieval_chunk_id, term, match_weight
    FROM single_chunk_term_matches

    UNION ALL

    SELECT retrieval_chunk_id, term, match_weight
    FROM phrase_chunk_term_matches
),
ranked_chunk_terms AS (
    SELECT
        retrieval_chunk_id,
        COUNT(DISTINCT term) AS match_count,
        SUM(match_weight)::real AS match_weight
    FROM chunk_term_matches
    GROUP BY retrieval_chunk_id
),
single_profile_card_term_matches AS (
    SELECT
        pcc.content_card_id,
        pcc.revision_id,
        GREATEST(1, COALESCE(pcc.page_start, 1)) AS card_page_start,
        GREATEST(
            GREATEST(1, COALESCE(pcc.page_start, 1)),
            COALESCE(pcc.page_end, GREATEST(1, COALESCE(pcc.page_start, 1)))
        ) AS card_page_end,
        pcc.title,
        jsonb_build_object(
            'title', pcc.title,
            'contentCardId', pcc.content_card_id,
            'pageStart', pcc.page_start,
            'pageEnd', pcc.page_end,
            'kind', pcc.kind,
            'signals', pcc.signals,
            'evidence', pcc.metadata->'evidence') AS card_json,
        single_terms.term,
        CASE
            WHEN length(single_terms.term) >= 10 THEN 3.0
            WHEN length(single_terms.term) >= 7 THEN 2.0
            ELSE 1.1
        END AS match_weight
    FROM scoped_docs d
    JOIN LATERAL (
        SELECT DISTINCT ON (
            card.normalized_title,
            GREATEST(1, COALESCE(card.page_start, 1)),
            GREATEST(
                GREATEST(1, COALESCE(card.page_start, 1)),
                COALESCE(card.page_end, GREATEST(1, COALESCE(card.page_start, 1)))
            ))
            card.*
        FROM document_profile_content_cards card
        WHERE card.tenant_id = d.tenant_id
          AND card.revision_id = d.revision_id
        ORDER BY
            card.normalized_title,
            GREATEST(1, COALESCE(card.page_start, 1)),
            GREATEST(
                GREATEST(1, COALESCE(card.page_start, 1)),
                COALESCE(card.page_end, GREATEST(1, COALESCE(card.page_start, 1)))
            ),
            CASE card.profile_version
                WHEN 'llm_backoffice_v1' THEN 0
                WHEN 'deterministic_v1' THEN 1
                ELSE 2
            END,
            card.updated_at DESC,
            card.card_index ASC
    ) pcc ON TRUE
    JOIN single_terms
      ON LOWER(pcc.search_text) LIKE '%' || single_terms.term || '%'
      OR pcc.normalized_title LIKE '%' || single_terms.term || '%'
    WHERE pcc.page_start IS NOT NULL
),
single_profile_card_candidates AS (
    SELECT DISTINCT content_card_id
    FROM single_profile_card_term_matches
),
phrase_profile_card_term_matches AS (
    SELECT
        pcc.content_card_id,
        pcc.revision_id,
        GREATEST(1, COALESCE(pcc.page_start, 1)) AS card_page_start,
        GREATEST(
            GREATEST(1, COALESCE(pcc.page_start, 1)),
            COALESCE(pcc.page_end, GREATEST(1, COALESCE(pcc.page_start, 1)))
        ) AS card_page_end,
        pcc.title,
        jsonb_build_object(
            'title', pcc.title,
            'contentCardId', pcc.content_card_id,
            'pageStart', pcc.page_start,
            'pageEnd', pcc.page_end,
            'kind', pcc.kind,
            'signals', pcc.signals,
            'evidence', pcc.metadata->'evidence') AS card_json,
        phrase_terms.term,
        CASE
            WHEN length(phrase_terms.term) >= 18 THEN 8.0
            ELSE 6.0
        END AS match_weight
    FROM scoped_docs d
    JOIN LATERAL (
        SELECT DISTINCT ON (
            card.normalized_title,
            GREATEST(1, COALESCE(card.page_start, 1)),
            GREATEST(
                GREATEST(1, COALESCE(card.page_start, 1)),
                COALESCE(card.page_end, GREATEST(1, COALESCE(card.page_start, 1)))
            ))
            card.*
        FROM document_profile_content_cards card
        WHERE card.tenant_id = d.tenant_id
          AND card.revision_id = d.revision_id
        ORDER BY
            card.normalized_title,
            GREATEST(1, COALESCE(card.page_start, 1)),
            GREATEST(
                GREATEST(1, COALESCE(card.page_start, 1)),
                COALESCE(card.page_end, GREATEST(1, COALESCE(card.page_start, 1)))
            ),
            CASE card.profile_version
                WHEN 'llm_backoffice_v1' THEN 0
                WHEN 'deterministic_v1' THEN 1
                ELSE 2
            END,
            card.updated_at DESC,
            card.card_index ASC
    ) pcc ON TRUE
    JOIN phrase_terms
      ON (
        NOT EXISTS (SELECT 1 FROM single_terms)
        OR EXISTS (
            SELECT 1
            FROM single_profile_card_candidates candidate
            WHERE candidate.content_card_id = pcc.content_card_id
        )
     )
     AND (
        LOWER(pcc.search_text) ~ REPLACE(phrase_terms.term, ' ', '([^[:alnum:]]+[[:alnum:]]{1,3}){0,2}[^[:alnum:]]+')
        OR pcc.normalized_title ~ REPLACE(phrase_terms.term, ' ', '([^[:alnum:]]+[[:alnum:]]{1,3}){0,2}[^[:alnum:]]+')
     )
    WHERE pcc.page_start IS NOT NULL
),
profile_card_term_matches AS (
    SELECT revision_id, card_page_start, card_page_end, title, card_json, term, match_weight
    FROM single_profile_card_term_matches

    UNION ALL

    SELECT revision_id, card_page_start, card_page_end, title, card_json, term, match_weight
    FROM phrase_profile_card_term_matches
),
profile_card_matches AS (
    SELECT
        revision_id,
        card_page_start,
        card_page_end,
        COUNT(DISTINCT term) AS match_count,
        LEFT(string_agg(DISTINCT title, '; '), 500) AS card_titles,
        jsonb_build_object('contentCards', jsonb_agg(DISTINCT card_json))::text AS matched_content_cards_json,
        SUM(match_weight)::real AS match_weight
    FROM profile_card_term_matches
    GROUP BY revision_id, card_page_start, card_page_end
),
profile_matched_chunks AS (
    SELECT
        rc.retrieval_chunk_id,
        SUM(pcm.match_count)::int AS match_count,
        LEFT(string_agg(DISTINCT pcm.card_titles, '; '), 500) AS card_titles,
        jsonb_build_object('contentCards', jsonb_agg(DISTINCT matched_card.card))::text AS matched_content_cards_json,
        SUM(pcm.match_weight)::real AS match_weight
    FROM profile_card_matches pcm
    LEFT JOIN LATERAL jsonb_array_elements(COALESCE((pcm.matched_content_cards_json::jsonb)->'contentCards', '[]'::jsonb)) AS matched_card(card) ON TRUE
    JOIN retrieval_chunks rc
      ON rc.revision_id = pcm.revision_id
     AND rc.page_start <= pcm.card_page_end
     AND rc.page_end >= pcm.card_page_start
    GROUP BY rc.retrieval_chunk_id
),
candidate_chunks AS (
    SELECT retrieval_chunk_id FROM ranked_chunk_terms
    UNION
    SELECT retrieval_chunk_id FROM profile_matched_chunks
)
SELECT
    d.doc_id AS "DocId",
    d.doc_path AS "DocPath",
    d.doc_name AS "DocName",
    d.category AS "Category",
    rc.page_start AS "PageStart",
    rc.page_end AS "PageEnd",
    CASE
        WHEN NULLIF(rc.metadata->>'offsetStart', '') ~ '^[-+]?[0-9]+$'
            THEN CASE
                WHEN (rc.metadata->>'offsetStart')::numeric BETWEEN -2147483648 AND 2147483647
                    THEN (rc.metadata->>'offsetStart')::int
                ELSE NULL
            END
        ELSE NULL
    END AS "OffsetStart",
    CASE
        WHEN NULLIF(rc.metadata->>'offsetEnd', '') ~ '^[-+]?[0-9]+$'
            THEN CASE
                WHEN (rc.metadata->>'offsetEnd')::numeric BETWEEN -2147483648 AND 2147483647
                    THEN (rc.metadata->>'offsetEnd')::int
                ELSE NULL
            END
        ELSE NULL
    END AS "OffsetEnd",
    rc.retrieval_chunk_id AS "ChunkId",
    rc.chunk_index AS "ChunkIndex",
    rc.text_content AS "Text",
    d.indexed_version AS "IngestionVersion",
    LOWER(ENCODE(d.content_hash, 'hex')) AS "HashDoc",
    CASE
        WHEN COALESCE(pcm.card_titles, '') <> ''
            THEN 'Matched profile title: ' || pcm.card_titles || E'\n' || cte.text_content
        ELSE cte.text_content
    END AS "EmbedText",
    rc.section_id AS "SectionOrdinalPlaceholder",
    COALESCE(rc.metadata->>'sectionTitle', s.title) AS "SectionTitle",
    COALESCE(rc.metadata->>'headingPath', s.title) AS "HeadingPath",
    COALESCE(rc.metadata->>'chunkType', 'contextual_text_v1') AS "ChunkType",
    COALESCE(rc.metadata->>'contentRole', 'content') AS "ContentRole",
    rc.metadata->>'navigationReason' AS "NavigationReason",
    rc.metadata->>'originalChunkType' AS "OriginalChunkType",
    CASE
        WHEN NULLIF(rc.metadata->>'navigationScore', '') ~ '^[-+]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?:[eE][-+]?[0-9]+)?$'
            THEN (rc.metadata->>'navigationScore')::double precision
        ELSE NULL
    END AS "NavigationScore",
    CASE
        WHEN NULLIF(rc.metadata->>'contentDensityScore', '') ~ '^[-+]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?:[eE][-+]?[0-9]+)?$'
            THEN (rc.metadata->>'contentDensityScore')::double precision
        ELSE NULL
    END AS "ContentDensityScore",
    rc.metadata->>'prevChunkId' AS "PrevChunkId",
    rc.metadata->>'nextChunkId' AS "NextChunkId",
    rc.metadata->>'sameSectionChunkId' AS "SameSectionChunkId",
    pcm.matched_content_cards_json AS "MatchedContentCardsJson",
    (COALESCE(lm.match_weight, 0.0) + (COALESCE(pcm.match_weight, 0.0) * 1.8))::real AS "SparseRank"
FROM candidate_chunks candidate
JOIN retrieval_chunks rc
  ON rc.retrieval_chunk_id = candidate.retrieval_chunk_id
JOIN scoped_docs d
  ON d.tenant_id = rc.tenant_id
 AND d.revision_id = rc.revision_id
JOIN contextual_text_entries cte
  ON cte.tenant_id = d.tenant_id
 AND cte.revision_id = d.revision_id
 AND cte.retrieval_chunk_id = rc.retrieval_chunk_id
LEFT JOIN document_sections s
  ON s.section_id = rc.section_id
LEFT JOIN ranked_chunk_terms lm
  ON lm.retrieval_chunk_id = rc.retrieval_chunk_id
LEFT JOIN profile_matched_chunks pcm
  ON pcm.retrieval_chunk_id = rc.retrieval_chunk_id
ORDER BY (COALESCE(lm.match_weight, 0.0) + (COALESCE(pcm.match_weight, 0.0) * 1.8)) DESC,
         COALESCE(pcm.match_count, 0) DESC,
         COALESCE(lm.match_count, 0) DESC,
         rc.chunk_index ASC
LIMIT @top_k;
""";

    internal static async Task<List<RagMatch>> SearchDocumentOverviewProfileMatchesAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        string query,
        string? category,
        string? docId,
        string? docPath,
        int topK,
        CancellationToken ct,
        string? categoryPath = null,
        bool requireLexicalMatch = false,
        Action<string, string?>? degradedRetrieverRef = null)
    {
        if (topK <= 0)
            return [];

        var lexicalTerms = BuildDocumentProfileLexicalTerms(query).ToArray();
        var normalizedDocPath = string.IsNullOrWhiteSpace(docPath)
            ? null
            : docPath.Trim().Replace('\\', '/').TrimStart('/');
        var normalizedCategoryPath = NormalizeRagCategoryPathForSql(categoryPath);
        Guid? normalizedDocId = Guid.TryParse(docId, out var parsedDocId) ? parsedDocId : null;
        var resultLimit = ComputeDocumentProfileSearchResultLimit(topK);

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
WITH lexical_terms AS (
    SELECT DISTINCT LOWER(term) AS term
    FROM unnest(@lexical_terms::text[]) AS term
    WHERE term IS NOT NULL AND term <> ''
)
SELECT
    d.doc_id AS "DocId",
    d.doc_path AS "DocPath",
    d.doc_name AS "DocName",
    d.category AS "Category",
    p.document_profile_id AS "ProfileId",
    d.indexed_version AS "IngestionVersion",
    LOWER(ENCODE(d.content_hash, 'hex')) AS "HashDoc",
    COALESCE(NULLIF(s.summary_text, ''), NULLIF(p.summary_text, ''), d.doc_name, d.doc_path) AS "Text",
    effective_profile.search_text AS "SearchText",
    COALESCE(cards.metadata_json, p.metadata::text) AS "MetadataJson",
    p.language AS "Language",
    CASE
        WHEN COALESCE(lm.match_count, 0) > 0
        THEN (0.01::double precision + (COALESCE(lm.match_weight, 0.0)::double precision * 0.05::double precision))
        ELSE 0.0::double precision
    END AS "SparseRank",
    COALESCE(lm.match_count, 0)::bigint AS "MatchCount"
FROM documents d
JOIN document_revisions r
  ON r.tenant_id = d.tenant_id
 AND r.doc_id = d.doc_id
 AND r.indexed_version = d.indexed_version
JOIN LATERAL (
    SELECT profile.*
    FROM document_profiles profile
    WHERE profile.tenant_id = r.tenant_id
      AND profile.revision_id = r.revision_id
    ORDER BY
        CASE profile.profile_version
            WHEN 'llm_backoffice_v1' THEN 0
            WHEN 'deterministic_v1' THEN 1
            ELSE 2
        END,
        profile.updated_at DESC
    LIMIT 1
) p ON TRUE
LEFT JOIN document_summaries s
  ON s.tenant_id = d.tenant_id
 AND s.doc_id = d.doc_id
 AND s.level = 'medium'
 AND s.source_hash = saaia_document_summary_source_hash(d.content_hash, d.doc_path, d.file_size, d.file_mtime, d.indexed_version)
LEFT JOIN LATERAL (
    SELECT
        CASE
            WHEN COUNT(*) = 0 THEN NULL
            ELSE jsonb_build_object(
                'contentCards',
                jsonb_agg(
                    jsonb_build_object(
                        'title', card.title,
                        'contentCardId', card.content_card_id,
                        'pageStart', card.page_start,
                        'pageEnd', card.page_end,
                        'kind', card.kind,
                        'signals', card.signals,
                        'evidence', card.metadata->'evidence')
                    ORDER BY card.card_index))::text
        END AS metadata_json,
        NULLIF(TRIM(BOTH FROM STRING_AGG(card.search_text, ' ' ORDER BY card.card_index)), '') AS search_text
    FROM (
        SELECT DISTINCT ON (
            raw_card.normalized_title,
            GREATEST(1, COALESCE(raw_card.page_start, 1)),
            GREATEST(
                GREATEST(1, COALESCE(raw_card.page_start, 1)),
                COALESCE(raw_card.page_end, GREATEST(1, COALESCE(raw_card.page_start, 1)))
            ))
            raw_card.*
        FROM document_profile_content_cards raw_card
        WHERE raw_card.tenant_id = r.tenant_id
          AND raw_card.revision_id = r.revision_id
        ORDER BY
            raw_card.normalized_title,
            GREATEST(1, COALESCE(raw_card.page_start, 1)),
            GREATEST(
                GREATEST(1, COALESCE(raw_card.page_start, 1)),
                COALESCE(raw_card.page_end, GREATEST(1, COALESCE(raw_card.page_start, 1)))
            ),
            CASE raw_card.profile_version
                WHEN 'llm_backoffice_v1' THEN 0
                WHEN 'deterministic_v1' THEN 1
                ELSE 2
            END,
            raw_card.updated_at DESC,
            raw_card.card_index ASC
        LIMIT 80
    ) card
) cards ON TRUE
CROSS JOIN LATERAL (
    SELECT TRIM(BOTH FROM CONCAT_WS(
        ' ',
        d.doc_path,
        d.doc_name,
        p.summary_text,
        ARRAY_TO_STRING(p.keywords, ' '),
        ARRAY_TO_STRING(p.entities, ' '),
        ARRAY_TO_STRING(p.topics, ' '),
        ARRAY_TO_STRING(p.hypothetical_questions, ' '),
        NULLIF(s.summary_text, ''),
        cards.search_text)) AS search_text
) effective_profile
LEFT JOIN LATERAL (
    SELECT
        COUNT(*) AS match_count,
        COALESCE(SUM(
            CASE
                WHEN lexical_terms.term LIKE '% %' AND length(lexical_terms.term) >= 18 THEN 7.0
                WHEN lexical_terms.term LIKE '% %' THEN 5.0
                WHEN length(lexical_terms.term) >= 10 THEN 2.5
                WHEN length(lexical_terms.term) >= 7 THEN 1.6
                ELSE 1.0
            END), 0.0) AS match_weight
    FROM lexical_terms
    WHERE LOWER(effective_profile.search_text) LIKE '%' || lexical_terms.term || '%'
) lm ON TRUE
WHERE d.tenant_id = @tenant_id
  AND d.status = 'indexed'
  AND d.indexed_version > 0
  AND (@category IS NULL OR LOWER(d.category) = @category)
  AND (@category_path IS NULL OR d.doc_path = @category_path OR d.doc_path LIKE (@category_path || '/%'))
  AND (@doc_id IS NULL OR d.doc_id = @doc_id)
  AND (@doc_path IS NULL OR d.doc_path = @doc_path)
  AND (NOT @require_lexical_match OR COALESCE(lm.match_count, 0) > 0)
ORDER BY
    CASE WHEN COALESCE(lm.match_count, 0) > 0 THEN 0 ELSE 1 END,
    COALESCE(lm.match_weight, 0.0) DESC,
    d.doc_path ASC
LIMIT @result_limit;
""";

        try
        {
            var rows = (await conn.QueryAsync<DocumentProfileMatchRow>(new CommandDefinition(sql, new
            {
                tenant_id = tenantId,
                lexical_terms = lexicalTerms,
                category,
                category_path = normalizedCategoryPath,
                doc_id = normalizedDocId,
                doc_path = normalizedDocPath,
                result_limit = resultLimit,
                require_lexical_match = requireLexicalMatch
            }, cancellationToken: ct))).ToList();

            return RankDocumentProfileRows(query, rows)
                .Take(topK)
                .Select(scored =>
            {
                var row = scored.Row;
                var pageRange = ResolveDocumentProfileMatchPageRange(row.MetadataJson, query);
                var matchedContentCards = BuildMatchedContentCards(row.MetadataJson, query);
                var profileSectionTitle = BuildDocumentProfileSectionTitle(row.Language);
                return new RagMatch(
                    Score: scored.Score,
                    DocId: row.DocId.ToString(),
                    DocPath: row.DocPath,
                    DocName: row.DocName,
                    Category: row.Category,
                    PageStart: pageRange.PageStart,
                    PageEnd: pageRange.PageEnd,
                    ChunkId: row.ProfileId.ToString(),
                    ChunkIndex: -1,
                    Text: BuildDocumentProfileMatchText(row.Text, row.MetadataJson, query, row.Language),
                    IngestionVersion: row.IngestionVersion,
                    HashDoc: row.HashDoc,
                    EmbedText: row.SearchText,
                    EmbeddingBasis: "document_profile_v1",
                    SectionOrdinal: null,
                    UnitOrdinal: null,
                    SectionTitle: profileSectionTitle,
                    HeadingPath: profileSectionTitle,
                    ChunkType: "document_profile",
                    PrevChunkId: null,
                    NextChunkId: null,
                    SameSectionChunkId: null,
                    MatchedContentCards: matchedContentCards.Count == 0 ? null : matchedContentCards);
            }).ToList();
        }
        catch (PostgresException ex)
        {
            RetrievalTelemetry.RecordRetrieverDegraded("document_profile_overview_v1", ex);
            degradedRetrieverRef?.Invoke("document_profile_overview_v1", FormatPostgresRetrieverError(ex));
            return [];
        }
    }

    internal static async Task<List<RagMatch>> SearchDocumentProfileMatchesAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        string query,
        string? category,
        string? docId,
        string? docPath,
        int topK,
        CancellationToken ct,
        string? categoryPath = null,
        Action<string, string?>? degradedRetrieverRef = null)
    {
        if (string.IsNullOrWhiteSpace(query) || topK <= 0)
            return [];

        var lexicalTerms = BuildDocumentProfileLexicalTerms(query);
        if (lexicalTerms.Count == 0)
            return [];

        var normalizedDocPath = string.IsNullOrWhiteSpace(docPath)
            ? null
            : docPath.Trim().Replace('\\', '/').TrimStart('/');
        var normalizedCategoryPath = NormalizeRagCategoryPathForSql(categoryPath);
        Guid? normalizedDocId = Guid.TryParse(docId, out var parsedDocId) ? parsedDocId : null;
        var resultLimit = ComputeDocumentProfileSearchResultLimit(topK);

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
WITH sparse_query AS (
    SELECT websearch_to_tsquery('simple', @query_text) AS q
),
lexical_terms AS (
    SELECT DISTINCT LOWER(term) AS term
    FROM unnest(@lexical_terms::text[]) AS term
    WHERE term IS NOT NULL AND term <> ''
)
SELECT
    d.doc_id AS "DocId",
    d.doc_path AS "DocPath",
    d.doc_name AS "DocName",
    d.category AS "Category",
    p.document_profile_id AS "ProfileId",
    d.indexed_version AS "IngestionVersion",
    LOWER(ENCODE(d.content_hash, 'hex')) AS "HashDoc",
    effective_profile.summary_text AS "Text",
    effective_profile.search_text AS "SearchText",
    COALESCE(cards.metadata_json, p.metadata::text) AS "MetadataJson",
    p.language AS "Language",
    ts_rank_cd(
        to_tsvector('simple', effective_profile.search_text),
        sparse_query.q,
        32
    ) + ((lm.match_weight + COALESCE(cm.match_weight, 0.0) + (COALESCE(hm.match_weight, 0.0) * 1.2))::real * 0.04) AS "SparseRank",
    (lm.match_count + COALESCE(cm.match_count, 0) + COALESCE(hm.match_count, 0)) AS "MatchCount"
FROM sparse_query
JOIN documents d
  ON d.tenant_id = @tenant_id
 AND d.status = 'indexed'
 AND d.indexed_version > 0
JOIN document_revisions r
  ON r.tenant_id = d.tenant_id
 AND r.doc_id = d.doc_id
 AND r.indexed_version = d.indexed_version
LEFT JOIN document_summaries s
  ON s.tenant_id = d.tenant_id
 AND s.doc_id = d.doc_id
 AND s.level = 'medium'
 AND s.source_hash = saaia_document_summary_source_hash(d.content_hash, d.doc_path, d.file_size, d.file_mtime, d.indexed_version)
JOIN LATERAL (
    SELECT profile.*
    FROM document_profiles profile
    WHERE profile.tenant_id = r.tenant_id
      AND profile.revision_id = r.revision_id
    ORDER BY
        CASE profile.profile_version
            WHEN 'llm_backoffice_v1' THEN 0
            WHEN 'deterministic_v1' THEN 1
            ELSE 2
        END,
        profile.updated_at DESC
    LIMIT 1
) p ON TRUE
LEFT JOIN LATERAL (
    SELECT
        NULLIF(TRIM(BOTH FROM STRING_AGG(NULLIF(profile.summary_text, ''), ' ' ORDER BY
            CASE profile.profile_version
                WHEN 'llm_backoffice_v1' THEN 0
                WHEN 'deterministic_v1' THEN 1
                ELSE 2
            END,
            profile.updated_at DESC)), '') AS summary_text,
        NULLIF(TRIM(BOTH FROM STRING_AGG(NULLIF(profile.search_text, ''), ' ' ORDER BY
            CASE profile.profile_version
                WHEN 'llm_backoffice_v1' THEN 0
                WHEN 'deterministic_v1' THEN 1
                ELSE 2
            END,
            profile.updated_at DESC)), '') AS search_text,
        NULLIF(TRIM(BOTH FROM STRING_AGG(NULLIF(ARRAY_TO_STRING(profile.keywords, ' '), ''), ' ')), '') AS keywords_text,
        NULLIF(TRIM(BOTH FROM STRING_AGG(NULLIF(ARRAY_TO_STRING(profile.entities, ' '), ''), ' ')), '') AS entities_text,
        NULLIF(TRIM(BOTH FROM STRING_AGG(NULLIF(ARRAY_TO_STRING(profile.topics, ' '), ''), ' ')), '') AS topics_text,
        NULLIF(TRIM(BOTH FROM STRING_AGG(NULLIF(ARRAY_TO_STRING(profile.hypothetical_questions, ' '), ''), ' ')), '') AS hypothetical_questions_text
    FROM document_profiles profile
    WHERE profile.tenant_id = r.tenant_id
      AND profile.revision_id = r.revision_id
) profile_terms ON TRUE
LEFT JOIN LATERAL (
    SELECT CASE
        WHEN COUNT(*) = 0 THEN NULL
        ELSE jsonb_build_object(
            'contentCards',
            jsonb_agg(
                jsonb_build_object(
                    'title', card.title,
                    'contentCardId', card.content_card_id,
                    'pageStart', card.page_start,
                    'pageEnd', card.page_end,
                    'kind', card.kind,
                    'signals', card.signals,
                    'evidence', card.metadata->'evidence')
                ORDER BY card.card_index))::text
        END AS metadata_json,
        NULLIF(TRIM(BOTH FROM STRING_AGG(card.search_text, ' ' ORDER BY card.card_index)), '') AS search_text
    FROM (
        SELECT DISTINCT ON (
            raw_card.normalized_title,
            GREATEST(1, COALESCE(raw_card.page_start, 1)),
            GREATEST(
                GREATEST(1, COALESCE(raw_card.page_start, 1)),
                COALESCE(raw_card.page_end, GREATEST(1, COALESCE(raw_card.page_start, 1)))
            ))
            raw_card.*
        FROM document_profile_content_cards raw_card
        WHERE raw_card.tenant_id = r.tenant_id
          AND raw_card.revision_id = r.revision_id
        ORDER BY
            raw_card.normalized_title,
            GREATEST(1, COALESCE(raw_card.page_start, 1)),
            GREATEST(
                GREATEST(1, COALESCE(raw_card.page_start, 1)),
                COALESCE(raw_card.page_end, GREATEST(1, COALESCE(raw_card.page_start, 1)))
            ),
            CASE raw_card.profile_version
                WHEN 'llm_backoffice_v1' THEN 0
                WHEN 'deterministic_v1' THEN 1
                ELSE 2
            END,
            raw_card.updated_at DESC,
            raw_card.card_index ASC
    ) card
) cards ON TRUE
CROSS JOIN LATERAL (
    SELECT
        COALESCE(NULLIF(s.summary_text, ''), p.summary_text) AS summary_text,
        TRIM(BOTH FROM CONCAT_WS(
            ' ',
            d.doc_path,
            d.doc_name,
            p.summary_text,
            profile_terms.summary_text,
            profile_terms.search_text,
            profile_terms.keywords_text,
            profile_terms.entities_text,
            profile_terms.topics_text,
            profile_terms.hypothetical_questions_text,
            NULLIF(s.summary_text, ''),
            ARRAY_TO_STRING(p.keywords, ' '),
            ARRAY_TO_STRING(p.entities, ' '),
            ARRAY_TO_STRING(p.topics, ' '),
            ARRAY_TO_STRING(p.hypothetical_questions, ' '),
            cards.search_text)) AS search_text
) effective_profile
LEFT JOIN LATERAL (
    SELECT
        COUNT(*) AS match_count,
        COALESCE(SUM(
            CASE
                WHEN lexical_terms.term LIKE '% %' AND length(lexical_terms.term) >= 18 THEN 7.0
                WHEN lexical_terms.term LIKE '% %' THEN 5.0
                WHEN length(lexical_terms.term) >= 10 THEN 2.5
                WHEN length(lexical_terms.term) >= 7 THEN 1.6
                ELSE 1.0
            END), 0.0) AS match_weight
    FROM (
        SELECT DISTINCT ON (
            raw_card.normalized_title,
            GREATEST(1, COALESCE(raw_card.page_start, 1)),
            GREATEST(
                GREATEST(1, COALESCE(raw_card.page_start, 1)),
                COALESCE(raw_card.page_end, GREATEST(1, COALESCE(raw_card.page_start, 1)))
            ))
            raw_card.*
        FROM document_profile_content_cards raw_card
        WHERE raw_card.tenant_id = r.tenant_id
          AND raw_card.revision_id = r.revision_id
        ORDER BY
            raw_card.normalized_title,
            GREATEST(1, COALESCE(raw_card.page_start, 1)),
            GREATEST(
                GREATEST(1, COALESCE(raw_card.page_start, 1)),
                COALESCE(raw_card.page_end, GREATEST(1, COALESCE(raw_card.page_start, 1)))
            ),
            CASE raw_card.profile_version
                WHEN 'llm_backoffice_v1' THEN 0
                WHEN 'deterministic_v1' THEN 1
                ELSE 2
            END,
            raw_card.updated_at DESC,
            raw_card.card_index ASC
    ) card
    CROSS JOIN lexical_terms
    WHERE CASE
        WHEN lexical_terms.term LIKE '% %'
            THEN LOWER(card.search_text) LIKE '%' || REPLACE(lexical_terms.term, ' ', '%') || '%'
                 OR card.normalized_title LIKE '%' || REPLACE(lexical_terms.term, ' ', '%') || '%'
        ELSE LOWER(card.search_text) LIKE '%' || lexical_terms.term || '%'
             OR card.normalized_title LIKE '%' || lexical_terms.term || '%'
      END
) cm ON TRUE
LEFT JOIN LATERAL (
    SELECT
        COUNT(*) AS match_count,
        COALESCE(SUM(
            CASE
                WHEN lexical_terms.term LIKE '% %' AND length(lexical_terms.term) >= 18 THEN 7.0
                WHEN lexical_terms.term LIKE '% %' THEN 5.0
                WHEN length(lexical_terms.term) >= 10 THEN 2.5
                WHEN length(lexical_terms.term) >= 7 THEN 1.6
                ELSE 1.0
            END), 0.0) AS match_weight
    FROM document_profiles question_profile
    CROSS JOIN LATERAL unnest(COALESCE(question_profile.hypothetical_questions, ARRAY[]::text[])) AS question(text)
    CROSS JOIN lexical_terms
    WHERE question_profile.tenant_id = r.tenant_id
      AND question_profile.revision_id = r.revision_id
      AND CASE
          WHEN lexical_terms.term LIKE '% %'
              THEN LOWER(question.text) LIKE '%' || REPLACE(lexical_terms.term, ' ', '%') || '%'
          ELSE LOWER(question.text) LIKE '%' || lexical_terms.term || '%'
      END
) hm ON TRUE
CROSS JOIN LATERAL (
    SELECT
        COUNT(*) AS match_count,
        COALESCE(SUM(
            CASE
                WHEN lexical_terms.term LIKE '% %' AND length(lexical_terms.term) >= 18 THEN 7.0
                WHEN lexical_terms.term LIKE '% %' THEN 5.0
                WHEN length(lexical_terms.term) >= 10 THEN 2.5
                WHEN length(lexical_terms.term) >= 7 THEN 1.6
                ELSE 1.0
            END), 0.0) AS match_weight
    FROM lexical_terms
    WHERE CASE
        WHEN lexical_terms.term LIKE '% %'
            THEN LOWER(effective_profile.search_text) LIKE '%' || REPLACE(lexical_terms.term, ' ', '%') || '%'
        ELSE LOWER(effective_profile.search_text) LIKE '%' || lexical_terms.term || '%'
    END
) lm
WHERE (
       to_tsvector('simple', effective_profile.search_text) @@ sparse_query.q
       OR lm.match_count > 0
       OR COALESCE(cm.match_count, 0) > 0
       OR COALESCE(hm.match_count, 0) > 0
      )
  AND (@category IS NULL OR LOWER(d.category) = @category)
  AND (@category_path IS NULL OR d.doc_path = @category_path OR d.doc_path LIKE (@category_path || '/%'))
  AND (@doc_id IS NULL OR d.doc_id = @doc_id)
  AND (@doc_path IS NULL OR d.doc_path = @doc_path)
ORDER BY
    (ts_rank_cd(to_tsvector('simple', effective_profile.search_text), sparse_query.q, 32)
        + ((lm.match_weight + COALESCE(cm.match_weight, 0.0) + (COALESCE(hm.match_weight, 0.0) * 1.2))::real * 0.04)) DESC,
    d.updated_at DESC
LIMIT @result_limit;
""";

        try
        {
            var rows = (await conn.QueryAsync<DocumentProfileMatchRow>(new CommandDefinition(sql, new
            {
                tenant_id = tenantId,
                query_text = query.Trim(),
                lexical_terms = lexicalTerms.ToArray(),
                category,
                category_path = normalizedCategoryPath,
                doc_id = normalizedDocId,
                doc_path = normalizedDocPath,
                result_limit = resultLimit
            }, cancellationToken: ct))).ToList();

            return RankDocumentProfileRows(query, rows)
                .Take(topK)
                .Select(scored =>
            {
                var row = scored.Row;
                var pageRange = ResolveDocumentProfileMatchPageRange(row.MetadataJson, query);
                var matchedContentCards = BuildMatchedContentCards(row.MetadataJson, query);
                var profileSectionTitle = BuildDocumentProfileSectionTitle(row.Language);
                return new RagMatch(
                    Score: scored.Score,
                    DocId: row.DocId.ToString(),
                    DocPath: row.DocPath,
                    DocName: row.DocName,
                    Category: row.Category,
                    PageStart: pageRange.PageStart,
                    PageEnd: pageRange.PageEnd,
                    ChunkId: row.ProfileId.ToString(),
                    ChunkIndex: -1,
                    Text: BuildDocumentProfileMatchText(row.Text, row.MetadataJson, query, row.Language),
                    IngestionVersion: row.IngestionVersion,
                    HashDoc: row.HashDoc,
                    EmbedText: row.SearchText,
                    EmbeddingBasis: "document_profile_v1",
                    SectionOrdinal: null,
                    UnitOrdinal: null,
                    SectionTitle: profileSectionTitle,
                    HeadingPath: profileSectionTitle,
                    ChunkType: "document_profile",
                    PrevChunkId: null,
                    NextChunkId: null,
                    SameSectionChunkId: null,
                    MatchedContentCards: matchedContentCards.Count == 0 ? null : matchedContentCards);
            }).ToList();
        }
        catch (PostgresException ex)
        {
            RetrievalTelemetry.RecordRetrieverDegraded("document_profile_v1", ex);
            degradedRetrieverRef?.Invoke("document_profile_v1", FormatPostgresRetrieverError(ex));
            return [];
        }
    }

    internal static int ComputeDocumentProfileSearchResultLimit(int topK)
    {
        if (topK <= 0)
            return 0;

        return Math.Clamp(Math.Max(topK + 8, topK * 4), topK, 80);
    }

    private static IReadOnlyList<ScoredDocumentProfileMatchRow> RankDocumentProfileRows(
        string query,
        IReadOnlyList<DocumentProfileMatchRow> rows)
    {
        if (rows.Count == 0)
            return [];

        var peerTexts = rows
            .Select(static row => BuildDocumentProfileCandidateLookupText(row))
            .ToArray();

        return rows
            .Select((row, index) =>
            {
                var baseScore = NormalizeDocumentProfileScore(row.SparseRank, (int)Math.Min(row.MatchCount, int.MaxValue));
                var specificityBoost = ComputeDocumentProfileSpecificityBoost(query, peerTexts[index], peerTexts);
                var score = Math.Clamp(baseScore + specificityBoost, 0.0, 0.92);
                return new ScoredDocumentProfileMatchRow(row, score, specificityBoost, baseScore);
            })
            .OrderByDescending(static item => item.Score)
            .ThenByDescending(static item => item.SpecificityBoost)
            .ThenByDescending(static item => item.BaseScore)
            .ThenBy(static item => item.Row.DocName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string BuildDocumentProfileCandidateLookupText(DocumentProfileMatchRow row)
        => CollapseWhitespace(string.Join(' ', new[]
        {
            row.DocPath,
            row.DocName,
            row.Text,
            row.SearchText,
            row.MetadataJson
        }));

    private static string FormatPostgresRetrieverError(PostgresException ex)
        => string.IsNullOrWhiteSpace(ex.MessageText)
            ? ex.SqlState
            : $"{ex.SqlState}: {ex.MessageText}";

    private static List<SparseMatchRow> MergeSparseRows(
        IReadOnlyList<SparseMatchRow> preferred,
        IReadOnlyList<SparseMatchRow> secondary,
        int topK)
    {
        var merged = new List<SparseMatchRow>(Math.Max(0, topK));
        var seen = new HashSet<Guid>();

        foreach (var row in preferred.Concat(secondary))
        {
            if (!seen.Add(row.ChunkId))
                continue;

            merged.Add(row);
            if (merged.Count >= topK)
                break;
        }

        return merged;
    }

    private static string BuildDocumentProfileMatchText(string summaryText, string? metadataJson, string query, string? language)
    {
        var summary = string.IsNullOrWhiteSpace(summaryText)
            ? string.Empty
            : summaryText.Trim();
        var contentCards = SelectDocumentProfileContentCards(metadataJson, query);
        if (contentCards.Count == 0)
            return summary;

        var rankedCards = contentCards
            .Select(FormatDocumentProfileContentCard)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        if (rankedCards.Length == 0)
            return summary;

        var cardsText = $"{BuildContentCuesLabel(language)}: {string.Join("; ", rankedCards)}.";
        return string.IsNullOrWhiteSpace(summary)
            ? cardsText
            : $"{summary} {cardsText}";
    }

    internal static IReadOnlyList<RagMatchedContentCard> BuildMatchedContentCards(
        string? metadataJson,
        string query,
        int limit = 12,
        int? pageStart = null,
        int? pageEnd = null)
        => SelectDocumentProfileContentCards(metadataJson, query, limit, pageStart, pageEnd)
            .Select(static card => new RagMatchedContentCard(
                Title: card.Title,
                ContentCardId: card.ContentCardId,
                PageStart: card.PageStart,
                PageEnd: card.PageEnd,
                Kind: card.Kind,
                Signals: card.Signals,
                Evidence: BuildContentCardEvidenceJson(card.Evidence)))
            .ToArray();

    private static IReadOnlyList<RagMatchedContentCard>? BuildSparseMatchedContentCards(SparseMatchRow row, string query)
    {
        var cards = BuildMatchedContentCards(row.MatchedContentCardsJson, query, pageStart: row.PageStart, pageEnd: row.PageEnd);
        return cards.Count == 0 ? null : cards;
    }

    private static JsonElement? BuildContentCardEvidenceJson(DocumentProfileCardEvidence? evidence)
        => evidence is null ? null : JsonSerializer.SerializeToElement(evidence);

    private static IReadOnlyList<DocumentProfileContentCard> SelectDocumentProfileContentCards(
        string? metadataJson,
        string query,
        int limit = 12,
        int? pageStart = null,
        int? pageEnd = null)
    {
        var contentCards = DocumentProfileProjector.ParseContentCards(metadataJson);
        if (contentCards.Count == 0 || limit <= 0)
            return [];

        var queryTokens = ExtractLexicalQueryTokens(query);
        if (queryTokens.Count == 0)
            return [];

        return contentCards
            .Where(card => ContentCardOverlapsMatchPageRange(card, pageStart, pageEnd))
            .Select(card => new
            {
                Card = card,
                Score = ComputeDocumentProfileContentCardQueryScore(query, queryTokens, card),
                Coverage = ComputeLexicalCoverage(queryTokens, BuildDocumentProfileContentCardQueryLookupText(card)),
                TitleCoverage = ComputeLexicalCoverage(queryTokens, card.Title)
            })
            .Where(static item => item.Score > 0.0)
            .OrderByDescending(static item => item.Score)
            .ThenByDescending(static item => item.TitleCoverage)
            .ThenByDescending(static item => item.Coverage)
            .ThenBy(static item => item.Card.PageStart ?? int.MaxValue)
            .ThenBy(static item => item.Card.Title, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(static item => item.Card)
            .ToArray();
    }

    private static bool ContentCardOverlapsMatchPageRange(DocumentProfileContentCard card, int? pageStart, int? pageEnd)
    {
        if (pageStart is null or <= 0)
            return true;

        var matchStart = pageStart.Value;
        var matchEnd = pageEnd is > 0 ? Math.Max(pageEnd.Value, matchStart) : matchStart;

        if (card.PageStart is null or <= 0)
            return false;

        var cardStart = card.PageStart.Value;
        var cardEnd = card.PageEnd is > 0 ? Math.Max(card.PageEnd.Value, cardStart) : cardStart;
        return cardStart <= matchEnd && cardEnd >= matchStart;
    }

    private static double ComputeDocumentProfileContentCardQueryScore(
        string query,
        IReadOnlyList<string> queryTokens,
        DocumentProfileContentCard card)
    {
        if (queryTokens.Count == 0 || string.IsNullOrWhiteSpace(card.Title))
            return 0.0;

        var title = card.Title.Trim();
        var signals = string.Join(' ', card.Signals ?? Array.Empty<string>());
        var evidenceText = BuildDocumentProfileContentCardEvidenceLookupText(card.Evidence);
        var candidateText = BuildDocumentProfileContentCardQueryLookupText(card);
        var coverage = ComputeLexicalCoverage(queryTokens, candidateText);
        var titleCoverage = ComputeLexicalCoverage(queryTokens, title);
        var signalCoverage = ComputeLexicalCoverage(queryTokens, signals);
        var evidenceCoverage = ComputeLexicalCoverage(queryTokens, evidenceText);
        if (coverage <= 0.0 && titleCoverage <= 0.0 && signalCoverage <= 0.0 && evidenceCoverage <= 0.0)
            return 0.0;

        var score = (coverage * 100.0) + (titleCoverage * 70.0) + (signalCoverage * 18.0) + (evidenceCoverage * 42.0);
        var normalizedQuery = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query));
        var normalizedTitle = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(title));
        if (normalizedTitle.Length >= 4 && normalizedQuery.Length >= 4)
        {
            if (normalizedQuery.Contains(normalizedTitle, StringComparison.Ordinal))
                score += 90.0 + Math.Min(35.0, normalizedTitle.Length / 2.0);
            else if (normalizedTitle.Contains(normalizedQuery, StringComparison.Ordinal) && normalizedQuery.Length >= 8)
                score += 45.0;
        }

        var phraseTerms = BuildLexicalContentFallbackTerms(query)
            .Where(static term => term.Contains(' '))
            .Take(24)
            .ToArray();
        if (phraseTerms.Any(term => ContainsOrderedPhraseWindow(title, term, maxGapChars: 30)))
            score += 35.0;
        else if (phraseTerms.Any(term => ContainsOrderedPhraseWindow(candidateText, term, maxGapChars: 45)))
            score += 18.0;
        else if (!string.IsNullOrWhiteSpace(evidenceText)
                 && phraseTerms.Any(term => ContainsOrderedPhraseWindow(evidenceText, term, maxGapChars: 45)))
        {
            score += 24.0;
        }

        var titleAnchorCount = CountSpecificLexicalAnchors(queryTokens, title);
        if (titleAnchorCount > 0)
            score += Math.Min(36.0, titleAnchorCount * 9.0);

        if (titleCoverage <= 0.0 && signalCoverage > 0.0)
            score *= 0.80;
        if (titleCoverage <= 0.0 && signalCoverage <= 0.0 && evidenceCoverage > 0.0)
            score *= 0.95;

        return score;
    }

    private static string BuildDocumentProfileContentCardQueryLookupText(DocumentProfileContentCard card)
        => CollapseWhitespace(string.Join(' ', new[]
        {
            card.Title,
            string.Join(' ', card.Signals ?? Array.Empty<string>()),
            BuildDocumentProfileContentCardEvidenceLookupText(card.Evidence)
        }));

    private static string BuildDocumentProfileContentCardEvidenceLookupText(DocumentProfileCardEvidence? evidence)
    {
        if (evidence is null)
            return string.Empty;

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(evidence.SchemaVersion))
            parts.Add(evidence.SchemaVersion);
        if (evidence.ScaleBasis is not null)
        {
            if (!string.IsNullOrWhiteSpace(evidence.ScaleBasis.Label))
                parts.Add(evidence.ScaleBasis.Label!);
            if (evidence.ScaleBasis.Count > 0)
                parts.Add(evidence.ScaleBasis.Count.ToString(CultureInfo.InvariantCulture));
        }

        foreach (var fact in evidence.QuantityFacts ?? [])
        {
            if (!string.IsNullOrWhiteSpace(fact.Label))
                parts.Add(fact.Label);
            if (!string.IsNullOrWhiteSpace(fact.Unit))
                parts.Add(fact.Unit);
            if (!string.IsNullOrWhiteSpace(fact.SourceText))
                parts.Add(fact.SourceText);
            if (fact.Value > 0)
                parts.Add(fact.Value.ToString("0.###", CultureInfo.InvariantCulture));
        }

        foreach (var reason in evidence.NonScalableReasons ?? [])
        {
            if (!string.IsNullOrWhiteSpace(reason))
                parts.Add(reason);
        }

        if (!string.IsNullOrWhiteSpace(evidence.Language))
            parts.Add($"language:{evidence.Language}");
        if ((evidence.Facts ?? []).Count > 0)
            parts.Add("structured_facts");
        foreach (var fact in evidence.Facts ?? [])
        {
            if (!string.IsNullOrWhiteSpace(fact.Kind))
                parts.Add(fact.Kind);
            if (!string.IsNullOrWhiteSpace(fact.Label))
                parts.Add(fact.Label);
            if (!string.IsNullOrWhiteSpace(fact.Value))
                parts.Add(fact.Value);
            if (!string.IsNullOrWhiteSpace(fact.Unit))
                parts.Add(fact.Unit);
            if (!string.IsNullOrWhiteSpace(fact.SourceText))
                parts.Add(fact.SourceText);
        }

        return CollapseWhitespace(string.Join(' ', parts));
    }

    private static string CollapseWhitespace(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : Regex.Replace(value.Trim(), @"\s+", " ", RegexOptions.CultureInvariant);

    private static (int? PageStart, int? PageEnd) ResolveDocumentProfileMatchPageRange(string? metadataJson, string query)
    {
        if (ExtractLexicalQueryTokens(query).Count == 0)
            return (null, null);

        var contentCards = SelectDocumentProfileContentCards(metadataJson, query);
        if (contentCards.Count == 0)
            return (null, null);

        var selected = contentCards
            .Where(static card => card.PageStart is > 0)
            .FirstOrDefault();

        return selected is null
            ? (null, null)
            : (selected.PageStart, selected.PageEnd ?? selected.PageStart);
    }

    private static string BuildContentCuesLabel(string? language)
        => DocumentLanguageResolver.PrimarySubtag(language) switch
        {
            "en" => "Content cues",
            "fr" => "Repères de contenu",
            "es" => "Pistas de contenido",
            "pt" => "Pistas de conteúdo",
            "de" => "Inhaltshinweise",
            "it" => "Indizi di contenuto",
            _ => "Content cues"
        };

    private static string BuildDocumentProfileSectionTitle(string? language)
        => DocumentLanguageResolver.PrimarySubtag(language) switch
        {
            "fr" => "Profil documentaire",
            "es" => "Perfil documental",
            "pt" => "Perfil documental",
            "de" => "Dokumentprofil",
            "it" => "Profilo documentale",
            _ => "Profile summary"
        };

    private static string FormatDocumentProfileContentCard(DocumentProfileContentCard card)
    {
        var title = card.Title.Trim();
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        return card.PageStart is > 0
            ? $"{title} (p.{card.PageStart})"
            : title;
    }

    private static IReadOnlyList<RagItemContentCardDto>? BuildMatchedContentCardDtos(RagMatch match)
    {
        var cards = match.MatchedContentCards;
        if (cards is null || cards.Count == 0)
            return null;

        return cards
            .Where(static card => !string.IsNullOrWhiteSpace(card.Title))
            .Select(static card => new RagItemContentCardDto(
                Title: card.Title.Trim(),
                ContentCardId: card.ContentCardId,
                PageStart: card.PageStart,
                PageEnd: card.PageEnd,
                Kind: card.Kind,
                Signals: card.Signals,
                Evidence: card.Evidence))
            .ToArray();
    }

    internal static RagItemSelectionHintsDto BuildSelectionHints(
        RagMatch match,
        RagItemExtractionQualityDto? quality = null)
    {
        var retriever = ResolveRetriever(match);
        var actionabilityScore = 0;
        var supportScore = 0;
        var fragmentScore = 0;
        var navigationScore = 0;
        var qualityPenalty = ComputeSelectionQualityPenalty(quality);

        if (LooksLikeStructuredAnswerUnit(match))
            actionabilityScore += 12;
        else if (LooksLikeStructuredAnswerChunk(match))
            actionabilityScore += 8;

        if (match.MatchedContentCards is { Count: > 0 })
        {
            actionabilityScore += match.MatchedContentCards.Any(static card =>
                string.Equals(card.Kind, "unit_lead", StringComparison.OrdinalIgnoreCase)
                || string.Equals(card.Kind, "exact_lead", StringComparison.OrdinalIgnoreCase))
                ? 5
                : 3;
            supportScore += 2;
        }

        if (string.Equals(retriever, "exact_match", StringComparison.Ordinal))
            actionabilityScore += 5;
        if (string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal))
            actionabilityScore += 2;
        if (string.Equals(retriever, "document_profile", StringComparison.Ordinal))
            supportScore += 6;
        if (string.Equals(retriever, "linked_context", StringComparison.Ordinal))
            fragmentScore += 6;
        if (string.Equals(retriever, "dense_qdrant", StringComparison.Ordinal))
            supportScore += 2;

        if (LooksLikeNavigationalChunk(match))
            navigationScore += ComputeNavigationHintScore(match);
        if (LooksLikeGlossaryChunk(match))
            navigationScore += 6;
        if (LooksLikeSourceListChunk(match))
            navigationScore += 8;
        if (LooksLikeDocumentOverviewChunk(match))
            supportScore += 4;

        var textLength = (match.Text ?? string.Empty).Trim().Length;
        if (textLength is > 0 and < 140 && !LooksLikeStructuredAnswerChunk(match))
            fragmentScore += 3;

        var evidenceRole =
            qualityPenalty >= 10 ? "low_confidence" :
            navigationScore >= Math.Max(7, actionabilityScore + 2) ? "navigation" :
            fragmentScore >= Math.Max(8, actionabilityScore + 3) ? "fragment" :
            actionabilityScore >= Math.Max(8, supportScore + 1) ? "actionable_item" :
            supportScore >= 5 ? "supporting_context" :
            "advisory";

        return new RagItemSelectionHintsDto(
            EvidenceRole: evidenceRole,
            ActionabilityScore: actionabilityScore,
            SupportScore: supportScore,
            FragmentScore: fragmentScore,
            NavigationScore: navigationScore,
            QualityPenalty: qualityPenalty);
    }

    private static int ComputeSelectionQualityPenalty(RagItemExtractionQualityDto? quality)
    {
        if (quality is null)
            return 0;

        var penalty = 0;
        if (quality.DocumentManualReviewRecommended == true)
            penalty += 4;
        if (quality.PageManualReviewRecommended == true)
            penalty += 6;
        if (quality.OcrRecommended == true && quality.OcrApplied != true)
            penalty += 5;

        var confidence = quality.PageExtractionConfidence ?? quality.DocumentExtractionConfidence;
        if (confidence is <= 0.35)
            penalty += 8;
        else if (confidence is <= 0.50)
            penalty += 5;
        else if (confidence is <= 0.70)
            penalty += 2;

        var status = string.Join(' ', quality.PageQualityStatus, quality.DocumentQualityStatus, quality.TextStatus)
            .ToLowerInvariant();
        if (status.Contains("ocr_failed", StringComparison.Ordinal)
            || status.Contains("low_confidence", StringComparison.Ordinal))
        {
            penalty += 6;
        }
        if (status.Contains("manual_review", StringComparison.Ordinal)
            || status.Contains("low_text", StringComparison.Ordinal)
            || status.Contains("empty_text", StringComparison.Ordinal))
        {
            penalty += 4;
        }

        return Math.Clamp(penalty, 0, 24);
    }

    private static int ComputeNavigationHintScore(RagMatch match)
    {
        if (match.NavigationScore is double score)
        {
            if (score >= 0.90)
                return 12;
            if (score >= 0.80)
                return 10;
            if (score >= 0.72)
                return 8;
            if (score >= 0.55)
                return 4;
        }

        if (string.Equals(match.ContentRole, RetrievalContentClassifier.NavigationRole, StringComparison.OrdinalIgnoreCase))
            return 10;
        if (string.Equals(match.ContentRole, RetrievalContentClassifier.MixedNavigationContentRole, StringComparison.OrdinalIgnoreCase))
            return 4;

        return 10;
    }

    internal static bool ShouldSupplementSparseWithLexicalFallback(string? category, string query)
        => ShouldSupplementSparseWithLexicalFallback(!string.IsNullOrWhiteSpace(category), query);

    internal static bool ShouldSupplementSparseWithLexicalFallback(bool scoped, string query)
    {
        var tokens = ExtractLexicalQueryTokens(query);
        if (tokens.Count == 0)
            return false;

        if (ContainsQuantityComputationIntent(query)
            && ExtractQuotedLookupPhrases(query).Count == 0)
        {
            return false;
        }

        if (ContainsBroadOnlySparseQueryIntent(query))
            return false;

        var hasStrongToken = tokens.Any(static token =>
            token.Length >= 7
            || token.Any(char.IsDigit)
            || IsReferenceLikeLookupTerm(token));

        if (scoped)
            return hasStrongToken || tokens.Count <= 4;

        return hasStrongToken && tokens.Count <= 4;
    }

    internal static bool ContainsBroadOnlySparseQueryIntent(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        var hasBroadIntent = ContainsBroadScopedSynthesisIntent(normalized)
            || ContainsSituationalBroadSelectionIntent(normalized)
            || ShouldPreferDocumentDiversity(query)
            || ContainsDocumentOverviewIntent(query);
        if (ExtractQuotedLookupPhrases(query).Count > 0)
        {
            return false;
        }

        if (ExtractFocusedLookupPhrases(query).Count > 0
            && !ContainsExplicitBroadScopedSynthesisIntent(normalized)
            && !ContainsSituationalBroadSelectionIntent(normalized)
            && !ShouldPreferComparativeDocumentDiversity(query))
        {
            return false;
        }

        return hasBroadIntent;
    }

    internal static bool ContainsQuantityComputationIntent(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        return ContainsAny(normalized,
            " combien ",
            " quantite ",
            " quantites ",
            " how many ",
            " quantity ",
            " quantities ",
            " calculate ",
            " calcula ",
            " calcular ",
            " calcular ",
            " quante ",
            " quanti ",
            " wie viele ",
            " menge ",
            " mengen ");
    }

    internal static bool ShouldSkipDocumentProfileSearchForQuantityLookup(string query)
    {
        if (!ContainsQuantityComputationIntent(query))
            return false;

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        return !HasReferenceLikeQueryToken(query)
            && !ContainsDocumentOverviewIntent(query)
            && !ContainsDocumentCorpusCountIntent(normalized)
            && !ContainsBroadScopedSynthesisIntent(normalized)
            && !ContainsSituationalBroadSelectionIntent(normalized);
    }

    private static bool ContainsDocumentCorpusCountIntent(string normalized)
        => ContainsAny(normalized,
            " document ",
            " documents ",
            " source ",
            " sources ",
            " fichier ",
            " fichiers ",
            " file ",
            " files ",
            " manual ",
            " manuals ",
            " rapport ",
            " rapports ",
            " report ",
            " reports ",
            " documento ",
            " documentos ",
            " documento ",
            " documenti ",
            " dokument ",
            " dokumente ");

    internal static bool ShouldBackfillEnumerativeSearch(string query, int selectedCount, int topK)
    {
        if (selectedCount >= topK || string.IsNullOrWhiteSpace(query))
            return false;

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant() + " ";
        if (ContainsEnumerativeLookupIntent(normalized) || ExtractFocusedLookupPhrases(query).Count > 0)
        {
            return BuildFocusedLexicalBackfillQuery(query).Length > 0;
        }

        return false;
    }

    internal static bool ShouldBackfillFuzzyTitleLead(string query, IReadOnlyList<RagMatch> selected)
    {
        if (string.IsNullOrWhiteSpace(query) || ExtractFocusedLookupPhrases(query).Count == 0)
            return false;

        if (selected.Count == 0 || !selected.Any(static match => IsContentSelectionCandidate(match)))
            return true;
        if (selected.Any(static match => IsResolvedTitleOrNavigationRoute(match)))
            return false;

        var focusedLookupPhrase = BuildFocusedLexicalBackfillQuery(query);
        var lexicalTokens = ExtractTitlePruneTokens(focusedLookupPhrase)
            .Where(static token => !LexicalStopwords.Contains(token))
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Where(static token => !SpecificAnchorStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();
        if (lexicalTokens.Length < 2)
            return false;

        var requiredAnchorCount = Math.Min(2, lexicalTokens.Length);
        return !selected.Any(match => CountSpecificLexicalAnchors(lexicalTokens, GetPreciseTitleSignalText(match)) >= requiredAnchorCount);
    }

    private static bool ContainsEnumerativeLookupIntent(string normalized)
        => ContainsAny(normalized,
            " quels ",
            " quelles ",
            " lesquels ",
            " lesquelles ",
            " liste ",
            " lister ",
            " trouve ",
            " trouver ",
            " recherche ",
            " chercher ",
            " montre ",
            " afficher ",
            " existent ",
            " disponibles ",
            " qui parlent ",
            " qui mentionnent ",
            " qui contiennent ",
            " avec ",
            " which ",
            " what ",
            " list ",
            " find ",
            " search ",
            " show ",
            " available ",
            " contain ",
            " contains ",
            " mentioning ",
            " mentions ",
            " welche ",
            " finden ",
            " zeige ",
            " cuales ",
            " lista ",
            " encuentra ",
            " quais ",
            " elenco ",
            " elenca ",
            " trova ");

    internal static string BuildFocusedLexicalBackfillQuery(string query)
    {
        var focusedLookupPhrase = ExtractFocusedLookupPhrases(query).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(focusedLookupPhrase))
            return focusedLookupPhrase.Trim();

        var lookup = ExactMatchEntryExtractor.NormalizeForLookup(query);
        var foldedLookup = FoldDiacritics(lookup);
        var phrase = BuildLexicalContentFallbackTerms(query)
            .Where(static term => term.Contains(' '))
            .OrderByDescending(term => PhraseOccursInQuery(term, lookup, foldedLookup))
            .ThenByDescending(ComputeFocusedBackfillTermScore)
            .ThenBy(static term => term.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length)
            .ThenBy(static term => term.Length)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(phrase))
            return phrase.Trim();

        var tokens = ExtractLexicalQueryTokens(query)
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Where(static token => !SpecificAnchorStopwords.Contains(token))
            .Take(5)
            .ToArray();

        return tokens.Length == 0
            ? string.Empty
            : string.Join(' ', tokens);
    }

    internal static IReadOnlyList<string> ExtractFocusedLookupPhrases(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        var phrases = new List<string>();
        foreach (var quotedPhrase in ExtractQuotedLookupPhrases(query))
            AddFocusedLookupPhrase(phrases, quotedPhrase);

        var surface = FoldDiacritics(query).ToLowerInvariant();
        surface = Regex.Replace(surface, @"[\u2010-\u2015_\-]+", " ", RegexOptions.CultureInvariant);
        surface = Regex.Replace(surface, @"['\u2019]", " ", RegexOptions.CultureInvariant);
        surface = Regex.Replace(surface, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
        if (string.IsNullOrWhiteSpace(surface))
            return phrases.Distinct(StringComparer.Ordinal).ToArray();
        if (IsCurrentPassageScopedLookup(surface))
            return phrases.Distinct(StringComparer.Ordinal).ToArray();

        foreach (var pattern in FocusedLookupTargetPatterns)
        {
            foreach (Match match in pattern.Matches(surface))
                AddFocusedLookupPhrase(phrases, match.Groups["target"].Value);
        }

        return phrases
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    internal static IReadOnlyList<string> ExtractComparativeLookupPhrases(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || !ShouldPreferComparativeDocumentDiversity(query))
            return Array.Empty<string>();

        var surface = FoldDiacritics(query).ToLowerInvariant();
        surface = Regex.Replace(surface, @"[\u2010-\u2015_\-]+", " ", RegexOptions.CultureInvariant);
        surface = Regex.Replace(surface, @"['\u2019]", " ", RegexOptions.CultureInvariant);
        surface = Regex.Replace(surface, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
        if (string.IsNullOrWhiteSpace(surface))
            return Array.Empty<string>();

        var phrases = new List<string>();
        foreach (var pattern in ComparativeLookupTargetPatterns)
        {
            foreach (Match match in pattern.Matches(surface))
                AddFocusedLookupPhrase(phrases, match.Groups["target"].Value);
        }

        return phrases
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddFocusedLookupPhrase(List<string> phrases, string candidate)
    {
        var phrase = NormalizeFocusedLookupPhrase(candidate);
        if (!string.IsNullOrWhiteSpace(phrase))
        {
            foreach (var variant in BuildFocusedLookupPhraseVariants(phrase))
                phrases.Add(variant);
        }
    }

    private static IEnumerable<string> BuildFocusedLookupPhraseVariants(string phrase)
    {
        var withoutContext = TrimTrailingFocusedLookupContext(phrase);
        if (!string.IsNullOrWhiteSpace(withoutContext)
            && !string.Equals(withoutContext, phrase, StringComparison.Ordinal))
        {
            yield return withoutContext;
        }

        yield return phrase;
    }

    private static string TrimTrailingFocusedLookupContext(string phrase)
    {
        var tokens = phrase
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        if (tokens.Length < 4)
            return phrase;

        for (var i = tokens.Length - 2; i >= 1; i--)
        {
            if (!IsFocusedLookupContextPreposition(tokens[i]))
                continue;

            var head = tokens[..i];
            var headSignalCount = head.Count(IsFocusedLookupSignalToken);
            if (headSignalCount < 2)
                continue;
            if (IsWeakFocusedLookupContextPreposition(tokens[i])
                && (tokens.Length < 5 || headSignalCount < 3))
            {
                continue;
            }

            var tail = tokens[(i + 1)..];
            if (tail.Length == 0 || tail.Length > 4)
                continue;

            if (tail.Any(static token => token.Length <= 1 || token.All(char.IsDigit)))
                continue;

            return string.Join(' ', head);
        }

        return phrase;
    }

    private static bool IsFocusedLookupContextPreposition(string token)
        => token is "a" or "au" or "aux" or "avec" or "chez" or "dans" or "de" or "du" or "en" or "pour" or "sur"
            or "about" or "for" or "from" or "in" or "on" or "with"
            or "con" or "para" or "sobre"
            or "com" or "em"
            or "mit" or "uber" or "ueber" or "von"
            or "per" or "su";

    private static bool IsWeakFocusedLookupContextPreposition(string token)
        => token is "a" or "au" or "aux" or "de" or "du" or "en" or "per" or "su";

    private static bool IsCurrentPassageScopedLookup(string normalizedSurface)
        => Regex.IsMatch(
            normalizedSurface,
            @"\b(?:dans|depuis|from|in)\s+(?:ce|cet|cette|this|the)\s+(?:passage|extrait|texte|text|snippet|section)\b",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));

    private static string? NormalizeFocusedLookupPhrase(string candidate)
    {
        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(candidate)).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(8)
            .ToList();

        while (tokens.Count > 0 && IsFocusedLookupLeadingEdgeToken(tokens[0]))
            tokens.RemoveAt(0);
        while (tokens.Count > 1 && IsFocusedLookupLeadingDescriptorToken(tokens[0]))
            tokens.RemoveAt(0);
        while (tokens.Count > 0 && IsFocusedLookupLeadingEdgeToken(tokens[0]))
            tokens.RemoveAt(0);
        var alternativeIndex = tokens.FindIndex(static token => IsFocusedLookupAlternativeSeparatorToken(token));
        if (alternativeIndex > 0)
            tokens = tokens.Take(alternativeIndex).ToList();
        while (tokens.Count > 0 && IsFocusedLookupTrailingEdgeToken(tokens[^1]))
            tokens.RemoveAt(tokens.Count - 1);

        if (tokens.Count == 0)
            return null;

        var signalCount = tokens.Count(IsFocusedLookupSignalToken);
        if (signalCount < 2 && !tokens.Any(static token => token.Length >= 6 || token.Any(char.IsDigit)))
            return null;

        var phrase = string.Join(' ', tokens);
        if (IsFocusedLookupMetaInstructionPhrase(phrase))
            return null;

        return phrase.Length is >= 4 and <= 80 ? phrase : null;
    }

    private static bool IsFocusedLookupMetaInstructionPhrase(string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
            return false;

        var normalized = $" {NormalizeQuery(FoldDiacritics(phrase).ToLowerInvariant())} ";
        return ContainsAny(
            normalized,
            " sans inventer ",
            " sans invention ",
            " without inventing ",
            " without invention ",
            " sin inventar ",
            " sin inventos ",
            " sem inventar ",
            " sem inventar nada ",
            " ohne zu erfinden ",
            " ohne erfindung ",
            " es anpasst ",
            " sie anpasst ",
            " anpasst ",
            " senza inventare ",
            " senza invenzione ",
            " how to adapt it ",
            " how to adapt them ",
            " como adaptarla ",
            " como adaptarlo ",
            " como adaptar ",
            " come adattarla ",
            " come adattarlo ",
            " wie anpassen ",
            " wie man es anpasst ",
            " anpassung ");
    }

    private static bool IsFocusedLookupLeadingEdgeToken(string token)
        => TitleConnectorTokens.Contains(token)
           || LexicalStopwords.Contains(token)
           || SpecificAnchorStopwords.Contains(token)
           || PrimaryAnchorStopwords.Contains(token)
           || token is "l" or "s";

    private static bool IsFocusedLookupLeadingDescriptorToken(string token)
        => token is "grand" or "grande" or "grands" or "grandes"
            or "principal" or "principale" or "principaux" or "principales"
            or "etape" or "etapes"
            or "step" or "steps" or "main" or "major"
            or "procedure" or "procedures";

    private static bool IsFocusedLookupAlternativeSeparatorToken(string token)
        => token is "ou" or "or" or "oder" or "oppure";

    private static bool IsFocusedLookupTrailingEdgeToken(string token)
        => !IsShortTitleSuffixToken(token)
           && IsFocusedLookupLeadingEdgeToken(token);

    private static bool IsShortTitleSuffixToken(string token)
        => token.Length == 1 && token.All(char.IsLetterOrDigit);

    private static bool IsFocusedLookupSignalToken(string token)
        => token.Length >= 3
           && token.Any(char.IsLetter)
           && !TitleConnectorTokens.Contains(token)
           && !LexicalStopwords.Contains(token)
           && !SpecificAnchorStopwords.Contains(token)
           && !PrimaryAnchorStopwords.Contains(token);

    private const string FocusedLookupArticlePattern =
        @"(?:(?:de\s+la|de\s+l|les|des|the|some|une|un|du|le|la|l|an|a)\b|l['\u2019])\s+";

    private const string FocusedLookupTargetPatternText =
        @"(?<target>[\p{L}\p{Nd}][\p{L}\p{Nd}\s\-]{2,80}?)";

    private const string FocusedLookupTargetStopLookahead =
        @"(?=\s+(?:dans|depuis|from|in|aus|im|von|vom|source|sources|fonte|fontes|fuente|fuentes|quelle|quellen|etape|etapes|step|steps|schritt|schritte|passo|passos|temps|time|duree|duration|duracion|duracao|dauer|pdf|document|documents|doc|docs|fichier|fichiers|arquivo|arquivos|archivo|archivos|file|files|livre|livres|book|books|libro|libros|manuale|manuel|manuels|manual|manuals|guide|guides|guia|guias|handbuch|handbucher|version|mode|reglage|reglages|setting|settings|parametre|parametres|avec|with|com|con|mit|senza|sans|without|compare|comparer|compara|comparar|vergleiche|si|oui|ja)\b|[\?:;,\.\r\n]|$)";

    private static readonly Regex FocusedLookupTargetPattern = new(
        @"\b(?:(?:fiche|ficha|scheda|karte|card|procedure|procedures|procedimiento|procedimientos|procedimento|procedimentos|process|processus|processo|processi|section|seccion|secao|sezione|abschnitt|chapitre|chapter|capitulo|capitolo|kapitel|topic|sujet|subject|tema|assunto|argomento)\b(?:\s+(?:claire|clair|clear|detaillee|detailee|detailed|simple|complete|completa|completo|klar)){0,3}|(?:donne|donner|montre|trouve|chercher|cherche|veux|souhaite|give|show|find|get|want|need|dame|muestra|mostrar|encuentra|encontrar|quero|procura|procurar|mostra|trova|cerca|voglio|zeige|finde|finden|suche|such|mochte|will|brauche)\b(?:\s+[\p{L}\p{Nd}']{1,24}){0,8}?)\s+(?:de|du|des|d['’]?|pour|sur|of|for|about|on|para|sobre|por|per|su|di|del|della|do|da|dos|das|em|zu|zum|zur|uber|ueber)\s+(?<target>[\p{L}\p{Nd}][\p{L}\p{Nd}\s\-]{2,80}?)(?=\s+(?:dans|depuis|from|in|aus|im|von|vom|source|sources|fonte|fontes|fuente|fuentes|quelle|quellen|etape|etapes|step|steps|schritt|schritte|passo|passos|temps|time|duree|duration|duracion|duracao|dauer|pdf|document|documents|doc|docs|fichier|fichiers|arquivo|arquivos|archivo|archivos|file|files|livre|livres|book|books|libro|libros|manuale|manuel|manuels|manual|manuals|guide|guides|guia|guias|handbuch|handbucher|avec|with|com|con|mit|senza|sans|without|compare|comparer|compara|comparar|vergleiche|si|oui|ja)\b|[\?:;,\.\r\n]|$)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex DirectObjectFocusedLookupTargetPattern = new(
        @"\b(?:il\s+me\s+faut|il\s+nous\s+faut|je\s+veux|j['\u2019]ai\s+besoin\s+de|donne\s+moi|donnez\s+moi|montre\s+moi|montrez\s+moi|trouve\s+moi|trouvez\s+moi|give\s+me|show\s+me|get\s+me|find\s+me|i\s+need|we\s+need|quiero|quero|voglio|ich\s+brauche)\s+(?:" + FocusedLookupArticlePattern + @")?" + FocusedLookupTargetPatternText + FocusedLookupTargetStopLookahead,
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex AvailabilityFocusedLookupTargetPattern = new(
        @"\b(?:tu\s+as|as\s+tu|avez\s+vous|vous\s+avez|est\s+ce\s+que\s+tu\s+as|est\s+ce\s+que\s+vous\s+avez|do\s+you\s+have|have\s+you\s+got|have\s+you|hay|tienes|tem|tens|hai|hast\s+du|haben\s+sie|gibt\s+es)\s+(?:" + FocusedLookupArticlePattern + @")?(?:[\p{L}\p{Nd}]{3,24}\s+(?:de|du|des|d['\u2019]|of|for)\s+)?" + FocusedLookupTargetPatternText + FocusedLookupTargetStopLookahead,
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex SourceAttributionFocusedLookupTargetPattern = new(
        @"\b(?:est\s+ce\s+que|is|does|viene|vem|stammt)\s+(?:" + FocusedLookupArticlePattern + @")?" + FocusedLookupTargetPatternText + @"(?=\s+(?:vient|provient|come|comes|viene|vem|stammt)\s+(?:de|du|des|d['\u2019]|from|di|da|do|de|aus|von)\b)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex ProvenanceFocusedLookupTargetPattern = new(
        @"\b(?:d\s+ou\s+(?:vient|provient|sort|est\s+issu|est\s+issue)|source\s+(?:de|du|des|d['\u2019]|for|of)|origine\s+(?:de|du|des|d['\u2019]|for|of)|where\s+(?:does|is)\s+[\p{L}\p{Nd}\s'’\-/]{0,40}?\s+(?:come|from))\s+(?:" + FocusedLookupArticlePattern + @")?(?:[\p{L}\p{Nd}]{3,24}\s+(?:de|du|des|d['\u2019]|of)\s+)?" + FocusedLookupTargetPatternText + FocusedLookupTargetStopLookahead,
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex SearchIntentFocusedLookupTargetPattern = new(
        @"\b(?:je\s+cherche|je\s+recherche|cherche|chercher|recherche|rechercher|trouve|trouver|i\s+(?:am\s+)?(?:looking\s+for|searching\s+for|seeking)|find|get|busco|busca|buscar|procuro|procura|procurar|cerco|cerca|cercare|ich\s+suche|suche|such|finde|finden)\s+(?:" + FocusedLookupArticlePattern + @")?" + FocusedLookupTargetPatternText + FocusedLookupTargetStopLookahead,
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex[] ComparativeLookupTargetPatterns =
    [
        new(
            @"\b(?:compare|comparer|comparez|comparison|comparaison|comparar|compara|confronta|vergleiche|vergleichen)\s+(?:(?:les|des|deux|plusieurs|the|some|multiple|several)\s+){0,3}(?<target>[\p{L}\p{Nd}][\p{L}\p{Nd}\s\-]{2,80}?)(?=\s+(?:du|de|des|dans|depuis|from|in|of|del|do|dos|delle|aus)\s+(?:corpus|base|documents?|docs?|pdfs?|fichiers?|files?|category|categorie|categoria)|\s*[:\?;\.,\r\n]|$)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100)),
        new(
            @"\b(?:choisir|choisis|choisissez|choose|select|escoge|escolher|scegliere)\s+(?:entre|between|tra|zwischen)\s+(?<target>[\p{L}\p{Nd}][\p{L}\p{Nd}\s,\-]{2,120}?)(?=\s+(?:et|and|e|und)?\s*(?:justifie|justify|explique|explain)\b|[\?:;\.\r\n]|$)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100)),
        new(
            @"\b(?:plusieurs|multiple|several|varias|varios|diverses|diversi|mehrere)\s+(?<target>[\p{L}\p{Nd}][\p{L}\p{Nd}\s\-]{2,80}?)(?=\s*[\?;,\.\r\n]\s*(?:compare|comparer|comparez|comparison|comparaison|comparar|compara|confronta|vergleiche|vergleichen)\b)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100))
    ];

    private static readonly Regex AlternativeFocusedLookupTargetPattern = new(
        @"\b(?:je\s+cherche|je\s+recherche|cherche|chercher|recherche|rechercher|trouve|trouver|i\s+(?:am\s+)?(?:looking\s+for|need|want)|find|get|want|need|busco|busca|buscar|quero|procuro|procurar|cerco|cerca|cercare|voglio|suche|such|finde|finden|will|brauche)\s+(?:" + FocusedLookupArticlePattern + @")?" + FocusedLookupTargetPatternText + @"(?=\s+(?:ou|or|oder|oppure)\b)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex ActionQuestionFocusedLookupTargetPattern = new(
        @"\b(?:comment|how|como|come|wie)\s+[\p{L}]{3,24}\s+(?:" + FocusedLookupArticlePattern + @")?" + FocusedLookupTargetPatternText + FocusedLookupTargetStopLookahead,
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex DefinitionThenActionFocusedLookupTargetPattern = new(
        @"\b(?:c\s+est\s+quoi|qu\s+est\s+ce\s+que|qu\s+est\s+ce\s+qu(?:il|elle|ils|elles)?|what\s+is|what\s+are|que\s+es|o\s+que\s+e|che\s+cos\s+e|was\s+ist)\s+(?:" + FocusedLookupArticlePattern + @")?" + FocusedLookupTargetPatternText + @"(?=\s+(?:et|and|y|e|und)\s+(?:comment|how|como|come|wie|lequel|laquelle|which)\b|[\?:;,\.\r\n]|$)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex ExplainFocusedLookupTargetPattern = new(
        @"\b(?:explique(?:r|z)?|detaille|details?|resume(?:r)?|resumes?|summarize|explain|describe|decris|descris)\s*(?:moi)?\s+(?:" + FocusedLookupArticlePattern + @")?" + FocusedLookupTargetPatternText + FocusedLookupTargetStopLookahead,
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex ParameterFocusedLookupTargetPattern = new(
        @"\b(?:combien\s+de\s+temps|quantites?|quantities?|amounts?|etapes?|temps|reglages?|parametres?|vitesses?|temperatures?|how\s+long|which\s+settings|settings?|parameters?)\b[\p{L}\p{Nd}\s'’\-/]{0,90}?\s+(?:pour|for|de|du|des|d['\u2019]|sur|about|on|para|sobre|per|su)\s+(?:(?:\d+|[a-z]+)\s+[\p{L}]{2,24}\s+(?:de|d['\u2019]|of)\s+)?(?:" + FocusedLookupArticlePattern + @")?" + FocusedLookupTargetPatternText + FocusedLookupTargetStopLookahead,
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex StructuredInfoFocusedLookupTargetPattern = new(
        @"\b(?:c\s+est\s+quoi|qu\s+est\s+ce\s+qu(?:il|elle|ils|elles)?\s+faut|what\s+(?:is|are)|which\s+(?:is|are))\b[\p{L}\p{Nd}\s'’\-/]{0,90}?\s+(?:pour|for|de|du|des|d['\u2019]|sur|about|on|of)\s+(?:" + FocusedLookupArticlePattern + @")?" + FocusedLookupTargetPatternText + FocusedLookupTargetStopLookahead,
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex[] FocusedLookupTargetPatterns =
    [
        SourceAttributionFocusedLookupTargetPattern,
        ProvenanceFocusedLookupTargetPattern,
        AvailabilityFocusedLookupTargetPattern,
        SearchIntentFocusedLookupTargetPattern,
        AlternativeFocusedLookupTargetPattern,
        FocusedLookupTargetPattern,
        DirectObjectFocusedLookupTargetPattern,
        ActionQuestionFocusedLookupTargetPattern,
        DefinitionThenActionFocusedLookupTargetPattern,
        ExplainFocusedLookupTargetPattern,
        ParameterFocusedLookupTargetPattern,
        StructuredInfoFocusedLookupTargetPattern
    ];

    private static bool PhraseOccursInQuery(string phrase, string lookup, string foldedLookup)
    {
        if (string.IsNullOrWhiteSpace(phrase))
            return false;

        return lookup.Contains(phrase, StringComparison.Ordinal)
            || foldedLookup.Contains(FoldDiacritics(phrase), StringComparison.Ordinal);
    }

    private static int ComputeFocusedBackfillTermScore(string term)
    {
        var parts = term
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => !LexicalStopwords.Contains(token))
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Where(static token => !SpecificAnchorStopwords.Contains(token))
            .ToArray();
        if (parts.Length == 0)
            return 0;

        return (parts.Count(static token => token.Length >= 7) * 4)
            + Math.Min(3, parts.Length)
            + Math.Min(3, parts.Sum(static token => Math.Min(1, token.Count(char.IsDigit))));
    }

    private static async Task<List<RagMatch>> FilterMatchesAgainstActiveDocumentVersionsAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        IReadOnlyList<RagMatch> rawMatches,
        CancellationToken ct,
        string? categoryPath = null)
    {
        if (rawMatches.Count == 0)
            return [];

        var docIds = rawMatches
            .Select(m => Guid.TryParse(m.DocId, out var docId) ? docId : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();

        if (docIds.Length == 0)
            return [];

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
SELECT d.doc_id AS "DocId",
       d.doc_path AS "DocPath",
       d.status AS "Status",
       d.indexed_version AS "IndexedVersion",
       r.ingestion_version AS "CurrentRevisionIngestionVersion",
       LOWER(ENCODE(d.content_hash, 'hex')) AS "ContentHashHex"
FROM documents d
LEFT JOIN document_revisions r
  ON r.tenant_id = d.tenant_id
 AND r.doc_id = d.doc_id
 AND r.indexed_version = d.indexed_version
WHERE d.tenant_id=@tenant_id
  AND d.doc_id = ANY(@doc_ids);
""";

        var rows = await conn.QueryAsync<DocVersionRow>(new CommandDefinition(sql, new
        {
            tenant_id = tenantId,
            doc_ids = docIds
        }, cancellationToken: ct));

        var active = rows.ToDictionary(x => x.DocId, x => x);
        var normalizedCategoryPath = NormalizeRagCategoryPathForSql(categoryPath);
        var filtered = new List<RagMatch>(rawMatches.Count);
        foreach (var match in rawMatches)
        {
            if (!Guid.TryParse(match.DocId, out var docIdValue))
                continue;
            if (!active.TryGetValue(docIdValue, out var row))
                continue;
            if (!string.Equals(row.Status, "indexed", StringComparison.OrdinalIgnoreCase))
                continue;
            if (row.IndexedVersion <= 0)
                continue;
            if (!RagDocPathMatchesCategoryPath(row.DocPath, normalizedCategoryPath))
                continue;

            if (!IsActiveDenseMatchForRevision(match, row.CurrentRevisionIngestionVersion, row.ContentHashHex))
                continue;

            filtered.Add(match);
        }

        return filtered;
    }

    private static async Task<List<RagMatch>> AttachDocumentProfileContentCardsAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        IReadOnlyList<RagMatch> matches,
        string query,
        CancellationToken ct,
        int perMatchLimit = 12)
    {
        if (matches.Count == 0 || perMatchLimit <= 0)
            return matches.ToList();

        var candidates = matches
            .Where(static match => match.MatchedContentCards is null || match.MatchedContentCards.Count == 0)
            .Select(static match => Guid.TryParse(match.DocId, out var docId) ? docId : Guid.Empty)
            .Where(static docId => docId != Guid.Empty)
            .Distinct()
            .ToArray();
        if (candidates.Length == 0)
            return matches.ToList();

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
SELECT
    d.doc_id AS "DocId",
    jsonb_build_object(
        'contentCards',
        COALESCE(
            jsonb_agg(
                jsonb_build_object(
                    'title', pcc.title,
                    'contentCardId', pcc.content_card_id,
                    'pageStart', pcc.page_start,
                    'pageEnd', pcc.page_end,
                    'kind', pcc.kind,
                    'signals', pcc.signals,
                    'evidence', pcc.metadata->'evidence')
                ORDER BY
                    CASE pcc.profile_version
                        WHEN 'llm_backoffice_v1' THEN 0
                        WHEN 'foundation_v1' THEN 1
                        WHEN 'deterministic_v1' THEN 2
                        ELSE 3
                    END,
                    pcc.card_index)
            FILTER (WHERE pcc.content_card_id IS NOT NULL),
            '[]'::jsonb))::text AS "ContentCardsJson"
FROM documents d
JOIN document_revisions r
  ON r.tenant_id = d.tenant_id
 AND r.doc_id = d.doc_id
 AND r.indexed_version = d.indexed_version
LEFT JOIN LATERAL (
    SELECT raw_card.*
    FROM document_profile_content_cards raw_card
    WHERE raw_card.tenant_id = d.tenant_id
      AND raw_card.revision_id = r.revision_id
      AND NULLIF(BTRIM(raw_card.title), '') IS NOT NULL
    ORDER BY
        CASE raw_card.profile_version
            WHEN 'llm_backoffice_v1' THEN 0
            WHEN 'foundation_v1' THEN 1
            WHEN 'deterministic_v1' THEN 2
            ELSE 3
        END,
        raw_card.card_index
    LIMIT 160
) pcc ON true
WHERE d.tenant_id = @tenant_id
  AND d.status = 'indexed'
  AND d.indexed_version > 0
  AND d.doc_id = ANY(@doc_ids)
GROUP BY d.doc_id;
""";

        var rows = await conn.QueryAsync<DocProfileContentCardsRow>(new CommandDefinition(sql, new
        {
            tenant_id = tenantId,
            doc_ids = candidates
        }, cancellationToken: ct));
        var byDocId = rows.ToDictionary(static row => row.DocId, static row => row.ContentCardsJson);
        if (byDocId.Count == 0)
            return matches.ToList();

        return matches
            .Select(match =>
            {
                if (match.MatchedContentCards is { Count: > 0 }
                    || !Guid.TryParse(match.DocId, out var docId)
                    || !byDocId.TryGetValue(docId, out var metadataJson))
                {
                    return match;
                }

                var selectionQuery = CollapseWhitespace(string.Join(' ', new[]
                {
                    query,
                    match.SectionTitle,
                    match.HeadingPath,
                    match.Text
                }));
                var cards = BuildMatchedContentCards(metadataJson, selectionQuery, perMatchLimit, match.PageStart, match.PageEnd);
                return cards.Count == 0
                    ? match
                    : match with { MatchedContentCards = cards };
            })
            .ToList();
    }

    internal static bool IsActiveDenseMatchForRevision(
        RagMatch match,
        int? currentRevisionIngestionVersion,
        string? currentContentHashHex)
    {
        var matchHash = NormalizeHashHex(match.HashDoc);
        var currentHash = NormalizeHashHex(currentContentHashHex);
        var hashCanBeChecked = !string.IsNullOrWhiteSpace(matchHash) && !string.IsNullOrWhiteSpace(currentHash);
        var hashMatches = hashCanBeChecked
            && string.Equals(matchHash, currentHash, StringComparison.OrdinalIgnoreCase);

        if (match.IngestionVersion.HasValue)
        {
            if (!currentRevisionIngestionVersion.HasValue)
                return false;
            if (match.IngestionVersion.Value != currentRevisionIngestionVersion.Value)
                return false;

            return !hashCanBeChecked || hashMatches;
        }

        return hashMatches;
    }

    private static string? NormalizeHashHex(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().ToLowerInvariant();
    }

    private sealed record DocVersionRow(
        Guid DocId,
        string DocPath,
        string Status,
        int IndexedVersion,
        int? CurrentRevisionIngestionVersion,
        string? ContentHashHex);
    private sealed record DocProfileContentCardsRow(
        Guid DocId,
        string? ContentCardsJson);
    private sealed record ExactMatchRow(
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
        string? MatchedTerm);

    private sealed record MetadataReferenceRow(
        Guid DocId,
        string DocPath,
        string DocName,
        string? Category,
        int IngestionVersion,
        string? HashDoc);

    private sealed record TopCategoryOrderRow(
        string Path,
        int DisplayOrder);

    private sealed class RagExtractionDocumentQualityRow
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
    }

    private sealed class RagExtractionPageQualityRow
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

    private sealed record RagExtractionPageQuality(
        string QualityStatus,
        double ExtractionConfidence,
        bool ManualReviewRecommended,
        string TextStatus,
        string[] Signals);

    private sealed record RagOcrDiagnosticsSummary(
        string? Mode = null,
        string? FailureReason = null,
        string? AppliedReason = null,
        bool? TimedOut = null,
        int? AttemptedPageCount = null,
        int? SkippedPageCount = null,
        int? PagesWithNovelTextCount = null);

    // Must match the SELECT column order/names/types in SearchSparseMatchesAsync exactly —
    // Dapper materialises records by positional constructor binding, so a missing column
    // (e.g. SectionOrdinalPlaceholder, which we don't use but must declare) or a type
    // mismatch (ts_rank_cd returns float/Single, not double) makes the whole endpoint
    // throw "no matching constructor" at runtime.
    private sealed record SparseMatchRow(
        Guid DocId,
        string DocPath,
        string DocName,
        string? Category,
        int PageStart,
        int PageEnd,
        int? OffsetStart,
        int? OffsetEnd,
        Guid ChunkId,
        int ChunkIndex,
        string Text,
        int IngestionVersion,
        string? HashDoc,
        string EmbedText,
        Guid? SectionOrdinalPlaceholder,
        string? SectionTitle,
        string? HeadingPath,
        string ChunkType,
        string? ContentRole,
        string? NavigationReason,
        string? OriginalChunkType,
        double? NavigationScore,
        double? ContentDensityScore,
        string? PrevChunkId,
        string? NextChunkId,
        string? SameSectionChunkId,
        string? MatchedContentCardsJson,
        float SparseRank);

    private sealed record DocumentProfileMatchRow(
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
        double SparseRank,
        long MatchCount);

    private sealed record ScoredDocumentProfileMatchRow(
        DocumentProfileMatchRow Row,
        double Score,
        double SpecificityBoost,
        double BaseScore);

    private sealed record LinkedMatchRow(
        Guid DocId,
        string DocPath,
        string DocName,
        string? Category,
        int PageStart,
        int PageEnd,
        int? OffsetStart,
        int? OffsetEnd,
        Guid ChunkId,
        int ChunkIndex,
        string Text,
        int IngestionVersion,
        string? HashDoc,
        string ChunkType,
        string? ContentRole,
        string? NavigationReason,
        string? OriginalChunkType,
        double? NavigationScore,
        double? ContentDensityScore,
        string? SectionTitle,
        string? HeadingPath,
        string LinkType,
        Guid AnchorSourceId,
        Guid AnchorChunkId,
        string? PrevChunkId,
        string? NextChunkId,
        string? SameSectionChunkId);

    private static void AddRankedMatches(
        List<RagMatch> selected,
        HashSet<string> selectedKeys,
        IEnumerable<RagMatch> matches,
        int topK,
        double minScore,
        int maxPerDoc,
        int maxPerPage,
        int maxPerSection = 2,
        bool prioritizeDocumentProfiles = false)
    {
        var perDoc = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var perPage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var perSection = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var selectedMatch in selected)
        {
            if (string.IsNullOrWhiteSpace(selectedMatch.DocPath))
                continue;
            if (!CountsAgainstChunkQuota(selectedMatch))
                continue;

            var docKey = selectedMatch.DocPath!;
            perDoc[docKey] = perDoc.TryGetValue(docKey, out var docCount) ? docCount + 1 : 1;
            var pageKey = $"{docKey}:{selectedMatch.PageStart ?? -1}:{selectedMatch.PageEnd ?? -1}";
            perPage[pageKey] = perPage.TryGetValue(pageKey, out var pageCount) ? pageCount + 1 : 1;
            var sectionKey = BuildSectionKey(selectedMatch);
            if (sectionKey != null)
                perSection[sectionKey] = perSection.TryGetValue(sectionKey, out var secCount) ? secCount + 1 : 1;
        }

        var prioritizedMatches = OrderMatchesForSelection(matches, prioritizeDocumentProfiles);

        foreach (var match in prioritizedMatches)
        {
            if (selected.Count >= topK)
                break;
            if (match.Score < minScore)
                continue;
            if (string.IsNullOrWhiteSpace(match.DocPath))
                continue;

            var dedupKey = BuildMatchDedupKey(match);
            if (!selectedKeys.Add(dedupKey))
                continue;

            if (selected.Any(existing => IsNearDuplicatePageOverlap(existing, match)))
            {
                selectedKeys.Remove(dedupKey);
                continue;
            }

            if (!IsResolvedTitleOrNavigationRoute(match)
                && LooksLikeNavigationalChunk(match)
                && selected.Any(static existing => IsContentSelectionCandidate(existing)))
            {
                selectedKeys.Remove(dedupKey);
                continue;
            }

            var docKey = match.DocPath!;
            perDoc.TryGetValue(docKey, out var docCountCurrent);
            if (docCountCurrent >= maxPerDoc)
            {
                selectedKeys.Remove(dedupKey);
                continue;
            }

            var pageKey = $"{docKey}:{match.PageStart ?? -1}:{match.PageEnd ?? -1}";
            perPage.TryGetValue(pageKey, out var pageCountCurrent);
            if (pageCountCurrent >= maxPerPage)
            {
                selectedKeys.Remove(dedupKey);
                continue;
            }

        // CDC v3.1 §11.3: max 2 chunks per section
            var sectionKey = BuildSectionKey(match);
            if (sectionKey != null)
            {
                perSection.TryGetValue(sectionKey, out var secCountCurrent);
                if (secCountCurrent >= maxPerSection)
                {
                    selectedKeys.Remove(dedupKey);
                    continue;
                }
            }

            selected.Add(match);
            perDoc[docKey] = docCountCurrent + 1;
            perPage[pageKey] = pageCountCurrent + 1;
            if (sectionKey != null)
                perSection[sectionKey] = perSection.TryGetValue(sectionKey, out var secUpdated) ? secUpdated + 1 : 1;
        }
    }

    internal static void RebuildSelectedKeys(IReadOnlyList<RagMatch> selected, HashSet<string> selectedKeys)
    {
        selectedKeys.Clear();
        foreach (var match in selected)
            selectedKeys.Add(BuildMatchDedupKey(match));
    }

    internal static IReadOnlyList<RagMatch> OrderMatchesForSelection(
        IEnumerable<RagMatch> matches,
        bool prioritizeDocumentProfiles)
    {
        var orderedMatches = matches as IReadOnlyList<RagMatch> ?? matches.ToList();
        return prioritizeDocumentProfiles
            ? orderedMatches
                .OrderByDescending(static match => IsDocumentProfileMatch(match))
                .ThenByDescending(static match => IsResolvedTitleOrNavigationRoute(match))
                .ThenByDescending(static match => match.Score)
                .ThenBy(static match => match.DocPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static match => match.ChunkIndex)
                .ToList()
            : orderedMatches
                .Where(static match => !IsDocumentProfileMatch(match))
                .OrderByDescending(static match => IsResolvedTitleOrNavigationRoute(match))
                .ThenByDescending(static match => match.Score)
                .ThenBy(static match => match.DocPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static match => match.ChunkIndex)
                .Concat(orderedMatches
                    .Where(IsDocumentProfileMatch)
                    .OrderByDescending(static match => match.Score)
                    .ThenBy(static match => match.DocPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static match => match.ChunkIndex))
                .ToList();
    }

    internal static bool ShouldPrioritizeDocumentProfilesForSelection(
        bool preferDocumentDiversity,
        bool allowSparseAssistForScopedProfileFallback)
        => preferDocumentDiversity && !allowSparseAssistForScopedProfileFallback;

    internal static bool ShouldDeferDocumentProfileSearch(
        bool canSearchDocumentProfiles,
        bool skipChunkRetrieversForDocumentOverview,
        bool useScopedProfileFallback,
        bool allowSparseAssistForScopedProfileFallback)
        => canSearchDocumentProfiles
           && !skipChunkRetrieversForDocumentOverview
           && useScopedProfileFallback
           && allowSparseAssistForScopedProfileFallback;

    internal static bool ShouldRunDeferredDocumentProfileSearch(
        IReadOnlyList<RagMatch> selected,
        int topK,
        bool preferDocumentDiversity,
        bool useScopedProfileFallback,
        bool allowSparseAssistForScopedProfileFallback)
    {
        if (!useScopedProfileFallback || !allowSparseAssistForScopedProfileFallback)
            return false;
        if (selected.Count == 0)
            return true;
        if (selected.Count < Math.Min(topK, 4))
            return true;
        if (!preferDocumentDiversity)
            return false;

        var distinctDocuments = selected
            .Where(static match => !string.IsNullOrWhiteSpace(match.DocPath))
            .Select(static match => match.DocPath!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        return distinctDocuments < Math.Min(selected.Count, 3);
    }

    internal static IReadOnlyList<RagMatch> RankDeferredDocumentProfileMatches(IEnumerable<RagMatch> matches)
        => matches
            .OrderByDescending(static match => match.Score)
            .ThenBy(static match => match.DocPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static match => match.ChunkIndex)
            .ToArray();

    private static bool IsDocumentProfileMatch(RagMatch match)
        => string.Equals(match.ChunkType, "document_profile", StringComparison.Ordinal)
           || string.Equals(ResolveRetriever(match), "document_profile", StringComparison.Ordinal);

    private static bool CountsAgainstChunkQuota(RagMatch match)
        => !IsDocumentProfileMatch(match);

    private static string? BuildSectionKey(RagMatch match)
    {
        if (string.IsNullOrWhiteSpace(match.DocPath))
            return null;
        // Use SectionOrdinal if available, fallback to SectionTitle
        if (match.SectionOrdinal.HasValue)
            return $"{match.DocPath}:sec:{match.SectionOrdinal.Value}";
        if (!string.IsNullOrWhiteSpace(match.SectionTitle))
            return $"{match.DocPath}:sec:{match.SectionTitle}";
        return null;
    }

    internal static async Task<List<RagMatch>> SearchLinkedMatchesAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        IReadOnlyList<RagMatch> anchorMatches,
        string? category,
        string? docId,
        string? docPath,
        int topK,
        CancellationToken ct,
        string? categoryPath = null)
    {
        if (topK <= 0)
            return [];

        var chunkAnchors = anchorMatches
            .Where(match =>
            {
                var retriever = ResolveRetriever(match);
                return (string.Equals(retriever, "dense_qdrant", StringComparison.Ordinal)
                        || string.Equals(retriever, "linked_context", StringComparison.Ordinal)
                        || string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal)
                        || string.Equals(retriever, "title_anchor_route", StringComparison.Ordinal)
                        || string.Equals(retriever, "navigation_route", StringComparison.Ordinal)
                        || string.Equals(retriever, "direct_title_token_route", StringComparison.Ordinal)
                        || string.Equals(retriever, "fuzzy_title_lead", StringComparison.Ordinal))
                    && Guid.TryParse(match.ChunkId, out _);
            })
            .Select(match => new LinkedAnchorCandidate(
                SourceId: Guid.Parse(match.ChunkId!),
                SourceRetriever: ResolveRetriever(match),
                AnchorScore: match.Score))
            .DistinctBy(item => item.SourceId)
            .ToList();

        var exactAnchors = anchorMatches
            .Where(match =>
                string.Equals(ResolveRetriever(match), "exact_match", StringComparison.Ordinal)
                && Guid.TryParse(match.ChunkId, out _))
            .Select(match => new LinkedAnchorCandidate(
                SourceId: Guid.Parse(match.ChunkId!),
                SourceRetriever: "exact_match",
                AnchorScore: match.Score))
            .DistinctBy(item => item.SourceId)
            .ToList();

        var anchorCandidates = chunkAnchors
            .Concat(exactAnchors)
            .OrderByDescending(item => item.AnchorScore)
            .Take(Math.Max(1, Math.Min(topK * 2, 4)))
            .ToList();

        if (anchorCandidates.Count == 0)
            return [];

        var anchorsBySourceId = anchorCandidates.ToDictionary(item => item.SourceId);
        var denseAnchorIds = anchorCandidates
            .Where(item => string.Equals(item.SourceRetriever, "dense_qdrant", StringComparison.Ordinal))
            .Select(item => item.SourceId)
            .ToArray();
        var linkedAnchorIds = anchorCandidates
            .Where(item => string.Equals(item.SourceRetriever, "linked_context", StringComparison.Ordinal))
            .Select(item => item.SourceId)
            .ToArray();
        var sparseAnchorIds = anchorCandidates
            .Where(item => string.Equals(item.SourceRetriever, "sparse_bm25", StringComparison.Ordinal))
            .Select(item => item.SourceId)
            .ToArray();
        var routeAnchorIds = anchorCandidates
            .Where(item =>
                string.Equals(item.SourceRetriever, "title_anchor_route", StringComparison.Ordinal)
                || string.Equals(item.SourceRetriever, "navigation_route", StringComparison.Ordinal)
                || string.Equals(item.SourceRetriever, "direct_title_token_route", StringComparison.Ordinal)
                || string.Equals(item.SourceRetriever, "fuzzy_title_lead", StringComparison.Ordinal))
            .Select(item => item.SourceId)
            .ToArray();
        var exactAnchorIds = anchorCandidates
            .Where(item => string.Equals(item.SourceRetriever, "exact_match", StringComparison.Ordinal))
            .Select(item => item.SourceId)
            .ToArray();

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
WITH requested_anchors AS (
    SELECT source_anchor_id, anchor_chunk_id, anchor_retriever
    FROM (
        SELECT anchor_chunk_id AS source_anchor_id, anchor_chunk_id, 'dense_qdrant'::text AS anchor_retriever
        FROM unnest(@dense_anchor_chunk_ids::uuid[]) AS anchor_chunk_id
        UNION ALL
        SELECT anchor_chunk_id AS source_anchor_id, anchor_chunk_id, 'linked_context'::text AS anchor_retriever
        FROM unnest(@linked_anchor_chunk_ids::uuid[]) AS anchor_chunk_id
        UNION ALL
        SELECT anchor_chunk_id AS source_anchor_id, anchor_chunk_id, 'sparse_bm25'::text AS anchor_retriever
        FROM unnest(@sparse_anchor_chunk_ids::uuid[]) AS anchor_chunk_id
        UNION ALL
        SELECT anchor_chunk_id AS source_anchor_id, anchor_chunk_id, 'route_context'::text AS anchor_retriever
        FROM unnest(@route_anchor_chunk_ids::uuid[]) AS anchor_chunk_id
        UNION ALL
        SELECT
            exact_anchors.exact_match_entry_id AS source_anchor_id,
            resolved_chunks.retrieval_chunk_id AS anchor_chunk_id,
            'exact_match'::text AS anchor_retriever
        FROM unnest(@exact_anchor_entry_ids::uuid[]) AS exact_anchor_id
        JOIN exact_match_entries exact_anchors
          ON exact_anchors.tenant_id = @tenant_id
         AND exact_anchors.exact_match_entry_id = exact_anchor_id
        JOIN LATERAL (
            SELECT rc.retrieval_chunk_id
            FROM retrieval_chunks rc
            WHERE rc.revision_id = exact_anchors.revision_id
              AND (
                    (exact_anchors.unit_id IS NOT NULL AND rc.unit_id = exact_anchors.unit_id)
                 OR (exact_anchors.unit_id IS NULL
                     AND exact_anchors.section_id IS NOT NULL
                     AND rc.section_id = exact_anchors.section_id
                     AND rc.page_start <= exact_anchors.page_end
                     AND rc.page_end >= exact_anchors.page_start)
              )
            ORDER BY
                CASE
                    WHEN exact_anchors.unit_id IS NOT NULL AND rc.unit_id = exact_anchors.unit_id THEN 0
                    ELSE 1
                END,
                rc.chunk_index ASC
            LIMIT 1
        ) AS resolved_chunks ON TRUE
    ) anchors
)
SELECT
    d.doc_id AS "DocId",
    d.doc_path AS "DocPath",
    d.doc_name AS "DocName",
    d.category AS "Category",
    rc.page_start AS "PageStart",
    rc.page_end AS "PageEnd",
    CASE
        WHEN NULLIF(rc.metadata->>'offsetStart', '') ~ '^[-+]?[0-9]+$'
            THEN CASE
                WHEN (rc.metadata->>'offsetStart')::numeric BETWEEN -2147483648 AND 2147483647
                    THEN (rc.metadata->>'offsetStart')::int
                ELSE NULL
            END
        ELSE NULL
    END AS "OffsetStart",
    CASE
        WHEN NULLIF(rc.metadata->>'offsetEnd', '') ~ '^[-+]?[0-9]+$'
            THEN CASE
                WHEN (rc.metadata->>'offsetEnd')::numeric BETWEEN -2147483648 AND 2147483647
                    THEN (rc.metadata->>'offsetEnd')::int
                ELSE NULL
            END
        ELSE NULL
    END AS "OffsetEnd",
    rc.retrieval_chunk_id AS "ChunkId",
    rc.chunk_index AS "ChunkIndex",
    rc.text_content AS "Text",
    d.indexed_version AS "IngestionVersion",
    LOWER(ENCODE(d.content_hash, 'hex')) AS "HashDoc",
    COALESCE(rc.metadata->>'chunkType', 'linked_context_v1') AS "ChunkType",
    COALESCE(rc.metadata->>'contentRole', 'content') AS "ContentRole",
    rc.metadata->>'navigationReason' AS "NavigationReason",
    rc.metadata->>'originalChunkType' AS "OriginalChunkType",
    CASE
        WHEN NULLIF(rc.metadata->>'navigationScore', '') ~ '^[-+]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?:[eE][-+]?[0-9]+)?$'
            THEN (rc.metadata->>'navigationScore')::double precision
        ELSE NULL
    END AS "NavigationScore",
    CASE
        WHEN NULLIF(rc.metadata->>'contentDensityScore', '') ~ '^[-+]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?:[eE][-+]?[0-9]+)?$'
            THEN (rc.metadata->>'contentDensityScore')::double precision
        ELSE NULL
    END AS "ContentDensityScore",
    COALESCE(rc.metadata->>'sectionTitle', s.title) AS "SectionTitle",
    COALESCE(rc.metadata->>'headingPath', s.title) AS "HeadingPath",
    l.link_type AS "LinkType",
    a.source_anchor_id AS "AnchorSourceId",
    l.retrieval_chunk_id AS "AnchorChunkId",
    rc.metadata->>'prevChunkId' AS "PrevChunkId",
    rc.metadata->>'nextChunkId' AS "NextChunkId",
    rc.metadata->>'sameSectionChunkId' AS "SameSectionChunkId"
FROM requested_anchors a
JOIN retrieval_chunk_links l
  ON l.tenant_id = @tenant_id
 AND l.retrieval_chunk_id = a.anchor_chunk_id
JOIN retrieval_chunks rc
  ON rc.retrieval_chunk_id = l.linked_chunk_id
JOIN document_revisions r
  ON r.revision_id = rc.revision_id
JOIN documents d
  ON d.tenant_id = r.tenant_id
 AND d.doc_id = r.doc_id
 AND d.status = 'indexed'
 AND d.indexed_version = r.indexed_version
LEFT JOIN document_sections s
  ON s.section_id = rc.section_id
WHERE (@category IS NULL OR LOWER(d.category) = @category)
  AND (@category_path IS NULL OR d.doc_path = @category_path OR d.doc_path LIKE (@category_path || '/%'))
  AND (@doc_id IS NULL OR d.doc_id = @doc_id)
  AND (@doc_path IS NULL OR d.doc_path = @doc_path)
ORDER BY
    CASE l.link_type
        WHEN 'same_section' THEN 0
        WHEN 'next' THEN 1
        ELSE 2
    END,
    rc.chunk_index ASC
LIMIT @top_k;
""";

        var normalizedDocPath = string.IsNullOrWhiteSpace(docPath)
            ? null
            : docPath.Trim().Replace('\\', '/').TrimStart('/');
        var normalizedCategoryPath = NormalizeRagCategoryPathForSql(categoryPath);
        Guid? normalizedDocId = Guid.TryParse(docId, out var parsedDocId) ? parsedDocId : null;

        var rows = await conn.QueryAsync<LinkedMatchRow>(new CommandDefinition(sql, new
        {
            tenant_id = tenantId,
                dense_anchor_chunk_ids = denseAnchorIds,
                linked_anchor_chunk_ids = linkedAnchorIds,
                sparse_anchor_chunk_ids = sparseAnchorIds,
                route_anchor_chunk_ids = routeAnchorIds,
                exact_anchor_entry_ids = exactAnchorIds,
            category,
            category_path = normalizedCategoryPath,
            doc_id = normalizedDocId,
            doc_path = normalizedDocPath,
            top_k = topK * 3
        }, cancellationToken: ct));

        var linkedMatches = rows.Select(row =>
        {
            anchorsBySourceId.TryGetValue(row.AnchorSourceId, out var anchor);
            return new RagMatch(
                Score: ComputeLinkedMatchScore(anchor?.AnchorScore ?? 0.0, row.LinkType, anchor?.SourceRetriever),
                DocId: row.DocId.ToString(),
                DocPath: row.DocPath,
                DocName: row.DocName,
                Category: row.Category,
                PageStart: row.PageStart,
                PageEnd: row.PageEnd,
                OffsetStart: row.OffsetStart,
                OffsetEnd: row.OffsetEnd,
                ChunkId: row.ChunkId.ToString(),
                ChunkIndex: row.ChunkIndex,
                Text: row.Text,
                IngestionVersion: row.IngestionVersion,
                HashDoc: row.HashDoc,
                EmbedText: row.Text,
                EmbeddingBasis: "linked_context_v1",
                SectionOrdinal: null,
                UnitOrdinal: null,
                SectionTitle: row.SectionTitle,
                HeadingPath: row.HeadingPath,
                ChunkType: row.ChunkType,
                ContentRole: row.ContentRole,
                NavigationReason: row.NavigationReason,
                OriginalChunkType: row.OriginalChunkType,
                NavigationScore: row.NavigationScore,
                ContentDensityScore: row.ContentDensityScore,
                PrevChunkId: row.PrevChunkId,
                NextChunkId: row.NextChunkId,
                SameSectionChunkId: row.SameSectionChunkId);
        }).ToList();

        var cardQuery = string.Join(
            ' ',
            anchorMatches
                .OrderByDescending(static match => match.Score)
                .Take(6)
                .Select(static match => $"{match.SectionTitle} {match.HeadingPath} {match.Text}"));
        return await AttachDocumentProfileContentCardsAsync(ds, tenantId, linkedMatches, cardQuery, ct);
    }

    /// <summary>
    /// CDC v3.1 §11.3: autocut - detect the largest relative score drop between consecutive
    /// results and trim everything after the gap, provided the absolute threshold is also met.
    /// Keeps at least 1 result. Only cuts if the gap is significant (>= 15% relative drop).
    /// </summary>
    internal static void ApplyAutocut(List<RagMatch> matches, double absoluteMinScore)
    {
        if (matches.Count <= 2)
            return;

        var bestGapIndex = -1;
        var bestGapRatio = 0.0;
        const double minRelativeDrop = 0.15;
        const int minKeepBeforeAutocut = 8;

        for (var i = 1; i < matches.Count; i++)
        {
            var prev = matches[i - 1].Score;
            var curr = matches[i].Score;
            if (prev <= 0.0)
                continue;

            var drop = (prev - curr) / prev;
            if (drop > bestGapRatio)
            {
                bestGapRatio = drop;
                bestGapIndex = i;
            }
        }

        if (bestGapRatio >= minRelativeDrop && bestGapIndex >= minKeepBeforeAutocut)
            matches.RemoveRange(bestGapIndex, matches.Count - bestGapIndex);

        // Also enforce absolute minimum on remaining items (skip exact_match which always passes)
        matches.RemoveAll(m =>
            m.Score < absoluteMinScore
            && !string.Equals(m.EmbeddingBasis, "exact_match_v1", StringComparison.Ordinal));
    }

    internal static string BuildMatchDedupKey(RagMatch match)
    {
        var normalizedText = ExactMatchEntryExtractor.NormalizeForLookup(match.Text ?? string.Empty);
        var textKey = normalizedText.Length <= 512
            ? normalizedText
            : $"{normalizedText.Length}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedText)))}";
        return $"{match.DocId}|{match.PageStart}|{match.PageEnd}|{textKey}";
    }

    internal static bool IsNearDuplicatePageOverlap(RagMatch left, RagMatch right)
    {
        if (left is null || right is null)
            return false;
        if (string.IsNullOrWhiteSpace(left.DocPath) || string.IsNullOrWhiteSpace(right.DocPath))
            return false;
        if (!string.Equals(left.DocPath, right.DocPath, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!PageRangesOverlap(left.PageStart, left.PageEnd, right.PageStart, right.PageEnd))
            return false;

        var leftText = ExactMatchEntryExtractor.NormalizeForLookup(left.Text ?? string.Empty);
        var rightText = ExactMatchEntryExtractor.NormalizeForLookup(right.Text ?? string.Empty);
        if (leftText.Length < 120 || rightText.Length < 120)
            return false;

        if (leftText.Contains(rightText, StringComparison.Ordinal) || rightText.Contains(leftText, StringComparison.Ordinal))
            return true;

        return ComputeTokenJaccard(leftText, rightText) >= 0.82;
    }

    private static bool PageRangesOverlap(int? leftStart, int? leftEnd, int? rightStart, int? rightEnd)
    {
        if (leftStart is not > 0 || rightStart is not > 0)
            return false;

        var lEnd = Math.Max(leftStart.Value, leftEnd ?? leftStart.Value);
        var rEnd = Math.Max(rightStart.Value, rightEnd ?? rightStart.Value);
        return leftStart.Value <= rEnd && rightStart.Value <= lEnd;
    }

    private static double ComputeTokenJaccard(string leftText, string rightText)
    {
        var leftTokens = leftText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length >= 4)
            .Take(160)
            .ToHashSet(StringComparer.Ordinal);
        var rightTokens = rightText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length >= 4)
            .Take(160)
            .ToHashSet(StringComparer.Ordinal);

        if (leftTokens.Count == 0 || rightTokens.Count == 0)
            return 0.0;

        var intersection = leftTokens.Count(token => rightTokens.Contains(token));
        var union = leftTokens.Count + rightTokens.Count - intersection;
        return union <= 0 ? 0.0 : (double)intersection / union;
    }

    internal static List<RagMatch> FuseWithRrf(
        IReadOnlyList<RagMatch> exactMatches,
        IReadOnlyList<RagMatch> sparseMatches,
        IReadOnlyList<RagMatch> denseMatches,
        IReadOnlyList<RagMatch>? profileMatches = null,
        IReadOnlyList<RagMatch>? titleAnchorRouteMatches = null,
        int rrfK = 60)
    {
        var accumulators = new Dictionary<string, RrfAccumulator>(StringComparer.OrdinalIgnoreCase);

        AccumulateRrf(accumulators, exactMatches, rrfK);
        if (titleAnchorRouteMatches is { Count: > 0 })
            AccumulateRrf(accumulators, titleAnchorRouteMatches, rrfK);
        AccumulateRrf(accumulators, sparseMatches, rrfK);
        AccumulateRrf(accumulators, denseMatches, rrfK);
        if (profileMatches is { Count: > 0 })
            AccumulateRrf(accumulators, profileMatches, rrfK);

        if (accumulators.Count == 0)
            return [];

        var maxRrf = accumulators.Values.Max(item => item.Score);
        return accumulators
            .Values
            .Select(item =>
            {
                var normalizedRrf = maxRrf > 0 ? item.Score / maxRrf : 0.0;
                var baseScore = Math.Clamp(item.Representative.Score, 0.0, 1.02);
                var blendedScore = Math.Min(1.02, (normalizedRrf * 0.65) + (baseScore * 0.35));
                return item.Representative with { Score = blendedScore };
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.DocPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ChunkIndex)
            .ToList();
    }

    private sealed record CalibrationCandidate(
        RagMatch Match,
        string Retriever,
        string TextForSignals,
        string TitleSignalText,
        string NormalizedTextForSignals,
        string NormalizedTitleSignalText,
        string NormalizedMatchText);

    internal static List<RagMatch> CalibrateFusedMatches(string query, IReadOnlyList<RagMatch> candidates, string? originalQuery = null)
    {
        if (candidates.Count <= 1)
            return candidates.ToList();

        var rankingQuery = originalQuery ?? query;
        var normalizedWhole = ExactMatchEntryExtractor.NormalizeForLookup(query);
        var referenceTerms = ExactMatchEntryExtractor.ExtractLookupTerms(query)
            .Where(term => !string.Equals(term, normalizedWhole, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var lexicalTokens = ExtractLexicalQueryTokens(query);
        var phraseTerms = BuildLexicalContentFallbackTerms(query)
            .Where(static term => term.Contains(' '))
            .Take(16)
            .ToArray();
        var quotedPhrases = ExtractQuotedLookupPhrases(rankingQuery);
        var documentHintTokens = ExtractDocumentHintTokens(rankingQuery);
        var comparativeSubjectTokens = ExtractComparativeSubjectAnchorTokens(rankingQuery);
        var titleScoringQuery = string.IsNullOrWhiteSpace(query) ? rankingQuery : query;
        var titleTokens = ExtractLexicalQueryTokens(titleScoringQuery)
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .Take(9)
            .ToArray();
        var normalizedTitleScoringQuery = NormalizeForLexicalSignal(titleScoringQuery);
        var titleScoringEnabled = !(quotedPhrases.Count == 0 && ContainsExactTitleActionMarker(rankingQuery));
        var hasReferenceLikeQueryToken = HasReferenceLikeQueryToken(rankingQuery);
        var calibrationCandidates = candidates
            .Select(match =>
            {
                var textForSignals = GetLexicalSignalText(match);
                var titleSignalText = GetTitleSignalText(match);
                return new CalibrationCandidate(
                    match,
                    ResolveRetriever(match),
                    textForSignals,
                    titleSignalText,
                    NormalizeForLexicalSignal(textForSignals),
                    NormalizeForLexicalSignal(titleSignalText),
                    NormalizeForLexicalSignal(match.Text));
            })
            .ToList();
        var useSpecificCoverageTitlePriority = ShouldApplySpecificCoverageTitlePriority(
            rankingQuery,
            lexicalTokens,
            comparativeSubjectTokens);
        var hasLexicalAnchor = lexicalTokens.Count >= 1
            && calibrationCandidates.Any(item =>
            {
                if (string.Equals(item.Retriever, "dense_qdrant", StringComparison.Ordinal)
                    || string.Equals(item.Retriever, "linked_context", StringComparison.Ordinal))
                    return false;

                return item.Match.Score >= 0.70
                    && ComputeLexicalCoverageInNormalizedText(lexicalTokens, item.NormalizedTextForSignals) >= 0.5;
            });

        return calibrationCandidates
            .Select(item =>
            {
                var match = item.Match;
                var retriever = item.Retriever;
                var adjusted = match.Score;
                var lexicalCoverage = lexicalTokens.Count > 0
                    ? ComputeLexicalCoverageInNormalizedText(lexicalTokens, item.NormalizedTextForSignals)
                    : 0.0;
                var exactTitleScore = titleScoringEnabled
                    ? ComputeExactTitleCandidateScoreCore(
                        match,
                        item.NormalizedTitleSignalText,
                        item.NormalizedMatchText,
                        titleTokens,
                        phraseTerms,
                        normalizedTitleScoringQuery)
                    : 0.0;
                var quotedLookupScore = quotedPhrases.Count > 0
                    ? ComputeQuotedLookupCandidateScore(
                        quotedPhrases,
                        string.Join("\n", new[]
                        {
                            item.TextForSignals,
                            match.Text,
                            match.SectionTitle,
                            match.HeadingPath
                        }.Where(static value => !string.IsNullOrWhiteSpace(value))))
                    : 0.0;
                var specificAnchorCount = CountSpecificLexicalAnchorsInNormalizedText(lexicalTokens, item.NormalizedTextForSignals);
                var requiresComparativeSubjectAnchor = comparativeSubjectTokens.Count > 0;
                var requiresPrimarySpecificAnchor = !requiresComparativeSubjectAnchor
                    && RequiresPrimarySpecificLexicalAnchor(lexicalTokens);
                var containsPrimarySpecificAnchor = !requiresPrimarySpecificAnchor
                    || ContainsPrimarySpecificLexicalAnchorInNormalizedText(lexicalTokens, item.NormalizedTextForSignals);
                var containsComparativeSubjectAnchor = !requiresComparativeSubjectAnchor
                    || ContainsComparativeSubjectAnchorInNormalizedText(comparativeSubjectTokens, item.NormalizedTextForSignals);
                var hasClosePhraseMatch = phraseTerms.Length > 0
                    && phraseTerms.Any(term => ContainsOrderedPhraseWindowInNormalizedText(item.NormalizedTextForSignals, term, maxGapChars: 40));
                var matchedCardTitleSignal = ComputeMatchedContentCardTitleSignal(
                    match,
                    titleTokens,
                    normalizedTitleScoringQuery);
                var matchesDocumentHint = DocumentMatchesHint(documentHintTokens, match);
                var strongExactReferenceSignal = hasReferenceLikeQueryToken
                    && referenceTerms.Length > 0
                    && string.Equals(retriever, "exact_match", StringComparison.Ordinal)
                    && (string.Equals(match.ChunkType, "exact_match_entry", StringComparison.Ordinal)
                        || string.Equals(match.ChunkType, "document_metadata_ref", StringComparison.Ordinal));

                if (referenceTerms.Length > 0)
                {
                    if (string.Equals(retriever, "exact_match", StringComparison.Ordinal))
                    {
                        adjusted += string.Equals(match.ChunkType, "document_metadata_ref", StringComparison.Ordinal)
                            ? 0.03
                            : 0.02;
                    }
                    else if (!string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal))
                    {
                        adjusted -= 0.01;
                    }
                }

                if (documentHintTokens.Count > 0)
                {
                    if (matchesDocumentHint)
                    {
                        adjusted += retriever switch
                        {
                            "exact_match" => 0.20,
                            "title_anchor_route" => 0.18,
                            "navigation_route" => 0.16,
                            "direct_title_token_route" => 0.16,
                            "fuzzy_title_lead" => 0.15,
                            "sparse_bm25" => 0.18,
                            "dense_qdrant" => 0.14,
                            "document_profile" => 0.12,
                            _ => 0.10
                        };
                    }
                    else if (!string.Equals(retriever, "linked_context", StringComparison.Ordinal))
                    {
                        adjusted -= retriever switch
                        {
                            "dense_qdrant" => 0.10,
                            "sparse_bm25" => 0.08,
                            "document_profile" => 0.06,
                            _ => 0.05
                        };
                    }
                }

                if (lexicalTokens.Count > 0)
                {
                    if (string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal))
                    {
                        adjusted += lexicalCoverage switch
                        {
                            >= 0.80 => 0.06,
                            >= 0.50 => 0.035,
                            >= 0.34 => 0.015,
                            _ when lexicalTokens.Count >= 4 => -0.08,
                            _ when lexicalTokens.Count >= 3 => -0.04,
                            _ => 0.0
                        };
                    }
                    else if (string.Equals(retriever, "dense_qdrant", StringComparison.Ordinal))
                    {
                        adjusted += lexicalCoverage switch
                        {
                            >= 0.80 => 0.025,
                            >= 0.50 => 0.012,
                            < 0.20 when lexicalTokens.Count >= 2 => -0.03,
                            _ => 0.0
                        };
                    }
                }

                if (exactTitleScore > 0.0)
                {
                    adjusted += retriever switch
                    {
                        "sparse_bm25" => Math.Min(0.32, 0.12 + (exactTitleScore * 0.006)),
                        "title_anchor_route" => Math.Min(0.30, 0.12 + (exactTitleScore * 0.006)),
                        "navigation_route" => Math.Min(0.26, 0.10 + (exactTitleScore * 0.005)),
                        "direct_title_token_route" => Math.Min(0.28, 0.11 + (exactTitleScore * 0.005)),
                        "fuzzy_title_lead" => Math.Min(0.24, 0.09 + (exactTitleScore * 0.005)),
                        "dense_qdrant" => Math.Min(0.16, 0.06 + (exactTitleScore * 0.004)),
                        "document_profile" => Math.Min(0.20, 0.08 + (exactTitleScore * 0.004)),
                        "exact_match" => Math.Min(0.18, 0.08 + (exactTitleScore * 0.004)),
                        _ => Math.Min(0.16, 0.06 + (exactTitleScore * 0.004))
                    };

                    if (LooksLikeStructuredAnswerUnit(match))
                        adjusted += 0.06;
                }

                if (matchedCardTitleSignal > 0)
                    adjusted += Math.Min(0.20, 0.05 * matchedCardTitleSignal);

                if (hasClosePhraseMatch)
                {
                    adjusted += retriever switch
                    {
                        "sparse_bm25" => 0.25,
                        "title_anchor_route" => 0.22,
                        "navigation_route" => 0.18,
                        "direct_title_token_route" => 0.20,
                        "fuzzy_title_lead" => 0.18,
                        "dense_qdrant" => 0.12,
                        "document_profile" => 0.08,
                        _ => 0.06
                    };
                }

                if (LooksLikeStructuredAnswerUnit(match)
                    && specificAnchorCount > 0
                    && (hasClosePhraseMatch || lexicalCoverage >= 0.80))
                {
                    adjusted += retriever switch
                    {
                        "sparse_bm25" => 0.16,
                        "dense_qdrant" => 0.0,
                        "document_profile" => 0.06,
                        _ => 0.08
                    };
                }

                if (specificAnchorCount >= 2 && LooksLikeStructuredAnswerChunk(match))
                {
                    adjusted += retriever switch
                    {
                        "sparse_bm25" => 0.18,
                        "dense_qdrant" => 0.0,
                        "document_profile" => 0.06,
                        _ => 0.08
                    };
                }

                if (HasProfileTitleHint(match)
                    && requiresPrimarySpecificAnchor
                    && !containsPrimarySpecificAnchor)
                {
                    adjusted -= retriever switch
                    {
                        "sparse_bm25" => 0.24,
                        "dense_qdrant" => 0.12,
                        "document_profile" => 0.08,
                        _ => 0.10
                    };
                    adjusted = Math.Min(adjusted, string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.78 : 0.74);
                }
                else if (requiresPrimarySpecificAnchor
                    && !containsPrimarySpecificAnchor
                    && !string.Equals(retriever, "exact_match", StringComparison.Ordinal))
                {
                    adjusted -= retriever switch
                    {
                        "sparse_bm25" => 0.14,
                        "dense_qdrant" => 0.08,
                        "document_profile" => 0.06,
                        _ => 0.07
                    };
                    adjusted = Math.Min(adjusted, string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.80 : 0.78);
                }

                if (requiresComparativeSubjectAnchor
                    && !containsComparativeSubjectAnchor
                    && !string.Equals(retriever, "exact_match", StringComparison.Ordinal))
                {
                    adjusted -= retriever switch
                    {
                        "sparse_bm25" => 0.24,
                        "dense_qdrant" => 0.13,
                        "document_profile" => 0.10,
                        _ => 0.12
                    };
                    adjusted = Math.Min(adjusted, string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.72 : 0.70);
                }

                if (HasProfileTitleHint(match)
                    && specificAnchorCount >= 2
                    && containsPrimarySpecificAnchor
                    && (hasClosePhraseMatch || lexicalCoverage >= 0.50))
                {
                    adjusted += retriever switch
                    {
                        "sparse_bm25" => 0.28,
                        "title_anchor_route" => 0.24,
                        "navigation_route" => 0.18,
                        "direct_title_token_route" => 0.20,
                        "fuzzy_title_lead" => 0.18,
                        "dense_qdrant" => 0.0,
                        "document_profile" => 0.08,
                        _ => 0.10
                    };
                }

                if (quotedLookupScore > 0.0)
                    adjusted += Math.Min(0.24, quotedLookupScore * 0.015);

                if (string.Equals(retriever, "dense_qdrant", StringComparison.Ordinal)
                    && hasLexicalAnchor
                    && lexicalCoverage < 0.20)
                    adjusted = Math.Min(adjusted - 0.15, 0.24);

                if (LooksLikeSourceListChunk(match))
                {
                    adjusted -= string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.26 : 0.16;
                    adjusted = Math.Min(adjusted, string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.72 : 0.70);
                }
                else if (LooksLikeDocumentOverviewChunk(match))
                {
                    adjusted -= string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.18 : 0.10;
                    adjusted = Math.Min(adjusted, string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.78 : 0.74);
                }
                else if (LooksLikeNavigationalChunk(match))
                {
                    var strongNavigation = LooksLikeStrongNavigationalChunk(match);
                    adjusted -= retriever switch
                    {
                        "sparse_bm25" when strongNavigation => 0.32,
                        "sparse_bm25" => 0.20,
                        _ when strongNavigation => 0.18,
                        _ => 0.11
                    };
                    adjusted = Math.Min(
                        adjusted,
                        strongNavigation
                            ? (string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.68 : 0.66)
                            : (string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.82 : 0.78));
                }
                else if (!ShouldAllowGlossaryResults(originalQuery ?? query) && LooksLikeGlossaryChunk(match))
                {
                    adjusted -= string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.22 : 0.14;
                    adjusted = Math.Min(adjusted, string.Equals(retriever, "sparse_bm25", StringComparison.Ordinal) ? 0.76 : 0.72);
                }

                return new
                {
                    Match = match with { Score = Math.Clamp(adjusted, 0.0, 1.02) },
                    ExactTitleScore = exactTitleScore,
                    DirectChunkTitleSignal = ComputeDirectChunkTitleSignalCore(
                        item.NormalizedMatchText,
                        titleTokens,
                        normalizedTitleScoringQuery),
                    MatchedCardTitleSignal = matchedCardTitleSignal,
                    QuotedLookupScore = quotedLookupScore,
                    LexicalCoverage = lexicalCoverage,
                    SpecificAnchorCount = specificAnchorCount,
                    StrongExactReferenceSignal = strongExactReferenceSignal,
                    DocumentHintMatched = matchesDocumentHint,
                    StructuredAnswerPriority = GetStructuredAnswerPriority(match)
                };
            })
            .OrderByDescending(static item => item.StrongExactReferenceSignal)
            .ThenByDescending(static item => item.DirectChunkTitleSignal)
            .ThenByDescending(static item => item.MatchedCardTitleSignal)
            .ThenByDescending(item => item.ExactTitleScore > 0.0 ? 1 : 0)
            .ThenByDescending(static item => item.DocumentHintMatched)
            .ThenByDescending(item => useSpecificCoverageTitlePriority && item.SpecificAnchorCount >= 2 ? 1 : 0)
            .ThenByDescending(item => useSpecificCoverageTitlePriority ? item.StructuredAnswerPriority : 0)
            .ThenByDescending(item => useSpecificCoverageTitlePriority ? item.SpecificAnchorCount : 0)
            .ThenByDescending(item => item.ExactTitleScore)
            .ThenByDescending(item => item.Match.Score)
            .ThenByDescending(static item => item.SpecificAnchorCount)
            .ThenByDescending(static item => item.LexicalCoverage)
            .ThenByDescending(static item => item.StructuredAnswerPriority)
            .ThenByDescending(item => item.QuotedLookupScore)
            .ThenBy(item => item.Match.DocPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Match.ChunkIndex)
            .Select(static item => item.Match)
            .ToList();
    }

    internal static bool ShouldSuppressUnanchoredSpecificResults(string query, IReadOnlyList<RagMatch> candidates)
    {
        if (string.IsNullOrWhiteSpace(query) || candidates.Count == 0)
            return false;

        var lexicalTokens = ExtractLexicalQueryTokens(query)
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (lexicalTokens.Length == 0 || lexicalTokens.Length > 2)
            return false;
        if (!lexicalTokens.Any(IsPrimarySpecificLexicalAnchorToken))
            return false;

        return !candidates.Any(match => HasSpecificQueryAnchor(lexicalTokens, match));
    }

    private static bool HasSpecificQueryAnchor(IReadOnlyList<string> lexicalTokens, RagMatch match)
    {
        var signalText = GetLexicalSignalText(match);
        return ContainsPrimarySpecificLexicalAnchor(lexicalTokens, signalText)
            || CountSpecificLexicalAnchors(lexicalTokens, signalText) > 0
            || ComputeLexicalCoverage(lexicalTokens, signalText) >= 0.50;
    }

    internal static List<RagMatch> SuppressNavigationalNoise(string query, IReadOnlyList<RagMatch> candidates)
    {
        if (candidates.Count == 0 || ShouldAllowNavigationalResults(query))
            return candidates.ToList();

        var allowGlossary = ShouldAllowGlossaryResults(query);
        var classified = candidates
            .Select(match =>
            {
                var keepResolvedRoute = IsResolvedTitleOrNavigationRoute(match);
                var isNavigational = !keepResolvedRoute && LooksLikeNavigationalChunk(match);
                var isGlossary = !allowGlossary && LooksLikeGlossaryChunk(match);
                return new
                {
                    Match = match,
                    IsNavigational = isNavigational,
                    IsGlossary = isGlossary
                };
            })
            .ToList();
        return classified
            .Where(static item => !item.IsNavigational && !item.IsGlossary)
            .Select(static item => item.Match)
            .ToList();
    }

    internal static void PruneNavigationalSelections(string query, List<RagMatch> selected)
    {
        if (selected.Count == 0 || ShouldAllowNavigationalResults(query))
            return;

        var allowGlossary = ShouldAllowGlossaryResults(query);
        selected.RemoveAll(match => !IsResolvedTitleOrNavigationRoute(match)
            && (LooksLikeNavigationalChunk(match)
                || (!allowGlossary && LooksLikeGlossaryChunk(match))));
    }

    internal static bool IsResolvedTitleOrNavigationRoute(RagMatch match)
    {
        var retriever = ResolveRetriever(match);
        if (string.IsNullOrWhiteSpace(match.ChunkId))
            return false;

        if (string.Equals(retriever, "title_anchor_route", StringComparison.Ordinal)
            || string.Equals(retriever, "direct_title_token_route", StringComparison.Ordinal)
            || string.Equals(retriever, "fuzzy_title_lead", StringComparison.Ordinal))
            return HasProfileTitleHint(match);

        return string.Equals(retriever, "navigation_route", StringComparison.Ordinal)
            && HasProfileTitleHint(match)
            && NavigationRouteHasTargetTitleEvidence(match);
    }

    private static bool IsContentSelectionCandidate(RagMatch match)
        => IsResolvedTitleOrNavigationRoute(match)
           || !LooksLikeNavigationalChunk(match);

    internal static void PrioritizeQuotedTitleSelections(string query, List<RagMatch> selected)
    {
        if (selected.Count <= 1)
            return;

        var quotedPhrases = ExtractQuotedLookupPhrases(query);
        if (quotedPhrases.Count == 0)
            return;

        var ranked = selected
            .Select(match => new
            {
                Match = match,
                QuotedScore = ComputeQuotedLookupCandidateScore(
                    quotedPhrases,
                    string.Join("\n", new[]
                    {
                        match.EmbedText,
                        match.Text,
                        match.SectionTitle,
                        match.HeadingPath
                    }.Where(static value => !string.IsNullOrWhiteSpace(value))))
            })
            .ToList();

        if (!ranked.Any(static item => item.QuotedScore > 0.0))
            return;

        selected.Clear();
        selected.AddRange(ranked
            .OrderByDescending(static item => item.QuotedScore)
            .ThenByDescending(static item => item.Match.Score)
            .ThenBy(static item => item.Match.DocPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Match.ChunkIndex)
            .Select(static item => item.Match));
    }

    internal static void PrioritizeExactTitleSelections(string query, List<RagMatch> selected)
    {
        if (selected.Count <= 1)
            return;

        var lexicalTokens = ExtractLexicalQueryTokens(query)
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        var useSpecificCoverageTitlePriority = ShouldApplySpecificCoverageTitlePriority(
            query,
            lexicalTokens,
            ExtractComparativeSubjectAnchorTokens(query));
        var ranked = selected
            .Select(match => new
            {
                Match = match,
                ExactTitleScore = ComputeExactTitleCandidateScore(query, match),
                DirectChunkTitleSignal = ComputeDirectChunkTitleSignal(query, match),
                MatchedCardTitleSignal = ComputeMatchedContentCardTitleSignal(
                    match,
                    lexicalTokens,
                    NormalizeForLexicalSignal(query)),
                SpecificAnchorCount = lexicalTokens.Length > 0
                    ? CountSpecificLexicalAnchors(lexicalTokens, GetTitleSignalText(match))
                    : 0,
                StructuredAnswerPriority = GetStructuredAnswerPriority(match)
            })
            .ToList();

        var hasExactTitleSignal = ranked.Any(static item => item.ExactTitleScore > 0.0 || item.MatchedCardTitleSignal > 0);
        var hasFullSpecificCoverage = useSpecificCoverageTitlePriority
            && ranked.Any(static item => item.SpecificAnchorCount >= 2);
        if (!hasExactTitleSignal && !hasFullSpecificCoverage)
            return;

        selected.Clear();
        selected.AddRange(ranked
            .OrderByDescending(static item => item.DirectChunkTitleSignal)
            .ThenByDescending(static item => item.MatchedCardTitleSignal)
            .ThenByDescending(static item => item.ExactTitleScore > 0.0 ? 1 : 0)
            .ThenByDescending(item => useSpecificCoverageTitlePriority && item.SpecificAnchorCount >= 2 ? 1 : 0)
            .ThenByDescending(item => useSpecificCoverageTitlePriority ? item.StructuredAnswerPriority : 0)
            .ThenByDescending(item => useSpecificCoverageTitlePriority ? item.SpecificAnchorCount : 0)
            .ThenByDescending(static item => item.ExactTitleScore)
            .ThenByDescending(static item => item.Match.Score)
            .ThenBy(static item => item.Match.DocPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Match.ChunkIndex)
            .Select(static item => item.Match));
    }

    internal static void PruneWeakTitleExpansionSelections(string query, List<RagMatch> selected)
    {
        if (selected.Count <= 1)
            return;

        var lexicalTokens = ExtractTitlePruneTokens(query);
        if (lexicalTokens.Length < 2)
            return;

        var useTitlePriority = ExtractQuotedLookupPhrases(query).Count > 0
            || ShouldApplySpecificCoverageTitlePriority(
                query,
                lexicalTokens,
                ExtractComparativeSubjectAnchorTokens(query));
        if (!useTitlePriority)
            return;

        var ranked = selected
            .Select(match => new TitleSelectionSignal(
                match,
                ComputeExactTitleCandidateScore(query, match),
                CountSpecificLexicalAnchors(lexicalTokens, GetTitleSignalText(match)),
                HasFullTitleAnchorCoverage(lexicalTokens, match),
                GetStructuredAnswerPriority(match)))
            .ToList();

        var requiredAnchorCount = Math.Min(2, lexicalTokens.Length);
        var strongAnchors = ranked
            .Where(item => item.SpecificAnchorCount >= requiredAnchorCount
                && item.FullTitleCoverage
                && item.StructuredAnswerPriority >= 2)
            .ToArray();
        if (strongAnchors.Length == 0)
        {
            var first = ranked.FirstOrDefault();
            if (first is null
                || first.StructuredAnswerPriority < 2
                || first.SpecificAnchorCount <= 0
                || first.Match.Score < 0.90)
            {
                return;
            }

            strongAnchors = [first];
        }

        var pruned = ranked
            .Where(item => !ShouldPruneWeakTitleExpansion(item, strongAnchors, lexicalTokens))
            .Select(static item => item.Match)
            .ToList();

        if (pruned.Count == selected.Count || pruned.Count == 0)
            return;

        selected.Clear();
        selected.AddRange(pruned);
    }

    private static bool ShouldPruneWeakTitleExpansion(
        TitleSelectionSignal item,
        IReadOnlyList<TitleSelectionSignal> strongAnchors,
        IReadOnlyList<string> lexicalTokens)
    {
        if (item.FullTitleCoverage)
        {
            if (string.Equals(ResolveRetriever(item.Match), "linked_context", StringComparison.Ordinal))
            {
                return !strongAnchors.Any(anchor =>
                    IsUsefulLinkedContextCompanion(lexicalTokens, anchor.Match, item.Match));
            }

            return false;
        }

        var retriever = ResolveRetriever(item.Match);
        if (string.Equals(retriever, "linked_context", StringComparison.Ordinal))
        {
            return !strongAnchors.Any(anchor =>
                IsUsefulLinkedContextCompanion(lexicalTokens, anchor.Match, item.Match));
        }

        foreach (var anchor in strongAnchors)
        {
            if (!string.Equals(anchor.Match.DocPath, item.Match.DocPath, StringComparison.OrdinalIgnoreCase))
                continue;
            if (SharesSpecificSection(anchor.Match, item.Match))
                return false;
            if (IsNearbyPage(anchor.Match, item.Match, maxDistance: 2))
                return true;
        }

        return false;
    }

    private static bool HasFullTitleAnchorCoverage(IReadOnlyList<string> lexicalTokens, RagMatch match)
    {
        if (lexicalTokens.Count < 2)
            return false;

        var titleSignalText = GetTitleSignalText(match);
        if (string.IsNullOrWhiteSpace(titleSignalText))
            return false;

        return ContainsOrderedTitleTokenSubstrings(titleSignalText, lexicalTokens, maxGapChars: 100)
            || ContainsTitleLikeLexicalSequence(
                FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(titleSignalText)),
                lexicalTokens);
    }

    private static bool IsNearbyPage(RagMatch anchor, RagMatch candidate, int maxDistance)
    {
        if (!anchor.PageStart.HasValue || !candidate.PageStart.HasValue)
            return false;

        return Math.Abs(anchor.PageStart.Value - candidate.PageStart.Value) <= maxDistance;
    }

    private static bool IsUsefulLinkedContextCompanion(
        IReadOnlyList<string> lexicalTokens,
        RagMatch anchor,
        RagMatch candidate)
    {
        if (!string.Equals(ResolveRetriever(candidate), "linked_context", StringComparison.Ordinal))
            return false;
        if (!string.Equals(anchor.DocPath, candidate.DocPath, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!IsNearbyPage(anchor, candidate, maxDistance: 2))
            return false;
        if (LooksLikeNavigationalChunk(candidate) || LooksLikeGlossaryChunk(candidate))
            return false;
        if (IsNearDuplicatePageOverlap(anchor, candidate))
            return false;

        if (SharesSpecificSection(anchor, candidate))
            return true;
        if (lexicalTokens.Count == 0)
            return false;

        var directSignalText = GetDirectChunkSignalText(candidate);
        if (string.IsNullOrWhiteSpace(directSignalText))
            return false;

        if (ComputeLexicalCoverage(lexicalTokens, directSignalText) >= 0.50)
            return true;

        var specificAnchors = CountSpecificLexicalAnchors(lexicalTokens, directSignalText);
        if (specificAnchors >= Math.Min(2, lexicalTokens.Count))
            return true;

        var titleTokens = lexicalTokens
            .Where(static token => !SpecificAnchorStopwords.Contains(token))
            .Take(6)
            .ToArray();
        return titleTokens.Length >= 2
            && ContainsOrderedTitleTokenSubstrings(directSignalText, titleTokens, maxGapChars: 80);
    }

    private static bool SharesSpecificSection(RagMatch left, RagMatch right)
    {
        if (!string.IsNullOrWhiteSpace(left.SectionTitle)
            && !string.IsNullOrWhiteSpace(right.SectionTitle)
            && !IsGenericSectionTitle(left.SectionTitle)
            && string.Equals(left.SectionTitle.Trim(), right.SectionTitle.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(left.HeadingPath)
            && !string.IsNullOrWhiteSpace(right.HeadingPath)
            && !IsGenericSectionTitle(left.HeadingPath)
            && string.Equals(left.HeadingPath.Trim(), right.HeadingPath.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool IsGenericSectionTitle(string? value)
    {
        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(value ?? string.Empty));
        return string.IsNullOrWhiteSpace(normalized)
            || normalized is "document" or "documents" or "root" or "page" or "pages" or "untitled" or "sans titre";
    }

    internal static void PruneWeakAdjacentSiblingSelections(string query, List<RagMatch> selected)
    {
        if (selected.Count <= 1)
            return;

        var lexicalTokens = ExtractTitlePruneTokens(query);
        if (lexicalTokens.Length < 2)
            return;

        var anchor = selected.FirstOrDefault(match =>
            match.Score >= 0.90
            && !string.Equals(ResolveRetriever(match), "linked_context", StringComparison.Ordinal)
            && LooksLikeStructuredAnswerChunk(match)
            && CountSpecificLexicalAnchors(lexicalTokens, GetTitleSignalText(match)) > 0);
        if (anchor is null)
            return;

        selected.RemoveAll(match =>
        {
            if (string.Equals(match.ChunkId, anchor.ChunkId, StringComparison.Ordinal)
                && string.Equals(match.DocPath, anchor.DocPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.Equals(match.DocPath, anchor.DocPath, StringComparison.OrdinalIgnoreCase))
                return false;
            if (SharesSpecificSection(anchor, match))
                return false;
            if (!IsNearbyPage(anchor, match, maxDistance: 2))
                return false;
            if (string.Equals(ResolveRetriever(match), "linked_context", StringComparison.Ordinal)
                && IsUsefulLinkedContextCompanion(lexicalTokens, anchor, match))
            {
                return false;
            }

            var coverage = ComputeLexicalCoverage(lexicalTokens, GetTitleSignalText(match));
            return coverage < 0.80;
        });
    }

    internal static void PrunePreciseTitleTailSelections(string query, List<RagMatch> selected)
    {
        if (selected.Count <= 1 || !ShouldConstrainPreciseTitleLookup(query))
            return;

        var lexicalTokens = ExtractTitlePruneTokens(query);
        var requiredAnchorCount = Math.Min(2, lexicalTokens.Length);
        var ranked = selected
            .Select(match => new
            {
                Match = match,
                StrictCoverage = HasStrictTitleAnchorCoverage(lexicalTokens, match),
                TitleAnchorCount = CountTitleAnchorTokens(lexicalTokens, GetPreciseTitleSignalText(match)),
                ExactTitleScore = ComputeExactTitleCandidateScore(query, match),
                DirectChunkTitleSignal = ComputeDirectChunkTitleSignal(query, match),
                StructuredAnswerPriority = GetStructuredAnswerPriority(match)
            })
            .ToList();
        var anchor = ranked
            .Where(item => item.Match.Score >= 0.90
                && item.TitleAnchorCount >= requiredAnchorCount
                && (item.StrictCoverage
                    || item.ExactTitleScore > 0.0
                    || item.StructuredAnswerPriority >= 2
                    || HasProfileTitleHint(item.Match)))
            .OrderByDescending(static item => item.StrictCoverage)
            .ThenByDescending(static item => item.DirectChunkTitleSignal)
            .ThenByDescending(static item => item.ExactTitleScore)
            .ThenByDescending(static item => item.StructuredAnswerPriority)
            .ThenByDescending(static item => item.Match.Score)
            .Select(static item => item.Match)
            .FirstOrDefault();
        if (anchor is null)
            return;

        var anchorIsContentChunk = GetStructuredAnswerPriority(anchor) >= 2;
        selected.RemoveAll(match =>
        {
            if (IsSameChunk(match, anchor))
                return false;

            if (string.Equals(ResolveRetriever(match), "linked_context", StringComparison.Ordinal)
                && IsUsefulLinkedContextCompanion(lexicalTokens, anchor, match))
            {
                return false;
            }

            return !HasStrictTitleAnchorCoverage(lexicalTokens, match)
                || (anchorIsContentChunk && string.Equals(match.ChunkType, "document_profile", StringComparison.Ordinal));
        });
    }

    internal static void PruneUnmatchedPreciseTitleSelections(string query, List<RagMatch> selected)
    {
        if (selected.Count == 0 || !ShouldConstrainPreciseTitleLookup(query))
            return;

        var primaryTokens = ExtractTitlePruneTokens(query)
            .Where(IsPrimarySpecificLexicalAnchorToken)
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();
        if (primaryTokens.Length == 0)
            return;

        var requiredPrimaryAnchorCount = Math.Min(2, primaryTokens.Length);
        var primaryAnchors = selected
            .Where(match => CountPrimarySpecificSelectionAnchors(primaryTokens, match) >= requiredPrimaryAnchorCount)
            .ToArray();
        if (primaryAnchors.Length == 0)
        {
            selected.Clear();
            return;
        }

        selected.RemoveAll(match =>
        {
            if (CountPrimarySpecificSelectionAnchors(primaryTokens, match) >= requiredPrimaryAnchorCount)
                return false;

            return !IsUsefulLinkedSelectionNearPrimaryAnchor(primaryTokens, match, primaryAnchors);
        });
    }

    private static int CountPrimarySpecificSelectionAnchors(IReadOnlyList<string> primaryTokens, RagMatch match)
    {
        if (primaryTokens.Count == 0)
            return 0;

        var titleSignalText = GetPreciseTitleSignalText(match);
        if (string.IsNullOrWhiteSpace(titleSignalText))
            return 0;

        return CountSpecificLexicalAnchors(primaryTokens, titleSignalText);
    }

    private static bool ContainsPrimarySpecificSelectionAnchor(IReadOnlyList<string> primaryTokens, RagMatch match)
    {
        if (primaryTokens.Count == 0)
            return true;

        return ContainsPrimarySpecificLexicalAnchor(primaryTokens, GetPreciseTitleSignalText(match));
    }

    internal static void PruneUnpagedProfileSelectionsForPreciseLookup(string query, List<RagMatch> selected)
    {
        if (selected.Count <= 1)
            return;

        var lexicalTokens = ExtractTitlePruneTokens(query);
        if (lexicalTokens.Length is < 2 or > 6)
            return;

        var hasPagedTitleEvidence = selected.Any(match =>
            match.PageStart.HasValue
            && (ComputeExactTitleCandidateScore(query, match) > 0.0
                || ContainsOrderedTitleTokenSubstrings(GetTitleSignalText(match), lexicalTokens, maxGapChars: 60)));
        if (!hasPagedTitleEvidence)
            return;

        selected.RemoveAll(static match =>
            !match.PageStart.HasValue
            && string.Equals(match.ChunkType, "document_profile", StringComparison.Ordinal));
    }

    private static bool IsUsefulLinkedSelectionNearPrimaryAnchor(
        IReadOnlyList<string> primaryTokens,
        RagMatch match,
        IReadOnlyList<RagMatch> primaryAnchors)
    {
        if (!string.Equals(ResolveRetriever(match), "linked_context", StringComparison.Ordinal))
            return false;
        if (LooksLikeNavigationalChunk(match) || LooksLikeGlossaryChunk(match))
            return false;

        return primaryAnchors.Any(anchor =>
            IsUsefulLinkedContextCompanion(primaryTokens, anchor, match));
    }

    private static bool IsSameChunk(RagMatch left, RagMatch right)
        => string.Equals(left.ChunkId, right.ChunkId, StringComparison.Ordinal)
           && string.Equals(left.DocPath, right.DocPath, StringComparison.OrdinalIgnoreCase);

    private static int CountTitleAnchorTokens(IReadOnlyList<string> lexicalTokens, string? candidateText)
    {
        if (lexicalTokens.Count == 0 || string.IsNullOrWhiteSpace(candidateText))
            return 0;

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(candidateText));
        var matched = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in lexicalTokens)
        {
            foreach (var variant in BuildLexicalTokenVariants(token))
            {
                var foldedVariant = FoldDiacritics(variant);
                if (foldedVariant.Length >= 4
                    && normalized.Contains(foldedVariant, StringComparison.Ordinal))
                {
                    if (seen.Add(token))
                        matched++;
                    break;
                }
            }
        }

        return matched;
    }

    private static bool HasStrictTitleAnchorCoverage(IReadOnlyList<string> lexicalTokens, RagMatch match)
    {
        if (lexicalTokens.Count < 2)
            return false;

        var titleSignalText = GetPreciseTitleSignalText(match);
        if (string.IsNullOrWhiteSpace(titleSignalText))
            return false;

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(titleSignalText));
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return ContainsTitleLikeLexicalSequence(normalized, lexicalTokens);
    }

    private static string GetPreciseTitleSignalText(RagMatch match)
    {
        return string.Join("\n", new[]
        {
            ExtractMatchedRouteOrProfileTitle(match.EmbedText),
            match.MatchedContentCards is { Count: > 0 }
                ? string.Join("\n", match.MatchedContentCards.Take(MaxCalibrationCardCount).Select(static card => card.Title))
                : null,
            match.Text,
            match.SectionTitle,
            match.HeadingPath
        }.Where(static value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string? ExtractMatchedRouteOrProfileTitle(string? embedText)
    {
        if (string.IsNullOrWhiteSpace(embedText))
            return null;

        string[] prefixes =
        [
            "Matched profile title:",
            "Matched quoted title:",
            "Matched title_anchor_route:",
            "Matched navigation_route:",
            "Matched direct_title_token_route:",
            "Matched fuzzy_title_lead:"
        ];
        var prefix = prefixes.FirstOrDefault(prefix => embedText.StartsWith(prefix, StringComparison.Ordinal));
        if (prefix is null)
            return null;

        var start = prefix.Length;
        var lineEnd = embedText.IndexOf('\n', start);
        if (lineEnd < 0)
        {
            var documentMarker = FindFirstMarker(embedText, start, [" Document:", " document_name:"]);
            lineEnd = documentMarker >= 0 ? documentMarker : embedText.Length;
        }

        return embedText[start..lineEnd].Trim();
    }

    private static int FindFirstMarker(string value, int startIndex, IReadOnlyList<string> markers)
    {
        var first = -1;
        foreach (var marker in markers)
        {
            var index = value.IndexOf(marker, startIndex, StringComparison.Ordinal);
            if (index >= 0 && (first < 0 || index < first))
                first = index;
        }

        return first;
    }

    private static string[] ExtractTitlePruneTokens(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query));
        if (string.IsNullOrWhiteSpace(normalized))
            return [];

        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => (token.Length >= 4 && token.Any(char.IsLetter)) || token.Any(char.IsDigit))
            .Where(static token => !TitleConnectorTokens.Contains(token))
            .Where(static token => !LexicalStopwords.Contains(token))
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
    }

    private static bool ShouldAllowNavigationalResults(string query)
    {
        var normalized = $" {NormalizeQuery(FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query))).ToLowerInvariant()} ";
        return ContainsAny(normalized,
            " index ",
            " sommaire ",
            " table des matieres ",
            " table of contents ",
            " inventaire ",
            " corpus ",
            " vue d ensemble ",
            " overview ",
            " quels livres ",
            " quelles sources ",
            " quels documents ")
            || LooksLikeGenericNavigationalRequest(normalized);
    }

    private static bool LooksLikeGenericNavigationalRequest(string normalizedPaddedQuery)
    {
        if (string.IsNullOrWhiteSpace(normalizedPaddedQuery))
            return false;

        return System.Text.RegularExpressions.Regex.IsMatch(
            normalizedPaddedQuery,
            @"\b(?:liste|list|catalogue|catalog|inventaire|inventory|index)\s+(?:des?|de|du|d['’]?|of|for)?\s*[\p{L}\p{N}][\p{L}\p{N}\s\-_]{2,80}\b|\b[\p{L}\p{N}][\p{L}\p{N}\s\-_]{2,80}\s+(?:liste|list|catalogue|catalog|inventory|index)\b",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static string NormalizeForLexicalSignal(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(value));

    internal static bool ContainsOrderedPhraseWindow(string? candidateText, string phrase, int maxGapChars)
    {
        if (string.IsNullOrWhiteSpace(candidateText) || string.IsNullOrWhiteSpace(phrase))
            return false;

        var normalizedPhrase = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(phrase));
        return ContainsOrderedPhraseWindowInNormalizedText(
            NormalizeForLexicalSignal(candidateText),
            normalizedPhrase,
            maxGapChars);
    }

    private static bool ContainsOrderedPhraseWindowInNormalizedText(string normalizedCandidate, string normalizedPhrase, int maxGapChars)
    {
        if (string.IsNullOrWhiteSpace(normalizedCandidate) || string.IsNullOrWhiteSpace(normalizedPhrase))
            return false;

        var tokens = normalizedPhrase
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length >= 2)
            .ToArray();
        if (tokens.Length < 2)
            return false;

        var searchFrom = 0;
        var previousEnd = -1;
        foreach (var token in tokens)
        {
            var index = normalizedCandidate.IndexOf(token, searchFrom, StringComparison.Ordinal);
            if (index < 0)
                return false;

            if (previousEnd >= 0 && index - previousEnd > maxGapChars)
                return false;

            previousEnd = index + token.Length;
            searchFrom = previousEnd;
        }

        return true;
    }

    private const int MaxCalibrationProfileSignalChars = 24000;
    private const int MaxCalibrationCardSignalChars = 8000;
    private const int MaxCalibrationCardCount = 32;

    private static string GetLexicalSignalText(RagMatch match)
    {
        if (IsDocumentProfileMatch(match))
            return BuildDocumentProfileCalibrationSignal(match, includeEmbedFallback: true);

        return string.IsNullOrWhiteSpace(match.EmbedText)
            ? match.Text ?? string.Empty
            : match.EmbedText!;
    }

    private static string GetDirectChunkSignalText(RagMatch match)
        => string.Join("\n", new[]
        {
            match.Text,
            match.SectionTitle,
            match.HeadingPath
        }.Where(static value => !string.IsNullOrWhiteSpace(value)));

    private static string GetTitleSignalText(RagMatch match)
    {
        if (IsDocumentProfileMatch(match))
            return BuildDocumentProfileCalibrationSignal(match, includeEmbedFallback: true);

        var includeEmbedText = string.Equals(match.ChunkType, "document_profile", StringComparison.Ordinal)
            || HasProfileTitleHint(match);
        return string.Join("\n", new[]
        {
            includeEmbedText ? match.EmbedText : null,
            match.Text,
            match.SectionTitle,
            match.HeadingPath
        }.Where(static value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string BuildDocumentProfileCalibrationSignal(RagMatch match, bool includeEmbedFallback)
    {
        var sb = new StringBuilder();
        AppendCalibrationSignalPart(sb, match.Text, maxChars: 5000);
        AppendCalibrationSignalPart(sb, match.SectionTitle, maxChars: 500);
        AppendCalibrationSignalPart(sb, match.HeadingPath, maxChars: 500);

        if (match.MatchedContentCards is { Count: > 0 })
        {
            var cardsSb = new StringBuilder();
            foreach (var card in match.MatchedContentCards.Take(MaxCalibrationCardCount))
            {
                AppendCalibrationSignalPart(cardsSb, card.Title, maxChars: 500);
                AppendCalibrationSignalPart(cardsSb, card.Kind, maxChars: 120);
                if (card.Signals is { Count: > 0 })
                {
                    foreach (var signal in card.Signals.Take(12))
                        AppendCalibrationSignalPart(cardsSb, signal, maxChars: 240);
                }

                if (cardsSb.Length >= MaxCalibrationCardSignalChars)
                    break;
            }

            AppendCalibrationSignalPart(sb, cardsSb.ToString(), MaxCalibrationCardSignalChars);
        }

        if (includeEmbedFallback)
            AppendCalibrationSignalPart(sb, match.EmbedText, MaxCalibrationProfileSignalChars);

        return sb.ToString();
    }

    private static void AppendCalibrationSignalPart(StringBuilder sb, string? value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value) || maxChars <= 0)
            return;

        if (sb.Length > 0)
            sb.Append('\n');

        var trimmed = value.Trim();
        if (trimmed.Length <= maxChars)
        {
            sb.Append(trimmed);
            return;
        }

        var headChars = Math.Max(1, maxChars / 2);
        var tailChars = Math.Max(1, maxChars - headChars);
        sb.Append(trimmed, 0, headChars);
        sb.Append('\n');
        sb.Append(trimmed, trimmed.Length - tailChars, tailChars);
    }

    internal static bool HasProfileTitleHint(RagMatch match)
    {
        if (string.IsNullOrWhiteSpace(match.EmbedText))
            return false;

        if (match.EmbedText!.StartsWith("Matched navigation_route:", StringComparison.Ordinal))
            return NavigationRouteHasTargetTitleEvidence(match);

        return match.EmbedText.StartsWith("Matched profile title:", StringComparison.Ordinal)
            || match.EmbedText.StartsWith("Matched quoted title:", StringComparison.Ordinal)
            || match.EmbedText.StartsWith("Matched title_anchor_route:", StringComparison.Ordinal)
            || match.EmbedText.StartsWith("Matched direct_title_token_route:", StringComparison.Ordinal)
            || match.EmbedText.StartsWith("Matched fuzzy_title_lead:", StringComparison.Ordinal);
    }

    internal static bool NavigationRouteHasTargetTitleEvidence(RagMatch match)
    {
        if (!IsNavigationRouteMatch(match))
            return true;

        var routeTitle = ExtractMatchedRouteOrProfileTitle(match.EmbedText);
        if (string.IsNullOrWhiteSpace(routeTitle))
            return false;

        var directSignal = GetDirectChunkSignalText(match);
        if (string.IsNullOrWhiteSpace(directSignal))
            return false;

        var normalizedRouteTitle = NormalizeNavigationRouteEvidenceTitle(routeTitle);
        if (string.IsNullOrWhiteSpace(normalizedRouteTitle))
            return false;

        var normalizedDirect = NormalizeForLexicalSignal(directSignal);
        if (string.IsNullOrWhiteSpace(normalizedDirect))
            return false;

        if (normalizedRouteTitle.Length >= 4
            && normalizedDirect.Contains(normalizedRouteTitle, StringComparison.Ordinal))
        {
            return true;
        }

        var titleTokens = normalizedRouteTitle
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length >= 3 || token.Any(char.IsDigit))
            .Where(static token => !TitleConnectorTokens.Contains(token))
            .Where(static token => !LexicalStopwords.Contains(token))
            .Where(static token => !SpecificAnchorStopwords.Contains(token))
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();

        if (titleTokens.Length == 1)
            return ContainsExactNormalizedToken(normalizedDirect, titleTokens[0]);

        return titleTokens.Length >= 2
            && ContainsTitleLikeLexicalSequence(normalizedDirect, titleTokens);
    }

    private static bool IsNavigationRouteMatch(RagMatch match)
        => string.Equals(ResolveRetriever(match), "navigation_route", StringComparison.Ordinal)
           || string.Equals(match.EmbeddingBasis, "navigation_route_v1", StringComparison.Ordinal);

    internal static int ResolveDirectTitleTokenRouteMinimumOverlap(int queryTokenCount)
    {
        if (queryTokenCount <= 0)
            return 1;

        return queryTokenCount switch
        {
            <= 2 => queryTokenCount,
            <= 4 => 3,
            _ => Math.Max(3, (int)Math.Ceiling(queryTokenCount * 0.60))
        };
    }

    private static string NormalizeNavigationRouteEvidenceTitle(string title)
    {
        var tokens = NormalizeForLexicalSignal(title)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        while (tokens.Count > 1
               && (tokens[0].Length == 1
                   || tokens[0].All(char.IsDigit)
                   || IsFocusedLookupLeadingEdgeToken(tokens[0])))
        {
            tokens.RemoveAt(0);
        }

        return string.Join(' ', tokens);
    }

    internal static bool ContainsSpecificLexicalAnchor(IReadOnlyList<string> lexicalTokens, string? candidateText)
        => CountSpecificLexicalAnchors(lexicalTokens, candidateText) > 0;

    internal static bool RequiresPrimarySpecificLexicalAnchor(IReadOnlyList<string> lexicalTokens)
        => lexicalTokens.Any(IsPrimarySpecificLexicalAnchorToken);

    internal static bool ContainsPrimarySpecificLexicalAnchor(IReadOnlyList<string> lexicalTokens, string? candidateText)
    {
        if (lexicalTokens.Count == 0 || string.IsNullOrWhiteSpace(candidateText))
            return false;

        return ContainsPrimarySpecificLexicalAnchorInNormalizedText(
            lexicalTokens,
            NormalizeForLexicalSignal(candidateText));
    }

    private static bool ContainsPrimarySpecificLexicalAnchorInNormalizedText(IReadOnlyList<string> lexicalTokens, string normalizedCandidate)
    {
        if (lexicalTokens.Count == 0 || string.IsNullOrWhiteSpace(normalizedCandidate))
            return false;

        var primaryTokens = lexicalTokens
            .Where(IsPrimarySpecificLexicalAnchorToken)
            .OrderByDescending(static token => token.Length)
            .ThenBy(static token => token, StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        if (primaryTokens.Length == 0)
            return true;

        foreach (var token in primaryTokens)
        {
            if (token.Any(char.IsDigit)
                && ContainsExactNormalizedToken(normalizedCandidate, token))
            {
                return true;
            }

            foreach (var variant in BuildLexicalTokenVariants(token))
            {
                var foldedVariant = FoldDiacritics(variant);
                if (foldedVariant.Length >= 5 && ContainsExactNormalizedToken(normalizedCandidate, foldedVariant))
                    return true;
                if (foldedVariant.Length >= 6 && normalizedCandidate.Contains(foldedVariant, StringComparison.Ordinal))
                    return true;
                if (foldedVariant.Length >= 6 && ContainsNearNormalizedToken(normalizedCandidate, foldedVariant))
                    return true;
            }
        }

        return false;
    }

    internal static IReadOnlyList<string> ExtractComparativeSubjectAnchorTokens(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query));
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 3)
            return Array.Empty<string>();

        var markerIndex = Array.FindIndex(tokens, IsComparativeCriterionMarker);
        if (markerIndex <= 0)
            return Array.Empty<string>();

        var start = Math.Max(0, markerIndex - 5);
        return tokens
            .Skip(start)
            .Take(markerIndex - start)
            .Where(IsComparativeSubjectAnchorToken)
            .TakeLast(2)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    internal static bool ContainsComparativeSubjectAnchor(IReadOnlyList<string> subjectTokens, string? candidateText)
    {
        if (subjectTokens.Count == 0)
            return true;
        if (string.IsNullOrWhiteSpace(candidateText))
            return false;

        return ContainsComparativeSubjectAnchorInNormalizedText(subjectTokens, NormalizeForLexicalSignal(candidateText));
    }

    private static bool ContainsComparativeSubjectAnchorInNormalizedText(IReadOnlyList<string> subjectTokens, string normalizedCandidate)
    {
        if (subjectTokens.Count == 0)
            return true;
        if (string.IsNullOrWhiteSpace(normalizedCandidate))
            return false;

        foreach (var token in subjectTokens)
        {
            var variants = BuildLexicalTokenVariants(token)
                .DefaultIfEmpty(token)
                .Distinct(StringComparer.Ordinal);
            if (variants.Any(variant => normalizedCandidate.Contains(FoldDiacritics(variant), StringComparison.Ordinal)))
                return true;
        }

        return false;
    }

    private static bool IsComparativeCriterionMarker(string token)
        => token is "plus" or "most" or "mas" or "mais" or "mehr";

    private static bool IsComparativeSubjectAnchorToken(string token)
        => token.Length >= 3
           && token.Any(char.IsLetter)
           && !LexicalStopwords.Contains(token)
           && !ComparativeSubjectStopwords.Contains(token);

    private static bool IsPrimarySpecificLexicalAnchorToken(string token)
        => !SpecificAnchorStopwords.Contains(token)
            && !PrimaryAnchorStopwords.Contains(token)
            && (token.Length >= 6 || token.Any(char.IsDigit) || IsReferenceLikeLookupTerm(token));

    internal static double ComputeExactTitleCandidateScore(string query, RagMatch match)
    {
        if (string.IsNullOrWhiteSpace(query))
            return 0.0;
        if (ExtractQuotedLookupPhrases(query).Count == 0 && ContainsExactTitleActionMarker(query))
            return 0.0;

        var titleTokens = ExtractLexicalQueryTokens(query)
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .Take(9)
            .ToArray();
        if (titleTokens.Length < 2 || titleTokens.Length > 8)
            return 0.0;

        var candidateText = GetTitleSignalText(match);
        if (string.IsNullOrWhiteSpace(candidateText))
            return 0.0;

        var phraseTerms = BuildLexicalContentFallbackTerms(query)
            .Where(static term => term.Contains(' '))
            .Take(16)
            .ToArray();
        return ComputeExactTitleCandidateScoreCore(
            match,
            NormalizeForLexicalSignal(candidateText),
            NormalizeForLexicalSignal(match.Text),
            titleTokens,
            phraseTerms,
            NormalizeForLexicalSignal(query));
    }

    private static double ComputeExactTitleCandidateScoreCore(
        RagMatch match,
        string normalizedCandidate,
        string normalizedChunkText,
        IReadOnlyList<string> titleTokens,
        IReadOnlyList<string> phraseTerms,
        string normalizedQuery)
    {
        if (titleTokens.Count < 2 || titleTokens.Count > 8)
            return 0.0;
        if (string.IsNullOrWhiteSpace(normalizedCandidate))
            return 0.0;

        var score = ContainsOrderedTitleTokenSubstringsInNormalizedText(normalizedCandidate, titleTokens, maxGapChars: 80)
            ? 16.0 + Math.Min(4.0, titleTokens.Count)
            : 0.0;
        if (normalizedQuery.Length >= 8
            && normalizedQuery.Count(static ch => ch == ' ') >= 1
            && normalizedCandidate.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            score += 18.0;
        }

        if (normalizedQuery.Length >= 8
            && normalizedQuery.Count(static ch => ch == ' ') >= 1
            && normalizedChunkText.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            score += 14.0;
        }

        var orderedPhraseMatch = phraseTerms.Any(term => ContainsOrderedPhraseWindowInNormalizedText(normalizedCandidate, term, maxGapChars: 40));
        if (orderedPhraseMatch)
            score += 10.0;

        if (ContainsOrderedTitleTokenSubstringsInNormalizedText(normalizedCandidate, titleTokens, maxGapChars: 60))
            score += 14.0;

        if (ContainsTitleLikeLexicalSequence(normalizedCandidate, titleTokens))
            score += 12.0;

        if (ContainsTitleLikeLexicalSequence(normalizedChunkText, titleTokens))
            score += 10.0;

        if (score <= 0.0)
            return 0.0;

        if (normalizedCandidate.StartsWith(normalizedQuery, StringComparison.Ordinal)
            || normalizedCandidate.StartsWith("matched profile title " + normalizedQuery, StringComparison.Ordinal))
        {
            score += 4.0;
        }

        if (string.Equals(match.ChunkType, "unit_exact_v1", StringComparison.Ordinal))
            score += 8.0;
        else if (string.Equals(match.ChunkType, "section_window_v1", StringComparison.Ordinal))
            score += 1.0;
        else if (string.Equals(match.ChunkType, "document_profile", StringComparison.Ordinal))
            score += 1.0;

        return score;
    }

    private static int ComputeDirectChunkTitleSignal(string query, RagMatch match)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(match.Text))
            return 0;

        var titleTokens = ExtractLexicalQueryTokens(query)
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .Take(9)
            .ToArray();
        if (titleTokens.Length < 2 || titleTokens.Length > 8)
            return 0;

        return ComputeDirectChunkTitleSignalCore(
            NormalizeForLexicalSignal(match.Text),
            titleTokens,
            NormalizeForLexicalSignal(query));
    }

    private static int ComputeDirectChunkTitleSignalCore(
        string normalizedChunkText,
        IReadOnlyList<string> titleTokens,
        string normalizedQuery)
    {
        if (titleTokens.Count < 2 || titleTokens.Count > 8 || string.IsNullOrWhiteSpace(normalizedChunkText))
            return 0;

        if (normalizedQuery.Length >= 8
            && normalizedQuery.Count(static ch => ch == ' ') >= 1
            && normalizedChunkText.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            return 3;
        }

        if (ContainsOrderedTitleTokenSubstringsInNormalizedText(normalizedChunkText, titleTokens, maxGapChars: 60))
            return 2;

        return ContainsTitleLikeLexicalSequence(normalizedChunkText, titleTokens) ? 1 : 0;
    }

    private static int ComputeMatchedContentCardTitleSignal(
        RagMatch match,
        IReadOnlyList<string> titleTokens,
        string normalizedQuery)
    {
        if (titleTokens.Count < 2 || titleTokens.Count > 8)
            return 0;
        if (match.MatchedContentCards is not { Count: > 0 })
            return 0;

        var best = 0;
        foreach (var card in match.MatchedContentCards.Take(MaxCalibrationCardCount))
        {
            var normalizedCard = NormalizeForLexicalSignal(string.Join(' ', new[]
            {
                card.Title,
                card.Kind,
                card.Signals is { Count: > 0 } ? string.Join(' ', card.Signals.Take(8)) : null
            }.Where(static value => !string.IsNullOrWhiteSpace(value))));
            if (string.IsNullOrWhiteSpace(normalizedCard))
                continue;

            if (normalizedQuery.Length >= 8
                && normalizedQuery.Count(static ch => ch == ' ') >= 1
                && normalizedCard.Contains(normalizedQuery, StringComparison.Ordinal))
                best = Math.Max(best, 4);
            else if (ContainsOrderedTitleTokenSubstringsInNormalizedText(normalizedCard, titleTokens, maxGapChars: 40))
                best = Math.Max(best, 3);
            else if (ContainsTitleLikeLexicalSequence(normalizedCard, titleTokens))
                best = Math.Max(best, 2);
            else if (ComputeLexicalCoverageInNormalizedText(titleTokens, normalizedCard) >= 0.80)
                best = Math.Max(best, 1);

            if (best >= 4)
                break;
        }

        return best;
    }

    private static bool ContainsTitleLikeLexicalSequence(string normalizedCandidate, IReadOnlyList<string> titleTokens)
    {
        if (string.IsNullOrWhiteSpace(normalizedCandidate) || titleTokens.Count < 2)
            return false;

        var candidateTokens = normalizedCandidate
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length > 0)
            .Take(1500)
            .ToArray();
        if (candidateTokens.Length < titleTokens.Count)
            return false;

        var tokenVariants = titleTokens
            .Select(token => BuildLexicalTokenVariants(token)
                .Select(FoldDiacritics)
                .Where(static variant => variant.Length >= 4)
                .DefaultIfEmpty(FoldDiacritics(token))
                .ToHashSet(StringComparer.Ordinal))
            .ToArray();

        for (var start = 0; start < candidateTokens.Length; start++)
        {
            if (!TokenMatchesTitleVariant(candidateTokens[start], tokenVariants[0]))
                continue;

            var current = start;
            var matchedAll = true;
            for (var expected = 1; expected < tokenVariants.Length; expected++)
            {
                var next = FindNextTitleToken(candidateTokens, current + 1, tokenVariants[expected]);
                if (next < 0)
                {
                    matchedAll = false;
                    break;
                }

                for (var i = current + 1; i < next; i++)
                {
                    if (!TitleConnectorTokens.Contains(candidateTokens[i]))
                    {
                        matchedAll = false;
                        break;
                    }
                }

                if (!matchedAll)
                    break;

                current = next;
            }

            if (matchedAll)
                return true;
        }

        return false;
    }

    private static int FindNextTitleToken(IReadOnlyList<string> candidateTokens, int startIndex, IReadOnlySet<string> expectedVariants)
    {
        var maxExclusive = Math.Min(candidateTokens.Count, startIndex + 6);
        for (var i = startIndex; i < maxExclusive; i++)
        {
            if (TokenMatchesTitleVariant(candidateTokens[i], expectedVariants))
                return i;
            if (!TitleConnectorTokens.Contains(candidateTokens[i]))
                break;
        }

        return -1;
    }

    private static bool TokenMatchesTitleVariant(string candidateToken, IReadOnlySet<string> expectedVariants)
    {
        if (expectedVariants.Contains(candidateToken))
            return true;

        foreach (var variant in expectedVariants)
        {
            if (variant.Length < 4
                || candidateToken.Length <= variant.Length
                || !candidateToken.StartsWith(variant, StringComparison.Ordinal))
            {
                continue;
            }

            var suffix = candidateToken[variant.Length..];
            if (TitleConnectorTokens.Contains(suffix)
                || suffix.All(char.IsDigit))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsOrderedTitleTokenSubstrings(string candidateText, IReadOnlyList<string> titleTokens, int maxGapChars)
    {
        if (string.IsNullOrWhiteSpace(candidateText) || titleTokens.Count < 2)
            return false;

        return ContainsOrderedTitleTokenSubstringsInNormalizedText(
            FoldDiacritics(candidateText).ToLowerInvariant(),
            titleTokens,
            maxGapChars);
    }

    private static bool ContainsOrderedTitleTokenSubstringsInNormalizedText(
        string normalizedCandidate,
        IReadOnlyList<string> titleTokens,
        int maxGapChars)
    {
        if (string.IsNullOrWhiteSpace(normalizedCandidate) || titleTokens.Count < 2)
            return false;

        var variantGroups = titleTokens
            .Select(token => BuildLexicalTokenVariants(token)
                .Select(FoldDiacritics)
                .Append(FoldDiacritics(token))
                .Where(static variant => variant.Length >= 4)
                .Distinct(StringComparer.Ordinal)
                .ToArray())
            .ToArray();
        if (variantGroups.Any(static group => group.Length == 0))
            return false;

        return TryFindOrderedTitleTokenSubstrings(
            normalizedCandidate,
            variantGroups,
            tokenIndex: 0,
            searchFrom: 0,
            previousEnd: -1,
            maxGapChars);
    }

    private static bool TryFindOrderedTitleTokenSubstrings(
        string normalizedCandidate,
        IReadOnlyList<string[]> variantGroups,
        int tokenIndex,
        int searchFrom,
        int previousEnd,
        int maxGapChars)
    {
        if (tokenIndex >= variantGroups.Count)
            return true;

        var attempts = 0;
        foreach (var occurrence in EnumerateTitleTokenOccurrences(normalizedCandidate, variantGroups[tokenIndex], searchFrom))
        {
            if (previousEnd >= 0 && occurrence.Index - previousEnd > maxGapChars)
                break;

            if (TryFindOrderedTitleTokenSubstrings(
                normalizedCandidate,
                variantGroups,
                tokenIndex + 1,
                occurrence.Index + occurrence.Length,
                occurrence.Index + occurrence.Length,
                maxGapChars))
            {
                return true;
            }

            attempts++;
            if (attempts >= 48)
                break;
        }

        return false;
    }

    private static IEnumerable<(int Index, int Length)> EnumerateTitleTokenOccurrences(
        string normalizedCandidate,
        IReadOnlyList<string> variants,
        int searchFrom)
    {
        var seen = new HashSet<int>();
        while (searchFrom < normalizedCandidate.Length)
        {
            var bestIndex = -1;
            var bestLength = 0;
            foreach (var variant in variants)
            {
                var index = normalizedCandidate.IndexOf(variant, searchFrom, StringComparison.Ordinal);
                if (index < 0)
                    continue;
                if (bestIndex < 0 || index < bestIndex || (index == bestIndex && variant.Length > bestLength))
                {
                    bestIndex = index;
                    bestLength = variant.Length;
                }
            }

            if (bestIndex < 0)
                yield break;

            if (seen.Add(bestIndex))
                yield return (bestIndex, bestLength);

            searchFrom = bestIndex + 1;
        }
    }

    private static double ComputeRawOrderedTitleScore(string query, string candidateText)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(candidateText))
            return 0.0;

        var queryTokens = System.Text.RegularExpressions.Regex
            .Split(FoldDiacritics(query).ToLowerInvariant(), @"[^\p{L}\p{Nd}]+")
            .Select(static token => ExactMatchEntryExtractor.NormalizeForLookup(token))
            .Where(static token => token.Length >= 4)
            .Where(static token => token.Any(char.IsLetter))
            .Where(static token => !LexicalStopwords.Contains(token))
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        if (queryTokens.Length < 2)
            return 0.0;

        return ContainsOrderedTitleTokenSubstrings(candidateText, queryTokens, maxGapChars: 80)
            ? 16.0 + Math.Min(4.0, queryTokens.Length)
            : 0.0;
    }

    private static bool ContainsExactTitleActionMarker(string query)
    {
        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query))} ";
        return ContainsAny(
            normalized,
            " explique ",
            " expliquer ",
            " expliquez ",
            " explain ",
            " explains ",
            " summarize ",
            " summary ",
            " resume ",
            " resumer ",
            " resumee ",
            " decris ",
            " decrire ",
            " describe ",
            " detaille ",
            " detailler ",
            " pourquoi ",
            " why ",
            " comment ",
            " how ");
    }

    private static bool ShouldApplySpecificCoverageTitlePriority(
        string query,
        IReadOnlyList<string> lexicalTokens,
        IReadOnlyList<string> comparativeSubjectTokens)
    {
        if (lexicalTokens.Count is < 2 or > 6)
            return false;
        if (comparativeSubjectTokens.Count > 0)
            return false;
        if (ExtractQuotedLookupPhrases(query).Count == 0 && ContainsExactTitleActionMarker(query))
            return false;

        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query))} ";
        if (IsRecommendationSelectionQuery(normalized))
            return false;

        return !ContainsAny(
            normalized,
            " compare ",
            " comparer ",
            " comparison ",
            " comparaison ",
            " difference ",
            " differences ",
            " differents ",
            " differentes ");
    }

    private static bool IsRecommendationSelectionQuery(string normalizedPaddedQuery)
        => ContainsAny(
                normalizedPaddedQuery,
                " quel ",
                " quelle ",
                " quelles ",
                " quels ",
                " which ",
                " what ",
                " cual ",
                " qual ",
                " quale ",
                " welche ")
            && ContainsAny(
                normalizedPaddedQuery,
                " choisir ",
                " choose ",
                " recommander ",
                " recommande ",
                " recommandes ",
                " recommendation ",
                " recommendations ",
                " adaptee ",
                " adaptees ",
                " adapte ",
                " adaptes ",
                " suitable ",
                " best ",
                " meilleur ",
                " meilleure ",
                " meilleurs ",
                " meilleures ");

    internal static int CountSpecificLexicalAnchors(IReadOnlyList<string> lexicalTokens, string? candidateText)
    {
        if (lexicalTokens.Count == 0 || string.IsNullOrWhiteSpace(candidateText))
            return 0;

        return CountSpecificLexicalAnchorsInNormalizedText(lexicalTokens, NormalizeForLexicalSignal(candidateText));
    }

    private static int CountSpecificLexicalAnchorsInNormalizedText(IReadOnlyList<string> lexicalTokens, string normalized)
    {
        if (lexicalTokens.Count == 0 || string.IsNullOrWhiteSpace(normalized))
            return 0;

        var matched = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in lexicalTokens)
        {
            if (SpecificAnchorStopwords.Contains(token))
                continue;
            if (token.Any(char.IsDigit)
                && ContainsExactNormalizedToken(normalized, token))
            {
                if (seen.Add(token))
                    matched++;
                continue;
            }

            if (token.Length < 5 && !token.Any(char.IsDigit))
                continue;

            foreach (var variant in BuildLexicalTokenVariants(token))
            {
                var foldedVariant = FoldDiacritics(variant);
                if ((foldedVariant.Length >= 5 && ContainsExactNormalizedToken(normalized, foldedVariant))
                    || (foldedVariant.Length >= 6
                    && (normalized.Contains(foldedVariant, StringComparison.Ordinal)
                        || ContainsNearNormalizedToken(normalized, foldedVariant)))
                   )
                {
                    if (seen.Add(token))
                        matched++;
                    break;
                }
            }
        }

        return matched;
    }

    private static bool ContainsNearNormalizedToken(string normalizedCandidate, string token)
    {
        if (string.IsNullOrWhiteSpace(normalizedCandidate) || string.IsNullOrWhiteSpace(token) || token.Length < 6)
            return false;

        foreach (var candidate in normalizedCandidate.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (candidate.Length < 6 || Math.Abs(candidate.Length - token.Length) > 1)
                continue;
            if (ComputeBoundedEditDistance(candidate, token, maxDistance: 1) <= 1)
                return true;
        }

        return false;
    }

    private static int ComputeBoundedEditDistance(string left, string right, int maxDistance)
    {
        if (Math.Abs(left.Length - right.Length) > maxDistance)
            return maxDistance + 1;

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            var rowMin = current[0];
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
                rowMin = Math.Min(rowMin, current[j]);
            }

            if (rowMin > maxDistance)
                return maxDistance + 1;

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static bool ContainsExactNormalizedToken(string normalizedCandidate, string token)
    {
        if (string.IsNullOrWhiteSpace(normalizedCandidate) || string.IsNullOrWhiteSpace(token))
            return false;

        return normalizedCandidate
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(token, StringComparer.Ordinal);
    }

    internal static bool LooksLikeStructuredAnswerUnit(RagMatch match)
    {
        if (!string.Equals(match.ChunkType, "unit_exact_v1", StringComparison.Ordinal))
            return false;

        return LooksLikeStructuredAnswerText(match.Text);
    }

    internal static bool LooksLikeStructuredAnswerChunk(RagMatch match)
    {
        if (!string.Equals(match.ChunkType, "unit_exact_v1", StringComparison.Ordinal)
            && !string.Equals(match.ChunkType, "section_window_v1", StringComparison.Ordinal))
            return false;

        return LooksLikeStructuredAnswerText(match.Text);
    }

    private static int GetStructuredAnswerPriority(RagMatch match)
    {
        if (LooksLikeStructuredAnswerUnit(match))
            return 3;
        if (LooksLikeStructuredAnswerChunk(match))
            return 2;
        if (string.Equals(match.ChunkType, "document_profile", StringComparison.Ordinal))
            return 1;
        return 0;
    }

    private static bool LooksLikeStructuredAnswerText(string? value)
    {
        return StructuredContentLexicon.ContainsStructuredAnswerCue(value)
            || LooksLikeStructuredQuantityList(value);
    }

    internal static bool LooksLikeStructuredQuantityList(string? value)
        => StructuredContentLexicon.LooksLikeStructuredQuantityList(value);

    internal static bool LooksLikeSourceListChunk(RagMatch match)
    {
        var text = match.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lower = text.ToLowerInvariant();
        var urlCount = CountOccurrences(lower, "http://")
            + CountOccurrences(lower, "https://")
            + CountOccurrences(lower, "www.");
        if (urlCount >= 3)
            return true;

        var domainMarkers = CountOccurrences(lower, ".com")
            + CountOccurrences(lower, ".org")
            + CountOccurrences(lower, ".net")
            + CountOccurrences(lower, ".fr")
            + CountOccurrences(lower, ".ca")
            + CountOccurrences(lower, ".ch");

        return domainMarkers >= 5 && CountInlinePageNumberBoundaries(text) <= 2;
    }

    internal static bool LooksLikeDocumentOverviewChunk(RagMatch match)
    {
        var text = FoldDiacritics(match.Text ?? string.Empty).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hasOverviewLanguage =
            text.Contains("ce livre", StringComparison.Ordinal)
            || text.Contains("ce guide", StringComparison.Ordinal)
            || text.Contains("ce document", StringComparison.Ordinal)
            || text.Contains("cet ouvrage", StringComparison.Ordinal)
            || text.Contains("this book", StringComparison.Ordinal)
            || text.Contains("this guide", StringComparison.Ordinal)
            || text.Contains("this document", StringComparison.Ordinal)
            || text.Contains("contains examples", StringComparison.Ordinal)
            || text.Contains("contient quelques exemples", StringComparison.Ordinal)
            || text.Contains("vous permettra", StringComparison.Ordinal)
            || text.Contains("permettra de", StringComparison.Ordinal)
            || text.Contains("introduction", StringComparison.Ordinal);

        if (!hasOverviewLanguage)
            return false;

        return !ContainsActionableDocumentProfileLanguage(text);
    }

    private static bool ContainsActionableDocumentProfileLanguage(string normalizedText)
        => ContainsAny(
            normalizedText,
            "procedure",
            "procedures",
            "materials",
            "material",
            "materiel",
            "materiaux",
            "materiales",
            "materiali",
            "materialien",
            "components",
            "component",
            "composants",
            "componentes",
            "componenti",
            "komponenten",
            "instructions",
            "instruction",
            "consignes",
            "instrucciones",
            "instrucoes",
            "istruzioni",
            "anweisungen",
            "steps",
            "step",
            "etapes",
            "pasos",
            "passos",
            "schritte",
            "tools",
            "outils",
            "herramientas",
            "ferramentas",
            "strumenti",
            "werkzeug",
            "equipment",
            "equipement",
            "equipamentos",
            "attrezzatura",
            "ausrustung");

    internal static bool LooksLikeNavigationalChunk(RagMatch match)
    {
        if (IsDocumentProfileMatch(match))
            return false;

        if (string.Equals(match.ContentRole, RetrievalContentClassifier.NavigationRole, StringComparison.OrdinalIgnoreCase))
            return true;

        if (match.NavigationScore is >= 0.72
            && (match.ContentDensityScore is null or < 0.50))
            return true;

        if (RetrievalContentClassifier.IsNavigationChunkType(match.ChunkType))
            return true;

        var text = match.Text ?? string.Empty;
        var context = $"{match.SectionTitle} {match.HeadingPath} {text}";
        var folded = FoldDiacritics(context).ToLowerInvariant();
        var foldedText = FoldDiacritics(text).ToLowerInvariant();
        var padded = $" {NormalizeQuery(folded)} ";
        var hasStrongIndexMarker = HasStrongNavigationalMarker(folded, padded);
        var hasStructuredProcedureBody = (foldedText.Contains("materials", StringComparison.Ordinal)
                || foldedText.Contains("materiaux", StringComparison.Ordinal)
                || foldedText.Contains("components", StringComparison.Ordinal)
                || foldedText.Contains("composants", StringComparison.Ordinal))
            && (foldedText.Contains("method", StringComparison.Ordinal)
                || foldedText.Contains("procedure", StringComparison.Ordinal)
                || foldedText.Contains("instructions", StringComparison.Ordinal)
                || foldedText.Contains("steps", StringComparison.Ordinal));
        if (hasStructuredProcedureBody && !hasStrongIndexMarker)
            return false;

        if (folded.Contains("sommaire", StringComparison.Ordinal)
            || folded.Contains("table des matieres", StringComparison.Ordinal)
            || folded.Contains("table of contents", StringComparison.Ordinal)
            || hasStrongIndexMarker
            || folded.Contains("fiche-index", StringComparison.Ordinal)
            || folded.Contains("fiche index", StringComparison.Ordinal)
            || padded.Contains(" sommaire ", StringComparison.Ordinal)
            || padded.Contains(" table des matieres ", StringComparison.Ordinal)
            || padded.Contains(" table of contents ", StringComparison.Ordinal)
            || padded.Contains(" contents ", StringComparison.Ordinal)
            || padded.Contains(" index ", StringComparison.Ordinal))
        {
            if (!hasStrongIndexMarker
                && padded.Contains(" index ", StringComparison.Ordinal)
                && CountBulletMarkers(text) < 8
                && CountInlinePageNumberBoundaries(text) < 5
                && !LooksLikeTitleListChunk(text))
                return false;

            return true;
        }

        if (CountInlinePageNumberBoundaries(text) >= 5)
            return true;

        return LooksLikeTitleListChunk(text);
    }

    internal static bool LooksLikeGlossaryChunk(RagMatch match)
    {
        if (IsDocumentProfileMatch(match))
            return false;

        var text = match.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var folded = FoldDiacritics(text).ToLowerInvariant();
        var definitionHeadings = System.Text.RegularExpressions.Regex.Matches(
            text,
            @"(?:^|[\r\n\s])[\p{Lu}][\p{Lu}\p{Mn}\p{Pd}\s]{2,42}\s*:",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant).Count;
        if (definitionHeadings >= 4)
            return true;

        var glossaryLanguage = ContainsAny(
            $" {NormalizeQuery(folded)} ",
            " glossaire ",
            " glossary ",
            " vocabulaire ",
            " vocabulary ",
            " definition ",
            " definitions ");
        if (!glossaryLanguage)
            return false;

        var shortDefinitionSignals = CountOccurrences(folded, " signifie ")
            + CountOccurrences(folded, " designe ")
            + CountOccurrences(folded, " means ")
            + CountOccurrences(folded, " refers to ")
            + CountOccurrences(folded, " consiste a ");
        return shortDefinitionSignals >= 2 || definitionHeadings >= 2;
    }

    internal static bool ShouldAllowGlossaryResults(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var normalized = $" {NormalizeQuery(FoldDiacritics(query).ToLowerInvariant())} ";
        return ContainsAny(
            normalized,
            " definition ",
            " definir ",
            " definis ",
            " que veut dire ",
            " signifie ",
            " meaning ",
            " define ",
            " glossaire ",
            " glossary ",
            " vocabulaire ",
            " vocabulary ",
            " terme ",
            " term ");
    }

    internal static bool LooksLikeStrongNavigationalChunk(RagMatch match)
    {
        if (match.NavigationScore is >= 0.85
            && (match.ContentDensityScore is null or < 0.45))
            return true;

        if (RetrievalContentClassifier.IsNavigationChunkType(match.ChunkType))
            return true;

        var text = match.Text ?? string.Empty;
        var context = $"{match.SectionTitle} {match.HeadingPath} {text}";
        var folded = FoldDiacritics(context).ToLowerInvariant();
        var padded = $" {NormalizeQuery(folded)} ";

        return HasStrongNavigationalMarker(folded, padded)
            || folded.Contains("table des matieres", StringComparison.Ordinal)
            || folded.Contains("table of contents", StringComparison.Ordinal)
            || padded.Contains(" sommaire ", StringComparison.Ordinal)
            || CountInlinePageNumberBoundaries(text) >= 8;
    }

    private static bool HasStrongNavigationalMarker(string foldedText, string paddedNormalizedText)
    {
        if (string.IsNullOrWhiteSpace(foldedText) || string.IsNullOrWhiteSpace(paddedNormalizedText))
            return false;

        if (foldedText.Contains("fiche-index", StringComparison.Ordinal)
            || foldedText.Contains("fiche index", StringComparison.Ordinal))
            return true;

        return System.Text.RegularExpressions.Regex.IsMatch(
            paddedNormalizedText,
            @"\b(?:index|liste|list|catalogue|catalog|inventaire|inventory)\s+(?:des?|de|du|d['’]?|of|for)?\s*[\p{L}\p{N}][\p{L}\p{N}\s\-_]{2,80}\b|\b[\p{L}\p{N}][\p{L}\p{N}\s\-_]{2,80}\s+(?:index|liste|list|catalogue|catalog|inventory)\b",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static int CountBulletMarkers(string text)
        => string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Count(static ch => ch is '\u2022' or '-' or '*');

    private static int CountInlinePageNumberBoundaries(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var count = 0;
        var digitRun = 0;
        foreach (var ch in text)
        {
            if (char.IsDigit(ch))
            {
                digitRun++;
                continue;
            }

            if (digitRun is > 0 and <= 4 && char.IsLetter(ch) && char.IsUpper(ch))
                count++;

            digitRun = 0;
        }

        return count;
    }

    private static bool LooksLikeTitleListChunk(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 180)
            return false;

        var words = 0;
        var capitalizedStarts = 0;
        var sentenceMarkers = 0;
        var inWord = false;

        foreach (var ch in text)
        {
            if (ch is '.' or '!' or '?' or ';' or ':' or '\u2022')
                sentenceMarkers++;

            if (char.IsLetter(ch))
            {
                if (!inWord)
                {
                    words++;
                    if (char.IsUpper(ch))
                        capitalizedStarts++;
                }

                inWord = true;
            }
            else
            {
                inWord = false;
            }
        }

        return (words >= 24
                && capitalizedStarts >= Math.Max(12, words / 3)
                && sentenceMarkers <= 2)
            || (words >= 40
                && capitalizedStarts >= 20
                && sentenceMarkers <= 2)
            || (CountLowerToUpperTransitions(text) >= 8 && sentenceMarkers <= 3);
    }

    private static int CountLowerToUpperTransitions(string text)
    {
        var count = 0;
        var previousWasLower = false;
        foreach (var ch in text)
        {
            if (char.IsUpper(ch) && previousWasLower)
                count++;

            previousWasLower = char.IsLower(ch);
        }

        return count;
    }

    private static int CountOccurrences(string text, string needle)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(needle))
            return 0;

        var count = 0;
        var index = 0;
        while (index < text.Length)
        {
            var found = text.IndexOf(needle, index, StringComparison.Ordinal);
            if (found < 0)
                break;

            count++;
            index = found + needle.Length;
        }

        return count;
    }

    internal static List<RagMatch> RerankDenseMatches(IReadOnlyList<RagMatch> matches)
        => matches
            .Select(match => new
            {
                Match = match,
                Score = match.Score + ComputeDenseBoost(match)
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Match.DocPath, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .Select(item => item.Match with { Score = item.Score })
            .ToList();

    internal static List<RagMatch> ApplyRerankScores(
        IReadOnlyList<RagMatch> originalCandidates,
        IReadOnlyList<TeiClient.RerankItem> rerankedItems,
        int rerankedPrefixCount)
    {
        if (originalCandidates.Count == 0 || rerankedItems.Count == 0 || rerankedPrefixCount <= 0)
            return originalCandidates.ToList();

        var limitedPrefixCount = Math.Min(rerankedPrefixCount, originalCandidates.Count);
        var prefix = originalCandidates.Take(limitedPrefixCount).ToArray();
        var suffix = originalCandidates.Skip(limitedPrefixCount).ToArray();
        var byIndex = rerankedItems
            .Where(item => item.Index >= 0 && item.Index < prefix.Length)
            .GroupBy(item => item.Index)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(x => x.Score).First().Score);

        if (byIndex.Count == 0)
            return originalCandidates.ToList();

        var orderedScores = rerankedItems
            .Where(item => item.Index >= 0 && item.Index < prefix.Length)
            .Select(item => item.Score)
            .OrderBy(score => score)
            .ToArray();
        var minScore = orderedScores.Length == 0 ? 0.0 : orderedScores.First();
        var maxScore = orderedScores.Length == 0 ? 0.0 : orderedScores.Last();

        var rerankedPrefix = byIndex
            .Select(pair =>
            {
                var match = prefix[pair.Key];
                var normalizedRerank = NormalizeRerankScore(pair.Value, minScore, maxScore);
                var blended = Math.Min(1.02, (match.Score * 0.35) + (normalizedRerank * 0.65));
                return match with
                {
                    Score = blended,
                    RerankScore = pair.Value
                };
            })
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.RerankScore)
            .ToList();

        var untouchedPrefix = Enumerable.Range(0, prefix.Length)
            .Where(index => !byIndex.ContainsKey(index))
            .Select(index => prefix[index])
            .ToList();

        var finalList = new List<RagMatch>(originalCandidates.Count);
        finalList.AddRange(rerankedPrefix);
        finalList.AddRange(untouchedPrefix);
        finalList.AddRange(suffix);
        return finalList;
    }

    private static double ComputeDenseBoost(RagMatch match)
    {
        double boost = 0.0;
        if (string.Equals(match.ChunkType, "unit_exact_v1", StringComparison.Ordinal))
            boost += 0.02;
        else if (string.Equals(match.ChunkType, "section_window_v1", StringComparison.Ordinal))
            boost += 0.01;

        if (!string.IsNullOrWhiteSpace(match.HeadingPath))
            boost += 0.005;
        if (string.Equals(match.EmbeddingBasis, "contextual_text_v1", StringComparison.Ordinal))
            boost += 0.005;

        return boost;
    }

    internal static double NormalizeRerankScore(double rawScore, double minScore, double maxScore)
    {
        if (double.IsNaN(rawScore) || double.IsInfinity(rawScore))
            return 0.0;
        if (maxScore <= minScore)
            return 1.0;

        var normalized = (rawScore - minScore) / (maxScore - minScore);
        return Math.Clamp(normalized, 0.0, 1.0);
    }

    internal static double NormalizeSparseScore(double rawScore)
    {
        if (rawScore <= 0.0 || double.IsNaN(rawScore) || double.IsInfinity(rawScore))
            return 0.0;

        var normalized = 0.45 + Math.Min(0.42, Math.Log10(1 + (rawScore * 1000.0)) * 0.24);
        return Math.Min(0.92, normalized);
    }

    internal static double NormalizeDocumentProfileScore(double sparseRank, int matchCount)
    {
        if (matchCount <= 0)
            return 0.32;

        var lexicalScore = NormalizeSparseScore(sparseRank);
        var matchScore = Math.Min(0.28, matchCount * 0.045);
        var combined = Math.Max(lexicalScore, 0.48 + matchScore);
        return Math.Clamp(combined, 0.0, 0.86);
    }

    internal static double ComputeDocumentProfileSpecificityBoost(
        string query,
        string? candidateText,
        IReadOnlyList<string> peerCandidateTexts)
    {
        if (string.IsNullOrWhiteSpace(candidateText) || peerCandidateTexts.Count == 0)
            return 0.0;

        var tokens = BuildDocumentProfileSpecificityTokens(query);
        if (tokens.Count == 0)
            return 0.0;

        var normalizedCandidate = NormalizeForLexicalSignal(candidateText);
        if (string.IsNullOrWhiteSpace(normalizedCandidate))
            return 0.0;

        var normalizedPeers = peerCandidateTexts
            .Select(NormalizeForLexicalSignal)
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .ToArray();
        if (normalizedPeers.Length == 0)
            return 0.0;

        var rawBoost = 0.0;
        foreach (var token in tokens)
        {
            var variants = BuildLexicalTokenVariants(token);
            if (variants.Count == 0)
                continue;

            if (!variants.Any(variant => normalizedCandidate.Contains(variant, StringComparison.Ordinal)))
                continue;

            var documentFrequency = normalizedPeers.Count(peer =>
                variants.Any(variant => peer.Contains(variant, StringComparison.Ordinal)));
            if (documentFrequency <= 0)
                continue;

            var inverseFrequency = Math.Log((normalizedPeers.Length + 1.0) / (documentFrequency + 0.5)) + 1.0;
            var tokenWeight =
                IsReferenceLikeLookupTerm(token) ? 1.45 :
                token.Length >= 10 ? 1.25 :
                token.Length >= 7 ? 1.10 :
                1.0;
            rawBoost += inverseFrequency * tokenWeight;
        }

        return Math.Clamp(rawBoost * 0.025, 0.0, 0.24);
    }

    internal static IReadOnlyList<string> BuildDocumentProfileSpecificityTokens(string query)
        => ExtractLexicalQueryTokens(query)
            .Where(static token => token.Length >= 5 || IsReferenceLikeLookupTerm(token))
            .Where(static token => !PrimaryAnchorStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    internal static IReadOnlyList<string> ExtractLexicalQueryTokens(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query));
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length >= 4)
            .Where(static token => token.Any(char.IsLetter))
            .Where(static token => !LexicalStopwords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    internal static IReadOnlyList<string> BuildLexicalContentFallbackTerms(string query)
    {
        var tokens = ExtractLexicalQueryTokens(query);
        if (tokens.Count == 0)
            return Array.Empty<string>();

        var terms = new HashSet<string>(StringComparer.Ordinal);
        var canonicalTokens = new List<string>();
        foreach (var token in tokens)
        {
            var variants = BuildLexicalTokenVariants(token);
            foreach (var variant in variants)
                terms.Add(variant);

            var canonical = variants
                .OrderBy(static variant => ContainsNonAscii(variant) ? 1 : 0)
                .ThenBy(static variant => variant.Length)
                .ThenBy(static variant => variant, StringComparer.Ordinal)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(canonical))
                canonicalTokens.Add(canonical);
        }

        AddAdjacentPhraseTerms(terms, canonicalTokens);

        var surfaceTokens = ExtractLexicalQuerySurfaceTokens(query);
        if (surfaceTokens.Count > 0)
        {
            foreach (var token in surfaceTokens)
                terms.Add(token);

            AddAdjacentPhraseTerms(terms, surfaceTokens);
        }

        AddAdjacentAlphaNumericPhraseTerms(terms, query);

        foreach (var quotedPhrase in ExtractQuotedLookupPhrases(query))
        {
            terms.Add(quotedPhrase);
            var foldedPhrase = FoldDiacritics(quotedPhrase);
            if (!string.Equals(foldedPhrase, quotedPhrase, StringComparison.Ordinal))
                terms.Add(foldedPhrase);
        }

        foreach (var focusedPhrase in ExtractFocusedLookupPhrases(query))
        {
            terms.Add(focusedPhrase);
            var foldedPhrase = FoldDiacritics(focusedPhrase);
            if (!string.Equals(foldedPhrase, focusedPhrase, StringComparison.Ordinal))
                terms.Add(foldedPhrase);
        }

        var filteredTerms = terms
            .Where(static term => term.Length >= 4)
            .Where(static term => !LexicalStopwords.Contains(term))
            .Where(static term => !SpecificAnchorStopwords.Contains(term))
            .ToArray();

        var phraseTerms = filteredTerms
            .Where(static term => term.Contains(' '))
            .OrderBy(static term => term, StringComparer.Ordinal)
            .Take(40);
        var singleTerms = filteredTerms
            .Where(static term => !term.Contains(' '))
            .OrderBy(static term => term, StringComparer.Ordinal)
            .Take(40);

        return phraseTerms
            .Concat(singleTerms)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    internal static IReadOnlyList<string> BuildDocumentProfileLexicalTerms(string query)
    {
        var terms = new HashSet<string>(BuildLexicalContentFallbackTerms(query), StringComparer.Ordinal);
        foreach (var token in ExtractLexicalQuerySurfaceTokens(query))
            terms.Add(token);
        foreach (var token in BuildDocumentProfileSpecificityTokens(query))
        {
            foreach (var variant in BuildLexicalTokenVariants(token))
                terms.Add(variant);
        }

        return terms
            .Where(static term => term.Length >= 4)
            .OrderByDescending(static term => term.Contains(' '))
            .ThenBy(static term => term, StringComparer.Ordinal)
            .Take(96)
            .ToArray();
    }

    internal static IReadOnlyList<string> ExtractQuotedLookupPhrases(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        var matches = System.Text.RegularExpressions.Regex.Matches(
            query,
            "[\u00ab\u201c\"](?<phrase>[^\u00bb\u201d\"]{3,80})[\u00bb\u201d\"]",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (matches.Count == 0)
            return Array.Empty<string>();

        return matches
            .Select(match => ExactMatchEntryExtractor.NormalizeForLookup(match.Groups["phrase"].Value))
            .Where(static phrase => phrase.Length >= 4)
            .Where(static phrase => phrase.Count(static ch => ch == ' ') >= 1 || IsSingleTokenQuotedLookupPhrase(phrase))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsSingleTokenQuotedLookupPhrase(string phrase)
    {
        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(phrase));
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Contains(' '))
            return false;

        return IsQuotedLookupSignalToken(normalized)
            && !SpecificAnchorStopwords.Contains(normalized)
            && !PrimaryAnchorStopwords.Contains(normalized);
    }

    internal static double ComputeQuotedLookupCandidateScore(IReadOnlyList<string> quotedPhrases, string? candidateText)
    {
        if (quotedPhrases.Count == 0 || string.IsNullOrWhiteSpace(candidateText))
            return 0.0;

        var normalizedCandidate = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(candidateText));
        if (string.IsNullOrWhiteSpace(normalizedCandidate))
            return 0.0;

        var compactCandidate = normalizedCandidate.Replace(" ", string.Empty);
        var bestScore = 0.0;
        foreach (var quotedPhrase in quotedPhrases)
        {
            var normalizedPhrase = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(quotedPhrase));
            if (string.IsNullOrWhiteSpace(normalizedPhrase))
                continue;

            var tokens = normalizedPhrase
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(IsQuotedLookupSignalToken)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var compactPhrase = normalizedPhrase.Replace(" ", string.Empty);
            var hasExactPhrase = normalizedCandidate.Contains(normalizedPhrase, StringComparison.Ordinal)
                || (compactPhrase.Length >= 8 && compactCandidate.Contains(compactPhrase, StringComparison.Ordinal));

            var matched = tokens.Count(token => ContainsQuotedLookupToken(normalizedCandidate, compactCandidate, token));
            if (!hasExactPhrase && matched < Math.Min(tokens.Length, 2))
                continue;

            var coverage = tokens.Length == 0
                ? hasExactPhrase ? 1.0 : 0.0
                : matched / (double)tokens.Length;
            var score = (hasExactPhrase ? 12.0 : 0.0)
                + (coverage * 8.0)
                + Math.Min(3, matched) * 0.5;
            score += ComputeQuotedPhrasePlacementBonus(quotedPhrase, candidateText);

            if (tokens.Length >= 4 && matched >= tokens.Length - 1)
                score += 2.0;

            bestScore = Math.Max(bestScore, score);
        }

        return bestScore;
    }

    private static double ComputeQuotedPhrasePlacementBonus(string quotedPhrase, string candidateText)
    {
        if (string.IsNullOrWhiteSpace(quotedPhrase) || string.IsNullOrWhiteSpace(candidateText))
            return 0.0;

        var foldedCandidate = FoldDiacritics(candidateText);
        var foldedPhrase = FoldDiacritics(quotedPhrase);
        if (foldedPhrase.Length < 4)
            return 0.0;

        var best = 0.0;
        var index = foldedCandidate.IndexOf(foldedPhrase, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var slice = foldedCandidate.Substring(index, Math.Min(foldedPhrase.Length, foldedCandidate.Length - index));
            var letters = slice.Where(char.IsLetter).ToArray();
            var uppercaseRatio = letters.Length == 0
                ? 0.0
                : letters.Count(char.IsUpper) / (double)letters.Length;
            var prefix = foldedCandidate.Substring(Math.Max(0, index - 32), Math.Min(32, index));
            var placement = uppercaseRatio >= 0.70 ? 7.0 : 0.0;
            if (LooksLikeMeasuredListLead(prefix))
                placement -= 4.0;

            best = Math.Max(best, placement);
            index = foldedCandidate.IndexOf(foldedPhrase, index + foldedPhrase.Length, StringComparison.OrdinalIgnoreCase);
        }

        return best;
    }

    private static bool LooksLikeMeasuredListLead(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return false;

        return System.Text.RegularExpressions.Regex.IsMatch(
            prefix,
            @"(?:\d+[.,]?\d*|\b(?:g|kg|mg|ml|cl|l|litre|litres|oz|lb|unit|units|unite|unites|item|items)\b)\s*(?:de|d')?\s*$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static bool IsQuotedLookupSignalToken(string token)
        => (token.Length >= 4 || token.Any(char.IsDigit))
           && !LexicalStopwords.Contains(token);

    private static bool ContainsQuotedLookupToken(string normalizedCandidate, string compactCandidate, string token)
    {
        if (token.All(char.IsDigit))
        {
            return normalizedCandidate
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(token, StringComparer.Ordinal);
        }

        return normalizedCandidate.Contains(token, StringComparison.Ordinal)
            || (token.Length >= 4 && compactCandidate.Contains(token, StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> ExtractLexicalQuerySurfaceTokens(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        var normalized = ExactMatchEntryExtractor.NormalizeForLookup(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length >= 4)
            .Where(static token => token.Any(char.IsLetter))
            .Where(static token => !LexicalStopwords.Contains(FoldDiacritics(token)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddAdjacentAlphaNumericPhraseTerms(HashSet<string> terms, string query)
    {
        AddAdjacentAlphaNumericPhraseTermsFromNormalized(terms, FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)));
        AddAdjacentAlphaNumericPhraseTermsFromNormalized(terms, ExactMatchEntryExtractor.NormalizeForLookup(query));
    }

    private static void AddAdjacentAlphaNumericPhraseTermsFromNormalized(HashSet<string> terms, string normalizedQuery)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return;

        var tokens = normalizedQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsAlphaNumericPhraseToken)
            .Take(24)
            .ToArray();
        if (tokens.Length < 2)
            return;

        for (var i = 0; i < tokens.Length - 1; i++)
        {
            var phrase = $"{tokens[i]} {tokens[i + 1]}";
            if (IsAlphaNumericPhraseTerm(phrase))
                terms.Add(phrase);
        }
    }

    private static bool IsAlphaNumericPhraseToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 24)
            return false;

        var hasLetter = token.Any(char.IsLetter);
        var hasDigit = token.Any(char.IsDigit);
        if (!hasLetter && !hasDigit)
            return false;
        if (!token.All(static ch => char.IsLetterOrDigit(ch)))
            return false;

        var folded = FoldDiacritics(token);
        if (hasLetter && !hasDigit)
            return token.Length >= 3 && !LexicalStopwords.Contains(folded);
        if (hasDigit && !hasLetter)
            return token.Length <= 8;

        return token.Length >= 2 && !LexicalStopwords.Contains(folded);
    }

    private static bool IsAlphaNumericPhraseTerm(string phrase)
    {
        if (phrase.Length is < 4 or > 40)
            return false;

        var parts = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return false;

        var hasDigit = parts.Any(static part => part.Any(char.IsDigit));
        var hasLetter = parts.Any(static part => part.Any(char.IsLetter));
        if (!hasDigit || !hasLetter)
            return false;

        return parts.Any(static part => part.Any(char.IsLetter) && part.Length >= 3);
    }

    private static IReadOnlyList<string> BuildLexicalTokenVariants(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Array.Empty<string>();

        var variants = new HashSet<string>(StringComparer.Ordinal) { token };

        if (token.Length >= 6 && token.EndsWith("es", StringComparison.Ordinal))
            variants.Add(token[..^1]);
        if (token.Length >= 6 && (token.EndsWith("s", StringComparison.Ordinal) || token.EndsWith("x", StringComparison.Ordinal)))
            variants.Add(token[..^1]);
        if (token.Length >= 7 && token.EndsWith("eux", StringComparison.Ordinal))
            variants.Add(token[..^3] + "e");
        if (token.Length >= 7 && token.EndsWith("te", StringComparison.Ordinal))
            variants.Add(token[..^1]);

        return variants
            .Where(static variant => variant.Length >= 4)
            .Where(static variant => variant.Any(char.IsLetter))
            .Where(static variant => !LexicalStopwords.Contains(variant))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddAdjacentPhraseTerms(HashSet<string> terms, IReadOnlyList<string> canonicalTokens)
    {
        if (canonicalTokens.Count < 2)
            return;

        var clean = canonicalTokens
            .Where(static token => token.Length >= 4)
            .Where(static token => !LexicalStopwords.Contains(token))
            .Where(static token => !SpecificAnchorStopwords.Contains(token))
            .Take(8)
            .ToArray();

        for (var n = 2; n <= 3; n++)
        {
            if (clean.Length < n)
                continue;

            for (var i = 0; i <= clean.Length - n; i++)
            {
                AddPhraseWithSurfaceVariants(terms, clean.Skip(i).Take(n).ToArray());
            }
        }
    }

    private static void AddPhraseWithSurfaceVariants(HashSet<string> terms, IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
            return;

        var tokenVariants = tokens
            .Select(BuildPhraseSurfaceVariants)
            .Where(static variants => variants.Count > 0)
            .ToArray();
        if (tokenVariants.Length != tokens.Count)
            return;

        var phrases = new List<string> { string.Empty };
        foreach (var variants in tokenVariants)
        {
            var next = new List<string>();
            foreach (var prefix in phrases)
            {
                foreach (var variant in variants)
                {
                    var phrase = string.IsNullOrWhiteSpace(prefix)
                        ? variant
                        : $"{prefix} {variant}";
                    next.Add(phrase);
                    if (next.Count >= 32)
                        break;
                }

                if (next.Count >= 32)
                    break;
            }

            phrases = next;
        }

        foreach (var phrase in phrases)
        {
            if (phrase.Length is >= 9 and <= 80)
                terms.Add(phrase);
        }
    }

    private static IReadOnlyList<string> BuildPhraseSurfaceVariants(string token)
        => BuildLexicalTokenVariants(token)
            .Where(static variant => variant.Length >= 4)
            .Where(static variant => !LexicalStopwords.Contains(variant))
            .OrderBy(variant => string.Equals(variant, token, StringComparison.Ordinal) ? 0 : ContainsNonAscii(variant) ? 1 : 2)
            .ThenBy(static variant => variant.Length)
            .ThenBy(static variant => variant, StringComparer.Ordinal)
            .Take(5)
            .ToArray();

    private static bool ContainsNonAscii(string value)
        => value.Any(static ch => ch > 127);

    internal static string ExpandRetrievalQuery(string query, string? category)
    {
        if (string.IsNullOrWhiteSpace(query))
            return query;

        _ = category;

        var lookup = ExactMatchEntryExtractor.NormalizeForLookup(query);
        var normalized = FoldDiacritics(lookup);
        if (string.IsNullOrWhiteSpace(normalized))
            return query.Trim();

        var trimmed = query.Trim();
        var expandedTerms = BuildQueryExpansionTerms(normalized);
        var additions = new List<string>();
        if (!string.Equals(normalized, lookup, StringComparison.Ordinal))
            additions.Add(normalized);
        foreach (var term in expandedTerms)
        {
            if (!lookup.Contains(term, StringComparison.OrdinalIgnoreCase)
                && !normalized.Contains(term, StringComparison.OrdinalIgnoreCase))
                additions.Add(term);
        }

        return additions.Count == 0
            ? trimmed
            : $"{trimmed} {string.Join(' ', additions.Distinct(StringComparer.OrdinalIgnoreCase))}";
    }

    internal static string ResolvePrimaryRetrievalQuery(string query, string? category)
    {
        if (ContainsDocumentOverviewIntent(query))
            return ExpandRetrievalQuery(query, category);

        var focusedLookupPhrase = ExtractFocusedLookupPhrases(query).FirstOrDefault();
        return ExpandRetrievalQuery(
            string.IsNullOrWhiteSpace(focusedLookupPhrase) ? query : focusedLookupPhrase,
            category);
    }

    internal static IReadOnlyList<string> BuildQueryExpansionTerms(string normalizedQuery)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return Array.Empty<string>();

        // Query expansion must stay corpus-agnostic. Domain synonyms and translations are
        // generated from indexed documents into profiles/content cards instead.
        return Array.Empty<string>();
    }

    internal static string FoldDiacritics(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                _ = ch switch
                {
                    'œ' => sb.Append("oe"),
                    'Œ' => sb.Append("OE"),
                    'æ' => sb.Append("ae"),
                    'Æ' => sb.Append("AE"),
                    'ß' => sb.Append("ss"),
                    'ø' => sb.Append('o'),
                    'Ø' => sb.Append('O'),
                    'ł' => sb.Append('l'),
                    'Ł' => sb.Append('L'),
                    'đ' => sb.Append('d'),
                    'Đ' => sb.Append('D'),
                    _ => sb.Append(ch)
                };
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    internal static double ComputeLexicalCoverage(IReadOnlyList<string> queryTokens, string? candidateText)
    {
        if (queryTokens.Count == 0 || string.IsNullOrWhiteSpace(candidateText))
            return 0.0;

        return ComputeLexicalCoverageInNormalizedText(queryTokens, NormalizeForLexicalSignal(candidateText));
    }

    private static double ComputeLexicalCoverageInNormalizedText(IReadOnlyList<string> queryTokens, string normalizedCandidate)
    {
        if (queryTokens.Count == 0 || string.IsNullOrWhiteSpace(normalizedCandidate))
            return 0.0;

        var matched = 0;
        foreach (var token in queryTokens)
        {
            var variants = BuildLexicalTokenVariants(token);
            if (variants.Any(variant => normalizedCandidate.Contains(variant, StringComparison.Ordinal)))
                matched++;
        }

        return matched == 0 ? 0.0 : (double)matched / queryTokens.Count;
    }

    internal static bool ComputeHypQuestionsMatched(string query, IReadOnlyList<string>? hypotheticalQuestions)
    {
        if (hypotheticalQuestions is null || hypotheticalQuestions.Count == 0)
            return false;

        var queryTokens = ExtractLexicalQueryTokens(query);
        if (queryTokens.Count == 0)
            return false;

        return hypotheticalQuestions.Any(question => ComputeLexicalCoverage(queryTokens, question) >= 0.5);
    }

    internal static bool? ResolveHypQuestionsMatched(string? docPath, IReadOnlyDictionary<string, bool?>? byDocPath)
    {
        if (string.IsNullOrWhiteSpace(docPath) || byDocPath is null)
            return null;

        var normalizedDocPath = NormalizeRagDocPath(docPath);
        if (!string.IsNullOrWhiteSpace(normalizedDocPath)
            && byDocPath.TryGetValue(normalizedDocPath, out var normalizedMatched))
            return normalizedMatched;

        return byDocPath.TryGetValue(docPath, out var matched) ? matched : null;
    }

    internal static double ComputeLinkedMatchScore(double anchorScore, string linkType, string? anchorRetriever = null)
    {
        var penalty = linkType switch
        {
            "same_section" => 0.02,
            "next" => 0.035,
            "prev" => 0.04,
            _ => 0.05
        };

        if (string.Equals(anchorRetriever, "linked_context", StringComparison.Ordinal))
            penalty += 0.015;
        else if (string.Equals(anchorRetriever, "exact_match", StringComparison.Ordinal))
            penalty += 0.01;
        else if (string.Equals(anchorRetriever, "sparse_bm25", StringComparison.Ordinal))
            penalty += 0.005;

        return Math.Max(0.0, anchorScore - penalty);
    }

    private sealed record LinkedAnchorCandidate(Guid SourceId, string SourceRetriever, double AnchorScore);

    private sealed record TitleSelectionSignal(
        RagMatch Match,
        double ExactTitleScore,
        int SpecificAnchorCount,
        bool FullTitleCoverage,
        int StructuredAnswerPriority);

    internal static double ComputeExactMatchScore(string? matchKind, string? matchedTerm, string? text)
    {
        var score = matchKind switch
        {
            "standard_ref" => 1.0,
            "code_ref" => 0.985,
            _ => 0.96
        };

        var normalizedMatchedTerm = ExactMatchEntryExtractor.NormalizeForLookup(matchedTerm ?? string.Empty);
        var normalizedText = ExactMatchEntryExtractor.NormalizeForLookup(text ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(normalizedMatchedTerm))
        {
            score += Math.Min(0.01, normalizedMatchedTerm.Length / 500.0);
            if (string.Equals(normalizedMatchedTerm, normalizedText, StringComparison.Ordinal))
                score += 0.005;
        }

        return Math.Min(1.02, score);
    }

    internal static bool ShouldShortCircuitAfterExact(IReadOnlyList<RagMatch> exactMatches)
    {
        if (exactMatches.Count == 0)
            return false;

        var top = exactMatches[0];
        if (!string.Equals(top.EmbeddingBasis, "exact_match_v1", StringComparison.Ordinal))
            return false;

        var isStrongReferenceHit =
            string.Equals(top.ChunkType, "exact_match_entry", StringComparison.Ordinal)
            || string.Equals(top.ChunkType, "document_metadata_ref", StringComparison.Ordinal);

        if (!isStrongReferenceHit || top.Score < 0.97)
            return false;
        if (LooksLikeNavigationalChunk(top))
            return false;

        if (exactMatches.Count == 1)
            return true;

        var second = exactMatches[1];
        return top.Score - second.Score >= 0.04;
    }

    internal static bool ShouldProbeUnquotedTitleAnchorRoute(
        string query,
        bool skipChunkRetrieversForDocumentOverview,
        bool useScopedProfileFallback)
    {
        if (skipChunkRetrieversForDocumentOverview || useScopedProfileFallback)
            return false;
        if (ExtractQuotedLookupPhrases(query).Count > 0)
            return false;

        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query))} ";
        var focusedPhrases = ExtractFocusedLookupPhrases(query)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var focusedBackfillQuery = BuildFocusedLexicalBackfillQuery(query);
        var focusedBackfillTokens = ExtractLexicalQueryTokens(focusedBackfillQuery);
        var hasPreciseLookupVerbTarget = ContainsPreciseLookupVerb(normalized)
            && focusedBackfillTokens.Count is >= 2 and <= 6;

        if (ShouldPreferComparativeDocumentDiversity(query)
            || ContainsDocumentOverviewIntent(query)
            || ContainsExplicitBroadScopedSynthesisIntent(normalized)
            || ContainsSituationalBroadSelectionIntent(normalized)
            || ContainsGuidanceOrAdviceSelectionIntent(normalized)
            || IsRecommendationSelectionQuery(normalized))
        {
            return false;
        }
        if (ContainsBroadScopedSynthesisIntent(normalized) && !hasPreciseLookupVerbTarget)
            return false;

        if (focusedPhrases.Length == 1 || ShouldConstrainPreciseTitleLookup(query))
            return true;

        return hasPreciseLookupVerbTarget;
    }

    private static bool ContainsPreciseLookupVerb(string normalized)
        => ContainsAny(
            normalized,
            " cherche ",
            " recherche ",
            " chercher ",
            " rechercher ",
            " trouve ",
            " trouver ",
            " find ",
            " search ",
            " looking ",
            " busca ",
            " buscar ",
            " busco ",
            " procura ",
            " procurar ",
            " procuro ",
            " cerca ",
            " cercare ",
            " cerco ",
            " suche ",
            " such ",
            " finde ",
            " finden ");

    internal static bool ShouldShortCircuitAfterTitleAnchorRoute(string query, IReadOnlyList<RagMatch> titleAnchorRouteMatches)
    {
        if (titleAnchorRouteMatches.Count == 0)
            return false;
        if (!ShouldProbeUnquotedTitleAnchorRoute(
                query,
                skipChunkRetrieversForDocumentOverview: false,
                useScopedProfileFallback: false))
        {
            return false;
        }

        var candidates = RankTitleAnchorRouteShortCircuitMatches(titleAnchorRouteMatches);
        if (candidates.Count == 0)
            return false;

        var top = candidates[0];
        var minimumScore = string.Equals(ResolveRetriever(top), "direct_title_token_route", StringComparison.Ordinal)
            ? 0.78
            : 0.90;
        if (top.Score < minimumScore)
            return false;

        var second = candidates.FirstOrDefault(match => !IsSameDocument(top, match));
        if (second is null)
            return true;

        return top.Score - second.Score >= 0.04;
    }

    internal static IReadOnlyList<RagMatch> RankTitleAnchorRouteShortCircuitMatches(IReadOnlyList<RagMatch> titleAnchorRouteMatches)
        => titleAnchorRouteMatches
            .Where(IsTitleAnchorRouteShortCircuitCandidate)
            .OrderByDescending(static match => match.Score)
            .ThenBy(static match => match.DocPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static match => match.ChunkIndex)
            .ToArray();

    private static bool IsTitleAnchorRouteShortCircuitCandidate(RagMatch match)
        => IsResolvedTitleOrNavigationRoute(match)
            && !LooksLikeNavigationalChunk(match)
            && !HasBorderlineNavigationShortcutRisk(match)
            && HasUsefulResolvedTitleBody(match);

    private static bool HasBorderlineNavigationShortcutRisk(RagMatch match)
        => match.NavigationScore is >= 0.72
            && match.ContentDensityScore is null or <= 0.55;

    private static bool HasUsefulResolvedTitleBody(RagMatch match)
    {
        if (GetStructuredAnswerPriority(match) >= 2)
            return true;
        if (match.MatchedContentCards is { Count: > 0 })
            return true;

        return (match.Text?.Trim().Length ?? 0) >= 160;
    }

    internal static bool ShouldShortCircuitAfterQuotedTitle(string query, IReadOnlyList<RagMatch> quotedTitleMatches)
    {
        if (quotedTitleMatches.Count == 0)
            return false;
        if (ExtractQuotedLookupPhrases(query).Count != 1)
            return false;

        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query))} ";
        if (ShouldPreferComparativeDocumentDiversity(query)
            || ContainsDocumentOverviewIntent(query)
            || ContainsEnumerativeLookupIntent(normalized)
            || ContainsGuidanceOrAdviceSelectionIntent(normalized))
        {
            return false;
        }

        var top = quotedTitleMatches[0];
        if (top.Score < 0.90 || LooksLikeNavigationalChunk(top))
            return false;

        if (quotedTitleMatches.Count == 1)
            return true;

        var second = quotedTitleMatches[1];
        if (IsSameDocument(top, second))
            return true;

        return top.Score - second.Score >= 0.04;
    }

    private static bool IsSameDocument(RagMatch left, RagMatch right)
    {
        if (!string.IsNullOrWhiteSpace(left.DocId)
            && !string.IsNullOrWhiteSpace(right.DocId)
            && string.Equals(left.DocId, right.DocId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(left.DocPath)
            && !string.IsNullOrWhiteSpace(right.DocPath)
            && string.Equals(left.DocPath, right.DocPath, StringComparison.OrdinalIgnoreCase);
    }

    internal static string ResolveRetriever(RagMatch match)
        => match.EmbeddingBasis switch
        {
            "exact_match_v1" => "exact_match",
            "sparse_bm25_v1" => "sparse_bm25",
            "document_profile_v1" => "document_profile",
            "linked_context_v1" => "linked_context",
            "title_anchor_route_v1" => "title_anchor_route",
            "navigation_route_v1" => "navigation_route",
            "direct_title_token_route_v1" => "direct_title_token_route",
            "fuzzy_title_lead_v1" => "fuzzy_title_lead",
            _ => "dense_qdrant"
        };

    private static void AccumulateRrf(
        Dictionary<string, RrfAccumulator> accumulators,
        IReadOnlyList<RagMatch> matches,
        int rrfK)
    {
        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var key = BuildMatchDedupKey(match);
            var contribution = 1.0 / (rrfK + i + 1);
            if (accumulators.TryGetValue(key, out var existing))
            {
                accumulators[key] = existing with
                {
                    Score = existing.Score + contribution,
                    Representative = SelectRepresentative(existing.Representative, match)
                };
            }
            else
            {
                accumulators[key] = new RrfAccumulator(contribution, match);
            }
        }
    }

    private static RagMatch SelectRepresentative(RagMatch left, RagMatch right)
    {
        var leftPriority = GetRetrieverPriority(left);
        var rightPriority = GetRetrieverPriority(right);
        if (rightPriority != leftPriority)
            return rightPriority > leftPriority ? right : left;

        return right.Score > left.Score ? right : left;
    }

    private static int GetRetrieverPriority(RagMatch match)
        => ResolveRetriever(match) switch
        {
            "exact_match" => 5,
            "title_anchor_route" => 4,
            "navigation_route" => 4,
            "direct_title_token_route" => 4,
            "fuzzy_title_lead" => 4,
            "sparse_bm25" => 3,
            "dense_qdrant" => 2,
            _ => 1
        };

    private static readonly HashSet<string> LexicalStopwords = new(StringComparer.Ordinal)
    {
        "dans", "avec", "sans", "pour", "vers", "entre", "apres", "avant",
        "quel", "quelle", "quels", "quelles", "trouve", "trouver", "montre",
        "montrez", "ou", "sont", "sous", "plus", "moins", "comme", "cela",
        "cette", "cet", "ces", "leurs", "leur", "par", "sur", "des", "une",
        "bien", "irait", "iraient", "convient", "conviendrait",
        "les", "que", "quoi", "dont", "when", "where", "which", "with", "from",
        "would", "could", "should", "well", "goes",
        "this", "that", "those", "these", "what", "into", "pdf", "doc", "document",
        "manuel", "manual", "guide", "please", "stp", "svp", "cherche", "show",
        "donne", "donner", "donnez", "give",
        "need", "have", "has", "just", "juste", "moi", "peux", "avoir",
        "faire", "fais", "fait", "make", "help", "aide", "aider",
        "parle", "parler", "documents", "compare", "comparer", "comparison",
        "difference", "differences", "different", "deux", "corpus",
        "methode", "method", "style", "lequel", "veux", "veut", "simple",
        "fiche", "claire", "clair", "detail", "details", "detailee", "detaillee",
        "etape", "etapes", "step", "steps", "source", "sources", "citation",
        "citations", "reponse", "answer", "format", "liste", "list",
        "plusieurs", "multiple", "several", "many",
        "temps", "time", "duration", "duree",
        "existe", "existes", "existent", "exister", "existing", "exist", "exists",
        "disponible", "disponibles", "available", "availability",
        "utile", "utiles", "useful", "pertinent", "pertinents", "relevant",
        "contient", "contiennent", "containing", "contain", "contains",
        "mentionne", "mentionnent", "mentioning", "mentions",
        "presente", "presentent", "present", "presents"
    };

    private static readonly HashSet<string> SpecificAnchorStopwords = new(StringComparer.Ordinal)
    {
        "items", "elements",
        "preparation", "materials", "materiaux", "components", "composants", "procedure", "procedures",
        "methode", "method", "methods", "instruction", "instructions",
        "materiel", "material", "materials", "requirement", "requirements",
        "warning", "warnings", "caution", "cautions", "summary", "overview",
        "checklist", "source", "sources", "citation", "citations", "detail",
        "details", "format", "answer", "reponse", "liste", "list",
        "temps", "time", "duration", "duree"
    };

    private static readonly HashSet<string> DocumentOverviewTopicStopwords = new(StringComparer.Ordinal)
    {
        "document", "documents", "doc", "docs", "pdf", "fichier", "fichiers",
        "file", "files", "source", "sources", "livre", "livres", "book", "books",
        "rapport", "rapports", "report", "reports", "manuel", "manuels", "manual", "manuals",
        "corpus", "base", "knowledge", "database", "disponible", "disponibles", "available",
        "inventaire", "inventory", "overview", "panorama", "panoramica", "uberblick",
        "liste", "list", "lister", "vois", "voir", "servir", "sert", "servent",
        "documentos", "fuentes", "archivos", "libros", "ficheiros", "arquivos", "livros",
        "dokumente", "quellen", "dateien", "bucher", "berichte", "handbucher",
        "documenti", "fonti", "libri", "manuali"
    };

    private static readonly HashSet<string> PrimaryAnchorStopwords = new(StringComparer.Ordinal)
    {
        "organiser", "organise", "organization", "organisation", "planifier",
        "planning", "prevoir", "preparer", "prepare", "prepared", "avance",
        "weekly", "hebdomadaire", "semaine",
        "question", "questions", "utilisateur", "client", "besoin", "conseil",
        "conseils", "recommendation", "recommendations"
    };

    private static readonly HashSet<string> TitleConnectorTokens = new(StringComparer.Ordinal)
    {
        "a", "al", "alla", "all", "an", "and", "as", "at", "au", "aux",
        "avec", "com", "con", "d", "da", "de", "del", "della", "des", "di", "do",
        "dos", "du", "e", "em", "en", "et", "for", "la", "las", "le", "les",
        "lo", "los", "mit", "of", "on", "the", "to", "und", "with", "y", "zu",
        "zum", "zur"
    };

    private static readonly HashSet<string> ComparativeSubjectStopwords = new(StringComparer.Ordinal)
    {
        "est", "sont", "etre", "etaient", "sera", "serait", "is", "are", "was", "were",
        "the", "le", "la", "les", "un", "une", "des", "du", "de", "del", "der", "die",
        "quel", "quelle", "quels", "quelles", "which", "what", "cual", "qual", "quale",
        "welches", "welcher", "welche", "plus", "most", "mas", "mais", "mehr"
    };

    private static readonly HashSet<string> DocumentHintStopwords = new(StringComparer.Ordinal)
    {
        "PDF", "DOC", "DOCUMENT", "GUIDE", "GUIDANCE", "MANUAL", "MANUEL", "NOTICE",
        "SYSTEM", "SYSTEMS", "PROCESS", "PROCESSUS", "PROCEDURE",
        "THE", "FOR", "AND", "WITH", "SUR",
        "POUR", "DES", "LES", "UNE", "UN", "DU", "DE", "LA", "LE", "ET", "ON", "OF"
    };

    private sealed record RrfAccumulator(double Score, RagMatch Representative);
    internal sealed record MatchedRetrievalContext(IReadOnlyList<string> DocHints);
    private sealed record GuidanceQualityRisk(bool RequiresCaveat, string Reason);

    internal static string ResolveProvenance(RagMatch match)
        => $"retriever:{ResolveRetriever(match)}";

    internal static RagAnswerGuidanceDto BuildAnswerGuidance(string query, IReadOnlyList<RagMatch> matches)
        => BuildAnswerGuidance(
            query,
            matches,
            new Dictionary<string, RagItemExtractionQualityDto>(StringComparer.OrdinalIgnoreCase));

    internal static RagAnswerGuidanceDto BuildAnswerGuidance(
        string query,
        IReadOnlyList<RagMatch> matches,
        IReadOnlyDictionary<string, RagItemExtractionQualityDto> extractionQualityByMatch)
    {
        var normalized = NormalizeQueryForGuidance(query);
        var guidanceLanguage = ResolveGuidanceLanguage(query);
        var matchedContext = BuildMatchedRetrievalContext(matches);
        var matchedDocHints = matchedContext.DocHints;

        if (matches.Count == 0)
        {
            return new RagAnswerGuidanceDto(
                Behavior: "answer_with_caveat",
                Reason: "no_relevant_source_found",
                ResponseShape: "no_source_match",
                QualificationNote: BuildNoRelevantSourceNote(guidanceLanguage),
                MatchedDocHints: matchedDocHints);
        }

        if (ContainsPlaceholderStandard(normalized))
        {
            return new RagAnswerGuidanceDto(
                Behavior: "ask_clarification",
                Reason: "missing_standard_identifier",
                ResponseShape: "clarify",
                ClarifyingQuestion: BuildClarifyingQuestion(normalized, matchedContext, "missing_standard_identifier", guidanceLanguage),
                MatchedDocHints: matchedDocHints);
        }

        if (ContainsCertificationScopeQuestion(normalized))
        {
            return new RagAnswerGuidanceDto(
                Behavior: "ask_clarification",
                Reason: "certification_requires_precise_scope",
                ResponseShape: "clarify",
                ClarifyingQuestion: BuildClarifyingQuestion(normalized, matchedContext, "certification_requires_precise_scope", guidanceLanguage),
                MatchedDocHints: matchedDocHints);
        }

        if (ContainsBroadComplianceQuestion(normalized))
        {
            return new RagAnswerGuidanceDto(
                Behavior: "ask_clarification",
                Reason: "broad_compliance_requires_scope",
                ResponseShape: "clarify",
                ClarifyingQuestion: BuildClarifyingQuestion(normalized, matchedContext, "broad_compliance_requires_scope", guidanceLanguage),
                MatchedDocHints: matchedDocHints);
        }

        var qualityRisk = AssessGuidanceQualityRisk(matches, extractionQualityByMatch);
        if (qualityRisk.RequiresCaveat)
        {
            return new RagAnswerGuidanceDto(
                Behavior: "answer_with_caveat",
                Reason: qualityRisk.Reason,
                ResponseShape: DetermineResponseShape(normalized, behavior: "answer_with_caveat"),
                QualificationNote: BuildSourceQualityQualificationNote(matchedContext, guidanceLanguage),
                MatchedDocHints: matchedDocHints);
        }

        if (ContainsSufficiencyQuestion(normalized))
        {
            return new RagAnswerGuidanceDto(
                Behavior: "answer",
                Reason: "document_scope_limit_can_be_answered_directly",
                ResponseShape: "qualified_answer",
                MatchedDocHints: matchedDocHints);
        }

        var simpleDocumentSelection = ContainsSimpleDocumentSelectionQuestion(normalized);
        var needsQualification =
            ContainsRiskRecommendationQuestion(normalized)
            || ContainsQuickCustomerReplySelectionQuestion(normalized)
            || (!simpleDocumentSelection && (
                ContainsComplianceLanguage(normalized)
                || ContainsCustomerReplyLanguage(normalized)
                || ContainsProjectAssessmentLanguage(normalized)
                || ContainsRiskRecommendationQuestion(normalized)));

        if (needsQualification)
        {
            return new RagAnswerGuidanceDto(
                Behavior: "answer_with_caveat",
                Reason: "high_impact_or_safety_answer_requires_qualification",
                ResponseShape: DetermineResponseShape(normalized, behavior: "answer_with_caveat"),
                QualificationNote: BuildHighImpactSafetyQualificationNote(normalized, matchedContext, simpleDocumentSelection, guidanceLanguage),
                MatchedDocHints: matchedDocHints);
        }

        return new RagAnswerGuidanceDto(
            Behavior: "answer",
            Reason: "documented_question_with_relevant_sources",
            ResponseShape: DetermineResponseShape(normalized, behavior: "answer"),
            MatchedDocHints: matchedDocHints);
    }

    private static GuidanceQualityRisk AssessGuidanceQualityRisk(
        IReadOnlyList<RagMatch> matches,
        IReadOnlyDictionary<string, RagItemExtractionQualityDto> extractionQualityByMatch)
    {
        foreach (var match in matches.Take(5))
        {
            var quality = ResolveExtractionQuality(match, extractionQualityByMatch);
            if (quality is null)
                continue;

            if (quality.DocumentManualReviewRecommended == true || quality.PageManualReviewRecommended == true)
                return new GuidanceQualityRisk(true, "source_quality_requires_manual_review_caveat");

            var confidence = quality.PageExtractionConfidence ?? quality.DocumentExtractionConfidence;
            if (confidence is <= 0.45)
                return new GuidanceQualityRisk(true, "source_quality_low_confidence_caveat");

            var status = string.Join(' ', quality.PageQualityStatus, quality.DocumentQualityStatus, quality.TextStatus)
                .ToLowerInvariant();
            if (status.Contains("ocr_failed", StringComparison.Ordinal)
                || status.Contains("empty_text", StringComparison.Ordinal)
                || status.Contains("low_text", StringComparison.Ordinal)
                || status.Contains("low_confidence", StringComparison.Ordinal)
                || status.Contains("manual_review", StringComparison.Ordinal))
            {
                return new GuidanceQualityRisk(true, "source_quality_status_caveat");
            }
        }

        return new GuidanceQualityRisk(false, "source_quality_ok");
    }

    internal static RagItemProvenanceDto BuildProvenanceInfo(RagMatch match, string? sourceHash = null, bool allowLegacyHashFallback = true)
        => new(
            Channel: ResolveRetriever(match),
            Label: ResolveProvenance(match),
            SourceHash: sourceHash ?? (allowLegacyHashFallback ? match.HashDoc : null),
            ChunkId: match.ChunkId,
            PageStart: match.PageStart,
            PageEnd: match.PageEnd,
            OffsetStart: match.OffsetStart,
            OffsetEnd: match.OffsetEnd);

    internal static RagItemContextDto BuildContextInfo(RagMatch match)
        => new(
            ChunkType: match.ChunkType,
            SectionTitle: match.SectionTitle,
            HeadingPath: match.HeadingPath,
            PrevChunkId: match.PrevChunkId,
            NextChunkId: match.NextChunkId,
            SameSectionChunkId: match.SameSectionChunkId,
            ContentRole: match.ContentRole,
            NavigationReason: match.NavigationReason,
            OriginalChunkType: match.OriginalChunkType,
            NavigationScore: match.NavigationScore,
            ContentDensityScore: match.ContentDensityScore);

    internal static async Task<IReadOnlyDictionary<string, string>> LoadTopCategoryRefsAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        IReadOnlyList<RagMatch> matches,
        CancellationToken ct)
    {
        var topLevelPaths = matches
            .Select(match => ExtractTopLevelCategoryPath(BuildDocumentCategoryPath(match.DocPath)))
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (topLevelPaths.Length == 0)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
SELECT
  path          AS "Path",
  display_order AS "DisplayOrder"
FROM documents_catalog_categories
WHERE tenant_id=@tenant
  AND path = ANY(@paths);
""";

        var rows = await conn.QueryAsync<TopCategoryOrderRow>(new CommandDefinition(
            sql,
            new { tenant = tenantId, paths = topLevelPaths },
            cancellationToken: ct));

        return rows.ToDictionary(
            static row => row.Path,
            static row => BuildCategoryRef(row.DisplayOrder),
            StringComparer.OrdinalIgnoreCase);
    }

    internal static string? BuildDocumentCategory(string? docPath)
    {
        var topLevel = ExtractTopLevelCategoryPath(BuildDocumentCategoryPath(docPath));
        return string.IsNullOrWhiteSpace(topLevel)
            ? null
            : topLevel.ToLowerInvariant();
    }

    internal static string? ResolveMatchCategory(RagMatch match, string? responseCategory)
    {
        var storedCategory = NormalizeRagCategory(match.Category);
        if (!string.IsNullOrWhiteSpace(storedCategory))
            return storedCategory;

        return BuildDocumentCategory(match.DocPath) ?? NormalizeRagCategory(responseCategory);
    }

    private static string? NormalizeRagCategory(string? category)
        => string.IsNullOrWhiteSpace(category) ? null : category.Trim().ToLowerInvariant();

    internal static string? BuildDocumentCategoryPath(string? docPath)
    {
        if (string.IsNullOrWhiteSpace(docPath))
            return null;

        var normalized = docPath.Trim().Replace('\\', '/').Trim('/');
        var slashIndex = normalized.LastIndexOf('/');
        if (slashIndex <= 0)
            return null;

        return normalized[..slashIndex];
    }

    internal static string? ResolveCategoryRef(string? categoryPath, IReadOnlyDictionary<string, string> categoryRefsByTopLevelPath)
    {
        var topLevel = ExtractTopLevelCategoryPath(categoryPath);
        if (string.IsNullOrWhiteSpace(topLevel))
            return null;

        return categoryRefsByTopLevelPath.TryGetValue(topLevel, out var categoryRef)
            ? categoryRef
            : null;
    }

    internal static string? BuildSnippet(string? text, int maxLength = 500, string? query = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var normalized = text.Trim();
        if (normalized.Length <= maxLength)
            return normalized;

        var anchor = FindQuerySnippetAnchor(normalized, query);
        if (anchor >= 0)
            return BuildCenteredSnippet(normalized, anchor, maxLength);

        // Cut at last sentence boundary within limit
        var cutoff = normalized.LastIndexOf('.', maxLength - 1);
        if (cutoff < maxLength / 2)
            cutoff = normalized.LastIndexOf(' ', maxLength - 1);
        if (cutoff < maxLength / 2)
            cutoff = maxLength;

        return normalized[..(cutoff + 1)].TrimEnd();
    }

    private static int FindQuerySnippetAnchor(string text, string? query)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(query))
            return -1;

        var folded = BuildFoldedTextIndex(text);
        if (folded.Text.Length == 0 || folded.OriginalIndexes.Count == 0)
            return -1;

        var tokens = ExtractLexicalQueryTokens(query)
            .Where(static token => !SnippetAnchorLowPriorityTerms.Contains(token))
            .OrderByDescending(static token => token.Length)
            .ThenBy(static token => token, StringComparer.Ordinal)
            .ToArray();

        if (tokens.Length == 0)
        {
            tokens = ExtractLexicalQueryTokens(query)
                .OrderByDescending(static token => token.Length)
                .ThenBy(static token => token, StringComparer.Ordinal)
                .ToArray();
        }

        foreach (var token in tokens)
        {
            var index = folded.Text.IndexOf(token, StringComparison.Ordinal);
            if (index >= 0 && index < folded.OriginalIndexes.Count)
                return folded.OriginalIndexes[index];
        }

        return -1;
    }

    private static string BuildCenteredSnippet(string text, int anchor, int maxLength)
    {
        if (maxLength <= 20 || text.Length <= maxLength)
            return text.Length <= maxLength ? text : text[..maxLength].TrimEnd();

        var start = Math.Max(0, anchor - (maxLength / 3));
        start = MoveSnippetStartToBoundary(text, start, anchor);

        if (start + maxLength > text.Length)
            start = Math.Max(0, text.Length - maxLength);

        var length = Math.Min(maxLength, text.Length - start);
        var end = start + length;
        end = MoveSnippetEndToBoundary(text, end, start);

        var snippet = text[start..end].Trim();
        if (start > 0)
            snippet = "..." + snippet.TrimStart();
        if (end < text.Length)
            snippet = snippet.TrimEnd() + "...";

        return snippet.Length <= maxLength
            ? snippet
            : snippet[..maxLength].TrimEnd();
    }

    private static int MoveSnippetStartToBoundary(string text, int start, int anchor)
    {
        if (start <= 0)
            return 0;

        var searchLength = Math.Max(0, anchor - start);
        if (searchLength == 0)
            return start;

        var sentence = text.LastIndexOfAny(new[] { '.', '!', '?', '\n', '\r' }, start + searchLength - 1, searchLength);
        if (sentence >= start && sentence + 1 < anchor)
            return sentence + 1;

        var space = text.IndexOf(' ', start);
        return space >= 0 && space < anchor ? space + 1 : start;
    }

    private static int MoveSnippetEndToBoundary(string text, int end, int start)
    {
        if (end >= text.Length)
            return text.Length;

        var sentence = text.LastIndexOfAny(new[] { '.', '!', '?', '\n', '\r' }, end - 1, Math.Max(0, end - start));
        if (sentence > start + ((end - start) / 2))
            return sentence + 1;

        var space = text.LastIndexOf(' ', end - 1, Math.Max(0, end - start));
        return space > start + ((end - start) / 2) ? space : end;
    }

    private static FoldedTextIndex BuildFoldedTextIndex(string value)
    {
        var sb = new StringBuilder(value.Length);
        var indexes = new List<int>(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            var normalized = value[i].ToString().Normalize(NormalizationForm.FormD);
            foreach (var ch in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                    continue;

                foreach (var folded in FoldSnippetChar(ch))
                {
                    sb.Append(char.ToLowerInvariant(folded));
                    indexes.Add(i);
                }
            }
        }

        return new FoldedTextIndex(sb.ToString().Normalize(NormalizationForm.FormC), indexes);
    }

    private static string FoldSnippetChar(char ch)
        => ch switch
        {
            'œ' or 'Œ' => "oe",
            'æ' or 'Æ' => "ae",
            'ß' => "ss",
            'ø' or 'Ø' => "o",
            'ł' or 'Ł' => "l",
            'đ' or 'Đ' => "d",
            _ => ch.ToString()
        };

    private static readonly HashSet<string> SnippetAnchorLowPriorityTerms = new(StringComparer.Ordinal)
    {
        "document",
        "documents",
        "fiche",
        "claire",
        "faire",
        "peux",
        "veux",
        "donne",
        "trouve",
        "etapes",
        "etape",
        "temps",
        "source",
        "sources",
        "page",
        "pages"
    };

    private sealed record FoldedTextIndex(string Text, IReadOnlyList<int> OriginalIndexes);

    internal static bool DetectHasTable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        // Heuristic: rows with pipe separators or tab-separated columns
        var lines = text.Split('\n');
        var pipeLines = lines.Count(l => l.Contains('|') && l.Count(c => c == '|') >= 2);
        return pipeLines >= 2;
    }

    internal static bool DetectHasWarning(string? text, string? chunkType)
    {
        if (string.Equals(chunkType, "warning", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var upper = text.AsSpan();
        return upper.Contains("WARNING", StringComparison.OrdinalIgnoreCase)
            || upper.Contains("DANGER", StringComparison.OrdinalIgnoreCase)
            || upper.Contains("CAUTION", StringComparison.OrdinalIgnoreCase)
            || upper.Contains("AVERTISSEMENT", StringComparison.OrdinalIgnoreCase)
            || upper.Contains("ATTENTION", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? ComputeDataHash(IReadOnlyList<RagMatch> matches)
    {
        if (matches.Count == 0)
            return null;

        using var sha = SHA256.Create();
        foreach (var match in matches)
        {
            var canonical = $"{match.DocId}|{match.ChunkId}|{match.IngestionVersion}|{match.HashDoc}|{match.EmbeddingBasis}";
            var bytes = Encoding.UTF8.GetBytes(canonical);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    internal static IReadOnlyList<string> ExtractMatchedDocHints(IReadOnlyList<RagMatch> matches)
    {
        var hints = new HashSet<string>(StringComparer.Ordinal);

        foreach (var match in matches)
        {
            CollectDocumentHints(match.DocName, hints);
            CollectDocumentHints(match.DocPath, hints);
        }

        if (hints.Count == 0)
            return Array.Empty<string>();

        var ordered = hints.OrderBy(static h => h, StringComparer.Ordinal).ToArray();
        var filtered = ordered
            .Where(hint => !ordered.Any(other =>
                !string.Equals(other, hint, StringComparison.Ordinal)
                && other.Length < hint.Length
                && hint.EndsWith(other, StringComparison.Ordinal)))
            .ToArray();

        return filtered.Length == 0 ? ordered : filtered;
    }

    internal static MatchedRetrievalContext BuildMatchedRetrievalContext(IReadOnlyList<RagMatch> matches)
    {
        var hints = ExtractMatchedDocHints(matches);

        return new MatchedRetrievalContext(hints);
    }

    private static bool ContainsPlaceholderStandard(string normalizedQuery)
        => normalizedQuery.Contains("norme xxx", StringComparison.Ordinal)
           || normalizedQuery.Contains("standard xxx", StringComparison.Ordinal);

    private static bool ContainsCertificationScopeQuestion(string normalizedQuery)
    {
        var functionSafetyClaim =
            (normalizedQuery.Contains("fonction de securite", StringComparison.Ordinal)
                && (normalizedQuery.Contains("affirmer", StringComparison.Ordinal)
                    || normalizedQuery.Contains("adapte", StringComparison.Ordinal)
                    || normalizedQuery.Contains("adapted", StringComparison.Ordinal)
                    || normalizedQuery.Contains("claim", StringComparison.Ordinal)))
            || (normalizedQuery.Contains("safety function", StringComparison.Ordinal)
                && (normalizedQuery.Contains("claim", StringComparison.Ordinal)
                    || normalizedQuery.Contains("adapted", StringComparison.Ordinal)));
        if (functionSafetyClaim)
            return !ContainsSimpleDocumentSelectionQuestion(normalizedQuery);

        return (normalizedQuery.Contains("certification", StringComparison.Ordinal)
                || normalizedQuery.Contains("certifie", StringComparison.Ordinal)
                || normalizedQuery.Contains("certifiee", StringComparison.Ordinal)
                || normalizedQuery.Contains("homologation", StringComparison.Ordinal)
                || normalizedQuery.Contains("approval", StringComparison.Ordinal)
                || normalizedQuery.Contains("approved", StringComparison.Ordinal)
                || normalizedQuery.Contains("qualification", StringComparison.Ordinal))
            && !HasSpecificReferenceLookup(normalizedQuery)
            && !ContainsSimpleDocumentSelectionQuestion(normalizedQuery);
    }

    private static bool ContainsComplianceLanguage(string normalizedQuery)
        => normalizedQuery.Contains("respecte", StringComparison.Ordinal)
           || normalizedQuery.Contains("conforme", StringComparison.Ordinal)
           || normalizedQuery.Contains("conformite", StringComparison.Ordinal)
           || normalizedQuery.Contains("compliance", StringComparison.Ordinal);

    private static bool ContainsHighImpactClaimLanguage(string normalizedQuery)
    {
        var foldedQuery = FoldDiacritics(normalizedQuery);
        return HighImpactClaimTerms.Any(term => ContainsNormalizedWholeTerm(foldedQuery, term));
    }

    private static readonly string[] HighImpactClaimTerms =
    [
        "garantir",
        "garantie",
        "obligation",
        "obligatoire",
        "reglementaire",
        "regulatory",
        "legal",
        "securite",
        "safety",
        "protection",
        "protege",
        "danger",
        "risque",
        "risques",
        "risk",
        "risks",
        "critique",
        "critical",
        "fiabilite",
        "reliability"
    ];

    private static bool ContainsNormalizedWholeTerm(string normalizedText, string term)
        => Regex.IsMatch(
            normalizedText,
            $@"(?:^|[^\p{{L}}\p{{N}}]){Regex.Escape(term)}(?:$|[^\p{{L}}\p{{N}}])",
            RegexOptions.CultureInvariant);

    private static bool ContainsBroadComplianceQuestion(string normalizedQuery)
        => ContainsComplianceLanguage(normalizedQuery)
           && !HasSpecificReferenceLookup(normalizedQuery)
           && ContainsBroadScopeLanguage(normalizedQuery);

    private static bool ContainsBroadScopeLanguage(string normalizedQuery)
        => normalizedQuery.Contains("partout", StringComparison.Ordinal)
           || normalizedQuery.Contains("en general", StringComparison.Ordinal)
           || normalizedQuery.Contains("global", StringComparison.Ordinal)
           || normalizedQuery.Contains("globalement", StringComparison.Ordinal)
           || normalizedQuery.Contains("juste", StringComparison.Ordinal)
           || normalizedQuery.Contains("sans plus", StringComparison.Ordinal)
           || normalizedQuery.Contains("overall", StringComparison.Ordinal)
           || normalizedQuery.Contains("everywhere", StringComparison.Ordinal)
           || normalizedQuery.Contains("generally", StringComparison.Ordinal)
           || normalizedQuery.Contains("in general", StringComparison.Ordinal);

    private static bool ContainsCustomerReplyLanguage(string normalizedQuery)
        => normalizedQuery.Contains("on lui repond", StringComparison.Ordinal)
           || normalizedQuery.Contains("tu repondrais", StringComparison.Ordinal)
           || normalizedQuery.Contains("reponse prudente", StringComparison.Ordinal)
           || normalizedQuery.Contains("qu est ce qu il faut lui demander", StringComparison.Ordinal)
           || normalizedQuery.Contains("qu'est ce qu'il faut lui demander", StringComparison.Ordinal);

    private static bool ContainsProjectAssessmentLanguage(string normalizedQuery)
        => normalizedQuery.Contains("projet", StringComparison.Ordinal)
           || normalizedQuery.Contains("notre systeme", StringComparison.Ordinal)
           || normalizedQuery.Contains("notre installation", StringComparison.Ordinal);

    private static bool ContainsSufficiencyQuestion(string normalizedQuery)
        => normalizedQuery.Contains("suffit a lui seul", StringComparison.Ordinal)
           || normalizedQuery.Contains("suffit a elle seule", StringComparison.Ordinal);

    private static bool ContainsSimpleDocumentSelectionQuestion(string normalizedQuery)
        => normalizedQuery.Contains("quel document faut il citer", StringComparison.Ordinal)
           || normalizedQuery.Contains("quel document faut-il citer", StringComparison.Ordinal)
           || normalizedQuery.Contains("lequel pour parler", StringComparison.Ordinal);

    private static bool ContainsRiskRecommendationQuestion(string normalizedQuery)
    {
        if (ContainsDirectFactualAnswerQuestion(normalizedQuery))
            return false;

        if (normalizedQuery.Contains("compare", StringComparison.Ordinal)
            || normalizedQuery.Contains("comparer", StringComparison.Ordinal)
            || normalizedQuery.Contains("difference", StringComparison.Ordinal)
            || normalizedQuery.Contains("distingue", StringComparison.Ordinal)
            || normalizedQuery.Contains("distinguer", StringComparison.Ordinal)
            || normalizedQuery.Contains("expliquer le fonctionnement", StringComparison.Ordinal)
            || normalizedQuery.Contains("explique simplement", StringComparison.Ordinal)
            || normalizedQuery.Contains("quand on utiliserait", StringComparison.Ordinal)
            || normalizedQuery.Contains("quand utiliser", StringComparison.Ordinal))
        {
            return false;
        }

        if (!ContainsCustomerOrAdviceLanguage(normalizedQuery))
        {
            return false;
        }

        return ContainsOperationalAdviceLanguage(normalizedQuery)
            || ContainsHighImpactClaimLanguage(normalizedQuery)
            || ContainsBroadComplianceQuestion(normalizedQuery);
    }

    private static bool ContainsCustomerOrAdviceLanguage(string normalizedQuery)
        => normalizedQuery.Contains("client", StringComparison.Ordinal)
           || normalizedQuery.Contains("on peut", StringComparison.Ordinal)
           || normalizedQuery.Contains("peut dire", StringComparison.Ordinal)
           || normalizedQuery.Contains("dit quoi", StringComparison.Ordinal)
           || normalizedQuery.Contains("demande", StringComparison.Ordinal)
           || normalizedQuery.Contains("repondre", StringComparison.Ordinal)
           || normalizedQuery.Contains("repondrais", StringComparison.Ordinal)
           || normalizedQuery.Contains("recommend", StringComparison.Ordinal)
           || normalizedQuery.Contains("recommendation", StringComparison.Ordinal);

    private static bool ContainsOperationalAdviceLanguage(string normalizedQuery)
        => ContainsCustomerReplyLanguage(normalizedQuery)
           || ContainsOperationalInstallationAdvice(normalizedQuery)
           || ContainsOperationalIntegrationSelection(normalizedQuery)
           || ContainsOperationalTargetAdvice(normalizedQuery)
           || ContainsOperationalUseAdvice(normalizedQuery);

    private static bool ContainsOperationalInstallationAdvice(string normalizedQuery)
        => (normalizedQuery.Contains("installer", StringComparison.Ordinal)
            || normalizedQuery.Contains("install", StringComparison.Ordinal))
           && (ContainsHighImpactClaimLanguage(normalizedQuery)
               || ContainsHighImpactSafetyContextLanguage(normalizedQuery));

    private static bool ContainsHighImpactSafetyContextLanguage(string normalizedQuery)
    {
        // General safety policy: operational guidance in high-impact industrial safety contexts stays qualified.
        var foldedQuery = FoldDiacritics(normalizedQuery);
        return HighImpactSafetyContextTerms.Any(term => foldedQuery.Contains(term, StringComparison.Ordinal));
    }

    private static readonly string[] HighImpactSafetyContextTerms =
    [
        "zone potentiellement explosive",
        "zone explosive",
        "atmosphere explosive",
        "zone dangereuse",
        "zone a risque",
        "environnement dangereux",
        "environnement a risque",
        "securite industrielle",
        "risque d explosion",
        "risques industriels",
        "prevention explosion",
        "hazardous area",
        "hazardous location",
        "hazardous environment",
        "dangerous area",
        "explosive atmosphere",
        "potentially explosive atmosphere",
        "explosion hazard",
        "explosion risk",
        "explosion protection",
        "explosion prevention",
        "industrial safety",
        "safety critical",
        "seguridad industrial",
        "area peligrosa",
        "zona peligrosa",
        "atmosfera explosiva",
        "riesgo de explosion",
        "seguranca industrial",
        "area perigosa",
        "zona perigosa",
        "risco de explosao",
        "industrielle sicherheit",
        "explosionsgefahrdeter bereich",
        "explosionsgefaehrdeter bereich",
        "gefahrlicher bereich",
        "explosionsgefahr",
        "sicurezza industriale",
        "area pericolosa",
        "atmosfera esplosiva",
        "rischio di esplosione"
    ];

    private static bool ContainsOperationalIntegrationSelection(string normalizedQuery)
        => (normalizedQuery.Contains("relier", StringComparison.Ordinal)
            || normalizedQuery.Contains("raccorder", StringComparison.Ordinal)
            || normalizedQuery.Contains("connecter", StringComparison.Ordinal)
            || normalizedQuery.Contains("integrer", StringComparison.Ordinal)
            || normalizedQuery.Contains("connect", StringComparison.Ordinal)
            || normalizedQuery.Contains("integrate", StringComparison.Ordinal))
           && (normalizedQuery.Contains("lequel", StringComparison.Ordinal)
               || normalizedQuery.Contains("quel document", StringComparison.Ordinal)
               || normalizedQuery.Contains("documents aide", StringComparison.Ordinal)
               || normalizedQuery.Contains("aide le plus", StringComparison.Ordinal));

    private static bool ContainsOperationalTargetAdvice(string normalizedQuery)
        => (normalizedQuery.Contains("viser", StringComparison.Ordinal)
            || normalizedQuery.Contains("target", StringComparison.Ordinal))
           && ContainsHighImpactClaimLanguage(normalizedQuery);

    private static bool ContainsOperationalUseAdvice(string normalizedQuery)
        => (normalizedQuery.Contains("utiliser", StringComparison.Ordinal)
            || normalizedQuery.Contains("use ", StringComparison.Ordinal))
           && (ContainsCustomerReplyLanguage(normalizedQuery)
               || normalizedQuery.Contains("repondre quoi", StringComparison.Ordinal)
               || normalizedQuery.Contains("document en dit quelque chose", StringComparison.Ordinal)
               || normalizedQuery.Contains("point de vigilance", StringComparison.Ordinal)
               || (ContainsHighImpactClaimLanguage(normalizedQuery)
                   && (normalizedQuery.Contains(" pour ", StringComparison.Ordinal)
                       || normalizedQuery.Contains(" comme ", StringComparison.Ordinal))));

    private static bool ContainsDirectFactualAnswerQuestion(string normalizedQuery)
    {
        return normalizedQuery.Contains("c est autorise", StringComparison.Ordinal)
            || normalizedQuery.Contains("est ce autorise", StringComparison.Ordinal)
            || normalizedQuery.Contains("est-ce autorise", StringComparison.Ordinal)
            || normalizedQuery.Contains("est ce qu on peut utiliser", StringComparison.Ordinal)
            || normalizedQuery.Contains("est-ce qu on peut utiliser", StringComparison.Ordinal)
            || normalizedQuery.Contains("est ce qu'on peut utiliser", StringComparison.Ordinal)
            || normalizedQuery.Contains("se connecte", StringComparison.Ordinal)
            || normalizedQuery.Contains("couvre aussi", StringComparison.Ordinal)
            || normalizedQuery.Contains("couvre ", StringComparison.Ordinal)
            || normalizedQuery.Contains("toutes les versions", StringComparison.Ordinal)
            || normalizedQuery.Contains("tous les modeles", StringComparison.Ordinal)
            || normalizedQuery.Contains("all versions", StringComparison.Ordinal)
            || normalizedQuery.Contains("all models", StringComparison.Ordinal);
    }

    private static bool ContainsQuickCustomerReplySelectionQuestion(string normalizedQuery)
        => normalizedQuery.Contains("repondre vite au client", StringComparison.Ordinal)
           || normalizedQuery.Contains("ouvrir en premier", StringComparison.Ordinal)
           || normalizedQuery.Contains("ouvre en premier", StringComparison.Ordinal)
           || normalizedQuery.Contains("lequel des deux docs", StringComparison.Ordinal)
           || (normalizedQuery.Contains("par lequel", StringComparison.Ordinal)
               && normalizedQuery.Contains("client", StringComparison.Ordinal));

    private static string ResolveGuidanceLanguage(string query)
    {
        var primary = DocumentLanguageResolver.PrimarySubtag(DocumentLanguageResolver.DetectDominantLanguage(query));
        return primary is "en" or "es" or "pt" or "de" or "it" ? primary : "fr";
    }

    private static string BuildNoRelevantSourceNote(string guidanceLanguage)
        => guidanceLanguage switch
        {
            "en" => "No sufficiently relevant source was retrieved for this query.",
            "es" => "No se ha recuperado ninguna fuente suficientemente pertinente para esta consulta.",
            "pt" => "Nenhuma fonte suficientemente relevante foi recuperada para esta pergunta.",
            "de" => "Fuer diese Anfrage wurde keine ausreichend relevante Quelle gefunden.",
            "it" => "Non e stata recuperata nessuna fonte sufficientemente pertinente per questa richiesta.",
            _ => "Aucune source suffisamment pertinente n'a ete retrouvee pour cette question."
        };

    private static string BuildClarifyingQuestion(string normalizedQuery, MatchedRetrievalContext matchedContext, string reason, string guidanceLanguage)
    {
        if (string.Equals(reason, "certification_requires_precise_scope", StringComparison.Ordinal))
        {
            return guidanceLanguage switch
            {
                "en" => "Which certification or qualification do you mean, for which exact scope, which item, and which usage conditions?",
                "es" => "A que certificacion o cualificacion te refieres, con que alcance exacto, que elemento y que condiciones de uso?",
                "pt" => "A que certificacao ou qualificacao se refere, com que escopo exato, que elemento e que condicoes de uso?",
                "de" => "Welche Zertifizierung oder Qualifikation meinst du, fuer welchen genauen Umfang, welches Element und welche Nutzungsbedingungen?",
                "it" => "A quale certificazione o qualificazione ti riferisci, con quale perimetro esatto, quale elemento e quali condizioni d'uso?",
                _ => "Tu parles de quelle certification ou qualification, pour quel perimetre exact, quel element concerne et quelles conditions d'utilisation ?"
            };
        }

        if (string.Equals(reason, "broad_compliance_requires_scope", StringComparison.Ordinal))
        {
            return guidanceLanguage switch
            {
                "en" => "Which exact requirement are you targeting, for which scope, equipment or document, and under which installation or usage conditions?",
                "es" => "Que requisito exacto buscas, con que alcance, para que equipo o documento, y con que condiciones de instalacion o uso?",
                "pt" => "Qual requisito exato voce quer tratar, com que escopo, para qual equipamento ou documento, e com quais condicoes de instalacao ou uso?",
                "de" => "Welche genaue Anforderung meinst du, fuer welchen Umfang, welches Geraet oder Dokument und unter welchen Installations- oder Nutzungsbedingungen?",
                "it" => "Quale requisito preciso intendi, con quale perimetro, per quale apparecchiatura o documento e con quali condizioni di installazione o uso?",
                _ => "Tu vises quelle exigence precise, sur quel perimetre, pour quel equipement ou document, et avec quelles conditions d'installation ou d'utilisation ?"
            };
        }

        return guidanceLanguage switch
        {
            "en" => "Which exact requirement do you mean, for which scope, which item or document, and under which usage conditions?",
            "es" => "A que requisito exacto te refieres, con que alcance, para que elemento o documento, y con que condiciones de uso?",
            "pt" => "A qual requisito exato voce se refere, com que escopo, para qual elemento ou documento, e com quais condicoes de uso?",
            "de" => "Welche genaue Anforderung meinst du, fuer welchen Umfang, welches Element oder Dokument und unter welchen Nutzungsbedingungen?",
            "it" => "A quale requisito esatto ti riferisci, con quale perimetro, quale elemento o documento e quali condizioni d'uso?",
            _ => "Tu parles de quelle exigence exactement, sur quel perimetre, pour quel element ou document, et avec quelles conditions d'utilisation ?"
        };
    }

    private static string BuildHighImpactSafetyQualificationNote(string normalizedQuery, MatchedRetrievalContext matchedContext, bool simpleDocumentSelection, string guidanceLanguage)
    {
        var hintLabel = FormatHintLabel(matchedContext.DocHints);

        if (ContainsQuickCustomerReplySelectionQuestion(normalizedQuery) || simpleDocumentSelection)
        {
            return guidanceLanguage switch
            {
                "en" => $"The retrieved documents{hintLabel} can help choose an initial source, but the scope, document version, affected items and context still need checking before making it a final answer.",
                "es" => $"Los documentos recuperados{hintLabel} pueden ayudar a escoger una primera fuente, pero aun hay que verificar el alcance, la version del documento, los elementos afectados y el contexto antes de dar una respuesta definitiva.",
                "pt" => $"Os documentos recuperados{hintLabel} podem ajudar a escolher uma primeira fonte, mas ainda e preciso verificar o escopo, a versao do documento, os elementos envolvidos e o contexto antes de dar uma resposta definitiva.",
                "de" => $"Die gefundenen Dokumente{hintLabel} koennen bei der ersten Quellenauswahl helfen, aber Umfang, Dokumentversion, betroffene Elemente und Kontext muessen vor einer endgueltigen Antwort noch geprueft werden.",
                "it" => $"I documenti recuperati{hintLabel} possono aiutare a scegliere una prima fonte, ma bisogna ancora verificare perimetro, versione del documento, elementi interessati e contesto prima di dare una risposta definitiva.",
                _ => $"Les documents retrouves{hintLabel} peuvent aider a choisir une premiere source, mais il faut encore verifier le perimetre, la version du document, les elements concernes et le contexte avant d'en faire une reponse definitive."
            };
        }

        return guidanceLanguage switch
        {
            "en" => $"The retrieved documents{hintLabel} can inform the topic, but the scope, affected items, usage conditions and project context still need framing before asserting compliance, an obligation or a final recommendation.",
            "es" => $"Los documentos recuperados{hintLabel} pueden aclarar el tema, pero aun hay que acotar el alcance, los elementos afectados, las condiciones de uso y el contexto del proyecto antes de afirmar conformidad, obligacion o recomendacion definitiva.",
            "pt" => $"Os documentos recuperados{hintLabel} podem esclarecer o tema, mas ainda e preciso enquadrar o escopo, os elementos envolvidos, as condicoes de uso e o contexto do projeto antes de afirmar conformidade, obrigacao ou recomendacao definitiva.",
            "de" => $"Die gefundenen Dokumente{hintLabel} koennen das Thema einordnen, aber Umfang, betroffene Elemente, Nutzungsbedingungen und Projektkontext muessen noch geklaert werden, bevor Konformitaet, Pflicht oder endgueltige Empfehlung behauptet wird.",
            "it" => $"I documenti recuperati{hintLabel} possono chiarire il tema, ma bisogna ancora definire perimetro, elementi interessati, condizioni d'uso e contesto di progetto prima di affermare conformita, obbligo o raccomandazione definitiva.",
            _ => $"Les documents retrouves{hintLabel} peuvent eclairer le sujet, mais il faut encore cadrer le perimetre, les elements concernes, les conditions d'utilisation et le contexte projet avant d'affirmer une conformite, une obligation ou une recommandation definitive."
        };
    }

    private static string BuildSourceQualityQualificationNote(MatchedRetrievalContext matchedContext, string guidanceLanguage)
    {
        var hintLabel = FormatHintLabel(matchedContext.DocHints);
        return guidanceLanguage switch
        {
            "en" => $"The retrieved evidence{hintLabel} appears usable, but at least one selected source has weak extraction or OCR quality. Answer from the cited passages, and explicitly mention uncertainty if a value, wording or page detail looks ambiguous.",
            "es" => $"La evidencia recuperada{hintLabel} parece utilizable, pero al menos una fuente seleccionada tiene calidad de extraccion u OCR debil. Responde a partir de los pasajes citados y menciona la incertidumbre si un valor, una formulacion o un detalle de pagina parece ambiguo.",
            "pt" => $"As evidencias recuperadas{hintLabel} parecem utilizaveis, mas pelo menos uma fonte selecionada tem qualidade fraca de extracao ou OCR. Responda a partir dos trechos citados e mencione a incerteza se um valor, uma formulacao ou um detalhe de pagina parecer ambiguo.",
            "de" => $"Die gefundenen Belege{hintLabel} wirken nutzbar, aber mindestens eine ausgewaehlte Quelle hat schwache Extraktions- oder OCR-Qualitaet. Antworte aus den zitierten Passagen und nenne Unsicherheit, wenn ein Wert, eine Formulierung oder ein Seitendetail mehrdeutig wirkt.",
            "it" => $"Le evidenze recuperate{hintLabel} sembrano utilizzabili, ma almeno una fonte selezionata ha qualita di estrazione o OCR debole. Rispondi dai passaggi citati e segnala l'incertezza se un valore, una formulazione o un dettaglio di pagina sembra ambiguo.",
            _ => $"Les elements retrouves{hintLabel} semblent exploitables, mais au moins une source selectionnee a une qualite d'extraction ou OCR faible. Reponds depuis les passages cites et signale l'incertitude si une valeur, une formulation ou un detail de page semble ambigu."
        };
    }

    private static string DetermineResponseShape(string normalizedQuery, string behavior)
    {
        if (string.Equals(behavior, "ask_clarification", StringComparison.Ordinal))
            return "clarify";

        if (normalizedQuery.Contains("compare", StringComparison.Ordinal)
            || normalizedQuery.Contains("difference", StringComparison.Ordinal)
            || normalizedQuery.Contains("comparer", StringComparison.Ordinal))
            return "comparison";

        if (normalizedQuery.Contains("montre", StringComparison.Ordinal)
            || normalizedQuery.Contains("passage", StringComparison.Ordinal)
            || normalizedQuery.Contains("ou dans le document", StringComparison.Ordinal))
            return "locate_passage";

        if (normalizedQuery.Contains("quel document", StringComparison.Ordinal)
            || normalizedQuery.Contains("manuel", StringComparison.Ordinal)
            || normalizedQuery.Contains("ouvrir", StringComparison.Ordinal)
            || normalizedQuery.Contains("commencer", StringComparison.Ordinal))
            return "locate_document";

        if (normalizedQuery.Contains("resumer", StringComparison.Ordinal)
            || normalizedQuery.Contains("resume", StringComparison.Ordinal)
            || normalizedQuery.Contains("reponse courte", StringComparison.Ordinal)
            || normalizedQuery.Contains("expliquer simplement", StringComparison.Ordinal))
            return "summary";

        if (string.Equals(behavior, "answer_with_caveat", StringComparison.Ordinal))
            return "qualified_answer";

        return "direct_answer";
    }

    private static void CollectDocumentHints(string? rawText, HashSet<string> hints)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return;

        var stem = ExtractDocumentStem(rawText);
        if (string.IsNullOrWhiteSpace(stem))
            return;

        var localHints = new HashSet<string>(StringComparer.Ordinal);
        string? fallbackAlpha = null;
        string? previousAlpha = null;

        foreach (var part in stem.Split([' ', '-', '_', '/', '\\', '.', ',', ';', ':', '(', ')', '[', ']'], StringSplitOptions.RemoveEmptyEntries))
        {
            var compact = CompactDocumentHintToken(part);
            if (string.IsNullOrEmpty(compact))
            {
                previousAlpha = null;
                continue;
            }

            if (compact.All(char.IsDigit))
            {
                if (!string.IsNullOrWhiteSpace(previousAlpha) && compact.Length is >= 2 and <= 6)
                    localHints.Add($"{previousAlpha}{compact}");

                if (compact.Length >= 4)
                    localHints.Add(compact);

                previousAlpha = null;
                continue;
            }

            if (compact.Any(char.IsDigit))
            {
                if (compact.Length is >= 4 and <= 16)
                    localHints.Add(compact);

                previousAlpha = null;
                continue;
            }

            if (IsMeaningfulDocumentHint(compact))
            {
                fallbackAlpha ??= compact;
                previousAlpha = compact.Length <= 5 ? compact : null;
                continue;
            }

            if (IsShortReferencePrefix(compact))
            {
                previousAlpha = compact;
                continue;
            }

            previousAlpha = null;
        }

        if (localHints.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(fallbackAlpha))
                hints.Add(fallbackAlpha);
            return;
        }

        foreach (var hint in localHints)
            hints.Add(hint);
    }

    private static string ExtractDocumentStem(string rawText)
    {
        var normalized = rawText.Replace('\\', '/');
        var tail = normalized[(normalized.LastIndexOf('/') + 1)..];
        var dotIndex = tail.LastIndexOf('.');
        return dotIndex > 0 ? tail[..dotIndex] : tail;
    }

    private static string CompactDocumentHintToken(string token)
    {
        Span<char> buffer = stackalloc char[token.Length];
        var length = 0;

        foreach (var ch in token)
        {
            if (char.IsLetterOrDigit(ch))
                buffer[length++] = char.ToUpperInvariant(ch);
        }

        return length == 0 ? string.Empty : new string(buffer[..length]);
    }

    private static bool IsMeaningfulDocumentHint(string token)
        => token.Length >= 4
           && token.All(char.IsLetter)
           && !DocumentHintStopwords.Contains(token);

    private static bool IsShortReferencePrefix(string token)
        => token.Length is >= 2 and <= 5
           && token.All(char.IsLetter)
           && !DocumentHintStopwords.Contains(token);

    private static bool ContainsAny(string? text, IReadOnlyList<string> keywords)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (var keyword in keywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string FormatHintLabel(IReadOnlyList<string> hints)
        => hints.Count switch
        {
            0 => string.Empty,
            1 => $" ({hints[0]})",
            2 => $" ({hints[0]} + {hints[1]})",
            _ => $" ({hints[0]} + {hints[1]} + autres sources)"
        };

    private static bool HasSpecificReferenceLookup(string normalizedQuery)
    {
        var normalizedWhole = ExactMatchEntryExtractor.NormalizeForLookup(normalizedQuery);
        return ExactMatchEntryExtractor.ExtractLookupTerms(normalizedQuery)
            .Any(term => !string.Equals(term, normalizedWhole, StringComparison.Ordinal));
    }

    private static string NormalizeQueryForGuidance(string query)
        => $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query))} ";

    private static string NormalizeQuery(string s)
    {
        s = (s ?? string.Empty).Trim();
        if (s.Length == 0)
            return s;

        var sb = new StringBuilder(s.Length);
        var inWhitespace = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!inWhitespace)
                {
                    sb.Append(' ');
                    inWhitespace = true;
                }
            }
            else
            {
                sb.Append(ch);
                inWhitespace = false;
            }
        }

        return sb.ToString();
    }

    private sealed record CapabilityAHypQuestionDocRow(
        Guid DocId,
        string DocPath,
        string DocName,
        int IndexedVersion,
        string? HypotheticalQuestionsJson);

    private sealed record CapabilityAHypQuestionDoc(
        Guid DocId,
        string DocPath,
        string DocName,
        int IndexedVersion,
        string[] HypotheticalQuestions);

    private static string[] ParseJsonStringArray(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        try
        {
            return JsonSerializer.Deserialize<string[]>(value) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string BuildCategoryRef(int displayOrder)
        => $"cat_{displayOrder:000}";

    private static string? ExtractTopLevelCategoryPath(string? categoryOrDocPath)
    {
        if (string.IsNullOrWhiteSpace(categoryOrDocPath))
            return null;

        var normalized = categoryOrDocPath.Trim().Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        return normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
    }

    private static async Task<List<RagMatch>> SearchDocumentMetadataMatchesAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        IReadOnlyList<string> normalizedTerms,
        IReadOnlyList<string> referenceKeys,
        string? category,
        Guid? docId,
        string? docPath,
        int topK,
        CancellationToken ct,
        string? categoryPath = null)
    {
        if (topK <= 0)
            return [];

        const string sql = """
SELECT
    d.doc_id AS "DocId",
    d.doc_path AS "DocPath",
    d.doc_name AS "DocName",
    d.category AS "Category",
    d.indexed_version AS "IngestionVersion",
    LOWER(ENCODE(d.content_hash, 'hex')) AS "HashDoc"
FROM documents d
WHERE d.tenant_id = @tenant_id
  AND d.status = 'indexed'
  AND d.indexed_version > 0
  AND (@category IS NULL OR LOWER(d.category) = @category)
  AND (@category_path IS NULL OR d.doc_path = @category_path OR d.doc_path LIKE (@category_path || '/%'))
  AND (@doc_id IS NULL OR d.doc_id = @doc_id)
  AND (@doc_path IS NULL OR d.doc_path = @doc_path)
ORDER BY d.updated_at DESC
LIMIT 500;
""";

        var rows = await conn.QueryAsync<MetadataReferenceRow>(new CommandDefinition(sql, new
        {
            tenant_id = tenantId,
            category,
            category_path = NormalizeRagCategoryPathForSql(categoryPath),
            doc_id = docId,
            doc_path = docPath
        }, cancellationToken: ct));

        return rows
            .Select(row => BuildMetadataReferenceMatch(row, normalizedTerms, referenceKeys))
            .Where(static match => match is not null)
            .Select(static match => match!)
            .OrderByDescending(static match => match.Score)
            .ThenBy(static match => match.DocPath, StringComparer.OrdinalIgnoreCase)
            .Take(topK)
            .ToList();
    }

    private static RagMatch? BuildMetadataReferenceMatch(
        MetadataReferenceRow row,
        IReadOnlyList<string> queryTerms,
        IReadOnlyList<string> queryReferenceKeys)
    {
        var metadataText = $"{row.DocName} {row.DocPath}";
        var metadataTerms = ExactMatchEntryExtractor.ExtractLookupTerms(metadataText);
        var metadataKeys = ExactMatchEntryExtractor.ExtractReferenceKeys(metadataText);
        var metadataReferenceTerms = ExactMatchEntryExtractor.ExtractTargetedReferences(metadataText)
            .Select(ExactMatchEntryExtractor.NormalizeForLookup)
            .Where(static term => IsReferenceLikeLookupTerm(term))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var queryReferenceTerms = queryTerms
            .Where(static term => IsReferenceLikeLookupTerm(term))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var exactReferenceMatches = queryReferenceTerms
            .Intersect(metadataReferenceTerms, StringComparer.Ordinal)
            .ToArray();
        var keyBackedReferenceMatches = metadataReferenceTerms
            .Where(term => QueryKeysFullyMatchTerm(queryReferenceKeys, term))
            .ToArray();
        var genericDirectMatches = queryTerms
            .Intersect(metadataTerms, StringComparer.Ordinal)
            .Except(exactReferenceMatches, StringComparer.Ordinal)
            .ToArray();
        var keyMatches = queryReferenceKeys
            .Intersect(metadataKeys, StringComparer.Ordinal)
            .ToArray();

        if (exactReferenceMatches.Length == 0
            && keyBackedReferenceMatches.Length == 0
            && genericDirectMatches.Length == 0
            && keyMatches.Length == 0)
            return null;

        var score = ComputeMetadataReferenceScore(
            exactReferenceMatches.Length,
            keyBackedReferenceMatches.Length,
            genericDirectMatches.Length,
            keyMatches.Length);
        var strongestReference = exactReferenceMatches.FirstOrDefault()
            ?? keyBackedReferenceMatches.FirstOrDefault();
        var text = strongestReference is not null
            ? $"{row.DocName} [{strongestReference}]"
            : keyMatches.Length > 0
                ? $"{row.DocName} [{string.Join(", ", keyMatches)}]"
                : row.DocName;

        return new RagMatch(
            Score: score,
            DocId: row.DocId.ToString(),
            DocPath: row.DocPath,
            DocName: row.DocName,
            Category: row.Category,
            PageStart: null,
            PageEnd: null,
            ChunkId: $"docmeta:{row.DocId}",
            ChunkIndex: -1,
            Text: text,
            IngestionVersion: row.IngestionVersion,
            HashDoc: row.HashDoc,
            EmbedText: text,
            EmbeddingBasis: "exact_match_v1",
            SectionOrdinal: null,
            UnitOrdinal: null,
            SectionTitle: null,
            HeadingPath: null,
            ChunkType: "document_metadata_ref",
            PrevChunkId: null,
            NextChunkId: null,
            SameSectionChunkId: null);
    }

    internal static double ComputeMetadataReferenceScore(int exactReferenceMatches, int keyBackedReferenceMatches, int genericDirectMatches, int keyMatches)
    {
        var score = 0.90;
        if (exactReferenceMatches > 0)
            score += 0.09 + Math.Min(0.01, exactReferenceMatches * 0.004);
        else if (keyBackedReferenceMatches > 0)
            score += 0.07 + Math.Min(0.01, keyBackedReferenceMatches * 0.004);
        else if (keyMatches > 0)
            score += 0.05 + Math.Min(0.01, keyMatches * 0.004);

        if (genericDirectMatches > 0)
            score += Math.Min(0.01, genericDirectMatches * 0.0025);

        return Math.Min(1.01, score);
    }

    private static bool QueryKeysFullyMatchTerm(IReadOnlyList<string> queryReferenceKeys, string metadataReferenceTerm)
    {
        if (queryReferenceKeys.Count == 0 || string.IsNullOrWhiteSpace(metadataReferenceTerm))
            return false;

        return queryReferenceKeys.All(key =>
            metadataReferenceTerm.Contains(key, StringComparison.Ordinal));
    }

    private static bool IsReferenceLikeLookupTerm(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return false;

        return term.Length >= 2 && term.Any(char.IsDigit) && term.Any(char.IsLetter);
    }

    private static bool HasReferenceLikeQueryToken(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        if (ExtractLexicalQueryTokens(query).Any(IsReferenceLikeLookupTerm)
            || ExtractLexicalQuerySurfaceTokens(query).Any(IsReferenceLikeLookupTerm))
        {
            return true;
        }

        return Regex.IsMatch(
            query,
            @"\b[\p{L}]{1,12}[-_][\p{L}\p{Nd}]*\d[\p{L}\p{Nd}]*\b|\b[A-Z]{1,8}\d{1,6}[A-Z0-9]*\b|\b[A-Z]{1,8}\s+\d{1,6}[A-Z0-9]*\b",
            RegexOptions.CultureInvariant);
    }

    private static async Task<IResult> ScrollAsync(
        HttpContext ctx,
        IOptions<RagOptions> ragOpt,
        IHttpClientFactory httpFactory,
        int? limit,
        string? docPath)
    {
        AdminAuth.EnsureAdmin(ctx);
        var tenantId = ctx.GetTenantId();
        var rag = ragOpt.Value;

        var qdrant = httpFactory.CreateClient("qdrant");
        qdrant.BaseAddress = new Uri(rag.QdrantBaseUrl);

        docPath = string.IsNullOrWhiteSpace(docPath)
            ? null
            : docPath.Trim().Replace('\\', '/').TrimStart('/');

        var must = new List<object>
        {
            new { key = "tenant_id", match = new { value = tenantId.ToString() } }
        };
        if (!string.IsNullOrWhiteSpace(docPath))
            must.Add(new { key = "doc_path", match = new { value = docPath } });

        var body = new
        {
            limit = Math.Clamp(limit ?? 20, 1, 200),
            with_payload = true,
            filter = new
            {
                must = must.ToArray()
            }
        };

        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await qdrant.PostAsync(
            $"/collections/{rag.QdrantCollection}/points/scroll",
            content,
            ctx.RequestAborted);

        if (!resp.IsSuccessStatusCode)
            return Results.Problem($"Qdrant scroll failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");

        var json = await resp.Content.ReadAsStringAsync(ctx.RequestAborted);
        return Results.Text(json, "application/json");
    }
}

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

public sealed class RagDocumentSourceHashRow
{
    public string DocId { get; set; } = "";
    public string DocPath { get; set; } = "";
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
    IReadOnlyDictionary<string, string>? DegradedRetrieverErrors = null
);
