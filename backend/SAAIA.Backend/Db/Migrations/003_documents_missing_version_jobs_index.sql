-- 003_documents_missing_version_jobs_index.sql

-- 1) Documents: missing_since / last_seen_at + ingestion_version
ALTER TABLE documents
  ADD COLUMN IF NOT EXISTS last_seen_at timestamptz,
  ADD COLUMN IF NOT EXISTS missing_since timestamptz,
  ADD COLUMN IF NOT EXISTS ingestion_version int NOT NULL DEFAULT 0;

-- 2) Status: on normalise + on met une contrainte propre
-- Valeurs cibles: new/pending/indexed/missing/deleted/error

UPDATE documents
SET status = 'pending'
WHERE status IS NULL OR status = ''
   OR status NOT IN ('new','pending','indexed','missing','deleted','error');

ALTER TABLE documents
  ADD CONSTRAINT documents_status_check
  CHECK (status IN ('new','pending','indexed','missing','deleted','error'))
  NOT VALID;

ALTER TABLE documents VALIDATE CONSTRAINT documents_status_check;

-- 3) Index utile pour missing cleanup
CREATE INDEX IF NOT EXISTS ix_documents_missing
  ON documents(tenant_id, status, missing_since);

-- 4) Jobs: permettre de re-queue pendant qu'un job est running
-- Actuel: ux_jobs_active bloque queued+running => empêche un nouveau queued quand running.
DROP INDEX IF EXISTS ux_jobs_active;

-- Un seul job QUEUED par (tenant, doc_path, action)
CREATE UNIQUE INDEX IF NOT EXISTS ux_jobs_queued
  ON ingestion_jobs(tenant_id, doc_path, action)
  WHERE status='queued';

-- (Optionnel mais recommandé) index pour "un seul running par doc"
CREATE INDEX IF NOT EXISTS ix_jobs_running_by_doc
  ON ingestion_jobs(tenant_id, doc_path)
  WHERE status='running';
