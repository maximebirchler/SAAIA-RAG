ALTER TABLE contextual_text_entries
    ADD COLUMN IF NOT EXISTS search_tsv tsvector
    GENERATED ALWAYS AS (to_tsvector('simple'::regconfig, COALESCE(text_content, ''))) STORED;

CREATE INDEX IF NOT EXISTS idx_contextual_text_entries_search_tsv
    ON contextual_text_entries
    USING GIN (search_tsv);
