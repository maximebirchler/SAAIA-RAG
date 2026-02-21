# 02 – Mise à jour

1) Récupère les changements (git pull).
2) Relance :
   - `powershell -ExecutionPolicy Bypass -File .\infra\scripts\prod\update.ps1`

Le script :
- relit `infra/.env`
- re‑détecte le `SAAIA_INSTALL_ROOT` (pour viser les bons bind-mounts)
- `docker compose pull`
- `docker compose up -d --build`
