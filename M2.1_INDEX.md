# M2.1 Implementation — File Index

## Quick Links

### 📋 Overview
- **M2.1_COMPLETION_SUMMARY.txt** — Executive summary (this is your starting point)
- **docs/M2.1_README.md** — Quick start guide

### 📚 Documentation
- **docs/M2.1_IMPLEMENTATION.md** — Technical details (what was implemented)
- **docs/M2.1_DEVELOPER_GUIDE.md** — How to use RequestId in your code
- **docs/M2.1_COMMIT_CHECKLIST.md** — Step-by-step commit instructions

### 💻 Code (Created)
- **backend/SAAIA.Backend/Middleware/RequestIdMiddleware.cs** — Generates X-Request-Id
- **backend/SAAIA.Backend/Middleware/ErrorHandlingMiddleware.cs** — Formats errors
- **backend/SAAIA.Backend/Models/ErrorResponse.cs** — DTO for errors

### 💻 Code (Modified)
- **backend/SAAIA.Backend/Extensions/WebApplicationExtensions.cs**
- **backend/SAAIA.Backend/Auth/ApiKeyAuthMiddleware.cs**
- **backend/SAAIA.Backend/Endpoints/ReadyEndpoints.cs**
- **backend/SAAIA.Backend/Endpoints/RagEndpoints.cs**
- **backend/SAAIA.Backend/Endpoints/ChatStoreEndpoints.cs**

### 🧪 Tests
- **tests/M2.1_tests.ps1** — PowerShell test suite (6 scenarios)

### 📊 Status
- **docs/Plan_action_v2.7_status.md** — Project status (updated for M2.1)

---

## Reading Order

If you're **just joining the project**:
1. Read: `M2.1_COMPLETION_SUMMARY.txt` (this folder)
2. Read: `docs/M2.1_README.md`
3. Read: `docs/04_CONTRACTS/ERROR_MODEL.md` (contract reference)

If you **want to understand the implementation**:
1. Read: `docs/M2.1_IMPLEMENTATION.md`
2. Review: `backend/SAAIA.Backend/Middleware/RequestIdMiddleware.cs`
3. Review: `backend/SAAIA.Backend/Middleware/ErrorHandlingMiddleware.cs`

If you **want to add a new endpoint using RequestId**:
1. Read: `docs/M2.1_DEVELOPER_GUIDE.md`
2. Follow the examples (code snippets provided)

If you **want to commit this code**:
1. Read: `docs/M2.1_COMMIT_CHECKLIST.md`
2. Follow the step-by-step instructions

---

## Key Files Summary

### RequestIdMiddleware.cs
**What it does:**
- Intercepts all HTTP requests
- Retrieves `X-Request-Id` from client header (if provided)
- If not provided, generates a new RequestId (from Activity.Current?.Id, TraceIdentifier, or UUID)
- Stores RequestId in HttpContext.Items for access in endpoints
- Configures log scopes: request_id, path, method
- Adds X-Request-Id header to response

**Usage in endpoints:**
```csharp
var requestId = ctx.GetRequestId();
```

### ErrorHandlingMiddleware.cs
**What it does:**
- Wraps the entire request pipeline in try-catch
- Catches all exceptions (BadHttpRequestException, UnauthorizedAccessException, generic)
- Formats exceptions as JSON: `{ "error": "message", "requestId": "..." }`
- Adds X-Request-Id header to error responses
- Hides technical details in production

**Error codes:**
- 400: BadHttpRequestException (validation)
- 403: UnauthorizedAccessException (authorization)
- 500: Generic exceptions

### ErrorResponse.cs
**What it does:**
- Simple DTO for uniform error format
- Properties: error (string), requestId (string)

---

## Integration Points

### Middleware Order (Critical!)
```
ErrorHandlingMiddleware
  ↓
RequestIdMiddleware
  ↓
RateLimiter
  ↓
ApiKeyAuthMiddleware
  ↓
Endpoints
```

**Why this order?**
1. ErrorHandling must be first to catch all exceptions
2. RequestId must be set before error handling needs it
3. RateLimiter is global
4. Auth happens last, can add tenant_id to log scopes

### Log Scopes
Automatically included in all logs:
- `[request_id=abc-123-...]` (set by RequestIdMiddleware)
- `[path=/rag/search]` (set by RequestIdMiddleware)
- `[method=POST]` (set by RequestIdMiddleware)
- `[tenant_id=xyz-789-...]` (set by ApiKeyAuthMiddleware after auth)

### Error Format
All errors now return:
```json
{
  "error": "Human-readable message",
  "requestId": "abc-123-xyz-789"
}
```

With HTTP status code and `X-Request-Id: abc-123-xyz-789` header.

---

## Testing Checklist

Before committing, verify:

- [ ] Build succeeds: `dotnet build` → 0 warnings, 0 errors
- [ ] GET /health returns X-Request-Id header
- [ ] GET /health with custom X-Request-Id echoes it back
- [ ] GET /ready includes requestId in both header and body
- [ ] POST /rag/search without API key → 401 with { error, requestId }
- [ ] POST /rag/search with invalid query → 400 with { error, requestId }
- [ ] Logs include [request_id] and [tenant_id] scopes
- [ ] No response body is plain text (all JSON)

Run: `.\tests\M2.1_tests.ps1` to automate most of these.

---

## Next Steps

1. **Review** the files in this order:
   - M2.1_COMPLETION_SUMMARY.txt (you are here)
   - docs/M2.1_README.md
   - docs/M2.1_IMPLEMENTATION.md

2. **Test** locally:
   ```bash
   cd backend/SAAIA.Backend
   dotnet build
   dotnet run
   ```

3. **Run tests**:
   ```powershell
   cd tests
   .\M2.1_tests.ps1
   ```

4. **Commit** following: `docs/M2.1_COMMIT_CHECKLIST.md`

---

## Questions?

Refer to:
- `.github/copilot-instructions.md` — Project guidelines
- `docs/Plan_action_v2.7.md` — Feature specifications
- `docs/04_CONTRACTS/ERROR_MODEL.md` — Error contract
- `docs/ARCHITECTURE.md` — System architecture
