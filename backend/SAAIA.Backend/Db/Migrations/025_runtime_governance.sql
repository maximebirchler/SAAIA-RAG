CREATE TABLE IF NOT EXISTS runtime_capability_state (
    capability_key      text PRIMARY KEY,
    capability_family   text NOT NULL,
    display_name        text NOT NULL,
    runtime_key         text NOT NULL,
    profile_key         text NOT NULL,
    implemented         boolean NOT NULL DEFAULT FALSE,
    desired_enabled     boolean NOT NULL DEFAULT FALSE,
    installed           boolean NOT NULL DEFAULT FALSE,
    configured          boolean NOT NULL DEFAULT FALSE,
    healthy             boolean NOT NULL DEFAULT FALSE,
    qualified           boolean NOT NULL DEFAULT FALSE,
    authorized          boolean NOT NULL DEFAULT FALSE,
    selected            boolean NOT NULL DEFAULT FALSE,
    pass_count          integer NOT NULL DEFAULT 0,
    last_checked_at     timestamptz NULL,
    last_qualified_at   timestamptz NULL,
    last_error          text NULL,
    details             jsonb NOT NULL DEFAULT '{}'::jsonb,
    updated_at          timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS runtime_warmup_results (
    warmup_result_id    uuid PRIMARY KEY,
    capability_key      text NOT NULL REFERENCES runtime_capability_state(capability_key) ON DELETE CASCADE,
    profile_key         text NOT NULL,
    pass_count          integer NOT NULL,
    passed              boolean NOT NULL,
    measured_at         timestamptz NOT NULL DEFAULT now(),
    details             jsonb NOT NULL DEFAULT '{}'::jsonb
);

CREATE INDEX IF NOT EXISTS ix_runtime_warmup_results_capability_measured
    ON runtime_warmup_results(capability_key, measured_at DESC);
