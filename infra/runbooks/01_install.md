# 01 – Installation (M4)

## Pré‑requis
- **Windows** : Docker Desktop + PowerShell 5.1+
- **Linux (recommandé en prod)** : Docker Engine + Docker Compose (plugin) + bash
- **.NET SDK 8.x** (Windows/Linux) : requis pour exécuter `tools/ConfigSigner` lors de `install` / `diag -ResignOnly`
- Ton **private key** de signature (Ed25519 RAW private key au format base64 texte) compatible avec `TrustedKeyring`.

## Étapes
1) Dans `infra/`, copie `.env.example` → `.env` et configure :
   - `POSTGRES_PASSWORD`
   - `SAAIA_AUTH_PEPPER`
   - `SAAIA_BOOTSTRAP_API_KEY`
   - `SAAIA_CONFIG_PRIVATE_KEY_PATH`
   - (optionnel) `QDRANT_API_KEY`, `TEI_MODEL_ID`, `HF_TOKEN`

2) (Optionnel) Si tu veux stocker **data + documents + config** hors du repo :
   - Dans `.env`, mets **un chemin absolu** :
     - Windows (Docker Desktop) : `SAAIA_INSTALL_ROOT=C:/SAAIA`
     - Linux : `SAAIA_INSTALL_ROOT=/opt/saaia`

3) Lance :
   - `powershell -ExecutionPolicy Bypass -File .\infra\scripts\prod\install.ps1`
   - (optionnel) **avec collector local OpenTelemetry** : `powershell -ExecutionPolicy Bypass -File .\infra\scripts\prod\install.ps1 -WithOtel`
     - alternative sans paramètre : dans `infra/.env`, mets `SAAIA_INSTALL_WITH_OTEL=true`

Le script :
- génère `deployment.config.json` depuis le template
- signe la config (`deployment.config.sig`)
- écrit les deux fichiers dans `<SAAIA_INSTALL_ROOT>\deploy` (ou `deploy/` à la racine du repo si INSTALL_ROOT est vide)
- démarre `docker compose -f infra/docker-compose.prod.yml up -d`
- si `-WithOtel` (ou `SAAIA_INSTALL_WITH_OTEL=true`), démarre aussi le collector via `infra/docker-compose.otel.yml` et active l’export OTLP du backend

## Vérifications
- Backend : `Invoke-WebRequest http://localhost:<BACKEND_HOST_PORT>/ready`
- Qdrant : `Invoke-WebRequest http://localhost:<QDRANT_HOST_PORT>/collections` (si auth activée, ajoute le header `api-key`)
- TEI : `Invoke-WebRequest http://localhost:<TEI_HOST_PORT>/`

## Stopper l’OTel collector (si activé)
- `powershell -ExecutionPolicy Bypass -File .\infra\scripts\prod\otel-down.ps1`

## Bootstrap (important)
Le template active le bootstrap via `SAAIA_BOOTSTRAP_ENABLED=true` (dans `infra/.env`) pour insérer la clé
`SAAIA_BOOTSTRAP_API_KEY` lors du **1er démarrage**.

### Hardening recommandé (après validation)
Objectif : ne plus dépendre de la clé bootstrap.

1) Crée une **nouvelle clé admin** (à stocker en coffre / hors repo) :

```powershell
$BOOT = "<SAAIA_BOOTSTRAP_API_KEY>"

'{"label":"admin","isAdmin":true}' |
  curl.exe -s -X POST "http://localhost:5122/admin/keys" `
    -H "Content-Type: application/json" `
    -H "X-Admin-Key: $BOOT" `
    --data-binary "@-"
```

2) (Optionnel) Crée une **clé non-admin** pour le client WinUI :

```powershell
$ADMIN = "<la nouvelle clé admin retournée>"

'{"label":"client","isAdmin":false}' |
  curl.exe -s -X POST "http://localhost:5122/admin/keys" `
    -H "Content-Type: application/json" `
    -H "X-Admin-Key: $ADMIN" `
    --data-binary "@-"
```

3) Révoque la clé bootstrap :

```powershell
$ADMIN = "<la nouvelle clé admin>"
$keys = curl.exe -s "http://localhost:5122/admin/keys" -H "X-Admin-Key: $ADMIN" | ConvertFrom-Json
$boot = $keys | Where-Object { $_.Label -eq "bootstrap" } | Select-Object -First 1
curl.exe -s -X POST "http://localhost:5122/admin/keys/$($boot.ApiKeyId)/revoke" -H "X-Admin-Key: $ADMIN"
```

4) Désactive le bootstrap + re-signe :

```powershell
# Dans infra/.env
# SAAIA_BOOTSTRAP_ENABLED=false

powershell -NoProfile -ExecutionPolicy Bypass -File .\infra\scripts\prod\diag.ps1 -ResignOnly
docker compose -f .\infra\docker-compose.prod.yml --env-file .\infra\.env restart backend
```
