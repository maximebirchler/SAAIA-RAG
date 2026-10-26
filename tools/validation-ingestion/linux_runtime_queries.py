"""Static read-only PostgreSQL observations, shared captured schema with Windows."""
BASELINE = """SELECT json_build_object('documents',(SELECT count(*) FROM documents),'jobs',(SELECT count(*) FROM ingestion_jobs),'revisions',(SELECT count(*) FROM document_revisions),'units',(SELECT count(*) FROM document_units),'chunks',(SELECT count(*) FROM retrieval_chunks),'canonicalBundles',(SELECT count(*) FROM document_revision_binary_artifacts))"""

PUBLICATION = r"""SELECT json_build_object('schemaVersion','saaia-owned-worker-observation.v1',
 'activeJobs',(SELECT count(*) FROM ingestion_jobs WHERE status IN ('queued','running','paused')),
 'jobs',COALESCE((SELECT json_agg(json_build_object('jobId',job_id,'docPath',doc_path,'status',status,'attempts',attempts,'payload',payload) ORDER BY doc_path) FROM ingestion_jobs),'[]'::json),
 'documents',COALESCE((SELECT json_agg(json_build_object('docPath',d.doc_path,'docId',d.doc_id,'revisionId',r.revision_id,
  'status',d.status,'indexedVersion',d.indexed_version,'pageCount',d.page_count,'sourceSha256',encode(r.source_hash,'hex'),
  'activeVectorPlanFingerprint',d.ingestion_vector_plan_fingerprint,
  'sourceAnchorCount',(SELECT count(*) FROM document_source_anchors a WHERE a.revision_id=r.revision_id),
  'chunks',COALESCE((SELECT json_agg(json_build_object('retrievalChunkId',c.retrieval_chunk_id,'chunkIndex',c.chunk_index,'pageStart',c.page_start,'pageEnd',c.page_end,
    'text',c.text_content,'checksumSha256',encode(c.checksum,'hex'),'metadata',c.metadata) ORDER BY c.chunk_index) FROM retrieval_chunks c WHERE c.revision_id=r.revision_id),'[]'::json),
  'binaryArtifacts',COALESCE((SELECT json_agg(json_build_object('artifactType',b.artifact_type,'schemaVersion',b.schema_version,'compression',b.compression,
    'byteSize',b.byte_size,'storedSize',b.stored_size,'contentHash',encode(b.content_hash,'hex'),'payloadBase64',encode(b.payload,'base64')))
    FROM document_revision_binary_artifacts b WHERE b.revision_id=r.revision_id AND b.artifact_type='canonical_ingestion_bundle'),'[]'::json)) ORDER BY d.doc_path)
  FROM documents d LEFT JOIN document_revisions r ON r.tenant_id=d.tenant_id AND r.doc_id=d.doc_id AND r.indexed_version=d.indexed_version),'[]'::json))"""

ACTIVE = "SELECT count(*) FROM ingestion_jobs WHERE status IN ('queued','running','paused')"

TEXT_CHECKSUMS = r"""
SELECT json_build_object('tables',json_build_array(
 json_build_object('table','document_units','rows',(SELECT count(*) FROM document_units),'mismatches',(SELECT count(*) FROM document_units WHERE checksum IS NULL OR octet_length(checksum) <> 32 OR checksum <> sha256(convert_to(text_content,'UTF8')))),
 json_build_object('table','retrieval_chunks','rows',(SELECT count(*) FROM retrieval_chunks),'mismatches',(SELECT count(*) FROM retrieval_chunks WHERE checksum IS NULL OR octet_length(checksum) <> 32 OR checksum <> sha256(convert_to(text_content,'UTF8')))),
 json_build_object('table','contextual_text_entries','rows',(SELECT count(*) FROM contextual_text_entries),'mismatches',(SELECT count(*) FROM contextual_text_entries WHERE checksum IS NULL OR octet_length(checksum) <> 32 OR checksum <> sha256(convert_to(text_content,'UTF8')))),
 json_build_object('table','exact_match_entries','rows',(SELECT count(*) FROM exact_match_entries),'mismatches',(SELECT count(*) FROM exact_match_entries WHERE checksum IS NULL OR octet_length(checksum) <> 32 OR checksum <> sha256(convert_to(normalized_text,'UTF8'))))))
"""

TEXT_PARSER = r"""
SELECT json_build_object(
 'unicode_punctuation', to_tsvector('simple', U&'alpha\2026beta\2014gamma') @@ to_tsquery('simple','alpha & beta & gamma'),
 'unicode_case',to_tsvector('simple',U&'\00C9COLE') @@ to_tsquery('simple',U&'\00E9cole'),
 'conjunction',to_tsvector('simple','alpha beta') @@ to_tsquery('simple','alpha & beta')
 AND NOT to_tsvector('simple','alpha') @@ to_tsquery('simple','alpha & beta')
 AND NOT to_tsvector('simple','beta') @@ to_tsquery('simple','alpha & beta'),
 'accent_preserved',NOT to_tsvector('simple',U&'caf\00E9') @@ to_tsquery('simple','cafe'))
"""
