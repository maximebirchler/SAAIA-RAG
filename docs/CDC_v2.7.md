# Cahier des Charges (CDC) — SAAIA Agent IA RAG On-Prem

Version : v2.7
Date : 2026-02-06
Auteur : Maxime Birchler + Assistant IA
## 0. Résumé exécutif
SAAIA est un assistant IA RAG 100% on-prem destiné aux environnements industriels/entreprise. Il
ingère automatiquement un dossier de documents (PDF principalement), indexe le contenu et répond
aux questions en citant précisément les sources (document, page, extrait).
Changement majeur v2.7 :

Le serveur n’héberge aucun LLM (ni moteur, ni modèle). Il fournit l’ingestion, le stockage, la
recherche (RAG) et la sécurité/observabilité.

Le LLM est obligatoire côté client : chaque utilisateur exécute un LLM local installé/configuré via
l’installateur (sans LM Studio/Ollama).

Le chat doit se comporter au maximum comme un assistant conversationnel “type ChatGPT”,
sans Internet, en s’appuyant sur le RAG comme base de connaissance.
## 1. Objectifs produit
### 1.1 Objectif principal
Permettre aux utilisateurs de retrouver et exploiter rapidement des informations dans de nombreux
documents techniques, tout en gardant les données confidentielles et locales.
### 1.2 Objectifs secondaires

Réduire le temps de recherche documentaire.

Standardiser les réponses (sourcing, structure).

Améliorer la qualité d’assistance : clarification, plan, propositions d’actions, mémoire
conversationnelle.

Industrialiser : sécurité, observabilité, packaging, runbook.
## 2. Portée et contraintes
### 2.1 Portée (in)

Ingestion automatique de documents (PDF prioritaire).

Indexation vectorielle + métadonnées.

Recherche (retrieval) exposée via API serveur.

Client WinUI : interface chat, sources cliquables, multi-turn, streaming local.

LLM local (obligatoire) piloté par le client.
### 2.2 Hors-périmètre (out) — v2.7

Accès Internet pour enrichissement externe (interdit par design).

Agents “tools” externes (ex : Jira, email, etc.) — possible en v2.8+.

Auth SSO/AD (option future).
### 2.3 Contraintes fortes

On-prem : aucun flux de données vers un service tiers.

Serveur sans LLM : pas de génération serveur, pas de modèle serveur.

LLM client obligatoire : installé/configuré via l’installateur, sans dépendances externes imposées.

Mode dégradé : si LLM local indisponible, l’utilisateur doit au minimum pouvoir :

faire des recherches,

consulter les sources,

exporter des extraits.
## 3. Architecture cible
### 3.1 Vue globale
Server RAG Core (Linux recommandé)

Ingestion : Watcher / Scanner / Worker

DB (Postgres) : tenants, docs, jobs, sessions chat, audit

Vector DB (Qdrant) : chunks + payload

Embeddings (TEI) : embeddings ingestion + embeddings requêtes

API Backend .NET : endpoints ingestion/admin/retrieval/chat-store/observabilité
Client Chat Agent (Windows WinUI)

UI Chat + sources cliquables

Orchestration “assistant conversationnel”

LLM local OpenAI-compatible (llama.cpp server embarqué)

Mémoire conversationnelle (stockée sur serveur en v2.7)

### 3.2 Principes d’architecture

Le serveur est stateless pour la génération (pas de LLM).

Le client est responsable de :

compréhension de l’intention,

plan de recherche,

génération de la réponse,

streaming des tokens en UI.

Le serveur est responsable de :

RAG (retrieval),

ingestion et cohérence d’index,

sécurité (API keys, tenants),

audit/observabilité,

stockage de l’historique (v2.7).
## 4. Profils de déploiement
### 4.1 Profil Serveur — ServerLinuxCompose (recommandé)

OS : Linux (VM / bare metal)

Runtime : Docker Engine + Docker Compose

Services :

`backend` (.NET)

`postgres`

`qdrant`

`tei-embeddings`

(optionnel) `otel-collector`, `prometheus`, `grafana`

Aucun service LLM.
### 4.2 Profil Client — Windows (obligatoire)

OS : Windows 10/11

App : WinUI 3 (MSIX)

Inclus :

binaire `llama.cpp server` (OpenAI-compatible) embarqué

