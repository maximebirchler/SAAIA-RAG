# M4 – Déploiement “prod-like” (Docker Compose)

Ce dossier contient les *runbooks* (procédures) pour :
- installer / démarrer la stack (compose prod)
- mettre à jour
- backup / restore
- diagnostics

> Objectif : une stack **on‑prem** simple à opérer, reproductible, et conforme au mode **config signée**.

## Points clés (chemins)
- Par défaut, les données persistent **dans le repo** (bind-mounts relatifs).
- Pour une installation “prod” hors repo, définis **SAAIA_INSTALL_ROOT** (chemin absolu).
  - Windows (Docker Desktop) : utiliser `C:/...` (forward slashes)
  - Linux : `/opt/...`

## Points clés (réseau)
- Par défaut, les ports sont bind en local uniquement via `SAAIA_BIND_ADDR=127.0.0.1`.
- Pour exposer sur LAN/WAN : `SAAIA_BIND_ADDR=0.0.0.0` (reverse-proxy/TLS recommandé).

## Arborescence
- `../docker-compose.prod.yml` : compose prod (postgres + qdrant + tei + backend)
- `../.env.example` : variables à copier en `.env`
- `../config/deployment.config.prod.template.json` : template de config *à signer*
- `../scripts/prod/*.(ps1|sh)` : scripts install/update/backup/restore/diag

## Quick start
1) Copie `.env.example` → `.env` et remplis les valeurs.
2) (Optionnel) Dans `.env`, mets `SAAIA_INSTALL_ROOT=C:/SAAIA`.
3) Lance `infra/scripts/prod/install.ps1`.
4) Vérifie : `Invoke-WebRequest http://localhost:<BACKEND_HOST_PORT>/ready`.

Ensuite lis : `01_install.md` (bootstrap & hardening).

Puis : `07_smoke_tests.md` (smoke fonctionnels chat-store/rate-limit/audit).

## Validation
- Voir `06_M4_DoD.md` (checklist d’acceptation M4 : install/diag/backup/restore).
Pour les smoke fonctionnels (M1.3/M3.2/M3.3) : `07_smoke_tests.md`.
