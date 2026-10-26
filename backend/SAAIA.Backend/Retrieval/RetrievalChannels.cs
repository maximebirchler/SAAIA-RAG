using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;
using static SAAIA.Backend.Retrieval.RetrievalAnswerGuidance;
using static SAAIA.Backend.Retrieval.RetrievalCandidateSignals;
using static SAAIA.Backend.Retrieval.RetrievalLexicalExpansion;
using static SAAIA.Backend.Retrieval.RetrievalMetadata;
using static SAAIA.Backend.Retrieval.RetrievalNavigation;
using static SAAIA.Backend.Retrieval.RetrievalQueryPolicy;
using static SAAIA.Backend.Retrieval.RetrievalResultProjector;
using static SAAIA.Backend.Retrieval.RetrievalRows;
using static SAAIA.Backend.Retrieval.RetrievalSelection;
using static SAAIA.Backend.Retrieval.RetrievalText;

namespace SAAIA.Backend.Retrieval;

internal static partial class RetrievalChannels
{

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
        var normalizedTerms = BuildExactMatchLookupTerms(query);
        if (normalizedTerms.Count == 0)
            return [];

        var referenceKeys = ExactMatchEntryExtractor.ExtractReferenceKeys(query);

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string lookupPredicateToken = "__exact_match_lookup_predicate__";
        const string sqlTemplate = """
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
    CASE
        WHEN COALESCE(e.metadata->>'kind', 'verbatim_excerpt') = 'verbatim_excerpt'
          AND char_length(e.text_content) BETWEEN 12 AND 160
          AND same_page_context.text_content IS NOT NULL
            THEN e.text_content || E'\n' || same_page_context.text_content
        ELSE e.text_content
    END AS "Text",
    d.indexed_version AS "IngestionVersion",
    LOWER(ENCODE(d.content_hash, 'hex')) AS "HashDoc",
    COALESCE(e.metadata->>'kind', 'verbatim_excerpt') AS "MatchKind",
    s.title AS "SectionTitle",
    lookup_terms.term AS "MatchedTerm",
    COALESCE(e.metadata->>'extractionTextStatus', u.metadata->>'extractionTextStatus') AS "ExtractionTextStatus",
    CASE
        WHEN LOWER(COALESCE(e.metadata->>'extractionTextSparse', u.metadata->>'extractionTextSparse', '')) IN ('true', 'false')
            THEN COALESCE(e.metadata->>'extractionTextSparse', u.metadata->>'extractionTextSparse')::boolean
        ELSE NULL
    END AS "ExtractionTextSparse",
    CASE
        WHEN LOWER(COALESCE(e.metadata->>'extractionOcrCandidate', u.metadata->>'extractionOcrCandidate', '')) IN ('true', 'false')
            THEN COALESCE(e.metadata->>'extractionOcrCandidate', u.metadata->>'extractionOcrCandidate')::boolean
        ELSE NULL
    END AS "ExtractionOcrCandidate",
    CASE
        WHEN jsonb_typeof(e.metadata->'extractionQualitySignals') = 'array'
            THEN (e.metadata->'extractionQualitySignals')::text
        WHEN jsonb_typeof(u.metadata->'extractionQualitySignals') = 'array'
            THEN (u.metadata->'extractionQualitySignals')::text
        ELSE NULL
    END AS "ExtractionQualitySignalsJson"
FROM (
    SELECT *
    FROM documents
    WHERE tenant_id = @tenant_id
      AND status = 'indexed'
      AND indexed_version > 0
      AND (@category IS NULL OR LOWER(category) = @category)
      AND (@category_path IS NULL OR doc_path = @category_path OR doc_path LIKE (@category_path || '/%'))
      AND (@doc_id IS NULL OR doc_id = @doc_id)
      AND (@doc_path IS NULL OR doc_path = @doc_path)
) d
JOIN document_revisions r
  ON r.tenant_id = d.tenant_id
 AND r.doc_id = d.doc_id
 AND r.indexed_version = d.indexed_version
JOIN exact_match_entries e
  ON e.tenant_id = r.tenant_id
 AND e.revision_id = r.revision_id
JOIN lookup_terms
  ON __exact_match_lookup_predicate__
LEFT JOIN document_sections s
  ON s.section_id = e.section_id
LEFT JOIN document_units u
  ON u.unit_id = e.unit_id
LEFT JOIN LATERAL (
    SELECT rc.text_content
    FROM retrieval_chunks rc
    WHERE rc.tenant_id = e.tenant_id
      AND rc.revision_id = e.revision_id
      AND rc.page_start <= e.page_end
      AND rc.page_end >= e.page_start
      AND char_length(rc.text_content) > char_length(e.text_content) + 80
      AND COALESCE(rc.metadata->>'contentRole', 'content') <> 'navigation'
      AND COALESCE(rc.metadata->>'chunkType', '') <> 'navigation_index_v1'
      AND COALESCE(rc.metadata->>'extractionTextStatus', 'ok') <> 'empty_text'
      AND COALESCE(rc.metadata->>'extractionTextSparse', 'false') <> 'true'
      AND NOT COALESCE(rc.metadata->'extractionQualitySignals' ? 'replacement_chars_remaining', false)
    ORDER BY
        CASE COALESCE(rc.metadata->>'contentRole', 'content')
            WHEN 'content' THEN 0
            WHEN 'mixed_navigation_content' THEN 1
            ELSE 2
        END,
        CASE
            WHEN NULLIF(rc.metadata->>'contentDensityScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
                THEN (rc.metadata->>'contentDensityScore')::double precision
            ELSE 0.0
        END DESC,
        rc.chunk_index ASC
    LIMIT 1
) same_page_context ON TRUE
WHERE d.tenant_id = @tenant_id
  AND d.status = 'indexed'
  AND d.indexed_version > 0
  AND (@category IS NULL OR LOWER(d.category) = @category)
  AND (@category_path IS NULL OR d.doc_path = @category_path OR d.doc_path LIKE (@category_path || '/%'))
  AND (@doc_id IS NULL OR d.doc_id = @doc_id)
  AND (@doc_path IS NULL OR d.doc_path = @doc_path)
ORDER BY
    CASE
        WHEN lookup_terms.term = e.normalized_text THEN 0
        WHEN e.normalized_text LIKE lookup_terms.term || '%' THEN 1
        ELSE 2
    END,
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

        Task<IEnumerable<ExactMatchRow>> QueryExactRowsAsync(
            string lookupPredicate,
            IReadOnlyList<string> lookupTerms,
            int limit)
        {
            var sql = sqlTemplate.Replace(lookupPredicateToken, lookupPredicate, StringComparison.Ordinal);
            return conn.QueryAsync<ExactMatchRow>(new CommandDefinition(sql, new
            {
                tenant_id = tenantId,
                normalized_terms = lookupTerms.ToArray(),
                category,
                category_path = normalizedCategoryPath,
                doc_id = normalizedDocId,
                doc_path = normalizedDocPath,
                top_k = limit
            }, cancellationToken: ct));
        }

        var rows = (await QueryExactRowsAsync(
            "md5(lookup_terms.term) = md5(e.normalized_text) AND lookup_terms.term = e.normalized_text",
            normalizedTerms,
            topK)).ToList();
        if (rows.Count < topK)
        {
            var containsTerms = BuildContainsExactMatchLookupTerms(normalizedTerms);
            if (containsTerms.Count > 0)
            {
                var containsRows = await QueryExactRowsAsync(
                    """
                    lookup_terms.term <> e.normalized_text
                      AND (
                          e.normalized_text LIKE lookup_terms.term || '%'
                          OR e.normalized_text LIKE '% ' || lookup_terms.term || '%'
                      )
                    """,
                    containsTerms,
                    topK - rows.Count);
                rows.AddRange(containsRows);
            }
        }

        var uniqueRows = rows
            .GroupBy(static row => (row.DocId, row.PageStart, row.PageEnd, row.Text))
            .Select(static group => group.First());

        var matches = uniqueRows.Select(row => new RagMatch(
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
            SameSectionChunkId: null,
            ExtractionTextStatus: row.ExtractionTextStatus,
            ExtractionTextSparse: row.ExtractionTextSparse,
            ExtractionOcrCandidate: row.ExtractionOcrCandidate,
            ExtractionQualitySignals: ParseExtractionQualitySignalsJson(row.ExtractionQualitySignalsJson),
            ExtractionQualityObservation: ObserveExtractionQualitySignalsJson(row.ExtractionQualitySignalsJson))).ToList();

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


    internal static IReadOnlyList<string> BuildExactMatchLookupTerms(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        var terms = new HashSet<string>(
            ExactMatchEntryExtractor.ExtractLookupTerms(query),
            StringComparer.Ordinal);

        foreach (var phrase in ExtractQuotedLookupPhrases(query).Concat(ExtractFocusedLookupPhrases(query)))
        {
            var normalized = ExactMatchEntryExtractor.NormalizeForLookup(phrase);
            if (!string.IsNullOrWhiteSpace(normalized))
                terms.Add(normalized);
        }

        var pruneWeakQuantityTerms = ShouldPruneWeakExactLookupTermsForQuery(query);

        return terms
            .Where(static term => term.Length >= 4)
            .Where(term => !pruneWeakQuantityTerms || !IsWeakCountQuantityExactLookupTerm(term))
            .OrderByDescending(static term => term.Length)
            .ThenBy(static term => term, StringComparer.Ordinal)
            .Take(64)
            .ToArray();
    }


    internal static IReadOnlyList<string> BuildContainsExactMatchLookupTerms(IReadOnlyList<string> normalizedTerms)
        => normalizedTerms
            .Where(ShouldUseContainsExactMatchLookupTerm)
            .ToArray();


    internal static bool ShouldUseContainsExactMatchLookupTerm(string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return false;

        var normalized = ExactMatchEntryExtractor.NormalizeForLookup(term);
        if (string.IsNullOrWhiteSpace(normalized) || !normalized.Contains(' '))
            return false;

        var tokenCount = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length;
        return normalized.Length >= 12 || (normalized.Length >= 8 && tokenCount >= 3);
    }


    internal static bool ShouldPruneWeakExactLookupTermsForQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || ExtractQuotedLookupPhrases(query).Count > 0)
            return false;

        var normalized = $" {FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)).ToLowerInvariant()} ";
        return ContainsBroadOnlySparseQueryIntent(query)
            || ContainsExplicitBroadScopedSynthesisIntent(normalized)
            || ContainsBroadScopedSynthesisIntent(normalized)
            || ContainsSituationalBroadSelectionIntent(normalized)
            || ShouldPreferDocumentDiversity(query);
    }


    internal static bool IsWeakCountQuantityExactLookupTerm(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return false;

        var normalized = FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(term)).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var compact = normalized.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (SafeRegexIsMatch(
                compact,
                @"^(?:pour|for|para|per|fur|fuer)\d{1,3}$",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                TimeSpan.FromMilliseconds(100)))
        {
            return true;
        }

        return SafeRegexIsMatch(
                normalized,
                @"^(?:pour|for|para|per|fur|fuer)\s+\d{1,3}(?:\s+(?:personnes?|people|items?|elements?|units?|unites?))?$",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                TimeSpan.FromMilliseconds(100))
            || SafeRegexIsMatch(
                normalized,
                @"^\d{1,3}\s+(?:personnes?|people|items?|elements?|units?|unites?)$",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                TimeSpan.FromMilliseconds(100));
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
        var quotedTerms = ExtractTitleLookupPhrases(query);
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
    SELECT DISTINCT
        LOWER(term) AS term,
        replace(replace(replace(replace(replace(replace(
            lower(translate(
                term,
                'ÀÁÂÃÄÅàáâãäåÇçÈÉÊËèéêëÌÍÎÏìíîïÑñÒÓÔÕÖØòóôõöøÙÚÛÜùúûüÝýÿ',
                'AAAAAAaaaaaaCcEEEEeeeeIIIIiiiiNnOOOOOOooooooUUUUuuuuYyy'
            )),
            'œ', 'oe'), 'æ', 'ae'), 'ß', 'ss'), 'ø', 'o'), 'ł', 'l'), 'đ', 'd') AS folded_term
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
              OR LOWER(pcc.search_text) LIKE '%' || quoted_terms.folded_term || '%'
              OR pcc.normalized_title LIKE '%' || quoted_terms.folded_term || '%'
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
          AND saaia_is_safe_profile_content_card(card.kind, card.title, card.page_start, card.page_end, card.metadata)
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
          OR LOWER(pcc.search_text) LIKE '%' || quoted_terms.folded_term || '%'
          OR pcc.normalized_title LIKE '%' || quoted_terms.folded_term || '%'
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
        WHEN NULLIF(rc.metadata->>'navigationScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
            THEN (rc.metadata->>'navigationScore')::double precision
        ELSE NULL
    END AS "NavigationScore",
    CASE
        WHEN NULLIF(rc.metadata->>'contentDensityScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
            THEN (rc.metadata->>'contentDensityScore')::double precision
        ELSE NULL
    END AS "ContentDensityScore",
    rc.metadata->>'prevChunkId' AS "PrevChunkId",
    rc.metadata->>'nextChunkId' AS "NextChunkId",
    rc.metadata->>'sameSectionChunkId' AS "SameSectionChunkId",
    rc.metadata->>'extractionTextStatus' AS "ExtractionTextStatus",
    CASE
        WHEN LOWER(COALESCE(rc.metadata->>'extractionTextSparse', '')) IN ('true', 'false')
            THEN (rc.metadata->>'extractionTextSparse')::boolean
        ELSE NULL
    END AS "ExtractionTextSparse",
    CASE
        WHEN LOWER(COALESCE(rc.metadata->>'extractionOcrCandidate', '')) IN ('true', 'false')
            THEN (rc.metadata->>'extractionOcrCandidate')::boolean
        ELSE NULL
    END AS "ExtractionOcrCandidate",
    CASE
        WHEN jsonb_typeof(rc.metadata->'extractionQualitySignals') = 'array'
            THEN (rc.metadata->'extractionQualitySignals')::text
        ELSE NULL
    END AS "ExtractionQualitySignalsJson",
    pcm.matched_content_cards_json AS "MatchedContentCardsJson",
    (tm.match_weight + (COALESCE(pcm.match_weight, 0.0) * 2.2))::real AS "SparseRank"
FROM (
    SELECT *
    FROM documents
    WHERE tenant_id = @tenant_id
      AND status = 'indexed'
      AND indexed_version > 0
      AND (@category IS NULL OR LOWER(category) = @category)
      AND (@category_path IS NULL OR doc_path = @category_path OR doc_path LIKE (@category_path || '/%'))
      AND (@doc_id IS NULL OR doc_id = @doc_id)
      AND (@doc_path IS NULL OR doc_path = @doc_path)
) d
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
        left(COALESCE(rc.text_content, ''), 720),
        CASE WHEN length(COALESCE(rc.text_content, '')) > 720 THEN right(COALESCE(rc.text_content, ''), 480) ELSE NULL END,
        COALESCE(rc.metadata->>'sectionTitle', s.title),
        COALESCE(rc.metadata->>'headingPath', s.title)
    )) AS text_lc,
    replace(replace(replace(replace(replace(replace(
        lower(translate(
            CONCAT_WS(
                E'\n',
                left(COALESCE(rc.text_content, ''), 720),
                CASE WHEN length(COALESCE(rc.text_content, '')) > 720 THEN right(COALESCE(rc.text_content, ''), 480) ELSE NULL END,
                COALESCE(rc.metadata->>'sectionTitle', s.title),
                COALESCE(rc.metadata->>'headingPath', s.title)
            ),
            'ÀÁÂÃÄÅàáâãäåÇçÈÉÊËèéêëÌÍÎÏìíîïÑñÒÓÔÕÖØòóôõöøÙÚÛÜùúûüÝýÿ',
            'AAAAAAaaaaaaCcEEEEeeeeIIIIiiiiNnOOOOOOooooooUUUUuuuuYyy'
        )),
        'œ', 'oe'), 'æ', 'ae'), 'ß', 'ss'), 'ø', 'o'), 'ł', 'l'), 'đ', 'd') AS text_folded
) normalized_chunk
CROSS JOIN LATERAL (
    SELECT
        COUNT(*) AS match_count,
        LEFT(string_agg(DISTINCT candidate_term.term, '; '), 500) AS matched_phrases,
        COALESCE(SUM(candidate_term.match_weight), 0.0) AS match_weight
    FROM (
        SELECT
            quoted_terms.term,
            CASE
                WHEN normalized_chunk.text_lc LIKE '%' || quoted_terms.term || '%'
                  OR normalized_chunk.text_folded LIKE '%' || quoted_terms.folded_term || '%'
                    THEN 10.0
                WHEN token_match.token_hits >= token_match.required_hits
                    THEN 5.0 + LEAST(6.0, token_match.token_hits::double precision)
                ELSE 0.0
            END AS match_weight
        FROM quoted_terms
        CROSS JOIN LATERAL (
            SELECT
                COUNT(*)::int AS token_count,
                COUNT(*) FILTER (
                    WHERE normalized_chunk.text_lc LIKE '%' || token_parts.token || '%'
                       OR normalized_chunk.text_folded LIKE '%' || token_parts.token || '%'
                )::int AS token_hits
            FROM regexp_split_to_table(quoted_terms.folded_term, '\s+') AS token_parts(token)
            WHERE length(token_parts.token) >= 4
        ) token_counts
        CROSS JOIN LATERAL (
            SELECT CASE
                WHEN token_counts.token_count <= 0 THEN 0
                WHEN token_counts.token_count <= 2 THEN token_counts.token_count
                WHEN token_counts.token_count = 3 THEN 2
                ELSE GREATEST(3, CEIL(token_counts.token_count::double precision * 0.75)::int)
            END AS required_hits,
            token_counts.token_hits
        ) token_match
        WHERE normalized_chunk.text_lc LIKE '%' || quoted_terms.term || '%'
           OR normalized_chunk.text_folded LIKE '%' || quoted_terms.folded_term || '%'
           OR (
                token_counts.token_count > 0
            AND token_match.token_hits >= token_match.required_hits
           )
    ) candidate_term
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

        List<SparseMatchRow> rows;
        try
        {
            rows = (await conn.QueryAsync<SparseMatchRow>(new CommandDefinition(sql, new
            {
                tenant_id = tenantId,
                quoted_terms = quotedTerms.ToArray(),
                category,
                category_path = normalizedCategoryPath,
                doc_id = normalizedDocId,
                doc_path = normalizedDocPath,
                candidate_limit = candidateLimit
            }, commandTimeout: 8, cancellationToken: ct))).ToList();
        }
        catch (Exception ex) when (IsRetrieverDatabaseTimeout(ex))
        {
            RetrievalTelemetry.RecordRetrieverDegraded("quoted_title", ex);
            return [];
        }

        return rows
            .GroupBy(static row => row.ChunkId)
            .Select(static group => group
                .OrderByDescending(static row => row.SparseRank)
                .ThenBy(static row => row.ChunkIndex)
                .First())
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
                    MatchedContentCards: BuildSparseMatchedContentCards(row, query),
                    ExtractionTextStatus: row.ExtractionTextStatus,
                    ExtractionTextSparse: row.ExtractionTextSparse,
                    ExtractionOcrCandidate: row.ExtractionOcrCandidate,
                    ExtractionQualitySignals: ParseExtractionQualitySignalsJson(row.ExtractionQualitySignalsJson),
                    ExtractionQualityObservation: ObserveExtractionQualitySignalsJson(row.ExtractionQualitySignalsJson));
            })
            .ToList();
    }


    private static async Task<IReadOnlyList<SparseMatchRow>> SearchLocalTitleTokenCoverageMatchesAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        IReadOnlyList<string> titleTokens,
        string? category,
        Guid? docId,
        string? docPath,
        string? categoryPath,
        int candidateLimit,
        CancellationToken ct,
        Action<string, string?>? degradedRetrieverRef = null)
    {
        if (titleTokens.Count == 0 || candidateLimit <= 0)
            return Array.Empty<SparseMatchRow>();

        const string sql = """
WITH title_tokens AS (
    SELECT DISTINCT lower(token) AS token
    FROM unnest(@title_tokens::text[]) AS token
    WHERE token IS NOT NULL AND length(token) >= 4
),
scored_chunks AS (
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
        s.title AS section_title,
        token_match.token_hits,
        token_match.body_token_hits,
        CASE
            WHEN NULLIF(rc.metadata->>'contentDensityScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
                THEN (rc.metadata->>'contentDensityScore')::double precision
            ELSE 0.0
        END AS content_density_score
    FROM documents d
    JOIN document_revisions r
      ON r.tenant_id = d.tenant_id
     AND r.doc_id = d.doc_id
     AND r.indexed_version = d.indexed_version
    JOIN retrieval_chunks rc
      ON rc.tenant_id = r.tenant_id
     AND rc.revision_id = r.revision_id
    LEFT JOIN document_sections s
      ON s.section_id = rc.section_id
    CROSS JOIN LATERAL (
        SELECT
            lower(CONCAT_WS(
                E'\n',
                left(COALESCE(rc.text_content, ''), 720),
                CASE WHEN length(COALESCE(rc.text_content, '')) > 720 THEN right(COALESCE(rc.text_content, ''), 480) ELSE NULL END,
                COALESCE(rc.metadata->>'sectionTitle', s.title),
                COALESCE(rc.metadata->>'headingPath', s.title)
            )) AS text_lc,
            lower(CONCAT_WS(
                E'\n',
                left(COALESCE(rc.text_content, ''), 720),
                CASE WHEN length(COALESCE(rc.text_content, '')) > 720 THEN right(COALESCE(rc.text_content, ''), 480) ELSE NULL END
            )) AS body_lc,
            replace(replace(replace(replace(replace(replace(
                lower(translate(
                    CONCAT_WS(
                        E'\n',
                        left(COALESCE(rc.text_content, ''), 720),
                        CASE WHEN length(COALESCE(rc.text_content, '')) > 720 THEN right(COALESCE(rc.text_content, ''), 480) ELSE NULL END,
                        COALESCE(rc.metadata->>'sectionTitle', s.title),
                        COALESCE(rc.metadata->>'headingPath', s.title)
                    ),
                    'Ã€ÃÃ‚ÃƒÃ„Ã…Ã Ã¡Ã¢Ã£Ã¤Ã¥Ã‡Ã§ÃˆÃ‰ÃŠÃ‹Ã¨Ã©ÃªÃ«ÃŒÃÃŽÃÃ¬Ã­Ã®Ã¯Ã‘Ã±Ã’Ã“Ã”Ã•Ã–Ã˜Ã²Ã³Ã´ÃµÃ¶Ã¸Ã™ÃšÃ›ÃœÃ¹ÃºÃ»Ã¼ÃÃ½Ã¿',
                    'AAAAAAaaaaaaCcEEEEeeeeIIIIiiiiNnOOOOOOooooooUUUUuuuuYyy'
                )),
                'Å“', 'oe'), 'Ã¦', 'ae'), 'ÃŸ', 'ss'), 'Ã¸', 'o'), 'Å‚', 'l'), 'Ä‘', 'd') AS text_folded
    ) local_text
    CROSS JOIN LATERAL (
        SELECT
            COUNT(*)::int AS token_hits,
            COUNT(*) FILTER (
                WHERE local_text.body_lc LIKE '%' || title_tokens.token || '%'
            )::int AS body_token_hits
        FROM title_tokens
        WHERE local_text.text_lc LIKE '%' || title_tokens.token || '%'
           OR local_text.text_folded LIKE '%' || title_tokens.token || '%'
    ) token_match
    WHERE d.tenant_id = @tenant_id
      AND d.status = 'indexed'
      AND d.indexed_version > 0
      AND (@category IS NULL OR LOWER(d.category) = @category)
      AND (@category_path IS NULL OR d.doc_path = @category_path OR d.doc_path LIKE (@category_path || '/%'))
      AND (@doc_id IS NULL OR d.doc_id = @doc_id)
      AND (@doc_path IS NULL OR d.doc_path = @doc_path)
      AND token_match.token_hits >= @minimum_token_hits
)
SELECT
    doc_id AS "DocId",
    doc_path AS "DocPath",
    doc_name AS "DocName",
    category AS "Category",
    page_start AS "PageStart",
    page_end AS "PageEnd",
    CASE
        WHEN NULLIF(metadata->>'offsetStart', '') ~ '^[-+]?[0-9]+$'
            THEN CASE
                WHEN (metadata->>'offsetStart')::numeric BETWEEN -2147483648 AND 2147483647
                    THEN (metadata->>'offsetStart')::int
                ELSE NULL
            END
        ELSE NULL
    END AS "OffsetStart",
    CASE
        WHEN NULLIF(metadata->>'offsetEnd', '') ~ '^[-+]?[0-9]+$'
            THEN CASE
                WHEN (metadata->>'offsetEnd')::numeric BETWEEN -2147483648 AND 2147483647
                    THEN (metadata->>'offsetEnd')::int
                ELSE NULL
            END
        ELSE NULL
    END AS "OffsetEnd",
    retrieval_chunk_id AS "ChunkId",
    chunk_index AS "ChunkIndex",
    text_content AS "Text",
    indexed_version AS "IngestionVersion",
    LOWER(ENCODE(content_hash, 'hex')) AS "HashDoc",
    text_content AS "EmbedText",
    section_id AS "SectionOrdinalPlaceholder",
    COALESCE(metadata->>'sectionTitle', section_title) AS "SectionTitle",
    COALESCE(metadata->>'headingPath', section_title) AS "HeadingPath",
    COALESCE(metadata->>'chunkType', 'contextual_text_v1') AS "ChunkType",
    COALESCE(metadata->>'contentRole', 'content') AS "ContentRole",
    metadata->>'navigationReason' AS "NavigationReason",
    metadata->>'originalChunkType' AS "OriginalChunkType",
    CASE
        WHEN NULLIF(metadata->>'navigationScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
            THEN (metadata->>'navigationScore')::double precision
        ELSE NULL
    END AS "NavigationScore",
    CASE
        WHEN NULLIF(metadata->>'contentDensityScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
            THEN (metadata->>'contentDensityScore')::double precision
        ELSE NULL
    END AS "ContentDensityScore",
    metadata->>'prevChunkId' AS "PrevChunkId",
    metadata->>'nextChunkId' AS "NextChunkId",
    metadata->>'sameSectionChunkId' AS "SameSectionChunkId",
    metadata->>'extractionTextStatus' AS "ExtractionTextStatus",
    CASE
        WHEN LOWER(COALESCE(metadata->>'extractionTextSparse', '')) IN ('true', 'false')
            THEN (metadata->>'extractionTextSparse')::boolean
        ELSE NULL
    END AS "ExtractionTextSparse",
    CASE
        WHEN LOWER(COALESCE(metadata->>'extractionOcrCandidate', '')) IN ('true', 'false')
            THEN (metadata->>'extractionOcrCandidate')::boolean
        ELSE NULL
    END AS "ExtractionOcrCandidate",
    CASE
        WHEN jsonb_typeof(metadata->'extractionQualitySignals') = 'array'
            THEN (metadata->'extractionQualitySignals')::text
        ELSE NULL
    END AS "ExtractionQualitySignalsJson",
    NULL::text AS "MatchedContentCardsJson",
    LEAST(1.02, 0.76 + LEAST(0.20, token_hits::double precision * 0.045) + LEAST(0.06, content_density_score * 0.06))::real AS "SparseRank"
FROM scored_chunks
ORDER BY
    body_token_hits DESC,
    CASE COALESCE(metadata->>'chunkType', '')
        WHEN 'footer_titled_item_window_v1' THEN 0
        WHEN 'unit_exact_v1' THEN 1
        WHEN 'section_window_v1' THEN 2
        ELSE 3
    END,
    token_hits DESC,
    content_density_score DESC,
    doc_path ASC,
    chunk_index ASC
LIMIT @candidate_limit;
""";

        try
        {
            return (await conn.QueryAsync<SparseMatchRow>(new CommandDefinition(sql, new
            {
                tenant_id = tenantId,
                title_tokens = titleTokens.ToArray(),
                category,
                category_path = categoryPath,
                doc_id = docId,
                doc_path = docPath,
                minimum_token_hits = ResolveLocalTitleTokenMinimumHits(titleTokens.Count),
                candidate_limit = candidateLimit
            }, commandTimeout: 12, cancellationToken: ct))).ToList();
        }
        catch (PostgresException ex)
        {
            RetrievalTelemetry.RecordRetrieverDegraded("local_title_token_route", ex);
            degradedRetrieverRef?.Invoke("local_title_token_route", FormatPostgresRetrieverError(ex));
            return Array.Empty<SparseMatchRow>();
        }
        catch (Exception ex) when (IsRetrieverDatabaseTimeout(ex))
        {
            RetrievalTelemetry.RecordRetrieverDegraded("local_title_token_route", ex);
            degradedRetrieverRef?.Invoke("local_title_token_route", FormatRetrieverError(ex));
            return Array.Empty<SparseMatchRow>();
        }
    }


    internal static int ResolveLocalTitleTokenMinimumHits(int tokenCount)
        => tokenCount switch
        {
            <= 0 => 1,
            1 => 1,
            2 => 2,
            3 or 4 => 2,
            _ => 3
        };


    private static bool ShouldKeepLocalTitleTokenCandidate(SparseMatchRow row, double quotedScore)
    {
        if (!IsLocalTitleTokenNavigationCandidate(row))
            return true;

        return ShouldPromoteLocalTitleTokenNavigationCandidate(row, quotedScore);
    }


    private static bool ShouldPromoteLocalTitleTokenNavigationCandidate(SparseMatchRow row, double quotedScore)
    {
        if (!IsLocalTitleTokenNavigationCandidate(row))
            return false;
        if (quotedScore < 16.0)
            return false;

        var text = row.Text ?? string.Empty;
        if (EstimateNormalizedTokenCount(text) < 36)
            return false;
        if (CountInlinePageNumberBoundaries(text) >= 5)
            return false;
        if (LooksLikeTitleListChunk(text) && !LooksLikeStructuredAnswerText(text))
            return false;

        return true;
    }


    private static bool IsLocalTitleTokenNavigationCandidate(SparseMatchRow row)
        => string.Equals(row.ContentRole, RetrievalContentClassifier.NavigationRole, StringComparison.OrdinalIgnoreCase)
           || RetrievalContentClassifier.IsNavigationChunkType(row.ChunkType);


    internal static bool HasLocalTitleTokenLeadEvidence(SparseMatchRow row, IReadOnlyList<string> quotedPhrases)
        => HasLocalTitleTokenLeadEvidence(row.Text, row.SectionTitle, row.HeadingPath, quotedPhrases);


    internal static bool HasLocalTitleTokenLeadEvidence(
        string? text,
        string? sectionTitle,
        string? headingPath,
        IReadOnlyList<string> quotedPhrases)
    {
        if (quotedPhrases.Count == 0)
            return false;

        var titleSignal = string.Join(' ', new[] { sectionTitle, headingPath }
            .Where(static value => !string.IsNullOrWhiteSpace(value)));
        if (ComputeQuotedLookupCandidateScore(quotedPhrases, titleSignal) > 0.0)
            return true;

        var normalizedText = NormalizeForLexicalSignal(text);
        if (string.IsNullOrWhiteSpace(normalizedText))
            return false;

        var leadingText = normalizedText.Length <= 240
            ? normalizedText
            : normalizedText[..240];
        var compactLeadingText = leadingText.Replace(" ", string.Empty);

        foreach (var quotedPhrase in quotedPhrases)
        {
            var normalizedPhrase = NormalizeForLexicalSignal(quotedPhrase);
            if (string.IsNullOrWhiteSpace(normalizedPhrase))
                continue;

            var exactPhraseIndex = normalizedText.IndexOf(normalizedPhrase, StringComparison.Ordinal);
            if (LooksLikeStandaloneLeadTitlePlacement(text, normalizedText, normalizedPhrase, exactPhraseIndex)
                || HasLocalTitleTokenFooterTitleEvidence(
                    normalizedText,
                    exactPhraseIndex,
                    normalizedPhrase.Length))
            {
                return true;
            }

            var compactPhrase = normalizedPhrase.Replace(" ", string.Empty);
            if (compactPhrase.Length >= 8
                && compactLeadingText.Contains(compactPhrase, StringComparison.Ordinal)
                && HasLeadTitleCue(leadingText, normalizedPhrase))
            {
                return true;
            }

            if (ComputeQuotedPhrasePlacementBonus(quotedPhrase, text ?? string.Empty) >= 6.0)
                return true;

            var titleTokens = normalizedPhrase
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(IsQuotedLookupSignalToken)
                .Distinct(StringComparer.Ordinal)
                .Take(8)
                .ToArray();
            if (titleTokens.Length < 2)
                continue;

            var firstTokenIndex = leadingText.IndexOf(titleTokens[0], StringComparison.Ordinal);
            if (firstTokenIndex is < 0 or > 80)
                continue;
            if (!HasLeadTitleCue(leadingText, normalizedPhrase, firstTokenIndex)
                && !HasUppercaseLeadTitleTokenCoverage(text, titleTokens))
                continue;

            if (ContainsOrderedTitleTokenSubstringsInNormalizedText(leadingText, titleTokens, maxGapChars: 32)
                || ContainsTitleLikeLexicalSequence(leadingText, titleTokens))
            {
                return true;
            }
        }

        return false;
    }


    internal static bool HasLeadTitleCue(string leadingText, string normalizedPhrase, int? firstTokenIndex = null)
    {
        if (string.IsNullOrWhiteSpace(leadingText) || string.IsNullOrWhiteSpace(normalizedPhrase))
            return false;

        var titleTokens = normalizedPhrase
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsQuotedLookupSignalToken)
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        if (titleTokens.Length == 0)
            return false;

        var titleStart = firstTokenIndex ?? leadingText.IndexOf(titleTokens[0], StringComparison.Ordinal);
        if (titleStart <= 8)
            return titleStart >= 0;

        var before = leadingText[..Math.Min(titleStart, leadingText.Length)];
        if (before.Contains('\n') || before.Contains('\r'))
            return true;

        var paddedBefore = $" {before} ";
        return ContainsAny(
            paddedBefore,
            " entry ",
            " item ",
            " fiche ",
            " titre ",
            " title ",
            " > ");
    }


    internal static bool HasUppercaseLeadTitleTokenCoverage(
        string? rawText,
        IReadOnlyList<string> titleTokens)
    {
        if (string.IsNullOrWhiteSpace(rawText) || titleTokens.Count < 2)
            return false;

        var normalizedTitleTokens = titleTokens
            .Select(NormalizeForLexicalSignal)
            .Where(static token => !string.IsNullOrWhiteSpace(token))
            .Select(static token => token.Replace(" ", string.Empty))
            .Take(8)
            .ToArray();
        if (normalizedTitleTokens.Length < 2)
            return false;

        var leadingRawText = rawText.Length <= 220 ? rawText : rawText[..220];
        var rawTokens = Regex.Matches(
            leadingRawText,
            @"[\p{L}\p{N}]+",
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        var matchedTitleTokenCount = 0;

        foreach (Match rawTokenMatch in rawTokens)
        {
            var rawToken = rawTokenMatch.Value;
            var letterCount = rawToken.Count(char.IsLetter);
            var uppercaseLetterCount = rawToken.Count(char.IsUpper);
            if (letterCount < 3 || uppercaseLetterCount < Math.Max(2, letterCount - 1))
                continue;

            var remaining = NormalizeForLexicalSignal(rawToken).Replace(" ", string.Empty);
            var candidateMatchedCount = 0;
            while (matchedTitleTokenCount + candidateMatchedCount < normalizedTitleTokens.Length)
            {
                var expectedToken = normalizedTitleTokens[matchedTitleTokenCount + candidateMatchedCount];
                if (!remaining.StartsWith(expectedToken, StringComparison.Ordinal))
                    break;

                remaining = remaining[expectedToken.Length..];
                candidateMatchedCount++;
                if (remaining.Length == 0 || remaining.All(char.IsDigit))
                    break;

                var joinedConnector = TitleConnectorTokens
                    .Where(static connector => connector.Length <= 4)
                    .OrderByDescending(static connector => connector.Length)
                    .FirstOrDefault(connector => remaining.StartsWith(connector, StringComparison.Ordinal));
                if (string.IsNullOrWhiteSpace(joinedConnector))
                    continue;

                remaining = remaining[joinedConnector.Length..];
                if (remaining.Length == 0 || remaining.All(char.IsDigit))
                    break;
            }

            if (candidateMatchedCount == 0
                || (remaining.Length > 0 && !remaining.All(char.IsDigit)))
            {
                continue;
            }

            matchedTitleTokenCount += candidateMatchedCount;
            if (matchedTitleTokenCount == normalizedTitleTokens.Length)
                return true;
        }

        return false;
    }


    internal static bool HasQuotedTitlePlacementEvidence(IReadOnlyList<string> quotedPhrases, RagMatch match)
    {
        if (quotedPhrases.Count == 0)
            return false;

        var titleSignal = string.Join(' ', new[] { match.SectionTitle, match.HeadingPath }
            .Where(static value => !string.IsNullOrWhiteSpace(value)));
        if (ComputeQuotedLookupCandidateScore(quotedPhrases, titleSignal) > 0.0)
            return true;

        if (match.MatchedContentCards is { Count: > 0 })
        {
            foreach (var card in match.MatchedContentCards.Take(MaxCalibrationCardCount))
            {
                if (HasQuotedCardTitlePlacementEvidence(quotedPhrases, match, card))
                    return true;
            }
        }

        if (IsWeakResolvedRouteTarget(match))
            return false;

        if (string.Equals(ResolveRetriever(match), "exact_match", StringComparison.Ordinal))
            return HasStandaloneQuotedTitlePlacementEvidence(quotedPhrases, match.Text);

        return HasLocalTitleTokenLeadEvidence(match.Text, match.SectionTitle, match.HeadingPath, quotedPhrases)
            || HasFuzzyQuotedTitleLeadPlacementEvidence(quotedPhrases, NormalizeForLexicalSignal(match.Text));
    }


    private static bool HasQuotedCardTitlePlacementEvidence(
        IReadOnlyList<string> quotedPhrases,
        RagMatch match,
        RagMatchedContentCard card)
    {
        if (ComputeQuotedLookupCandidateScore(quotedPhrases, card.Title) <= 0.0)
            return false;
        if (IsDocumentProfileMatch(match))
            return true;

        return HasLocalTitleTokenLeadEvidence(match.Text, match.SectionTitle, match.HeadingPath, quotedPhrases);
    }


    internal static bool HasStandaloneQuotedTitlePlacementEvidence(
        IReadOnlyList<string> quotedPhrases,
        string? text)
    {
        var normalizedText = NormalizeForLexicalSignal(text);
        if (string.IsNullOrWhiteSpace(normalizedText))
            return false;

        foreach (var quotedPhrase in quotedPhrases)
        {
            var normalizedPhrase = NormalizeForLexicalSignal(quotedPhrase);
            if (string.IsNullOrWhiteSpace(normalizedPhrase))
                continue;

            var exactPhraseIndex = normalizedText.IndexOf(normalizedPhrase, StringComparison.Ordinal);
            if (LooksLikeStandaloneLeadTitlePlacement(text, normalizedText, normalizedPhrase, exactPhraseIndex)
                || HasLocalTitleTokenFooterTitleEvidence(normalizedText, exactPhraseIndex, normalizedPhrase.Length)
                || HasFuzzyQuotedTitleLeadPlacementEvidence(quotedPhrases, normalizedText))
            {
                return true;
            }
        }

        return false;
    }


    private static bool HasFuzzyQuotedTitleLeadPlacementEvidence(
        IReadOnlyList<string> quotedPhrases,
        string normalizedText)
        => ComputePreciseQuotedLeadTitleSpecificityPriority(quotedPhrases, normalizedText) >= 2;


    internal static bool HasLocalTitleTokenFooterTitleEvidence(
        string normalizedText,
        int exactPhraseIndex,
        int phraseLength)
    {
        if (exactPhraseIndex < 0 || phraseLength <= 0 || normalizedText.Length < 320)
            return false;

        var prefixStart = Math.Max(0, exactPhraseIndex - 96);
        var prefix = normalizedText[prefixStart..exactPhraseIndex];
        var suffixStart = Math.Min(normalizedText.Length, exactPhraseIndex + phraseLength);
        var suffixLength = Math.Min(120, normalizedText.Length - suffixStart);
        var suffix = normalizedText.Substring(suffixStart, suffixLength);
        var context = $" {prefix} {suffix} ";

        var trailingThreshold = Math.Max(240, (int)Math.Round(normalizedText.Length * 0.75, MidpointRounding.AwayFromZero));
        if (exactPhraseIndex < trailingThreshold)
        {
            var duplicatedOcrFooterThreshold = Math.Max(240, (int)Math.Round(normalizedText.Length * 0.55, MidpointRounding.AwayFromZero));
            if (exactPhraseIndex < duplicatedOcrFooterThreshold
                || (!context.Contains(" copyright ", StringComparison.Ordinal)
                    && !System.Text.RegularExpressions.Regex.IsMatch(
                        context,
                        @"(?:^|\s)(?:19|20)\d{2}(?:\s|$)",
                        System.Text.RegularExpressions.RegexOptions.CultureInvariant)))
            {
                return false;
            }
        }

        return context.Contains(" page ", StringComparison.Ordinal)
            || context.Contains(" copyright ", StringComparison.Ordinal)
            || System.Text.RegularExpressions.Regex.IsMatch(
                context,
                @"(?:^|\s)(?:19|20)\d{2}(?:\s|$)",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            || LooksLikeTrailingPageMarkerAfterFooterTitle(suffix);
    }


    private static bool LooksLikeTrailingPageMarkerAfterFooterTitle(string suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix))
            return false;

        var trimmed = suffix.TrimStart();
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                trimmed,
                @"^\d{1,4}(?:\s|$)",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            return false;
        }

        return !System.Text.RegularExpressions.Regex.IsMatch(
            trimmed,
            @"^\d{1,4}\s+(?:g|gr|kg|mg|ml|cl|l|oz|lb|c|min|minute|minutes|h|heure|heures|personne|personnes|portion|portions)\b",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }


    internal static async Task<List<RagMatch>> SearchLocalTitleTokenRouteMatchesAsync(
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
        var quotedTerms = ExtractTitleLookupPhrases(query);
        if (quotedTerms.Count == 0 || topK <= 0)
            return [];

        var titleTokens = BuildTitleLookupSqlTokens(quotedTerms);
        if (titleTokens.Count == 0)
            return [];

        var normalizedDocPath = string.IsNullOrWhiteSpace(docPath)
            ? null
            : docPath.Trim().Replace('\\', '/').TrimStart('/');
        var normalizedCategoryPath = NormalizeRagCategoryPathForSql(categoryPath);
        Guid? normalizedDocId = Guid.TryParse(docId, out var parsedDocId) ? parsedDocId : null;
        var hasDocScope = normalizedDocId.HasValue || !string.IsNullOrWhiteSpace(normalizedDocPath);
        var hasCategoryScope = !string.IsNullOrWhiteSpace(normalizedCategoryPath) || !string.IsNullOrWhiteSpace(category);
        var candidateLimit = ResolveLocalTitleTokenSqlCandidateLimit(
            topK,
            hasDocScope,
            hasCategoryScope,
            fullFallback: false);
        var fullCandidateLimit = ResolveLocalTitleTokenSqlCandidateLimit(
            topK,
            hasDocScope,
            hasCategoryScope,
            fullFallback: true);

        await using var conn = await ds.OpenConnectionAsync(ct);
        var rows = await SearchLocalTitleTokenCoverageMatchesAsync(
            conn,
            tenantId,
            titleTokens,
            category,
            normalizedDocId,
            normalizedDocPath,
            normalizedCategoryPath,
            candidateLimit,
            ct,
            degradedRetrieverRef);
        var matches = RankLocalTitleTokenRouteRows(rows, quotedTerms, topK);

        if (ShouldRetryLocalTitleTokenSqlCoverage(
                rows.Count,
                candidateLimit,
                fullCandidateLimit,
                matches.Count,
                topK,
                hasCategoryScope))
        {
            rows = await SearchLocalTitleTokenCoverageMatchesAsync(
                conn,
                tenantId,
                titleTokens,
                category,
                normalizedDocId,
                normalizedDocPath,
                normalizedCategoryPath,
                fullCandidateLimit,
                ct,
                degradedRetrieverRef);
            matches = RankLocalTitleTokenRouteRows(rows, quotedTerms, topK);
        }

        return matches;
    }


    internal static async Task<List<RagMatch>> SearchExplicitDocumentTitleMatchesAsync(
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
        var explicitTerms = ExtractExplicitFileLookupPhrases(query);
        if (explicitTerms.Count == 0 || topK <= 0)
            return [];

        var normalizedDocPath = string.IsNullOrWhiteSpace(docPath)
            ? null
            : docPath.Trim().Replace('\\', '/').TrimStart('/');
        var normalizedCategoryPath = NormalizeRagCategoryPathForSql(categoryPath);
        Guid? normalizedDocId = Guid.TryParse(docId, out var parsedDocId) ? parsedDocId : null;
        var maxChunksPerDoc = Math.Clamp(topK <= 4 ? 2 : 3, 1, 4);
        var candidateLimit = Math.Clamp(topK * 2, topK, 32);

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
WITH explicit_terms AS (
    SELECT DISTINCT
        LOWER(term) AS term,
        replace(LOWER(term), ' ', '') AS compact_term
    FROM unnest(@explicit_terms::text[]) AS term
    WHERE term IS NOT NULL AND length(term) >= 4
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
document_candidates AS (
    SELECT
        d.*,
        doc_match.matched_term,
        doc_match.document_score
    FROM scoped_docs d
    CROSS JOIN LATERAL (
        SELECT
            btrim(regexp_replace(lower(d.doc_path), '[^[:alnum:]]+', ' ', 'g')) AS path_norm,
            replace(btrim(regexp_replace(lower(d.doc_path), '[^[:alnum:]]+', ' ', 'g')), ' ', '') AS path_compact,
            btrim(regexp_replace(lower(regexp_replace(d.doc_path, '^.*/', '')), '[^[:alnum:]]+', ' ', 'g')) AS base_norm,
            replace(btrim(regexp_replace(lower(regexp_replace(d.doc_path, '^.*/', '')), '[^[:alnum:]]+', ' ', 'g')), ' ', '') AS base_compact,
            btrim(regexp_replace(
                btrim(regexp_replace(lower(regexp_replace(d.doc_path, '^.*/', '')), '[^[:alnum:]]+', ' ', 'g')),
                '\s+(pdf|doc|docx|xls|xlsx|ppt|pptx|md|txt|csv)$',
                '',
                'g')) AS base_without_extension,
            btrim(regexp_replace(lower(d.doc_name), '[^[:alnum:]]+', ' ', 'g')) AS name_norm,
            btrim(regexp_replace(
                btrim(regexp_replace(lower(d.doc_name), '[^[:alnum:]]+', ' ', 'g')),
                '\s+(pdf|doc|docx|xls|xlsx|ppt|pptx|md|txt|csv)$',
                '',
                'g')) AS name_without_extension
    ) doc_text
    CROSS JOIN LATERAL (
        SELECT
            MIN(et.term) AS matched_term,
            MAX(CASE
                WHEN doc_text.base_norm = et.term THEN 1.00
                WHEN doc_text.name_norm = et.term THEN 1.00
                WHEN doc_text.base_without_extension = et.term THEN 0.98
                WHEN doc_text.name_without_extension = et.term THEN 0.98
                WHEN doc_text.base_compact = et.compact_term THEN 0.98
                WHEN doc_text.path_norm = et.term THEN 0.96
                WHEN (' ' || doc_text.path_norm || ' ') LIKE ('% ' || et.term || ' %') THEN 0.92
                WHEN length(et.compact_term) >= 10 AND doc_text.path_compact = et.compact_term THEN 0.90
                ELSE 0.0
            END) AS document_score
        FROM explicit_terms et
        WHERE doc_text.base_norm = et.term
           OR doc_text.name_norm = et.term
           OR doc_text.base_without_extension = et.term
           OR doc_text.name_without_extension = et.term
           OR doc_text.base_compact = et.compact_term
           OR doc_text.path_norm = et.term
           OR (' ' || doc_text.path_norm || ' ') LIKE ('% ' || et.term || ' %')
           OR (length(et.compact_term) >= 10 AND doc_text.path_compact = et.compact_term)
    ) doc_match
    WHERE doc_match.document_score > 0.0
),
ranked_chunks AS (
    SELECT
        d.doc_id,
        d.doc_path,
        d.doc_name,
        d.category,
        d.indexed_version,
        d.content_hash,
        d.matched_term,
        d.document_score,
        rc.retrieval_chunk_id,
        rc.chunk_index,
        rc.page_start,
        rc.page_end,
        rc.text_content,
        rc.metadata,
        rc.section_id,
        s.title AS section_title,
        CASE
            WHEN NULLIF(rc.metadata->>'contentDensityScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
                THEN (rc.metadata->>'contentDensityScore')::double precision
            ELSE 0.0
        END AS content_density_score,
        ROW_NUMBER() OVER (
            PARTITION BY d.doc_id
            ORDER BY
                CASE COALESCE(rc.metadata->>'contentRole', 'content')
                    WHEN 'content' THEN 0
                    WHEN 'mixed_navigation_content' THEN 1
                    ELSE 2
                END,
                CASE
                    WHEN rc.page_start BETWEEN 1 AND 3 THEN 0
                    ELSE 1
                END,
                CASE COALESCE(rc.metadata->>'chunkType', '')
                    WHEN 'section_window_v1' THEN 0
                    WHEN 'unit_exact_v1' THEN 1
                    WHEN 'footer_titled_item_window_v1' THEN 2
                    ELSE 3
                END,
                CASE
                    WHEN NULLIF(rc.metadata->>'contentDensityScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
                        THEN (rc.metadata->>'contentDensityScore')::double precision
                    ELSE 0.0
                END DESC,
                rc.chunk_index ASC
        ) AS doc_chunk_rank
    FROM document_candidates d
    JOIN retrieval_chunks rc
      ON rc.tenant_id = d.tenant_id
     AND rc.revision_id = d.revision_id
    LEFT JOIN document_sections s
      ON s.section_id = rc.section_id
    WHERE COALESCE(rc.metadata->>'contentRole', 'content') <> 'navigation'
      AND COALESCE(rc.metadata->>'chunkType', '') <> 'navigation_index_v1'
      AND btrim(COALESCE(rc.text_content, '')) <> ''
)
SELECT
    doc_id AS "DocId",
    doc_path AS "DocPath",
    doc_name AS "DocName",
    category AS "Category",
    page_start AS "PageStart",
    page_end AS "PageEnd",
    CASE
        WHEN NULLIF(metadata->>'offsetStart', '') ~ '^[-+]?[0-9]+$'
            THEN CASE
                WHEN (metadata->>'offsetStart')::numeric BETWEEN -2147483648 AND 2147483647
                    THEN (metadata->>'offsetStart')::int
                ELSE NULL
            END
        ELSE NULL
    END AS "OffsetStart",
    CASE
        WHEN NULLIF(metadata->>'offsetEnd', '') ~ '^[-+]?[0-9]+$'
            THEN CASE
                WHEN (metadata->>'offsetEnd')::numeric BETWEEN -2147483648 AND 2147483647
                    THEN (metadata->>'offsetEnd')::int
                ELSE NULL
            END
        ELSE NULL
    END AS "OffsetEnd",
    retrieval_chunk_id AS "ChunkId",
    chunk_index AS "ChunkIndex",
    COALESCE(text_content, '') AS "Text",
    indexed_version AS "IngestionVersion",
    LOWER(ENCODE(content_hash, 'hex')) AS "HashDoc",
    ('Matched explicit_document_title_route: ' || matched_term || E'\n' || COALESCE(text_content, '')) AS "EmbedText",
    section_id AS "SectionOrdinalPlaceholder",
    COALESCE(metadata->>'sectionTitle', section_title) AS "SectionTitle",
    COALESCE(metadata->>'headingPath', section_title) AS "HeadingPath",
    COALESCE(metadata->>'chunkType', 'contextual_text_v1') AS "ChunkType",
    COALESCE(metadata->>'contentRole', 'content') AS "ContentRole",
    metadata->>'navigationReason' AS "NavigationReason",
    metadata->>'originalChunkType' AS "OriginalChunkType",
    CASE
        WHEN NULLIF(metadata->>'navigationScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
            THEN (metadata->>'navigationScore')::double precision
        ELSE NULL
    END AS "NavigationScore",
    CASE
        WHEN NULLIF(metadata->>'contentDensityScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
            THEN (metadata->>'contentDensityScore')::double precision
        ELSE NULL
    END AS "ContentDensityScore",
    metadata->>'prevChunkId' AS "PrevChunkId",
    metadata->>'nextChunkId' AS "NextChunkId",
    metadata->>'sameSectionChunkId' AS "SameSectionChunkId",
    metadata->>'extractionTextStatus' AS "ExtractionTextStatus",
    CASE
        WHEN LOWER(COALESCE(metadata->>'extractionTextSparse', '')) IN ('true', 'false')
            THEN (metadata->>'extractionTextSparse')::boolean
        ELSE NULL
    END AS "ExtractionTextSparse",
    CASE
        WHEN LOWER(COALESCE(metadata->>'extractionOcrCandidate', '')) IN ('true', 'false')
            THEN (metadata->>'extractionOcrCandidate')::boolean
        ELSE NULL
    END AS "ExtractionOcrCandidate",
    CASE
        WHEN jsonb_typeof(metadata->'extractionQualitySignals') = 'array'
            THEN (metadata->'extractionQualitySignals')::text
        ELSE NULL
    END AS "ExtractionQualitySignalsJson",
    NULL::text AS "MatchedContentCardsJson",
    LEAST(
        1.02,
        0.92
        + (document_score * 0.10)
        + LEAST(0.04, content_density_score * 0.04)
        - ((doc_chunk_rank - 1)::double precision * 0.01)
    )::real AS "SparseRank"
FROM ranked_chunks
WHERE doc_chunk_rank <= @max_chunks_per_doc
ORDER BY
    document_score DESC,
    doc_path ASC,
    doc_chunk_rank ASC
LIMIT @candidate_limit;
""";

        try
        {
            var rows = await conn.QueryAsync<SparseMatchRow>(new CommandDefinition(sql, new
            {
                tenant_id = tenantId,
                explicit_terms = explicitTerms.ToArray(),
                category,
                category_path = normalizedCategoryPath,
                doc_id = normalizedDocId,
                doc_path = normalizedDocPath,
                max_chunks_per_doc = maxChunksPerDoc,
                candidate_limit = candidateLimit
            }, commandTimeout: 12, cancellationToken: ct));

            return rows
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
                    EmbeddingBasis: "explicit_document_title_route_v1",
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
                    ExtractionTextStatus: row.ExtractionTextStatus,
                    ExtractionTextSparse: row.ExtractionTextSparse,
                    ExtractionOcrCandidate: row.ExtractionOcrCandidate,
                    ExtractionQualitySignals: ParseExtractionQualitySignalsJson(row.ExtractionQualitySignalsJson),
                    ExtractionQualityObservation: ObserveExtractionQualitySignalsJson(row.ExtractionQualitySignalsJson)))
                .ToList();
        }
        catch (PostgresException ex)
        {
            RetrievalTelemetry.RecordRetrieverDegraded("explicit_document_title_route", ex);
            degradedRetrieverRef?.Invoke("explicit_document_title_route", FormatPostgresRetrieverError(ex));
            return [];
        }
        catch (Exception ex) when (IsRetrieverDatabaseTimeout(ex))
        {
            RetrievalTelemetry.RecordRetrieverDegraded("explicit_document_title_route", ex);
            degradedRetrieverRef?.Invoke("explicit_document_title_route", FormatRetrieverError(ex));
            return [];
        }
    }


