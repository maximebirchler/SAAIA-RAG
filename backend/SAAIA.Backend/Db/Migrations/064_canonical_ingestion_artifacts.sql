CREATE TABLE IF NOT EXISTS document_revision_binary_artifacts (
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
  revision_id uuid NOT NULL REFERENCES document_revisions(revision_id) ON DELETE CASCADE,
  artifact_type text NOT NULL,
  schema_version text NOT NULL,
  content_hash bytea NOT NULL,
  compression text NOT NULL CHECK (compression IN ('gzip')),
  byte_size bigint NOT NULL CHECK (byte_size >= 0),
  stored_size bigint NOT NULL CHECK (stored_size >= 0),
  payload bytea NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (revision_id, artifact_type)
);

CREATE INDEX IF NOT EXISTS ix_document_revision_binary_artifacts_tenant_revision
  ON document_revision_binary_artifacts(tenant_id, revision_id, artifact_type);

CREATE TABLE IF NOT EXISTS document_source_anchors (
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
  revision_id uuid NOT NULL REFERENCES document_revisions(revision_id) ON DELETE CASCADE,
  anchor_id text NOT NULL,
  projection_type text NOT NULL,
  projection_id text NOT NULL,
  precision text NOT NULL,
  page_start int NOT NULL CHECK (page_start >= 1),
  page_end int NOT NULL CHECK (page_end >= page_start),
  content_hash bytea NOT NULL,
  manifest_hash bytea NOT NULL,
  payload jsonb NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (revision_id, anchor_id),
  UNIQUE (revision_id, projection_type, projection_id)
);

CREATE INDEX IF NOT EXISTS ix_document_source_anchors_projection
  ON document_source_anchors(tenant_id, projection_type, projection_id, revision_id);

CREATE INDEX IF NOT EXISTS ix_document_source_anchors_pages
  ON document_source_anchors(revision_id, page_start, page_end);
