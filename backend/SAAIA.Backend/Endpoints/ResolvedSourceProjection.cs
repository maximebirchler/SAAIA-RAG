using System.Text.Json;
using Dapper;
using Npgsql;
using SAAIA.Contracts;

namespace SAAIA.Backend.Endpoints;

internal static class ResolvedSourceProjection
{
    private const string ResolveByRefSql = """
WITH matched_doc AS (
  SELECT
    d.doc_id,
    d.tenant_id,
    d.doc_path,
    d.doc_name,
    d.page_count,
    d.category,
    d.indexed_version,
    d.content_hash,
    d.file_size,
    d.file_mtime,
    d.updated_at,
    rev.revision_id,
    CASE WHEN d.doc_path LIKE '%/%' THEN regexp_replace(d.doc_path, '/[^/]+$', '') ELSE '' END AS category_path
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
    AND d.status='indexed'
    AND (
          CAST(d.doc_id AS text)=@raw
       OR LOWER(d.doc_path)=LOWER(@normalizedPath)
       OR LOWER(d.doc_name)=LOWER(@normalizedName)
    )
  ORDER BY
    CASE
      WHEN CAST(d.doc_id AS text)=@raw THEN 0
      WHEN LOWER(d.doc_path)=LOWER(@normalizedPath) THEN 1
      WHEN LOWER(d.doc_name)=LOWER(@normalizedName) THEN 2
      ELSE 3
    END,
    d.updated_at DESC
  LIMIT 1
)
""" + SourceProjectionSelectSql;

    private const string ResolveByDocIdSql = """
WITH matched_doc AS (
  SELECT
    d.doc_id,
    d.tenant_id,
    d.doc_path,
    d.doc_name,
    d.page_count,
    d.category,
    d.indexed_version,
    d.content_hash,
    d.file_size,
    d.file_mtime,
    d.updated_at,
    rev.revision_id,
    CASE WHEN d.doc_path LIKE '%/%' THEN regexp_replace(d.doc_path, '/[^/]+$', '') ELSE '' END AS category_path
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
    AND d.doc_id=@docId
    AND d.status='indexed'
  ORDER BY d.updated_at DESC
  LIMIT 1
)
""" + SourceProjectionSelectSql;