assistant de configuration au premier lancement

Modèles : téléchargés/placés localement via l’assistant (pas inclus dans le MSIX si trop
volumineux).

## 5. Données et modèles
### 5.1 Entités principales (Postgres)

`tenants`

`api_keys`

`documents`

`document_versions` (option, recommandé)

`ingestion_jobs`

`chat_sessions`

`chat_messages`

`audit_events`
### 5.2 Principes de stockage chat (v2.7)

Historique serveur uniquement (Postgres), segmenté par tenant.

Identité utilisateur :

`user_id` généré côté client (GUID) et persisté localement

envoyé au serveur dans les requêtes chat (avec l’API key)
> Évolution v2.8+ possible : cache/DB locale client (SQLite) + sync.
### 5.3 Payload Qdrant (chunks)
Chaque point contient :

`tenant_id`

`doc_id`, `doc_name`, `doc_path`, `category`

`chunk_id`, `chunk_index`

`page_start`, `page_end`

`text` (chunk)

`embed_text` (optionnel)

`hash_doc` / `hash_chunk`

timestamps
## 6. API serveur
### 6.1 Auth

Header : `X-Api-Key: `

Résolution : `tenant_id`, `api_key_id`, `is_admin`
### 6.2 Endpoints Retrieval (RAG)
#### `POST /rag/search`
Recherche vectorielle + retour de chunks sourçables.
Request (exemple)
{
  "query": "Comment régler la vanne XYZ ?",
  "category": "general",
  "topK": 12,
  "minScore": 0.2,
  "diversity": {
    "maxChunksPerDoc": 2,
    "preferDistinctPages": true
  }
}
Response (exemple)
{
  "requestId": "...",
  "items": [
    {
      "score": 0.63,
      "docId": "...",
      "docName": "Manual ABC.pdf",
      "docPath": "...",
      "category": "general",
      "pageStart": 12,
      "pageEnd": 13,
      "chunkId": "...",
      "chunkIndex": 42,
      "text": "..."
    }
  ],
  "metrics": {
    "tookMs": 54,
    "returned": 12
  }
}
> Note : le serveur ne génère pas de réponse textuelle finale.
### 6.3 Endpoints Chat Store (historique serveur)
#### `POST /chat/sessions`
Crée ou retourne une session.
#### `GET /chat/sessions?userId=...`
Liste les sessions d’un utilisateur.

#### `POST /chat/messages`
Ajoute un message (user/assistant/system).
#### `GET /chat/messages?sessionId=...`
Récupère l’historique (paginé).
### 6.4 Endpoints Ingestion

`POST /ingestion/scan` (admin)

`GET /ingestion/jobs` (admin)

`GET /documents` / `GET /documents/{id}` (admin)
### 6.5 Endpoints Observabilité

`GET /health`

`GET /ready`

`GET /metrics` (optionnel)
## 7. Ingestion (Watcher / Scanner / Worker)
### 7.1 Watcher

Surveille un dossier documents.

Debounce.

Crée des jobs `upsert`.

Marque `missing_since` si fichier disparu.
### 7.2 Scanner

Réconciliation périodique.

Anti-wipe : protection si le répertoire apparaît vide de manière suspecte.

Mode auto-heal : si Qdrant est vide et que DB contient des documents, forcer la réindexation.
### 7.3 Worker

Extraction (PDF → texte)

Chunking

Embeddings TEI

Upsert Qdrant

Delete Qdrant par doc si nécessaire

## 8. Assistant conversationnel (client) — “ChatGPT-like”
### 8.1 Objectif UX
Donner une expérience de chat comparable à un assistant avancé :

comprend le besoin

clarifie si nécessaire

cherche intelligemment

répond clairement

propose des prochaines étapes

reste honnête sur les limites (sources manquantes)
### 8.2 Pipeline d’orchestration (client)
#### Étape A — Intent Router (LLM local, non-stream)

Détecte : intent (question, procédure, synthèse, troubleshooting, comparaison, rédaction)

Décide :

besoin de clarification (0–2 questions max)

plan de retrieval (2–4 requêtes max)

style de réponse

seuil de confiance
#### Étape B — Retrieval multi-requêtes (serveur)

Appels `POST /rag/search` 1..N (selon plan)
#### Étape C — Fusion / Diversité / Rerank (client)

