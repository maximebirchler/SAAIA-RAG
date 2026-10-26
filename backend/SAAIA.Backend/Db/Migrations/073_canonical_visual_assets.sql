CREATE TABLE IF NOT EXISTS document_revision_visual_assets (
  tenant_id uuid NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
  revision_id uuid NOT NULL REFERENCES document_revisions(revision_id) ON DELETE CASCADE,
  asset_id text NOT NULL,
  page_number integer NOT NULL CHECK (page_number > 0),
  figure_id text NULL,
  source_hash bytea NOT NULL CHECK (octet_length(source_hash) = 32),
  content_hash bytea NOT NULL CHECK (octet_length(content_hash) = 32),
  media_type text NOT NULL CHECK (media_type = 'image/png'),
  pixel_width integer NOT NULL CHECK (pixel_width BETWEEN 1 AND 8192),
  pixel_height integer NOT NULL CHECK (pixel_height BETWEEN 1 AND 8192),
  payload bytea NOT NULL CHECK (octet_length(payload) BETWEEN 57 AND 16777216),
  created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, revision_id, asset_id),
  CHECK (pixel_width::bigint * pixel_height::bigint <= 16000000)
);
CREATE INDEX IF NOT EXISTS ix_revision_visual_assets_pages
  ON document_revision_visual_assets(tenant_id, revision_id, page_number);