    private const string SourceProjectionSelectSql = """
SELECT
  md.doc_id      AS "DocId",
  md.doc_path    AS "DocPath",
  md.doc_name    AS "DocName",
  md.page_count  AS "PageCount",
  md.category    AS "Category",
  md.category_path AS "CategoryPath",
  CASE
    WHEN top.display_order IS NULL THEN NULL
    ELSE 'cat_' || LPAD(top.display_order::text, 3, '0')
  END AS "CategoryRef",
  saaia_document_summary_source_hash(md.content_hash, md.doc_path, md.file_size, md.file_mtime, md.indexed_version) AS "SourceHash",
  profile.document_profile_id AS "ProfileId",
  profile.profile_version AS "ProfileVersion",
  profile.language AS "ProfileLanguage",
  profile.keywords AS "ProfileKeywords",
  profile.entities AS "ProfileEntities",
  profile.topics AS "ProfileTopics",
  profile.hypothetical_questions AS "ProfileHypotheticalQuestions",
  profile.limits AS "ProfileLimits",
  summary.doc_language AS "SummaryLanguage",
  run.payload ->> 'documentLanguage' AS "RunDocumentLanguage",
  run.payload ->> 'extractionSource' AS "ExtractionSource",
  CASE
    WHEN LOWER(COALESCE(NULLIF(run.payload ->> 'ocrAttempted', ''), '')) IN ('true','false')
      THEN LOWER(run.payload ->> 'ocrAttempted')::boolean
    ELSE false
  END AS "OcrAttempted",
  CASE
    WHEN LOWER(COALESCE(NULLIF(run.payload ->> 'ocrApplied', ''), '')) IN ('true','false')
      THEN LOWER(run.payload ->> 'ocrApplied')::boolean
    ELSE false
  END AS "OcrApplied",
  NULLIF(run.payload ->> 'ocrLanguages', '') AS "OcrLanguages",
  CASE
    WHEN COALESCE(run.payload ->> 'ocrDurationMs', '') ~ '^[0-9]{1,18}$'
      THEN (run.payload ->> 'ocrDurationMs')::bigint
    ELSE NULL
  END AS "OcrDurationMs",
  run.payload #>> '{nativeExtractionQuality,textStatus}' AS "NativeTextStatus",
  CASE
    WHEN LOWER(COALESCE(NULLIF(run.payload #>> '{nativeExtractionQuality,ocrRecommended}', ''), '')) IN ('true','false')
      THEN LOWER(run.payload #>> '{nativeExtractionQuality,ocrRecommended}')::boolean
    ELSE NULL
  END AS "NativeOcrRecommended",
  (run.payload -> 'ocrDiagnostics')::text AS "OcrDiagnosticsJson",
  run.payload #>> '{extractionQuality,textStatus}' AS "TextStatus",
  CASE
    WHEN LOWER(COALESCE(NULLIF(run.payload #>> '{extractionQuality,ocrRecommended}', ''), '')) IN ('true','false')
      THEN LOWER(run.payload #>> '{extractionQuality,ocrRecommended}')::boolean
    ELSE false
  END AS "OcrRecommended",
  NULLIF(run.payload #>> '{extractionQuality,signals}', '') AS "SignalsJson",
  (run.payload -> 'retrievalChunkQuality')::text AS "RetrievalChunkQualityJson",
  CASE
    WHEN COALESCE(run.payload #>> '{extractionQuality,pageCount}', '') ~ '^[0-9]{1,9}$'
      THEN (run.payload #>> '{extractionQuality,pageCount}')::int
    ELSE COALESCE(pq.page_count, md.page_count, 0)
  END AS "QualityPageCount",
  CASE
    WHEN COALESCE(run.payload #>> '{extractionQuality,textPageCount}', '') ~ '^[0-9]{1,9}$'
      THEN (run.payload #>> '{extractionQuality,textPageCount}')::int
    ELSE COALESCE(pq.text_page_count, 0)
  END AS "TextPageCount",
  CASE
    WHEN COALESCE(run.payload #>> '{extractionQuality,emptyPageCount}', '') ~ '^[0-9]{1,9}$'
      THEN (run.payload #>> '{extractionQuality,emptyPageCount}')::int
    ELSE COALESCE(pq.empty_page_count, 0)
  END AS "EmptyPageCount",
  CASE
    WHEN COALESCE(run.payload #>> '{extractionQuality,sparsePageCount}', '') ~ '^[0-9]{1,9}$'
      THEN (run.payload #>> '{extractionQuality,sparsePageCount}')::int
    ELSE COALESCE(pq.sparse_page_count, 0)
  END AS "SparsePageCount",
  COALESCE(pq.image_page_count, 0) AS "ImagePageCount",
  COALESCE(pq.page_warning_count, 0) AS "PageWarningCount",
  COALESCE(pq.page_review_recommended_count, 0) AS "PageReviewRecommendedCount",
  COALESCE(cards.content_cards_json, '[]') AS "ContentCardsJson"
FROM matched_doc md
LEFT JOIN documents_catalog_categories top
  ON top.tenant_id = md.tenant_id
 AND top.path = split_part(md.category_path, '/', 1)
LEFT JOIN LATERAL (
  SELECT
    p.document_profile_id,
    NULLIF(BTRIM(p.profile_version), '') AS profile_version,
    NULLIF(BTRIM(p.language), '') AS language,
    p.keywords,
    p.entities,
    p.topics,
    p.hypothetical_questions,
    p.limits
  FROM document_profiles p
  JOIN document_revisions r
    ON r.revision_id = p.revision_id
   AND r.tenant_id = p.tenant_id
   AND r.doc_id = p.doc_id
  WHERE p.tenant_id = md.tenant_id
    AND p.doc_id = md.doc_id
    AND r.indexed_version = md.indexed_version
  ORDER BY
    (NULLIF(BTRIM(p.language), 'und') IS NULL) ASC,
    CASE p.profile_version
      WHEN 'llm_backoffice_v1' THEN 0
      WHEN 'foundation_v1' THEN 1
      WHEN 'deterministic_v1' THEN 2
      ELSE 3
    END,
    r.published_at DESC NULLS LAST,
    p.updated_at DESC NULLS LAST
  LIMIT 1
) profile ON true
LEFT JOIN LATERAL (
  SELECT NULLIF(BTRIM(s.doc_language), '') AS doc_language
  FROM document_summaries s
  WHERE s.tenant_id = md.tenant_id
    AND s.doc_id = md.doc_id
    AND s.level = 'medium'
    AND s.source_hash = saaia_document_summary_source_hash(md.content_hash, md.doc_path, md.file_size, md.file_mtime, md.indexed_version)
  ORDER BY s.updated_at DESC NULLS LAST, s.created_at DESC NULLS LAST
  LIMIT 1
) summary ON true
LEFT JOIN LATERAL (
  SELECT pr.payload
  FROM document_processing_runs pr
  WHERE pr.tenant_id = md.tenant_id
    AND pr.doc_id = md.doc_id
    AND pr.action = 'upsert'
    AND pr.status = 'done'
    AND (
      (md.revision_id IS NOT NULL AND pr.revision_id = md.revision_id)
      OR (md.revision_id IS NULL AND pr.indexed_version_after = md.indexed_version)
    )
  ORDER BY pr.finished_at DESC NULLS LAST, pr.started_at DESC NULLS LAST
  LIMIT 1
) run ON true
LEFT JOIN LATERAL (
  SELECT
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
    )::int AS page_review_recommended_count
  FROM (
    SELECT
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
      COALESCE(uc.unit_count, 0) AS unit_count,
      COALESCE(cc.chunk_count, 0) AS chunk_count
    FROM document_page_index pi
    LEFT JOIN LATERAL (
      SELECT COUNT(*)::int AS unit_count
      FROM document_units u
      WHERE u.tenant_id=pi.tenant_id
        AND u.revision_id=pi.revision_id
        AND u.page_start <= pi.page_number
        AND pi.page_number <= u.page_end
    ) uc ON true
    LEFT JOIN LATERAL (
      SELECT COUNT(*)::int AS chunk_count
      FROM retrieval_chunks rc
      WHERE rc.tenant_id=pi.tenant_id
        AND rc.revision_id=pi.revision_id
        AND rc.page_start <= pi.page_number
        AND pi.page_number <= rc.page_end
    ) cc ON true
    WHERE pi.tenant_id = md.tenant_id
      AND pi.revision_id = md.revision_id
  ) page_projection
) pq ON true
LEFT JOIN LATERAL (
  SELECT jsonb_agg(
    jsonb_build_object(
      'title', c.title,
      'contentCardId', c.content_card_id,
      'pageStart', c.page_start,
      'pageEnd', c.page_end,
      'kind', c.kind,
      'signals', c.signals,
      'evidence', c.evidence)
    ORDER BY c.card_index)::text AS content_cards_json
  FROM (
    SELECT
      title,
      content_card_id,
      page_start,
      page_end,
      kind,
      signals,
      CASE WHEN metadata ? 'evidence' THEN metadata->'evidence' ELSE NULL END AS evidence,
      card_index
    FROM document_profile_content_cards
    WHERE tenant_id = md.tenant_id
      AND document_profile_id = profile.document_profile_id
      AND NULLIF(BTRIM(title), '') IS NOT NULL
      AND saaia_is_safe_profile_content_card(kind, title, page_start, page_end, metadata)
    ORDER BY card_index
    LIMIT 32
  ) c
) cards ON true;
""";

