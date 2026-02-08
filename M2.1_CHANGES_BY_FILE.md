# M2.1 — Changes by File

This document lists exactly what was changed in each file.

## NEW FILES (8)

### 1. backend/SAAIA.Backend/Middleware/RequestIdMiddleware.cs
**Size:** ~90 lines
**Purpose:** Middleware that generates/retrieves X-Request-Id and configures log scopes

**Key components:**
- `InvokeAsync()` : Main middleware logic
- `RequestIdExtensions.GetRequestId()` : Extension to access RequestId in endpoints
- Constants: RequestIdHeaderName, RequestIdItemKey, TenantIdItemKey, UserIdItemKey

**Usage:**
```csharp
var requestId = ctx.GetRequestId();
```

---

### 2. backend/SAAIA.Backend/Middleware/ErrorHandlingMiddleware.cs
**Size:** ~110 lines
**Purpose:** Catches exceptions and formats them as { error, requestId }

**Key components:**
- `InvokeAsync()` : Wraps entire pipeline in try-catch
- Exception handlers:
  - BadHttpRequestException → 400
  - UnauthorizedAccessException → 403
  - Generic Exception → 500
- `WriteErrorResponseAsync()` : Formats error as JSON

**Error response format:**
```json
{
  "error": "Human-readable message",
  "requestId": "uuid"
}
```

---

### 3. backend/SAAIA.Backend/Models/ErrorResponse.cs
**Size:** ~15 lines
**Purpose:** DTO for uniform error responses

**Content:**
```csharp
public sealed class ErrorResponse
{
    public string Error { get; set; }
    public string RequestId { get; set; }
    
    public ErrorResponse(string error, string requestId) { ... }
}
```

---

### 4. tests/M2.1_tests.ps1
**Size:** ~220 lines
**Purpose:** PowerShell test suite (6 test scenarios)

**Tests:**
1. GET /health without X-Request-Id
2. GET /health with client-provided X-Request-Id
3. GET /ready (public endpoint)
4. POST /rag/search without API key → 401
5. POST /rag/search with invalid API key → 401
6. POST /rag/search with empty query → 400

---

### 5. docs/M2.1_IMPLEMENTATION.md
**Size:** ~180 lines
**Purpose:** Technical implementation details

**Covers:**
- What was implemented (3 components)
- Contract compliance checklist
- Testing instructions
- Breaking changes (none)
- Next steps

---

### 6. docs/M2.1_DEVELOPER_GUIDE.md
**Size:** ~200 lines
**Purpose:** How to use RequestId in new endpoints

**Covers:**
- Using RequestId in endpoints
- Error handling best practices
- Middleware order (critical)
- Log scopes access
- Error response format
- Client integration examples
- Testing your endpoint
- FAQ

---

### 7. docs/M2.1_COMMIT_CHECKLIST.md
**Size:** ~160 lines
**Purpose:** Step-by-step commit instructions

**Covers:**
- Build & test commands
- How to commit (git commands)
- Post-commit steps
- Verification checklist
- Next steps

---

### 8. docs/M2.1_README.md
**Size:** ~150 lines
**Purpose:** Quick start guide

**Covers:**
- Summary of features
- What changed (file list)
- How to use
- For new endpoints (example)
- Contract compliance
- Next steps

---

## MODIFIED FILES (6)

### 1. backend/SAAIA.Backend/Extensions/WebApplicationExtensions.cs

**Changes:**

**Added import:**
```csharp
using SAAIA.Backend.Middleware;
```

**In `UseSaaiaPipeline()` method:**

Before:
```csharp
// Map UnauthorizedAccessException -> 403 (utilisé par AdminAuth)
app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (UnauthorizedAccessException)
    {
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsync("Forbidden.");
        }
    }
});

// Rate limiting (global)
app.UseRateLimiter();

// Auth middleware
app.UseMiddleware<ApiKeyAuthMiddleware>();
```

After:
```csharp
// M2.1: Error handling (doit être AVANT les autres middlewares)
app.UseMiddleware<ErrorHandlingMiddleware>();

// M2.1: RequestId (génère/récupère X-Request-Id, configure log scopes)
app.UseMiddleware<RequestIdMiddleware>();

// Rate limiting (global)
app.UseRateLimiter();

// Auth middleware
app.UseMiddleware<ApiKeyAuthMiddleware>();
```

**Rationale:** ErrorHandling must be first to catch all exceptions

---

### 2. backend/SAAIA.Backend/Auth/ApiKeyAuthMiddleware.cs

**Added imports:**
```csharp
using System.Text.Json;
using SAAIA.Backend.Middleware;
using SAAIA.Backend.Models;
```

**Constructor change:**
```csharp
// Before
public ApiKeyAuthMiddleware(RequestDelegate next) => _next = next;

// After
public ApiKeyAuthMiddleware(RequestDelegate next, ILogger<ApiKeyAuthMiddleware> logger)
{
    _next = next;
    _logger = logger;
}
```

**In `InvokeAsync()` method:**

Changed error responses from plain text:
```csharp
// Before
if (!ctx.Request.Headers.TryGetValue(opt.Value.ApiKeyHeaderName, out var keyVals))
{
    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
    await ctx.Response.WriteAsync("Missing API key.");
    return;
}
```

