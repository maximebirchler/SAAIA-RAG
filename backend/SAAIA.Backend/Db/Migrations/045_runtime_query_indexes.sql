-- Additive indexes for the enriched ingestion/RAG runtime.
--
-- Keep this migration non-destructive. Profile/content-card quality should be
-- corrected by versioned ingestion/profile regeneration, not by global DELETE
-- sweeps during service startup.

CREATE INDEX IF NOT EXISTS ix_admin_jobs_capability_b_queued_pick
  ON admin_jobs (
    (
      COALESCE(
        CASE
          WHEN jsonb_typeof(payload->'priorityScore') = 'number'
            THEN (payload->>'priorityScore')::int
          ELSE NULL::int
        END,
        0
      )
    ) DESC,
    created_at ASC
  )
  WHERE job_type = 'summary.generate'
    AND status = 'queued'
    AND payload ->> 'source' = 'capability_b';

CREATE INDEX IF NOT EXISTS ix_ingestion_jobs_tenant_status_activity
  ON ingestion_jobs (
    tenant_id,
    status,
    (COALESCE(locked_at, started_at, finished_at, created_at)) DESC
  );

CREATE INDEX IF NOT EXISTS ix_document_summaries_fresh_source
  ON document_summaries (
    tenant_id,
    doc_id,
    level,
    source_hash,
    updated_at DESC,
    created_at DESC
  );

CREATE INDEX IF NOT EXISTS ix_document_processing_runs_latest_done_upsert_by_indexed_version
  ON document_processing_runs (
    tenant_id,
    doc_id,
    indexed_version_after,
    finished_at DESC NULLS LAST,
    started_at DESC NULLS LAST
  )
  WHERE action = 'upsert'
    AND status = 'done';

CREATE INDEX IF NOT EXISTS ix_document_revisions_doc_version_published
  ON document_revisions (
    tenant_id,
    doc_id,
    indexed_version,
    published_at DESC NULLS LAST
  );

CREATE INDEX IF NOT EXISTS ix_document_profiles_revision_preferred
  ON document_profiles (
    tenant_id,
    revision_id,
    profile_version,
    updated_at DESC
  );

CREATE INDEX IF NOT EXISTS ix_document_profiles_rag_preferred_revision
  ON document_profiles (
    tenant_id,
    revision_id,
    (CASE profile_version
      WHEN 'llm_backoffice_v1' THEN 0
      WHEN 'deterministic_v1' THEN 1
      ELSE 2
    END),
    updated_at DESC NULLS LAST
  )
  INCLUDE (
    document_profile_id,
    doc_id,
    profile_version,
    language
  );

CREATE INDEX IF NOT EXISTS ix_document_profile_content_cards_profile_order_cover
  ON document_profile_content_cards (
    tenant_id,
    document_profile_id,
    card_index
  )
  INCLUDE (
    title,
    page_start,
    page_end,
    kind,
    signals,
    search_text,
    normalized_title
  );
