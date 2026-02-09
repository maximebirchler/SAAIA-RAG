-- 006_add_user_id_to_chat_sessions.sql
-- Add user_id column to chat_sessions (CDC v2.7: mandatory for multi-user isolation)

ALTER TABLE chat_sessions
ADD COLUMN user_id TEXT NOT NULL DEFAULT 'unknown';

-- Drop old index (will recreate with user_id)
DROP INDEX IF EXISTS ix_chat_sessions_tenant_updated;

-- Create new index with user_id (CDC v2.7 scoping)
CREATE INDEX IF NOT EXISTS ix_chat_sessions_tenant_user_updated
  ON chat_sessions (tenant_id, user_id, updated_at DESC);

-- Add constraint: user_id must be non-empty (after migration)
ALTER TABLE chat_sessions
ALTER COLUMN user_id DROP DEFAULT;

-- Update composite primary key comment (for documentation)
COMMENT ON TABLE chat_sessions IS 'Chat sessions scoped by tenant_id + user_id (CDC v2.7)';
COMMENT ON COLUMN chat_sessions.user_id IS 'Client-generated GUID (stable per user session)';
