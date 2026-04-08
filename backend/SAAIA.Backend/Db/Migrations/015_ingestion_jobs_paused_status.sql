DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'ingestion_jobs_status_check'
    ) THEN
        ALTER TABLE ingestion_jobs DROP CONSTRAINT ingestion_jobs_status_check;
    END IF;
END $$;

ALTER TABLE ingestion_jobs
    ADD CONSTRAINT ingestion_jobs_status_check
    CHECK (status IN ('queued','running','paused','done','failed','canceled'));

DROP INDEX IF EXISTS ux_jobs_active;
CREATE UNIQUE INDEX IF NOT EXISTS ux_jobs_active
  ON ingestion_jobs(tenant_id, doc_path, action)
  WHERE status IN ('queued','running','paused');
