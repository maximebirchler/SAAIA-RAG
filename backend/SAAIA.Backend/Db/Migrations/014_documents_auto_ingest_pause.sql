-- 014_documents_auto_ingest_pause.sql

ALTER TABLE documents
  ADD COLUMN IF NOT EXISTS auto_ingest_paused boolean NOT NULL DEFAULT false;

ALTER TABLE documents
  ADD COLUMN IF NOT EXISTS auto_ingest_paused_at timestamptz NULL;

ALTER TABLE documents
  ADD COLUMN IF NOT EXISTS auto_ingest_pause_reason text NULL;

CREATE INDEX IF NOT EXISTS ix_documents_auto_ingest_paused
  ON documents(tenant_id, auto_ingest_paused, status);
