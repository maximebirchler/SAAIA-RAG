# Prompt — server: M1.4 Contrats API alignés CDC v2.7 — STRICT

## Objectif
Aligner API + DTO + docs **sans casser** l’existant.
⚠️ Interdit de supprimer/renommer des docs existantes (CHANGELOG, contrats…).

## DoD (Definition of Done)
1) `POST /rag/search`
   - respecte le contrat doc (request/response)
   - **compatibilité** : accepte l’ancien format si déjà utilisé (mapping interne)
   - réponse stable : `requestId`, `items[]`, `metrics{tookMs, returned}`

2) Chat-store
   - `userId` obligatoire
   - **toutes** les opérations sont scoppées `tenant_id + user_id`
   - PATCH/DELETE session doivent aussi vérifier `userId` (pas seulement sessionId)

3) DB
   - si `user_id` est requis dans le code => migration SQL obligatoire
   - index tenant_id + user_id + updated_at recommandé

4) Docs
   - `docs/04_CONTRACTS/API_RAG_SEARCH.md` doit exister (ne pas supprimer)
   - `docs/04_CONTRACTS/API_CHAT_STORE*.md` doit exister
   - `docs/04_CONTRACTS/README.md` cohérent (aucune référence vers fichier supprimé)
   - `docs/CHANGELOG.md` doit exister et être mis à jour
   - `docs/Plan_action_v2.7_status.md` mis à jour

5) Tests
   - Fournir commandes curl PowerShell pour :
     - /rag/categories (avec key)
     - /rag/search (OK + query vide => 400 validation)
     - endpoints chat (create session, list sessions, add message, list messages)

## Contrôle anti-erreur (OBLIGATOIRE en fin)
- Confirmer : “aucun fichier doc supprimé/renommé”
- Confirmer : “migration DB ajoutée si schéma impacté”
- Confirmer : “README contrats ne référence pas de fichier manquant”

## Notes d’implémentation (recommandées)
- DTOs dans `backend/SAAIA.Backend/Models/*Dto.cs`
- Garder compat request /rag/search :
  - nouveau format CDC : `diversity.maxChunksPerDoc`, `diversity.preferDistinctPages`
  - ancien format : `maxPerDoc`, `maxPerPage`, `candidates`
  - mapper vers une structure interne unique

Terminer par : tests + update status + update changelog + message de commit `server: M1.4 contracts (rag+chat)`.