    private static List<RagMatch> RankLocalTitleTokenRouteRows(
        IReadOnlyList<SparseMatchRow> rows,
        IReadOnlyList<string> quotedTerms,
        int topK)
    {
        return rows
            .GroupBy(static row => row.ChunkId)
            .Select(static group => group
                .OrderByDescending(static row => row.SparseRank)
                .ThenBy(static row => row.ChunkIndex)
                .First())
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
            .Where(item => HasLocalTitleTokenLeadEvidence(item.Row, quotedTerms))
            .Where(static item => ShouldKeepLocalTitleTokenCandidate(item.Row, item.QuotedScore))
            .OrderByDescending(static item => item.QuotedScore)
            .ThenBy(static item => PreciseTitleChunkTypePriority(item.Row.ChunkType))
            .ThenByDescending(static item => item.Row.SparseRank)
            .ThenBy(static item => item.Row.DocPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Row.ChunkIndex)
            .Take(topK)
            .Select(item =>
            {
                var row = item.Row;
                var promoteNavigationRow = ShouldPromoteLocalTitleTokenNavigationCandidate(row, item.QuotedScore);
                return new RagMatch(
                    Score: Math.Clamp(0.90 + Math.Min(0.12, item.QuotedScore * 0.01), 0.0, 1.02),
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
                    EmbeddingBasis: "local_title_token_route_v1",
                    SectionOrdinal: null,
                    UnitOrdinal: null,
                    SectionTitle: row.SectionTitle,
                    HeadingPath: row.HeadingPath,
                    ChunkType: promoteNavigationRow ? "section_window_v1" : row.ChunkType,
                    ContentRole: promoteNavigationRow ? RetrievalContentClassifier.MixedNavigationContentRole : row.ContentRole,
                    NavigationReason: row.NavigationReason,
                    OriginalChunkType: row.OriginalChunkType,
                    NavigationScore: row.NavigationScore,
                    ContentDensityScore: row.ContentDensityScore,
                    PrevChunkId: row.PrevChunkId,
                    NextChunkId: row.NextChunkId,
                    SameSectionChunkId: row.SameSectionChunkId,
                    MatchedContentCards: null,
                    ExtractionTextStatus: row.ExtractionTextStatus,
                    ExtractionTextSparse: row.ExtractionTextSparse,
                    ExtractionOcrCandidate: row.ExtractionOcrCandidate,
                    ExtractionQualitySignals: ParseExtractionQualitySignalsJson(row.ExtractionQualitySignalsJson),
                    ExtractionQualityObservation: ObserveExtractionQualitySignalsJson(row.ExtractionQualitySignalsJson));
            })
            .ToList();
    }


