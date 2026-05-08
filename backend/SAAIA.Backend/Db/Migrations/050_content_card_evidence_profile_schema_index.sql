CREATE INDEX IF NOT EXISTS ix_document_profiles_content_card_evidence_schema
  ON document_profiles ((
    CASE
      WHEN COALESCE(metadata ->> 'contentCardEvidenceSchemaVersion', '') ~ '^[0-9]+$'
        THEN (metadata ->> 'contentCardEvidenceSchemaVersion')::int
      ELSE 0
    END
  ))
  WHERE profile_version = 'llm_backoffice_v1';
