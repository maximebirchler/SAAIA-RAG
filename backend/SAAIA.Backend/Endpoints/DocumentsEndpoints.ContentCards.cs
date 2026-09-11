using System.Text.Json;
using Dapper;
using Npgsql;
using SAAIA.Backend.Auth;

namespace SAAIA.Backend.Endpoints;

public static partial class DocumentsEndpoints
{
    private static async Task<IResult> ContentCardsAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        string? categoryPath,
        string? categoryRef,
        Guid? docId,
        string? docPath,
        string? q,
        string? inventoryMode,
        int? limit,
        int? offset)
    {
        var tenantId = ctx.GetTenantId();
        var ct = ctx.RequestAborted;

        categoryPath = DocumentsCategoryScopeResolver.NormalizeCategoryPathOrNull(categoryPath);
        categoryRef = DocumentsCategoryScopeResolver.NormalizeCategoryRefOrNull(categoryRef);
        docPath = DocumentsCategoryScopeResolver.NormalizeCategoryPathOrNull(docPath);
        q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        inventoryMode = string.IsNullOrWhiteSpace(inventoryMode)
            ? "ordered"
            : inventoryMode.Trim().ToLowerInvariant();
        if (inventoryMode is not ("ordered" or "representative"))
        {
            return Results.BadRequest(new
            {
                error = "invalid_inventory_mode",
                allowedValues = new[] { "ordered", "representative" }
            });
        }
        var lim = Math.Clamp(limit ?? 60, 1, 120);
        var off = Math.Max(offset ?? 0, 0);

        await using var conn = await ds.OpenConnectionAsync(ct);
        categoryPath = await DocumentsCategoryScopeResolver.ResolveCategoryScopeAsync(
                conn,
                tenantId,
                categoryPath,
                categoryRef,
                ct)
            .ConfigureAwait(false);

        const string sql = """
WITH scoped_docs AS (
  SELECT
    d.doc_id,
    d.doc_path,
    d.doc_name,
    d.category,
    d.page_count,
    d.indexed_version,
    d.content_hash,
    d.file_size,
    d.file_mtime,
    revision.revision_id,
    revision.source_hash AS revision_source_hash,
    CASE
      WHEN d.doc_path LIKE '%/%' THEN regexp_replace(d.doc_path, '/[^/]+$', '')
      ELSE ''
    END AS category_path
  FROM documents d
  JOIN LATERAL (
    SELECT r.revision_id, r.source_hash
    FROM document_revisions r
    WHERE r.tenant_id = d.tenant_id
      AND r.doc_id = d.doc_id
      AND r.indexed_version = d.indexed_version
    ORDER BY r.published_at DESC NULLS LAST
    LIMIT 1
  ) revision ON true
  WHERE d.tenant_id = @tenant
    AND d.status = 'indexed'
    AND d.indexed_version > 0
    AND (@categoryPath IS NULL
      OR d.doc_path = @categoryPath
      OR d.doc_path LIKE (@categoryPath || '/%'))
    AND (@docId IS NULL OR d.doc_id = @docId)
    AND (@docPath IS NULL OR d.doc_path = @docPath)
),
card_candidates AS (
  SELECT
    d.doc_id AS "DocId",
    d.doc_path AS "DocPath",
    d.doc_name AS "DocName",
    d.category AS "Category",
    d.category_path AS "CategoryPath",
    d.page_count AS "PageCount",
    d.revision_id AS "RevisionId",
    encode(d.revision_source_hash, 'hex') AS "SourceHash",
    c.content_card_id AS "ContentCardId",
    c.profile_version AS "ProfileVersion",
    c.card_index AS "CardIndex",
    c.title AS "Title",
    c.kind AS "Kind",
    c.signals AS "Signals",
    card_structure.heading_path AS "HeadingPath",
    card_structure.section_level AS "SectionLevel",
    card_structure.source_chunk_index AS "SourceChunkIndex",
    COALESCE(c.page_start, evidence_pages.page_start) AS "PageStart",
    COALESCE(c.page_end, evidence_pages.page_end, c.page_start, evidence_pages.page_start) AS "PageEnd",
    CASE WHEN c.metadata ? 'evidence' THEN (c.metadata->'evidence')::text ELSE NULL END AS "EvidenceJson",
    CASE
      WHEN c.metadata ? 'evidence'
       AND saaia_profile_content_card_has_grounded_evidence(c.metadata)
        THEN TRUE
      ELSE FALSE
    END AS "HasGroundedEvidence",
    CASE
      WHEN @q IS NULL THEN 0::double precision
      ELSE GREATEST(
        similarity(lower(c.title), lower(@q)),
        similarity(lower(c.search_text), lower(@q)))
    END AS "QueryScore",
    ROW_NUMBER() OVER (
      PARTITION BY
        c.doc_id,
        c.normalized_title,
        COALESCE(c.page_start, evidence_pages.page_start, 0),
        COALESCE(c.page_end, evidence_pages.page_end, c.page_start, evidence_pages.page_start, 0)
      ORDER BY
        saaia_profile_content_card_has_grounded_evidence(c.metadata) DESC,
        CASE c.profile_version
          WHEN 'llm_backoffice_v1' THEN 0
          WHEN 'foundation_v1' THEN 1
          WHEN 'deterministic_v1' THEN 2
          ELSE 3
        END,
        c.updated_at DESC NULLS LAST,
        c.card_index
    ) AS duplicate_rank
  FROM scoped_docs d
  JOIN document_profile_content_cards c
    ON c.tenant_id = @tenant
   AND c.revision_id = d.revision_id
   AND c.doc_id = d.doc_id
  LEFT JOIN LATERAL (
    SELECT
      MIN(
        CASE
          WHEN COALESCE(fact.item->>'pageStart', '') ~ '^[0-9]+$'
            THEN (fact.item->>'pageStart')::int
          ELSE NULL
        END) AS page_start,
      MAX(
        CASE
          WHEN COALESCE(fact.item->>'pageEnd', '') ~ '^[0-9]+$'
            THEN (fact.item->>'pageEnd')::int
          WHEN COALESCE(fact.item->>'pageStart', '') ~ '^[0-9]+$'
            THEN (fact.item->>'pageStart')::int
          ELSE NULL
        END) AS page_end
    FROM jsonb_array_elements(
      CASE
        WHEN jsonb_typeof(c.metadata #> '{evidence,facts}') = 'array'
          THEN c.metadata #> '{evidence,facts}'
        ELSE '[]'::jsonb
      END) AS fact(item)
  ) evidence_pages ON true
  LEFT JOIN LATERAL (
    SELECT
      MAX(fact.item->>'value') FILTER (
        WHERE fact.item->>'kind' = 'canonical_structure'
          AND fact.item->>'label' = 'heading_path') AS heading_path,
      MAX(
        CASE
          WHEN fact.item->>'kind' = 'canonical_structure'
           AND fact.item->>'label' = 'section_level'
           AND COALESCE(fact.item->>'value', '') ~ '^[0-9]+$'
            THEN (fact.item->>'value')::int
          ELSE NULL
        END) AS section_level,
      MAX(
        CASE
          WHEN fact.item->>'kind' = 'canonical_source'
           AND fact.item->>'label' = 'retrieval_chunk_index'
           AND COALESCE(fact.item->>'value', '') ~ '^[0-9]+$'
            THEN (fact.item->>'value')::int
          ELSE NULL
        END) AS source_chunk_index
    FROM jsonb_array_elements(
      CASE
        WHEN jsonb_typeof(c.metadata #> '{evidence,facts}') = 'array'
          THEN c.metadata #> '{evidence,facts}'
        ELSE '[]'::jsonb
      END) AS fact(item)
  ) card_structure ON true
  WHERE NULLIF(BTRIM(c.title), '') IS NOT NULL
    AND saaia_is_safe_profile_content_card(
      c.kind,
      c.title,
      c.page_start,
      c.page_end,
      c.metadata)
    AND (
      @q IS NULL
      OR to_tsvector('simple', c.search_text) @@ plainto_tsquery('simple', @q)
      OR lower(c.title) LIKE ('%' || lower(@q) || '%')
      OR similarity(lower(c.title), lower(@q)) >= 0.18
      OR similarity(lower(c.search_text), lower(@q)) >= 0.12
    )
),
distinct_cards AS (
  SELECT *
  FROM card_candidates
  WHERE duplicate_rank = 1
    AND COALESCE("PageStart", "PageEnd", 0) > 0
),
document_positioned AS (
  SELECT
    *,
    COUNT(*) OVER() AS "Total",
    COUNT(*) OVER (PARTITION BY "DocId") AS "DocumentTotal",
    ROW_NUMBER() OVER (
      PARTITION BY "DocId"
      ORDER BY
        "QueryScore" DESC,
        "HasGroundedEvidence" DESC,
        "PageStart" ASC,
        "CardIndex" ASC,
        "Title" ASC
    ) AS "DocumentRank"
  FROM distinct_cards
),
position_stratified AS (
  SELECT
    *,
    LEAST(
      16,
      FLOOR(
        (("DocumentRank" - 1)::double precision * 16)
        / GREATEST("DocumentTotal", 1))::int + 1
    ) AS "PositionStratum"
  FROM document_positioned
),
representative_ranked AS (
  SELECT
    *,
    ROW_NUMBER() OVER (
      PARTITION BY "DocId", "PositionStratum"
      ORDER BY
        "PageStart" ASC,
        "CardIndex" ASC,
        "Title" ASC
    ) AS "PositionStratumRank"
  FROM position_stratified
)
SELECT *
FROM representative_ranked
ORDER BY
  CASE
    WHEN @q IS NULL AND @inventoryMode = 'representative'
      THEN "PositionStratumRank"
    ELSE NULL
  END ASC,
  CASE
    WHEN @q IS NULL AND @inventoryMode = 'representative'
      THEN CASE "PositionStratum"
        WHEN 9 THEN 1
        WHEN 5 THEN 2
        WHEN 13 THEN 3
        WHEN 3 THEN 4
        WHEN 7 THEN 5
        WHEN 11 THEN 6
        WHEN 15 THEN 7
        WHEN 2 THEN 8
        WHEN 4 THEN 9
        WHEN 6 THEN 10
        WHEN 8 THEN 11
        WHEN 10 THEN 12
        WHEN 12 THEN 13
        WHEN 14 THEN 14
        WHEN 16 THEN 15
        ELSE 16
      END
    ELSE NULL
  END ASC,
  CASE
    WHEN @q IS NULL AND @inventoryMode = 'representative'
      THEN "DocPath"
    ELSE NULL
  END ASC,
  CASE
    WHEN @q IS NULL AND @inventoryMode = 'ordered'
      THEN "DocumentRank"
    ELSE NULL
  END ASC,
  CASE
    WHEN @q IS NULL AND @inventoryMode = 'ordered'
      THEN "DocPath"
    ELSE NULL
  END ASC,
  CASE WHEN @q IS NOT NULL THEN "QueryScore" ELSE NULL END DESC,
  "HasGroundedEvidence" DESC,
  "DocPath" ASC,
  "PageStart" ASC,
  "CardIndex" ASC,
  "Title" ASC
LIMIT @lim OFFSET @off;
""";

        var rows = (await conn.QueryAsync<ContentCardInventoryRow>(new CommandDefinition(
                sql,
                new
                {
                    tenant = tenantId,
                    categoryPath,
                    docId,
                    docPath,
                    q,
                    inventoryMode,
                    lim,
                    off
                },
                cancellationToken: ct)))
            .ToList();
        var total = rows.Count == 0 ? 0 : rows[0].Total;

        return Results.Ok(new
        {
            citable = true,
            usage = "Each item is a mechanically source-backed candidate, not a semantically preclassified answer item. Use kind, headingPath and evidence to decide suitability; cite only candidates whose source meaning fits the request.",
            querySemantics = "q is a lexical filter over the title and source-backed evidence text. Omit q for inventory browsing. inventoryMode=ordered preserves source order while balancing documents; inventoryMode=representative samples stable relative positions across every document without semantic preclassification.",
            scopePath = categoryPath,
            query = q,
            inventoryMode = q is null ? inventoryMode : "relevance",
            total,
            limit = lim,
            offset = off,
            nextOffset = off + rows.Count < total ? off + rows.Count : (int?)null,
            items = rows.Select(row => new
            {
                docId = row.DocId,
                docPath = row.DocPath,
                docName = row.DocName,
                category = row.Category,
                categoryPath = row.CategoryPath,
                pageCount = row.PageCount,
                revisionId = row.RevisionId,
                sourceHash = row.SourceHash,
                contentCardId = row.ContentCardId,
                profileVersion = row.ProfileVersion,
                cardIndex = row.CardIndex,
                title = row.Title,
                kind = row.Kind,
                signals = row.Signals,
                headingPath = row.HeadingPath,
                sectionLevel = row.SectionLevel,
                sourceChunkIndex = row.SourceChunkIndex,
                pageStart = row.PageStart,
                pageEnd = row.PageEnd,
                evidence = ParseOptionalJsonElement(row.EvidenceJson),
                hasGroundedEvidence = row.HasGroundedEvidence,
                queryScore = row.QueryScore
            })
        });
    }

    private sealed class ContentCardInventoryRow
    {
        public Guid DocId { get; set; }
        public string DocPath { get; set; } = "";
        public string DocName { get; set; } = "";
        public string? Category { get; set; }
        public string CategoryPath { get; set; } = "";
        public int? PageCount { get; set; }
        public Guid RevisionId { get; set; }
        public string? SourceHash { get; set; }
        public Guid ContentCardId { get; set; }
        public string ProfileVersion { get; set; } = "";
        public int CardIndex { get; set; }
        public string Title { get; set; } = "";
        public string Kind { get; set; } = "";
        public string[] Signals { get; set; } = [];
        public string? HeadingPath { get; set; }
        public int? SectionLevel { get; set; }
        public int? SourceChunkIndex { get; set; }
        public int? PageStart { get; set; }
        public int? PageEnd { get; set; }
        public string? EvidenceJson { get; set; }
        public bool HasGroundedEvidence { get; set; }
        public double QueryScore { get; set; }
        public int Total { get; set; }
        public long DocumentRank { get; set; }
    }
}
