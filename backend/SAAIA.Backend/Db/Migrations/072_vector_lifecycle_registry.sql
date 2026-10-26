-- These barriers deliberately do not cascade from tenants/documents/jobs.
-- Restoring or resetting this history requires an explicit storage-namespace reset protocol.
CREATE TABLE IF NOT EXISTS ingestion_generation_watermarks (
  tenant_id uuid NOT NULL,
  doc_id uuid NOT NULL,
  highest_generation bigint NOT NULL CHECK (highest_generation >= 0),
  updated_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, doc_id)
);

INSERT INTO ingestion_generation_watermarks(tenant_id, doc_id, highest_generation)
SELECT tenant_id, doc_id, max(generation)
FROM (
  SELECT tenant_id, doc_id, GREATEST(COALESCE(ingestion_version,0),COALESCE(indexed_version,0))::bigint AS generation FROM documents
  UNION ALL SELECT tenant_id, doc_id, GREATEST(ingestion_version,indexed_version)::bigint FROM document_revisions
  UNION ALL SELECT tenant_id, doc_id, GREATEST(ingestion_version,indexed_version_before,indexed_version_after)::bigint FROM document_processing_runs
  UNION ALL
  SELECT tenant_id, (payload->>'docId')::uuid, (payload->>'version')::bigint
  FROM ingestion_jobs
  WHERE payload->>'docId' ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
    AND payload->>'version' ~ '^[0-9]{1,10}$'
) history
GROUP BY tenant_id, doc_id
ON CONFLICT (tenant_id,doc_id) DO UPDATE
SET highest_generation=GREATEST(ingestion_generation_watermarks.highest_generation,EXCLUDED.highest_generation);

CREATE TABLE IF NOT EXISTS ingestion_vector_plans (
  plan_id uuid PRIMARY KEY,
  tenant_id uuid NOT NULL,
  doc_id uuid NOT NULL,
  generation int NOT NULL CHECK (generation > 0),
  plan_fingerprint text NOT NULL CHECK (plan_fingerprint ~ '^[0-9a-f]{64}$'),
  target_key text NOT NULL CHECK (target_key ~ '^[0-9a-f]{64}$'),
  target_endpoint text NOT NULL,
  target_collection text NOT NULL,
  target_revision text NOT NULL,
  point_count int NOT NULL CHECK (point_count >= 0),
  manifest_sha256 text NOT NULL CHECK (manifest_sha256 ~ '^[0-9a-f]{64}$'),
  manifest_sealed boolean NOT NULL DEFAULT false,
  registered_at timestamptz NOT NULL DEFAULT now(),
  retired_at timestamptz,
  retirement_reason text,
  available_at timestamptz NOT NULL DEFAULT now(),
  gc_owner uuid,
  gc_expires_at timestamptz,
  gc_cursor int NOT NULL DEFAULT 0 CHECK (gc_cursor >= 0),
  last_sweep_completed_at timestamptz,
  last_batch_absence_at timestamptz,
  last_gc_code text,
  gc_attempts int NOT NULL DEFAULT 0,
  UNIQUE (tenant_id,doc_id,generation,plan_fingerprint)
);
CREATE INDEX IF NOT EXISTS ix_ingestion_vector_gc_due ON ingestion_vector_plans(available_at,plan_id) WHERE retired_at IS NOT NULL;
CREATE TABLE IF NOT EXISTS ingestion_vector_plan_points (
  plan_id uuid NOT NULL REFERENCES ingestion_vector_plans(plan_id),
  ordinal int NOT NULL CHECK (ordinal >= 0),
  point_id uuid NOT NULL,
  canonical_chunk_id uuid NOT NULL,
  PRIMARY KEY (plan_id,ordinal),
  UNIQUE (plan_id,point_id)
);

CREATE OR REPLACE FUNCTION protect_ingestion_generation_watermark() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP='DELETE' THEN RAISE EXCEPTION 'ingestion_generation_barrier_delete_forbidden'; END IF;
  IF NEW.tenant_id<>OLD.tenant_id OR NEW.doc_id<>OLD.doc_id OR NEW.highest_generation<OLD.highest_generation THEN
    RAISE EXCEPTION 'ingestion_generation_barrier_regression';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER ingestion_generation_watermark_immutable
BEFORE UPDATE OR DELETE ON ingestion_generation_watermarks FOR EACH ROW EXECUTE FUNCTION protect_ingestion_generation_watermark();

CREATE OR REPLACE FUNCTION protect_ingestion_vector_manifest() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP='DELETE' THEN RAISE EXCEPTION 'ingestion_vector_manifest_delete_forbidden'; END IF;
  IF ROW(NEW.plan_id,NEW.tenant_id,NEW.doc_id,NEW.generation,NEW.plan_fingerprint,NEW.target_key,
    NEW.target_endpoint,NEW.target_collection,NEW.target_revision,NEW.point_count,NEW.manifest_sha256,NEW.registered_at)
    IS DISTINCT FROM ROW(OLD.plan_id,OLD.tenant_id,OLD.doc_id,OLD.generation,OLD.plan_fingerprint,OLD.target_key,
    OLD.target_endpoint,OLD.target_collection,OLD.target_revision,OLD.point_count,OLD.manifest_sha256,OLD.registered_at)
    OR (OLD.manifest_sealed AND NOT NEW.manifest_sealed)
    OR (OLD.retired_at IS NOT NULL AND (NEW.retired_at IS DISTINCT FROM OLD.retired_at OR NEW.retirement_reason IS DISTINCT FROM OLD.retirement_reason)) THEN
    RAISE EXCEPTION 'ingestion_vector_manifest_mutation_forbidden';
  END IF;
  IF NEW.manifest_sealed AND NOT OLD.manifest_sealed AND
    (SELECT count(*) FROM ingestion_vector_plan_points WHERE plan_id=NEW.plan_id)<>NEW.point_count THEN
    RAISE EXCEPTION 'ingestion_vector_manifest_incomplete';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER ingestion_vector_manifest_immutable BEFORE UPDATE OR DELETE ON ingestion_vector_plans
FOR EACH ROW EXECUTE FUNCTION protect_ingestion_vector_manifest();

CREATE OR REPLACE FUNCTION protect_ingestion_vector_point() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP='INSERT' THEN
    PERFORM 1 FROM ingestion_vector_plans WHERE plan_id=NEW.plan_id AND NOT manifest_sealed AND NEW.ordinal<point_count FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'ingestion_vector_manifest_sealed_or_ordinal_invalid'; END IF;
    RETURN NEW;
  END IF;
  RAISE EXCEPTION 'ingestion_vector_point_mutation_forbidden';
END $$;
CREATE TRIGGER ingestion_vector_point_immutable BEFORE INSERT OR UPDATE OR DELETE ON ingestion_vector_plan_points
FOR EACH ROW EXECUTE FUNCTION protect_ingestion_vector_point();
