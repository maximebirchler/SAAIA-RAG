---
applyTo: "backend/**/Chat*;backend/**/Endpoints/Chat*"
---
# Backend Chat-store — règles spécifiques

- Respecter `docs/04_CONTRACTS/API_CHAT_STORE.md`.
- `user_id` obligatoire et utilisé pour scoper.
- Index DB tenant_id+user_id+updated_at.
