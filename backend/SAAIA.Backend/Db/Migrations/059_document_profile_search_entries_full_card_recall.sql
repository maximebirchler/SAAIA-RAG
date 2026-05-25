DO $$
DECLARE
  refresh_function_sql text;
BEGIN
  SELECT pg_get_functiondef('saaia_refresh_document_profile_search_entry(uuid, uuid)'::regprocedure)
    INTO refresh_function_sql;

  IF refresh_function_sql IS NULL THEN
    RAISE EXCEPTION 'saaia_refresh_document_profile_search_entry(uuid, uuid) is missing';
  END IF;

  refresh_function_sql := REPLACE(refresh_function_sql, E'\n      LIMIT 80\n', E'\n');
  EXECUTE refresh_function_sql;
END $$;

SELECT saaia_refresh_document_profile_search_entry(r.tenant_id, r.revision_id)
FROM document_revisions r
JOIN documents d
  ON d.tenant_id = r.tenant_id
 AND d.doc_id = r.doc_id
 AND d.indexed_version = r.indexed_version
WHERE d.status = 'indexed'
  AND d.indexed_version > 0;
