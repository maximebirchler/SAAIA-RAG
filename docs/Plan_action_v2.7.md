# Plan d’action — SAAIA RAG On-Prem (CDC v2.7)

Ce document est la **référence exécutable** : il décrit les jalons (M0 → M8), les livrables et les DoD.
Il est volontairement **structuré** pour être utilisé “commit par commit” par un agent IA.

> Important : le jalon “M2.x” (observabilité/readiness) est côté **serveur**.  
> Le jalon “M5.x” (WinUI) est côté **client**.

---

## M0 — Repo, conventions, docs
### M0.1 — Documentation minimale et conventions
**But :** rendre le repo compréhensible (humain + agent IA).  
**Livrables :**
- `docs/CDC_v2.7.md` (source de vérité)
- `docs/ARCHITECTURE.md`
- `docs/ROADMAP.md` (liste jalons + préfixes commits)
- `.github/` (instructions + prompts Copilot)

**DoD :**
- Un nouveau dev peut lancer le projet dev en < 30 min.
- Copilot trouve et cite les docs (références explicites dans `.github/copilot-instructions.md`).

---

## M1 — API retrieval + chat-store + contrats
### M1.1 — Serveur “client-only” (NO LLM serveur)
**But :** valider que le serveur ne fait que retrieval/stockage.  
**DoD :** `/ready` indique `llm:"client-only"`.

### M1.2 — Retrieval : `POST /rag/search`
**But :** endpoint stable qui renvoie des items citables (doc/pages/extrait).  
**DoD :**
- input validé (400)
- réponse contient `requestId`, `items[]`, `metrics`

### M1.3 — Chat-store serveur
**But :** persister sessions/messages (v2.7) avec séparation par `user_id`.  
**DoD :**
- `POST /chat/sessions`, `GET /chat/sessions?userId=...`
- `POST /chat/messages`, `GET /chat/messages?sessionId=...`
- scoping par tenant_id + user_id

### M1.4 — Contrats (DTO) + compat
**But :** figer les shapes request/response (éviter drift).  
**DoD :**
- DTOs explicites (ex: `RagSearchRequest`, `RagSearchResponse`)
- compat si ancien contrat utilisé (alias/versioning)

---

## M2 — Observabilité + readiness
### M2.1 — RequestId middleware + log scopes
**DoD :**
- `X-Request-Id` sur toute réponse (y compris erreurs)
- erreurs JSON `{ error, requestId }`
- scopes logs : request_id, tenant_id, user_id, path, method
- ingestion scopes : job_id, doc_id, doc_path

### M2.2 — OpenTelemetry (toggle)
**DoD :**
- OTel activable/désactivable sans casser le runtime
- instrumentation ASP.NET + HttpClient (et Npgsql si possible)
- doc d’activation + variables d’environnement

### M2.3 — `/ready` complet (DB/Qdrant/TEI/config)
**DoD :**
- `/ready` vérifie DB + Qdrant + TEI + config signée valide (si mécanisme activé)
- retourne timings + requestId

---

## M3 — Ingestion robuste
**But :** watcher/scanner/worker stables, anti-wipe, reprocess, etc.  
**DoD :**
- reprise après crash
- idempotence ingestion
- suppression doc → suppression Qdrant + DB

---

## M4 — Infra prod + runbook
**DoD :**
- `docker-compose.prod.yml` inclut backend + dépendances
- scripts : install/update/backup/restore/diag
- runbooks dans `docs/06_RUNBOOKS/`

---

## M5 — Client WinUI (MVP)
### M5.1 — MVVM + pages
**DoD :**
- pages Chat/Sessions/Settings
- mode UI-only

### M5.2 — Settings + user_id + DPAPI
**DoD :**
- serverUrl/apiKey/userId persistés
- apiKey protégée (DPAPI)
- test connexion (`GET /ready`)

---

## M6 — LLM local intégré (OpenAI-compatible)
**DoD :**
- wrapper streaming OpenAI-compatible
- éventuellement process manager (llama.cpp server) côté client

---

## M7 — Agent “ChatGPT-like” (plan → multi-search → synthèse)
**DoD :**
- plan (1–3 requêtes) + diversification
- citations obligatoires
- question de clarification si sources insuffisantes

---

## M8 — Packaging (zéro config)
**DoD :**
- install client
- install serveur
- runbook + checklists
