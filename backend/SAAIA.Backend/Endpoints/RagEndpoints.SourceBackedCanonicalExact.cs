using Dapper;
using Npgsql;

namespace SAAIA.Backend.Endpoints;

public static partial class RagEndpoints
{
    // An exact-index entry is a locator into its source unit, never a chunk.
    // Return only a current chunk that both belongs to that unit (including
    // declared composition) and actually contains the indexed source text.
    internal static async Task<List<RagMatch>> SearchSourceBackedCanonicalExactMatchesAsync(
        NpgsqlDataSource ds, Guid tenantId, string query, string? category,
        string? docId, string? docPath, int topK, CancellationToken ct,
        string? categoryPath = null)
    {
        var terms = ExactMatchEntryExtractor.ExtractLookupTerms(query).Take(64).ToArray();
        if (terms.Length == 0 || topK <= 0) return [];
        var normalizedDocPath = string.IsNullOrWhiteSpace(docPath)
            ? null : docPath.Trim().Replace('\\', '/').TrimStart('/');
        Guid? normalizedDocId = Guid.TryParse(docId, out var parsedId) ? parsedId : null;
        const string sql = """
WITH lookup_terms AS (SELECT DISTINCT unnest(@terms::text[]) AS term)
SELECT d.doc_id AS "DocId", d.doc_path AS "DocPath", d.doc_name AS "DocName",
    d.category AS "Category", rc.page_start AS "PageStart", rc.page_end AS "PageEnd",
    rc.retrieval_chunk_id AS "ChunkId", rc.chunk_index AS "ChunkIndex",
    rc.text_content AS "Text", rc.text_content AS "EmbedText",
    d.indexed_version AS "IngestionVersion", encode(r.source_hash, 'hex') AS "HashDoc",
    CASE WHEN NULLIF(rc.metadata->>'offsetStart', '') ~ '^[-+]?[0-9]+$'
        THEN CASE WHEN (rc.metadata->>'offsetStart')::numeric BETWEEN -2147483648 AND 2147483647
            THEN (rc.metadata->>'offsetStart')::int END END AS "OffsetStart",
    CASE WHEN NULLIF(rc.metadata->>'offsetEnd', '') ~ '^[-+]?[0-9]+$'
        THEN CASE WHEN (rc.metadata->>'offsetEnd')::numeric BETWEEN -2147483648 AND 2147483647
            THEN (rc.metadata->>'offsetEnd')::int END END AS "OffsetEnd",
    COALESCE(rc.metadata->>'sectionTitle', s.title) AS "SectionTitle",
    COALESCE(rc.metadata->>'headingPath', s.title) AS "HeadingPath",
    COALESCE(rc.metadata->>'chunkType', 'contextual_text_v1') AS "ChunkType",
    COALESCE(rc.metadata->>'contentRole', 'content') AS "ContentRole",
    rc.metadata->>'navigationReason' AS "NavigationReason",
    rc.metadata->>'originalChunkType' AS "OriginalChunkType",
    CASE WHEN NULLIF(rc.metadata->>'navigationScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
        THEN (rc.metadata->>'navigationScore')::double precision END AS "NavigationScore",
    CASE WHEN NULLIF(rc.metadata->>'contentDensityScore', '') ~ '^[-+]?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$'
        THEN (rc.metadata->>'contentDensityScore')::double precision END AS "ContentDensityScore",
    rc.metadata->>'extractionTextStatus' AS "ExtractionTextStatus",
    CASE WHEN LOWER(COALESCE(rc.metadata->>'extractionTextSparse', '')) IN ('true', 'false')
        THEN (rc.metadata->>'extractionTextSparse')::boolean END AS "ExtractionTextSparse",
    CASE WHEN LOWER(COALESCE(rc.metadata->>'extractionOcrCandidate', '')) IN ('true', 'false')
        THEN (rc.metadata->>'extractionOcrCandidate')::boolean END AS "ExtractionOcrCandidate",
    CASE WHEN jsonb_typeof(rc.metadata->'extractionQualitySignals') = 'array'
        THEN (rc.metadata->'extractionQualitySignals')::text END AS "ExtractionQualitySignalsJson",
    rc.metadata->>'prevChunkId' AS "PrevChunkId", rc.metadata->>'nextChunkId' AS "NextChunkId",
    rc.metadata->>'sameSectionChunkId' AS "SameSectionChunkId"
FROM documents d
JOIN document_revisions r ON r.tenant_id=d.tenant_id AND r.doc_id=d.doc_id
    AND r.indexed_version=d.indexed_version
JOIN retrieval_chunks rc ON rc.tenant_id=r.tenant_id AND rc.revision_id=r.revision_id
LEFT JOIN document_sections s ON s.tenant_id=rc.tenant_id AND s.revision_id=rc.revision_id AND s.section_id=rc.section_id
WHERE d.tenant_id=@tenant AND d.status='indexed' AND d.indexed_version>0
    AND (@category IS NULL OR LOWER(d.category)=@category)
    AND (@categoryPath IS NULL OR d.doc_path=@categoryPath OR d.doc_path LIKE (@categoryPath || '/%'))
    AND (@docId IS NULL OR d.doc_id=@docId)
    AND (@docPath IS NULL OR d.doc_path=@docPath)
    AND EXISTS (
        SELECT 1 FROM exact_match_entries e
        JOIN document_units u ON u.tenant_id=e.tenant_id AND u.revision_id=e.revision_id AND u.unit_id=e.unit_id
        JOIN lookup_terms t ON md5(e.normalized_text)=md5(t.term) AND e.normalized_text=t.term
        WHERE e.tenant_id=rc.tenant_id AND e.revision_id=rc.revision_id
            AND (e.unit_id=rc.unit_id OR rc.metadata->'sourceUnitOrdinals' @> jsonb_build_array(u.ordinal))
            AND e.text_content<>'' AND position(e.text_content in rc.text_content)>0
    )
ORDER BY d.doc_id, rc.chunk_index
LIMIT @topK;
""";
        await using var conn = await ds.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SparseMatchRow>(new CommandDefinition(sql, new
        {
            tenant = tenantId, terms, category,
            categoryPath = NormalizeRagCategoryPathForSql(categoryPath),
            docId = normalizedDocId, docPath = normalizedDocPath, topK
        }, cancellationToken: ct));
        // Every result is a literal exact match. Source type and query intent
        // do not introduce semantic bonuses; the existing RRF combines channels.
        return BuildSparseRagMatches(rows, query, "exact_match_v1")
            .Select(static match => match with { Score = 1.0 }).ToList();
    }
}
