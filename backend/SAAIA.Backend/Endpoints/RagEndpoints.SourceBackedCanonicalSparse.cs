using System.Diagnostics;
using Dapper;
using Npgsql;

namespace SAAIA.Backend.Endpoints;

public static partial class RagEndpoints
{
    /// <summary>
    /// Indexed lexical retrieval for the LLM-led source-backed path.
    ///
    /// This deliberately has no intent recognizer, unindexed LIKE/regex
    /// fallback, profile-card expansion, or semantic backfill. The LLM owns
    /// the search query; this method only executes that query mechanically
    /// against the stored full-text index. Dense and exact retrieval remain
    /// independent parallel channels in the canonical pipeline.
    /// </summary>
    internal static async Task<List<RagMatch>> SearchSourceBackedCanonicalSparseMatchesAsync(
        NpgsqlDataSource ds,
        Guid tenantId,
        string query,
        string? category,
        string? docId,
        string? docPath,
        int topK,
        CancellationToken ct,
        Action<long> sparseMsRef,
        string? categoryPath = null,
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
        Guid? normalizedDocId = Guid.TryParse(docId, out var parsedDocId)
            ? parsedDocId
            : null;
        var commandTimeout = RagOptions.ResolveSearchSparseCommandTimeoutSeconds(
            commandTimeoutSeconds);

        const string sql = """
WITH sparse_query AS (
    SELECT websearch_to_tsquery('simple', @query_text) AS q
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
             AND (rc.metadata->>'offsetStart')::numeric BETWEEN -2147483648 AND 2147483647
            THEN (rc.metadata->>'offsetStart')::int
        ELSE NULL
    END AS "OffsetStart",
    CASE
        WHEN NULLIF(rc.metadata->>'offsetEnd', '') ~ '^[-+]?[0-9]+$'
             AND (rc.metadata->>'offsetEnd')::numeric BETWEEN -2147483648 AND 2147483647
            THEN (rc.metadata->>'offsetEnd')::int
        ELSE NULL
    END AS "OffsetEnd",
    rc.retrieval_chunk_id AS "ChunkId",
    rc.chunk_index AS "ChunkIndex",
    rc.text_content AS "Text",
    d.indexed_version AS "IngestionVersion",
    LOWER(ENCODE(d.content_hash, 'hex')) AS "HashDoc",
    cte.text_content AS "EmbedText",
    rc.section_id AS "SectionOrdinalPlaceholder",
    COALESCE(rc.metadata->>'sectionTitle', s.title) AS "SectionTitle",
    COALESCE(rc.metadata->>'headingPath', s.title) AS "HeadingPath",
    COALESCE(rc.metadata->>'chunkType', 'contextual_text_v1') AS "ChunkType",
    COALESCE(rc.metadata->>'contentRole', 'content') AS "ContentRole",
    rc.metadata->>'navigationReason' AS "NavigationReason",
    rc.metadata->>'originalChunkType' AS "OriginalChunkType",
    CASE
        WHEN NULLIF(rc.metadata->>'navigationScore', '') ~ '^[-+]?([0-9]+(\\.[0-9]+)?|\\.[0-9]+)([eE][-+]?[0-9]+)?$'
            THEN (rc.metadata->>'navigationScore')::double precision
        ELSE NULL
    END AS "NavigationScore",
    CASE
        WHEN NULLIF(rc.metadata->>'contentDensityScore', '') ~ '^[-+]?([0-9]+(\\.[0-9]+)?|\\.[0-9]+)([eE][-+]?[0-9]+)?$'
            THEN (rc.metadata->>'contentDensityScore')::double precision
        ELSE NULL
    END AS "ContentDensityScore",
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
    rc.metadata->>'prevChunkId' AS "PrevChunkId",
    rc.metadata->>'nextChunkId' AS "NextChunkId",
    rc.metadata->>'sameSectionChunkId' AS "SameSectionChunkId",
    NULL::text AS "MatchedContentCardsJson",
    ts_rank_cd(cte.search_tsv, sparse_query.q, 32)::real AS "SparseRank"
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
WHERE cte.search_tsv @@ sparse_query.q
ORDER BY "SparseRank" DESC, rc.chunk_index ASC
LIMIT @top_k;
""";

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var connection = await ds.OpenConnectionAsync(ct);
            var rows = (await connection.QueryAsync<SparseMatchRow>(new CommandDefinition(
                sql,
                new
                {
                    tenant_id = tenantId,
                    query_text = query.Trim(),
                    category,
                    category_path = normalizedCategoryPath,
                    doc_id = normalizedDocId,
                    doc_path = normalizedDocPath,
                    top_k = topK
                },
                commandTimeout: commandTimeout,
                cancellationToken: ct))).ToList();

            return BuildSparseRagMatches(
                rows,
                query,
                embeddingBasis: "source_backed_sparse_fts_v1");
        }
        catch (PostgresException ex)
        {
            RetrievalTelemetry.RecordRetrieverDegraded("canonical_sparse_fts", ex);
            degradedRetrieverRef?.Invoke(
                "canonical_sparse_fts",
                FormatPostgresRetrieverError(ex));
            return [];
        }
        catch (Exception ex) when (IsRetrieverDatabaseTimeout(ex))
        {
            RetrievalTelemetry.RecordRetrieverDegraded("canonical_sparse_fts", ex);
            degradedRetrieverRef?.Invoke(
                "canonical_sparse_fts",
                FormatRetrieverError(ex));
            return [];
        }
        finally
        {
            stopwatch.Stop();
            sparseMsRef(stopwatch.ElapsedMilliseconds);
        }
    }
}
