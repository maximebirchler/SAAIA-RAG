-- Backfill page ranges for materialized profile content cards that only carry
-- grounded evidence facts with page refs.
--
-- Retrieval filters page-scoped matches against document_profile_content_cards
-- page_start/page_end. Older llm_backoffice_v1 rows can have facts with
-- pageStart/pageEnd in metadata->evidence while the materialized columns are
-- still NULL, which makes otherwise grounded cards invisible for page matches.

WITH materialized_evidence AS (
  SELECT
    pcc.content_card_id,
    0 AS priority,
    pcc.metadata -> 'evidence' AS evidence
  FROM document_profile_content_cards pcc
  WHERE pcc.page_start IS NULL
    AND pcc.metadata ? 'evidence'
    AND pcc.metadata -> 'evidence' IS NOT NULL
    AND pcc.metadata -> 'evidence' <> 'null'::jsonb
),
profile_evidence AS (
  SELECT
    pcc.content_card_id,
    1 AS priority,
    card.item -> 'evidence' AS evidence
  FROM document_profile_content_cards pcc
  JOIN document_profiles p
    ON p.document_profile_id = pcc.document_profile_id
  CROSS JOIN LATERAL jsonb_array_elements(
    CASE
      WHEN jsonb_typeof(p.metadata -> 'contentCards') = 'array' THEN p.metadata -> 'contentCards'
      ELSE '[]'::jsonb
    END) AS card(item)
  WHERE pcc.page_start IS NULL
    AND card.item ? 'evidence'
    AND card.item -> 'evidence' IS NOT NULL
    AND card.item -> 'evidence' <> 'null'::jsonb
    AND LOWER(REGEXP_REPLACE(BTRIM(card.item ->> 'title'), '[[:space:]]+', ' ', 'g')) = pcc.normalized_title
),
evidence_sources AS (
  SELECT DISTINCT ON (content_card_id)
    content_card_id,
    evidence
  FROM (
    SELECT * FROM materialized_evidence
    UNION ALL
    SELECT * FROM profile_evidence
  ) sources
  ORDER BY content_card_id, priority
),
fact_pages AS (
  SELECT
    es.content_card_id,
    CASE
      WHEN raw.page_start_text ~ '^[0-9]{1,6}$' THEN raw.page_start_text::int
      ELSE NULL
    END AS page_start,
    CASE
      WHEN raw.page_end_text ~ '^[0-9]{1,6}$' THEN raw.page_end_text::int
      ELSE NULL
    END AS page_end
  FROM evidence_sources es
  CROSS JOIN LATERAL jsonb_array_elements(
    CASE
      WHEN jsonb_typeof(es.evidence -> 'facts') = 'array' THEN es.evidence -> 'facts'
      ELSE '[]'::jsonb
    END) AS fact(item)
  CROSS JOIN LATERAL (
    SELECT
      NULLIF(BTRIM(fact.item ->> 'pageStart'), '') AS page_start_text,
      NULLIF(BTRIM(fact.item ->> 'pageEnd'), '') AS page_end_text
  ) raw
),
bounded_ranges AS (
  SELECT
    content_card_id,
    MIN(page_start) AS page_start,
    MAX(GREATEST(COALESCE(page_end, page_start), page_start)) AS page_end
  FROM fact_pages
  WHERE page_start BETWEEN 1 AND 100000
    AND (page_end IS NULL OR page_end BETWEEN 1 AND 100000)
  GROUP BY content_card_id
  HAVING MAX(GREATEST(COALESCE(page_end, page_start), page_start)) - MIN(page_start) <= 7
)
UPDATE document_profile_content_cards pcc
SET page_start = bounded.page_start,
    page_end = bounded.page_end,
    metadata = jsonb_set(COALESCE(pcc.metadata, '{}'::jsonb), '{pageRangeDerivedFromEvidence}', 'true'::jsonb, true),
    updated_at = now()
FROM bounded_ranges bounded
WHERE bounded.content_card_id = pcc.content_card_id
  AND pcc.page_start IS NULL;