    private static int PreciseTitleChunkTypePriority(string? chunkType)
        => chunkType switch
        {
            "footer_titled_item_window_v1" => 0,
            "section_window_v1" => 1,
            "unit_exact_v1" => 2,
            _ => 3
        };


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
        Action<string, string?>? degradedRetrieverRef = null,
        bool allowDirectChunkRoute = true,
        bool allowNavigationCatalogRoute = false)
    {
        if (topK <= 0)
            return [];

        var focusedPhrases = ExtractStandardReferenceTitleLookupPhrases(query)
            .Concat(ExtractComparativeLookupPhrases(query))
            .Concat(ExtractTitleLookupPhrases(query))
            .Concat(ExtractFocusedLookupPhrases(query))
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
        var directChunkRouteEnabled = allowDirectChunkRoute
            && hasExplicitFocusedLookupPhrase
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
        var scopedCatalogRouteEnabled = allowNavigationCatalogRoute
            && (!string.IsNullOrWhiteSpace(category)
                || !string.IsNullOrWhiteSpace(normalizedCategoryPath)
                || normalizedDocId.HasValue
                || !string.IsNullOrWhiteSpace(normalizedDocPath));
        var navigationCatalogLimit = ResolveTitleAnchorNavigationCatalogLimit(topK);

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
        d.file_size,
        d.file_mtime,
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
        d.file_size,
        d.file_mtime,
        d.revision_id,
        a.title AS route_title,
        CASE
            WHEN a.source_kind IN ('section', 'content_card')
             AND COALESCE(phrase_match.exact_phrase_hit, 0) = 1
             AND a.retrieval_chunk_id IS NULL
             AND a.page_start IS NOT NULL
             AND target.page_start <= COALESCE(a.page_end, a.page_start)
             AND target.page_end >= a.page_start
                THEN 'title_anchor_exact_page_route'
            ELSE 'title_anchor_route'
        END::text AS route_source,
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
     AND (
            EXISTS (
                SELECT 1
                FROM unnest(@query_tokens::text[]) AS token
                WHERE token = ANY(a.title_tokens)
            )
         OR EXISTS (
                SELECT 1
                FROM query_phrases qp
                WHERE replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(
                          a.normalized_title,
                          'Å“', 'oe'), 'Å’', 'oe'), 'Ã¦', 'ae'), 'Ã†', 'ae'), 'ÃŸ', 'ss'), 'Ã¸', 'o'), 'Ã˜', 'o'), 'Å‚', 'l'), 'Å', 'l'), 'Ä‘', 'd'), 'Ä', 'd') = qp.phrase
                   OR replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(
                          a.normalized_title,
                          'Å“', 'oe'), 'Å’', 'oe'), 'Ã¦', 'ae'), 'Ã†', 'ae'), 'ÃŸ', 'ss'), 'Ã¸', 'o'), 'Ã˜', 'o'), 'Å‚', 'l'), 'Å', 'l'), 'Ä‘', 'd'), 'Ä', 'd') LIKE '%' || qp.phrase || '%'
                   OR qp.phrase LIKE '%' || replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(
                          a.normalized_title,
                          'Å“', 'oe'), 'Å’', 'oe'), 'Ã¦', 'ae'), 'Ã†', 'ae'), 'ÃŸ', 'ss'), 'Ã¸', 'o'), 'Ã˜', 'o'), 'Å‚', 'l'), 'Å', 'l'), 'Ä‘', 'd'), 'Ä', 'd') || '%'
                   OR similarity(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(
                          a.normalized_title,
                          'Å“', 'oe'), 'Å’', 'oe'), 'Ã¦', 'ae'), 'Ã†', 'ae'), 'ÃŸ', 'ss'), 'Ã¸', 'o'), 'Ã˜', 'o'), 'Å‚', 'l'), 'Å', 'l'), 'Ä‘', 'd'), 'Ä', 'd'), qp.phrase) >= @fuzzy_title_similarity_min
            )
      )
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
        SELECT
            MAX(CASE
                WHEN title_search.normalized_title = qp.phrase THEN 0.34
                WHEN title_search.normalized_title LIKE '%' || qp.phrase || '%' THEN 0.24
                WHEN qp.phrase LIKE '%' || title_search.normalized_title || '%' THEN 0.20
                WHEN similarity(title_search.normalized_title, qp.phrase) >= @fuzzy_title_similarity_min THEN 0.26
                ELSE 0.0
            END) AS phrase_score,
            MAX(CASE WHEN title_search.normalized_title = qp.phrase THEN 1 ELSE 0 END)::int AS exact_phrase_hit
        FROM query_phrases qp
        WHERE title_search.normalized_title = qp.phrase
           OR title_search.normalized_title LIKE '%' || qp.phrase || '%'
           OR qp.phrase LIKE '%' || title_search.normalized_title || '%'
           OR similarity(title_search.normalized_title, qp.phrase) >= @fuzzy_title_similarity_min
    ) phrase_match
    JOIN LATERAL (
        SELECT rc.retrieval_chunk_id, rc.page_start, rc.page_end
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
                    WHEN NULLIF(rc.metadata->>'contentDensityScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
                        THEN (rc.metadata->>'contentDensityScore')::double precision
                    ELSE 0.0
                END AS content_density_score,
                length(replace(replace(replace(replace(replace(replace(
                    lower(translate(
                        COALESCE(rc.text_content, '') || ' ' || COALESCE(rc.metadata->>'sectionTitle', '') || ' ' || COALESCE(rc.metadata->>'headingPath', ''),
                        'ÀÁÂÃÄÅàáâãäåÇçÈÉÊËèéêëÌÍÎÏìíîïÑñÒÓÔÕÖØòóôõöøÙÚÛÜùúûüÝýÿ',
                        'AAAAAAaaaaaaCcEEEEeeeeIIIIiiiiNnOOOOOOooooooUUUUuuuuYyy'
                    )),
                    'œ', 'oe'), 'æ', 'ae'), 'ß', 'ss'), 'ø', 'o'), 'ł', 'l'), 'đ', 'd')) AS searchable_text_length,
                COALESCE(array_length(regexp_split_to_array(btrim(COALESCE(rc.text_content, '')), '\s+'), 1), 0) AS token_count
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
                WHEN a.source_kind IN ('section', 'content_card')
                 AND a.page_start IS NOT NULL
                 AND rc.page_start <= COALESCE(a.page_end, a.page_start)
                 AND rc.page_end >= a.page_start
                 AND candidate_title_match.full_title_hit = 1
                 AND (
                        candidate_title_match.full_title_position <= 0
                     OR candidate_title_match.full_title_position < GREATEST(96, (candidate_stats.searchable_text_length * 0.65)::int)
                 )
                    THEN 1
                WHEN a.source_kind IN ('section', 'content_card')
                 AND a.page_start IS NOT NULL
                 AND rc.page_start <= COALESCE(a.page_end, a.page_start)
                 AND rc.page_end >= a.page_start
                 AND candidate_title_match.title_token_hits >= CASE
                        WHEN COALESCE(array_length(a.title_tokens, 1), 0) <= 2 THEN COALESCE(array_length(a.title_tokens, 1), 0)
                        ELSE 2
                    END
                 AND (
                        candidate_title_match.full_title_position <= 0
                     OR candidate_title_match.full_title_position < GREATEST(96, (candidate_stats.searchable_text_length * 0.65)::int)
                 )
                    THEN 2
                WHEN a.page_start IS NOT NULL
                 AND rc.page_start BETWEEN a.page_start + 1 AND COALESCE(a.page_end, a.page_start) + 2
                 AND COALESCE(rc.metadata->>'chunkType', '') IN ('section_window_v1', 'unit_exact_v1', 'footer_titled_item_window_v1')
                 AND candidate_stats.content_density_score >= 0.50
                 AND candidate_stats.token_count >= 24
                    THEN 3
                WHEN a.source_kind = 'content_card'
                 AND a.page_start IS NOT NULL
                 AND rc.page_start <= COALESCE(a.page_end, a.page_start)
                 AND rc.page_end >= a.page_start
                 AND candidate_title_match.title_token_hits >= 1
                 AND (
                        candidate_title_match.full_title_position <= 0
                     OR candidate_title_match.full_title_position < GREATEST(80, (candidate_stats.searchable_text_length * 0.65)::int)
                 )
                    THEN 4
                WHEN a.source_kind = 'content_card'
                 AND candidate_title_match.full_title_hit = 1
                    THEN 5
                WHEN a.source_kind = 'content_card'
                 AND a.page_start IS NOT NULL
                 AND rc.page_start BETWEEN a.page_start AND COALESCE(a.page_end, a.page_start) + 1
                 AND candidate_title_match.title_token_hits >= 2
                 AND (
                        candidate_title_match.full_title_position <= 0
                     OR candidate_title_match.full_title_position < GREATEST(80, (candidate_stats.searchable_text_length * 0.65)::int)
                 )
                    THEN 6
                WHEN a.source_kind = 'content_card'
                 AND a.page_start IS NOT NULL
                 AND rc.page_start BETWEEN a.page_start + 1 AND COALESCE(a.page_end, a.page_start) + 1
                 AND COALESCE(rc.metadata->>'chunkType', '') IN ('section_window_v1', 'unit_exact_v1', 'footer_titled_item_window_v1')
                    THEN 7
                ELSE 7
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
        d.file_size,
        d.file_mtime,
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
     AND (
            EXISTS (
                SELECT 1
                FROM unnest(@query_tokens::text[]) AS token
                WHERE token = ANY(ne.label_tokens)
            )
         OR EXISTS (
                SELECT 1
                FROM query_phrases qp
                WHERE replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(
                          ne.normalized_label,
                          'Å“', 'oe'), 'Å’', 'oe'), 'Ã¦', 'ae'), 'Ã†', 'ae'), 'ÃŸ', 'ss'), 'Ã¸', 'o'), 'Ã˜', 'o'), 'Å‚', 'l'), 'Å', 'l'), 'Ä‘', 'd'), 'Ä', 'd') = qp.phrase
                   OR replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(
                          ne.normalized_label,
                          'Å“', 'oe'), 'Å’', 'oe'), 'Ã¦', 'ae'), 'Ã†', 'ae'), 'ÃŸ', 'ss'), 'Ã¸', 'o'), 'Ã˜', 'o'), 'Å‚', 'l'), 'Å', 'l'), 'Ä‘', 'd'), 'Ä', 'd') LIKE '%' || qp.phrase || '%'
                   OR qp.phrase LIKE '%' || replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(
                          ne.normalized_label,
                          'Å“', 'oe'), 'Å’', 'oe'), 'Ã¦', 'ae'), 'Ã†', 'ae'), 'ÃŸ', 'ss'), 'Ã¸', 'o'), 'Ã˜', 'o'), 'Å‚', 'l'), 'Å', 'l'), 'Ä‘', 'd'), 'Ä', 'd') || '%'
                   OR similarity(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(
                          ne.normalized_label,
                          'Å“', 'oe'), 'Å’', 'oe'), 'Ã¦', 'ae'), 'Ã†', 'ae'), 'ÃŸ', 'ss'), 'Ã¸', 'o'), 'Ã˜', 'o'), 'Å‚', 'l'), 'Å', 'l'), 'Ä‘', 'd'), 'Ä', 'd'), qp.phrase) >= @fuzzy_title_similarity_min
            )
      )
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
                WHEN NULLIF(rc.metadata->>'contentDensityScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
                    THEN (rc.metadata->>'contentDensityScore')::double precision
                ELSE 0.0
            END DESC,
            rc.chunk_index ASC
        LIMIT 1
    ) target ON TRUE
    WHERE COALESCE(phrase_match.phrase_score, 0.0) > 0.0
       OR token_match.overlap_count >= @min_overlap
),
navigation_catalog_routes AS (
    SELECT
        d.doc_id,
        d.doc_path,
        d.doc_name,
        d.category,
        d.indexed_version,
        d.content_hash,
        d.file_size,
        d.file_mtime,
        d.revision_id,
        ne.label AS route_title,
        'navigation_catalog_route'::text AS route_source,
        target.retrieval_chunk_id AS route_chunk_id,
        LEAST(
            0.88,
            0.48
            + LEAST(0.20, COALESCE(ne.confidence, 0.0) * 0.20)
            + LEAST(0.12, target.content_density_score * 0.12)
            + CASE WHEN ne.target_chunk_id IS NOT NULL THEN 0.04 ELSE 0.0 END
        )::real AS route_rank
    FROM scoped_docs d
    JOIN document_navigation_entries ne
      ON ne.tenant_id = d.tenant_id
     AND ne.revision_id = d.revision_id
     AND ne.confidence >= @navigation_route_min_confidence
     AND ne.resolution_method <> 'page_unresolved'
     AND (ne.target_chunk_id IS NOT NULL OR ne.target_anchor_id IS NOT NULL OR ne.target_page_start IS NOT NULL)
     AND length(COALESCE(ne.label, '')) >= 4
    JOIN LATERAL (
        SELECT
            rc.retrieval_chunk_id,
            CASE
                WHEN NULLIF(rc.metadata->>'contentDensityScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
                    THEN (rc.metadata->>'contentDensityScore')::double precision
                ELSE 0.0
            END AS content_density_score,
            COALESCE(array_length(regexp_split_to_array(btrim(COALESCE(rc.text_content, '')), '\s+'), 1), 0) AS token_count
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
          AND COALESCE(rc.metadata->>'chunkType', '') <> 'navigation_index_v1'
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
                WHEN NULLIF(rc.metadata->>'contentDensityScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
                    THEN (rc.metadata->>'contentDensityScore')::double precision
                ELSE 0.0
            END DESC,
            COALESCE(array_length(regexp_split_to_array(btrim(COALESCE(rc.text_content, '')), '\s+'), 1), 0) DESC,
            rc.chunk_index ASC
        LIMIT 1
    ) target ON TRUE
    WHERE @navigation_catalog_route_enabled
    ORDER BY route_rank DESC, d.doc_path ASC, ne.entry_index ASC
    LIMIT @navigation_catalog_limit
),
direct_chunk_routes AS (
    SELECT
        d.doc_id,
        d.doc_path,
        d.doc_name,
        d.category,
        d.indexed_version,
        d.content_hash,
        d.file_size,
        d.file_mtime,
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
                WHEN COALESCE(rc.metadata->>'chunkType', '') = 'footer_titled_item_window_v1' THEN 0.06
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
                left(COALESCE(rc.text_content, ''), 720)
                || ' ' || right(COALESCE(rc.text_content, ''), 480)
                || ' ' || COALESCE(rc.metadata->>'sectionTitle', '')
                || ' ' || COALESCE(rc.metadata->>'headingPath', ''),
                'ÀÁÂÃÄÅàáâãäåÇçÈÉÊËèéêëÌÍÎÏìíîïÑñÒÓÔÕÖØòóôõöøÙÚÛÜùúûüÝýÿ',
                'AAAAAAaaaaaaCcEEEEeeeeIIIIiiiiNnOOOOOOooooooUUUUuuuuYyy'
            )) AS searchable_text,
            ' ' || regexp_replace(lower(translate(
                left(COALESCE(rc.text_content, ''), 720)
                || ' ' || right(COALESCE(rc.text_content, ''), 480)
                || ' ' || COALESCE(rc.metadata->>'sectionTitle', '')
                || ' ' || COALESCE(rc.metadata->>'headingPath', ''),
                'ÀÁÂÃÄÅàáâãäåÇçÈÉÊËèéêëÌÍÎÏìíîïÑñÒÓÔÕÖØòóôõöøÙÚÛÜùúûüÝýÿ',
                'AAAAAAaaaaaaCcEEEEeeeeIIIIiiiiNnOOOOOOooooooUUUUuuuuYyy'
            )), '[^[:alnum:]]+', ' ', 'g') || ' ' AS searchable_words,
            CASE
                WHEN NULLIF(rc.metadata->>'contentDensityScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
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
                left(COALESCE(rc.text_content, ''), 720)
                || ' ' || right(COALESCE(rc.text_content, ''), 480),
                'ÀÁÂÃÄÅàáâãäåÇçÈÉÊËèéêëÌÍÎÏìíîïÑñÒÓÔÕÖØòóôõöøÙÚÛÜùúûüÝýÿ',
                'AAAAAAaaaaaaCcEEEEeeeeIIIIiiiiNnOOOOOOooooooUUUUuuuuYyy'
            )) LIKE ('%' || token || '%')
      )
      AND direct_match.overlap_count >= @direct_min_overlap
      AND COALESCE(rc.metadata->>'contentRole', 'content') <> 'navigation'
      AND COALESCE(rc.metadata->>'chunkType', '') <> 'navigation_index_v1'
      AND (
            NULLIF(rc.metadata->>'navigationScore', '') IS NULL
         OR NULLIF(rc.metadata->>'navigationScore', '') !~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
         OR (rc.metadata->>'navigationScore')::double precision < 0.82
         OR direct_stats.content_density_score >= 0.50
      )
      AND (
            direct_stats.content_density_score >= 0.25
         OR COALESCE(rc.metadata->>'chunkType', '') IN ('section_window_v1', 'unit_exact_v1', 'footer_titled_item_window_v1')
      )
),
routes AS (
    SELECT * FROM anchor_routes
    UNION ALL
    SELECT * FROM navigation_routes
    UNION ALL
    SELECT * FROM navigation_catalog_routes
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
                CASE WHEN routes.route_source IN ('title_anchor_route', 'title_anchor_exact_page_route') THEN 0 ELSE 1 END,
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
        WHEN NULLIF(rc.metadata->>'navigationScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
            THEN (rc.metadata->>'navigationScore')::double precision
        ELSE NULL
    END AS "NavigationScore",
    CASE
        WHEN NULLIF(rc.metadata->>'contentDensityScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
            THEN (rc.metadata->>'contentDensityScore')::double precision
        ELSE NULL
    END AS "ContentDensityScore",
    rc.metadata->>'prevChunkId' AS "PrevChunkId",
    rc.metadata->>'nextChunkId' AS "NextChunkId",
    rc.metadata->>'sameSectionChunkId' AS "SameSectionChunkId",
    rc.metadata->>'extractionTextStatus' AS "ExtractionTextStatus",
    CASE
        WHEN LOWER(COALESCE(rc.metadata->>'extractionTextSparse', '')) IN ('true', 'false')
            THEN (rc.metadata->>'extractionTextSparse')::boolean
        ELSE NULL
    END AS "ExtractionTextSparse",
    CASE
        WHEN LOWER(COALESCE(rc.metadata->>'extractionOcrCandidate', '')) IN ('true', 'false')
            THEN (rc.metadata->>'extractionOcrCandidate')::boolean
        ELSE NULL
    END AS "ExtractionOcrCandidate",
    CASE
        WHEN jsonb_typeof(rc.metadata->'extractionQualitySignals') = 'array'
            THEN (rc.metadata->'extractionQualitySignals')::text
        ELSE NULL
    END AS "ExtractionQualitySignalsJson",
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
                navigation_catalog_route_enabled = scopedCatalogRouteEnabled,
                navigation_catalog_limit = navigationCatalogLimit,
                fuzzy_title_similarity_min = FuzzyTitleSimilarityMinimum,
                direct_chunk_route_enabled = directChunkRouteEnabled,
                category,
                category_path = normalizedCategoryPath,
                doc_id = normalizedDocId,
                doc_path = normalizedDocPath,
                candidate_limit = candidateLimit
            }, commandTimeout: 12, cancellationToken: ct));

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
                    EmbeddingBasis: row.EmbedText.StartsWith("Matched navigation_catalog_route", StringComparison.Ordinal)
                        ? "navigation_catalog_route_v1"
                        : row.EmbedText.StartsWith("Matched navigation_route", StringComparison.Ordinal)
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
                    SameSectionChunkId: row.SameSectionChunkId,
                    ExtractionTextStatus: row.ExtractionTextStatus,
                    ExtractionTextSparse: row.ExtractionTextSparse,
                    ExtractionOcrCandidate: row.ExtractionOcrCandidate,
                    ExtractionQualitySignals: ParseExtractionQualitySignalsJson(row.ExtractionQualitySignalsJson),
                    ExtractionQualityObservation: ObserveExtractionQualitySignalsJson(row.ExtractionQualitySignalsJson)))
                .Where(static match => !IsNavigationRouteMatch(match) || NavigationRouteHasTargetTitleEvidence(match))
                .Where(static match => !IsTitleAnchorRouteMatch(match) || TitleAnchorRouteHasTargetTitleEvidence(match))
                .ToList();

            var exactPageAnchorsByDoc = matches
                .Where(IsExactPageTitleAnchorRouteMatch)
                .GroupBy(static match => match.DocId ?? string.Empty, StringComparer.Ordinal)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.ToArray(),
                    StringComparer.Ordinal);
            if (exactPageAnchorsByDoc.Count > 0)
            {
                matches.RemoveAll(match =>
                {
                    if (!IsTitleAnchorRouteMatch(match)
                        || IsExactPageTitleAnchorRouteMatch(match)
                        || !exactPageAnchorsByDoc.TryGetValue(match.DocId ?? string.Empty, out var exactPageAnchors))
                    {
                        return false;
                    }

                    return !exactPageAnchors.Any(exactPageAnchor => PageRangesOverlap(
                        match.PageStart,
                        match.PageEnd,
                        exactPageAnchor.PageStart,
                        exactPageAnchor.PageEnd));
                });
            }

            return await AttachDocumentProfileContentCardsAsync(ds, tenantId, matches, tokenSource, ct);
        }
        catch (PostgresException ex)
        {
            RetrievalTelemetry.RecordRetrieverDegraded("title_anchor_route", ex);
            degradedRetrieverRef?.Invoke("title_anchor_route", FormatPostgresRetrieverError(ex));
            return [];
        }
        catch (Exception ex) when (IsRetrieverDatabaseTimeout(ex))
        {
            RetrievalTelemetry.RecordRetrieverDegraded("title_anchor_route", ex);
            degradedRetrieverRef?.Invoke("title_anchor_route", FormatRetrieverError(ex));
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
            WHEN COALESCE(rc.metadata->>'chunkType', '') = 'footer_titled_item_window_v1' THEN 2
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
        WHEN NULLIF(cc.metadata->>'navigationScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
            THEN (cc.metadata->>'navigationScore')::double precision
        ELSE NULL
    END AS "NavigationScore",
    CASE
        WHEN NULLIF(cc.metadata->>'contentDensityScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
            THEN (cc.metadata->>'contentDensityScore')::double precision
        ELSE NULL
    END AS "ContentDensityScore",
    cc.metadata->>'prevChunkId' AS "PrevChunkId",
    cc.metadata->>'nextChunkId' AS "NextChunkId",
    cc.metadata->>'sameSectionChunkId' AS "SameSectionChunkId",
    cc.metadata->>'extractionTextStatus' AS "ExtractionTextStatus",
    CASE
        WHEN LOWER(COALESCE(cc.metadata->>'extractionTextSparse', '')) IN ('true', 'false')
            THEN (cc.metadata->>'extractionTextSparse')::boolean
        ELSE NULL
    END AS "ExtractionTextSparse",
    CASE
        WHEN LOWER(COALESCE(cc.metadata->>'extractionOcrCandidate', '')) IN ('true', 'false')
            THEN (cc.metadata->>'extractionOcrCandidate')::boolean
        ELSE NULL
    END AS "ExtractionOcrCandidate",
    CASE
        WHEN jsonb_typeof(cc.metadata->'extractionQualitySignals') = 'array'
            THEN (cc.metadata->'extractionQualitySignals')::text
        ELSE NULL
    END AS "ExtractionQualitySignalsJson",
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
            }, commandTimeout: 15, cancellationToken: ct));

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
                    SameSectionChunkId: row.SameSectionChunkId,
                    ExtractionTextStatus: row.ExtractionTextStatus,
                    ExtractionTextSparse: row.ExtractionTextSparse,
                    ExtractionOcrCandidate: row.ExtractionOcrCandidate,
                    ExtractionQualitySignals: ParseExtractionQualitySignalsJson(row.ExtractionQualitySignalsJson),
                    ExtractionQualityObservation: ObserveExtractionQualitySignalsJson(row.ExtractionQualitySignalsJson)))
                .ToList();

            return await AttachDocumentProfileContentCardsAsync(ds, tenantId, matches, query, ct);
        }
        catch (PostgresException ex)
        {
            RetrievalTelemetry.RecordRetrieverDegraded("fuzzy_title_lead", ex);
            degradedRetrieverRef?.Invoke("fuzzy_title_lead", FormatPostgresRetrieverError(ex));
            return [];
        }
        catch (Exception ex) when (IsRetrieverDatabaseTimeout(ex))
        {
            RetrievalTelemetry.RecordRetrieverDegraded("fuzzy_title_lead", ex);
            degradedRetrieverRef?.Invoke("fuzzy_title_lead", FormatRetrieverError(ex));
            return [];
        }
    }


    internal static async Task<List<RagMatch>> SearchDenseMatchesAsync(
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
        string? categoryPath = null,
        Action<string, string?>? degradedRetrieverRef = null,
        TeiWorkloadGovernor? teiGovernor = null,
        bool attachContentCards = true,
        ILogger? logger = null)
    {
        var tei = httpFactory.CreateClient("tei");
        tei.BaseAddress = new Uri(rag.EmbeddingsBaseUrl);

        var swTei = Stopwatch.StartNew();
        var queryEmbeddingInput = TeiClient.FormatEmbeddingInput(
            rag.EmbeddingsModel,
            queryNorm,
            TeiClient.EmbeddingInputKind.Query);
        float[][] emb;
        try
        {
            var denseEmbeddingTimeoutSeconds = RagOptions.ResolveSearchDenseEmbeddingTimeoutSeconds(rag.SearchDenseEmbeddingTimeoutSeconds);
            using var denseEmbeddingCts = denseEmbeddingTimeoutSeconds > 0
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;
            denseEmbeddingCts?.CancelAfter(TimeSpan.FromSeconds(denseEmbeddingTimeoutSeconds));
            var denseEmbeddingToken = denseEmbeddingCts?.Token ?? ct;

            using var teiLease = teiGovernor is null
                ? null
                : await teiGovernor.AcquireInteractiveAsync(denseEmbeddingToken);
            emb = await TeiClient.EmbedAsync(tei, rag.EmbeddingsModel, [queryEmbeddingInput], denseEmbeddingToken);
            swTei.Stop();
            teiMsRef(swTei.ElapsedMilliseconds);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            swTei.Stop();
            teiMsRef(swTei.ElapsedMilliseconds);
            RetrievalTelemetry.RecordRetrieverDegraded("dense_qdrant", ex);
            degradedRetrieverRef?.Invoke("dense_qdrant", FormatRetrieverError(ex));
            return [];
        }

        var qvec = emb[0];
        var qdrant = httpFactory.CreateClient("qdrant");
        qdrant.BaseAddress = new Uri(rag.QdrantBaseUrl);

        var filterMust = new List<object>
        {
            new { key = "tenant_id", match = new { value = tenantId.ToString() } }
        };
        var filterMustNot = new List<object>
        {
            new { key = "content_role", match = new { value = RetrievalContentClassifier.NavigationRole } },
            new { key = "chunk_type", match = new { value = RetrievalContentClassifier.NavigationChunkType } }
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
        if (!string.IsNullOrWhiteSpace(rag.EmbeddingsModelRevision))
            filterMust.Add(new { key = "embedding_model_revision", match = new { value = rag.EmbeddingsModelRevision.Trim() } });

        var qdrantLimit = string.IsNullOrWhiteSpace(categoryPath)
            ? candidates
            : Math.Clamp(candidates * 3, candidates, Math.Max(candidates, 256));
        object RequestBody(int limit) => new
        {
            vector = qvec,
            limit,
            with_payload = true,
            filter = new { must = filterMust, must_not = filterMustNot }
        };

        var swQ = Stopwatch.StartNew();
        try
        {
            var filtered = await DenseActiveCandidateSearch.ExecuteAsync(qdrant, rag.QdrantCollection, qvec.Length,
                qdrantLimit, candidates, RequestBody,
                match => IsDenseMatchEmbeddingCompatible(match, rag.EmbeddingsModel),
                (matches, filterCt) => FilterMatchesAgainstActiveDocumentVersionsAsync(ds, tenantId, matches, filterCt, categoryPath),
                qdrantStatusRef, degradedRetrieverRef, ct, logger);
            var ranked = RerankDenseMatches(filtered);
            if (!attachContentCards)
            {
                return ranked
                    .Take(candidates)
                    .Select(static match => match with
                    {
                        MatchedContentCards = null,
                        ProfileSignals = null
                    })
                    .ToList();
            }

            var contentCardAttachmentLimit = ResolveDenseContentCardAttachmentLimit(candidates);
            if (ranked.Count == 0 || contentCardAttachmentLimit <= 0)
                return ranked.Take(candidates).ToList();

            var headCount = Math.Min(contentCardAttachmentLimit, ranked.Count);
            var enrichedHead = await AttachDocumentProfileContentCardsAsync(
                ds,
                tenantId,
                ranked.Take(headCount).ToList(),
                queryNorm,
                ct);

            if (headCount >= ranked.Count)
                return enrichedHead.Take(candidates).ToList();

            var denseResult = new List<RagMatch>(Math.Min(candidates, ranked.Count));
            denseResult.AddRange(enrichedHead);
            denseResult.AddRange(ranked.Skip(headCount).Take(Math.Max(0, candidates - denseResult.Count)));
            return denseResult;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            RetrievalTelemetry.RecordRetrieverDegraded("dense_qdrant", ex);
            degradedRetrieverRef?.Invoke("dense_qdrant", FormatRetrieverError(ex));
            return [];
        }
        finally
        {
            swQ.Stop();
            qdrantMsRef(swQ.ElapsedMilliseconds);
        }
    }


    internal static int ResolveDenseContentCardAttachmentLimit(int candidates)
    {
        if (candidates <= 0)
            return 0;

        return candidates <= 64 ? candidates : 48;
    }


    internal sealed record RerankAttempt(List<RagMatch> Matches, bool Applied);


    internal static bool IsDenseMatchEmbeddingCompatible(RagMatch match, string? embeddingsModel)
    {
        if (string.Equals(match.ContentRole, RetrievalContentClassifier.NavigationRole, StringComparison.OrdinalIgnoreCase)
            || string.Equals(match.ChunkType, RetrievalContentClassifier.NavigationChunkType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var expectedFormat = TeiClient.RequiresE5InstructionPrefix(embeddingsModel)
            ? "e5_passage_v1"
            : "raw_passage_v1";

        if (!string.Equals(match.EmbeddingInputFormat, expectedFormat, StringComparison.OrdinalIgnoreCase))
            return false;

        return string.IsNullOrWhiteSpace(embeddingsModel)
            || (!string.IsNullOrWhiteSpace(match.EmbeddingModel)
                && string.Equals(match.EmbeddingModel.Trim(), embeddingsModel.Trim(), StringComparison.OrdinalIgnoreCase));
    }


    internal static async Task<RerankAttempt> TryRerankWithTeiAsync(
        IHttpClientFactory httpFactory,
        RagOptions rag,
        string query,
        IReadOnlyList<RagMatch> candidates,
        CancellationToken ct,
        Action<long> rerankMsRef,
        TeiWorkloadGovernor? teiGovernor = null,
        Action<string, string?>? degradedRetrieverRef = null)
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
            using var teiLease = teiGovernor is null
                ? null
                : await teiGovernor.AcquireInteractiveAsync(ct);
            var reranked = await TeiClient.RerankAsync(tei, rag.RerankModel, query, texts, ct);
            return new RerankAttempt(ApplyRerankScores(candidates, reranked, rerankSlice.Count), Applied: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            degradedRetrieverRef?.Invoke("tei_rerank", FormatRetrieverError(ex));
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
        Action<string, string?>? degradedRetrieverRef = null,
        int commandTimeoutSeconds = RagOptions.DefaultSearchSparseCommandTimeoutSeconds)
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
        var sparseCommandTimeout = RagOptions.ResolveSearchSparseCommandTimeoutSeconds(commandTimeoutSeconds);

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
          AND saaia_is_safe_profile_content_card(card.kind, card.title, card.page_start, card.page_end, card.metadata)
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
        WHEN NULLIF(rc.metadata->>'navigationScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
            THEN (rc.metadata->>'navigationScore')::double precision
        ELSE NULL
    END AS "NavigationScore",
    CASE
        WHEN NULLIF(rc.metadata->>'contentDensityScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
            THEN (rc.metadata->>'contentDensityScore')::double precision
        ELSE NULL
    END AS "ContentDensityScore",
    rc.metadata->>'prevChunkId' AS "PrevChunkId",
    rc.metadata->>'nextChunkId' AS "NextChunkId",
    rc.metadata->>'sameSectionChunkId' AS "SameSectionChunkId",
    rc.metadata->>'extractionTextStatus' AS "ExtractionTextStatus",
    CASE
        WHEN LOWER(COALESCE(rc.metadata->>'extractionTextSparse', '')) IN ('true', 'false')
            THEN (rc.metadata->>'extractionTextSparse')::boolean
        ELSE NULL
    END AS "ExtractionTextSparse",
    CASE
        WHEN LOWER(COALESCE(rc.metadata->>'extractionOcrCandidate', '')) IN ('true', 'false')
            THEN (rc.metadata->>'extractionOcrCandidate')::boolean
        ELSE NULL
    END AS "ExtractionOcrCandidate",
    CASE
        WHEN jsonb_typeof(rc.metadata->'extractionQualitySignals') = 'array'
            THEN (rc.metadata->'extractionQualitySignals')::text
        ELSE NULL
    END AS "ExtractionQualitySignalsJson",
    pcm.matched_content_cards_json AS "MatchedContentCardsJson",
    (
        ts_rank_cd(
            cte.search_tsv,
            sparse_query.q,
            32
        ) + ((COALESCE(pcm.match_weight, 0.0) * 0.035)::real)
    )::real AS "SparseRank"
FROM sparse_query
JOIN documents d
  ON d.tenant_id = @tenant_id
 AND d.status = 'indexed'
 AND d.indexed_version > 0
 AND (@category IS NULL OR LOWER(d.category) = @category)
 AND (@category_path IS NULL OR d.doc_path = @category_path OR d.doc_path LIKE (@category_path || '/%'))
 AND (@doc_id IS NULL OR d.doc_id = @doc_id)
 AND (@doc_path IS NULL OR d.doc_path = @doc_path)
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
WHERE (cte.search_tsv @@ sparse_query.q OR COALESCE(pcm.match_count, 0) > 0)
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
            var hasScopedSparseSearch = !string.IsNullOrWhiteSpace(category)
                || !string.IsNullOrWhiteSpace(normalizedCategoryPath)
                || normalizedDocId.HasValue
                || !string.IsNullOrWhiteSpace(normalizedDocPath);
            var shouldSupplementSparseWithFallback = ShouldSupplementSparseWithLexicalFallback(hasScopedSparseSearch, query);
            if (lexicalTerms.Count > 0
                && shouldSupplementSparseWithFallback
                && !ShouldUseChoiceBetweenLexicalRoute(query)
                && ShouldSkipUnanchoredTitleAnchorRouteForBroadDiversity(query))
            {
                var focusedFallbackRows = (await conn.QueryAsync<SparseMatchRow>(new CommandDefinition(LexicalContentFallbackSql, new
                {
                    tenant_id = tenantId,
                    lexical_terms = BuildSupplementalSparseLexicalFallbackTerms(query).ToArray(),
                    category,
                    category_path = normalizedCategoryPath,
                    doc_id = normalizedDocId,
                    doc_path = normalizedDocPath,
                    top_k = topK
                }, commandTimeout: sparseCommandTimeout, cancellationToken: ct))).ToList();

                var focusedFallbackMatches = BuildSparseRagMatches(focusedFallbackRows, query);
                return await AttachDocumentProfileContentCardsAsync(ds, tenantId, focusedFallbackMatches, query, ct, perMatchLimit: 6);
            }

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
            }, commandTimeout: sparseCommandTimeout, cancellationToken: ct))).ToList();

            if (lexicalTerms.Count > 0
                && (rows.Count == 0
                    || shouldSupplementSparseWithFallback))
            {
                var fallbackLexicalTerms = shouldSupplementSparseWithFallback
                    ? BuildSupplementalSparseLexicalFallbackTerms(query)
                    : lexicalTerms;
                var fallbackRows = (await conn.QueryAsync<SparseMatchRow>(new CommandDefinition(LexicalContentFallbackSql, new
                {
                    tenant_id = tenantId,
                    lexical_terms = fallbackLexicalTerms.ToArray(),
                    category,
                    category_path = normalizedCategoryPath,
                    doc_id = normalizedDocId,
                    doc_path = normalizedDocPath,
                    top_k = topK
                }, commandTimeout: sparseCommandTimeout, cancellationToken: ct))).ToList();

                rows = rows.Count == 0
                    ? fallbackRows
                    : MergeSparseRows(fallbackRows, rows, topK);
            }

            var matches = BuildSparseRagMatches(rows, query);
            return await AttachDocumentProfileContentCardsAsync(ds, tenantId, matches, query, ct, perMatchLimit: 6);
        }
        catch (PostgresException ex)
        {
            RetrievalTelemetry.RecordRetrieverDegraded("sparse_bm25", ex);
            degradedRetrieverRef?.Invoke("sparse_bm25", FormatPostgresRetrieverError(ex));
            return [];
        }
        catch (Exception ex) when (IsRetrieverDatabaseTimeout(ex))
        {
            RetrievalTelemetry.RecordRetrieverDegraded("sparse_bm25", ex);
            degradedRetrieverRef?.Invoke("sparse_bm25", FormatRetrieverError(ex));
            return [];
        }
        finally
        {
            swSparse.Stop();
            sparseMsRef(swSparse.ElapsedMilliseconds);
        }
    }


    internal static async Task<List<RagMatch>> SearchLexicalContentFallbackMatchesAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        string query,
        string? category,
        string? docId,
        string? docPath,
        int topK,
        CancellationToken ct,
        string? categoryPath = null,
        Action<string, string?>? degradedRetrieverRef = null,
        int commandTimeoutSeconds = RagOptions.DefaultSearchSparseCommandTimeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(query) || topK <= 0)
            return [];

        var lexicalTerms = BuildLexicalContentFallbackTerms(query);
        if (lexicalTerms.Count == 0)
            return [];

        var normalizedDocPath = string.IsNullOrWhiteSpace(docPath)
            ? null
            : docPath.Trim().Replace('\\', '/').TrimStart('/');
        var normalizedCategoryPath = NormalizeRagCategoryPathForSql(categoryPath);
        Guid? normalizedDocId = Guid.TryParse(docId, out var parsedDocId) ? parsedDocId : null;
        var commandTimeout = RagOptions.ResolveSearchSparseCommandTimeoutSeconds(commandTimeoutSeconds);
        await using var conn = await ds.OpenConnectionAsync(ct);
        try
        {
            var rows = (await conn.QueryAsync<SparseMatchRow>(new CommandDefinition(LexicalContentFallbackSql, new
            {
                tenant_id = tenantId,
                lexical_terms = lexicalTerms.ToArray(),
                category,
                category_path = normalizedCategoryPath,
                doc_id = normalizedDocId,
                doc_path = normalizedDocPath,
                top_k = topK
            }, commandTimeout: commandTimeout, cancellationToken: ct))).ToList();

            return BuildSparseRagMatches(rows, query);
        }
        catch (PostgresException ex)
        {
            RetrievalTelemetry.RecordRetrieverDegraded("lexical_content_fallback", ex);
            degradedRetrieverRef?.Invoke("lexical_content_fallback", FormatPostgresRetrieverError(ex));
            return [];
        }
        catch (Exception ex) when (IsRetrieverDatabaseTimeout(ex))
        {
            RetrievalTelemetry.RecordRetrieverDegraded("lexical_content_fallback", ex);
            degradedRetrieverRef?.Invoke("lexical_content_fallback", FormatRetrieverError(ex));
            return [];
        }
    }


    internal static async Task<List<RagMatch>> SearchShortTechnicalDirectPhraseMatchesAsync(
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
        if (string.IsNullOrWhiteSpace(query) || topK <= 0 || !IsShortTechnicalPropertyLookup(query))
            return [];

        var phraseTerms = BuildShortTechnicalDirectPhraseTerms(query);
        if (phraseTerms.Count == 0)
            return [];

        var normalizedDocPath = string.IsNullOrWhiteSpace(docPath)
            ? null
            : docPath.Trim().Replace('\\', '/').TrimStart('/');
        var normalizedCategoryPath = NormalizeRagCategoryPathForSql(categoryPath);
        Guid? normalizedDocId = Guid.TryParse(docId, out var parsedDocId) ? parsedDocId : null;

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
WITH phrase_terms AS (
    SELECT DISTINCT LOWER(term) AS term
    FROM unnest(@phrase_terms::text[]) AS term
    WHERE term IS NOT NULL AND length(term) >= 4 AND term LIKE '% %'
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
chunk_term_matches AS (
    SELECT
        rc.retrieval_chunk_id,
        phrase_terms.term,
        CASE
            WHEN phrase_terms.term LIKE '% properties' OR phrase_terms.term LIKE 'proprietes %' THEN 14.0
            WHEN phrase_terms.term LIKE '% characteristics' OR phrase_terms.term LIKE 'caracteristiques %' THEN 12.0
            WHEN phrase_terms.term LIKE '% strength' OR phrase_terms.term LIKE '% temperature%' OR phrase_terms.term LIKE '% point' THEN 10.0
            ELSE 7.0
        END AS match_weight,
        NULLIF(strpos(searchable.searchable_text, phrase_terms.term), 0) AS phrase_start
    FROM scoped_docs d
    JOIN retrieval_chunks rc
      ON rc.tenant_id = d.tenant_id
     AND rc.revision_id = d.revision_id
    CROSS JOIN LATERAL (
        SELECT LOWER(CONCAT_WS(
            ' ',
            rc.text_content,
            rc.metadata->>'sectionTitle',
            rc.metadata->>'headingPath')) AS searchable_text
    ) searchable
    JOIN phrase_terms
      ON searchable.searchable_text LIKE '%' || phrase_terms.term || '%'
      OR searchable.searchable_text ~ REPLACE(phrase_terms.term, ' ', '([^[:alnum:]]+[[:alnum:]]{1,3}){0,2}[^[:alnum:]]+')
),
ranked_chunks AS (
    SELECT
        retrieval_chunk_id,
        COUNT(DISTINCT term)::int AS match_count,
        SUM(match_weight)::real AS match_weight,
        MIN(phrase_start) AS phrase_start,
        LEFT(string_agg(DISTINCT term, '; ' ORDER BY term), 500) AS matched_terms
    FROM chunk_term_matches
    GROUP BY retrieval_chunk_id
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
    ('Matched direct technical phrase: ' || ranked.matched_terms || E'\n' || rc.text_content) AS "EmbedText",
    rc.section_id AS "SectionOrdinalPlaceholder",
    COALESCE(rc.metadata->>'sectionTitle', s.title) AS "SectionTitle",
    COALESCE(rc.metadata->>'headingPath', s.title) AS "HeadingPath",
    COALESCE(rc.metadata->>'chunkType', 'contextual_text_v1') AS "ChunkType",
    COALESCE(rc.metadata->>'contentRole', 'content') AS "ContentRole",
    rc.metadata->>'navigationReason' AS "NavigationReason",
    rc.metadata->>'originalChunkType' AS "OriginalChunkType",
    CASE
        WHEN NULLIF(rc.metadata->>'navigationScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
            THEN (rc.metadata->>'navigationScore')::double precision
        ELSE NULL
    END AS "NavigationScore",
    CASE
        WHEN NULLIF(rc.metadata->>'contentDensityScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
            THEN (rc.metadata->>'contentDensityScore')::double precision
        ELSE NULL
    END AS "ContentDensityScore",
    rc.metadata->>'prevChunkId' AS "PrevChunkId",
    rc.metadata->>'nextChunkId' AS "NextChunkId",
    rc.metadata->>'sameSectionChunkId' AS "SameSectionChunkId",
    rc.metadata->>'extractionTextStatus' AS "ExtractionTextStatus",
    CASE
        WHEN LOWER(COALESCE(rc.metadata->>'extractionTextSparse', '')) IN ('true', 'false')
            THEN (rc.metadata->>'extractionTextSparse')::boolean
        ELSE NULL
    END AS "ExtractionTextSparse",
    CASE
        WHEN LOWER(COALESCE(rc.metadata->>'extractionOcrCandidate', '')) IN ('true', 'false')
            THEN (rc.metadata->>'extractionOcrCandidate')::boolean
        ELSE NULL
    END AS "ExtractionOcrCandidate",
    CASE
        WHEN jsonb_typeof(rc.metadata->'extractionQualitySignals') = 'array'
            THEN (rc.metadata->'extractionQualitySignals')::text
        ELSE NULL
    END AS "ExtractionQualitySignalsJson",
    NULL::text AS "MatchedContentCardsJson",
    (
        ranked.match_weight
        + LEAST(ranked.match_count, 4)::real * 2.0
        + CASE WHEN ranked.phrase_start IS NULL THEN 0.0 ELSE GREATEST(0.0, 3.0 - (ranked.phrase_start::real / 220.0)) END
    )::real AS "SparseRank"
FROM ranked_chunks ranked
JOIN retrieval_chunks rc
  ON rc.retrieval_chunk_id = ranked.retrieval_chunk_id
JOIN scoped_docs d
  ON d.tenant_id = rc.tenant_id
 AND d.revision_id = rc.revision_id
LEFT JOIN document_sections s
  ON s.section_id = rc.section_id
ORDER BY ranked.match_count DESC,
         ranked.match_weight DESC,
         COALESCE(ranked.phrase_start, 100000) ASC,
         CASE
            WHEN COALESCE(rc.metadata->>'contentRole', 'content') = 'content' THEN 0
            WHEN COALESCE(rc.metadata->>'contentRole', 'content') = 'mixed_navigation_content' THEN 1
            ELSE 2
         END,
         rc.chunk_index ASC
LIMIT @top_k;
""";
        try
        {
            var rows = (await conn.QueryAsync<SparseMatchRow>(new CommandDefinition(sql, new
            {
                tenant_id = tenantId,
                phrase_terms = phraseTerms.ToArray(),
                category,
                category_path = normalizedCategoryPath,
                doc_id = normalizedDocId,
                doc_path = normalizedDocPath,
                top_k = Math.Max(topK, phraseTerms.Count * 2)
            }, commandTimeout: 30, cancellationToken: ct))).ToList();

            return BuildSparseRagMatches(rows, query, embeddingBasis: "short_technical_phrase_v1")
                .Where(match => ComputeShortTechnicalDirectEvidencePriority(query, match, string.Empty, string.Empty) > 0)
                .Take(topK)
                .ToList();
        }
        catch (PostgresException ex)
        {
            RetrievalTelemetry.RecordRetrieverDegraded("short_technical_direct_phrase", ex);
            degradedRetrieverRef?.Invoke("short_technical_direct_phrase", FormatPostgresRetrieverError(ex));
            return [];
        }
        catch (Exception ex) when (IsRetrieverDatabaseTimeout(ex))
        {
            RetrievalTelemetry.RecordRetrieverDegraded("short_technical_direct_phrase", ex);
            degradedRetrieverRef?.Invoke("short_technical_direct_phrase", FormatRetrieverError(ex));
            return [];
        }
    }


    internal static async Task<List<RagMatch>> SearchDocumentScopedLexicalContentMatchesAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        string query,
        string? category,
        string docPath,
        int topK,
        CancellationToken ct,
        string? categoryPath = null,
        Action<string, string?>? degradedRetrieverRef = null)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(docPath) || topK <= 0)
            return [];

        var lexicalTerms = BuildLexicalContentFallbackTerms(query)
            .OrderByDescending(static term => term.Contains(' '))
            .ThenByDescending(static term => term.Length)
            .ThenBy(static term => term, StringComparer.Ordinal)
            .Take(24)
            .ToArray();
        if (lexicalTerms.Length == 0)
            return [];

        var normalizedDocPath = docPath.Trim().Replace('\\', '/').TrimStart('/');
        var normalizedCategoryPath = NormalizeRagCategoryPathForSql(categoryPath);

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
WITH lexical_terms AS (
    SELECT DISTINCT LOWER(term) AS term
    FROM unnest(@lexical_terms::text[]) AS term
    WHERE term IS NOT NULL AND term <> ''
),
scoped_doc AS (
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
      AND d.doc_path = @doc_path
      AND (@category IS NULL OR LOWER(d.category) = @category)
      AND (@category_path IS NULL OR d.doc_path = @category_path OR d.doc_path LIKE (@category_path || '/%'))
),
chunk_term_matches AS (
    SELECT
        rc.retrieval_chunk_id,
        lt.term,
        CASE
            WHEN lt.term LIKE '% %' AND length(lt.term) >= 18 THEN 7.0
            WHEN lt.term LIKE '% %' THEN 5.0
            WHEN length(lt.term) >= 10 THEN 2.5
            WHEN length(lt.term) >= 7 THEN 1.6
            ELSE 1.0
        END AS match_weight
    FROM scoped_doc d
    JOIN retrieval_chunks rc
      ON rc.tenant_id = d.tenant_id
     AND rc.revision_id = d.revision_id
    JOIN lexical_terms lt
      ON CASE
          WHEN lt.term LIKE '% %'
              THEN LOWER(rc.text_content) LIKE '%' || REPLACE(lt.term, ' ', '%') || '%'
          ELSE LOWER(rc.text_content) LIKE '%' || lt.term || '%'
      END
    WHERE COALESCE(rc.metadata->>'contentRole', 'content') <> 'navigation'
      AND COALESCE(rc.metadata->>'chunkType', 'contextual_text_v1') <> 'navigation_index_v1'
      AND btrim(COALESCE(rc.text_content, '')) <> ''
),
ranked_chunk_terms AS (
    SELECT
        retrieval_chunk_id,
        COUNT(DISTINCT term) AS match_count,
        SUM(match_weight)::real AS match_weight
    FROM chunk_term_matches
    GROUP BY retrieval_chunk_id
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
    rc.text_content AS "EmbedText",
    rc.section_id AS "SectionOrdinalPlaceholder",
    COALESCE(rc.metadata->>'sectionTitle', s.title) AS "SectionTitle",
    COALESCE(rc.metadata->>'headingPath', s.title) AS "HeadingPath",
    COALESCE(rc.metadata->>'chunkType', 'contextual_text_v1') AS "ChunkType",
    COALESCE(rc.metadata->>'contentRole', 'content') AS "ContentRole",
    rc.metadata->>'navigationReason' AS "NavigationReason",
    rc.metadata->>'originalChunkType' AS "OriginalChunkType",
    CASE
        WHEN NULLIF(rc.metadata->>'navigationScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
            THEN (rc.metadata->>'navigationScore')::double precision
        ELSE NULL
    END AS "NavigationScore",
    CASE
        WHEN NULLIF(rc.metadata->>'contentDensityScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
            THEN (rc.metadata->>'contentDensityScore')::double precision
        ELSE NULL
    END AS "ContentDensityScore",
    rc.metadata->>'prevChunkId' AS "PrevChunkId",
    rc.metadata->>'nextChunkId' AS "NextChunkId",
    rc.metadata->>'sameSectionChunkId' AS "SameSectionChunkId",
    rc.metadata->>'extractionTextStatus' AS "ExtractionTextStatus",
    CASE
        WHEN LOWER(COALESCE(rc.metadata->>'extractionTextSparse', '')) IN ('true', 'false')
            THEN (rc.metadata->>'extractionTextSparse')::boolean
        ELSE NULL
    END AS "ExtractionTextSparse",
    CASE
        WHEN LOWER(COALESCE(rc.metadata->>'extractionOcrCandidate', '')) IN ('true', 'false')
            THEN (rc.metadata->>'extractionOcrCandidate')::boolean
        ELSE NULL
    END AS "ExtractionOcrCandidate",
    CASE
        WHEN jsonb_typeof(rc.metadata->'extractionQualitySignals') = 'array'
            THEN (rc.metadata->'extractionQualitySignals')::text
        ELSE NULL
    END AS "ExtractionQualitySignalsJson",
    NULL::text AS "MatchedContentCardsJson",
    ranked.match_weight AS "SparseRank"
FROM ranked_chunk_terms ranked
JOIN retrieval_chunks rc
  ON rc.retrieval_chunk_id = ranked.retrieval_chunk_id
JOIN scoped_doc d
  ON d.tenant_id = rc.tenant_id
 AND d.revision_id = rc.revision_id
LEFT JOIN document_sections s
  ON s.section_id = rc.section_id
ORDER BY ranked.match_weight DESC,
         ranked.match_count DESC,
         rc.chunk_index ASC
LIMIT @top_k;
""";

        try
        {
            var rows = (await conn.QueryAsync<SparseMatchRow>(new CommandDefinition(sql, new
            {
                tenant_id = tenantId,
                lexical_terms = lexicalTerms,
                category,
                category_path = normalizedCategoryPath,
                doc_path = normalizedDocPath,
                top_k = topK
            }, commandTimeout: 12, cancellationToken: ct))).ToList();

            return BuildSparseRagMatches(rows, query);
        }
        catch (PostgresException ex)
        {
            RetrievalTelemetry.RecordRetrieverDegraded("document_scoped_lexical_content", ex);
            degradedRetrieverRef?.Invoke("document_scoped_lexical_content", FormatPostgresRetrieverError(ex));
            return [];
        }
        catch (Exception ex) when (IsRetrieverDatabaseTimeout(ex))
        {
            RetrievalTelemetry.RecordRetrieverDegraded("document_scoped_lexical_content", ex);
            degradedRetrieverRef?.Invoke("document_scoped_lexical_content", FormatRetrieverError(ex));
            return [];
        }
    }


    internal static List<RagMatch> BuildSparseRagMatches(
        IEnumerable<SparseMatchRow> rows,
        string query,
        string embeddingBasis = "sparse_bm25_v1")
        => rows.Select(row => new RagMatch(
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
            EmbeddingBasis: embeddingBasis,
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
            MatchedContentCards: BuildSparseMatchedContentCards(row, query),
            ExtractionTextStatus: row.ExtractionTextStatus,
            ExtractionTextSparse: row.ExtractionTextSparse,
            ExtractionOcrCandidate: row.ExtractionOcrCandidate,
            ExtractionQualitySignals: ParseExtractionQualitySignalsJson(row.ExtractionQualitySignalsJson),
            ExtractionQualityObservation: ObserveExtractionQualitySignalsJson(row.ExtractionQualitySignalsJson))).ToList();


    internal static IReadOnlyList<string>? ParseExtractionQualitySignalsJson(string? value)
    {
        var observed = ObserveExtractionQualitySignalsJson(value);
        return observed.Signals.Count == 0 ? null : observed.Signals;
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
          AND saaia_is_safe_profile_content_card(card.kind, card.title, card.page_start, card.page_end, card.metadata)
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
          AND saaia_is_safe_profile_content_card(card.kind, card.title, card.page_start, card.page_end, card.metadata)
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
        WHEN NULLIF(rc.metadata->>'navigationScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
            THEN (rc.metadata->>'navigationScore')::double precision
        ELSE NULL
    END AS "NavigationScore",
    CASE
        WHEN NULLIF(rc.metadata->>'contentDensityScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
            THEN (rc.metadata->>'contentDensityScore')::double precision
        ELSE NULL
    END AS "ContentDensityScore",
    rc.metadata->>'prevChunkId' AS "PrevChunkId",
    rc.metadata->>'nextChunkId' AS "NextChunkId",
    rc.metadata->>'sameSectionChunkId' AS "SameSectionChunkId",
    rc.metadata->>'extractionTextStatus' AS "ExtractionTextStatus",
    CASE
        WHEN LOWER(COALESCE(rc.metadata->>'extractionTextSparse', '')) IN ('true', 'false')
            THEN (rc.metadata->>'extractionTextSparse')::boolean
        ELSE NULL
    END AS "ExtractionTextSparse",
    CASE
        WHEN LOWER(COALESCE(rc.metadata->>'extractionOcrCandidate', '')) IN ('true', 'false')
            THEN (rc.metadata->>'extractionOcrCandidate')::boolean
        ELSE NULL
    END AS "ExtractionOcrCandidate",
    CASE
        WHEN jsonb_typeof(rc.metadata->'extractionQualitySignals') = 'array'
            THEN (rc.metadata->'extractionQualitySignals')::text
        ELSE NULL
    END AS "ExtractionQualitySignalsJson",
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
        Action<string, string?>? degradedRetrieverRef = null,
        int commandTimeoutSeconds = RagOptions.DefaultSearchSparseCommandTimeoutSeconds)
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
        var commandTimeout = RagOptions.ResolveSearchSparseCommandTimeoutSeconds(commandTimeoutSeconds);

        await using var conn = await ds.OpenConnectionAsync(ct);
        await EnsureFreshDocumentProfileSearchEntriesAsync(
            conn,
            tenantId,
            category,
            normalizedCategoryPath,
            normalizedDocId,
            normalizedDocPath,
            commandTimeout,
            ct,
            degradedRetrieverRef);

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
    e.document_profile_id AS "ProfileId",
    d.indexed_version AS "IngestionVersion",
    LOWER(ENCODE(d.content_hash, 'hex')) AS "HashDoc",
    COALESCE(NULLIF(e.summary_text, ''), d.doc_name, d.doc_path) AS "Text",
    e.search_text AS "SearchText",
    e.metadata_json::text AS "MetadataJson",
    e.language AS "Language",
    e.profile_version AS "ProfileVersion",
    e.keywords AS "Keywords",
    e.entities AS "Entities",
    e.topics AS "Topics",
    e.hypothetical_questions AS "HypotheticalQuestions",
    e.limits AS "Limits",
    CASE
        WHEN COALESCE(lm.match_count, 0) > 0
        THEN (0.01::double precision + (COALESCE(lm.match_weight, 0.0)::double precision * 0.05::double precision))
        ELSE 0.0::double precision
    END AS "SparseRank",
    COALESCE(lm.match_count, 0)::bigint AS "MatchCount"
FROM (
    SELECT *
    FROM documents
    WHERE tenant_id = @tenant_id
      AND status = 'indexed'
      AND indexed_version > 0
      AND (@category IS NULL OR LOWER(category) = @category)
      AND (@category_path IS NULL OR doc_path = @category_path OR doc_path LIKE (@category_path || '/%'))
      AND (@doc_id IS NULL OR doc_id = @doc_id)
      AND (@doc_path IS NULL OR doc_path = @doc_path)
) d
JOIN document_revisions r
  ON r.tenant_id = d.tenant_id
 AND r.doc_id = d.doc_id
 AND r.indexed_version = d.indexed_version
JOIN document_profile_search_entries e
  ON e.tenant_id = r.tenant_id
 AND e.revision_id = r.revision_id
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
    WHERE LOWER(e.search_text) LIKE '%' || lexical_terms.term || '%'
) lm ON TRUE
WHERE (NOT @require_lexical_match OR COALESCE(lm.match_count, 0) > 0)
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
            }, commandTimeout: commandTimeout, cancellationToken: ct))).ToList();

            var useCatalogOverviewFallback = !requireLexicalMatch
                && ShouldUseCatalogDocumentOverviewFallback(query);

            return RankDocumentProfileRows(query, rows)
                .Select(scored => useCatalogOverviewFallback && scored.Score < 0.42
                    ? scored with { Score = 0.42, BaseScore = Math.Max(scored.BaseScore, 0.42) }
                    : scored)
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
                    MatchedContentCards: matchedContentCards.Count == 0 ? null : matchedContentCards,
                    ProfileSignals: BuildDocumentProfileSignals(row, query));
            }).ToList();
        }
        catch (Exception ex) when (ex is PostgresException or InvalidOperationException || IsRetrieverDatabaseTimeout(ex))
        {
            RetrievalTelemetry.RecordRetrieverDegraded("document_profile_overview_v1", ex);
            degradedRetrieverRef?.Invoke("document_profile_overview_v1", FormatRetrieverError(ex));
            return [];
        }
    }


    internal static async Task<List<RagMatch>> SearchStandardReferenceDocumentNameContentMatchesAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        string query,
        IReadOnlyList<string> standardReferencePhrases,
        string? category,
        string? docId,
        string? docPath,
        int topK,
        CancellationToken ct,
        string? categoryPath = null,
        Action<string, string?>? degradedRetrieverRef = null)
    {
        if (topK <= 0 || standardReferencePhrases.Count == 0)
            return [];

        var phrases = standardReferencePhrases
            .Select(ExactMatchEntryExtractor.NormalizeForLookup)
            .Where(LooksLikeStandardReferenceTitleLookupPhrase)
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToArray();
        if (phrases.Length == 0)
            return [];

        var normalizedDocPath = string.IsNullOrWhiteSpace(docPath)
            ? null
            : docPath.Trim().Replace('\\', '/').TrimStart('/');
        var normalizedCategoryPath = NormalizeRagCategoryPathForSql(categoryPath);
        Guid? normalizedDocId = Guid.TryParse(docId, out var parsedDocId) ? parsedDocId : null;

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
WITH reference_phrases AS (
    SELECT DISTINCT LOWER(phrase) AS phrase
    FROM unnest(@phrases::text[]) AS phrase
    WHERE phrase IS NOT NULL AND length(phrase) >= 4
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
        r.revision_id,
        regexp_replace(LOWER(d.doc_path || ' ' || d.doc_name), '[^a-z0-9]+', ' ', 'g') AS normalized_doc_text,
        substring(
            regexp_replace(LOWER(d.doc_path || ' ' || d.doc_name), '[^a-z0-9]+', ' ', 'g')
            from '((en|iso|iec|astm|din|nfpa|api|ansi|ul|cen|tr|ts|pd|bs|nist|sp) (sp )?[0-9]{2,6}( [0-9]{1,4}[a-z0-9]*)?)') AS family_key
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
matched_docs AS (
    SELECT
        d.*,
        COUNT(*) AS phrase_match_count,
        MAX(length(p.phrase)) AS phrase_match_length
    FROM scoped_docs d
    JOIN reference_phrases p
      ON d.normalized_doc_text LIKE '%' || replace(p.phrase, ' ', '%') || '%'
    GROUP BY
        d.tenant_id,
        d.doc_id,
        d.doc_path,
        d.doc_name,
        d.category,
        d.indexed_version,
        d.content_hash,
        d.revision_id,
        d.normalized_doc_text
),
ranked_chunks AS (
    SELECT
        d.*,
        rc.page_start,
        rc.page_end,
        CASE
            WHEN NULLIF(rc.metadata->>'offsetStart', '') ~ '^[-+]?[0-9]+$'
                THEN CASE
                    WHEN (rc.metadata->>'offsetStart')::numeric BETWEEN -2147483648 AND 2147483647
                        THEN (rc.metadata->>'offsetStart')::int
                    ELSE NULL
                END
            ELSE NULL
        END AS offset_start,
        CASE
            WHEN NULLIF(rc.metadata->>'offsetEnd', '') ~ '^[-+]?[0-9]+$'
                THEN CASE
                    WHEN (rc.metadata->>'offsetEnd')::numeric BETWEEN -2147483648 AND 2147483647
                        THEN (rc.metadata->>'offsetEnd')::int
                    ELSE NULL
                END
            ELSE NULL
        END AS offset_end,
        rc.retrieval_chunk_id,
        rc.chunk_index,
        cte.text_content,
        COALESCE(rc.metadata->>'sectionTitle', s.title) AS section_title,
        COALESCE(rc.metadata->>'headingPath', s.title) AS heading_path,
        COALESCE(rc.metadata->>'chunkType', 'contextual_text_v1') AS chunk_type,
        COALESCE(rc.metadata->>'contentRole', 'content') AS content_role,
        rc.metadata->>'navigationReason' AS navigation_reason,
        rc.metadata->>'originalChunkType' AS original_chunk_type,
        CASE
            WHEN NULLIF(rc.metadata->>'navigationScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
                THEN (rc.metadata->>'navigationScore')::double precision
            ELSE NULL
        END AS navigation_score,
        CASE
            WHEN NULLIF(rc.metadata->>'contentDensityScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
                THEN (rc.metadata->>'contentDensityScore')::double precision
            ELSE NULL
        END AS content_density_score,
        rc.metadata->>'prevChunkId' AS prev_chunk_id,
        rc.metadata->>'nextChunkId' AS next_chunk_id,
        rc.metadata->>'sameSectionChunkId' AS same_section_chunk_id,
        rc.metadata->>'extractionTextStatus' AS extraction_text_status,
        CASE
            WHEN LOWER(COALESCE(rc.metadata->>'extractionTextSparse', '')) IN ('true', 'false')
                THEN (rc.metadata->>'extractionTextSparse')::boolean
            ELSE NULL
        END AS extraction_text_sparse,
        CASE
            WHEN LOWER(COALESCE(rc.metadata->>'extractionOcrCandidate', '')) IN ('true', 'false')
                THEN (rc.metadata->>'extractionOcrCandidate')::boolean
            ELSE NULL
        END AS extraction_ocr_candidate,
        CASE
            WHEN jsonb_typeof(rc.metadata->'extractionQualitySignals') = 'array'
                THEN (rc.metadata->'extractionQualitySignals')::text
            ELSE NULL
        END AS extraction_quality_signals_json,
        ROW_NUMBER() OVER (
            PARTITION BY d.doc_id
            ORDER BY
                CASE COALESCE(rc.metadata->>'contentRole', 'content')
                    WHEN 'content' THEN 0
                    WHEN 'mixed_navigation_content' THEN 1
                    ELSE 2
                END,
                CASE WHEN COALESCE(rc.metadata->>'chunkType', '') = 'unit_exact_v1' THEN 0 ELSE 1 END,
                GREATEST(1, COALESCE(rc.page_start, 1)),
                rc.chunk_index
        ) AS doc_chunk_rank
    FROM matched_docs d
    JOIN retrieval_chunks rc
      ON rc.tenant_id = d.tenant_id
     AND rc.revision_id = d.revision_id
    JOIN contextual_text_entries cte
      ON cte.tenant_id = rc.tenant_id
     AND cte.revision_id = rc.revision_id
     AND cte.retrieval_chunk_id = rc.retrieval_chunk_id
    LEFT JOIN document_sections s
      ON s.section_id = rc.section_id
    WHERE length(cte.text_content) >= 80
      AND COALESCE(rc.metadata->>'contentRole', 'content') <> 'navigation'
      AND COALESCE(rc.metadata->>'chunkType', '') <> 'navigation_index_v1'
)
SELECT
    doc_id AS "DocId",
    doc_path AS "DocPath",
    doc_name AS "DocName",
    category AS "Category",
    page_start AS "PageStart",
    page_end AS "PageEnd",
    offset_start AS "OffsetStart",
    offset_end AS "OffsetEnd",
    retrieval_chunk_id AS "ChunkId",
    chunk_index AS "ChunkIndex",
    text_content AS "Text",
    indexed_version AS "IngestionVersion",
    LOWER(ENCODE(content_hash, 'hex')) AS "HashDoc",
    doc_name || E'\n' || text_content AS "EmbedText",
    NULL::uuid AS "SectionOrdinalPlaceholder",
    section_title AS "SectionTitle",
    heading_path AS "HeadingPath",
    chunk_type AS "ChunkType",
    content_role AS "ContentRole",
    navigation_reason AS "NavigationReason",
    original_chunk_type AS "OriginalChunkType",
    navigation_score AS "NavigationScore",
    content_density_score AS "ContentDensityScore",
    prev_chunk_id AS "PrevChunkId",
    next_chunk_id AS "NextChunkId",
    same_section_chunk_id AS "SameSectionChunkId",
    extraction_text_status AS "ExtractionTextStatus",
    extraction_text_sparse AS "ExtractionTextSparse",
    extraction_ocr_candidate AS "ExtractionOcrCandidate",
    extraction_quality_signals_json AS "ExtractionQualitySignalsJson",
    NULL::text AS "MatchedContentCardsJson",
    (0.62 + LEAST(phrase_match_count, 3)::real * 0.04 + LEAST(phrase_match_length, 40)::real * 0.002)::real AS "SparseRank"
FROM ranked_chunks
WHERE doc_chunk_rank <= 2
ORDER BY
    phrase_match_count DESC,
    phrase_match_length DESC,
    doc_path ASC,
    doc_chunk_rank ASC
LIMIT @top_k;
""";

        try
        {
            var rows = (await conn.QueryAsync<SparseMatchRow>(new CommandDefinition(sql, new
            {
                tenant_id = tenantId,
                phrases,
                category,
                category_path = normalizedCategoryPath,
                doc_id = normalizedDocId,
                doc_path = normalizedDocPath,
                top_k = topK
            }, commandTimeout: 90, cancellationToken: ct))).ToList();

            return BuildSparseRagMatches(rows, query)
                .Select(static match => match with { EmbeddingBasis = "standard_reference_document_name_v1" })
                .ToList();
        }
        catch (Exception ex) when (ex is PostgresException or InvalidOperationException || IsRetrieverDatabaseTimeout(ex))
        {
            RetrievalTelemetry.RecordRetrieverDegraded("standard_reference_document_name_content", ex);
            degradedRetrieverRef?.Invoke("standard_reference_document_name_content", FormatRetrieverError(ex));
            return [];
        }
    }


    internal static async Task<List<RagMatch>> SearchStandardReferenceDocumentNameProfileMatchesAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        string query,
        IReadOnlyList<string> standardReferencePhrases,
        string? category,
        string? docId,
        string? docPath,
        int topK,
        CancellationToken ct,
        string? categoryPath = null,
        Action<string, string?>? degradedRetrieverRef = null,
        int commandTimeoutSeconds = RagOptions.DefaultSearchSparseCommandTimeoutSeconds)
    {
        if (topK <= 0 || standardReferencePhrases.Count == 0)
            return [];

        var phrases = standardReferencePhrases
            .Select(ExactMatchEntryExtractor.NormalizeForLookup)
            .Where(LooksLikeStandardReferenceTitleLookupPhrase)
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToArray();
        if (phrases.Length == 0)
            return [];

        var normalizedDocPath = string.IsNullOrWhiteSpace(docPath)
            ? null
            : docPath.Trim().Replace('\\', '/').TrimStart('/');
        var normalizedCategoryPath = NormalizeRagCategoryPathForSql(categoryPath);
        Guid? normalizedDocId = Guid.TryParse(docId, out var parsedDocId) ? parsedDocId : null;
        var commandTimeout = RagOptions.ResolveSearchSparseCommandTimeoutSeconds(commandTimeoutSeconds);

        await using var conn = await ds.OpenConnectionAsync(ct);
        await EnsureFreshDocumentProfileSearchEntriesAsync(
            conn,
            tenantId,
            category,
            normalizedCategoryPath,
            normalizedDocId,
            normalizedDocPath,
            commandTimeout,
            ct,
            degradedRetrieverRef);

        const string sql = """
WITH reference_phrases AS (
    SELECT DISTINCT LOWER(phrase) AS phrase
    FROM unnest(@phrases::text[]) AS phrase
    WHERE phrase IS NOT NULL AND phrase <> ''
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
        d.file_size,
        d.file_mtime,
        r.revision_id,
        regexp_replace(LOWER(d.doc_path || ' ' || d.doc_name), '[^a-z0-9]+', ' ', 'g') AS normalized_doc_text
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
matched_docs AS (
    SELECT
        d.*,
        COUNT(*) AS phrase_match_count,
        MAX(length(p.phrase)) AS phrase_match_length
    FROM scoped_docs d
    JOIN reference_phrases p
      ON d.normalized_doc_text LIKE '%' || replace(p.phrase, ' ', '%') || '%'
    GROUP BY
        d.tenant_id,
        d.doc_id,
        d.doc_path,
        d.doc_name,
        d.category,
        d.indexed_version,
        d.content_hash,
        d.file_size,
        d.file_mtime,
        d.revision_id,
        d.normalized_doc_text
)
SELECT
    d.doc_id AS "DocId",
    d.doc_path AS "DocPath",
    d.doc_name AS "DocName",
    d.category AS "Category",
    e.document_profile_id AS "ProfileId",
    d.indexed_version AS "IngestionVersion",
    LOWER(ENCODE(d.content_hash, 'hex')) AS "HashDoc",
    COALESCE(NULLIF(e.summary_text, ''), NULLIF(s.summary_text, ''), d.doc_name, d.doc_path) AS "Text",
    TRIM(BOTH FROM CONCAT_WS(
        ' ',
        d.doc_path,
        d.doc_name,
        e.search_text,
        e.summary_text,
        ARRAY_TO_STRING(e.keywords, ' '),
        ARRAY_TO_STRING(e.entities, ' '),
        ARRAY_TO_STRING(e.topics, ' '),
        ARRAY_TO_STRING(e.hypothetical_questions, ' '),
        ARRAY_TO_STRING(e.limits, ' '),
        NULLIF(s.summary_text, ''))) AS "SearchText",
    e.metadata_json::text AS "MetadataJson",
    e.language AS "Language",
    e.profile_version AS "ProfileVersion",
    e.keywords AS "Keywords",
    e.entities AS "Entities",
    e.topics AS "Topics",
    e.hypothetical_questions AS "HypotheticalQuestions",
    e.limits AS "Limits",
    (0.66::double precision
        + LEAST(d.phrase_match_count, 3)::double precision * 0.04::double precision
        + LEAST(d.phrase_match_length, 40)::double precision * 0.002::double precision) AS "SparseRank",
    d.phrase_match_count::bigint AS "MatchCount"
FROM matched_docs d
JOIN document_profile_search_entries e
  ON e.tenant_id = d.tenant_id
 AND e.revision_id = d.revision_id
LEFT JOIN document_summaries s
  ON s.tenant_id = d.tenant_id
 AND s.doc_id = d.doc_id
 AND s.level = 'medium'
 AND s.source_hash = saaia_document_summary_source_hash(d.content_hash, d.doc_path, d.file_size, d.file_mtime, d.indexed_version)
ORDER BY
    d.phrase_match_count DESC,
    d.phrase_match_length DESC,
    d.doc_path ASC
LIMIT @top_k;
""";

        try
        {
            var rows = (await conn.QueryAsync<DocumentProfileMatchRow>(new CommandDefinition(sql, new
            {
                tenant_id = tenantId,
                phrases,
                category,
                category_path = normalizedCategoryPath,
                doc_id = normalizedDocId,
                doc_path = normalizedDocPath,
                top_k = topK
            }, commandTimeout: commandTimeout, cancellationToken: ct))).ToList();

            return RankDocumentProfileRows(query, rows)
                .Take(topK)
                .Select(scored =>
                {
                    var row = scored.Row;
                    var pageRange = ResolveDocumentProfileMatchPageRange(row.MetadataJson, query);
                    var matchedContentCards = BuildMatchedContentCards(row.MetadataJson, query);
                    var profileSectionTitle = BuildDocumentProfileSectionTitle(row.Language);
                    return new RagMatch(
                        Score: Math.Max(scored.Score, scored.Row.SparseRank),
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
                        EmbeddingBasis: "standard_reference_document_name_v1",
                        SectionOrdinal: null,
                        UnitOrdinal: null,
                        SectionTitle: profileSectionTitle,
                        HeadingPath: profileSectionTitle,
                        ChunkType: "document_profile",
                        PrevChunkId: null,
                        NextChunkId: null,
                        SameSectionChunkId: null,
                        MatchedContentCards: matchedContentCards.Count == 0 ? null : matchedContentCards,
                        ProfileSignals: BuildDocumentProfileSignals(row, query),
                        ContentRole: RetrievalContentClassifier.ContentRole);
                })
                .ToList();
        }
        catch (Exception ex) when (ex is PostgresException or InvalidOperationException || IsRetrieverDatabaseTimeout(ex))
        {
            RetrievalTelemetry.RecordRetrieverDegraded("standard_reference_document_name_profile", ex);
            degradedRetrieverRef?.Invoke("standard_reference_document_name_profile", FormatRetrieverError(ex));
            return [];
        }
    }


    internal static async Task<List<RagMatch>> SearchDocumentStatusOperandContentMatchesAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        string query,
        IReadOnlyList<string> statusOperandTerms,
        string? category,
        string? docId,
        string? docPath,
        int topK,
        CancellationToken ct,
        string? categoryPath = null,
        Action<string, string?>? degradedRetrieverRef = null)
    {
        if (topK <= 0 || statusOperandTerms.Count == 0)
            return [];

        var terms = statusOperandTerms
            .Select(ExactMatchEntryExtractor.NormalizeForLookup)
            .Where(static term => term.Length >= 2)
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        if (terms.Length == 0)
            return [];

        var normalizedDocPath = string.IsNullOrWhiteSpace(docPath)
            ? null
            : docPath.Trim().Replace('\\', '/').TrimStart('/');
        var normalizedCategoryPath = NormalizeRagCategoryPathForSql(categoryPath);
        Guid? normalizedDocId = Guid.TryParse(docId, out var parsedDocId) ? parsedDocId : null;
        var topicTerms = ExtractDocumentStatusOperandQueryTopicTokens(query, statusOperandTerms)
            .Take(12)
            .ToArray();

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
WITH status_terms AS (
    SELECT DISTINCT LOWER(term) AS term
    FROM unnest(@terms::text[]) AS term
    WHERE term IS NOT NULL AND length(term) >= 2
),
topic_terms AS (
    SELECT DISTINCT LOWER(term) AS term
    FROM unnest(@topic_terms::text[]) AS term
    WHERE term IS NOT NULL AND length(term) >= 4
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
        r.revision_id,
        regexp_replace(LOWER(d.doc_path || ' ' || d.doc_name), '[^a-z0-9]+', ' ', 'g') AS normalized_doc_text,
        substring(
            regexp_replace(LOWER(d.doc_path || ' ' || d.doc_name), '[^a-z0-9]+', ' ', 'g')
            from '((en|iso|iec|astm|din|nfpa|api|ansi|ul|cen|tr|ts|pd|bs|nist|sp) (sp )?[0-9]{2,6}( [0-9]{1,4}[a-z0-9]*)?)') AS family_key
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
doc_topic_scores AS (
    SELECT
        d.doc_id,
        COUNT(DISTINCT t.term) AS doc_topic_hit_count
    FROM scoped_docs d
    JOIN topic_terms t
      ON (' ' || d.normalized_doc_text || ' ') LIKE '% ' || t.term || ' %'
      OR (length(t.term) >= 4 AND (' ' || d.normalized_doc_text || ' ') LIKE '% ' || t.term || 's %')
      OR (
          length(t.term) >= 5
          AND right(t.term, 1) = 's'
          AND (' ' || d.normalized_doc_text || ' ') LIKE '% ' || left(t.term, length(t.term) - 1) || ' %'
      )
    GROUP BY d.doc_id
),
family_topic_scores AS (
    SELECT
        d.family_key,
        MAX(COALESCE(s.doc_topic_hit_count, 0)) AS family_topic_hit_count
    FROM scoped_docs d
    LEFT JOIN doc_topic_scores s
      ON s.doc_id = d.doc_id
    WHERE d.family_key IS NOT NULL AND length(d.family_key) >= 4
    GROUP BY d.family_key
),
status_doc_terms AS (
    SELECT
        d.tenant_id,
        d.doc_id,
        d.doc_path,
        d.doc_name,
        d.category,
        d.indexed_version,
        d.content_hash,
        d.revision_id,
        d.normalized_doc_text,
        t.term AS matched_term,
        length(t.term) AS matched_term_length,
        d.family_key
    FROM scoped_docs d
    JOIN status_terms t
      ON (' ' || d.normalized_doc_text || ' ') LIKE '% ' || replace(t.term, ' ', '%') || ' %'
      OR (
          t.term ~ '^[a-z]{1,4}[0-9]{1,4}[a-z]?$'
          AND d.normalized_doc_text ~ ('(^| )[a-z0-9]{1,12}' || regexp_replace(t.term, '[^a-z0-9]', '', 'g') || '( |$)')
      )
),
status_docs AS (
    SELECT
        d.tenant_id,
        d.doc_id,
        d.doc_path,
        d.doc_name,
        d.category,
        d.indexed_version,
        d.content_hash,
        d.revision_id,
        d.normalized_doc_text,
        COUNT(DISTINCT d.matched_term) AS status_match_count,
        MAX(d.matched_term_length) AS status_match_length,
        MIN(d.matched_term) AS representative_status_term,
        MAX(d.family_key) AS family_key,
        MAX(COALESCE(fts.family_topic_hit_count, 0)) AS family_topic_hit_count
    FROM status_doc_terms d
    LEFT JOIN family_topic_scores fts
      ON fts.family_key = d.family_key
    GROUP BY
        d.tenant_id,
        d.doc_id,
        d.doc_path,
        d.doc_name,
        d.category,
        d.indexed_version,
        d.content_hash,
        d.revision_id,
        d.normalized_doc_text
),
family_keys AS (
    SELECT DISTINCT family_key
    FROM status_docs
    WHERE family_key IS NOT NULL AND length(family_key) >= 4
),
matched_docs AS (
    SELECT
        d.tenant_id,
        d.doc_id,
        d.doc_path,
        d.doc_name,
        d.category,
        d.indexed_version,
        d.content_hash,
        d.revision_id,
        d.normalized_doc_text,
        TRUE AS direct_status_match,
        d.status_match_count,
        d.status_match_length,
        d.representative_status_term AS matched_term,
        d.family_key,
        d.family_topic_hit_count
    FROM status_docs d
    UNION ALL
    SELECT
        d.tenant_id,
        d.doc_id,
        d.doc_path,
        d.doc_name,
        d.category,
        d.indexed_version,
        d.content_hash,
        d.revision_id,
        d.normalized_doc_text,
        FALSE AS direct_status_match,
        0::bigint AS status_match_count,
        0::int AS status_match_length,
        NULL::text AS matched_term,
        f.family_key,
        COALESCE(fts.family_topic_hit_count, 0) AS family_topic_hit_count
    FROM scoped_docs d
    JOIN family_keys f
      ON d.normalized_doc_text LIKE '%' || replace(f.family_key, ' ', '%') || '%'
    LEFT JOIN family_topic_scores fts
      ON fts.family_key = f.family_key
    WHERE NOT EXISTS (
        SELECT 1
        FROM status_docs sd
        WHERE sd.doc_id = d.doc_id
    )
),
ranked_chunks AS (
    SELECT
        d.*,
        COALESCE(substring(d.normalized_doc_text from '((19|20)[0-9]{2})')::int, 0) AS document_year,
        rc.page_start,
        rc.page_end,
        CASE
            WHEN NULLIF(rc.metadata->>'offsetStart', '') ~ '^[-+]?[0-9]+$'
                THEN CASE
                    WHEN (rc.metadata->>'offsetStart')::numeric BETWEEN -2147483648 AND 2147483647
                        THEN (rc.metadata->>'offsetStart')::int
                    ELSE NULL
                END
            ELSE NULL
        END AS offset_start,
        CASE
            WHEN NULLIF(rc.metadata->>'offsetEnd', '') ~ '^[-+]?[0-9]+$'
                THEN CASE
                    WHEN (rc.metadata->>'offsetEnd')::numeric BETWEEN -2147483648 AND 2147483647
                        THEN (rc.metadata->>'offsetEnd')::int
                    ELSE NULL
                END
            ELSE NULL
        END AS offset_end,
        rc.retrieval_chunk_id,
        rc.chunk_index,
        cte.text_content,
        COALESCE(rc.metadata->>'sectionTitle', s.title) AS section_title,
        COALESCE(rc.metadata->>'headingPath', s.title) AS heading_path,
        COALESCE(rc.metadata->>'chunkType', 'contextual_text_v1') AS chunk_type,
        COALESCE(rc.metadata->>'contentRole', 'content') AS content_role,
        rc.metadata->>'navigationReason' AS navigation_reason,
        rc.metadata->>'originalChunkType' AS original_chunk_type,
        CASE
            WHEN NULLIF(rc.metadata->>'navigationScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
                THEN (rc.metadata->>'navigationScore')::double precision
            ELSE NULL
        END AS navigation_score,
        CASE
            WHEN NULLIF(rc.metadata->>'contentDensityScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
                THEN (rc.metadata->>'contentDensityScore')::double precision
            ELSE NULL
        END AS content_density_score,
        rc.metadata->>'prevChunkId' AS prev_chunk_id,
        rc.metadata->>'nextChunkId' AS next_chunk_id,
        rc.metadata->>'sameSectionChunkId' AS same_section_chunk_id,
        rc.metadata->>'extractionTextStatus' AS extraction_text_status,
        CASE
            WHEN LOWER(COALESCE(rc.metadata->>'extractionTextSparse', '')) IN ('true', 'false')
                THEN (rc.metadata->>'extractionTextSparse')::boolean
            ELSE NULL
        END AS extraction_text_sparse,
        CASE
            WHEN LOWER(COALESCE(rc.metadata->>'extractionOcrCandidate', '')) IN ('true', 'false')
                THEN (rc.metadata->>'extractionOcrCandidate')::boolean
            ELSE NULL
        END AS extraction_ocr_candidate,
        CASE
            WHEN jsonb_typeof(rc.metadata->'extractionQualitySignals') = 'array'
                THEN (rc.metadata->'extractionQualitySignals')::text
            ELSE NULL
        END AS extraction_quality_signals_json,
        ROW_NUMBER() OVER (
            PARTITION BY COALESCE(d.matched_term, '__family__'), d.doc_id
            ORDER BY
                CASE COALESCE(rc.metadata->>'contentRole', 'content')
                    WHEN 'content' THEN 0
                    WHEN 'mixed_navigation_content' THEN 1
                    ELSE 2
                END,
                CASE WHEN COALESCE(rc.metadata->>'chunkType', '') = 'unit_exact_v1' THEN 0 ELSE 1 END,
                GREATEST(1, COALESCE(rc.page_start, 1)),
                rc.chunk_index
        ) AS doc_chunk_rank
    FROM matched_docs d
    JOIN retrieval_chunks rc
      ON rc.tenant_id = d.tenant_id
     AND rc.revision_id = d.revision_id
    JOIN contextual_text_entries cte
      ON cte.tenant_id = rc.tenant_id
     AND cte.revision_id = rc.revision_id
     AND cte.retrieval_chunk_id = rc.retrieval_chunk_id
    LEFT JOIN document_sections s
      ON s.section_id = rc.section_id
    WHERE length(cte.text_content) >= 80
      AND COALESCE(rc.metadata->>'contentRole', 'content') <> 'navigation'
      AND COALESCE(rc.metadata->>'chunkType', '') <> 'navigation_index_v1'
),
term_ranked_chunks AS (
    SELECT
        ranked_chunks.*,
        ROW_NUMBER() OVER (
            PARTITION BY matched_term
            ORDER BY
                family_topic_hit_count DESC,
                direct_status_match DESC,
                status_match_count DESC,
                status_match_length DESC,
                document_year DESC,
                doc_path ASC,
                doc_chunk_rank ASC
        ) AS matched_term_rank
    FROM ranked_chunks
    WHERE doc_chunk_rank <= 2
)
SELECT
    doc_id AS "DocId",
    doc_path AS "DocPath",
    doc_name AS "DocName",
    category AS "Category",
    page_start AS "PageStart",
    page_end AS "PageEnd",
    offset_start AS "OffsetStart",
    offset_end AS "OffsetEnd",
    retrieval_chunk_id AS "ChunkId",
    chunk_index AS "ChunkIndex",
    text_content AS "Text",
    indexed_version AS "IngestionVersion",
    LOWER(ENCODE(content_hash, 'hex')) AS "HashDoc",
    doc_name || E'\n' || text_content AS "EmbedText",
    NULL::uuid AS "SectionOrdinalPlaceholder",
    section_title AS "SectionTitle",
    heading_path AS "HeadingPath",
    chunk_type AS "ChunkType",
    content_role AS "ContentRole",
    navigation_reason AS "NavigationReason",
    original_chunk_type AS "OriginalChunkType",
    navigation_score AS "NavigationScore",
    content_density_score AS "ContentDensityScore",
    prev_chunk_id AS "PrevChunkId",
    next_chunk_id AS "NextChunkId",
    same_section_chunk_id AS "SameSectionChunkId",
    extraction_text_status AS "ExtractionTextStatus",
    extraction_text_sparse AS "ExtractionTextSparse",
    extraction_ocr_candidate AS "ExtractionOcrCandidate",
    extraction_quality_signals_json AS "ExtractionQualitySignalsJson",
    NULL::text AS "MatchedContentCardsJson",
    (CASE WHEN direct_status_match THEN 0.86 ELSE 0.72 END
        + LEAST(status_match_count, 3)::real * 0.04
        + LEAST(status_match_length, 24)::real * 0.002
        + LEAST(family_topic_hit_count, 3)::real * 0.06)::real AS "SparseRank"
FROM term_ranked_chunks
ORDER BY
    CASE WHEN matched_term IS NOT NULL AND matched_term_rank = 1 THEN 0 ELSE 1 END,
    family_topic_hit_count DESC,
    direct_status_match DESC,
    status_match_count DESC,
    status_match_length DESC,
    document_year DESC,
    doc_path ASC,
    doc_chunk_rank ASC
LIMIT @top_k;
""";

        try
        {
            var rows = (await conn.QueryAsync<SparseMatchRow>(new CommandDefinition(sql, new
            {
                tenant_id = tenantId,
                terms,
                topic_terms = topicTerms,
                category,
                category_path = normalizedCategoryPath,
                doc_id = normalizedDocId,
                doc_path = normalizedDocPath,
                top_k = topK
            }, commandTimeout: 90, cancellationToken: ct))).ToList();

            return BuildSparseRagMatches(rows, query)
                .Select(static match => match with { EmbeddingBasis = "document_status_operand_v1" })
                .ToList();
        }
        catch (Exception ex) when (ex is PostgresException or InvalidOperationException || IsRetrieverDatabaseTimeout(ex))
        {
            RetrievalTelemetry.RecordRetrieverDegraded("document_status_operand", ex);
            degradedRetrieverRef?.Invoke("document_status_operand", FormatRetrieverError(ex));
            return [];
        }
    }


    internal static async Task<List<RagMatch>> SearchReferenceVersionOperandDocumentNameContentMatchesAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        string query,
        IReadOnlyList<string> referenceGroupKeys,
        string? category,
        string? docId,
        string? docPath,
        int topK,
        CancellationToken ct,
        string? categoryPath = null,
        Action<string, string?>? degradedRetrieverRef = null)
    {
        if (topK <= 0 || referenceGroupKeys.Count == 0)
            return [];

        var groupKeys = referenceGroupKeys
            .Select(ExactMatchEntryExtractor.NormalizeForLookup)
            .Where(LooksLikeStandardReferenceTitleLookupPhrase)
            .Distinct(StringComparer.Ordinal)
            .Take(6)
            .ToArray();
        var operands = ExtractReferenceVersionComparisonOperands(query)
            .Select(ExactMatchEntryExtractor.NormalizeForLookup)
            .Where(static operand => !string.IsNullOrWhiteSpace(operand))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        if (groupKeys.Length == 0 || operands.Length < 2)
            return [];

        var normalizedDocPath = string.IsNullOrWhiteSpace(docPath)
            ? null
            : docPath.Trim().Replace('\\', '/').TrimStart('/');
        var normalizedCategoryPath = NormalizeRagCategoryPathForSql(categoryPath);
        Guid? normalizedDocId = Guid.TryParse(docId, out var parsedDocId) ? parsedDocId : null;

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
WITH reference_groups AS (
    SELECT DISTINCT LOWER(group_key) AS group_key
    FROM unnest(@group_keys::text[]) AS group_key
    WHERE group_key IS NOT NULL AND length(group_key) >= 4
),
version_operands AS (
    SELECT DISTINCT LOWER(operand) AS operand
    FROM unnest(@operands::text[]) AS operand
    WHERE operand IS NOT NULL AND length(operand) >= 2
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
        r.revision_id,
        regexp_replace(LOWER(d.doc_path || ' ' || d.doc_name), '[^a-z0-9]+', ' ', 'g') AS normalized_doc_text
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
matched_docs AS (
    SELECT
        d.*,
        COUNT(DISTINCT g.group_key) AS group_match_count,
        MAX(length(g.group_key)) AS group_match_length,
        COUNT(DISTINCT o.operand) FILTER (
            WHERE d.normalized_doc_text LIKE '%' || replace(o.operand, ' ', '%') || '%'
        ) AS operand_match_count
    FROM scoped_docs d
    JOIN reference_groups g
      ON d.normalized_doc_text LIKE '%' || replace(g.group_key, ' ', '%') || '%'
    LEFT JOIN version_operands o
      ON d.normalized_doc_text LIKE '%' || replace(o.operand, ' ', '%') || '%'
    GROUP BY
        d.tenant_id,
        d.doc_id,
        d.doc_path,
        d.doc_name,
        d.category,
        d.indexed_version,
        d.content_hash,
        d.revision_id,
        d.normalized_doc_text
    HAVING COUNT(DISTINCT o.operand) FILTER (
        WHERE d.normalized_doc_text LIKE '%' || replace(o.operand, ' ', '%') || '%'
    ) > 0
),
ranked_chunks AS (
    SELECT
        d.*,
        rc.page_start,
        rc.page_end,
        CASE
            WHEN NULLIF(rc.metadata->>'offsetStart', '') ~ '^[-+]?[0-9]+$'
                THEN CASE
                    WHEN (rc.metadata->>'offsetStart')::numeric BETWEEN -2147483648 AND 2147483647
                        THEN (rc.metadata->>'offsetStart')::int
                    ELSE NULL
                END
            ELSE NULL
        END AS offset_start,
        CASE
            WHEN NULLIF(rc.metadata->>'offsetEnd', '') ~ '^[-+]?[0-9]+$'
                THEN CASE
                    WHEN (rc.metadata->>'offsetEnd')::numeric BETWEEN -2147483648 AND 2147483647
                        THEN (rc.metadata->>'offsetEnd')::int
                    ELSE NULL
                END
            ELSE NULL
        END AS offset_end,
        rc.retrieval_chunk_id,
        rc.chunk_index,
        cte.text_content,
        COALESCE(rc.metadata->>'sectionTitle', s.title) AS section_title,
        COALESCE(rc.metadata->>'headingPath', s.title) AS heading_path,
        COALESCE(rc.metadata->>'chunkType', 'contextual_text_v1') AS chunk_type,
        COALESCE(rc.metadata->>'contentRole', 'content') AS content_role,
        rc.metadata->>'navigationReason' AS navigation_reason,
        rc.metadata->>'originalChunkType' AS original_chunk_type,
        CASE
            WHEN NULLIF(rc.metadata->>'navigationScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
                THEN (rc.metadata->>'navigationScore')::double precision
            ELSE NULL
        END AS navigation_score,
        CASE
            WHEN NULLIF(rc.metadata->>'contentDensityScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
                THEN (rc.metadata->>'contentDensityScore')::double precision
            ELSE NULL
        END AS content_density_score,
        rc.metadata->>'prevChunkId' AS prev_chunk_id,
        rc.metadata->>'nextChunkId' AS next_chunk_id,
        rc.metadata->>'sameSectionChunkId' AS same_section_chunk_id,
        rc.metadata->>'extractionTextStatus' AS extraction_text_status,
        CASE
            WHEN LOWER(COALESCE(rc.metadata->>'extractionTextSparse', '')) IN ('true', 'false')
                THEN (rc.metadata->>'extractionTextSparse')::boolean
            ELSE NULL
        END AS extraction_text_sparse,
        CASE
            WHEN LOWER(COALESCE(rc.metadata->>'extractionOcrCandidate', '')) IN ('true', 'false')
                THEN (rc.metadata->>'extractionOcrCandidate')::boolean
            ELSE NULL
        END AS extraction_ocr_candidate,
        CASE
            WHEN jsonb_typeof(rc.metadata->'extractionQualitySignals') = 'array'
                THEN (rc.metadata->'extractionQualitySignals')::text
            ELSE NULL
        END AS extraction_quality_signals_json,
        ROW_NUMBER() OVER (
            PARTITION BY d.doc_id
            ORDER BY
                CASE COALESCE(rc.metadata->>'contentRole', 'content')
                    WHEN 'content' THEN 0
                    WHEN 'mixed_navigation_content' THEN 1
                    ELSE 2
                END,
                CASE WHEN COALESCE(rc.metadata->>'chunkType', '') = 'unit_exact_v1' THEN 0 ELSE 1 END,
                GREATEST(1, COALESCE(rc.page_start, 1)),
                rc.chunk_index
        ) AS doc_chunk_rank
    FROM matched_docs d
    JOIN retrieval_chunks rc
      ON rc.tenant_id = d.tenant_id
     AND rc.revision_id = d.revision_id
    JOIN contextual_text_entries cte
      ON cte.tenant_id = rc.tenant_id
     AND cte.revision_id = rc.revision_id
     AND cte.retrieval_chunk_id = rc.retrieval_chunk_id
    LEFT JOIN document_sections s
      ON s.section_id = rc.section_id
    WHERE length(cte.text_content) >= 80
      AND COALESCE(rc.metadata->>'contentRole', 'content') <> 'navigation'
      AND COALESCE(rc.metadata->>'chunkType', '') <> 'navigation_index_v1'
)
SELECT
    doc_id AS "DocId",
    doc_path AS "DocPath",
    doc_name AS "DocName",
    category AS "Category",
    page_start AS "PageStart",
    page_end AS "PageEnd",
    offset_start AS "OffsetStart",
    offset_end AS "OffsetEnd",
    retrieval_chunk_id AS "ChunkId",
    chunk_index AS "ChunkIndex",
    text_content AS "Text",
    indexed_version AS "IngestionVersion",
    LOWER(ENCODE(content_hash, 'hex')) AS "HashDoc",
    doc_name || E'\n' || text_content AS "EmbedText",
    NULL::uuid AS "SectionOrdinalPlaceholder",
    section_title AS "SectionTitle",
    heading_path AS "HeadingPath",
    chunk_type AS "ChunkType",
    content_role AS "ContentRole",
    navigation_reason AS "NavigationReason",
    original_chunk_type AS "OriginalChunkType",
    navigation_score AS "NavigationScore",
    content_density_score AS "ContentDensityScore",
    prev_chunk_id AS "PrevChunkId",
    next_chunk_id AS "NextChunkId",
    same_section_chunk_id AS "SameSectionChunkId",
    extraction_text_status AS "ExtractionTextStatus",
    extraction_text_sparse AS "ExtractionTextSparse",
    extraction_ocr_candidate AS "ExtractionOcrCandidate",
    extraction_quality_signals_json AS "ExtractionQualitySignalsJson",
    NULL::text AS "MatchedContentCardsJson",
    (0.66 + LEAST(operand_match_count, 4)::real * 0.045 + LEAST(group_match_length, 40)::real * 0.001)::real AS "SparseRank"
FROM ranked_chunks
WHERE doc_chunk_rank <= 2
ORDER BY
    operand_match_count DESC,
    group_match_count DESC,
    group_match_length DESC,
    doc_path ASC,
    doc_chunk_rank ASC
LIMIT @top_k;
""";

        try
        {
            var rows = (await conn.QueryAsync<SparseMatchRow>(new CommandDefinition(sql, new
            {
                tenant_id = tenantId,
                group_keys = groupKeys,
                operands,
                category,
                category_path = normalizedCategoryPath,
                doc_id = normalizedDocId,
                doc_path = normalizedDocPath,
                top_k = topK
            }, commandTimeout: 90, cancellationToken: ct))).ToList();

            return BuildSparseRagMatches(rows, query)
                .Select(static match => match with { EmbeddingBasis = "reference_version_operand_document_name_v1" })
                .ToList();
        }
        catch (Exception ex) when (ex is PostgresException or InvalidOperationException || IsRetrieverDatabaseTimeout(ex))
        {
            RetrievalTelemetry.RecordRetrieverDegraded("reference_version_operand_document_name", ex);
            degradedRetrieverRef?.Invoke("reference_version_operand_document_name", FormatRetrieverError(ex));
            return [];
        }
    }


    internal static async Task<List<RagMatch>> SearchDateAnchoredDocumentNameContentMatchesAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        string query,
        IReadOnlyList<string> dateAnchoredPhrases,
        string? category,
        string? docId,
        string? docPath,
        int topK,
        CancellationToken ct,
        string? categoryPath = null,
        Action<string, string?>? degradedRetrieverRef = null)
    {
        if (topK <= 0 || dateAnchoredPhrases.Count == 0)
            return [];

        var phrases = dateAnchoredPhrases
            .Select(ExactMatchEntryExtractor.NormalizeForLookup)
            .Where(static phrase => phrase.Any(char.IsDigit) && phrase.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToArray();
        if (phrases.Length == 0)
            return [];

        var normalizedDocPath = string.IsNullOrWhiteSpace(docPath)
            ? null
            : docPath.Trim().Replace('\\', '/').TrimStart('/');
        var normalizedCategoryPath = NormalizeRagCategoryPathForSql(categoryPath);
        Guid? normalizedDocId = Guid.TryParse(docId, out var parsedDocId) ? parsedDocId : null;

        await using var conn = await ds.OpenConnectionAsync(ct);
        const string sql = """
WITH date_phrases AS (
    SELECT DISTINCT LOWER(phrase) AS phrase
    FROM unnest(@phrases::text[]) AS phrase
    WHERE phrase IS NOT NULL AND length(phrase) >= 4
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
        r.revision_id,
        regexp_replace(LOWER(d.doc_path || ' ' || d.doc_name), '[^a-z0-9]+', ' ', 'g') AS normalized_doc_text
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
matched_docs AS (
    SELECT
        d.*,
        COUNT(*) AS phrase_match_count,
        MAX(length(p.phrase)) AS phrase_match_length
    FROM scoped_docs d
    JOIN date_phrases p
      ON d.normalized_doc_text LIKE '%' || replace(p.phrase, ' ', '%') || '%'
    GROUP BY
        d.tenant_id,
        d.doc_id,
        d.doc_path,
        d.doc_name,
        d.category,
        d.indexed_version,
        d.content_hash,
        d.revision_id,
        d.normalized_doc_text
),
ranked_chunks AS (
    SELECT
        d.*,
        rc.page_start,
        rc.page_end,
        CASE
            WHEN NULLIF(rc.metadata->>'offsetStart', '') ~ '^[-+]?[0-9]+$'
                THEN CASE
                    WHEN (rc.metadata->>'offsetStart')::numeric BETWEEN -2147483648 AND 2147483647
                        THEN (rc.metadata->>'offsetStart')::int
                    ELSE NULL
                END
            ELSE NULL
        END AS offset_start,
        CASE
            WHEN NULLIF(rc.metadata->>'offsetEnd', '') ~ '^[-+]?[0-9]+$'
                THEN CASE
                    WHEN (rc.metadata->>'offsetEnd')::numeric BETWEEN -2147483648 AND 2147483647
                        THEN (rc.metadata->>'offsetEnd')::int
                    ELSE NULL
                END
            ELSE NULL
        END AS offset_end,
        rc.retrieval_chunk_id,
        rc.chunk_index,
        cte.text_content,
        COALESCE(rc.metadata->>'sectionTitle', s.title) AS section_title,
        COALESCE(rc.metadata->>'headingPath', s.title) AS heading_path,
        COALESCE(rc.metadata->>'chunkType', 'contextual_text_v1') AS chunk_type,
        COALESCE(rc.metadata->>'contentRole', 'content') AS content_role,
        rc.metadata->>'navigationReason' AS navigation_reason,
        rc.metadata->>'originalChunkType' AS original_chunk_type,
        CASE
            WHEN NULLIF(rc.metadata->>'navigationScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
                THEN (rc.metadata->>'navigationScore')::double precision
            ELSE NULL
        END AS navigation_score,
        CASE
            WHEN NULLIF(rc.metadata->>'contentDensityScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
                THEN (rc.metadata->>'contentDensityScore')::double precision
            ELSE NULL
        END AS content_density_score,
        rc.metadata->>'prevChunkId' AS prev_chunk_id,
        rc.metadata->>'nextChunkId' AS next_chunk_id,
        rc.metadata->>'sameSectionChunkId' AS same_section_chunk_id,
        rc.metadata->>'extractionTextStatus' AS extraction_text_status,
        CASE
            WHEN LOWER(COALESCE(rc.metadata->>'extractionTextSparse', '')) IN ('true', 'false')
                THEN (rc.metadata->>'extractionTextSparse')::boolean
            ELSE NULL
        END AS extraction_text_sparse,
        CASE
            WHEN LOWER(COALESCE(rc.metadata->>'extractionOcrCandidate', '')) IN ('true', 'false')
                THEN (rc.metadata->>'extractionOcrCandidate')::boolean
            ELSE NULL
        END AS extraction_ocr_candidate,
        CASE
            WHEN jsonb_typeof(rc.metadata->'extractionQualitySignals') = 'array'
                THEN (rc.metadata->'extractionQualitySignals')::text
            ELSE NULL
        END AS extraction_quality_signals_json,
        ROW_NUMBER() OVER (
            PARTITION BY d.doc_id
            ORDER BY
                CASE COALESCE(rc.metadata->>'contentRole', 'content')
                    WHEN 'content' THEN 0
                    WHEN 'mixed_navigation_content' THEN 1
                    ELSE 2
                END,
                CASE WHEN COALESCE(rc.metadata->>'chunkType', '') = 'unit_exact_v1' THEN 0 ELSE 1 END,
                GREATEST(1, COALESCE(rc.page_start, 1)),
                rc.chunk_index
        ) AS doc_chunk_rank
    FROM matched_docs d
    JOIN retrieval_chunks rc
      ON rc.tenant_id = d.tenant_id
     AND rc.revision_id = d.revision_id
    JOIN contextual_text_entries cte
      ON cte.tenant_id = rc.tenant_id
     AND cte.revision_id = rc.revision_id
     AND cte.retrieval_chunk_id = rc.retrieval_chunk_id
    LEFT JOIN document_sections s
      ON s.section_id = rc.section_id
    WHERE length(cte.text_content) >= 80
      AND COALESCE(rc.metadata->>'contentRole', 'content') <> 'navigation'
      AND COALESCE(rc.metadata->>'chunkType', '') <> 'navigation_index_v1'
)
SELECT
    doc_id AS "DocId",
    doc_path AS "DocPath",
    doc_name AS "DocName",
    category AS "Category",
    page_start AS "PageStart",
    page_end AS "PageEnd",
    offset_start AS "OffsetStart",
    offset_end AS "OffsetEnd",
    retrieval_chunk_id AS "ChunkId",
    chunk_index AS "ChunkIndex",
    text_content AS "Text",
    indexed_version AS "IngestionVersion",
    LOWER(ENCODE(content_hash, 'hex')) AS "HashDoc",
    doc_name || E'\n' || text_content AS "EmbedText",
    NULL::uuid AS "SectionOrdinalPlaceholder",
    section_title AS "SectionTitle",
    heading_path AS "HeadingPath",
    chunk_type AS "ChunkType",
    content_role AS "ContentRole",
    navigation_reason AS "NavigationReason",
    original_chunk_type AS "OriginalChunkType",
    navigation_score AS "NavigationScore",
    content_density_score AS "ContentDensityScore",
    prev_chunk_id AS "PrevChunkId",
    next_chunk_id AS "NextChunkId",
    same_section_chunk_id AS "SameSectionChunkId",
    extraction_text_status AS "ExtractionTextStatus",
    extraction_text_sparse AS "ExtractionTextSparse",
    extraction_ocr_candidate AS "ExtractionOcrCandidate",
    extraction_quality_signals_json AS "ExtractionQualitySignalsJson",
    NULL::text AS "MatchedContentCardsJson",
    (0.70 + LEAST(phrase_match_count, 4)::real * 0.04 + LEAST(phrase_match_length, 40)::real * 0.002)::real AS "SparseRank"
FROM ranked_chunks
WHERE doc_chunk_rank <= 3
ORDER BY
    phrase_match_count DESC,
    phrase_match_length DESC,
    doc_path ASC,
    doc_chunk_rank ASC
LIMIT @top_k;
""";

        try
        {
            var rows = (await conn.QueryAsync<SparseMatchRow>(new CommandDefinition(sql, new
            {
                tenant_id = tenantId,
                phrases,
                category,
                category_path = normalizedCategoryPath,
                doc_id = normalizedDocId,
                doc_path = normalizedDocPath,
                top_k = topK
            }, commandTimeout: 90, cancellationToken: ct))).ToList();

            return BuildSparseRagMatches(rows, query)
                .Select(static match => match with { EmbeddingBasis = "date_document_name_v1" })
                .ToList();
        }
        catch (Exception ex) when (ex is PostgresException or InvalidOperationException || IsRetrieverDatabaseTimeout(ex))
        {
            RetrievalTelemetry.RecordRetrieverDegraded("date_document_name", ex);
            degradedRetrieverRef?.Invoke("date_document_name", FormatRetrieverError(ex));
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
        Action<string, string?>? degradedRetrieverRef = null,
        int commandTimeoutSeconds = RagOptions.DefaultSearchSparseCommandTimeoutSeconds)
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
        var commandTimeout = RagOptions.ResolveSearchSparseCommandTimeoutSeconds(commandTimeoutSeconds);

        await using var conn = await ds.OpenConnectionAsync(ct);
        await EnsureFreshDocumentProfileSearchEntriesAsync(
            conn,
            tenantId,
            category,
            normalizedCategoryPath,
            normalizedDocId,
            normalizedDocPath,
            commandTimeout,
            ct,
            degradedRetrieverRef);

        const string sql = """
WITH sparse_query AS (
    SELECT websearch_to_tsquery('simple', @query_text) AS q
),
lexical_terms AS (
    SELECT DISTINCT LOWER(term) AS term
    FROM unnest(@lexical_terms::text[]) AS term
    WHERE term IS NOT NULL AND term <> ''
),
scoped_entries AS (
    SELECT
        d.doc_id,
        d.doc_path,
        d.doc_name,
        d.category,
        d.indexed_version,
        d.content_hash,
        d.updated_at,
        e.document_profile_id,
        e.summary_text,
        e.search_text,
        e.search_tsv,
        e.metadata_json,
        e.language,
        e.profile_version,
        e.keywords,
        e.entities,
        e.topics,
        e.hypothetical_questions,
        e.limits
    FROM documents d
    JOIN document_revisions r
      ON r.tenant_id = d.tenant_id
     AND r.doc_id = d.doc_id
     AND r.indexed_version = d.indexed_version
    JOIN document_profile_search_entries e
      ON e.tenant_id = r.tenant_id
     AND e.revision_id = r.revision_id
    WHERE d.tenant_id = @tenant_id
      AND d.status = 'indexed'
      AND d.indexed_version > 0
      AND (@category IS NULL OR LOWER(d.category) = @category)
      AND (@category_path IS NULL OR d.doc_path = @category_path OR d.doc_path LIKE (@category_path || '/%'))
      AND (@doc_id IS NULL OR d.doc_id = @doc_id)
      AND (@doc_path IS NULL OR d.doc_path = @doc_path)
)
SELECT
    se.doc_id AS "DocId",
    se.doc_path AS "DocPath",
    se.doc_name AS "DocName",
    se.category AS "Category",
    se.document_profile_id AS "ProfileId",
    se.indexed_version AS "IngestionVersion",
    LOWER(ENCODE(se.content_hash, 'hex')) AS "HashDoc",
    COALESCE(NULLIF(se.summary_text, ''), se.doc_name, se.doc_path) AS "Text",
    se.search_text AS "SearchText",
    se.metadata_json::text AS "MetadataJson",
    se.language AS "Language",
    se.profile_version AS "ProfileVersion",
    se.keywords AS "Keywords",
    se.entities AS "Entities",
    se.topics AS "Topics",
    se.hypothetical_questions AS "HypotheticalQuestions",
    se.limits AS "Limits",
    ts_rank_cd(
        se.search_tsv,
        sparse_query.q,
        32
    ) + (lm.match_weight::real * 0.04) AS "SparseRank",
    lm.match_count AS "MatchCount"
FROM sparse_query
JOIN scoped_entries se ON TRUE
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
            THEN LOWER(se.search_text) LIKE '%' || REPLACE(lexical_terms.term, ' ', '%') || '%'
        ELSE LOWER(se.search_text) LIKE '%' || lexical_terms.term || '%'
    END
) lm
WHERE (
       se.search_tsv @@ sparse_query.q
       OR lm.match_count > 0
      )
ORDER BY
    (ts_rank_cd(se.search_tsv, sparse_query.q, 32)
        + (lm.match_weight::real * 0.04)) DESC,
    se.updated_at DESC
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
            }, commandTimeout: commandTimeout, cancellationToken: ct))).ToList();

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
                    MatchedContentCards: matchedContentCards.Count == 0 ? null : matchedContentCards,
                    ProfileSignals: BuildDocumentProfileSignals(row, query));
            }).ToList();
        }
        catch (Exception ex) when (ex is PostgresException or InvalidOperationException || IsRetrieverDatabaseTimeout(ex))
        {
            RetrievalTelemetry.RecordRetrieverDegraded("document_profile_v1", ex);
            degradedRetrieverRef?.Invoke("document_profile_v1", FormatRetrieverError(ex));
            return [];
        }
    }


    private const int DocumentProfileProjectionRepairLimit = 8;


    private static async Task EnsureFreshDocumentProfileSearchEntriesAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string? category,
        string? normalizedCategoryPath,
        Guid? docId,
        string? normalizedDocPath,
        int commandTimeoutSeconds,
        CancellationToken ct,
        Action<string, string?>? degradedRetrieverRef)
    {
        if (docId is null && string.IsNullOrWhiteSpace(normalizedDocPath))
            return;

        const string sql = """
WITH scoped_revisions AS (
    SELECT
        r.tenant_id,
        r.revision_id
    FROM documents d
    JOIN document_revisions r
      ON r.tenant_id = d.tenant_id
     AND r.doc_id = d.doc_id
     AND r.indexed_version = d.indexed_version
    LEFT JOIN document_profile_search_entries e
      ON e.tenant_id = r.tenant_id
     AND e.revision_id = r.revision_id
    LEFT JOIN LATERAL (
        SELECT MAX(p.updated_at) AS latest_profile_update
        FROM document_profiles p
        WHERE p.tenant_id = r.tenant_id
          AND p.revision_id = r.revision_id
    ) profile_updates ON TRUE
    LEFT JOIN LATERAL (
        SELECT MAX(card.updated_at) AS latest_card_update
        FROM document_profile_content_cards card
        WHERE card.tenant_id = r.tenant_id
          AND card.revision_id = r.revision_id
    ) card_updates ON TRUE
    LEFT JOIN LATERAL (
        SELECT MAX(s.updated_at) AS latest_summary_update
        FROM document_summaries s
        WHERE s.tenant_id = d.tenant_id
          AND s.doc_id = d.doc_id
          AND s.level = 'medium'
          AND s.source_hash = saaia_document_summary_source_hash(
              d.content_hash,
              d.doc_path,
              d.file_size,
              d.file_mtime,
              d.indexed_version)
    ) summary_updates ON TRUE
    WHERE d.tenant_id = @tenant_id
      AND d.status = 'indexed'
      AND d.indexed_version > 0
      AND (@category IS NULL OR LOWER(d.category) = @category)
      AND (@category_path IS NULL OR d.doc_path = @category_path OR d.doc_path LIKE (@category_path || '/%'))
      AND (@doc_id IS NULL OR d.doc_id = @doc_id)
      AND (@doc_path IS NULL OR d.doc_path = @doc_path)
      AND profile_updates.latest_profile_update IS NOT NULL
      AND (
          e.revision_id IS NULL
          OR e.updated_at < GREATEST(
              COALESCE(profile_updates.latest_profile_update, 'epoch'::timestamptz),
              COALESCE(card_updates.latest_card_update, 'epoch'::timestamptz),
              COALESCE(summary_updates.latest_summary_update, 'epoch'::timestamptz)
          )
      )
    ORDER BY d.updated_at DESC
    LIMIT @repair_limit
)
SELECT saaia_refresh_document_profile_search_entry(tenant_id, revision_id)
FROM scoped_revisions;
""";

        try
        {
            var repairTimeout = Math.Clamp(commandTimeoutSeconds, 3, 15);
            await conn.ExecuteAsync(new CommandDefinition(sql, new
            {
                tenant_id = tenantId,
                category,
                category_path = normalizedCategoryPath,
                doc_id = docId,
                doc_path = normalizedDocPath,
                repair_limit = DocumentProfileProjectionRepairLimit
            }, commandTimeout: repairTimeout, cancellationToken: ct));
        }
        catch (Exception ex) when (ex is PostgresException or InvalidOperationException || IsRetrieverDatabaseTimeout(ex))
        {
            RetrievalTelemetry.RecordRetrieverDegraded("document_profile_projection_refresh_v1", ex);
            degradedRetrieverRef?.Invoke("document_profile_projection_refresh_v1", FormatRetrieverError(ex));
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


    private static RagProfileSignals? BuildDocumentProfileSignals(DocumentProfileMatchRow row, string query)
    {
        var queryTokens = ExtractLexicalQueryTokens(query);
        var lexicalTerms = BuildDocumentProfileLexicalTerms(query);
        var matchedTerms = SelectMatchedProfileTerms(lexicalTerms, row.SearchText, maxItems: 12);
        var keywords = SelectProfileSignalValues(ProfileSignalValues(row.Keywords), queryTokens, lexicalTerms, maxItems: 8);
        var entities = SelectProfileSignalValues(ProfileSignalValues(row.Entities), queryTokens, lexicalTerms, maxItems: 8);
        var topics = SelectProfileSignalValues(ProfileSignalValues(row.Topics), queryTokens, lexicalTerms, maxItems: 8, fallbackItems: 3);
        var questions = SelectProfileSignalValues(ProfileSignalValues(row.HypotheticalQuestions), queryTokens, lexicalTerms, maxItems: 4);
        var limits = SelectProfileSignalValues(ProfileSignalValues(row.Limits), queryTokens, lexicalTerms, maxItems: 4, fallbackItems: 2);

        if (string.IsNullOrWhiteSpace(row.ProfileVersion)
            && string.IsNullOrWhiteSpace(row.Language)
            && matchedTerms.Count == 0
            && keywords.Count == 0
            && entities.Count == 0
            && topics.Count == 0
            && questions.Count == 0
            && limits.Count == 0
            && row.MatchCount <= 0)
        {
            return null;
        }

        return new RagProfileSignals(
            ProfileVersion: row.ProfileVersion,
            Language: row.Language,
            Keywords: keywords,
            Entities: entities,
            Topics: topics,
            HypotheticalQuestions: questions,
            Limits: limits,
            MatchedTerms: matchedTerms,
            MatchCount: row.MatchCount > int.MaxValue ? int.MaxValue : (int)Math.Max(0, row.MatchCount));
    }


    private static IReadOnlyList<string> SelectMatchedProfileTerms(
        IEnumerable<string>? terms,
        string? candidateText,
        int maxItems)
    {
        var normalizedCandidate = NormalizeForLexicalSignal(candidateText);
        if (string.IsNullOrWhiteSpace(normalizedCandidate) || terms is null)
            return Array.Empty<string>();

        return terms
            .Where(static term => !string.IsNullOrWhiteSpace(term))
            .Select(static term => term.Trim())
            .Where(term => normalizedCandidate.Contains(NormalizeForLexicalSignal(term), StringComparison.Ordinal))
            .Select(CompactProfileSignalValue)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maxItems, 1, 24))
            .ToArray();
    }


    private static IReadOnlyList<string>? ProfileSignalValues(Array? values)
    {
        if (values is null || values.Length == 0)
            return null;

        return values
            .Cast<object?>()
            .Select(static value => value?.ToString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim())
            .ToArray();
    }


    private static IReadOnlyList<string> SelectProfileSignalValues(
        IEnumerable<string>? values,
        IReadOnlyList<string> queryTokens,
        IReadOnlyList<string> lexicalTerms,
        int maxItems,
        int fallbackItems = 0)
    {
        if (values is null)
            return Array.Empty<string>();

        var candidates = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Select(CompactProfileSignalValue)
            .Where(static value => value.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 0)
            return Array.Empty<string>();

        var matched = candidates
            .Where(value => ProfileSignalOverlapsQuery(value, queryTokens, lexicalTerms))
            .Take(Math.Clamp(maxItems, 1, 24))
            .ToArray();
        if (matched.Length > 0 || fallbackItems <= 0)
            return matched;

        return candidates
            .Take(Math.Min(Math.Clamp(fallbackItems, 0, maxItems), candidates.Length))
            .ToArray();
    }


    private static string CompactProfileSignalValue(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 160 ? trimmed : trimmed[..160].TrimEnd();
    }


    private static bool ProfileSignalOverlapsQuery(
        string value,
        IReadOnlyList<string> queryTokens,
        IReadOnlyList<string> lexicalTerms)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalizedValue = NormalizeForLexicalSignal(value);
        if (string.IsNullOrWhiteSpace(normalizedValue))
            return false;

        if (lexicalTerms.Any(term => normalizedValue.Contains(NormalizeForLexicalSignal(term), StringComparison.Ordinal)))
            return true;

        return ComputeLexicalCoverage(queryTokens, value) >= 0.35;
    }


    internal static string FormatPostgresRetrieverError(PostgresException ex)
        => string.IsNullOrWhiteSpace(ex.MessageText)
            ? ex.SqlState
            : $"{ex.SqlState}: {ex.MessageText}";


    internal static string FormatRetrieverError(Exception ex)
        => ex is PostgresException pg
            ? FormatPostgresRetrieverError(pg)
            : $"{ex.GetType().Name}: {ex.Message}".Trim();


    internal static bool IsRetrieverDatabaseTimeout(Exception ex)
    {
        if (ex is TimeoutException)
            return true;
        if (ex is OperationCanceledException)
            return true;
        if (ex is NpgsqlException { InnerException: TimeoutException })
            return true;
        if (ex is NpgsqlException { InnerException: OperationCanceledException })
            return true;
        return ex.InnerException is not null && IsRetrieverDatabaseTimeout(ex.InnerException);
    }


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
        => BuildMatchedContentCardsFromParsed(
            DocumentProfileProjector.ParseContentCards(metadataJson),
            query,
            limit,
            pageStart,
            pageEnd);


    internal static IReadOnlyList<RagMatchedContentCard> BuildMatchedContentCardsFromParsed(
        IReadOnlyList<DocumentProfileContentCard> contentCards,
        string query,
        int limit = 12,
        int? pageStart = null,
        int? pageEnd = null)
        => SelectDocumentProfileContentCards(contentCards, query, limit, pageStart, pageEnd)
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


    internal static IReadOnlyList<DocumentProfileContentCard> SelectDocumentProfileContentCards(
        string? metadataJson,
        string query,
        int limit = 12,
        int? pageStart = null,
        int? pageEnd = null)
    {
        var contentCards = DocumentProfileProjector.ParseContentCards(metadataJson);
        return SelectDocumentProfileContentCards(contentCards, query, limit, pageStart, pageEnd);
    }


    internal static IReadOnlyList<DocumentProfileContentCard> SelectDocumentProfileContentCards(
        IReadOnlyList<DocumentProfileContentCard> contentCards,
        string query,
        int limit = 12,
        int? pageStart = null,
        int? pageEnd = null)
    {
        if (contentCards.Count == 0 || limit <= 0)
            return [];

        var queryTokens = ExtractLexicalQueryTokens(query);
        if (queryTokens.Count == 0)
            return [];

        var preferConcreteContentItems = ShouldPreferConcreteContentCardsForComparativeQuery(query);
        var ranked = contentCards
            .Where(card => IsContentCardSafeToExpose(card, pageStart, pageEnd))
            .Where(card => ContentCardOverlapsMatchPageRange(card, pageStart, pageEnd))
            .Select(card => new
            {
                Card = card,
                Score = ComputeDocumentProfileContentCardQueryScore(query, queryTokens, card),
                Coverage = ComputeLexicalCoverage(queryTokens, BuildDocumentProfileContentCardQueryLookupText(card)),
                TitleCoverage = ComputeLexicalCoverage(queryTokens, card.Title),
                ConcretePriority = ComputeConcreteComparativeContentCardPriority(card),
                IsFacetOnly = LooksLikeFacetOnlyComparativeContentCard(queryTokens, card)
            })
            .Where(item => item.Score > 0.0 || (preferConcreteContentItems && item.ConcretePriority > 0 && !item.IsFacetOnly))
            .ToArray();

        if (preferConcreteContentItems)
        {
            var targetCount = ResolveComparativeContentCardTargetCount(query, limit);
            var concreteCards = ranked
                .Where(static item => item.ConcretePriority > 0 && !item.IsFacetOnly)
                .OrderByDescending(static item => item.ConcretePriority)
                .ThenByDescending(static item => item.Score > 0.0)
                .ThenByDescending(static item => item.Score)
                .ThenByDescending(static item => item.TitleCoverage)
                .ThenByDescending(static item => item.Coverage)
                .ThenBy(static item => item.Card.PageStart ?? int.MaxValue)
                .ThenBy(static item => item.Card.Title, StringComparer.OrdinalIgnoreCase)
                .Take(targetCount)
                .Select(static item => item.Card)
                .ToArray();

            if (concreteCards.Length > 0)
            {
                var selectedIds = new HashSet<string>(concreteCards.Select(GetDocumentProfileContentCardSelectionKey), StringComparer.OrdinalIgnoreCase);
                var supportingCards = ranked
                    .Where(item => !selectedIds.Contains(GetDocumentProfileContentCardSelectionKey(item.Card)))
                    .Where(static item => item.Score > 0.0)
                    .OrderBy(static item => item.IsFacetOnly ? 1 : 0)
                    .ThenByDescending(static item => item.Score)
                    .ThenByDescending(static item => item.TitleCoverage)
                    .ThenByDescending(static item => item.Coverage)
                    .ThenBy(static item => item.Card.PageStart ?? int.MaxValue)
                    .ThenBy(static item => item.Card.Title, StringComparer.OrdinalIgnoreCase)
                    .Select(static item => item.Card);

                return concreteCards
                    .Concat(supportingCards)
                    .Take(limit)
                    .ToArray();
            }
        }

        return ranked
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


    private static string GetDocumentProfileContentCardSelectionKey(DocumentProfileContentCard card)
        => string.IsNullOrWhiteSpace(card.ContentCardId)
            ? $"{card.Title}|{card.PageStart}|{card.PageEnd}|{card.Kind}"
            : card.ContentCardId;


    private static bool ShouldPreferConcreteContentCardsForComparativeQuery(string query)
        => ShouldPreferComparativeDocumentDiversity(query)
           && ResolveComparativeContentCardTargetCount(query, limit: 4) >= 2;


    private static int ResolveComparativeContentCardTargetCount(string query, int limit)
    {
        if (limit <= 0)
            return 0;

        var normalized = " " + FoldDiacritics(ExactMatchEntryExtractor.NormalizeForLookup(query)) + " ";
        var requested = 0;
        if (ContainsAny(normalized, " deux ", " two ", " dos ", " duas ", " due ", " zwei "))
            requested = 2;
        else if (ContainsAny(normalized, " trois ", " three ", " tres ", " tre ", " drei "))
            requested = 3;
        else if (ContainsAny(normalized, " quatre ", " four ", " cuatro ", " quattro ", " vier "))
            requested = 4;
        else if (ContainsAny(normalized, " cinq ", " five ", " cinco ", " cinque ", " funf ", " fuenf "))
            requested = 5;

        if (requested <= 0)
            requested = Math.Min(3, limit);

        return Math.Clamp(requested, 1, Math.Min(limit, 6));
    }


    private static int ComputeConcreteComparativeContentCardPriority(DocumentProfileContentCard card)
    {
        if (string.IsNullOrWhiteSpace(card.Title))
            return 0;
        if (LooksLikeGenericContentSectionCardTitle(card.Title))
            return 0;

        var titleTokens = ExtractLexicalQueryTokens(card.Title);
        if (titleTokens.Count == 0)
            return 0;

        var priority = 0;
        if (card.PageStart is > 0)
            priority += 3;
        if (CardKindUsuallyNamesConcreteContentItem(card.Kind))
            priority += 2;
        if (HasStrongDocumentProfileContentCardEvidence(card))
            priority += 4;
        else
            priority -= 3;
        if (titleTokens.Count is >= 2 and <= 8)
            priority += 2;
        else if (titleTokens.Count == 1 && IsReferenceLikeLookupTerm(titleTokens[0]))
            priority += 1;

        if (LooksLikeProceduralFragmentContentCardTitle(card.Title))
            priority -= 5;
        if (LooksLikeTruncatedContentCardTitle(card.Title))
            priority -= 4;
        if (LooksLikeQuantityOrOcrFragmentContentCardTitle(card.Title))
            priority -= 7;

        priority -= ComputeWeakDocumentProfileContentCardEvidencePenalty(card.Evidence);

        return Math.Max(0, priority);
    }


    private static bool CardKindUsuallyNamesConcreteContentItem(string? kind)
        => string.Equals(kind, "llm_content_card", StringComparison.OrdinalIgnoreCase)
           || string.Equals(kind, "content_item", StringComparison.OrdinalIgnoreCase)
           || string.Equals(kind, "unit_lead", StringComparison.OrdinalIgnoreCase)
           || string.Equals(kind, "exact_lead", StringComparison.OrdinalIgnoreCase)
           || string.Equals(kind, "page_embedded_title", StringComparison.OrdinalIgnoreCase)
           || string.Equals(kind, "standard_ref", StringComparison.OrdinalIgnoreCase);


    internal static bool HasStrongDocumentProfileContentCardEvidence(DocumentProfileContentCard card)
        => card.Evidence is { QuantityFacts.Count: > 0 }
           || card.Evidence?.Facts is { Count: > 0 }
           || (card.Signals ?? [])
               .Any(static signal =>
                   string.Equals(signal, "structured_facts", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(signal, "quantity_list", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(signal, "scale_basis", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(signal, "scalable_quantities", StringComparison.OrdinalIgnoreCase));

}
