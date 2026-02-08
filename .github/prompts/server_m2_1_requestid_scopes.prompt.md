# Prompt — server: M2.1 RequestId + log scopes

Implémente M2.1 selon `docs/Plan_action_v2.7.md` et `docs/04_CONTRACTS/ERROR_MODEL.md`.
- `X-Request-Id` sur toute réponse
- erreurs JSON `{ error, requestId }`
- scopes logs request_id/tenant_id/user_id/path/method
- scopes ingestion job_id/doc_id/doc_path

Termine par tests PowerShell et mise à jour docs status + changelog.
