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

Avec la clé bootstrap (ou une clé admin) :

```powershell
cd <repo>
powershell -NoProfile -ExecutionPolicy Bypass -File .\infra\scripts\prod\smoke.ps1 -ApiKey "SAAIA_BOOTSTRAP_API_KEY_ICI"
```

Tester aussi l’audit (nécessite une **clé admin**) :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\infra\scripts\prod\smoke.ps1 `
  -ApiKey "CLE_POUR_CHAT_RAG" `
  -AdminApiKey "CLE_ADMIN_POUR_AUDIT"
```

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

Lister les messages (doit retourner ≥2) :

```powershell
curl.exe -s "http://localhost:5122/chat/sessions/$SID/messages?userId=$USER&limit=50" -H "X-Api-Key: $API"
```

Lister les sessions :

```powershell
curl.exe -s "http://localhost:5122/chat/sessions?userId=$USER&limit=10" -H "X-Api-Key: $API"
```

Supprimer la session (cleanup) :

```powershell
curl.exe -s -X DELETE "http://localhost:5122/chat/sessions/$SID?userId=$USER" -H "X-Api-Key: $API"
```

### 3) Rate limiting (M3.2)

Boucle agressive : on veut voir apparaître un `429` et un header `Retry-After`.

```powershell
$API="saaia_dev_bootstrap_2026_CHANGE_ME"
1..600 | % {
  '{"query":"ping","topK":1}' | curl.exe -s -o NUL -w "%{http_code}`n" -X POST "http://localhost:5122/rag/search" `
    -H "Content-Type: application/json" `
    -H "X-Api-Key: $API" `
    --data-binary "@-"
}
```

### 4) Audit (M3.3) — clé admin requise

```powershell
$ADMIN="<admin key>"
curl.exe -s "http://localhost:5122/admin/audit?limit=20" -H "X-Api-Key: $ADMIN"
```

---

## Notes

- Si tu n’obtiens pas de `429`, c’est possible si `RateLimiting:PermitLimit` est élevé et/ou si ta boucle est trop lente.
  - Augmente le nombre d’itérations (ex: `-RateLimitAttempts 1500`)
  - Ou abaisse temporairement `PermitLimit` dans la config signée (pour validation DoD).
