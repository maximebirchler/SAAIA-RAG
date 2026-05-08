ALTER TABLE admin_jobs
  DROP CONSTRAINT IF EXISTS admin_jobs_status_check;

ALTER TABLE admin_jobs
  ADD CONSTRAINT admin_jobs_status_check
  CHECK (status IN ('queued','running','paused','done','failed','canceled'));
