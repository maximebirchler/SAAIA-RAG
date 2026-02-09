# M2.1 Implementation — Quick Start

## Summary

**M2.1 (RequestId middleware + error handling + log scopes)** has been successfully implemented according to the CDC v2.7 specification.

### Key Features Implemented

✅ **X-Request-Id Header**
- Automatically added to all responses (including errors)
- Client can provide via header, or server auto-generates
- Supports distributed tracing

✅ **Uniform Error Format**
- All errors return JSON: `{ error: string, requestId: string }`
- Replaces plain text error responses
- Includes requestId for correlation

✅ **Log Scopes**
- Automatically includes: `request_id`, `path`, `method`
- After authentication: `tenant_id`
- Structured logging for observability

✅ **Production Ready**
- Build succeeds (0 warnings, 0 errors)
- No breaking changes
- Comprehensive tests provided

## What Changed

### New Files (8 files)
- `backend/SAAIA.Backend/Middleware/RequestIdMiddleware.cs`
- `backend/SAAIA.Backend/Middleware/ErrorHandlingMiddleware.cs`
- `backend/SAAIA.Backend/Models/ErrorResponse.cs`
- `tests/M2.1_tests.ps1`
- `docs/M2.1_IMPLEMENTATION.md`
- `docs/M2.1_DEVELOPER_GUIDE.md`
- `docs/M2.1_COMMIT_CHECKLIST.md`
- `docs/M2.1_README.md` (this file)

### Modified Files (6 files)
- `backend/SAAIA.Backend/Extensions/WebApplicationExtensions.cs` (added middlewares)
- `backend/SAAIA.Backend/Auth/ApiKeyAuthMiddleware.cs` (error formatting + log scopes)
- `backend/SAAIA.Backend/Endpoints/ReadyEndpoints.cs` (requestId in response)
- `backend/SAAIA.Backend/Endpoints/RagEndpoints.cs` (use ctx.GetRequestId())
- `backend/SAAIA.Backend/Endpoints/ChatStoreEndpoints.cs` (import middleware)
- `docs/Plan_action_v2.7_status.md` (updated status)

## How to Use

### 1. Build & Run

```bash
cd backend/SAAIA.Backend
dotnet build  # Should succeed with 0 warnings
dotnet run     # Server on http://localhost:5000
```

### 2. Test

**Option A: Automated PowerShell Tests**
```powershell
cd tests
.\M2.1_tests.ps1
```

**Option B: Manual Test**
```bash
# Test RequestId in responses
curl -H "X-Request-Id: my-trace-123" http://localhost:5000/health

# Test error format
curl -X POST http://localhost:5000/rag/search \
  -H "Content-Type: application/json" \
  -d '{"query":"test"}'
# Returns: {"error":"Missing API key.","requestId":"..."}
```

### 3. For New Endpoints

When creating a new endpoint, you can access RequestId:

```csharp
app.MapPost("/my/endpoint", async (HttpContext ctx, ...) =>
{
    var requestId = ctx.GetRequestId();
    // Use requestId in responses, logging, etc.
    return Results.Ok(new { requestId, data = "..." });
});
```

### 4. Error Handling

Throw exceptions as usual — they're automatically formatted:

```csharp
if (string.IsNullOrWhiteSpace(input))
    throw new BadHttpRequestException("input is required");
// Returns: 400 { error: "input is required", requestId: "..." }

if (!authorized)
    throw new UnauthorizedAccessException("Forbidden");
// Returns: 403 { error: "Forbidden", requestId: "..." }
```

## Contract Compliance

✅ Complies with `docs/04_CONTRACTS/ERROR_MODEL.md`:
- All responses include `X-Request-Id` header
- All errors follow `{ error, requestId }` format
- Log scopes include request context

## Documentation

- **Implementation Details:** `docs/M2.1_IMPLEMENTATION.md`
- **Developer Guide:** `docs/M2.1_DEVELOPER_GUIDE.md`
- **Commit Steps:** `docs/M2.1_COMMIT_CHECKLIST.md`
- **Tests:** `tests/M2.1_tests.ps1`

## Next Steps

The implementation is **ready to commit**. After committing M2.1:

1. **M2.2** (OpenTelemetry): Add optional traces/metrics via environment variable
2. **M2.3** (/ready improvements): Add signature validation to readiness check
3. **M1.4** (Chat store improvements): Add `user_id` support to chat sessions

## Questions?

Refer to:
- `.github/copilot-instructions.md` — project guidelines
- `docs/Plan_action_v2.7.md` — feature specifications
- `docs/04_CONTRACTS/ERROR_MODEL.md` — error contract details
- `docs/M2.1_DEVELOPER_GUIDE.md` — usage examples

---

**Status:** ✅ Implementation Complete  
**Build:** ✅ Success (0 warnings, 0 errors)  
**Tests:** ✅ Provided (PowerShell)  
**Ready to Commit:** ✅ Yes
