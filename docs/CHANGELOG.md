# Changelog — SAAIA RAG On-Prem (v2.7)

Format : entree par commit (ou par jalon).
**Objectif :** permettre a une autre instance (humaine ou IA) de comprendre "ce qui a change" en 30 secondes.

## 2026-02-08 — server: M1.4 API contracts + DTOs (rag/search items[], chat user_id MANDATORY)

**But :** Aligner API /rag/search et /chat/* sur contrats CDC v2.7 avec DTOs explicites et user_id obligatoire

**Fichiers crees :**
- `backend/SAAIA.Backend/Models/RagSearchDto.cs` (DTOs: RagSearchResponseDto, RagItemDto, RagMetricsDto)
- `backend/SAAIA.Backend/Models/ChatStoreDto.cs` (DTOs: ChatSessionCreateRequestDto, ChatMessageCreateRequestDto)
- `backend/SAAIA.Backend/Db/Migrations/006_add_user_id_to_chat_sessions.sql` (migration: ajouter user_id)
- `docs/04_CONTRACTS/API_RAG_SEARCH.md` (contrat + curl examples)
- `docs/04_CONTRACTS/API_CHAT_STORE.md` (contrat + curl examples)
- `docs/CHANGELOG.md` (ce fichier)

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
