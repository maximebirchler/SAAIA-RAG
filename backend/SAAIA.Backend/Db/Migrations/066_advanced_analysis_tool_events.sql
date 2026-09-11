-- Bounded, metadata-only tool trace for resumable advanced-analysis attempts.

ALTER TABLE advanced_analysis_jobs
  ADD COLUMN IF NOT EXISTS tool_event_count INTEGER NOT NULL DEFAULT 0;

ALTER TABLE advanced_analysis_jobs
  DROP CONSTRAINT IF EXISTS ck_advanced_analysis_tool_event_count;

ALTER TABLE advanced_analysis_jobs
  ADD CONSTRAINT ck_advanced_analysis_tool_event_count
  CHECK (tool_event_count BETWEEN 0 AND 2048);

CREATE UNIQUE INDEX IF NOT EXISTS ux_advanced_analysis_tenant_job
  ON advanced_analysis_jobs(tenant_id, job_id);

CREATE TABLE IF NOT EXISTS advanced_analysis_tool_events (
  tenant_id UUID NOT NULL,
  job_id UUID NOT NULL,
  event_sequence INTEGER NOT NULL,
  attempt_count INTEGER NOT NULL,
  worker_id TEXT NOT NULL,
  tool_name TEXT NOT NULL,
  status TEXT NOT NULL,
  request JSONB NOT NULL,
  evidence_references JSONB NOT NULL DEFAULT '[]'::jsonb,
  degraded_retrievers TEXT[] NOT NULL DEFAULT ARRAY[]::text[],
  elapsed_milliseconds BIGINT NOT NULL DEFAULT 0,
  error_code TEXT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (job_id, event_sequence),
  CONSTRAINT fk_advanced_analysis_tool_event_job
    FOREIGN KEY (tenant_id, job_id)
    REFERENCES advanced_analysis_jobs(tenant_id, job_id)
    ON DELETE CASCADE,
  CONSTRAINT ck_advanced_analysis_tool_event_sequence
    CHECK (event_sequence BETWEEN 1 AND 2048),
  CONSTRAINT ck_advanced_analysis_tool_event_attempt
    CHECK (attempt_count >= 1),
  CONSTRAINT ck_advanced_analysis_tool_event_worker
    CHECK (length(btrim(worker_id)) BETWEEN 1 AND 300),
  CONSTRAINT ck_advanced_analysis_tool_event_name
    CHECK (tool_name IN ('source_backed_canonical_search')),
  CONSTRAINT ck_advanced_analysis_tool_event_status
    CHECK (status IN ('succeeded', 'failed')),
  CONSTRAINT ck_advanced_analysis_tool_event_request
    CHECK (jsonb_typeof(request) = 'object'),
  CONSTRAINT ck_advanced_analysis_tool_event_evidence
    CHECK (
      jsonb_typeof(evidence_references) = 'array'
      AND jsonb_array_length(evidence_references) <= 256),
  CONSTRAINT ck_advanced_analysis_tool_event_elapsed
    CHECK (elapsed_milliseconds BETWEEN 0 AND 3600000)
);

CREATE INDEX IF NOT EXISTS ix_advanced_analysis_tool_event_tenant_job
  ON advanced_analysis_tool_events(tenant_id, job_id, event_sequence);
