-- 013_catalog_category_aliases.sql
-- Explicit, stable multilingual aliases for top-level catalog categories.

CREATE TABLE IF NOT EXISTS documents_catalog_category_aliases (
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
  path text NOT NULL,
  alias text NOT NULL,
  alias_key text NOT NULL,
  language text NULL,
  source text NOT NULL DEFAULT 'snapshot',
  priority int NOT NULL DEFAULT 100,
  updated_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, path, alias_key)
);

CREATE INDEX IF NOT EXISTS ix_documents_catalog_category_aliases_lookup
  ON documents_catalog_category_aliases(tenant_id, alias_key, priority, path);
