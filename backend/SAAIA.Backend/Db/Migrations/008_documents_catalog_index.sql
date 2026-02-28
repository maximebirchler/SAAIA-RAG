-- Speed up GET /documents/catalog
-- Typical pattern: WHERE tenant_id=? AND status <> 'missing' ORDER BY category, doc_name LIMIT ? OFFSET ?

CREATE INDEX IF NOT EXISTS ix_documents_catalog_active
ON documents(tenant_id, category, doc_name)
WHERE status <> 'missing';
