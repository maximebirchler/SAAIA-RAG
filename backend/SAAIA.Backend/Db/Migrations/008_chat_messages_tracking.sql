-- 008_chat_messages_tracking.sql
-- Persist chat message tracking metadata and progress for durable UI job tracking.

ALTER TABLE chat_messages
ADD COLUMN IF NOT EXISTS progress_text TEXT NULL;

ALTER TABLE chat_messages
ADD COLUMN IF NOT EXISTS tracking_meta_json JSONB NULL;

COMMENT ON COLUMN chat_messages.progress_text IS 'Optional client-facing progress line for long running operations.';
COMMENT ON COLUMN chat_messages.tracking_meta_json IS 'Optional tracking metadata json payload (jobId, jobType, displayLabel, terminal state...).';
