# État d'avancement — SAAIA RAG On-Prem (CDC v2.7)

Version : 2026-02-08 (M1.4 Fix)  
Mise à jour : chaque commit DOIT modifier ce fichier.

---

## M0 — Repo, conventions, docs

### M0.1 — Documentation minimale et conventions
**Statut : FAIT**

**Éléments implémentés :**
- `docs/CDC_v2.7.md` : source de vérité CDC
- `docs/ARCHITECTURE.md` : architecture cible
- `docs/Plan_action_v2.7.md` : jalons et livrables
- `.github/copilot-instructions.md` : directives agent IA
- `.github/instructions/repo_hygiene.instructions.md` : règles d'hygiène repo

---

## M1 — API retrieval + chat-store + contrats

### M1.1 — Serveur "client-only" (NO LLM serveur)
**Statut : FAIT**

**Éléments observés :**
- `/ready` endpoint présent (ReadyEndpoints.cs)
- Aucun service LLM embarqué

### M1.2 — Retrieval : `POST /rag/search`
**Statut : FAIT (v2.7)**

**Éléments implémentés :**
- RagEndpoints.cs : SearchAsync retourne `RagSearchResponseDto` (items[] au lieu de matches[])
- Response inclut `requestId`, `items[]`, `metrics`
- Support des modes : focused / balanced / broad
- Filtrage : minScore, maxPerDoc, maxPerPage (diversité)

**Fichiers créés :**
- `backend/SAAIA.Backend/Models/RagSearchDto.cs` (DTOs v2.7)

**Fichiers modifiés :**
- `backend/SAAIA.Backend/Endpoints/RagEndpoints.cs`

### M1.3 — Chat-store serveur
**Statut : PARTIEL**

**Éléments implémentés :**
- `POST /chat/sessions` : création session avec userId (obligatoire)
- `GET /chat/sessions?userId=...` : listing sessions par user_id
- `GET /chat/sessions/{sessionId}?userId=...` : récupération session
- `POST /chat/sessions/{sessionId}/messages` : ajout message (userId validé)
- `GET /chat/sessions/{sessionId}/messages?userId=...` : listing messages (user scoped)
- migration `006_add_user_id_to_chat_sessions.sql` : ajout colonne user_id

