CREATE TABLE IF NOT EXISTS retrieval_chunk_links (
  retrieval_chunk_id uuid NOT NULL REFERENCES retrieval_chunks(retrieval_chunk_id) ON DELETE CASCADE,
  linked_chunk_id uuid NOT NULL REFERENCES retrieval_chunks(retrieval_chunk_id) ON DELETE CASCADE,
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
  revision_id uuid NOT NULL REFERENCES document_revisions(revision_id) ON DELETE CASCADE,
  link_type text NOT NULL CHECK (link_type IN ('prev', 'next', 'same_section')),
  created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (retrieval_chunk_id, linked_chunk_id, link_type)
);

CREATE INDEX IF NOT EXISTS ix_retrieval_chunk_links_forward
  ON retrieval_chunk_links(tenant_id, revision_id, retrieval_chunk_id, link_type);

CREATE INDEX IF NOT EXISTS ix_retrieval_chunk_links_reverse
  ON retrieval_chunk_links(tenant_id, revision_id, linked_chunk_id, link_type);
