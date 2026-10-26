-- Run only against the explicitly selected deployment/qualification database.
-- No URL, secret, source text, vector or document path is returned.
BEGIN READ ONLY;
SET LOCAL statement_timeout='5s';
SELECT
 (SELECT count(*) FROM ingestion_vector_plans) AS registered_plans,
 (SELECT count(*) FROM ingestion_vector_plans WHERE retired_at IS NOT NULL) AS retired_plans,
 (SELECT count(*) FROM ingestion_vector_plans p JOIN documents d ON d.tenant_id=p.tenant_id AND d.doc_id=p.doc_id
  WHERE p.generation>=d.ingestion_version OR (p.generation=d.indexed_version AND
   (d.ingestion_vector_plan_fingerprint IS NULL OR d.ingestion_vector_plan_fingerprint=p.plan_fingerprint))) AS current_or_active_protected_plans,
 (SELECT count(*) FROM ingestion_vector_plans WHERE retired_at IS NOT NULL AND available_at<=now()) AS rechecks_due,
 (SELECT count(*) FROM ingestion_vector_plans WHERE last_gc_code LIKE 'blocked_target_%') AS blocked_targets,
 (SELECT count(*) FROM ingestion_vector_plans WHERE last_gc_code LIKE 'read_http_%' OR last_gc_code LIKE 'delete_http_%'
  OR last_gc_code IN ('operation_failed','operation_timeout')) AS transport_failures,
 (SELECT count(*) FROM ingestion_vector_plans WHERE last_gc_code IN
  ('read_response_invalid','response_json_invalid','response_or_manifest_invalid','delete_not_completed','absence_not_verified','reappearance_not_verified')) AS protocol_failures,
 (SELECT count(*) FROM ingestion_vector_plans WHERE NOT manifest_sealed OR last_gc_code IN
  ('manifest_incomplete','manifest_identity_mismatch','point_identity_mismatch')) AS quarantined_plans,
 (SELECT count(*) FROM documents d WHERE d.indexed_version>0 AND NOT EXISTS (
  SELECT 1 FROM ingestion_vector_plans p WHERE p.tenant_id=d.tenant_id AND p.doc_id=d.doc_id
   AND p.generation=d.indexed_version AND p.plan_fingerprint=d.ingestion_vector_plan_fingerprint)) AS unmanaged_active_publications,
 false AS legacy_vector_inventory_complete;

SELECT plan_id,tenant_id,doc_id,generation,plan_fingerprint,target_key,point_count,manifest_sealed,
 retired_at,retirement_reason,gc_cursor,gc_attempts,last_gc_code,last_batch_absence_at,
 last_sweep_completed_at,available_at,(gc_expires_at>now()) AS collector_lease_current
FROM ingestion_vector_plans
ORDER BY available_at,plan_id
LIMIT 100;
ROLLBACK;
