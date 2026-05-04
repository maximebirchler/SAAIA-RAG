CREATE EXTENSION IF NOT EXISTS pg_trgm;

CREATE INDEX IF NOT EXISTS idx_retrieval_chunks_text_trgm
    ON retrieval_chunks
    USING GIN (LOWER(text_content) gin_trgm_ops);
