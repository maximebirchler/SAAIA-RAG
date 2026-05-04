WITH target_profiles AS (
  SELECT target.*
  FROM document_profiles target
  WHERE target.profile_version <> 'deterministic_v1'
    AND NOT EXISTS (
      SELECT 1
      FROM document_profile_content_cards existing
      WHERE existing.document_profile_id = target.document_profile_id
    )
),
source_cards AS (
  SELECT
    target.document_profile_id AS target_profile_id,
    target.tenant_id,
    target.revision_id,
    target.doc_id,
    target.profile_version,
    source.card_index,
    source.title,
    source.normalized_title,
    source.page_start,
    source.page_end,
    source.kind,
    source.signals,
    source.search_text,
    source.token_count,
    source.checksum,
    md5(target.document_profile_id::text || '|content-card|' || source.card_index::text) AS stable_hash
  FROM target_profiles target
  JOIN document_profiles deterministic
    ON deterministic.tenant_id = target.tenant_id
   AND deterministic.revision_id = target.revision_id
   AND deterministic.profile_version = 'deterministic_v1'
  JOIN document_profile_content_cards source
    ON source.tenant_id = deterministic.tenant_id
   AND source.document_profile_id = deterministic.document_profile_id
)
INSERT INTO document_profile_content_cards(
  content_card_id,
  tenant_id,
  document_profile_id,
  revision_id,
  doc_id,
  profile_version,
  card_index,
  title,
  normalized_title,
  page_start,
  page_end,
  kind,
  signals,
  search_text,
  token_count,
  checksum,
  metadata)
SELECT
  (
    SUBSTRING(stable_hash, 1, 8) || '-' ||
    SUBSTRING(stable_hash, 9, 4) || '-' ||
    SUBSTRING(stable_hash, 13, 4) || '-' ||
    SUBSTRING(stable_hash, 17, 4) || '-' ||
    SUBSTRING(stable_hash, 21, 12)
  )::uuid,
  tenant_id,
  target_profile_id,
  revision_id,
  doc_id,
  profile_version,
  card_index,
  title,
  normalized_title,
  page_start,
  page_end,
  kind,
  signals,
  search_text,
  token_count,
  checksum,
  jsonb_build_object('backfilledFrom', 'deterministic_v1')
FROM source_cards
ON CONFLICT DO NOTHING;
