-- Bind resumable advanced-analysis jobs to the provider and model that first
-- acquired them. A retry must fail explicitly after a configuration change
-- instead of continuing one logical execution on a different LLM.

ALTER TABLE advanced_analysis_jobs
  ADD COLUMN IF NOT EXISTS provider_model TEXT NULL;

ALTER TABLE advanced_analysis_jobs
  DROP CONSTRAINT IF EXISTS ck_advanced_analysis_provider_model;

ALTER TABLE advanced_analysis_jobs
  ADD CONSTRAINT ck_advanced_analysis_provider_model
  CHECK (provider_model IS NULL OR length(provider_model) <= 256);

CREATE INDEX IF NOT EXISTS ix_advanced_analysis_provider_affinity
  ON advanced_analysis_jobs(status, provider_key, provider_model, available_at)
  WHERE status = 'queued';
