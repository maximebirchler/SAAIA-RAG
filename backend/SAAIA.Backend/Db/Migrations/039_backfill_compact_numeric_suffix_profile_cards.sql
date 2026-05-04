WITH profile_targets AS (
  SELECT
    p.document_profile_id,
    p.tenant_id,
    p.revision_id,
    p.doc_id,
    p.profile_version
  FROM document_profiles p
  JOIN document_revisions r
    ON r.revision_id = p.revision_id
  JOIN documents d
    ON d.tenant_id = r.tenant_id
   AND d.doc_id = r.doc_id
   AND d.indexed_version = r.indexed_version
   AND d.status = 'indexed'
),
existing_counts AS (
  SELECT
    p.document_profile_id,
    COALESCE(MAX(card.card_index) + 1, 0)::int AS next_card_index,
    COUNT(card.content_card_id)::int AS existing_count
  FROM profile_targets p
  LEFT JOIN document_profile_content_cards card
    ON card.document_profile_id = p.document_profile_id
  GROUP BY p.document_profile_id
),
raw_candidates AS (
  SELECT
    p.document_profile_id,
    p.tenant_id,
    p.revision_id,
    p.doc_id,
    p.profile_version,
    rc.page_start,
    rc.page_end,
    REGEXP_REPLACE(BTRIM(title_match.match[1]), '[[:space:]]+', ' ', 'g') AS title
  FROM profile_targets p
  JOIN retrieval_chunks rc
    ON rc.tenant_id = p.tenant_id
   AND rc.revision_id = p.revision_id
  CROSS JOIN LATERAL regexp_matches(
    rc.text_content,
    '(?:^|[0-9.!?;:)])([[:upper:]][[:alpha:]'' -]{3,90}?)[0-9]{5,}(?=[[:space:]]|[[:upper:]]|$)',
    'g') AS title_match(match)
),
normalized_candidates AS (
  SELECT
    *,
    LOWER(REGEXP_REPLACE(title, '[[:space:]]+', ' ', 'g')) AS normalized_title
  FROM raw_candidates
  WHERE CHAR_LENGTH(title) BETWEEN 4 AND 120
    AND CARDINALITY(REGEXP_SPLIT_TO_ARRAY(title, '[[:space:]]+')) BETWEEN 2 AND 14
    AND title ~ '[[:alpha:]]'
    AND title !~ '[0-9]{4,}'
    AND LOWER(title) !~ '^(ingredients?|preparations?|method|methods|steps?|etapes?|sources?|notes?|sommaire|contents|index|page)([[:space:]:-]|$)'
),
deduped AS (
  SELECT DISTINCT ON (candidate.document_profile_id, candidate.normalized_title)
    candidate.*
  FROM normalized_candidates candidate
  WHERE NOT EXISTS (
    SELECT 1
    FROM document_profile_content_cards existing
    WHERE existing.document_profile_id = candidate.document_profile_id
      AND existing.normalized_title = candidate.normalized_title
  )
  ORDER BY candidate.document_profile_id, candidate.normalized_title, candidate.page_start, candidate.page_end
),
ranked AS (
  SELECT
    d.*,
    ec.next_card_index + (
      ROW_NUMBER() OVER (
        PARTITION BY d.document_profile_id
        ORDER BY d.page_start, d.page_end, d.title
      ) - 1
    )::int AS card_index
  FROM deduped d
  JOIN existing_counts ec
    ON ec.document_profile_id = d.document_profile_id
  WHERE ec.existing_count < 240
),
prepared AS (
  SELECT
    *,
    REGEXP_REPLACE(
      CONCAT_WS(' ', title, normalized_title, 'layout_title'),
      '[[:space:]]+',
      ' ',
      'g') AS search_text,
    md5(document_profile_id::text || '|compact-numeric-suffix-card|' || normalized_title || '|' || COALESCE(page_start::text, '')) AS stable_hash
  FROM ranked
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
  'layout_title',
  ARRAY[title]::text[],
  search_text,
  CARDINALITY(REGEXP_SPLIT_TO_ARRAY(search_text, '[[:space:]]+')),
  DECODE(md5(search_text), 'hex'),
  jsonb_build_object('backfilledFrom', 'retrieval_chunks.compact_numeric_suffix_title')
FROM prepared
ON CONFLICT DO NOTHING;
