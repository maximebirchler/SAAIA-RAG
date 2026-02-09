# M2.1 Implementation Summary

## Overview
Implemented M2.1 (RequestId middleware + error handling + log scopes) according to `docs/04_CONTRACTS/ERROR_MODEL.md` and `docs/Plan_action_v2.7.md`.

## What was implemented

### 1. RequestIdMiddleware
**File:** `backend/SAAIA.Backend/Middleware/RequestIdMiddleware.cs`
- Generates or retrieves `X-Request-Id` from client header or creates a new one
- Stores RequestId in `HttpContext.Items`
- Adds `X-Request-Id` to response headers
- Configures log scopes with `request_id`, `path`, `method`
- Public extension `GetRequestId()` for accessing RequestId in endpoints

### 2. ErrorHandlingMiddleware
**File:** `backend/SAAIA.Backend/Middleware/ErrorHandlingMiddleware.cs`
- Catches all exceptions and formats them as JSON: `{ error, requestId }`
- Distinguishes between exception types:
  - `BadHttpRequestException` → 400
  - `UnauthorizedAccessException` → 403
  - Generic exceptions → 500
- Always includes `X-Request-Id` header in error responses
- Hides technical details in production

### 3. ErrorResponse DTO
**File:** `backend/SAAIA.Backend/Models/ErrorResponse.cs`
- Simple record for uniform error responses
- Format: `{ error: string, requestId: string }`

### 4. Updated Components

#### WebApplicationExtensions
- Added middlewares in correct order:
  1. ErrorHandlingMiddleware (first to catch all exceptions)
  2. RequestIdMiddleware (to set request ID early)
  3. RateLimiter
  4. ApiKeyAuthMiddleware

#### ApiKeyAuthMiddleware
- Uses `ctx.GetRequestId()` for error responses
- Formats all auth errors as `{ error, requestId }`
- Adds log scopes with `tenant_id` after successful auth

#### ReadyEndpoints
- Includes `requestId` in response payload
- Retrieves RequestId via `ctx.GetRequestId()`

#### RagEndpoints
- Changed from `ctx.TraceIdentifier` to `ctx.GetRequestId()`
- RagSearchResponse now consistently includes RequestId

#### ChatStoreEndpoints
- Added import of RequestIdMiddleware (ready for future logging)

## Contract Compliance

✅ **X-Request-Id Header:** Present on all responses (success and errors)
✅ **Error Format:** `{ error, requestId }` for all HTTP errors
✅ **Log Scopes:** 
- Always: `request_id`, `path`, `method`
- After auth: `tenant_id`
✅ **RequestId Generation:** From client header or auto-generated (Activity.Current?.Id / TraceIdentifier / Guid)

## Testing

**PowerShell Tests:** `tests/M2.1_tests.ps1`
- Test 1: GET /health without X-Request-Id
- Test 2: GET /health with client-provided X-Request-Id (should echo back)
- Test 3: GET /ready (public endpoint, should have requestId in header and body)
- Test 4: POST /rag/search without API key → 401 with { error, requestId }
- Test 5: POST /rag/search with invalid API key → 401
- Test 6: POST /rag/search with empty query → 400

**Compilation:** ✅ Build succeed (0 warnings, 0 errors)

## Breaking Changes

None. All changes are additive:
- New headers are added to responses
- Error formats are more consistent but compatible
- No endpoints were modified or removed

## Files Created
- backend/SAAIA.Backend/Middleware/RequestIdMiddleware.cs
- backend/SAAIA.Backend/Middleware/ErrorHandlingMiddleware.cs
- backend/SAAIA.Backend/Models/ErrorResponse.cs
- tests/M2.1_tests.ps1

## Files Modified
- backend/SAAIA.Backend/Extensions/WebApplicationExtensions.cs
- backend/SAAIA.Backend/Auth/ApiKeyAuthMiddleware.cs
- backend/SAAIA.Backend/Endpoints/ReadyEndpoints.cs
- backend/SAAIA.Backend/Endpoints/RagEndpoints.cs
- backend/SAAIA.Backend/Endpoints/ChatStoreEndpoints.cs
- docs/Plan_action_v2.7_status.md

## Next Steps (M2.2 / M2.3)
1. M2.2: OpenTelemetry (traces + metrics, optional toggle)
2. M2.3: /ready signature validation
3. M1.4: Add user_id support to chat-store

## How to Run Tests

```powershell
# Start the backend
cd backend/SAAIA.Backend
dotnet run

# In another terminal, run tests
cd tests
.\M2.1_tests.ps1
```

Expected output:
- All 6 tests should pass (some may be skipped if API key not configured)
- All responses should include X-Request-Id header
- Error responses should follow { error, requestId } format
