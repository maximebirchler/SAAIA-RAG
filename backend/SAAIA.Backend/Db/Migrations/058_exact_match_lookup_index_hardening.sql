CREATE EXTENSION IF NOT EXISTS pg_trgm;

DROP INDEX IF EXISTS ix_exact_match_entries_revision_normalized;

CREATE INDEX IF NOT EXISTS ix_exact_match_entries_revision_normalized_hash
    ON exact_match_entries (revision_id, md5(normalized_text));

CREATE INDEX IF NOT EXISTS ix_exact_match_entries_normalized_trgm
    ON exact_match_entries USING GIN (normalized_text gin_trgm_ops);
