CREATE INDEX IF NOT EXISTS ix_documents_resolve_indexed_path_ci
  ON documents (tenant_id, lower(doc_path), updated_at DESC)
  WHERE status = 'indexed';

CREATE INDEX IF NOT EXISTS ix_documents_resolve_indexed_name_ci
  ON documents (tenant_id, lower(doc_name), updated_at DESC)
  WHERE status = 'indexed';

CREATE INDEX IF NOT EXISTS ix_document_processing_runs_latest_done_upsert
  ON document_processing_runs (
    tenant_id,
    doc_id,
    revision_id,
    indexed_version_after,
    finished_at DESC NULLS LAST,
    started_at DESC NULLS LAST
  )
  WHERE action = 'upsert'
    AND status = 'done';
