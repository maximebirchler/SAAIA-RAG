-- Derived raw observations. The canonical bundle remains the publication authority.
CREATE TABLE document_revision_native_raw_catalog (
  tenant_id uuid NOT NULL, doc_id uuid NOT NULL, revision_id uuid NOT NULL,
  source_hash bytea NOT NULL CHECK(octet_length(source_hash)=32),
  generation_id bytea NOT NULL CHECK(octet_length(generation_id)=32),
  projection_version text NOT NULL,
  processing_run_id uuid NOT NULL,
  canonical_bundle_hash bytea NOT NULL CHECK(octet_length(canonical_bundle_hash)=32),
  manifest_hash bytea NOT NULL CHECK(octet_length(manifest_hash)=32),
  mode text NOT NULL CHECK(mode IN ('raw_pages','inline_fallback')),
  reason text NULL, page_count integer NOT NULL CHECK(page_count>=0),
  content_hash bytea NOT NULL CHECK(octet_length(content_hash)=32),
  payload bytea NOT NULL CHECK(octet_length(payload) BETWEEN 2 AND 1048576),
  created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY(tenant_id,revision_id),
  UNIQUE(tenant_id,doc_id,revision_id,source_hash,generation_id),
  FOREIGN KEY(tenant_id,doc_id,revision_id,source_hash)
    REFERENCES document_revisions(tenant_id,doc_id,revision_id,source_hash) ON DELETE CASCADE,
  CHECK((mode='raw_pages' AND reason IS NULL) OR (mode='inline_fallback' AND reason IS NOT NULL))
);
CREATE TABLE document_revision_native_raw_pages (
  tenant_id uuid NOT NULL, doc_id uuid NOT NULL, revision_id uuid NOT NULL,
  source_hash bytea NOT NULL CHECK(octet_length(source_hash)=32),
  generation_id bytea NOT NULL CHECK(octet_length(generation_id)=32),
  page_number integer NOT NULL CHECK(page_number>0),
  availability text NOT NULL CHECK(availability IN ('observed','unavailable')),
  reason text NULL, block_count integer NULL CHECK(block_count>0),
  content_hash bytea NOT NULL CHECK(octet_length(content_hash)=32),
  compression text NOT NULL CHECK(compression='gzip'),
  byte_size integer NOT NULL CHECK(byte_size BETWEEN 2 AND 4194304),
  stored_size integer NOT NULL CHECK(stored_size BETWEEN 1 AND 4194304),
  payload bytea NOT NULL CHECK(octet_length(payload)=stored_size),
  PRIMARY KEY(tenant_id,revision_id,generation_id,page_number),
  FOREIGN KEY(tenant_id,doc_id,revision_id,source_hash,generation_id)
    REFERENCES document_revision_native_raw_catalog(tenant_id,doc_id,revision_id,source_hash,generation_id)
    ON DELETE CASCADE,
  CHECK((availability='observed' AND block_count IS NOT NULL AND reason IS NULL)
     OR (availability='unavailable' AND block_count IS NULL AND reason IS NOT NULL))
);
