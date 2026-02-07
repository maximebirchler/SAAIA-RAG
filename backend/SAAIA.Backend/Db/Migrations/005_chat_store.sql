-- 005_chat_store.sql
-- Chat store server-side (sessions + messages)

CREATE TABLE IF NOT EXISTS chat_sessions (
  tenant_id UUID NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
  session_id UUID NOT NULL,
  title TEXT NULL,
  client_user TEXT NULL, -- identifiant libre (ex: "Maxime", ou Windows user) - optionnel
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  last_message_at TIMESTAMPTZ NULL,
  PRIMARY KEY (tenant_id, session_id)
);

CREATE INDEX IF NOT EXISTS ix_chat_sessions_tenant_updated
  ON chat_sessions (tenant_id, updated_at DESC);

CREATE TABLE IF NOT EXISTS chat_messages (
  tenant_id UUID NOT NULL,
  session_id UUID NOT NULL,
  message_id UUID NOT NULL,
  role TEXT NOT NULL CHECK (role IN ('system','user','assistant','tool')),
  content TEXT NOT NULL,
  sources_json JSONB NULL, -- citations/sources (ex: matches rag)
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, message_id),
  FOREIGN KEY (tenant_id, session_id)
    REFERENCES chat_sessions(tenant_id, session_id)
    ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_chat_messages_session_created
  ON chat_messages (tenant_id, session_id, created_at ASC);
