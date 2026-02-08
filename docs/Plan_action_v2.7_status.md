# Plan d'action v2.7 — état d'avancement (basé sur `Plan d'action - Agent IA RAG.pdf` + analyse du repo)
- Analyse effectuée sur l’archive : `RAG.zip`
- Branche HEAD : `cdc-v2.7-m1.1`
- Commit HEAD : `41a285c`
- Date d’analyse : 2026-02-08 18:34 UTC
## Derniers commits détectés
```text
41a285c (HEAD -> cdc-v2.7-m1.1) M2.1: add WinUI client skeleton (sessions/messages + rag/search placeholder)
4583682 chore: ignore VS/Build artifacts recursively
e1b4189 chore: allow appsettings.Local override after signed config in Development
7852bda chore: ignore local req*.json payload files
6c87857 M1.3: add server chat store (sessions + messages)
decb82b M1.2: add /rag/search (candidates, diversity, timings) + enrich matches
fd9d57f M1.1: serveur sans LLM (client-only) + infra sans llama + UI /ui retrieval-only
3504a49 (main) Le socle P0 est stable
001a497 Le socle P0 est stable
668d3f2 Signature Private/Public key
4166400 Program.cs has been split into 20 files
34b444c Folder cleaned / Qwen2.5 working / Web UI working
9088394 cleanup: config + requests + qdrant/tei fixes
e5957bf Init repo: config + structure
e5e3c05 Init structure projet + config
```
## Synthèse
- Le serveur a déjà une base solide (M1.1) + un `/rag/search` fonctionnel mais non aligné contrat v2.7.
- Le chat-store existe mais **il manque `user_id`** et les endpoints ne matchent pas exactement le plan.
- L’observabilité (M2.x) et l’infra prod (M4.x) ne sont pas encore implémentées.
- Le client WinUI a un squelette mais n’est pas encore en MVVM/pages.
## Détail par jalon
### M0.1 — docs: add CDC v2.7 + architecture
**Statut : NON FAIT**
**Éléments observés :**
- `/docs` absent
- `README.md` présent mais vide

### M1.1 — server: remove server-side LLM generation
**Statut : FAIT (à vérifier en runtime)**
**Éléments observés :**
- commit `fd9d57f` 'serveur sans LLM (client-only)'
- aucune occurrence de `/chat/stream` dans le repo

**À vérifier (tests/validation) :**
- `GET /health`
- `GET /ready` retourne `llm=client-only`

### M1.2 — server: add POST /rag/search (v2.7)
**Statut : PARTIEL (endpoint OK, contrat à réaligner)**
**Éléments observés :**
- commit `decb82b`
- endpoint `POST /rag/search` présent (RagEndpoints.cs)
- réponse actuelle = `matches[]/timings` (diffère de `items[]/metrics` CDC)

**À vérifier (tests/validation) :**
- curl /rag/search sur une base non vide (Qdrant+TEI up)

**À faire ensuite :**
- Réaligner DTO v2.7 en M1.4 (request/response) tout en gardant compat legacy si besoin

### M1.3 — server: add chat store schema + endpoints
**Statut : PARTIEL (schema OK, user_id manquant, routes diffèrent)**
**Éléments observés :**
- migration `005_chat_store.sql` (chat_sessions/chat_messages) OK
- endpoints existants: `/chat/sessions/{sessionId}/messages` (ChatStoreEndpoints.cs)
- `user_id` absent de la table `chat_sessions` (CDC/Plan l'exigent)

**À vérifier (tests/validation) :**
- suite curl create session -> post message -> get messages

**À faire ensuite :**
- Ajouter `user_id` (migration) + filtrage par user + wrappers `POST /chat/messages` & `GET /chat/messages`

### M1.4 — server: tighten contracts + DTOs for client
**Statut : NON FAIT**
**Éléments observés :**
- pas de DTOs v2.7 stabilisés
- erreurs 401/403/429/400 pas encore homogènes avec requestId

### M2.1 — server: RequestId middleware + log scopes
**Statut : NON FAIT**
**Éléments observés :**
- aucune occurrence `X-Request-Id` dans le code
- pas de middleware request-id

### M2.2 — server: OpenTelemetry metrics+traces (toggle)
**Statut : NON FAIT**
**Éléments observés :**
- aucune occurrence `OpenTelemetry`

### M2.3 — server: /ready checks DB+Qdrant+TEI+signature
**Statut : PARTIEL**
**Éléments observés :**
- /ready check DB/Qdrant/TEI présent (ReadyEndpoints.cs)
- signature config non vérifiée explicitement dans /ready

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

### M4.1 — infra: add docker-compose.prod
**Statut : NON FAIT**
**Éléments observés :**
- docker-compose.prod absent

### M4.2 — docs: runbook + scripts
**Statut : NON FAIT**
**Éléments observés :**
- scripts install/update/backup/restore/diag absents

### M5.1 — client: add WinUI3 app skeleton (MVVM)
**Statut : PARTIEL (squelette présent, MVVM/pages à faire)**
**Éléments observés :**
- commit `41a285c` ajoute un projet WinUI (mais message commit label 'M2.1')
- UI/Navigation MVVM pages Chat/Sessions/Settings non présentes dans l'état actuel

**À vérifier (tests/validation) :**
- build WinUI
- navigation entre pages

### M5.2+ — client settings/user_id/DPAPI, etc.
**Statut : NON FAIT**
**Éléments observés :**
- pas de persistance userId/DPAPI détectée (à confirmer selon code client)

## Prochain ordre recommandé (commit par commit)
1. `docs: add CDC_v2.7.md + ARCHITECTURE.md + ROADMAP.md + copilot instructions` (M0.1)
2. `server: M1.4 tighten contracts + DTOs` (réaligner `/rag/search` + chat endpoints + erreurs)
3. `server: M2.1 request-id + log scopes`
4. `server: M2.2 OpenTelemetry toggle`
5. `server: M2.3 /ready signature checks`
6. `client: M5.1 MVVM pages` puis `M5.2 settings/userId/DPAPI`
