# M2.1 Commit Checklist & Commands

## What was done

✅ Implemented RequestId middleware + error handling + log scopes  
✅ All responses now include X-Request-Id header  
✅ All errors return { error, requestId } JSON format  
✅ Log scopes include request_id, path, method, tenant_id  
✅ Build succeeds (0 warnings, 0 errors)  
✅ Tests provided (PowerShell)  
✅ Documentation complete  

## Files Created

```
backend/SAAIA.Backend/Middleware/RequestIdMiddleware.cs
backend/SAAIA.Backend/Middleware/ErrorHandlingMiddleware.cs
backend/SAAIA.Backend/Models/ErrorResponse.cs
tests/M2.1_tests.ps1
docs/M2.1_IMPLEMENTATION.md
docs/M2.1_DEVELOPER_GUIDE.md
docs/M2.1_COMMIT_CHECKLIST.md (this file)
```

## Files Modified

```
backend/SAAIA.Backend/Extensions/WebApplicationExtensions.cs
backend/SAAIA.Backend/Auth/ApiKeyAuthMiddleware.cs
backend/SAAIA.Backend/Endpoints/ReadyEndpoints.cs
backend/SAAIA.Backend/Endpoints/RagEndpoints.cs
backend/SAAIA.Backend/Endpoints/ChatStoreEndpoints.cs
docs/Plan_action_v2.7_status.md
```

## Build & Test Commands

### Build
```bash
cd backend/SAAIA.Backend
dotnet build
# Expected: Build succeeded (0 warnings, 0 errors)
```

### Run Server
```bash
cd backend/SAAIA.Backend
dotnet run
# Server starts on http://localhost:5000
```

### Run Tests (PowerShell)
```powershell
cd tests
.\M2.1_tests.ps1
```

### Manual Test
```bash
# Test /health endpoint
curl -H "X-Request-Id: my-test-id" http://localhost:5000/health

# Should return:
# Headers: X-Request-Id: my-test-id
# Body: {"ok":true,"ts":"2026-02-08T..."}

# Test 401 error
curl -X POST http://localhost:5000/rag/search \
  -H "Content-Type: application/json" \
  -d '{"query":"test"}'

# Should return:
# Status: 401
# Headers: X-Request-Id: (auto-generated or from client)
# Body: {"error":"Missing API key.","requestId":"..."}
```

## How to Commit

```bash
# Stage files
git add backend/SAAIA.Backend/Middleware/
git add backend/SAAIA.Backend/Models/
git add backend/SAAIA.Backend/Extensions/WebApplicationExtensions.cs
git add backend/SAAIA.Backend/Auth/ApiKeyAuthMiddleware.cs
git add backend/SAAIA.Backend/Endpoints/ReadyEndpoints.cs
git add backend/SAAIA.Backend/Endpoints/RagEndpoints.cs
git add backend/SAAIA.Backend/Endpoints/ChatStoreEndpoints.cs
git add tests/M2.1_tests.ps1
git add docs/M2.1_*.md
git add docs/Plan_action_v2.7_status.md

# Commit with message
git commit -m "server: M2.1 RequestId middleware + error handling + log scopes

- Add RequestIdMiddleware: generates/retrieves X-Request-Id, configures log scopes
- Add ErrorHandlingMiddleware: formats errors as { error, requestId }
- Update all endpoints to include requestId in responses
- Add ErrorResponse DTO for uniform error format
- Update ApiKeyAuthMiddleware with log scopes (tenant_id)
- Middleware order: ErrorHandling → RequestId → RateLimiter → Auth
- Add PowerShell tests (tests/M2.1_tests.ps1)
- Build: ✅ success (0 warnings, 0 errors)
- Docs: M2.1_IMPLEMENTATION.md, M2.1_DEVELOPER_GUIDE.md"

# Push
git push origin cdc-v2.7-m1.1
```

## Post-Commit Steps

1. Update `docs/Plan_action_v2.7_status.md` with new commit SHA
2. Update `docs/CHANGELOG.md` with date and commit details
3. Run tests on deployed instance to verify header/format handling
4. Update integration tests if you have external API consumers

## Verification Checklist

- [ ] Build succeeds
- [ ] /health returns X-Request-Id header
- [ ] /ready returns X-Request-Id in header and requestId in body
- [ ] /rag/search without API key returns 401 with { error, requestId }
- [ ] /rag/search with invalid query returns 400 with { error, requestId }
- [ ] Logs include [request_id=...] [tenant_id=...] scopes
- [ ] Response body format is JSON (not plain text)
- [ ] No breaking changes to existing endpoints
- [ ] All tests pass

## Next Steps (M2.2)

After this commit is accepted:
1. M2.2: Add OpenTelemetry (optional toggle)
2. M2.3: Add signature validation to /ready
3. M1.4: Add user_id support to chat-store

## Contact / Questions

Refer to:
- `docs/04_CONTRACTS/ERROR_MODEL.md` — error contract definition
- `docs/M2.1_DEVELOPER_GUIDE.md` — how to use RequestId in new endpoints
- `tests/M2.1_tests.ps1` — example API calls
