CREATE OR REPLACE FUNCTION saaia_profile_content_card_has_grounded_evidence(p_metadata jsonb)
RETURNS boolean
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT
    EXISTS (
      SELECT 1
      FROM jsonb_array_elements(
        CASE
          WHEN jsonb_typeof(p_metadata #> '{evidence,facts}') = 'array'
            THEN p_metadata #> '{evidence,facts}'
          ELSE '[]'::jsonb
        END) AS fact(item)
      WHERE btrim(COALESCE(fact.item->>'sourceText', '')) <> ''
         OR CASE
              WHEN COALESCE(fact.item->>'pageStart', '') ~ '^[0-9]+$'
                THEN (fact.item->>'pageStart')::int
              ELSE 0
            END > 0
         OR CASE
              WHEN COALESCE(fact.item->>'pageEnd', '') ~ '^[0-9]+$'
                THEN (fact.item->>'pageEnd')::int
              ELSE 0
            END > 0
    )
    OR EXISTS (
      SELECT 1
      FROM jsonb_array_elements(
        CASE
          WHEN jsonb_typeof(p_metadata #> '{evidence,quantityFacts}') = 'array'
            THEN p_metadata #> '{evidence,quantityFacts}'
          ELSE '[]'::jsonb
        END) AS fact(item)
      WHERE btrim(COALESCE(fact.item->>'sourceText', '')) <> ''
    );
$$;

CREATE OR REPLACE FUNCTION saaia_profile_content_card_has_technical_identifier(p_title text)
RETURNS boolean
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT COALESCE(p_title, '') ~* '(^|[^[:alnum:]])(EN|ISO|IEC|ASTM|DIN|NFPA|API|ANSI|CEN|TR|TS|PD|BS|NF|SN|UL|CSA)([[:space:]_./:-]+[A-Z]{1,6}){0,4}[[:space:]_./:-]*[0-9][A-Z0-9._/:-]*([^[:alnum:]]|$)';
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
    OR saaia_profile_content_card_has_technical_identifier(p_title)
    OR (
      lower(btrim(COALESCE(p_kind, ''))) IN (
        'section',
        'exact_lead',
        'page_embedded_title',
        'unit_lead',
        'standard_ref',
        'code_ref'
      )
      AND (COALESCE(p_page_start, 0) > 0 OR COALESCE(p_page_end, 0) > 0)
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

CREATE OR REPLACE FUNCTION saaia_refresh_document_profile_search_entry(
  p_tenant_id uuid,
  p_revision_id uuid
) RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
  WITH revision_doc AS (
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
      r.revision_id
    FROM document_revisions r
    JOIN documents d
      ON d.tenant_id = r.tenant_id
     AND d.doc_id = r.doc_id
     AND d.indexed_version = r.indexed_version
    WHERE r.tenant_id = p_tenant_id
      AND r.revision_id = p_revision_id
      AND d.status = 'indexed'
      AND d.indexed_version > 0
  ),
  preferred_profile AS (
    SELECT profile.*
    FROM document_profiles profile
    JOIN revision_doc d
      ON d.tenant_id = profile.tenant_id
     AND d.revision_id = profile.revision_id
    ORDER BY
      CASE profile.profile_version
        WHEN 'llm_backoffice_v1' THEN 0
        WHEN 'deterministic_v1' THEN 1
        ELSE 2
      END,
      profile.updated_at DESC
    LIMIT 1
  ),
  profile_terms AS (
    SELECT
      NULLIF(TRIM(BOTH FROM STRING_AGG(NULLIF(profile.summary_text, ''), ' ' ORDER BY
        CASE profile.profile_version
          WHEN 'llm_backoffice_v1' THEN 0
          WHEN 'deterministic_v1' THEN 1
          ELSE 2
        END,
        profile.updated_at DESC)), '') AS summary_text,
      NULLIF(TRIM(BOTH FROM STRING_AGG(NULLIF(ARRAY_TO_STRING(profile.keywords, ' '), ''), ' ')), '') AS keywords_text,
      NULLIF(TRIM(BOTH FROM STRING_AGG(NULLIF(ARRAY_TO_STRING(profile.entities, ' '), ''), ' ')), '') AS entities_text,
      NULLIF(TRIM(BOTH FROM STRING_AGG(NULLIF(ARRAY_TO_STRING(profile.topics, ' '), ''), ' ')), '') AS topics_text,
      NULLIF(TRIM(BOTH FROM STRING_AGG(NULLIF(ARRAY_TO_STRING(profile.hypothetical_questions, ' '), ''), ' ')), '') AS hypothetical_questions_text,
      NULLIF(TRIM(BOTH FROM STRING_AGG(NULLIF(ARRAY_TO_STRING(profile.limits, ' '), ''), ' ')), '') AS limits_text
    FROM document_profiles profile
    JOIN revision_doc d
      ON d.tenant_id = profile.tenant_id
     AND d.revision_id = profile.revision_id
  ),
  cards AS (
    SELECT
      CASE
        WHEN COUNT(*) = 0 THEN NULL
        ELSE jsonb_build_object(
          'contentCardCount',
          COUNT(*),
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
            ORDER BY card.card_index))
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
      JOIN revision_doc d
        ON d.tenant_id = raw_card.tenant_id
       AND d.revision_id = raw_card.revision_id
      WHERE saaia_is_safe_profile_content_card(
        raw_card.kind,
        raw_card.title,
        raw_card.page_start,
        raw_card.page_end,
        raw_card.metadata)
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
  )
  INSERT INTO document_profile_search_entries(
    tenant_id,
    revision_id,
    doc_id,
    document_profile_id,
    profile_version,
    language,
    summary_text,
    search_text,
    metadata_json,
    keywords,
    entities,
    topics,
    hypothetical_questions,
    limits,
    updated_at)
  SELECT
    d.tenant_id,
    d.revision_id,
    d.doc_id,
    p.document_profile_id,
    p.profile_version,
    p.language,
    COALESCE(NULLIF(s.summary_text, ''), NULLIF(p.summary_text, ''), d.doc_name, d.doc_path),
    TRIM(BOTH FROM CONCAT_WS(
      ' ',
      d.doc_path,
      d.doc_name,
      p.summary_text,
      profile_terms.summary_text,
      profile_terms.keywords_text,
      profile_terms.entities_text,
      profile_terms.topics_text,
      profile_terms.hypothetical_questions_text,
      profile_terms.limits_text,
      NULLIF(s.summary_text, ''),
      ARRAY_TO_STRING(p.keywords, ' '),
      ARRAY_TO_STRING(p.entities, ' '),
      ARRAY_TO_STRING(p.topics, ' '),
      ARRAY_TO_STRING(p.hypothetical_questions, ' '),
      ARRAY_TO_STRING(p.limits, ' '),
      cards.search_text)),
    jsonb_strip_nulls(
      (COALESCE(p.metadata, '{}'::jsonb) - 'contentCards' - 'contentCardCount')
      || COALESCE(cards.metadata_json, '{}'::jsonb)),
    p.keywords,
    p.entities,
    p.topics,
    p.hypothetical_questions,
    p.limits,
    now()
  FROM revision_doc d
  JOIN preferred_profile p ON TRUE
  CROSS JOIN profile_terms
  CROSS JOIN cards
  LEFT JOIN document_summaries s
    ON s.tenant_id = d.tenant_id
   AND s.doc_id = d.doc_id
   AND s.level = 'medium'
   AND s.source_hash = saaia_document_summary_source_hash(d.content_hash, d.doc_path, d.file_size, d.file_mtime, d.indexed_version)
  ON CONFLICT (tenant_id, revision_id) DO UPDATE
  SET doc_id = EXCLUDED.doc_id,
      document_profile_id = EXCLUDED.document_profile_id,
      profile_version = EXCLUDED.profile_version,
      language = EXCLUDED.language,
      summary_text = EXCLUDED.summary_text,
      search_text = EXCLUDED.search_text,
      metadata_json = EXCLUDED.metadata_json,
      keywords = EXCLUDED.keywords,
      entities = EXCLUDED.entities,
      topics = EXCLUDED.topics,
      hypothetical_questions = EXCLUDED.hypothetical_questions,
      limits = EXCLUDED.limits,
      updated_at = now();

  IF NOT FOUND THEN
    DELETE FROM document_profile_search_entries
    WHERE tenant_id = p_tenant_id
      AND revision_id = p_revision_id;
  END IF;
END;
$$;

SELECT saaia_refresh_document_profile_search_entry(r.tenant_id, r.revision_id)
FROM document_revisions r
JOIN documents d
  ON d.tenant_id = r.tenant_id
 AND d.doc_id = r.doc_id
 AND d.indexed_version = r.indexed_version
WHERE d.status = 'indexed'
  AND d.indexed_version > 0;