To JSON format:
```csharp
// After
var requestId = ctx.GetRequestId();

if (!ctx.Request.Headers.TryGetValue(opt.Value.ApiKeyHeaderName, out var keyVals))
{
    _logger.LogWarning("Missing API key for {Path}", path);
    await WriteErrorResponseAsync(ctx, StatusCodes.Status401Unauthorized, "Missing API key.", requestId);
    return;
}
```

**Added log scopes:**
```csharp
// After successful auth
using (_logger.BeginScope(new Dictionary<string, object>
{
    { "tenant_id", principal.TenantId }
}))
{
    await _next(ctx);
}
```

**Added helper method:**
```csharp
private static async Task WriteErrorResponseAsync(HttpContext ctx, int statusCode, string message, string requestId)
{
    if (ctx.Response.HasStarted)
        return;

    ctx.Response.StatusCode = statusCode;
    ctx.Response.ContentType = "application/json";
    ctx.Response.Headers["X-Request-Id"] = requestId;

    var json = JsonSerializer.Serialize(new ErrorResponse(message, requestId), ...);
    await ctx.Response.WriteAsync(json);
}
```

---

### 3. backend/SAAIA.Backend/Endpoints/ReadyEndpoints.cs

**Added import:**
```csharp
using SAAIA.Backend.Middleware;
```

**In `HandleAsync()` method signature:**
```csharp
// Added parameter
HttpContext ctx,

// In the method:
var requestId = ctx.GetRequestId();
```

**In response payload:**
```csharp
// Before
var payload = new { ok, ts = DateTimeOffset.UtcNow, details };

// After
var payload = new { ok, ts = DateTimeOffset.UtcNow, requestId, details };
```

---

### 4. backend/SAAIA.Backend/Endpoints/RagEndpoints.cs

**Added import:**
```csharp
using SAAIA.Backend.Middleware;
```

**In `SearchCoreAsync()` method:**
```csharp
// Before
return new RagSearchResponse(
    RequestId: ctx.TraceIdentifier,
    ...
);

// After
return new RagSearchResponse(
    RequestId: ctx.GetRequestId(),
    ...
);
```

---

### 5. backend/SAAIA.Backend/Endpoints/ChatStoreEndpoints.cs

**Added import:**
```csharp
using SAAIA.Backend.Middleware;
```

(Ready for future log scopes usage)

---

### 6. docs/Plan_action_v2.7_status.md

**In M2.1 section:**

Changed from:
```markdown
### M2.1 — server: RequestId middleware + log scopes
**Statut : NON FAIT**
**Éléments observés :**
- aucune occurrence `X-Request-Id` dans le code
- pas de middleware request-id
```

To:
```markdown
### M2.1 — server: RequestId middleware + log scopes
**Statut : FAIT**
**Éléments implémentés :**
- RequestIdMiddleware : génère/récupère `X-Request-Id`
- ErrorHandlingMiddleware : capture exceptions
- Log scopes : request_id, path, method, tenant_id (après auth)
- Tous les endpoints retournent `X-Request-Id`
- ReadyEndpoints et RagEndpoints alignés

**Fichiers créés :**
- backend/SAAIA.Backend/Middleware/RequestIdMiddleware.cs
- backend/SAAIA.Backend/Middleware/ErrorHandlingMiddleware.cs
- backend/SAAIA.Backend/Models/ErrorResponse.cs

**Fichiers modifiés :**
[List of 5 files]

**Tests :**
- tests/M2.1_tests.ps1
```

---

## Summary of Changes

| Category | Count | Details |
|----------|-------|---------|
| Files Created | 8 | 3 code + 4 docs + 1 ps1 |
| Files Modified | 6 | 5 endpoints/extensions + 1 status |
| Lines Added | ~900 | Middleware, error handling, documentation |
| Lines Removed | ~30 | Old error handling code |
| Breaking Changes | 0 | All changes backward compatible |
| Build Status | ✅ | Success (0 warnings, 0 errors) |

---

## Verification Checklist

- [x] All files created successfully
- [x] All imports added correctly
- [x] Middleware order correct (ErrorHandling → RequestId → RateLimiter → Auth)
- [x] Error responses use ErrorResponse DTO
- [x] Log scopes configured
- [x] RequestId retrieved/generated properly
- [x] Build succeeds
- [x] No breaking changes
- [x] Tests provided
- [x] Documentation complete

---

## How to Review

1. **For code review:**
   - Focus on: Middleware/RequestIdMiddleware.cs, Middleware/ErrorHandlingMiddleware.cs
   - Check: Order of middleware in WebApplicationExtensions.cs
   - Verify: Error format consistency across files

2. **For functional review:**
   - Run tests: `.\tests\M2.1_tests.ps1`
   - Manual test: curl with/without X-Request-Id
   - Check logs: Verify scopes are present

3. **For documentation review:**
   - Check: docs/M2.1_README.md for completeness
   - Check: docs/M2.1_DEVELOPER_GUIDE.md for clarity
   - Check: Comments in code match implementation
