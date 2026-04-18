CREATE INDEX IF NOT EXISTS idx_contextual_text_entries_text_fts_simple
    ON contextual_text_entries
    USING GIN (to_tsvector('simple', text_content));
