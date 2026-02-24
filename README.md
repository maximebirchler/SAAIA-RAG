# SAAIA - RAG On-Prem (serveur sans LLM, LLM obligatoire côté client)

SAAIA est un assistant IA RAG 100% on-prem pour environnements industriels/entreprise.

- **Serveur (Linux recommandé)** : ingestion + indexation (TEI + Qdrant) + retrieval sourcé + chat-store + sécurité + observabilité.
- **Client (Windows WinUI)** : expérience chat "type ChatGPT" + **LLM local embarqué** (OpenAI-compatible / llama.cpp server) + téléchargement modèle.

> Décision produit : **aucun LLM côté serveur**. Le serveur ne fait **pas** de génération.

---

## Architecture (vue rapide)

- `backend/` : API .NET (RAG, ingestion, chat-store, admin, health/ready, audit, licensing)
- `infra/` : Docker Compose prod + scripts d'exploitation (install/backup/restore/diag/smoke)
- `client/` : app WinUI (UI chat + orchestration + gestion LLM local + support bundle)
- `contracts/` : DTO/contrats partagés
- `tools/` : outils (signature de config, etc.)

---

## Prérequis

### Serveur (prod)
- Linux (VM ou bare metal)
- Docker Engine + Docker Compose

### Client (Windows)
- Windows 10/11
- (dev) Visual Studio + Windows App SDK

---

## Démarrage rapide (dev)

### 1) Serveur (Docker)

```powershell
# depuis la racine du repo
powershell -NoProfile -ExecutionPolicy Bypass -File .\infra\scripts\prod\install.ps1

# vérifier la readiness
curl http://localhost:5122/ready
```

### 2) Test retrieval

```powershell
$body = @'
{"query":"accord transfert code source","topK":5,"minScore":0.0,"mode":"default"}
'@

$body | curl.exe -s -X POST "http://localhost:5122/rag/search" `
  -H "Content-Type: application/json" `
  -H "X-Api-Key: <YOUR_API_KEY>" `
  --data-binary "@-"
```

### 3) Client WinUI (dev)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\client\scriptsun-client-x64.ps1
```

Les logs client sont dans : `%LOCALAPPDATA%\SAAIA\logs\`.

---

## Provisioning (déploiement IT)

Optionnel : déposer un fichier de provisioning (ex : URL serveur, options UI) sous :

- `C:\ProgramData\SAAIA\provisioning.json`

> La **clé seat** (poste) est idéalement attribuée par poste (licensing), et stockée via DPAPI côté client.

---

## Exploitation (prod)

Scripts principaux :
- `infra/scripts/prod/install.ps1`
- `infra/scripts/prod/update.ps1`
- `infra/scripts/prod/backup.ps1`
- `infra/scripts/prod/restore.ps1`
- `infra/scripts/prod/diag.ps1`
- `infra/scripts/prod/smoke.ps1`

Runbooks : `infra/runbooks/`.

---

## Notes importantes

- **Le serveur ne génère pas** de texte final (pas de chat completions côté serveur).
- Le client orchestre : intent -> retrieval -> génération LLM local -> citations.
- Les catégories de documents proviennent des dossiers dans `documents/` (dynamiques).

---

## Licence

- Licence entreprise (expiration + max seats) validée localement par le serveur (sans cloud).
- Clés "seat" par poste : activées au 1er usage (binding device) ; réutilisables après release admin.