**Éléments observés :**
- endpoints existants: `/chat/sessions/{sessionId}/messages` (ChatStoreEndpoints.cs)
- userId absent de la table `chat_sessions` (CDC/Plan l'exigent) [PARTIELLEMENT FIXÉ par migration 006]

**À corriger — ÉTAPE 2 (M1.4 Fix) :**
- PATCH /chat/sessions/{sessionId} : **MANQUE userId dans le WHERE**
- DELETE /chat/sessions/{sessionId} : **MANQUE userId dans le WHERE**
- AddMessageAsync : UPDATE chat_sessions peut inclure user_id dans WHERE (optionnel mais propre)

### M1.4 — Contrats (DTO) + compat
**Statut : PARTIEL (à compléter)**

**Éléments implémentés :**
- DTOs explicites pour /rag/search : RagSearchRequestDto, RagSearchResponseDto, RagItemDto, RagMetricsDto
- DTOs explicites pour /chat/* : ChatSessionCreateRequestDto, ChatSessionDto, ChatMessageCreateRequestDto, ChatMessageDto
- Contrats API documentés : docs/04_CONTRACTS/API_RAG_SEARCH.md, docs/04_CONTRACTS/API_CHAT_STORE.md

**À faire — ÉTAPE 3 (M1.4 Fix) :**
- Ajouter support champ `diversity{}` dans RagSearchRequestDto (compat CDC v2.7)
  - RagDiversityDto(maxChunksPerDoc?, preferDistinctPages?)
  - Intégration SearchCoreAsync pour override maxPerDoc/maxPerPage si diversity != null

**À corriger — ÉTAPE 4 (M1.4 Fix) :**
- API_CHAT_STORE.md : caractère UTF-8 corrompu dans le titre (`\x97` → `—`)
- API_CHAT_STORE.md : ajouter exemples curl PATCH/DELETE /chat/sessions/{sessionId}?userId=...
- API_RAG_SEARCH.md : documenter le champ diversity (compat)

---

## M2 — Observabilité + readiness

### M2.1 — RequestId middleware + log scopes
**Statut : FAIT**

**Éléments implémentés :**
- RequestIdMiddleware : génère/récupère `X-Request-Id` (header client ou nouveau UUID)
- ErrorHandlingMiddleware : capture exceptions et les formate en `{ error, requestId }`
- Log scopes : `request_id`, `path`, `method`, `tenant_id` (après auth)
- Tous les endpoints retournent `X-Request-Id` (même erreurs)
- ReadyEndpoints et RagEndpoints alignés avec le contrat

**Fichiers créés :**
- `backend/SAAIA.Backend/Middleware/RequestIdMiddleware.cs`
- `backend/SAAIA.Backend/Middleware/ErrorHandlingMiddleware.cs`
- `backend/SAAIA.Backend/Models/ErrorResponse.cs`

**Fichiers modifiés :**
- `backend/SAAIA.Backend/Extensions/WebApplicationExtensions.cs`
- `backend/SAAIA.Backend/Auth/ApiKeyAuthMiddleware.cs`
- `backend/SAAIA.Backend/Endpoints/ReadyEndpoints.cs`
- `backend/SAAIA.Backend/Endpoints/RagEndpoints.cs`
- `backend/SAAIA.Backend/Endpoints/ChatStoreEndpoints.cs`

### M2.2 — OpenTelemetry metrics+traces (toggle)
**Statut : NON FAIT**

**Éléments observés :**
- aucune occurrence `OpenTelemetry`

### M2.3 — /ready checks DB+Qdrant+TEI+signature
**Statut : PARTIEL**

**Éléments observés :**
- `/ready` check DB/Qdrant/TEI présent (ReadyEndpoints.cs)
- signature config non vérifiée explicitement dans /ready

---

## M3 — Ingestion robuste

### M3.1 — infra: secure qdrant with api-key (prod)
**Statut : NON FAIT (prod)**

**Éléments observés :**
- pas de docker-compose.prod
- pas de header `api-key` Qdrant configuré

### M3.2 — server: rate limit rag+chat-store per api key
**Statut : PARTIEL/À VÉRIFIER**

**Éléments observés :**
- RateLimiter configuré (ServiceCollectionExtensions.cs)
- à confirmer: partitionnement par api key + Retry-After

### M3.3 — server: audit events for admin + chat
**Statut : PARTIEL**

**Éléments observés :**
- migration `004_phase1_security.sql` crée `audit_events`
- AuditWriter existe et est utilisé dans `AdminKeysEndpoints`
- pas d'audit généralisé pour chat/ingestion (à compléter)

---

## M4 — Infra prod + runbook

### M4.1 — infra: add docker-compose.prod
**Statut : NON FAIT**

**Éléments observés :**
- docker-compose.prod absent

### M4.2 — docs: runbook + scripts
**Statut : NON FAIT**

**Éléments observés :**
- scripts install/update/backup/restore/diag absents

---

## M5 — Client WinUI (MVP)

### M5.1 — MVVM + pages
**Statut : PARTIEL (squelette présent, MVVM/pages à faire)**

**Éléments observés :**
- projet WinUI présent
- UI/Navigation MVVM pages Chat/Sessions/Settings non présentes

### M5.2 — Settings + user_id + DPAPI
**Statut : NON FAIT**

**Éléments observés :**
- pas de persistance userId/DPAPI détectée

---

## M6 — LLM local intégré (OpenAI-compatible)
**Statut : NON FAIT**

---

## M7 — Agent "ChatGPT-like" (plan → multi-search → synthèse)
**Statut : NON FAIT**

---

## M8 — Packaging (zéro config)
**Statut : NON FAIT**

---

## Dettes techniques — M1.4 Fix

**Priority 1 (CETTE SÉANCE) :**
1. ✅ Restaurer docs/Plan_action_v2.7_status.md
2. 🔧 PATCH /chat/sessions/{sessionId} : ajouter userId dans WHERE
3. 🔧 DELETE /chat/sessions/{sessionId} : ajouter userId dans WHERE
4. ✨ POST /rag/search : support diversity{} (compat CDC v2.7)
5. 📝 API_CHAT_STORE.md : fix UTF-8, ajouter exemples PATCH/DELETE
6. 📝 API_RAG_SEARCH.md : documenter diversity
7. 📝 CHANGELOG : noter restauration + fixes

**Priority 2 (future) :**
- OpenTelemetry toggle (M2.2)
- /ready signature checks (M2.3)
- Docker-compose prod (M4.1)
- Runbooks (M4.2)
- WinUI MVVM pages (M5.1)
- Settings/DPAPI (M5.2)

---

## Commandes de test — M1.4 Fix

```powershell
# Build backend
dotnet build

# Test RAG categories
curl.exe -X GET "http://localhost:5122/rag/categories" `
  -H "X-Api-Key: saaia_dev_bootstrap_2026_CHANGE_ME"

# Test POST /rag/search avec diversity
@'
{
  "query": "configuration",
  "topK": 5,
  "minScore": 0.3,
  "diversity": {
    "maxChunksPerDoc": 2,
    "preferDistinctPages": true
  }
}
'@ | curl.exe -X POST "http://localhost:5122/rag/search" `
  -H "Content-Type: application/json" `
  -H "X-Api-Key: saaia_dev_bootstrap_2026_CHANGE_ME" `
  -d @-

# Test chat sessions
# Create
$userId = "550e8400-e29b-41d4-a716-446655440000"
@'
{"userId":"550e8400-e29b-41d4-a716-446655440000","title":"Test Session"}
'@ | curl.exe -X POST "http://localhost:5122/chat/sessions" `
  -H "Content-Type: application/json" `
  -H "X-Api-Key: saaia_dev_bootstrap_2026_CHANGE_ME"

# List sessions
curl.exe -X GET "http://localhost:5122/chat/sessions?userId=$userId" `
  -H "X-Api-Key: saaia_dev_bootstrap_2026_CHANGE_ME"

# PATCH session (avec userId obligatoire)
curl.exe -X PATCH "http://localhost:5122/chat/sessions/{sessionId}?userId=$userId" `
  -H "Content-Type: application/json" `
  -H "X-Api-Key: saaia_dev_bootstrap_2026_CHANGE_ME" `
  -d '{"title":"Updated Title"}'

# DELETE session (avec userId obligatoire)
curl.exe -X DELETE "http://localhost:5122/chat/sessions/{sessionId}?userId=$userId" `
  -H "X-Api-Key: saaia_dev_bootstrap_2026_CHANGE_ME"
```

---

**Prochain ordre recommandé :**
1. ✅ Restaurer Plan_action_v2.7_status.md (THIS COMMIT)
2. 🔧 PATCH/DELETE userId scoping (THIS COMMIT)
3. ✨ diversity{} support (THIS COMMIT)
4. 📝 Contrats API + CHANGELOG (THIS COMMIT)
