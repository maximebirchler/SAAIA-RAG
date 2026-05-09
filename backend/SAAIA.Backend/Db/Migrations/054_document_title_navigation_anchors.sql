CREATE EXTENSION IF NOT EXISTS pg_trgm;

CREATE TABLE IF NOT EXISTS document_title_anchors (
  title_anchor_id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
  revision_id uuid NOT NULL REFERENCES document_revisions(revision_id) ON DELETE CASCADE,
  doc_id uuid NOT NULL,
  anchor_index int NOT NULL CHECK (anchor_index >= 0),
  source_kind text NOT NULL,
  source_ordinal int NULL,
  section_id uuid NULL REFERENCES document_sections(section_id) ON DELETE SET NULL,
  unit_id uuid NULL REFERENCES document_units(unit_id) ON DELETE SET NULL,
  retrieval_chunk_id uuid NULL REFERENCES retrieval_chunks(retrieval_chunk_id) ON DELETE SET NULL,
  content_card_id uuid NULL REFERENCES document_profile_content_cards(content_card_id) ON DELETE SET NULL,
  title text NOT NULL,
  normalized_title text NOT NULL,
  title_tokens text[] NOT NULL DEFAULT ARRAY[]::text[],
  page_start int NULL CHECK (page_start IS NULL OR page_start >= 1),
  page_end int NULL CHECK (page_end IS NULL OR page_start IS NULL OR page_end >= page_start),
  confidence real NOT NULL DEFAULT 0,
  metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (revision_id, anchor_index),
  FOREIGN KEY (tenant_id, doc_id) REFERENCES documents(tenant_id, doc_id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_document_title_anchors_revision
  ON document_title_anchors(tenant_id, revision_id, anchor_index);

CREATE INDEX IF NOT EXISTS ix_document_title_anchors_doc_page
  ON document_title_anchors(tenant_id, doc_id, page_start, page_end);

CREATE INDEX IF NOT EXISTS ix_document_title_anchors_normalized_trgm
  ON document_title_anchors
  USING GIN (normalized_title gin_trgm_ops);

CREATE INDEX IF NOT EXISTS ix_document_title_anchors_title_trgm
  ON document_title_anchors
  USING GIN (LOWER(title) gin_trgm_ops);

CREATE INDEX IF NOT EXISTS ix_document_title_anchors_tokens
  ON document_title_anchors
  USING GIN (title_tokens);

CREATE TABLE IF NOT EXISTS document_navigation_entries (
  navigation_entry_id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
  revision_id uuid NOT NULL REFERENCES document_revisions(revision_id) ON DELETE CASCADE,
  doc_id uuid NOT NULL,
  entry_index int NOT NULL CHECK (entry_index >= 0),
  source_page int NOT NULL CHECK (source_page >= 1),
  source_chunk_id uuid NULL REFERENCES retrieval_chunks(retrieval_chunk_id) ON DELETE SET NULL,
  source_unit_id uuid NULL REFERENCES document_units(unit_id) ON DELETE SET NULL,
  label text NOT NULL,
  normalized_label text NOT NULL,
  label_tokens text[] NOT NULL DEFAULT ARRAY[]::text[],
  target_anchor_id uuid NULL REFERENCES document_title_anchors(title_anchor_id) ON DELETE SET NULL,
  target_chunk_id uuid NULL REFERENCES retrieval_chunks(retrieval_chunk_id) ON DELETE SET NULL,
  target_page_start int NULL CHECK (target_page_start IS NULL OR target_page_start >= 1),
  target_page_end int NULL CHECK (target_page_end IS NULL OR target_page_start IS NULL OR target_page_end >= target_page_start),
  resolution_method text NOT NULL DEFAULT 'unresolved',
  confidence real NOT NULL DEFAULT 0,
  metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (revision_id, entry_index),
  FOREIGN KEY (tenant_id, doc_id) REFERENCES documents(tenant_id, doc_id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_document_navigation_entries_revision
  ON document_navigation_entries(tenant_id, revision_id, entry_index);

CREATE INDEX IF NOT EXISTS ix_document_navigation_entries_doc_target_page
  ON document_navigation_entries(tenant_id, doc_id, target_page_start, target_page_end);

CREATE INDEX IF NOT EXISTS ix_document_navigation_entries_label_trgm
  ON document_navigation_entries
  USING GIN (normalized_label gin_trgm_ops);

CREATE INDEX IF NOT EXISTS ix_document_navigation_entries_tokens
  ON document_navigation_entries
  USING GIN (label_tokens);

WITH section_anchors AS (
  SELECT
    s.tenant_id,
    s.revision_id,
    r.doc_id,
    ROW_NUMBER() OVER (PARTITION BY s.revision_id ORDER BY s.page_start, s.ordinal) - 1 AS anchor_index,
    'section'::text AS source_kind,
    s.ordinal AS source_ordinal,
    s.section_id,
    NULL::uuid AS unit_id,
    NULL::uuid AS retrieval_chunk_id,
    NULL::uuid AS content_card_id,
    s.title,
    LOWER(REGEXP_REPLACE(s.title, '\s+', ' ', 'g')) AS normalized_title,
    s.page_start,
    s.page_end,
    0.94::real AS confidence
  FROM document_sections s
  JOIN document_revisions r ON r.revision_id = s.revision_id
  WHERE NULLIF(BTRIM(s.title), '') IS NOT NULL
),
card_anchors AS (
  SELECT
    c.tenant_id,
    c.revision_id,
    c.doc_id,
    100000 + ROW_NUMBER() OVER (PARTITION BY c.revision_id ORDER BY c.page_start NULLS LAST, c.card_index) - 1 AS anchor_index,
    'content_card'::text AS source_kind,
    c.card_index AS source_ordinal,
    NULL::uuid AS section_id,
    NULL::uuid AS unit_id,
    NULL::uuid AS retrieval_chunk_id,
    c.content_card_id,
    c.title,
    c.normalized_title,
    c.page_start,
    c.page_end,
    0.82::real AS confidence
  FROM document_profile_content_cards c
  WHERE NULLIF(BTRIM(c.title), '') IS NOT NULL
),
raw_anchors AS (
  SELECT * FROM section_anchors
  UNION ALL
  SELECT * FROM card_anchors
),
deduped_anchors AS (
  SELECT DISTINCT ON (revision_id, normalized_title, COALESCE(page_start, -1), COALESCE(page_end, -1))
    *
  FROM raw_anchors
  WHERE NULLIF(normalized_title, '') IS NOT NULL
  ORDER BY
    revision_id,
    normalized_title,
    COALESCE(page_start, -1),
    COALESCE(page_end, -1),
    confidence DESC,
    source_kind,
    source_ordinal NULLS LAST
),
ordered_anchors AS (
  SELECT
    *,
    ROW_NUMBER() OVER (
      PARTITION BY revision_id
      ORDER BY COALESCE(page_start, 2147483647), source_kind, source_ordinal NULLS LAST, normalized_title
    ) - 1 AS stable_anchor_index,
    md5(revision_id::text || '|title-anchor|' || (
      ROW_NUMBER() OVER (
        PARTITION BY revision_id
        ORDER BY COALESCE(page_start, 2147483647), source_kind, source_ordinal NULLS LAST, normalized_title
      ) - 1
    )::text) AS stable_hash
  FROM deduped_anchors
)
INSERT INTO document_title_anchors(
  title_anchor_id,
  tenant_id,
  revision_id,
  doc_id,
  anchor_index,
  source_kind,
  source_ordinal,
  section_id,
  unit_id,
  retrieval_chunk_id,
  content_card_id,
  title,
  normalized_title,
  title_tokens,
  page_start,
  page_end,
  confidence,
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
  revision_id,
  doc_id,
  stable_anchor_index,
  source_kind,
  source_ordinal,
  section_id,
  unit_id,
  retrieval_chunk_id,
  content_card_id,
  title,
  normalized_title,
  COALESCE(ARRAY(
    SELECT DISTINCT token
    FROM regexp_split_to_table(normalized_title, '\s+') AS token
    WHERE length(token) >= 3
    LIMIT 16
  ), ARRAY[]::text[]),
  page_start,
  page_end,
  confidence,
  jsonb_build_object('backfilledFrom', source_kind)
FROM ordered_anchors
ON CONFLICT DO NOTHING;
