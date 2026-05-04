DELETE FROM document_profile_content_cards pcc
USING documents d, document_revisions r
WHERE pcc.tenant_id = d.tenant_id
  AND pcc.doc_id = d.doc_id
  AND r.tenant_id = d.tenant_id
  AND r.doc_id = d.doc_id
  AND r.indexed_version = d.indexed_version
  AND pcc.revision_id <> r.revision_id;
