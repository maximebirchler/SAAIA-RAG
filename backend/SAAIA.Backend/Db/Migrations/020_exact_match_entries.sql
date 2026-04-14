CREATE TABLE IF NOT EXISTS exact_match_entries (
    exact_match_entry_id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL,
    revision_id uuid NOT NULL REFERENCES document_revisions(revision_id) ON DELETE CASCADE,
    section_id uuid NULL REFERENCES document_sections(section_id) ON DELETE SET NULL,
    unit_id uuid NULL REFERENCES document_units(unit_id) ON DELETE SET NULL,
    entry_index integer NOT NULL,
    page_start integer NOT NULL,
    page_end integer NOT NULL,
    text_content text NOT NULL,
    normalized_text text NOT NULL,
    char_count integer NOT NULL,
    token_count integer NOT NULL,
    checksum bytea NOT NULL,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (revision_id, entry_index)
);

CREATE INDEX IF NOT EXISTS idx_exact_match_entries_revision_page
    ON exact_match_entries (revision_id, page_start, page_end);

CREATE INDEX IF NOT EXISTS idx_exact_match_entries_revision_unit
    ON exact_match_entries (revision_id, unit_id);
