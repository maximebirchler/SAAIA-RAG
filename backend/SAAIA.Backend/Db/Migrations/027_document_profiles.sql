CREATE TABLE IF NOT EXISTS document_profiles (
  document_profile_id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
  revision_id uuid NOT NULL REFERENCES document_revisions(revision_id) ON DELETE CASCADE,
  doc_id uuid NOT NULL,
  profile_version text NOT NULL DEFAULT 'deterministic_v1',
  language text NULL,
  summary_text text NOT NULL,
  keywords text[] NOT NULL DEFAULT ARRAY[]::text[],
  entities text[] NOT NULL DEFAULT ARRAY[]::text[],
  topics text[] NOT NULL DEFAULT ARRAY[]::text[],
  hypothetical_questions text[] NOT NULL DEFAULT ARRAY[]::text[],
  limits text[] NOT NULL DEFAULT ARRAY[]::text[],
  search_text text NOT NULL,
  token_count int NOT NULL DEFAULT 0,
  checksum bytea NOT NULL,
  metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (revision_id, profile_version),
  FOREIGN KEY (tenant_id, doc_id) REFERENCES documents(tenant_id, doc_id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_document_profiles_revision
  ON document_profiles(tenant_id, revision_id, profile_version);

CREATE INDEX IF NOT EXISTS ix_document_profiles_doc
  ON document_profiles(tenant_id, doc_id, profile_version);

CREATE INDEX IF NOT EXISTS ix_document_profiles_search_fts_simple
  ON document_profiles
  USING GIN (to_tsvector('simple', search_text));
