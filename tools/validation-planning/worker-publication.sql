-- Static READ ONLY observation, portable across owned Windows/Linux PostgreSQL.
WITH publication AS (
SELECT jsonb_build_object('schemaVersion','saaia-planning-worker-corpus-observation.v1',
 'capturedAtUtc',to_char(clock_timestamp() AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
 'advancedJobCount',(SELECT count(*) FROM advanced_analysis_jobs),
 'activeJobCount',(SELECT count(*) FROM ingestion_jobs WHERE status IN ('queued','running','paused')),
 'documents',COALESCE((SELECT jsonb_agg(jsonb_build_object(
  'tenantId',d.tenant_id,'docId',d.doc_id,'revisionId',r.revision_id,'docPath',d.doc_path,'sourceSha256',encode(r.source_hash,'hex'),
  'pageCount',d.page_count,'status',d.status,'ingestionVersion',d.ingestion_version,'indexedVersion',d.indexed_version,
  'activeVectorPlanFingerprint',d.ingestion_vector_plan_fingerprint,
  'ingestionJob',to_jsonb(j),
  'canonicalArtifact',(SELECT jsonb_build_object('artifactType',b.artifact_type,'schemaVersion',b.schema_version,
    'compression',b.compression,'byteSize',b.byte_size,'storedSize',b.stored_size,'contentHash',encode(b.content_hash,'hex'),
    'payloadBase64',encode(b.payload,'base64')) FROM document_revision_binary_artifacts b
     WHERE b.revision_id=r.revision_id AND b.artifact_type='canonical_ingestion_bundle'),
  'sourceAnchors',COALESCE((SELECT jsonb_agg(jsonb_build_object('anchorId',a.anchor_id,'projectionType',a.projection_type,
    'projectionId',a.projection_id,'precision',a.precision,'pageStart',a.page_start,'pageEnd',a.page_end,
    'contentHash',encode(a.content_hash,'hex'),'manifestHash',encode(a.manifest_hash,'hex'),'canonicalAnchor',a.payload)
    ORDER BY a.anchor_id) FROM document_source_anchors a WHERE a.revision_id=r.revision_id),'[]'::jsonb),
  'nativeRawCatalog',(SELECT (to_jsonb(n)-'payload'-'content_hash'-'canonical_bundle_hash'-'source_hash'-'generation_id'-'manifest_hash') ||
    jsonb_build_object('payloadBase64',encode(n.payload,'base64'),'contentHash',encode(n.content_hash,'hex'),
     'canonicalBundleHash',encode(n.canonical_bundle_hash,'hex'),'sourceHash',encode(n.source_hash,'hex'),
     'generationId',encode(n.generation_id,'hex'),'manifestHash',encode(n.manifest_hash,'hex'))
    FROM document_revision_native_raw_catalog n WHERE n.revision_id=r.revision_id),
  'nativeRawPages',COALESCE((SELECT jsonb_agg((to_jsonb(n)-'payload'-'content_hash'-'source_hash'-'generation_id') ||
    jsonb_build_object('payloadBase64',encode(n.payload,'base64'),'contentHash',encode(n.content_hash,'hex'),
     'sourceHash',encode(n.source_hash,'hex'),'generationId',encode(n.generation_id,'hex')) ORDER BY n.page_number)
    FROM document_revision_native_raw_pages n WHERE n.revision_id=r.revision_id),'[]'::jsonb),
  'profiles',COALESCE((SELECT jsonb_agg(to_jsonb(p)) FROM document_profiles p WHERE p.revision_id=r.revision_id),'[]'::jsonb),
  'processingRuns',COALESCE((SELECT jsonb_agg(to_jsonb(p)) FROM document_processing_runs p WHERE p.revision_id=r.revision_id),'[]'::jsonb))
  ORDER BY d.doc_path) FROM documents d JOIN document_revisions r ON r.doc_id=d.doc_id AND r.indexed_version=d.indexed_version
  JOIN LATERAL (SELECT * FROM ingestion_jobs j0 WHERE j0.doc_path=d.doc_path ORDER BY j0.finished_at DESC NULLS LAST LIMIT 1) j ON true),'[]'::jsonb))
), projections AS (
SELECT jsonb_build_object('schemaVersion','saaia-planning-corpus-sql-observation.v1',
 'retrievalChunks',(SELECT coalesce(jsonb_agg(jsonb_build_object('retrievalChunkId',retrieval_chunk_id,'revisionId',revision_id,
   'sectionId',section_id,'unitId',unit_id,'chunkIndex',chunk_index,'pageStart',page_start,'pageEnd',page_end,
   'text',text_content,'checksumSha256',encode(checksum,'hex'),'metadata',metadata) ORDER BY revision_id,chunk_index),'[]'::jsonb) FROM retrieval_chunks),
 'units',(SELECT coalesce(jsonb_agg(jsonb_build_object('unitId',unit_id,'revisionId',revision_id,'sectionId',section_id,
   'ordinal',ordinal,'pageStart',page_start,'pageEnd',page_end,'text',text_content,'checksumSha256',encode(checksum,'hex'),
   'metadata',metadata) ORDER BY revision_id,ordinal),'[]'::jsonb) FROM document_units),
 'sections',(SELECT coalesce(jsonb_agg(jsonb_build_object('sectionId',section_id,'revisionId',revision_id,'ordinal',ordinal,
   'title',title,'pageStart',page_start,'pageEnd',page_end) ORDER BY revision_id,ordinal),'[]'::jsonb) FROM document_sections),
 'contentCards',(SELECT coalesce(jsonb_agg(jsonb_build_object('contentCardId',content_card_id,'docId',doc_id,'revisionId',revision_id,
   'documentProfileId',document_profile_id,'cardIndex',card_index,'title',title,'normalizedTitle',normalized_title,
   'pageStart',page_start,'pageEnd',page_end,'searchText',search_text,'checksumSha256',encode(checksum,'hex'),
   'metadata',metadata) ORDER BY revision_id,card_index),'[]'::jsonb) FROM document_profile_content_cards))
)
SELECT (SELECT * FROM publication) || ((SELECT * FROM projections)-'schemaVersion');
