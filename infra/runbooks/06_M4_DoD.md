# 06 – M4 Definition of Done (DoD)

Ce document fixe les critères *objectifs* pour considérer **M4 terminé** (déploiement “prod-like”).

## Pré‑requis
- Docker Desktop (Windows) / Docker Engine (Linux)
- PowerShell 5.1+ (Windows)
- Un fichier de clé privée Ed25519 (base64) **hors repo** : `SAAIA_CONFIG_PRIVATE_KEY_PATH`

## DoD – Critères techniques

### A) Installation / Démarrage (install.ps1)
Commande :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\infra\scripts\prod\install.ps1
```

Attendu :
- Génération + signature `deployment.config.json` + `deployment.config.sig` dans `<SAAIA_INSTALL_ROOT>\deploy`.
- `docker compose up -d --build` OK.
- Le script affiche : `/ready => 200 (try X/40)`.

### B) Réseau sécurisé par défaut (bind local)
Commande :

```powershell
docker compose -f .\infra\docker-compose.prod.yml --env-file .\infra\.env ps
```

Attendu :
- Ports bind sur **127.0.0.1** par défaut (ex: `127.0.0.1:5122->5122/tcp`).
- Pour exposer sur LAN/WAN : `SAAIA_BIND_ADDR=0.0.0.0` (reverse proxy/TLS recommandé).

### C) Health global
Commande :

```powershell
curl.exe -i http://localhost:5122/ready
```

Attendu :
- `HTTP/1.1 200 OK`
- `"ok": true`
- `config_signature_verified: true`

### D) Diag bundle
Commande :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\infra\scripts\prod\diag.ps1 -OutDir diag
```

Attendu :
- Un dossier créé sous `<SAAIA_INSTALL_ROOT>\diag\<timestamp>`
- Fichiers : `compose_ps.txt`, `ready.json`, `logs_backend.txt`, `logs_qdrant.txt`, `logs_tei.txt`, `logs_postgres.txt`

### E) Backup
Commande :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\infra\scripts\prod\backup.ps1 -IncludeDocuments
```

Attendu :
- Un dossier créé sous `<SAAIA_INSTALL_ROOT>\backups\<timestamp>`
- Fichiers :
  - `postgres_dump.sql`
  - `qdrant_storage.tgz`
  - `deploy\deployment.config.json` + `deploy\deployment.config.sig`
  - (optionnel) `documents\...`

### F) Restore (déterministe)
Commande :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\infra\scripts\prod\restore.ps1 -InDir "C:\SAAIA\backups\<timestamp>"
```

Attendu :
- `Postgres ready (try X/60)`
- `== Start full stack ==`
- `/ready => 200 (try X/60)`
- `DONE`

⚠️ Le restore est **destructif** : il fait `docker compose down -v` (wipe volumes) avant restauration.

### G) Smoke test RAG

> Pour des smoke plus complets (Chat-store, rate-limit, audit) : voir `07_smoke_tests.md`.
Commande :

```powershell
$API = "<ta clé>"  # bootstrap ou admin
'{"query":"transfert du code source","category":"general","topK":5}' |
  curl.exe -s -X POST "http://localhost:5122/rag/search" `
    -H "Content-Type: application/json" `
    -H "X-Api-Key: $API" `
    --data-binary "@-"
```

Attendu :
- JSON avec `items[]` non vide et champs `docName`, `pageStart`, `pageEnd`, `text`.

## DoD – Hardening (recommandé)
Après validation, on recommande :
1) Créer une nouvelle clé admin.
2) Révoquer la clé `bootstrap`.
3) Mettre `SAAIA_BOOTSTRAP_ENABLED=false` dans `infra/.env`.
4) Re-signer et redémarrer le backend :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\infra\scripts\prod\diag.ps1 -ResignOnly
docker compose -f .\infra\docker-compose.prod.yml --env-file .\infra\.env restart backend
```
