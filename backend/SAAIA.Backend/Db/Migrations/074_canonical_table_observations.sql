-- Secondary table observations are revision-scoped evidence, never retrieval chunks.
CREATE UNIQUE INDEX IF NOT EXISTS ux_document_revisions_observation_identity
  ON document_revisions(tenant_id, doc_id, revision_id, source_hash);

CREATE TABLE IF NOT EXISTS document_revision_table_observation_catalog (
  tenant_id uuid NOT NULL,
  doc_id uuid NOT NULL,
  revision_id uuid NOT NULL,
  source_hash bytea NOT NULL CHECK (octet_length(source_hash) = 32),
  availability text NOT NULL CHECK (availability IN ('observed', 'partial', 'unavailable')),
  observation_count integer NULL CHECK (observation_count BETWEEN 0 AND 8),
  content_hash bytea NOT NULL CHECK (octet_length(content_hash) = 32),
  payload bytea NOT NULL CHECK (octet_length(payload) BETWEEN 2 AND 1048576),
  created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, revision_id),
  UNIQUE (tenant_id, doc_id, revision_id, source_hash),
  FOREIGN KEY (tenant_id, doc_id, revision_id, source_hash)
    REFERENCES document_revisions(tenant_id, doc_id, revision_id, source_hash) ON DELETE CASCADE,
  CHECK ((availability = 'unavailable' AND observation_count IS NULL)
      OR (availability IN ('observed', 'partial') AND observation_count IS NOT NULL))
);

CREATE TABLE IF NOT EXISTS document_revision_table_observations (
  tenant_id uuid NOT NULL,
  doc_id uuid NOT NULL,
  revision_id uuid NOT NULL,
  source_hash bytea NOT NULL CHECK (octet_length(source_hash) = 32),
  page_number integer NOT NULL CHECK (page_number > 0),
  table_id text NOT NULL CHECK (length(table_id) BETWEEN 1 AND 200),
  observation_id text NOT NULL CHECK (length(observation_id) BETWEEN 1 AND 200),
  raw_observation_hash bytea NOT NULL CHECK (octet_length(raw_observation_hash) = 32),
  raw_observation_byte_count integer NOT NULL CHECK (raw_observation_byte_count BETWEEN 2 AND 2000000),
  raw_payload bytea NOT NULL CHECK (octet_length(raw_payload) = raw_observation_byte_count),
  line_count integer NOT NULL CHECK (line_count BETWEEN 0 AND 5000),
  content_hash bytea NOT NULL CHECK (octet_length(content_hash) = 32),
  payload bytea NOT NULL CHECK (octet_length(payload) BETWEEN 2 AND 4000000),
  created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, revision_id, observation_id),
  FOREIGN KEY (tenant_id, doc_id, revision_id, source_hash)
    REFERENCES document_revision_table_observation_catalog(tenant_id, doc_id, revision_id, source_hash)
    ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_revision_table_observations_page
  ON document_revision_table_observations(tenant_id, revision_id, page_number, table_id);