Déduplication

Diversité doc/page

Rerank (option) via petit pass LLM local ou heuristique
#### Étape D — Answer (LLM local, stream)
Génère la réponse avec structure recommandée :
1) Réponse sourcée (citations par paragraphe/puce)
2) Sources (liste doc/page)
3) Ce qui manque / incertitudes (si nécessaire)
4) Prochaines étapes (1–3 actions)
#### Étape E — Critic pass (LLM local, optionnel)

Vérifie que les affirmations factuelles sont soutenues par les sources

Corrige / ajoute avertissements si nécessaire
### 8.3 Mémoire conversationnelle (v2.7)

Le client récupère l’historique via le serveur.

Injection au prompt :

un résumé de session (généré périodiquement)

les derniers messages (fenêtre)
### 8.4 Modes

Assistant (défaut) : sourcé + initiative

Strict (audit) : uniquement sourcé, pas de conseils non-sourcés
## 9. LLM local (client) — installation et contraintes
### 9.1 Exigences

LLM local obligatoire.

Installation/configuration via l’application : aucun outil tiers requis.
### 9.2 Moteur recommandé (embarqué)

`llama.cpp server` inclus (OpenAI-compatible).

L’app lance/arrête le process, gère le port local, et vérifie la santé.
### 9.3 Modèles

Téléchargement via l’assistant (premier run) ou import d’un fichier local.

Stockage local : `%LOCALAPPDATA%\SAAIA\Models\...`

Validation : hash/tailles attendues.
### 9.4 Paramètres performance

Choix CPU/GPU (si dispo), threads, context size.

Timeout par requête.
## 10. Sécurité
### 10.1 API keys

Une clé par utilisateur ou par poste (recommandé), liée au tenant.

Rotation possible.
### 10.2 Qdrant

Qdrant non exposé publiquement en prod.

API key Qdrant en profil Linux.
### 10.3 Isolation tenants

Filtrage strict par `tenant_id` sur toutes les requêtes.
### 10.4 Stockage secrets client

API key stockée localement via mécanisme sécurisé (DPAPI recommandé).
## 11. Observabilité
### 11.1 Logs structurés

Champs minimum : `request_id`, `tenant_id`, `user_id`, `path`, `method`.

Ingestion : `doc_id`, `job_id`, `doc_path`.
### 11.2 Health

`/health` : liveness

`/ready` : DB + Qdrant + TEI + signature config valide
### 11.3 OpenTelemetry (optionnel mais recommandé)

Traces ASP.NET + HttpClient

Metrics :

latence retrieval

jobs ingestion started/failed

queue worker

erreurs TEI/Qdrant
## 12. Packaging / Installation
### 12.1 Serveur

Docker Compose “zéro config” :

`.env` minimal

`deployment.config.json` + `.sig`

Post install : `/ready` doit être OK
### 12.2 Client (MSIX)

Installation WinUI via MSIX.

Premier lancement : assistant guidé

connexion serveur (URL)

saisie/validation API key

installation/initialisation LLM local (llama.cpp server)

téléchargement/import modèle

test LLM local + test `/ready`
## 13. Tests et critères d’acceptation
### 13.1 RAG

Retrieval renvoie des chunks pertinents avec doc/page.

Diversité doc/page respectée.
### 13.2 Chat (assistant)

Multi-turn : l’assistant utilise le contexte (historique).

Clarification : 0–2 questions max si ambigu.

Réponse structurée + prochaines étapes.

Transparence : signale clairement ce qui n’est pas dans les sources.
### 13.3 Sans LLM serveur

Le serveur démarre et est pleinement fonctionnel (ingestion/retrieval) sans aucun composant LLM.

Le client fonctionne uniquement via le LLM local.
### 13.4 Sécurité

Accès refusé sans API key.

Isolation tenants vérifiée.
## 14. Livrables

Serveur : compose prod + runbook + config signée

Client : MSIX + assistant de setup LLM

Documentation :

manuel utilisateur

manuel admin

guide dépannage
## 15. Roadmap (macro)

v2.7 : Serveur sans LLM + Client WinUI + LLM local + orchestration assistant + mémoire serveur

v2.8 : cache local + sync optionnelle, outils avancés, rerank amélioré

v2.9 : intégrations (option), SSO/AD (option)
