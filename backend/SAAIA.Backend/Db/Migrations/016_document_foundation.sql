CREATE TABLE IF NOT EXISTS document_revisions (
  revision_id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
  doc_id uuid NOT NULL,
  doc_path text NOT NULL,
  source_hash bytea NOT NULL,
  source_size bigint,
  source_mtime timestamptz,
  ingestion_version int NOT NULL,
  indexed_version int NOT NULL,
  published_at timestamptz NOT NULL DEFAULT now(),
  created_at timestamptz NOT NULL DEFAULT now(),
  FOREIGN KEY (tenant_id, doc_id) REFERENCES documents(tenant_id, doc_id) ON DELETE CASCADE,
  UNIQUE (tenant_id, doc_id, indexed_version)
);

CREATE INDEX IF NOT EXISTS ix_document_revisions_doc
  ON document_revisions(tenant_id, doc_id, indexed_version DESC);

CREATE INDEX IF NOT EXISTS ix_document_revisions_path
  ON document_revisions(tenant_id, doc_path, indexed_version DESC);

CREATE TABLE IF NOT EXISTS document_revision_artifacts (
  artifact_id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
  revision_id uuid NOT NULL REFERENCES document_revisions(revision_id) ON DELETE CASCADE,
  artifact_type text NOT NULL,
  content_hash bytea,
  byte_size bigint,
  payload jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (revision_id, artifact_type)
);

CREATE INDEX IF NOT EXISTS ix_document_revision_artifacts_revision
  ON document_revision_artifacts(revision_id, artifact_type);

CREATE TABLE IF NOT EXISTS document_page_index (
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
  revision_id uuid NOT NULL REFERENCES document_revisions(revision_id) ON DELETE CASCADE,
  page_number int NOT NULL CHECK (page_number >= 1),
  char_count int NOT NULL DEFAULT 0,
  checksum bytea,
  metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (revision_id, page_number)
);

CREATE INDEX IF NOT EXISTS ix_document_page_index_tenant_revision
  ON document_page_index(tenant_id, revision_id, page_number);

CREATE TABLE IF NOT EXISTS document_processing_runs (
  processing_run_id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
  job_id uuid,
  doc_id uuid NOT NULL,
  doc_path text NOT NULL,
  revision_id uuid NULL REFERENCES document_revisions(revision_id) ON DELETE SET NULL,
  action text NOT NULL CHECK (action IN ('upsert', 'delete')),
  status text NOT NULL CHECK (status IN ('done', 'failed', 'canceled', 'paused')),
  ingestion_version int NOT NULL,
  indexed_version_before int NOT NULL DEFAULT 0,
  indexed_version_after int NOT NULL DEFAULT 0,
  source_hash bytea,
  started_at timestamptz,
  finished_at timestamptz NOT NULL DEFAULT now(),
  created_at timestamptz NOT NULL DEFAULT now(),
  payload jsonb NOT NULL DEFAULT '{}'::jsonb,
  FOREIGN KEY (tenant_id, doc_id) REFERENCES documents(tenant_id, doc_id) ON DELETE CASCADE,
  UNIQUE (tenant_id, job_id)
);

CREATE INDEX IF NOT EXISTS ix_document_processing_runs_doc
  ON document_processing_runs(tenant_id, doc_id, finished_at DESC);

CREATE INDEX IF NOT EXISTS ix_document_processing_runs_job
  ON document_processing_runs(tenant_id, job_id);
