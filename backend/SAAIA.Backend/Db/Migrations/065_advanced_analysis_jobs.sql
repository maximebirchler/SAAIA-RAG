-- Durable, tenant-scoped jobs created from saaia.advanced-analysis-handoff.v1.
-- Provider execution is added separately; this migration only persists state.

CREATE UNIQUE INDEX IF NOT EXISTS ux_chat_sessions_tenant_session_user
  ON chat_sessions(tenant_id, session_id, user_id);

CREATE TABLE IF NOT EXISTS advanced_analysis_jobs (
  job_id UUID PRIMARY KEY,
  tenant_id UUID NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
  user_id TEXT NOT NULL,
  session_id UUID NOT NULL,
  handoff_id UUID NOT NULL,
  schema_version TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'queued',
  revision INTEGER NOT NULL DEFAULT 1,
  attempt_count INTEGER NOT NULL DEFAULT 0,
  handoff JSONB NOT NULL,
  provider_key TEXT NULL,
  result JSONB NULL,
  last_error_code TEXT NULL,
  requested_by_api_key_id UUID NULL,
  available_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  lease_owner TEXT NULL,
  lease_expires_at TIMESTAMPTZ NULL,
  cancel_requested_at TIMESTAMPTZ NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  started_at TIMESTAMPTZ NULL,
  finished_at TIMESTAMPTZ NULL,
  expires_at TIMESTAMPTZ NOT NULL,
  CONSTRAINT fk_advanced_analysis_session
    FOREIGN KEY (tenant_id, session_id, user_id)
    REFERENCES chat_sessions(tenant_id, session_id, user_id)
    ON DELETE CASCADE,
  CONSTRAINT ck_advanced_analysis_user_nonblank
    CHECK (length(btrim(user_id)) BETWEEN 1 AND 200),
  CONSTRAINT ck_advanced_analysis_schema
    CHECK (schema_version = 'saaia.advanced-analysis-handoff.v1'),
  CONSTRAINT ck_advanced_analysis_status
    CHECK (status IN ('queued', 'running', 'succeeded', 'failed', 'canceled')),
  CONSTRAINT ck_advanced_analysis_revision
    CHECK (revision >= 1),
  CONSTRAINT ck_advanced_analysis_attempt_count
    CHECK (attempt_count >= 0),
  CONSTRAINT ck_advanced_analysis_handoff_object
    CHECK (jsonb_typeof(handoff) = 'object'),
  CONSTRAINT ck_advanced_analysis_result_object
    CHECK (result IS NULL OR jsonb_typeof(result) = 'object'),
  CONSTRAINT ck_advanced_analysis_expiration
    CHECK (expires_at > created_at)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_advanced_analysis_handoff
  ON advanced_analysis_jobs(tenant_id, user_id, handoff_id);

CREATE INDEX IF NOT EXISTS ix_advanced_analysis_claim
  ON advanced_analysis_jobs(status, available_at, created_at)
  WHERE status = 'queued';

CREATE INDEX IF NOT EXISTS ix_advanced_analysis_user_recent
  ON advanced_analysis_jobs(tenant_id, user_id, updated_at DESC);

CREATE INDEX IF NOT EXISTS ix_advanced_analysis_expiry
  ON advanced_analysis_jobs(expires_at);
