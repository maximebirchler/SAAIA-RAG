-- Bounded operational research state retained across worker retries.
-- Documentary content remains in canonical evidence storage and is revalidated.

ALTER TABLE advanced_analysis_jobs
  ADD COLUMN IF NOT EXISTS research_checkpoint JSONB NULL;

ALTER TABLE advanced_analysis_jobs
  DROP CONSTRAINT IF EXISTS ck_advanced_analysis_research_checkpoint;

ALTER TABLE advanced_analysis_jobs
  ADD CONSTRAINT ck_advanced_analysis_research_checkpoint
  CHECK (
    research_checkpoint IS NULL
    OR (
      jsonb_typeof(research_checkpoint) = 'object'
      AND octet_length(research_checkpoint::text) <= 65536));
