# 05 – Notes sécurité (minimum)

- **Config signée** : ne versionne jamais `deploy/deployment.config.json` + `.sig`.
- **Private key** : stocke le fichier de clé hors repo (ex: `C:\Users\...\secrets\...key`).
- **Bootstrap** : désactive après l’installation (sinon n’importe qui avec la clé bootstrap peut créer des clés).
- **Exposition réseau** :
  - Par défaut, `docker-compose.prod.yml` mappe les ports sur l’hôte.
  - Par défaut, on bind en local uniquement via `SAAIA_BIND_ADDR=127.0.0.1`.
  - Si tu exposes sur le réseau (LAN/WAN), ajoute un reverse proxy + TLS + auth, et passe `SAAIA_BIND_ADDR=0.0.0.0`.
  - En vrai prod : évite d'exposer Postgres/Qdrant/TEI (garde-les accessibles uniquement depuis le réseau docker).

## Hardening rapide (recommandé)
1) Crée une nouvelle clé admin via `/admin/keys`.
2) Révoque la clé `bootstrap`.
3) Mets `SAAIA_BOOTSTRAP_ENABLED=false` dans `infra/.env`.
4) Re-signe + restart backend :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\infra\scripts\prod\diag.ps1 -ResignOnly
docker compose -f .\infra\docker-compose.prod.yml --env-file .\infra\.env restart backend
```
