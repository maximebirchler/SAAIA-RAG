-- Content-free immutable history of durable state transitions. Existing migrations stay unchanged.
ALTER TABLE advanced_analysis_jobs ADD COLUMN IF NOT EXISTS parent_traceparent text NULL;
ALTER TABLE advanced_analysis_jobs ADD COLUMN IF NOT EXISTS diagnostic_request_id text NULL;
ALTER TABLE advanced_analysis_jobs ADD COLUMN IF NOT EXISTS diagnostic_turn_id text NULL;
ALTER TABLE advanced_analysis_jobs ADD COLUMN IF NOT EXISTS diagnostic_session_id text NULL;

ALTER TABLE advanced_analysis_tool_events DROP CONSTRAINT ck_advanced_analysis_tool_event_name;
ALTER TABLE advanced_analysis_tool_events ADD CONSTRAINT ck_advanced_analysis_tool_event_name
  CHECK (tool_name IN ('source_backed_canonical_search', 'source_backed_canonical_read', 'source_backed_canonical_find'));

CREATE TABLE IF NOT EXISTS advanced_analysis_job_events (
  event_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  tenant_id uuid NOT NULL,
  job_id uuid NOT NULL REFERENCES advanced_analysis_jobs(job_id) ON DELETE CASCADE,
  occurred_at timestamptz NOT NULL DEFAULT clock_timestamp(),
  event_name text NOT NULL,
  previous_status text NULL,
  status text NOT NULL,
  attempt_count integer NOT NULL,
  worker_id text NULL,
  previous_error_code text NULL,
  error_code text NULL,
  available_at timestamptz NOT NULL,
  lease_expires_at timestamptz NULL,
  queue_wait_milliseconds double precision NULL,
  parent_traceparent text NULL,
  request_id text NULL,
  turn_id text NULL,
  session_id text NOT NULL,
  client_session_id text NULL,
  client_session_id_mismatch boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS ix_advanced_analysis_job_events_job
  ON advanced_analysis_job_events(tenant_id, job_id, event_id);

CREATE OR REPLACE FUNCTION record_advanced_analysis_job_transition()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
  event_name_value text;
  old_status text;
  old_error text;
  previous_worker text;
  queue_wait double precision;
BEGIN
  IF TG_OP = 'INSERT' THEN
    event_name_value := 'queued';
  ELSE
    old_status := OLD.status;
    old_error := OLD.last_error_code;
    previous_worker := OLD.lease_owner;
    IF OLD.status IS NOT DISTINCT FROM NEW.status
      AND OLD.cancel_requested_at IS NOT DISTINCT FROM NEW.cancel_requested_at
      AND OLD.attempt_count IS NOT DISTINCT FROM NEW.attempt_count
      AND OLD.last_error_code IS NOT DISTINCT FROM NEW.last_error_code
      AND OLD.lease_owner IS NOT DISTINCT FROM NEW.lease_owner THEN
      RETURN NEW; -- Heartbeats/checkpoints are measured as operations, not unbounded SQL history.
    END IF;
    event_name_value := CASE
      WHEN NEW.cancel_requested_at IS DISTINCT FROM OLD.cancel_requested_at THEN 'cancel_requested'
      WHEN NEW.status = 'running' AND OLD.status = 'queued' THEN 'lease_acquired'
      WHEN OLD.status = 'running' AND NEW.status = 'queued' AND NEW.last_error_code = 'lease_expired_retry' THEN 'lease_recovered'
      WHEN OLD.status = 'running' AND NEW.status = 'queued' THEN 'retry_scheduled'
      ELSE NEW.status
    END;
    IF NEW.status = 'running' AND OLD.status = 'queued' THEN
      queue_wait := greatest(0, extract(epoch FROM (clock_timestamp() - OLD.available_at)) * 1000);
    END IF;
  END IF;
  INSERT INTO advanced_analysis_job_events(
    tenant_id, job_id, event_name, previous_status, status, attempt_count, worker_id,
    previous_error_code, error_code, available_at, lease_expires_at, queue_wait_milliseconds,
    parent_traceparent, request_id, turn_id, session_id, client_session_id, client_session_id_mismatch)
  VALUES (NEW.tenant_id, NEW.job_id, event_name_value, old_status, NEW.status, NEW.attempt_count,
    COALESCE(NEW.lease_owner, previous_worker), old_error, NEW.last_error_code,
    NEW.available_at, NEW.lease_expires_at, queue_wait, NEW.parent_traceparent,
    NEW.diagnostic_request_id, NEW.diagnostic_turn_id, NEW.session_id::text,
    NEW.diagnostic_session_id,
    NEW.diagnostic_session_id IS NOT NULL AND (
      NEW.diagnostic_session_id !~* '^([0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$'
      OR replace(lower(NEW.diagnostic_session_id), '-', '') <> replace(NEW.session_id::text, '-', '')));
  RETURN NEW;
END;
$$;

CREATE TRIGGER advanced_analysis_job_transition
  AFTER INSERT OR UPDATE ON advanced_analysis_jobs
  FOR EACH ROW EXECUTE FUNCTION record_advanced_analysis_job_transition();

CREATE OR REPLACE FUNCTION reject_advanced_analysis_event_update()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION 'advanced_analysis_job_events is append-only';
END;
$$;
CREATE TRIGGER advanced_analysis_event_immutable
  BEFORE UPDATE ON advanced_analysis_job_events
  FOR EACH ROW EXECUTE FUNCTION reject_advanced_analysis_event_update();
-- Retention removes jobs and cascades their events; operational updates never rewrite history.
