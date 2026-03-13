-- 011_summary_schema_alignment.sql
-- Reconcile summary/admin job column names with runtime code expectations.
-- Safe on both fresh and already-patched databases.

DO $$
BEGIN
  -- document_summaries.meta_json -> summary_meta
  IF EXISTS (
    SELECT 1
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'document_summaries' AND column_name = 'meta_json'
  ) AND NOT EXISTS (
    SELECT 1
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'document_summaries' AND column_name = 'summary_meta'
  ) THEN
    ALTER TABLE document_summaries RENAME COLUMN meta_json TO summary_meta;
  END IF;

  IF NOT EXISTS (
    SELECT 1
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'document_summaries' AND column_name = 'summary_meta'
  ) THEN
    ALTER TABLE document_summaries ADD COLUMN summary_meta jsonb NOT NULL DEFAULT '{}'::jsonb;
  END IF;

  IF EXISTS (
    SELECT 1
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'document_summaries' AND column_name = 'meta_json'
  ) AND EXISTS (
    SELECT 1
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'document_summaries' AND column_name = 'summary_meta'
  ) THEN
    EXECUTE 'UPDATE document_summaries SET summary_meta = COALESCE(summary_meta, meta_json, ''{}''::jsonb)';
    ALTER TABLE document_summaries DROP COLUMN meta_json;
  END IF;

  EXECUTE 'UPDATE document_summaries SET summary_meta = COALESCE(summary_meta, ''{}''::jsonb)';
  ALTER TABLE document_summaries ALTER COLUMN summary_meta SET DEFAULT '{}'::jsonb;
  ALTER TABLE document_summaries ALTER COLUMN summary_meta SET NOT NULL;

  -- admin_jobs.result_json -> result
  IF EXISTS (
    SELECT 1
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'admin_jobs' AND column_name = 'result_json'
  ) AND NOT EXISTS (
    SELECT 1
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'admin_jobs' AND column_name = 'result'
  ) THEN
    ALTER TABLE admin_jobs RENAME COLUMN result_json TO result;
  END IF;

  IF NOT EXISTS (
    SELECT 1
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'admin_jobs' AND column_name = 'result'
  ) THEN
    ALTER TABLE admin_jobs ADD COLUMN result jsonb NULL;
  END IF;

  IF EXISTS (
    SELECT 1
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'admin_jobs' AND column_name = 'result_json'
  ) AND EXISTS (
    SELECT 1
    FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'admin_jobs' AND column_name = 'result'
  ) THEN
    EXECUTE 'UPDATE admin_jobs SET result = COALESCE(result, result_json)';
    ALTER TABLE admin_jobs DROP COLUMN result_json;
  END IF;
END $$;
