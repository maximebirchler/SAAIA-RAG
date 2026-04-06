-- 004_documents_indexed_version_and_jobs_refactor.sql

-- Active indexed version kept separate from ingestion_version.
-- ingestion_version = desired/latest requested version
-- indexed_version   = last successfully indexed version visible to user/search
ALTER TABLE documents
  ADD COLUMN IF NOT EXISTS indexed_version int NOT NULL DEFAULT 0;

-- Heal historical rows created by the old reindex flow:
-- a document that was previously indexed could be switched to status='pending'
-- as soon as a reindex was enqueued. If that reindex was canceled or failed,
-- the document disappeared from the user catalog even though its previous index
-- was still the last valid committed one.
UPDATE documents
SET indexed_version = CASE
        WHEN status='indexed' THEN GREATEST(COALESCE(ingestion_version, 0), 1)
        WHEN status='pending' AND last_ingested_at IS NOT NULL AND COALESCE(ingestion_version, 0) > 0
            THEN GREATEST(COALESCE(ingestion_version, 0) - 1, 1)
        ELSE COALESCE(indexed_version, 0)
    END
WHERE indexed_version IS NULL
   OR indexed_version = 0;

UPDATE documents
SET status='indexed',
    updated_at=now()
WHERE status='pending'
  AND last_ingested_at IS NOT NULL
  AND indexed_version > 0;

CREATE INDEX IF NOT EXISTS ix_documents_status_versions
  ON documents(tenant_id, status, indexed_version, ingestion_version);
