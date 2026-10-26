-- Logical source/chunk identities remain stable; vector points belong to one complete projection plan.
-- NULL is reserved for legacy points, never a wildcard for a new generation.
ALTER TABLE documents ADD COLUMN IF NOT EXISTS ingestion_vector_plan_fingerprint text;
ALTER TABLE documents ADD CONSTRAINT documents_ingestion_vector_plan_fingerprint_check
    CHECK (ingestion_vector_plan_fingerprint IS NULL OR ingestion_vector_plan_fingerprint ~ '^[a-f0-9]{64}$');
