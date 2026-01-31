-- 004_phase1_security.sql
-- Phase 1 (sécurité produit): admin API keys + audit events

ALTER TABLE IF EXISTS api_keys
  ADD COLUMN IF NOT EXISTS label TEXT NULL,
  ADD COLUMN IF NOT EXISTS is_admin BOOLEAN NOT NULL DEFAULT FALSE,
  ADD COLUMN IF NOT EXISTS last_used_at TIMESTAMPTZ NULL;

CREATE INDEX IF NOT EXISTS ix_api_keys_prefix_active
  ON api_keys (key_prefix)
  WHERE revoked_at IS NULL;

CREATE INDEX IF NOT EXISTS ix_api_keys_tenant_active
  ON api_keys (tenant_id)
  WHERE revoked_at IS NULL;

CREATE TABLE IF NOT EXISTS audit_events (
  audit_id UUID PRIMARY KEY,
  ts TIMESTAMPTZ NOT NULL DEFAULT now(),
  tenant_id UUID NULL,
  actor_api_key_id UUID NULL,
  actor_is_admin BOOLEAN NOT NULL DEFAULT FALSE,
  action TEXT NOT NULL,
  target TEXT NULL,
  payload_json TEXT NULL,
  ip TEXT NULL
);

CREATE INDEX IF NOT EXISTS ix_audit_events_ts
  ON audit_events (ts DESC);

CREATE INDEX IF NOT EXISTS ix_audit_events_tenant_ts
  ON audit_events (tenant_id, ts DESC);