    public static async Task<ResolvedSourceDto?> ResolveByRefAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string raw,
        CancellationToken ct)
    {
        var normalizedPath = raw.Replace('\\', '/').Trim().Trim('/');
        var normalizedName = Path.GetFileName(normalizedPath);

        var row = await conn.QueryFirstOrDefaultAsync<ResolvedSourceRow>(new CommandDefinition(
            ResolveByRefSql,
            new { tenant = tenantId, raw, normalizedPath, normalizedName },
            cancellationToken: ct));

        return BuildSource(row);
    }

    public static async Task<ResolvedSourceDto?> ResolveByDocIdAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        Guid docId,
        CancellationToken ct)
    {
        var row = await conn.QueryFirstOrDefaultAsync<ResolvedSourceRow>(new CommandDefinition(
            ResolveByDocIdSql,
            new { tenant = tenantId, docId },
            cancellationToken: ct));

        return BuildSource(row);
    }

    private static ResolvedSourceDto? BuildSource(ResolvedSourceRow? row)
    {
        if (row is null)
            return null;

        var pageEnd = row.PageCount > 0 ? row.PageCount : 1;
        var profileLanguage = NormalizeSourceLanguageTag(row.ProfileLanguage);
        var summaryLanguage = NormalizeSourceLanguageTag(row.SummaryLanguage);
        var runLanguage = NormalizeSourceLanguageTag(row.RunDocumentLanguage);
        var docLanguage = !string.Equals(profileLanguage, "und", StringComparison.Ordinal)
            ? profileLanguage
            : !string.Equals(summaryLanguage, "und", StringComparison.Ordinal)
                ? summaryLanguage
                : runLanguage;
        var quality = BuildExtractionQuality(row);
        var cards = ParseContentCards(row.ContentCardsJson);
        return new ResolvedSourceDto
        {
            DocId = row.DocId,
            DocPath = row.DocPath ?? string.Empty,
            DocName = row.DocName ?? string.Empty,
            PageStart = 1,
            PageEnd = pageEnd,
            Label = string.IsNullOrWhiteSpace(row.DocName) ? row.DocPath ?? string.Empty : row.DocName!,
            SourceHash = NullIfWhiteSpace(row.SourceHash),
            DocLanguage = docLanguage,
            ProfileLanguage = string.Equals(profileLanguage, "und", StringComparison.Ordinal) ? null : profileLanguage,
            Category = NullIfWhiteSpace(row.Category),
            CategoryRef = NullIfWhiteSpace(row.CategoryRef),
            CategoryPath = NullIfWhiteSpace(row.CategoryPath),
            ChunkId = null,
            ExtractionQuality = quality,
            MatchedContentCards = cards.Count == 0 ? null : cards,
            SelectionHints = BuildSelectionHints(quality, cards),
            ProfileSignals = BuildProfileSignals(row, profileLanguage, docLanguage)
        };
    }

    private static RagItemProfileSignals? BuildProfileSignals(
        ResolvedSourceRow row,
        string profileLanguage,
        string? docLanguage)
    {
        var keywords = CompactProfileValues(row.ProfileKeywords, maxItems: 8);
        var entities = CompactProfileValues(row.ProfileEntities, maxItems: 8);
        var topics = CompactProfileValues(row.ProfileTopics, maxItems: 8);
        var questions = CompactProfileValues(row.ProfileHypotheticalQuestions, maxItems: 4);
        var limits = CompactProfileValues(row.ProfileLimits, maxItems: 4);
        var signalCount = keywords.Count + entities.Count + topics.Count + questions.Count + limits.Count;
        var profileVersion = NullIfWhiteSpace(row.ProfileVersion);
        var language = !string.Equals(profileLanguage, "und", StringComparison.Ordinal)
            ? profileLanguage
            : NullIfWhiteSpace(docLanguage);
        if (signalCount == 0
            && string.IsNullOrWhiteSpace(profileVersion)
            && string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        return new RagItemProfileSignals
        {
            ProfileVersion = profileVersion,
            Language = language,
            Keywords = keywords.Count == 0 ? null : keywords,
            Entities = entities.Count == 0 ? null : entities,
            Topics = topics.Count == 0 ? null : topics,
            HypotheticalQuestions = questions.Count == 0 ? null : questions,
            Limits = limits.Count == 0 ? null : limits
        };
    }

    private static List<string> CompactProfileValues(IEnumerable<string>? values, int maxItems)
    {
        if (values is null)
            return [];

        return values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Where(static value => value.Length >= 2)
            .Select(static value => value.Length <= 160 ? value : value[..160].TrimEnd())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maxItems, 1, 16))
            .ToList();
    }

    private static RagItemExtractionQuality? BuildExtractionQuality(ResolvedSourceRow row)
    {
        var textStatus = NullIfWhiteSpace(row.TextStatus)
            ?? NullIfWhiteSpace(row.NativeTextStatus)
            ?? InferTextStatusFromPageCounters(row);
        var qualityStatus = ResolveDocumentQualityStatus(row, textStatus);
        var signals = ParseStringArray(row.SignalsJson);
        if (signals.Count == 0 && !string.IsNullOrWhiteSpace(textStatus))
            signals.Add(textStatus);
        var diagnosticSummary = BuildDiagnosticSummary(row);

        if (string.IsNullOrWhiteSpace(row.ExtractionSource)
            && !row.OcrAttempted
            && !row.OcrApplied
            && string.IsNullOrWhiteSpace(qualityStatus)
            && string.IsNullOrWhiteSpace(textStatus)
            && signals.Count == 0
            && diagnosticSummary is null)
        {
            return null;
        }

        var confidence = ResolveDocumentExtractionConfidence(row, textStatus);
        var manualReview = IsManualReviewRecommended(row, textStatus);
        return new RagItemExtractionQuality
        {
            ExtractionSource = NullIfWhiteSpace(row.ExtractionSource),
            OcrAttempted = row.OcrAttempted,
            OcrApplied = row.OcrApplied,
            DocumentQualityStatus = qualityStatus,
            DocumentExtractionConfidence = confidence,
            DocumentManualReviewRecommended = manualReview,
            PageQualityStatus = qualityStatus,
            PageExtractionConfidence = confidence,
            PageManualReviewRecommended = manualReview,
            TextStatus = textStatus,
            OcrRecommended = row.OcrRecommended,
            Signals = signals.Count == 0 ? null : signals,
            DiagnosticSummary = diagnosticSummary
        };
    }

    private static string? InferTextStatusFromPageCounters(ResolvedSourceRow row)
    {
        if (row.QualityPageCount <= 0)
            return null;
        if (row.TextPageCount <= 0)
            return "empty_text";
        return "ok";
    }

    private static RagItemSelectionHints BuildSelectionHints(
        RagItemExtractionQuality? quality,
        IReadOnlyList<RagItemContentCard> cards)
    {
        var qualityPenalty = ComputeSelectionQualityPenalty(quality);
        var actionabilityScore = 5;
        var supportScore = 6;
        var fragmentScore = 0;
        var navigationScore = 0;

        if (cards.Count > 0)
        {
            actionabilityScore += cards.Any(static card =>
                string.Equals(card.Kind, "unit_lead", StringComparison.OrdinalIgnoreCase)
                || string.Equals(card.Kind, "exact_lead", StringComparison.OrdinalIgnoreCase))
                ? 5
                : 3;
            supportScore += 2;
        }

        var evidenceRole = qualityPenalty >= 10
            ? "low_confidence"
            : supportScore >= Math.Max(5, actionabilityScore - 2)
                ? "supporting_context"
                : "advisory";

        return new RagItemSelectionHints
        {
            EvidenceRole = evidenceRole,
            ActionabilityScore = actionabilityScore,
            SupportScore = supportScore,
            FragmentScore = fragmentScore,
            NavigationScore = navigationScore,
            QualityPenalty = qualityPenalty
        };
    }

    private static int ComputeSelectionQualityPenalty(RagItemExtractionQuality? quality)
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

    private static string? ResolveDocumentQualityStatus(ResolvedSourceRow row, string? textStatus)
    {
        if (row.OcrApplied
            && string.Equals(textStatus, "ok", StringComparison.OrdinalIgnoreCase)
            && string.Equals(row.ExtractionSource, "pdf_text_plus_image_ocr", StringComparison.OrdinalIgnoreCase))
        {
            return "image_ocr_applied_ok";
        }
        if (row.OcrApplied
            && string.Equals(textStatus, "ok", StringComparison.OrdinalIgnoreCase)
            && (row.PageWarningCount > 0 || row.PageReviewRecommendedCount > 0))
        {
            return "ocr_applied_ok_with_page_warnings";
        }
        if (row.OcrApplied && string.Equals(textStatus, "ok", StringComparison.OrdinalIgnoreCase))
            return "ocr_applied_ok";
        if (row.OcrApplied && !string.IsNullOrWhiteSpace(textStatus) && !string.Equals(textStatus, "ok", StringComparison.OrdinalIgnoreCase))
            return "ocr_applied_low_confidence";
        if (row.OcrAttempted && !row.OcrApplied && row.OcrRecommended)
            return "ocr_failed_or_insufficient";
        if (string.Equals(textStatus, "empty_text", StringComparison.OrdinalIgnoreCase))
            return "manual_review_empty_text";
        if (string.Equals(textStatus, "low_text", StringComparison.OrdinalIgnoreCase))
            return "manual_review_low_text";
        if (string.Equals(textStatus, "unknown", StringComparison.OrdinalIgnoreCase))
            return "unknown";
        if (string.Equals(textStatus, "ok", StringComparison.OrdinalIgnoreCase) && row.PageReviewRecommendedCount > 0)
            return "extraction_ok_with_page_review";
        if (string.Equals(textStatus, "ok", StringComparison.OrdinalIgnoreCase) && row.PageWarningCount > 0)
            return "extraction_ok_with_page_warnings";
        if (string.Equals(textStatus, "ok", StringComparison.OrdinalIgnoreCase) && row.ImagePageCount > 0 && !row.OcrApplied)
            return "text_extraction_ok_with_images";
        if (string.Equals(textStatus, "ok", StringComparison.OrdinalIgnoreCase))
            return "extraction_ok";
        if (row.QualityPageCount > 0 && row.TextPageCount <= 0)
            return "manual_review_empty_text";
        return string.IsNullOrWhiteSpace(textStatus) ? null : "unknown";
    }

    private static double? ResolveDocumentExtractionConfidence(ResolvedSourceRow row, string? textStatus)
    {
        if (row.OcrApplied
            && string.Equals(textStatus, "ok", StringComparison.OrdinalIgnoreCase)
            && string.Equals(row.ExtractionSource, "pdf_text_plus_image_ocr", StringComparison.OrdinalIgnoreCase))
        {
            return 0.92;
        }
        if (row.OcrApplied
            && string.Equals(textStatus, "ok", StringComparison.OrdinalIgnoreCase)
            && (row.PageWarningCount > 0 || row.PageReviewRecommendedCount > 0))
        {
            return 0.86;
        }
        if (row.OcrApplied && string.Equals(textStatus, "ok", StringComparison.OrdinalIgnoreCase))
            return 0.90;
        if (string.Equals(textStatus, "ok", StringComparison.OrdinalIgnoreCase) && row.PageReviewRecommendedCount > 0)
            return 0.82;
        if (string.Equals(textStatus, "ok", StringComparison.OrdinalIgnoreCase) && row.PageWarningCount > 0)
            return 0.88;
        if (string.Equals(textStatus, "ok", StringComparison.OrdinalIgnoreCase) && row.ImagePageCount > 0 && !row.OcrApplied)
            return 0.85;
        if (row.OcrApplied)
            return 0.45;
        if (row.OcrAttempted && !row.OcrApplied && row.OcrRecommended)
            return 0.30;
        if (string.Equals(textStatus, "ok", StringComparison.OrdinalIgnoreCase))
            return 1.00;
        if (string.Equals(textStatus, "low_text", StringComparison.OrdinalIgnoreCase))
            return 0.35;
        if (string.Equals(textStatus, "empty_text", StringComparison.OrdinalIgnoreCase))
            return 0.15;
        if (row.QualityPageCount > 0 && row.TextPageCount > 0)
            return 0.75;
        return null;
    }

    private static bool IsManualReviewRecommended(ResolvedSourceRow row, string? textStatus)
        => string.Equals(textStatus, "empty_text", StringComparison.OrdinalIgnoreCase)
           || string.Equals(textStatus, "low_text", StringComparison.OrdinalIgnoreCase)
           || string.Equals(textStatus, "unknown", StringComparison.OrdinalIgnoreCase)
           || (row.OcrAttempted && !row.OcrApplied && row.OcrRecommended)
           || (row.OcrApplied && !string.Equals(textStatus, "ok", StringComparison.OrdinalIgnoreCase))
           || (row.QualityPageCount > 0 && row.TextPageCount <= 0)
           || row.PageReviewRecommendedCount > 0;

    private static RagItemExtractionDiagnosticSummary? BuildDiagnosticSummary(ResolvedSourceRow row)
    {
        var ocrDiagnostics = ParseOcrDiagnostics(row.OcrDiagnosticsJson);
        var retrievalChunkQuality = BuildRetrievalChunkQuality(row.RetrievalChunkQualityJson);
        var summary = new RagItemExtractionDiagnosticSummary
        {
            NativeTextStatus = NullIfWhiteSpace(row.NativeTextStatus),
            NativeOcrRecommended = row.NativeOcrRecommended,
            OcrMode = ocrDiagnostics.Mode,
            OcrLanguages = NullIfWhiteSpace(row.OcrLanguages),
            OcrDurationMs = row.OcrDurationMs,
            OcrFailureReason = ocrDiagnostics.FailureReason,
            OcrAppliedReason = ocrDiagnostics.AppliedReason,
            OcrTimedOut = ocrDiagnostics.TimedOut == true ? true : null,
            OcrAttemptedPageCount = PositiveOrNull(ocrDiagnostics.AttemptedPageCount.GetValueOrDefault()),
            OcrSkippedPageCount = PositiveOrNull(ocrDiagnostics.SkippedPageCount.GetValueOrDefault()),
            OcrPagesWithNovelTextCount = PositiveOrNull(ocrDiagnostics.PagesWithNovelTextCount.GetValueOrDefault()),
            PageCount = PositiveOrNull(row.QualityPageCount),
            TextPageCount = row.QualityPageCount > 0 ? row.TextPageCount : null,
            EmptyPageCount = row.QualityPageCount > 0 ? row.EmptyPageCount : null,
            SparsePageCount = row.QualityPageCount > 0 ? row.SparsePageCount : null,
            ImagePageCount = PositiveOrNull(row.ImagePageCount),
            PageWarningCount = PositiveOrNull(row.PageWarningCount),
            PageReviewRecommendedCount = PositiveOrNull(row.PageReviewRecommendedCount),
            RetrievalChunkQuality = retrievalChunkQuality
        };

        return HasDiagnosticValue(summary) ? summary : null;
    }

    private static bool HasDiagnosticValue(RagItemExtractionDiagnosticSummary summary)
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

    private static RagItemRetrievalChunkQuality? BuildRetrievalChunkQuality(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || string.Equals(json, "null", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var reasons = ReadRetrievalChunkRejectionReasons(root);
            var summary = new RagItemRetrievalChunkQuality
            {
                TotalChunkCount = NonNegativeOrNull(GetJsonInt(root, "totalChunkCount")),
                SearchableChunkCount = NonNegativeOrNull(GetJsonInt(root, "searchableChunkCount")),
                RejectedChunkCount = NonNegativeOrNull(GetJsonInt(root, "rejectedChunkCount")),
                ManualReviewRecommended = GetJsonBool(root, "manualReviewRecommended"),
                RejectionReasons = reasons.Count == 0 ? null : reasons
            };

            return HasRetrievalChunkQualityValue(summary) ? summary : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, int> ReadRetrievalChunkRejectionReasons(JsonElement root)
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

    private static bool HasRetrievalChunkQualityValue(RagItemRetrievalChunkQuality summary)
        => summary.TotalChunkCount.HasValue
           || summary.SearchableChunkCount.HasValue
           || summary.RejectedChunkCount.HasValue
           || summary.ManualReviewRecommended.HasValue
           || summary.RejectionReasons is { Count: > 0 };

    private static int? NonNegativeOrNull(int? value)
        => value is >= 0 ? value : null;

    private static OcrDiagnosticsSummary ParseOcrDiagnostics(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || string.Equals(json, "null", StringComparison.OrdinalIgnoreCase))
            return new OcrDiagnosticsSummary();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new OcrDiagnosticsSummary();

            var failureReason = GetJsonString(root, "failureReason");
            var timedOut = GetJsonBool(root, "timedOut");
            var imageFailureReason = default(string);
            var imageTimedOut = false;
            if (root.TryGetProperty("imagePageDiagnostics", out var imageDiagnostics)
                && imageDiagnostics.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in imageDiagnostics.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;

                    imageTimedOut |= GetJsonBool(item, "timedOut") == true;
                    var status = GetJsonString(item, "status");
                    var reason = GetJsonString(item, "reason");
                    if (imageFailureReason is null
                        && (ContainsFailure(status) || ContainsFailure(reason) || GetJsonBool(item, "timedOut") == true))
                    {
                        imageFailureReason = NullIfWhiteSpace(reason) ?? NullIfWhiteSpace(status) ?? "ocr_failed";
                    }
                }
            }

            return new OcrDiagnosticsSummary(
                Mode: NullIfWhiteSpace(GetJsonString(root, "mode")),
                FailureReason: NullIfWhiteSpace(failureReason) ?? imageFailureReason,
                AppliedReason: NullIfWhiteSpace(GetJsonString(root, "appliedReason")),
                TimedOut: timedOut == true || imageTimedOut,
                AttemptedPageCount: GetJsonInt(root, "attemptedPageCount"),
                SkippedPageCount: GetJsonInt(root, "skippedPageCount"),
                PagesWithNovelTextCount: CountJsonArray(root, "pagesWithNovelText"));
        }
        catch (JsonException)
        {
            return new OcrDiagnosticsSummary();
        }
    }

    private static bool ContainsFailure(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && (value.Contains("fail", StringComparison.OrdinalIgnoreCase)
               || value.Contains("timeout", StringComparison.OrdinalIgnoreCase)
               || value.Contains("timed_out", StringComparison.OrdinalIgnoreCase));

    private static string? GetJsonString(JsonElement source, string propertyName)
        => source.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? GetJsonInt(JsonElement source, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var property))
            return null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
            return value;
        return property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out value)
            ? value
            : null;
    }

    private static bool? GetJsonBool(JsonElement source, string propertyName)
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

    private static int? CountJsonArray(JsonElement source, string propertyName)
        => source.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Array
            ? property.GetArrayLength()
            : null;

    private static int? PositiveOrNull(int value)
        => value > 0 ? value : null;

    internal static List<RagItemContentCard> ParseContentCards(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<RagItemContentCard>>(json) is { Count: > 0 } cards
                ? cards
                    .Where(static card => !string.IsNullOrWhiteSpace(card.Title))
                    .Where(IsContentCardSafeToExpose)
                    .Take(32)
                    .ToList()
                : [];
        }
        catch
        {
            return [];
        }
    }

    private static bool IsContentCardSafeToExpose(RagItemContentCard card)
    {
        if (card.PageStart is > 0 || card.PageEnd is > 0)
        {
            return IsDeterministicContentCardKind(card.Kind)
                   || HasGroundedContentCardEvidence(card.Evidence)
                   || HasTechnicalIdentifier(card.Title);
        }

        return HasGroundedContentCardEvidence(card.Evidence)
               || HasTechnicalIdentifier(card.Title);
    }

    private static bool IsDeterministicContentCardKind(string? kind)
    {
        var normalized = string.IsNullOrWhiteSpace(kind)
            ? string.Empty
            : kind.Trim().ToLowerInvariant();
        return normalized is "section" or "exact_lead" or "page_embedded_title" or "unit_lead" or "standard_ref";
    }

    private static bool HasGroundedContentCardEvidence(JsonElement? evidence)
    {
        if (evidence is not { ValueKind: JsonValueKind.Object } root)
            return false;

        if (root.TryGetProperty("facts", out var facts)
            && facts.ValueKind == JsonValueKind.Array
            && facts.EnumerateArray().Any(HasGroundedEvidenceFact))
        {
            return true;
        }

        return root.TryGetProperty("quantityFacts", out var quantityFacts)
               && quantityFacts.ValueKind == JsonValueKind.Array
               && quantityFacts.EnumerateArray().Any(static fact =>
                   fact.ValueKind == JsonValueKind.Object
                   && HasNonBlankJsonString(fact, "sourceText"));
    }

    private static bool HasGroundedEvidenceFact(JsonElement fact)
    {
        if (fact.ValueKind != JsonValueKind.Object)
            return false;

        return HasNonBlankJsonString(fact, "sourceText")
               || GetPositiveJsonInt(fact, "pageStart") is > 0
               || GetPositiveJsonInt(fact, "pageEnd") is > 0;
    }

    private static bool HasNonBlankJsonString(JsonElement source, string propertyName)
        => source.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(value.GetString());

    private static int? GetPositiveJsonInt(JsonElement source, string propertyName)
        => source.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var parsed)
           && parsed > 0
            ? parsed
            : null;

    private static bool HasTechnicalIdentifier(string? title)
        => !string.IsNullOrWhiteSpace(title)
           && ExactMatchEntryExtractor.ExtractTargetedReferences(title).Any();

    private static List<string> ParseStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            return doc.RootElement.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString())
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Select(static item => item!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string NormalizeSourceLanguageTag(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return "und";

        var normalized = language.Trim().Replace('_', '-').ToLowerInvariant();
        return normalized.Length is >= 2 and <= 35 ? normalized : "und";
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class ResolvedSourceRow
    {
        public Guid? DocId { get; set; }
        public string? DocPath { get; set; }
        public string? DocName { get; set; }
        public int PageCount { get; set; }
        public string? Category { get; set; }
        public string? CategoryRef { get; set; }
        public string? CategoryPath { get; set; }
        public string? SourceHash { get; set; }
        public Guid? ProfileId { get; set; }
        public string? ProfileVersion { get; set; }
        public string? ProfileLanguage { get; set; }
        public string[]? ProfileKeywords { get; set; }
        public string[]? ProfileEntities { get; set; }
        public string[]? ProfileTopics { get; set; }
        public string[]? ProfileHypotheticalQuestions { get; set; }
        public string[]? ProfileLimits { get; set; }
        public string? SummaryLanguage { get; set; }
        public string? RunDocumentLanguage { get; set; }
        public string? ExtractionSource { get; set; }
        public bool OcrAttempted { get; set; }
        public bool OcrApplied { get; set; }
        public string? OcrLanguages { get; set; }
        public long? OcrDurationMs { get; set; }
        public string? NativeTextStatus { get; set; }
        public bool? NativeOcrRecommended { get; set; }
        public string? OcrDiagnosticsJson { get; set; }
        public string? TextStatus { get; set; }
        public bool OcrRecommended { get; set; }
        public string? SignalsJson { get; set; }
        public string? RetrievalChunkQualityJson { get; set; }
        public int QualityPageCount { get; set; }
        public int TextPageCount { get; set; }
        public int EmptyPageCount { get; set; }
        public int SparsePageCount { get; set; }
        public int ImagePageCount { get; set; }
        public int PageWarningCount { get; set; }
        public int PageReviewRecommendedCount { get; set; }
        public string? ContentCardsJson { get; set; }
    }

    private sealed record OcrDiagnosticsSummary(
        string? Mode = null,
        string? FailureReason = null,
        string? AppliedReason = null,
        bool? TimedOut = null,
        int? AttemptedPageCount = null,
        int? SkippedPageCount = null,
        int? PagesWithNovelTextCount = null);
}
