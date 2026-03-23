-- 012_catalog_categories.sql
-- Stable top-level category snapshot for inventory/categories flows.

CREATE TABLE IF NOT EXISTS documents_catalog_categories (
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
  path text NOT NULL,
  name text NOT NULL,
  display_order int NOT NULL,
  doc_count int NOT NULL DEFAULT 0,
  direct_doc_count int NOT NULL DEFAULT 0,
  subfolder_count int NOT NULL DEFAULT 0,
  updated_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, path)
);

CREATE INDEX IF NOT EXISTS ix_documents_catalog_categories_display_order
  ON documents_catalog_categories(tenant_id, display_order, name);
