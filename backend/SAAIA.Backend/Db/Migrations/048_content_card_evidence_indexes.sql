CREATE INDEX IF NOT EXISTS ix_document_profile_content_cards_evidence_scale_basis
  ON document_profile_content_cards
  USING GIN ((metadata->'evidence'->'scaleBasis'));

CREATE INDEX IF NOT EXISTS ix_document_profile_content_cards_evidence_quantity_facts
  ON document_profile_content_cards
  USING GIN ((metadata->'evidence'->'quantityFacts'));

CREATE INDEX IF NOT EXISTS ix_document_profile_content_cards_evidence_schema
  ON document_profile_content_cards ((metadata->'evidence'->>'schemaVersion'));

CREATE INDEX IF NOT EXISTS ix_document_profile_content_cards_evidence_language
  ON document_profile_content_cards ((metadata->'evidence'->>'language'));

CREATE INDEX IF NOT EXISTS ix_document_profile_content_cards_evidence_facts
  ON document_profile_content_cards
  USING GIN ((metadata->'evidence'->'facts'));
