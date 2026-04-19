CREATE TABLE IF NOT EXISTS runtime_capability_events (
  event_id uuid PRIMARY KEY,
  capability_key text NOT NULL,
  profile_key text NULL,
  event_type text NOT NULL,
  actor text NOT NULL,
  reason text NULL,
  details jsonb NOT NULL DEFAULT '{}'::jsonb,
  occurred_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_runtime_capability_events_capability_occurred
  ON runtime_capability_events(capability_key, occurred_at DESC);

CREATE INDEX IF NOT EXISTS idx_runtime_capability_events_occurred
  ON runtime_capability_events(occurred_at DESC);
