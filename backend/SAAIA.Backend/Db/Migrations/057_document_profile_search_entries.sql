CREATE EXTENSION IF NOT EXISTS pg_trgm;

CREATE TABLE IF NOT EXISTS document_profile_search_entries (
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
  revision_id uuid NOT NULL REFERENCES document_revisions(revision_id) ON DELETE CASCADE,
  doc_id uuid NOT NULL,
  document_profile_id uuid NULL,
  profile_version text NULL,
  language text NULL,
  summary_text text NOT NULL DEFAULT '',
  search_text text NOT NULL,
  metadata_json jsonb NULL,
  keywords text[] NOT NULL DEFAULT ARRAY[]::text[],
  entities text[] NOT NULL DEFAULT ARRAY[]::text[],
  topics text[] NOT NULL DEFAULT ARRAY[]::text[],
  hypothetical_questions text[] NOT NULL DEFAULT ARRAY[]::text[],
  limits text[] NOT NULL DEFAULT ARRAY[]::text[],
  updated_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, revision_id),
  FOREIGN KEY (tenant_id, doc_id) REFERENCES documents(tenant_id, doc_id) ON DELETE CASCADE
);

ALTER TABLE document_profile_search_entries
  ADD COLUMN IF NOT EXISTS search_tsv tsvector
  GENERATED ALWAYS AS (to_tsvector('simple'::regconfig, COALESCE(search_text, ''))) STORED;

CREATE INDEX IF NOT EXISTS ix_document_profile_search_entries_doc
  ON document_profile_search_entries(tenant_id, doc_id, revision_id);

CREATE INDEX IF NOT EXISTS ix_document_profile_search_entries_tsv
  ON document_profile_search_entries
  USING GIN (search_tsv);

CREATE INDEX IF NOT EXISTS ix_document_profile_search_entries_trgm
  ON document_profile_search_entries
  USING GIN (LOWER(search_text) gin_trgm_ops);

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
      NULLIF(TRIM(BOTH FROM STRING_AGG(NULLIF(profile.search_text, ''), ' ' ORDER BY
        CASE profile.profile_version
          WHEN 'llm_backoffice_v1' THEN 0
          WHEN 'deterministic_v1' THEN 1
          ELSE 2
        END,
        profile.updated_at DESC)), '') AS search_text,
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
      profile_terms.search_text,
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
    COALESCE(cards.metadata_json, p.metadata),
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
