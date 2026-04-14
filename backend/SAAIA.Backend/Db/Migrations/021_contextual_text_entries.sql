CREATE TABLE IF NOT EXISTS contextual_text_entries (
    contextual_text_entry_id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL,
    revision_id uuid NOT NULL REFERENCES document_revisions(revision_id) ON DELETE CASCADE,
    section_id uuid NULL REFERENCES document_sections(section_id) ON DELETE SET NULL,
    unit_id uuid NULL REFERENCES document_units(unit_id) ON DELETE SET NULL,
    retrieval_chunk_id uuid NULL REFERENCES retrieval_chunks(retrieval_chunk_id) ON DELETE SET NULL,
    entry_index integer NOT NULL,
    page_start integer NOT NULL,
    page_end integer NOT NULL,
    text_content text NOT NULL,
    char_count integer NOT NULL,
    token_count integer NOT NULL,
    checksum bytea NOT NULL,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (revision_id, entry_index)
);

CREATE INDEX IF NOT EXISTS idx_contextual_text_entries_revision_page
    ON contextual_text_entries (revision_id, page_start, page_end);

CREATE INDEX IF NOT EXISTS idx_contextual_text_entries_revision_chunk
    ON contextual_text_entries (revision_id, retrieval_chunk_id);
