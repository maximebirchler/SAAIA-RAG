-- Publication and scanner share a durable profile identity independent of retained jobs.
ALTER TABLE documents ADD COLUMN IF NOT EXISTS ingestion_pipeline_fingerprint text;
ALTER TABLE documents ADD CONSTRAINT documents_ingestion_pipeline_fingerprint_check
    CHECK (ingestion_pipeline_fingerprint IS NULL OR ingestion_pipeline_fingerprint ~ '^[a-f0-9]{64}$');
