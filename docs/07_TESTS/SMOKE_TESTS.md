# Smoke tests (PowerShell)

## 401 sans API key
```powershell
curl.exe -i -X GET "http://localhost:5122/rag/categories"
```

## 400 /rag/search query vide
```powershell
@'
{"query":"","topK":5}
'@ | curl.exe -i -X POST "http://localhost:5122/rag/search" `
  -H "Content-Type: application/json" `
  -H "X-Api-Key: saaia_dev_bootstrap_2026_CHANGE_ME" `
  --data-binary "@-"
```

## test "ok"
```powershell
@'
{"query":"test","topK":5}
'@ | curl.exe -i -X POST "http://localhost:5122/rag/search" `
  -H "Content-Type: application/json" `
  -H "X-Api-Key: saaia_dev_bootstrap_2026_CHANGE_ME" `
  --data-binary "@-"
```

## Build backend
```powershell
dotnet build "backend\SAAIA.Backend\SAAIA.Backend.csproj"
```
