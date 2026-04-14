ALTER TABLE IF EXISTS exact_match_entries
    ADD CONSTRAINT fk_exact_match_entries_tenant
    FOREIGN KEY (tenant_id) REFERENCES tenants(tenant_id) ON DELETE CASCADE;

ALTER TABLE IF EXISTS exact_match_entries
    ADD CONSTRAINT ck_exact_match_entries_entry_index
    CHECK (entry_index >= 0);

ALTER TABLE IF EXISTS exact_match_entries
    ADD CONSTRAINT ck_exact_match_entries_page_bounds
    CHECK (page_start >= 1 AND page_end >= page_start);

CREATE INDEX IF NOT EXISTS ix_exact_match_entries_revision_normalized
    ON exact_match_entries (revision_id, normalized_text);

ALTER TABLE IF EXISTS contextual_text_entries
    ADD CONSTRAINT fk_contextual_text_entries_tenant
    FOREIGN KEY (tenant_id) REFERENCES tenants(tenant_id) ON DELETE CASCADE;

ALTER TABLE IF EXISTS contextual_text_entries
    ADD CONSTRAINT ck_contextual_text_entries_entry_index
    CHECK (entry_index >= 0);

ALTER TABLE IF EXISTS contextual_text_entries
    ADD CONSTRAINT ck_contextual_text_entries_page_bounds
    CHECK (page_start >= 1 AND page_end >= page_start);
