CREATE TABLE IF NOT EXISTS document_sections (
  section_id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
  revision_id uuid NOT NULL REFERENCES document_revisions(revision_id) ON DELETE CASCADE,
  ordinal int NOT NULL CHECK (ordinal >= 0),
  title text NOT NULL,
  section_level int NOT NULL DEFAULT 1 CHECK (section_level >= 1),
  page_start int NOT NULL CHECK (page_start >= 1),
  page_end int NOT NULL CHECK (page_end >= page_start),
  start_line int,
  end_line int,
  metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (revision_id, ordinal)
);

CREATE INDEX IF NOT EXISTS ix_document_sections_revision
  ON document_sections(revision_id, ordinal);

CREATE INDEX IF NOT EXISTS ix_document_sections_pages
  ON document_sections(tenant_id, revision_id, page_start, page_end);
