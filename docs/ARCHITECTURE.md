# Architecture — SAAIA RAG On-Prem (CDC v2.7)

## Principe v2.7 (non négociable)
- **Serveur = NO LLM** : aucune génération de réponse finale côté serveur.
- **Client = LLM local** : génération, planification, streaming, clarification.

---

## Composants
### Côté Serveur
- **API .NET** : retrieval (`/rag/search`), chat-store, ingestion/admin, health/ready, observabilité.
- **PostgreSQL** :
  - tenants, api_keys, audit_events
  - documents, ingestion_jobs, ingestion_events
  - chat_sessions, chat_messages (**scoppés par tenant_id + user_id**)
- **Qdrant** : points = chunks, payload = tenant/doc/page/text.
- **TEI** : embeddings ingestion + requêtes.
- **Watcher/Scanner/Worker** : pipeline ingestion (fichier → texte → chunks → embeddings → Qdrant).

### Côté Client (WinUI)
- **UI** : chat + sources + sessions + settings
- **Orchestrateur** :
  - plan → 1–3 recherches RAG → fusion/dédup → synthèse
  - si insuffisant : question de clarification
- **LLM local** : OpenAI-compatible (ex: llama.cpp server), streaming SSE.

---

## Flux “ChatGPT-like” (sans internet)
1) L’utilisateur pose une question dans le client.
2) Le client exécute un **plan** (local LLM) :
   - reformule / décompose
   - propose 1–3 requêtes RAG
   - peut demander une clarification si ambigu
3) Le client appelle `POST /rag/search` 1–3 fois.
4) Le client compose une synthèse (local LLM) **avec citations**.
5) Le client stocke les messages (option v2.7) sur le serveur (chat-store).

---

## Contrats à respecter
- `X-Api-Key` : obligatoire sur endpoints protégés.
- `X-Request-Id` : toujours présent, et retourné dans les erreurs.
- `user_id` : GUID stable client, obligatoire pour chat-store (séparation multi-user).

Voir :
- `docs/04_CONTRACTS/API_RAG_SEARCH.md`
- `docs/04_CONTRACTS/API_CHAT_STORE.md`
- `docs/05_DATA/DB_SCHEMA.md`
