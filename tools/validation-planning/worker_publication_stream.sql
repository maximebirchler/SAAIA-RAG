-- Named static SELECT templates. The reader owns ONE REPEATABLE READ READ ONLY
-- session. No global JSON aggregate; all data rows and binary slices are streamed.
--@ header
SELECT jsonb_build_object('schemaVersion','saaia-planning-worker-corpus-observation.v1',
 'capturedAtUtc',to_char(clock_timestamp() AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
 'advancedJobCount',(SELECT count(*) FROM advanced_analysis_jobs),
 'activeJobCount',(SELECT count(*) FROM ingestion_jobs WHERE status IN ('queued','running','paused')))::text;
--@ counts
SELECT jsonb_build_object('documents',(SELECT count(*) FROM documents),
 'retrievalChunks',(SELECT count(*) FROM retrieval_chunks),'units',(SELECT count(*) FROM document_units),
 'sections',(SELECT count(*) FROM document_sections),'contentCards',(SELECT count(*) FROM document_profile_content_cards))::text;
--@ cell_preflight
-- TEXT lengths use TOAST logical lengths without constructing JSON strings.
-- JSONB storage is observed only: compressed size is NOT a logical memory bound.
SELECT jsonb_build_object('maximumTextBytes',greatest(
 COALESCE((SELECT max(octet_length(text_content)) FROM retrieval_chunks),0),
 COALESCE((SELECT max(octet_length(text_content)) FROM document_units),0),
 COALESCE((SELECT max(octet_length(title)) FROM document_sections),0),
 COALESCE((SELECT max(greatest(octet_length(title),octet_length(normalized_title),octet_length(search_text))) FROM document_profile_content_cards),0),
 COALESCE((SELECT max(greatest(octet_length(summary_text),octet_length(search_text))) FROM document_profiles),0),
 COALESCE((SELECT max(greatest(octet_length(doc_path),octet_length(last_error))) FROM ingestion_jobs),0)),
 'maximumJsonbStoredBytes',greatest(
 COALESCE((SELECT max(pg_column_size(metadata)) FROM retrieval_chunks),0),
 COALESCE((SELECT max(pg_column_size(metadata)) FROM document_units),0),
 COALESCE((SELECT max(pg_column_size(payload)) FROM document_source_anchors),0),
 COALESCE((SELECT max(pg_column_size(metadata)) FROM document_profiles),0),
 COALESCE((SELECT max(pg_column_size(metadata)) FROM document_profile_content_cards),0),
 COALESCE((SELECT max(pg_column_size(payload)) FROM ingestion_jobs),0),
 COALESCE((SELECT max(pg_column_size(payload)) FROM document_processing_runs),0)))::text;
--@ native_schema
SELECT table_name || E'\t' || column_name FROM information_schema.columns
WHERE table_schema='public' AND table_name IN ('document_revision_native_raw_catalog','document_revision_native_raw_pages')
ORDER BY table_name,ordinal_position;
--@ documents
SELECT jsonb_build_object('tenantId',d.tenant_id,'docId',d.doc_id,'revisionId',r.revision_id,'docPath',d.doc_path,
 'sourceSha256',encode(r.source_hash,'hex'),'pageCount',d.page_count,'status',d.status,
 'ingestionVersion',d.ingestion_version,'indexedVersion',d.indexed_version,
 'activeVectorPlanFingerprint',d.ingestion_vector_plan_fingerprint,'ingestionJob',to_jsonb(j))::text
FROM documents d JOIN document_revisions r ON r.doc_id=d.doc_id AND r.indexed_version=d.indexed_version
JOIN LATERAL (SELECT * FROM ingestion_jobs j0 WHERE j0.doc_path=d.doc_path
 ORDER BY j0.finished_at DESC NULLS LAST LIMIT 1) j ON true ORDER BY d.doc_path;
--@ document_counts
SELECT jsonb_build_object('canonicalArtifact',(SELECT count(*) FROM document_revision_binary_artifacts WHERE revision_id='{{revision}}'::uuid AND artifact_type='canonical_ingestion_bundle'),
 'sourceAnchors',(SELECT count(*) FROM document_source_anchors WHERE revision_id='{{revision}}'::uuid),
 'nativeRawCatalog',(SELECT count(*) FROM document_revision_native_raw_catalog WHERE revision_id='{{revision}}'::uuid),
 'nativeRawPages',(SELECT count(*) FROM document_revision_native_raw_pages WHERE revision_id='{{revision}}'::uuid),
 'profiles',(SELECT count(*) FROM document_profiles WHERE revision_id='{{revision}}'::uuid),
 'processingRuns',(SELECT count(*) FROM document_processing_runs WHERE revision_id='{{revision}}'::uuid))::text;
--@ canonicalArtifact
SELECT COALESCE(octet_length(b.payload),-1)::text || E'\t' || jsonb_build_object(
 'artifactType',b.artifact_type,'schemaVersion',b.schema_version,'compression',b.compression,
 'byteSize',b.byte_size,'storedSize',b.stored_size,'contentHash',encode(b.content_hash,'hex'))::text
FROM document_revision_binary_artifacts b WHERE b.revision_id='{{revision}}'::uuid AND b.artifact_type='canonical_ingestion_bundle';
--@ canonicalArtifact_payload
SELECT to_json(CASE WHEN s.byte_offset+58368<octet_length(b.payload)
 THEN rtrim(encode(substring(b.payload FROM s.byte_offset+1 FOR 58368),'base64'),E'\n') || E'\n'
 ELSE encode(substring(b.payload FROM s.byte_offset+1 FOR 58368),'base64') END)::text
FROM document_revision_binary_artifacts b CROSS JOIN LATERAL generate_series(0,octet_length(b.payload)-1,58368) s(byte_offset)
WHERE b.revision_id='{{revision}}'::uuid AND b.artifact_type='canonical_ingestion_bundle' ORDER BY s.byte_offset;
--@ sourceAnchors
SELECT jsonb_build_object('anchorId',a.anchor_id,'projectionType',a.projection_type,'projectionId',a.projection_id,
 'precision',a.precision,'pageStart',a.page_start,'pageEnd',a.page_end,'contentHash',encode(a.content_hash,'hex'),
 'manifestHash',encode(a.manifest_hash,'hex'),'canonicalAnchor',a.payload)::text
FROM document_source_anchors a WHERE a.revision_id='{{revision}}'::uuid ORDER BY a.anchor_id;
--@ nativeRawCatalog
SELECT COALESCE(octet_length(n.payload),-1)::text || E'\t' || jsonb_build_object(
 'tenant_id',n.tenant_id,'doc_id',n.doc_id,'revision_id',n.revision_id,'projection_version',n.projection_version,
 'processing_run_id',n.processing_run_id,'mode',n.mode,'reason',n.reason,'page_count',n.page_count,'created_at',n.created_at,
 'contentHash',encode(n.content_hash,'hex'),'canonicalBundleHash',encode(n.canonical_bundle_hash,'hex'),
 'sourceHash',encode(n.source_hash,'hex'),'generationId',encode(n.generation_id,'hex'),'manifestHash',encode(n.manifest_hash,'hex'))::text
FROM document_revision_native_raw_catalog n WHERE n.revision_id='{{revision}}'::uuid;
--@ nativeRawCatalog_payload
SELECT to_json(CASE WHEN s.byte_offset+58368<octet_length(n.payload)
 THEN rtrim(encode(substring(n.payload FROM s.byte_offset+1 FOR 58368),'base64'),E'\n') || E'\n'
 ELSE encode(substring(n.payload FROM s.byte_offset+1 FOR 58368),'base64') END)::text
FROM document_revision_native_raw_catalog n CROSS JOIN LATERAL generate_series(0,octet_length(n.payload)-1,58368) s(byte_offset)
WHERE n.revision_id='{{revision}}'::uuid ORDER BY s.byte_offset;
--@ nativeRawPages
SELECT n.page_number::text || E'\t' || COALESCE(octet_length(n.payload),-1)::text || E'\t' ||
 jsonb_build_object('tenant_id',n.tenant_id,'doc_id',n.doc_id,'revision_id',n.revision_id,'page_number',n.page_number,
 'availability',n.availability,'reason',n.reason,'block_count',n.block_count,'compression',n.compression,
 'byte_size',n.byte_size,'stored_size',n.stored_size,'contentHash',encode(n.content_hash,'hex'),
 'sourceHash',encode(n.source_hash,'hex'),'generationId',encode(n.generation_id,'hex'))::text
FROM document_revision_native_raw_pages n WHERE n.revision_id='{{revision}}'::uuid ORDER BY n.page_number;
--@ nativeRawPages_payload
SELECT to_json(CASE WHEN s.byte_offset+58368<octet_length(n.payload)
 THEN rtrim(encode(substring(n.payload FROM s.byte_offset+1 FOR 58368),'base64'),E'\n') || E'\n'
 ELSE encode(substring(n.payload FROM s.byte_offset+1 FOR 58368),'base64') END)::text
FROM document_revision_native_raw_pages n CROSS JOIN LATERAL generate_series(0,octet_length(n.payload)-1,58368) s(byte_offset)
WHERE n.revision_id='{{revision}}'::uuid AND n.page_number={{page}} ORDER BY s.byte_offset;
--@ profiles
SELECT to_jsonb(p)::text FROM document_profiles p WHERE p.revision_id='{{revision}}'::uuid;
--@ processingRuns
SELECT to_jsonb(p)::text FROM document_processing_runs p WHERE p.revision_id='{{revision}}'::uuid;
--@ retrievalChunks
SELECT jsonb_build_object('retrievalChunkId',retrieval_chunk_id,'revisionId',revision_id,'sectionId',section_id,'unitId',unit_id,
 'chunkIndex',chunk_index,'pageStart',page_start,'pageEnd',page_end,'text',text_content,'checksumSha256',encode(checksum,'hex'),'metadata',metadata)::text
FROM retrieval_chunks ORDER BY revision_id,chunk_index;
--@ units
SELECT jsonb_build_object('unitId',unit_id,'revisionId',revision_id,'sectionId',section_id,'ordinal',ordinal,
 'pageStart',page_start,'pageEnd',page_end,'text',text_content,'checksumSha256',encode(checksum,'hex'),'metadata',metadata)::text
FROM document_units ORDER BY revision_id,ordinal;
--@ sections
SELECT jsonb_build_object('sectionId',section_id,'revisionId',revision_id,'ordinal',ordinal,'title',title,'pageStart',page_start,'pageEnd',page_end)::text
FROM document_sections ORDER BY revision_id,ordinal;
--@ contentCards
SELECT jsonb_build_object('contentCardId',content_card_id,'docId',doc_id,'revisionId',revision_id,'documentProfileId',document_profile_id,
 'cardIndex',card_index,'title',title,'normalizedTitle',normalized_title,'pageStart',page_start,'pageEnd',page_end,'searchText',search_text,
 'checksumSha256',encode(checksum,'hex'),'metadata',metadata)::text FROM document_profile_content_cards ORDER BY revision_id,card_index;
