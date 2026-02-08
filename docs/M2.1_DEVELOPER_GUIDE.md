# M2.1 — RequestId + Error Handling — Developer Guide

## Overview

M2.1 introduces observability infrastructure for request tracing and uniform error handling. All HTTP endpoints now:
1. Include `X-Request-Id` header in responses
2. Return errors as JSON: `{ error, requestId }`
3. Log with scopes: `request_id`, `path`, `method`, `tenant_id`

## For Endpoint Authors

### Using RequestId in your endpoint

```csharp
app.MapPost("/my/endpoint", async (HttpContext ctx, ...) =>
{
    var requestId = ctx.GetRequestId();
    var tenantId = ctx.GetTenantId();
    
    // Your logic here
    return Results.Ok(new { requestId, data = "..." });
});
```

The `GetRequestId()` extension is automatically available for `HttpContext`.

### Error Handling Best Practice

```csharp
// For validation errors:
if (string.IsNullOrWhiteSpace(input))
    throw new BadHttpRequestException("input is required");

// For authorization errors:
if (!ctx.IsAdmin())
    throw new UnauthorizedAccessException("Admin role required");

// For not found:
var entity = await GetEntity(id);
if (entity is null)
    return Results.NotFound(new { error = "Entity not found" });
```

The `ErrorHandlingMiddleware` automatically catches exceptions and formats them as `{ error, requestId }`.

## Middleware Order (Critical)

The pipeline order is:
1. **ErrorHandlingMiddleware** ← must be first to catch ALL exceptions
2. **RequestIdMiddleware** ← generates/stores requestId
3. **RateLimiter**
4. **ApiKeyAuthMiddleware** ← adds tenant_id to log scopes

If you change the order, RequestId won't be available to error handlers.

## Log Scopes

Logs automatically include scopes:

```csharp
// Always present:
// - request_id: "abc-123-..."
// - path: "/rag/search"
// - method: "POST"

// After auth:
// - tenant_id: "xyz-789-..."
```

To access scopes in your code:

```csharp
var logger = ctx.RequestServices.GetRequiredService<ILogger<MyClass>>();
logger.LogInformation("Processing request");
// Output includes [request_id=abc-123] [path=/rag/search] [method=POST] [tenant_id=xyz-789]
```

## Error Response Format

All errors follow this format:

```json
{
  "error": "Human-readable message",
  "requestId": "abc-123-xyz-789"
}
```

HTTP status codes:
- **400** : Bad request (validation)
- **401** : Unauthorized (missing/invalid API key)
- **403** : Forbidden (insufficient permissions)
- **404** : Not found (resource doesn't exist)
- **429** : Too many requests (rate limited)
- **500** : Internal server error

## Client Integration

When calling the API:

```bash
# Option 1: Server generates RequestId
curl -H "X-Api-Key: your-key" \
  -X POST http://localhost:5000/rag/search \
  -H "Content-Type: application/json" \
  -d '{"query": "test"}'

# Option 2: Client provides RequestId (for tracing)
curl -H "X-Api-Key: your-key" \
  -H "X-Request-Id: my-trace-id-12345" \
  -X POST http://localhost:5000/rag/search \
  -H "Content-Type: application/json" \
  -d '{"query": "test"}'
```

Response header will include:
```
X-Request-Id: my-trace-id-12345  (or server-generated if not provided)
```

## Testing Your Endpoint

Use the provided PowerShell tests:

```powershell
# From project root
cd tests
.\M2.1_tests.ps1
```

Or test manually:

```powershell
$headers = @{
    "X-Api-Key" = "your-test-key"
    "X-Request-Id" = [guid]::NewGuid().ToString()
}

Invoke-WebRequest -Uri "http://localhost:5000/your/endpoint" `
  -Method Post `
  -Headers $headers `
  -ContentType "application/json" `
  -Body '{"query": "test"}'
```

## FAQ

**Q: What if I don't provide X-Request-Id?**  
A: The server auto-generates one from Activity.Current?.Id, TraceIdentifier, or a new GUID.

**Q: Can I use the RequestId for distributed tracing?**  
A: Yes! It's designed for that. Include it in API responses and logs so you can correlate requests.

**Q: What if my endpoint was working with plain text errors?**  
A: ErrorHandlingMiddleware now returns JSON `{ error, requestId }`. Update clients to parse JSON.

**Q: Do health/ready endpoints need authentication?**  
A: No, they're public. But they still get RequestId.

**Q: How do I log structured data with request context?**  
A: Use the built-in scopes — they're automatically included:

```csharp
_logger.LogInformation("Processing {Count} items", count);
// Output: [request_id=...] [tenant_id=...] Processing 42 items
```

## Configuration

No configuration needed for M2.1. It's enabled by default.

Future M2.2 will add optional OpenTelemetry toggle via `appsettings.json`.
