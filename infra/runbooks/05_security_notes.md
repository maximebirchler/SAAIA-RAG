# 05 – Notes sécurité (minimum)

- **Config signée** : ne versionne jamais `deploy/deployment.config.json` + `.sig`.
- **Private key** : stocke le fichier de clé hors repo (ex: `C:\Users\...\secrets\...key`).
- **Bootstrap** : désactive après l’installation (sinon n’importe qui avec la clé bootstrap peut créer des clés).
- **Exposition réseau** :
  - Par défaut, `docker-compose.prod.yml` mappe les ports sur l’hôte.
  - `SAAIA_BIND_ADDR` ne contrôle que le backend. Par défaut, il reste local sur `127.0.0.1`.
  - PostgreSQL, Qdrant et TEI utilisent `SAAIA_INTERNAL_BIND_ADDR`, qui doit rester `127.0.0.1` en production.
  - Si le backend est exposé sur le LAN/WAN avec `SAAIA_BIND_ADDR=0.0.0.0`, place-le derrière un reverse proxy TLS authentifié ou dans un tunnel chiffré administré. Une API HTTP directement accessible transmettrait la clé et les contenus sans chiffrement de transport.
  - Ne publie jamais les ports PostgreSQL, Qdrant ou TEI sur une interface réseau du serveur.

- **Chiffrement au repos** :
  - Les volumes PostgreSQL, Qdrant, les documents et les sauvegardes ne sont pas chiffrés par l'application.
  - Le volume de l'hôte qui contient `SAAIA_INSTALL_ROOT` et chaque destination de sauvegarde doit être chiffré par le système ou l'infrastructure du client.
  - `backup.ps1` produit actuellement un dump SQL, une archive Qdrant et des copies de configuration en clair. Ne déplace pas ces fichiers hors d'un stockage chiffré et contrôlé.

## Hardening rapide (recommandé)
1) Crée une nouvelle clé admin via `/admin/keys`.
2) Révoque la clé `bootstrap`.
3) Mets `SAAIA_BOOTSTRAP_ENABLED=false` dans `infra/.env`.
4) Re-signe + restart backend :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\infra\scripts\prod\diag.ps1 -ResignOnly
docker compose -f .\infra\docker-compose.prod.yml --env-file .\infra\.env restart backend
```
