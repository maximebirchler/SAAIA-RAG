---
applyTo: "backend/**/Rag*;backend/**/Endpoints/Rag*;backend/**/Rag/**"
---
# Backend RAG — règles spécifiques

- Respecter le contrat `docs/04_CONTRACTS/API_RAG_SEARCH.md`.
- Renvoi : `requestId` + `items[]` citables + `metrics`.
- Diversité : limiter par doc/page selon contrat (`diversity`).
- Ne jamais générer la réponse finale côté serveur.
