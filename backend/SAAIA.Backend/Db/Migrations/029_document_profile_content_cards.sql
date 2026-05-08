CREATE EXTENSION IF NOT EXISTS pg_trgm;

CREATE TABLE IF NOT EXISTS document_profile_content_cards (
  content_card_id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
  document_profile_id uuid NOT NULL REFERENCES document_profiles(document_profile_id) ON DELETE CASCADE,
  revision_id uuid NOT NULL REFERENCES document_revisions(revision_id) ON DELETE CASCADE,
  doc_id uuid NOT NULL,
  profile_version text NOT NULL,
  card_index int NOT NULL,
  title text NOT NULL,
  normalized_title text NOT NULL,
  page_start int NULL,
  page_end int NULL,
  kind text NOT NULL DEFAULT 'content_item',
  signals text[] NOT NULL DEFAULT ARRAY[]::text[],
  search_text text NOT NULL,
  token_count int NOT NULL DEFAULT 0,
  checksum bytea NOT NULL,
  metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (document_profile_id, card_index),
  UNIQUE (revision_id, profile_version, normalized_title),
  FOREIGN KEY (tenant_id, doc_id) REFERENCES documents(tenant_id, doc_id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_document_profile_content_cards_revision
  ON document_profile_content_cards(tenant_id, revision_id, profile_version, card_index);

CREATE INDEX IF NOT EXISTS ix_document_profile_content_cards_doc
  ON document_profile_content_cards(tenant_id, doc_id, profile_version, card_index);

CREATE INDEX IF NOT EXISTS ix_document_profile_content_cards_search_fts_simple
  ON document_profile_content_cards
  USING GIN (to_tsvector('simple', search_text));

CREATE INDEX IF NOT EXISTS ix_document_profile_content_cards_search_trgm
  ON document_profile_content_cards
  USING GIN (LOWER(search_text) gin_trgm_ops);

CREATE INDEX IF NOT EXISTS ix_document_profile_content_cards_title_trgm
  ON document_profile_content_cards
  USING GIN (LOWER(title) gin_trgm_ops);

CREATE INDEX IF NOT EXISTS ix_document_profile_content_cards_normalized_title_trgm
  ON document_profile_content_cards
  USING GIN (normalized_title gin_trgm_ops);

WITH raw_cards AS (
  SELECT
    p.document_profile_id,
    p.tenant_id,
    p.revision_id,
    p.doc_id,
    p.profile_version,
    (card_item.ordinality - 1)::int AS card_index,
    NULLIF(BTRIM(card_item.card->>'title'), '') AS title,
    NULLIF(BTRIM(card_item.card->>'kind'), '') AS kind,
    CASE
      WHEN card_item.card ? 'pageStart' AND NULLIF(card_item.card->>'pageStart', '') IS NOT NULL
        THEN NULLIF(card_item.card->>'pageStart', '')::int
      ELSE NULL
    END AS page_start,
    CASE
      WHEN card_item.card ? 'pageEnd' AND NULLIF(card_item.card->>'pageEnd', '') IS NOT NULL
        THEN NULLIF(card_item.card->>'pageEnd', '')::int
      ELSE NULL
    END AS page_end,
    COALESCE((
      SELECT ARRAY_AGG(DISTINCT NULLIF(BTRIM(signal.value), ''))
      FROM jsonb_array_elements_text(COALESCE(card_item.card->'signals', '[]'::jsonb)) AS signal(value)
      WHERE NULLIF(BTRIM(signal.value), '') IS NOT NULL
    ), ARRAY[]::text[]) AS signals
  FROM document_profiles p
  CROSS JOIN LATERAL jsonb_array_elements(COALESCE(p.metadata->'contentCards', '[]'::jsonb)) WITH ORDINALITY AS card_item(card, ordinality)
),
normalized_cards AS (
  SELECT
    *,
    LOWER(REGEXP_REPLACE(title, '\s+', ' ', 'g')) AS normalized_title,
    REGEXP_REPLACE(
      CONCAT_WS(
        ' ',
        title,
        LOWER(REGEXP_REPLACE(title, '\s+', ' ', 'g')),
        kind,
        ARRAY_TO_STRING(signals, ' '),
        LOWER(REGEXP_REPLACE(ARRAY_TO_STRING(signals, ' '), '\s+', ' ', 'g'))),
      '\s+',
      ' ',
      'g') AS search_text,
    md5(document_profile_id::text || '|content-card|' || LOWER(REGEXP_REPLACE(title, '\s+', ' ', 'g'))) AS stable_hash
  FROM raw_cards
  WHERE title IS NOT NULL
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
  COALESCE(kind, 'content_item'),
  signals,
  search_text,
  CARDINALITY(REGEXP_SPLIT_TO_ARRAY(search_text, '\s+')),
  DECODE(md5(search_text), 'hex'),
  jsonb_build_object('backfilledFrom', 'document_profiles.metadata.contentCards')
FROM normalized_cards
ON CONFLICT DO NOTHING;
