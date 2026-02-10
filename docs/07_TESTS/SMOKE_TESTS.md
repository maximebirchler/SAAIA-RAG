# Smoke tests — M1.4 (PowerShell)

## Build backend
```powershell
dotnet build "backend\SAAIA.Backend\SAAIA.Backend.csproj"
```

---

## RAG Tests

### 401 sans API key
```powershell
curl.exe -i -X GET "http://localhost:5122/rag/categories"
```

### 200 GET /rag/categories (avec key)
```powershell
curl.exe -i -X GET "http://localhost:5122/rag/categories" `
  -H "X-Api-Key: saaia_dev_bootstrap_2026_CHANGE_ME"
```

### 400 /rag/search query vide
```powershell
@'
{"query":"","topK":5}
'@ | curl.exe -i -X POST "http://localhost:5122/rag/search" `
  -H "Content-Type: application/json" `
  -H "X-Api-Key: saaia_dev_bootstrap_2026_CHANGE_ME" `
  --data-binary "@-"
```

### 200 POST /rag/search basic
```powershell
@'
{"query":"test","topK":5}
'@ | curl.exe -i -X POST "http://localhost:5122/rag/search" `
  -H "Content-Type: application/json" `
  -H "X-Api-Key: saaia_dev_bootstrap_2026_CHANGE_ME" `
  --data-binary "@-"
```

### 200 POST /rag/search avec diversity (CDC v2.7) — NEW M1.4
```powershell
@'
{
  "query":"configuration",
  "topK":5,
  "minScore":0.3,
  "diversity":{
    "maxChunksPerDoc":2,
    "preferDistinctPages":true
  }
}
'@ | curl.exe -i -X POST "http://localhost:5122/rag/search" `
  -H "Content-Type: application/json" `
  -H "X-Api-Key: saaia_dev_bootstrap_2026_CHANGE_ME" `
  --data-binary "@-"
```

---

## Chat Store Tests — NEW M1.4

### 200 POST /chat/sessions (userId obligatoire)
```powershell
@'
{"userId":"550e8400-e29b-41d4-a716-446655440000","title":"Test Session","clientUser":"john.doe"}
'@ | curl.exe -i -X POST "http://localhost:5122/chat/sessions" `
  -H "Content-Type: application/json" `
  -H "X-Api-Key: saaia_dev_bootstrap_2026_CHANGE_ME" `
  --data-binary "@-"
```

### 200 GET /chat/sessions?userId=... (userId obligatoire)
```powershell
curl.exe -i -X GET "http://localhost:5122/chat/sessions?userId=550e8400-e29b-41d4-a716-446655440000&limit=10" `
  -H "X-Api-Key: saaia_dev_bootstrap_2026_CHANGE_ME"
```

### 200 PATCH /chat/sessions/{sessionId}?userId=... (userId obligatoire)
```powershell
@'
{"title":"Updated Session"}
'@ | curl.exe -i -X PATCH "http://localhost:5122/chat/sessions/SESSION_ID?userId=550e8400-e29b-41d4-a716-446655440000" `
  -H "Content-Type: application/json" `
  -H "X-Api-Key: saaia_dev_bootstrap_2026_CHANGE_ME" `
  --data-binary "@-"
```

### 200 DELETE /chat/sessions/{sessionId}?userId=... (userId obligatoire)
```powershell
curl.exe -i -X DELETE "http://localhost:5122/chat/sessions/SESSION_ID?userId=550e8400-e29b-41d4-a716-446655440000" `
  -H "X-Api-Key: saaia_dev_bootstrap_2026_CHANGE_ME"
```

### 400 PATCH sans userId (validation error)
```powershell
@'
{"title":"Should fail"}
'@ | curl.exe -i -X PATCH "http://localhost:5122/chat/sessions/SESSION_ID" `
  -H "Content-Type: application/json" `
  -H "X-Api-Key: saaia_dev_bootstrap_2026_CHANGE_ME" `
  --data-binary "@-"
```

### 400 DELETE sans userId (validation error)
```powershell
curl.exe -i -X DELETE "http://localhost:5122/chat/sessions/SESSION_ID" `
  -H "X-Api-Key: saaia_dev_bootstrap_2026_CHANGE_ME"
```

---

## Notes
- Remplacer `SESSION_ID` par un ID valide (reçu du POST /chat/sessions)
- userId est **OBLIGATOIRE** dans tous les endpoints chat (CDC v2.7)
- diversity est **OPTIONNEL** dans POST /rag/search (backward compat)
- Tous les endpoints retournent `X-Request-Id` (M2.1)

