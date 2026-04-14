CREATE TABLE IF NOT EXISTS retrieval_chunks (
  retrieval_chunk_id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
  revision_id uuid NOT NULL REFERENCES document_revisions(revision_id) ON DELETE CASCADE,
  section_id uuid NULL REFERENCES document_sections(section_id) ON DELETE SET NULL,
  unit_id uuid NULL REFERENCES document_units(unit_id) ON DELETE SET NULL,
  chunk_index int NOT NULL CHECK (chunk_index >= 0),
  page_start int NOT NULL CHECK (page_start >= 1),
  page_end int NOT NULL CHECK (page_end >= page_start),
  text_content text NOT NULL,
  token_count int NOT NULL DEFAULT 0,
  checksum bytea,
  metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (revision_id, chunk_index)
);

CREATE INDEX IF NOT EXISTS ix_retrieval_chunks_revision
  ON retrieval_chunks(revision_id, chunk_index);

CREATE INDEX IF NOT EXISTS ix_retrieval_chunks_links
  ON retrieval_chunks(section_id, unit_id, chunk_index);

CREATE INDEX IF NOT EXISTS ix_retrieval_chunks_pages
  ON retrieval_chunks(tenant_id, revision_id, page_start, page_end);
