using System.Diagnostics;

namespace SAAIA.Backend.Middleware;

/// <summary>
/// Middleware qui :
/// 1. Génère ou récupère X-Request-Id (depuis le header client ou génère un nouveau)
/// 2. Ajoute RequestId à HttpContext.Items
/// 3. Configure les log scopes
/// 4. Ajoute X-Request-Id à la réponse
/// </summary>
public sealed class RequestIdMiddleware
{
    private const string RequestIdHeaderName = "X-Request-Id";
    public const string RequestIdItemKey = "RequestId";
    public const string TenantIdItemKey = "TenantId";
    public const string UserIdItemKey = "UserId";

    private readonly RequestDelegate _next;
    private readonly ILogger<RequestIdMiddleware> _logger;

    public RequestIdMiddleware(RequestDelegate next, ILogger<RequestIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        // 1. Récupère ou génère le RequestId
        var requestId = ctx.Request.Headers.TryGetValue(RequestIdHeaderName, out var headerValue)
            ? headerValue.ToString()
            : null;

        if (string.IsNullOrWhiteSpace(requestId))
        {
            requestId = Activity.Current?.Id ?? ctx.TraceIdentifier ?? Guid.NewGuid().ToString();
        }

        // 2. Ajoute à Items pour accès dans les endpoints
        ctx.Items[RequestIdItemKey] = requestId;

        // 3. Ajoute à la réponse (même si non authentifié)
        ctx.Response.OnStarting(() =>
        {
            if (!ctx.Response.Headers.ContainsKey(RequestIdHeaderName))
            {
                ctx.Response.Headers[RequestIdHeaderName] = requestId;
            }
            return Task.CompletedTask;
        });

        // 4. Configure les log scopes (tenant_id, user_id si présents après auth)
        using (_logger.BeginScope(new Dictionary<string, object>
        {
            { "request_id", requestId },
            { "path", ctx.Request.Path.Value ?? "/" },
            { "method", ctx.Request.Method }
        }))
        {
            await _next(ctx);

            // 5. Log après traitement (optional, pour observabilité)
            _logger.LogInformation(
                "Request completed: {Method} {Path} {StatusCode}",
                ctx.Request.Method,
                ctx.Request.Path.Value,
                ctx.Response.StatusCode);
        }
    }
}

public static class RequestIdExtensions
{
    public static string GetRequestId(this HttpContext ctx)
        => ctx.Items.TryGetValue(RequestIdMiddleware.RequestIdItemKey, out var val) && val is string s
            ? s
            : ctx.TraceIdentifier;
}
