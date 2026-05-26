CREATE OR REPLACE FUNCTION saaia_profile_content_card_has_noisy_title(p_title text)
RETURNS boolean
LANGUAGE sql
IMMUTABLE
AS $$
  WITH normalized AS (
    SELECT btrim(COALESCE(p_title, '')) AS raw_title
  ),
  tokenized AS (
    SELECT
      raw_title,
      ARRAY_REMOVE(regexp_split_to_array(raw_title, '\s+'), '') AS tokens
    FROM normalized
  ),
  stats AS (
    SELECT
      raw_title,
      tokens,
      COALESCE(cardinality(tokens), 0) AS token_count,
      (
        SELECT COUNT(*)::int
        FROM unnest(tokens) AS token(value)
        WHERE token.value ~ '^[[:lower:]]'
      ) AS lowercase_lead_count
    FROM tokenized
  )
  SELECT
    raw_title = ''
    OR length(raw_title) < 4
    OR length(raw_title) > 120
    OR token_count > 14
    OR raw_title !~ '[[:alpha:]]'
    OR raw_title ~ '[.]{5,}'
    OR raw_title ~ '[[:alpha:]]{28,}'
    OR lower(raw_title) ~ '([[:alpha:]])\1{4,}'
    OR lower(raw_title) ~ '([[:alpha:]]{2,4})\1{3,}'
    OR (
      length(raw_title) >= 70
      AND token_count >= 9
      AND raw_title <> upper(raw_title)
      AND lowercase_lead_count >= CEIL(token_count::numeric * 0.55)
    )
  FROM stats;
$$;

CREATE OR REPLACE FUNCTION saaia_is_safe_profile_content_card(
  p_kind text,
  p_title text,
  p_page_start int,
  p_page_end int,
  p_metadata jsonb
) RETURNS boolean
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT
    saaia_profile_content_card_has_grounded_evidence(p_metadata)
    OR (
      (COALESCE(p_page_start, 0) > 0 OR COALESCE(p_page_end, 0) > 0)
      AND (
        saaia_profile_content_card_has_technical_identifier(p_title)
        OR NOT saaia_profile_content_card_has_noisy_title(p_title)
      )
      AND (
        saaia_profile_content_card_has_technical_identifier(p_title)
        OR lower(btrim(COALESCE(p_kind, ''))) IN (
          'section',
          'exact_lead',
          'page_embedded_title',
          'unit_lead',
          'standard_ref',
          'code_ref'
        )
      )
    );
$$;

DELETE FROM document_profile_content_cards card
WHERE NOT saaia_is_safe_profile_content_card(
  card.kind,
  card.title,
  card.page_start,
  card.page_end,
  card.metadata);

WITH filtered_profiles AS (
  SELECT
    p.tenant_id,
    p.document_profile_id,
    COALESCE(filtered.cards, '[]'::jsonb) AS content_cards,
    COALESCE(filtered.card_count, 0) AS content_card_count
  FROM document_profiles p
  LEFT JOIN LATERAL (
    SELECT
      jsonb_agg(card.item ORDER BY card.ordinality) AS cards,
      COUNT(*)::int AS card_count
    FROM jsonb_array_elements(
      CASE
        WHEN jsonb_typeof(p.metadata->'contentCards') = 'array'
          THEN p.metadata->'contentCards'
        ELSE '[]'::jsonb
      END) WITH ORDINALITY AS card(item, ordinality)
    CROSS JOIN LATERAL (
      SELECT
        CASE
          WHEN COALESCE(card.item->>'pageStart', '') ~ '^[0-9]+$'
            THEN (card.item->>'pageStart')::int
          ELSE NULL
        END AS page_start,
        CASE
          WHEN COALESCE(card.item->>'pageEnd', '') ~ '^[0-9]+$'
            THEN (card.item->>'pageEnd')::int
          ELSE NULL
        END AS page_end
    ) parsed
    WHERE saaia_is_safe_profile_content_card(
      card.item->>'kind',
      card.item->>'title',
      parsed.page_start,
      parsed.page_end,
      jsonb_build_object('evidence', card.item->'evidence'))
  ) filtered ON TRUE
  WHERE jsonb_typeof(p.metadata->'contentCards') = 'array'
)
UPDATE document_profiles profile
SET metadata = jsonb_set(
    jsonb_set(
      COALESCE(profile.metadata, '{}'::jsonb) - 'contentCards' - 'contentCardCount',
      '{contentCards}',
      filtered_profiles.content_cards,
      true),
    '{contentCardCount}',
    to_jsonb(filtered_profiles.content_card_count),
    true),
    updated_at = now()
FROM filtered_profiles
WHERE profile.tenant_id = filtered_profiles.tenant_id
  AND profile.document_profile_id = filtered_profiles.document_profile_id;

WITH safe_card_search_text AS (
  SELECT
    profile.tenant_id,
    profile.document_profile_id,
    NULLIF(TRIM(BOTH FROM STRING_AGG(
      NULLIF(CONCAT_WS(
        ' ',
        card.title,
        card.search_text,
        ARRAY_TO_STRING(card.signals, ' ')), ''),
      ' ' ORDER BY card.card_index)), '') AS search_text
  FROM document_profiles profile
  LEFT JOIN document_profile_content_cards card
    ON card.tenant_id = profile.tenant_id
   AND card.document_profile_id = profile.document_profile_id
   AND saaia_is_safe_profile_content_card(
     card.kind,
     card.title,
     card.page_start,
     card.page_end,
     card.metadata)
  GROUP BY profile.tenant_id, profile.document_profile_id
),
safe_profile_search_text AS (
  SELECT
    profile.tenant_id,
    profile.document_profile_id,
    REGEXP_REPLACE(TRIM(BOTH FROM CONCAT_WS(
      ' ',
      doc.doc_path,
      doc.doc_name,
      profile.summary_text,
      ARRAY_TO_STRING(profile.keywords, ' '),
      ARRAY_TO_STRING(profile.entities, ' '),
      ARRAY_TO_STRING(profile.topics, ' '),
      ARRAY_TO_STRING(profile.hypothetical_questions, ' '),
      ARRAY_TO_STRING(profile.limits, ' '),
      safe_card_search_text.search_text)), '[[:space:]]+', ' ', 'g') AS search_text
  FROM document_profiles profile
  JOIN documents doc
    ON doc.tenant_id = profile.tenant_id
   AND doc.doc_id = profile.doc_id
  LEFT JOIN safe_card_search_text
    ON safe_card_search_text.tenant_id = profile.tenant_id
   AND safe_card_search_text.document_profile_id = profile.document_profile_id
)
UPDATE document_profiles profile
SET search_text = safe_profile_search_text.search_text
FROM safe_profile_search_text
WHERE profile.tenant_id = safe_profile_search_text.tenant_id
  AND profile.document_profile_id = safe_profile_search_text.document_profile_id
  AND safe_profile_search_text.search_text <> '';

SELECT saaia_refresh_document_profile_search_entry(r.tenant_id, r.revision_id)
FROM document_revisions r
JOIN documents d
  ON d.tenant_id = r.tenant_id
 AND d.doc_id = r.doc_id
 AND d.indexed_version = r.indexed_version
WHERE d.status = 'indexed'
  AND d.indexed_version > 0;
