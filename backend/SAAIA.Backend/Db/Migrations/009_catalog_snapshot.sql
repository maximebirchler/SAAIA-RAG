-- M1.4: Catalog snapshot (tree/stats/count) to make inventory endpoints O(1)

CREATE TABLE IF NOT EXISTS documents_category_nodes (
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
  path text NOT NULL,            -- categoryPath ('' for root)
  name text NOT NULL,            -- last segment ('' for root)
  parent_path text,              -- NULL for root
  depth int NOT NULL,            -- 0 for root, 1..N for segments
  doc_count int NOT NULL,        -- cumulative docs under this node (indexed only)
  direct_doc_count int NOT NULL DEFAULT 0, -- docs directly in this folder (indexed only)
  updated_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, path)
);

CREATE INDEX IF NOT EXISTS ix_documents_category_nodes_parent
  ON documents_category_nodes(tenant_id, parent_path);

CREATE TABLE IF NOT EXISTS documents_catalog_summary (
  tenant_id uuid PRIMARY KEY REFERENCES tenants(tenant_id) ON DELETE CASCADE,
  computed_at timestamptz NOT NULL DEFAULT now(),
  total_docs bigint NOT NULL DEFAULT 0,
  max_depth int NOT NULL DEFAULT 0,
  nodes_by_depth jsonb NOT NULL DEFAULT '{}'::jsonb,
  docs_by_depth jsonb NOT NULL DEFAULT '{}'::jsonb,
  direct_docs_by_depth jsonb NOT NULL DEFAULT '{}'::jsonb,
  top jsonb NOT NULL DEFAULT '[]'::jsonb
);
