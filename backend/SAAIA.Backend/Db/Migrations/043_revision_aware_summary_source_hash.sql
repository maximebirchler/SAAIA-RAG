CREATE OR REPLACE FUNCTION saaia_document_summary_source_hash(
  p_content_hash bytea,
  p_doc_path text,
  p_file_size bigint,
  p_file_mtime timestamptz,
  p_indexed_version integer
)
RETURNS text
LANGUAGE sql
IMMUTABLE
AS $$
SELECT md5(
  COALESCE(
    encode(p_content_hash, 'hex'),
    md5(COALESCE(p_doc_path,'') || '|' || COALESCE(p_file_size::text,'') || '|' || COALESCE(EXTRACT(EPOCH FROM p_file_mtime)::text,''))
  )
  || '|indexed_version=' || COALESCE(p_indexed_version, 0)::text
);
$$;
