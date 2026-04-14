CREATE TABLE IF NOT EXISTS document_units (
  unit_id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
  revision_id uuid NOT NULL REFERENCES document_revisions(revision_id) ON DELETE CASCADE,
  section_id uuid NULL REFERENCES document_sections(section_id) ON DELETE SET NULL,
  ordinal int NOT NULL CHECK (ordinal >= 0),
  page_start int NOT NULL CHECK (page_start >= 1),
  page_end int NOT NULL CHECK (page_end >= page_start),
  text_content text NOT NULL,
  char_count int NOT NULL DEFAULT 0,
  token_count int NOT NULL DEFAULT 0,
  checksum bytea,
  metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (revision_id, ordinal)
);

CREATE INDEX IF NOT EXISTS ix_document_units_revision
  ON document_units(revision_id, ordinal);

CREATE INDEX IF NOT EXISTS ix_document_units_section
  ON document_units(section_id, ordinal);

CREATE INDEX IF NOT EXISTS ix_document_units_pages
  ON document_units(tenant_id, revision_id, page_start, page_end);
