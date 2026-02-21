# 04 – Diagnostics

- `powershell -ExecutionPolicy Bypass -File .\infra\scripts\prod\diag.ps1`

Collecte :
- `docker compose ps`
- ping `/ready`
- logs (backend/qdrant/tei/postgres)

Option : `-OutDir` pour écrire un bundle à partager.
- Si `-OutDir` est **relatif**, le bundle est créé sous `<SAAIA_INSTALL_ROOT>\<OutDir>\<timestamp>` (ou sous le repo si INSTALL_ROOT est vide).

Option : `-ResignOnly` pour re‑signer la config dans `<SAAIA_INSTALL_ROOT>\deploy`.
