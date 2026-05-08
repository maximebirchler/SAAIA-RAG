-- Backfill materialized content-card evidence from the profile metadata JSON.
--
-- Older installations can have document_profiles.metadata.contentCards[].evidence
-- without the same evidence copied into document_profile_content_cards.metadata.
-- This keeps the materialized fast path aligned without deleting or reshaping
-- existing cards.

WITH profile_cards AS (
  SELECT
    p.document_profile_id,
    LOWER(REGEXP_REPLACE(BTRIM(card.item ->> 'title'), '[[:space:]]+', ' ', 'g')) AS normalized_title,
    BTRIM(card.item ->> 'title') AS title,
    card.item -> 'evidence' AS evidence
  FROM document_profiles p
  CROSS JOIN LATERAL jsonb_array_elements(COALESCE(p.metadata -> 'contentCards', '[]'::jsonb)) AS card(item)
  WHERE NULLIF(BTRIM(card.item ->> 'title'), '') IS NOT NULL
    AND card.item ? 'evidence'
    AND card.item -> 'evidence' IS NOT NULL
    AND card.item -> 'evidence' <> 'null'::jsonb
),
matched_cards AS (
  SELECT DISTINCT ON (pcc.content_card_id)
    pcc.content_card_id,
    pc.evidence
  FROM document_profile_content_cards pcc
  JOIN profile_cards pc
    ON pc.document_profile_id = pcc.document_profile_id
   AND (
        pc.normalized_title = pcc.normalized_title
     OR LOWER(pc.title) = LOWER(pcc.title)
   )
  WHERE (
      NOT (pcc.metadata ? 'evidence')
      OR pcc.metadata -> 'evidence' IS NULL
      OR pcc.metadata -> 'evidence' = 'null'::jsonb
    )
  ORDER BY pcc.content_card_id
),
prepared AS (
  SELECT
    pcc.content_card_id,
    jsonb_set(COALESCE(pcc.metadata, '{}'::jsonb), '{evidence}', matched.evidence, true) AS metadata,
    REGEXP_REPLACE(CONCAT_WS(' ', pcc.search_text, matched.evidence::text), '[[:space:]]+', ' ', 'g') AS search_text
  FROM document_profile_content_cards pcc
  JOIN matched_cards matched
    ON matched.content_card_id = pcc.content_card_id
)
UPDATE document_profile_content_cards pcc
SET metadata = prepared.metadata,
    search_text = prepared.search_text,
    token_count = CARDINALITY(REGEXP_SPLIT_TO_ARRAY(prepared.search_text, '[[:space:]]+')),
    checksum = DECODE(md5(prepared.search_text), 'hex'),
    updated_at = now()
FROM prepared
WHERE prepared.content_card_id = pcc.content_card_id;
