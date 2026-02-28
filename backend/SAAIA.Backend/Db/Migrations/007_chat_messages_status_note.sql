-- 007_chat_messages_status_note.sql
-- Persist a small status label for chat messages (client UX):
-- e.g. "Génération interrompue." or "Échec".

ALTER TABLE chat_messages
ADD COLUMN IF NOT EXISTS status_note TEXT NULL;

COMMENT ON COLUMN chat_messages.status_note IS 'Optional client-facing status label (e.g. cancelled/failed)';
