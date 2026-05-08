UPDATE admin_jobs
SET doc_path = payload ->> 'docPath'
WHERE job_type = 'summary.generate'
  AND payload ->> 'source' = 'capability_b'
  AND doc_path IS NULL
  AND COALESCE(payload ->> 'docPath', '') <> '';
