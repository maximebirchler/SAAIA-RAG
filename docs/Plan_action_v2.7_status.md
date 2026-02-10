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
**Statut : FAIT**

**Éléments implémentés :**
- `POST /chat/sessions` : création session avec userId (obligatoire)
- `GET /chat/sessions?userId=...` : listing sessions par user_id
- `GET /chat/sessions/{sessionId}?userId=...` : récupération session
- `PATCH /chat/sessions/{sessionId}?userId=...` : modification session (userId scoped)
- `DELETE /chat/sessions/{sessionId}?userId=...` : suppression session (userId scoped)
- `POST /chat/sessions/{sessionId}/messages` : ajout message (userId validé)
- `GET /chat/sessions/{sessionId}/messages?userId=...` : listing messages (user scoped)
- migration `006_add_user_id_to_chat_sessions.sql` : ajout colonne user_id + scoping

**Fichiers modifiés :**
- `backend/SAAIA.Backend/Endpoints/ChatStoreEndpoints.cs` (PATCH/DELETE avec userId obligatoire)

### M1.4 — Contrats (DTO) + compat
**Statut : FAIT**

**Éléments implémentés :**
- DTOs explicites pour /rag/search : RagSearchRequestDto, RagSearchResponseDto, RagItemDto, RagMetricsDto
- DTOs pour diversity : RagDiversityDto(maxChunksPerDoc?, preferDistinctPages?)
- DTOs explicites pour /chat/* : ChatSessionCreateRequestDto, ChatSessionDto, ChatMessageCreateRequestDto, ChatMessageDto
- Contrats API documentés avec exemples curl complets
- Support diversity{} dans RagSearchRequestDto + SearchCoreAsync override
- PATCH/DELETE /chat/sessions/{sessionId}?userId=... avec exemples

**Fichiers créés :**
- `backend/SAAIA.Backend/Models/RagSearchDto.cs` (RagDiversityDto added)

**Fichiers modifiés :**
- `backend/SAAIA.Backend/Endpoints/RagEndpoints.cs` (diversity override in SearchCoreAsync)
- `backend/SAAIA.Backend/Endpoints/ChatStoreEndpoints.cs` (PATCH/DELETE userId scoping)
- `docs/04_CONTRACTS/API_CHAT_STORE.md` (UTF-8 + GET/PATCH/DELETE + curl)
- `docs/04_CONTRACTS/API_RAG_SEARCH.md` (diversity doc + curl examples)

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

## Dettes techniques (post-M1.4)

**M1.4 COMPLÈTEMENT TERMINÉ** ✅ — Aucune dette restante de M1.4.

**Priority (M2 et après) :**
- M2.1 : Vérifier RequestId/log scopes en prod (optionnel)
- M2.2 : OpenTelemetry toggle
- M2.3 : /ready signature checks
- M3.1 : Docker-compose prod + Qdrant API-key
- M4.1 : Runbooks + scripts install/backup/restore
- M5.1 : WinUI MVVM pages (Chat/Sessions/Settings)
- M5.2 : Settings/userId/DPAPI côté client

---

## Commandes de test — M1.4 (POST-FIX)

```powershell
# 1. GET /rag/categories
curl.exe -i -X GET "http://localhost:5122/rag/categories" `
  -H "X-Api-Key: saaia_dev_bootstrap_2026_CHANGE_ME"

# 2. POST /rag/search (basic)
@'
{"query":"test","topK":5,"minScore":0.3}
'@ | curl.exe -i -X POST "http://localhost:5122/rag/search" `
  -H "Content-Type: application/json" `
  -H "X-Api-Key: saaia_dev_bootstrap_2026_CHANGE_ME" `
  --data-binary "@-"

# 3. POST /rag/search avec diversity (CDC v2.7)
@'
{
  "query":"configuration",
  "topK":5,
  "minScore":0.3,
  "diversity":{
    "maxChunksPerDoc":2,
    "preferDistinctPages":true
  }
}
'@ | curl.exe -i -X POST "http://localhost:5122/rag/search" `
  -H "Content-Type: application/json" `
  -H "X-Api-Key: saaia_dev_bootstrap_2026_CHANGE_ME" `
  --data-binary "@-"

# 4. POST /chat/sessions (userId obligatoire)
@'
{"userId":"550e8400-e29b-41d4-a716-446655440000","title":"Test Session","clientUser":"john.doe"}
'@ | curl.exe -i -X POST "http://localhost:5122/chat/sessions" `
  -H "Content-Type: application/json" `
  -H "X-Api-Key: saaia_dev_bootstrap_2026_CHANGE_ME" `
  --data-binary "@-"

# 5. GET /chat/sessions?userId=... (userId obligatoire)
curl.exe -i -X GET "http://localhost:5122/chat/sessions?userId=550e8400-e29b-41d4-a716-446655440000&limit=10" `
  -H "X-Api-Key: saaia_dev_bootstrap_2026_CHANGE_ME"

# 6. PATCH /chat/sessions/{sessionId}?userId=... (userId obligatoire)
@'
{"title":"Updated Session Title"}
'@ | curl.exe -i -X PATCH "http://localhost:5122/chat/sessions/SESSION_ID?userId=550e8400-e29b-41d4-a716-446655440000" `
  -H "Content-Type: application/json" `
  -H "X-Api-Key: saaia_dev_bootstrap_2026_CHANGE_ME" `
  --data-binary "@-"

# 7. DELETE /chat/sessions/{sessionId}?userId=... (userId obligatoire)
curl.exe -i -X DELETE "http://localhost:5122/chat/sessions/SESSION_ID?userId=550e8400-e29b-41d4-a716-446655440000" `
  -H "X-Api-Key: saaia_dev_bootstrap_2026_CHANGE_ME"
```

**Notes :**
- Remplacer `SESSION_ID` par le sessionId reçu du POST /chat/sessions
- userId query param est **OBLIGATOIRE** (400 BadRequest sinon) — c'est une validation CDC v2.7
- Toutes les requêtes retournent `X-Request-Id` (M2.1)
