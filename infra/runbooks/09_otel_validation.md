# 09 — Validation OpenTelemetry (M2.2)

Objectif : prouver que le toggle OTel fonctionne et que le backend exporte bien traces + métriques vers un collector OTLP.

Le repo fournit :
- `infra/docker-compose.otel.yml` (collector + override backend env)
- `infra/otel/otel-collector-config.yaml` (exporter `debug` -> logs)
- `infra/scripts/prod/otel-up.ps1` / `otel-down.ps1`

---

## A) Démarrer la stack avec OTel

Depuis la racine du repo :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\infra\scripts\prod\otel-up.ps1
```

Attendu :
- backend + collector up
- `/ready => 200`

---

## B) Générer du trafic (pour produire des spans)

Option simple : relancer le smoke sans rate-limit (pour éviter d’être throttlé) :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\infra\scripts\prod\smoke.ps1 `
  -ApiKey "saaia_dev_bootstrap_2026_CHANGE_ME" `
  -SkipRateLimit
```

Ou juste faire un call :

```powershell
'{"query":"ping","topK":1}' | curl.exe -s -X POST "http://localhost:5122/rag/search" -H "Content-Type: application/json" -H "X-Api-Key: saaia_dev_bootstrap_2026_CHANGE_ME" --data-binary "@-"
```

---

## C) Vérifier que le collector reçoit bien

```powershell
docker compose -f .\infra\docker-compose.prod.yml -f .\infra\docker-compose.otel.yml --env-file .\infra\.env logs -f otel-collector
```

Attendu : des logs `debug` avec des traces/metrics OTLP (spans ASP.NET + HttpClient).

---

## D) Stopper le collector (sans impacter la stack prod)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\infra\scripts\prod\otel-down.ps1
```

Optionnel (supprimer aussi les volumes du *compose override*) :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\infra\scripts\prod\otel-down.ps1 -RemoveVolumes
```

---
