# SAAIA - RAG On-Prem

SAAIA est un assistant IA RAG 100% on-prem pour environnements industriels/entreprise.

- **Serveur (Linux recommandé)** : ingestion + indexation (TEI + Qdrant) + retrieval sourcé + chat-store + sécurité + observabilité.
- **Client (Windows WinUI)** : expérience chat "type ChatGPT" + **petit LLM local embarqué** (OpenAI-compatible / llama.cpp server) + téléchargement modèle.

> État actuel : le chemin standard génère la réponse avec le petit LLM local du
> poste. L'amendement A755 ajoute une capacité avancée optionnelle : le client
> crée un handoff explicite et le backend pilote alors un grand LLM configuré,
> revalide les preuves et publie seulement le résultat sourcé validé. Cette
> capacité reste `TESTE_NON_APPROUVE` et désactivée par défaut.

Le développement dispose aussi de deux modes externes explicitement bornés :
`OpenAiDev` pour la baseline GPT-5.6 Terra et `RunPodBench` pour qualifier un
llama-server distant. Ils sont désactivés par défaut et servent à préparer la
cible avancée finale : un grand modèle hébergé sur l'infrastructure on-prem du
client, sans dépendance OpenAI ou RunPod. Voir
[`documents/agent/llm-provider-architecture-v1.md`](documents/agent/llm-provider-architecture-v1.md).

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
powershell -NoProfile -ExecutionPolicy Bypass -File .\client\scripts\run-client-x64.ps1
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

- Le chemin simple reste : intent -> retrieval -> génération LLM locale -> citations.
- Le chemin avancé, lorsqu'il est licencié et explicitement configuré, exécute
  Planner -> tools RAG SAAIA -> Writer sur le backend avec un fournisseur unique.
- Les catégories de documents proviennent des dossiers dans `documents/` (dynamiques).

---

## Licence

- Licence entreprise (expiration + max seats) validée localement par le serveur (sans cloud).
- Clés "seat" par poste : activées au 1er usage (binding device) ; réutilisables après release admin.
