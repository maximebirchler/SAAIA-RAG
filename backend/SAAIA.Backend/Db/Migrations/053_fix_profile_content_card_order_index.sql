DROP INDEX IF EXISTS ix_document_profile_content_cards_profile_order_cover;

CREATE INDEX IF NOT EXISTS ix_document_profile_content_cards_profile_order
  ON document_profile_content_cards (
    tenant_id,
    document_profile_id,
    card_index
  );
