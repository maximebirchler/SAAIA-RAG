---
applyTo: "backend/**"
---
# Backend — Règles (CDC v2.7)

## Objectif
Backend = ingestion + retrieval + chat-store + sécurité + observabilité. **NO LLM**.

## DoD communs
- Validation input (400)
- Auth (401) stable
- Erreurs : `X-Request-Id` + JSON `{ error, requestId }`
- Logs : scopes request_id/tenant_id/user_id

## Conventions
- Endpoints : Minimal APIs
- `CancellationToken ct` dans chaque handler
