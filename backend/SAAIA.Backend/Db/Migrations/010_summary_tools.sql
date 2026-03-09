-- 010_summary_tools.sql
-- v2.8.1 summaries + admin jobs support

CREATE TABLE IF NOT EXISTS document_summaries (
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
  doc_id uuid NOT NULL,
  level text NOT NULL DEFAULT 'medium',
  doc_language text NOT NULL,
  source_hash text NOT NULL,
  summary_text text NOT NULL,
  meta_json jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, doc_id, level),
  FOREIGN KEY (tenant_id, doc_id) REFERENCES documents(tenant_id, doc_id) ON DELETE CASCADE,
  CONSTRAINT document_summaries_level_check CHECK (level IN ('medium'))
);

CREATE INDEX IF NOT EXISTS ix_document_summaries_updated
  ON document_summaries(tenant_id, updated_at DESC);

CREATE TABLE IF NOT EXISTS admin_jobs (
  job_id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
  job_type text NOT NULL,
  doc_id uuid NULL,
  doc_path text NULL,
  level text NULL,
  status text NOT NULL DEFAULT 'queued',
  requested_by_api_key_id uuid NULL,
  payload jsonb NOT NULL DEFAULT '{}'::jsonb,
  result_json jsonb NULL,
  last_error text NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  started_at timestamptz NULL,
  finished_at timestamptz NULL,
  canceled_at timestamptz NULL,
  CONSTRAINT admin_jobs_status_check CHECK (status IN ('queued','running','done','failed','canceled'))
);

CREATE INDEX IF NOT EXISTS ix_admin_jobs_tenant_created
  ON admin_jobs(tenant_id, created_at DESC);

CREATE INDEX IF NOT EXISTS ix_admin_jobs_tenant_status
  ON admin_jobs(tenant_id, status, created_at DESC);

CREATE INDEX IF NOT EXISTS ix_admin_jobs_tenant_type
  ON admin_jobs(tenant_id, job_type, created_at DESC);
