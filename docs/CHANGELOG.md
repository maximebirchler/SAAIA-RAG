# Changelog — SAAIA RAG On-Prem (v2.7)

Format : entree par commit (ou par jalon).
**Objectif :** permettre a une autre instance (humaine ou IA) de comprendre "ce qui a change" en 30 secondes.

## 2026-02-08 — server+docs: M1.4 fix (restore status, enforce chat user scope, add rag diversity compat)

**But :** Restaurer docs/Plan_action_v2.7_status.md, enforcer userId scoping dans PATCH/DELETE chat, ajouter support diversity{} dans RAG

**Fichiers crees :**
- `docs/Plan_action_v2.7_status.md` (restored from git f1b7cfe, updated status)

**Fichiers modifies :**
- `backend/SAAIA.Backend/Models/RagSearchDto.cs` (RagDiversityDto added + Diversity field)
- `backend/SAAIA.Backend/Endpoints/RagEndpoints.cs` (diversity override in SearchCoreAsync)
- `backend/SAAIA.Backend/Endpoints/ChatStoreEndpoints.cs` (PATCH/DELETE userId obligatoire + WHERE scoping)
- `docs/04_CONTRACTS/API_CHAT_STORE.md` (UTF-8 cleanup + GET/PATCH/DELETE endpoints + curl)
- `docs/04_CONTRACTS/API_RAG_SEARCH.md` (diversity doc + curl examples)
- `docs/CHANGELOG.md` (cleanup duplicates + M1.4 fix note)

**Changements API :**
- PATCH /chat/sessions/{sessionId}?userId=... (userId query param OBLIGATOIRE)
- DELETE /chat/sessions/{sessionId}?userId=... (userId query param OBLIGATOIRE)
- POST /rag/search : diversity{} optionnel (compat CDC v2.7)
  - diversity.maxChunksPerDoc : override maxPerDoc
  - diversity.preferDistinctPages : force maxPerPage=1

**Impact API :** Compat (non-breaking)
- Champs diversity optionnels
- userId query params OBLIGATOIRES (400 BadRequest si absent)
- Mode old (sans diversity) continue de marcher (backward compat)

**Tests :**
- Build: SUCCESS
- Curl examples: fournis dans API_RAG_SEARCH.md et API_CHAT_STORE.md
- PATCH/DELETE userId validation

**Notes :**
- M1.4 TERMINÉ : M1.3 + M1.4 statut = FAIT dans Plan_action_v2.7_status.md
- UTF-8 : API_CHAT_STORE.md titre nettoyé
- userId obligatoire en query param (CDC v2.7 security)
- Aucune dette restante de M1.4

---

## 2026-02-08 — server: M1.4 API contracts + DTOs (rag/search items[], chat user_id MANDATORY)

**But :** Aligner API /rag/search et /chat/* sur contrats CDC v2.7 avec DTOs explicites et user_id obligatoire

**Fichiers crees :**
- `backend/SAAIA.Backend/Models/RagSearchDto.cs` (DTOs: RagSearchResponseDto, RagItemDto, RagMetricsDto)
- `backend/SAAIA.Backend/Models/ChatStoreDto.cs` (DTOs: ChatSessionCreateRequestDto, ChatMessageCreateRequestDto)
- `backend/SAAIA.Backend/Db/Migrations/006_add_user_id_to_chat_sessions.sql` (migration: ajouter user_id)
- `docs/04_CONTRACTS/API_RAG_SEARCH.md` (contrat + curl examples)
- `docs/04_CONTRACTS/API_CHAT_STORE.md` (contrat + curl examples)

**Fichiers modifies :**
- `backend/SAAIA.Backend/Endpoints/RagEndpoints.cs` (retourner items[] au lieu de matches[])
- `backend/SAAIA.Backend/Endpoints/ChatStoreEndpoints.cs` (userId obligatoire + filtrage + validation)

**Impact API :** Breaking
- `/rag/search` retourne `items[]` (au lieu de `matches[]`)
- `/chat/*` endpoints requierent `userId` (query param ou payload)
- Erreur 400 si userId manquant

**Tests :**
- Build: SUCCESS (0 errors)
- Curl examples: fournis dans API_RAG_SEARCH.md et API_CHAT_STORE.md
- Migration 006: ajoute user_id colonne + index tenant_id+user_id+updated_at

**Notes :**
- DTOs explicites alignes sur CDC v2.7
- user_id obligatoire pour isolation multi-user (securite)
- Migration 006 ajoute colonne user_id (was NULL par defaut, to be cleaned up)
- /rag/query endpoint legacy conserve ancien format (backward compat)


---

## Template (copier-coller)
### YYYY-MM-DD — <prefix>: <sujet du commit>
- **But :**
- **Fichiers touches :**
- **Impact API :** (aucun / compatible / breaking + migration)
- **Tests :**
- **Notes / dette :**
