# 07 — Smoke tests (serveur v2.7)

Objectif : valider rapidement (et de façon reproductible) que le serveur respecte les DoD suivants :
- **M1.3** Chat-store (sessions/messages)
- **M3.2** Rate limiting (429 + Retry-After)
- **M3.3** Audit (listing admin)
- **M2.x** Ready/liveness

> Pré‑requis : la stack tourne (`install.ps1` OK) et `/ready` renvoie 200.

---

## A) Smoke automatisé (recommandé)

Un script est fourni :

- `infra/scripts/prod/smoke.ps1`

### Exemples

Avec la **clé bootstrap** (ou toute clé admin) :

```powershell
cd <repo>
powershell -NoProfile -ExecutionPolicy Bypass -File .\infra\scripts\prod\smoke.ps1 -ApiKey "SAAIA_BOOTSTRAP_API_KEY_ICI"
```

> Note : la clé bootstrap créée par l’installer est **ADMIN par défaut**, donc l’audit est testé automatiquement.

Avec une clé “client” (non-admin) + une clé admin séparée pour l’audit :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\infra\scripts\prod\smoke.ps1 `
  -ApiKey "CLE_CLIENT_POUR_CHAT_RAG" `
  -AdminApiKey "CLE_ADMIN_POUR_AUDIT"
```

Éviter d’être throttlé ~60s (si tu veux enchaîner des appels API manuels juste après) :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\infra\scripts\prod\smoke.ps1 `
  -ApiKey "SAAIA_BOOTSTRAP_API_KEY_ICI" `
  -SkipRateLimit
```

Variables d’environnement possibles :
- `SAAIA_SMOKE_API_KEY`
- `SAAIA_SMOKE_ADMIN_API_KEY`

---

## B) Tests manuels (curl PowerShell)

### 1) Ready

```powershell
curl.exe -i http://localhost:5122/ready
```

Attendu : `HTTP/1.1 200 OK`

### 2) Chat-store (M1.3)

```powershell
$API  = "saaia_dev_bootstrap_2026_CHANGE_ME"
$USER = (New-Guid).Guid
```

Créer une session :

```powershell
'{"userId":"'$USER'","title":"Smoke","clientUser":"manual"}' |
  curl.exe -s -X POST "http://localhost:5122/chat/sessions" `
    -H "Content-Type: application/json" `
    -H "X-Api-Key: $API" `
    --data-binary "@-"
```

Récupère `sessionId`, puis ajoute 2 messages :

```powershell
$SID="<sessionId>"

'{"userId":"'$USER'","role":"user","content":"Salut","sourcesJson":null}' |
  curl.exe -s -X POST "http://localhost:5122/chat/sessions/$SID/messages" `
    -H "Content-Type: application/json" `
    -H "X-Api-Key: $API" `
    --data-binary "@-"

'{"userId":"'$USER'","role":"assistant","content":"Bonjour","sourcesJson":null}' |
  curl.exe -s -X POST "http://localhost:5122/chat/sessions/$SID/messages" `
    -H "Content-Type: application/json" `
    -H "X-Api-Key: $API" `
    --data-binary "@-"
```

Lister les messages :

```powershell
curl.exe -s "http://localhost:5122/chat/sessions/$SID/messages?userId=$USER&limit=50" -H "X-Api-Key: $API"
```

Supprimer la session (⚠️ PowerShell : utiliser `$($SID)` avant `?userId=`) :

```powershell
curl.exe -s -X DELETE "http://localhost:5122/chat/sessions/$($SID)?userId=$USER" -H "X-Api-Key: $API"
```

### 3) Rate limiting (M3.2)

⚠️ Ce test peut throttler la clé pendant ~60s (`Retry-After`).  
Si tu veux éviter ça, utilise le smoke avec `-SkipRateLimit`.

```powershell
1..300 | % {
  '{"query":"ping","topK":1}' | curl.exe -s -o NUL -w "%{http_code}`n" -X POST "http://localhost:5122/rag/search" -H "Content-Type: application/json" -H "X-Api-Key: $API" --data-binary "@-"
}
```

Attendu : apparition de `429` + header `Retry-After`.

### 4) Audit (M3.3)

Nécessite une **clé admin**.

```powershell
$ADMIN = "CLE_ADMIN"
curl.exe -s "http://localhost:5122/admin/audit?limit=50" -H "X-Api-Key: $ADMIN"
```

Attendu : JSON avec `{ items: [...], total: ... }`

