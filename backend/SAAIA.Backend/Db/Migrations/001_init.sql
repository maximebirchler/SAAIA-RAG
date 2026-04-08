CREATE TABLE IF NOT EXISTS schema_migrations (
  version text PRIMARY KEY,
  applied_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS tenants (
  tenant_id uuid PRIMARY KEY,
  name text NOT NULL,
  is_active boolean NOT NULL DEFAULT true,
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS api_keys (
  api_key_id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
  key_prefix text NOT NULL,
  key_hash bytea NOT NULL, -- SHA-256 (32 bytes)
  created_at timestamptz NOT NULL DEFAULT now(),
  revoked_at timestamptz
);

CREATE INDEX IF NOT EXISTS ix_api_keys_prefix ON api_keys(key_prefix);

CREATE TABLE IF NOT EXISTS documents (
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
  doc_id uuid NOT NULL,
  doc_path text NOT NULL,        -- chemin canonique (RELATIF)
  doc_name text NOT NULL,
  category text NOT NULL,
  content_hash bytea,            -- SHA-256
  file_size bigint,
  mime_type text,
  page_count int,
  status text NOT NULL DEFAULT 'pending', -- pending/indexed/deleted/error
  last_ingested_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, doc_id),
  UNIQUE (tenant_id, doc_path)
);

CREATE TABLE IF NOT EXISTS ingestion_jobs (
  job_id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
  action text NOT NULL CHECK (action IN ('upsert','delete')),
  doc_path text NOT NULL,      -- RELATIF
  category text,
  priority int NOT NULL DEFAULT 100,
  status text NOT NULL DEFAULT 'queued' CHECK (status IN ('queued','running','paused','done','failed','canceled')),
  attempts int NOT NULL DEFAULT 0,
  locked_by text,
  locked_at timestamptz,
  available_at timestamptz NOT NULL DEFAULT now(),
  last_error text,
  created_at timestamptz NOT NULL DEFAULT now(),
  started_at timestamptz,
  finished_at timestamptz,
  payload jsonb
);

CREATE INDEX IF NOT EXISTS ix_jobs_pick
  ON ingestion_jobs(status, available_at, priority, created_at);

-- Empêche d'empiler 50 jobs identiques
CREATE UNIQUE INDEX IF NOT EXISTS ux_jobs_active
  ON ingestion_jobs(tenant_id, doc_path, action)
  WHERE status IN ('queued','running','paused');
