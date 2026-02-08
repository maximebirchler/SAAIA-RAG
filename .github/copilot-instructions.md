# GitHub Copilot — Instructions repo (SAAIA RAG On-Prem) — CDC v2.7

## Point d’entrée (OBLIGATOIRE)
Avant toute modification de code, lis :
1) `docs/00_README.md`
2) `docs/CDC_v2.7.md`
3) `docs/Plan_action_v2.7.md`
4) `docs/Plan_action_v2.7_status.md`
5) `docs/ARCHITECTURE.md`
6) `docs/04_CONTRACTS/`

## Non négociables (v2.7)
- **Serveur = NO LLM** : aucune génération de réponse côté serveur.
- Le client WinUI gère : planification, synthèse, streaming, clarification.
- RAG = réponses sourcées (doc/page/extrait).
- Chat-store : isolation `tenant_id` + `user_id`.

## Règles “commit par commit”
- Un commit = un jalon (ou sous-jalon explicite).
- Toujours livrer : fichiers modifiés, commandes de test, DoD.
- Mettre à jour :
  - `docs/Plan_action_v2.7_status.md`
  - `docs/CHANGELOG.md`

## Qualité backend
- `CancellationToken` partout.
- HttpClient : disposer `HttpResponseMessage`/`HttpContent`.
- Erreurs JSON `{ error, requestId }` + header `X-Request-Id`.
- Logs scopes : request_id/tenant_id/user_id/path/method.

## Qualité client
- Le bouton Send ne doit jamais “ne rien faire” : feedback visible.
- Modes : UI-only / RAG-only / LLM.
- `user_id` : GUID stable (LocalSettings), apiKey protégée (DPAPI).

