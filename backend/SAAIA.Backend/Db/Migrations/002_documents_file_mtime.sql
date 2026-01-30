ALTER TABLE documents
  ADD COLUMN IF NOT EXISTS file_mtime timestamptz;

-- Optionnel mais utile si tu veux auditer rapidement
-- CREATE INDEX IF NOT EXISTS ix_documents_file_mtime ON documents(tenant_id, file_mtime);
