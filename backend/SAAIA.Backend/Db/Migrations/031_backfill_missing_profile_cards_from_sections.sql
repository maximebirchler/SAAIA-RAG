WITH profile_without_cards AS (
  SELECT p.*
  FROM document_profiles p
  WHERE NOT EXISTS (
    SELECT 1
    FROM document_profile_content_cards existing
    WHERE existing.document_profile_id = p.document_profile_id
  )
),
section_candidates AS (
  SELECT
    p.document_profile_id,
    p.tenant_id,
    p.revision_id,
    p.doc_id,
    p.profile_version,
    ds.ordinal,
    NULLIF(BTRIM(ds.title), '') AS title,
    ds.page_start,
    ds.page_end
  FROM profile_without_cards p
  JOIN document_sections ds
    ON ds.tenant_id = p.tenant_id
   AND ds.revision_id = p.revision_id
  WHERE NULLIF(BTRIM(ds.title), '') IS NOT NULL
),
ranked_cards AS (
  SELECT
    *,
    (ROW_NUMBER() OVER (
      PARTITION BY document_profile_id
      ORDER BY ordinal
    ) - 1)::int AS card_index
  FROM section_candidates
  WHERE CHAR_LENGTH(title) BETWEEN 4 AND 120
    AND CARDINALITY(REGEXP_SPLIT_TO_ARRAY(title, '\s+')) BETWEEN 2 AND 14
    AND LOWER(REGEXP_REPLACE(title, '\s+', ' ', 'g')) NOT IN (
      'notes',
      'note',
      'references',
      'source',
      'sources',
      'sommaire',
      'contents',
      'table des matieres',
      'table of contents',
      'index',
      'document',
      'documents',
      'page',
      'pages'
    )
),
normalized_cards AS (
  SELECT
    *,
    LOWER(REGEXP_REPLACE(title, '\s+', ' ', 'g')) AS normalized_title,
    REGEXP_REPLACE(
      CONCAT_WS(' ', title, LOWER(REGEXP_REPLACE(title, '\s+', ' ', 'g')), 'section'),
      '\s+',
      ' ',
      'g') AS search_text,
    md5(document_profile_id::text || '|content-card|' || LOWER(REGEXP_REPLACE(title, '\s+', ' ', 'g'))) AS stable_hash
  FROM ranked_cards
  WHERE card_index < 240
)
INSERT INTO document_profile_content_cards(
  content_card_id,
  tenant_id,
  document_profile_id,
  revision_id,
  doc_id,
  profile_version,
  card_index,
  title,
  normalized_title,
  page_start,
  page_end,
  kind,
  signals,
  search_text,
  token_count,
  checksum,
  metadata)
SELECT
  (
    SUBSTRING(stable_hash, 1, 8) || '-' ||
    SUBSTRING(stable_hash, 9, 4) || '-' ||
    SUBSTRING(stable_hash, 13, 4) || '-' ||
    SUBSTRING(stable_hash, 17, 4) || '-' ||
    SUBSTRING(stable_hash, 21, 12)
  )::uuid,
  tenant_id,
  document_profile_id,
  revision_id,
  doc_id,
  profile_version,
  card_index,
  title,
  normalized_title,
  page_start,
  page_end,
  'section',
  ARRAY[title]::text[],
  search_text,
  CARDINALITY(REGEXP_SPLIT_TO_ARRAY(search_text, '\s+')),
  DECODE(md5(search_text), 'hex'),
  jsonb_build_object('backfilledFrom', 'document_sections')
FROM normalized_cards
ON CONFLICT DO NOTHING;
