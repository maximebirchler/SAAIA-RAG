# 03 – Backup / Restore

## Backup
- `powershell -ExecutionPolicy Bypass -File .\infra\scripts\prod\backup.ps1`
  - Par défaut, si `-OutDir` est **relatif**, le dossier est créé sous `<SAAIA_INSTALL_ROOT>\backups\<timestamp>` (ou sous le repo si INSTALL_ROOT est vide).

Exemple explicite :
- `powershell -ExecutionPolicy Bypass -File .\infra\scripts\prod\backup.ps1 -OutDir C:\SAAIA\backups -IncludeDocuments`

Sauvegarde :
- Postgres (dump SQL)
- Qdrant (archive du storage)
- fichiers `deploy/` (config signée, depuis **InstallRoot**)
- (optionnel) `documents/`

## Restore
- `powershell -ExecutionPolicy Bypass -File .\infra\scripts\prod\restore.ps1 -InDir .\backups\<timestamp>`

Le restore :
- stoppe la stack **et supprime les volumes** (`down -v`) → restore déterministe
- restaure Qdrant (storage) puis Postgres (dump SQL)
- redémarre la stack complète
- attend que `/ready` repasse à **200** avant de terminer.

⚠️ **Attention** : le restore est **destructif** (il écrase l'état actuel). Utilise-le pour restaurer sur une instance cible.
