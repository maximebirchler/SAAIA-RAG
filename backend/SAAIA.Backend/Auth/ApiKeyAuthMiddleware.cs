using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Middleware;
using SAAIA.Backend.Models;

namespace SAAIA.Backend.Auth;

public sealed class ApiKeyAuthOptions
{
    public string ApiKeyHeaderName { get; set; } = "X-Api-Key";

    /// <summary>
    /// Header dédié aux endpoints admin (/admin/*). Doit correspondre à une clé avec is_admin=true.
    /// </summary>
    public string AdminKeyHeaderName { get; set; } = "X-Admin-Key";

    /// <summary>
    /// Pepper optionnel : SHA256(pepper + apiKey). A stocker uniquement en local (appsettings.Local.json / env).
    /// </summary>
    public string Pepper { get; set; } = "";
}

public sealed class ApiKeyAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiKeyAuthMiddleware> _logger;

    public ApiKeyAuthMiddleware(RequestDelegate next, ILogger<ApiKeyAuthMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext ctx, NpgsqlDataSource ds, IOptions<ApiKeyAuthOptions> opt)
    {
        // Allow health + swagger without auth
        var path = ctx.Request.Path.Value ?? "";
        if (path.StartsWith("/health")
            || path.StartsWith("/ready")
            || path.StartsWith("/swagger")
            || path.StartsWith("/ui"))
        {
            await _next(ctx);
            return;
        }

        // Admin endpoints are protected by a dedicated header (architecture v2.8.1).
        // Some legacy admin-only endpoints may not be under /admin; they must opt-in via endpoint metadata.
        var endpoint = ctx.GetEndpoint();
        var isAdminRoute = path.StartsWith("/admin", StringComparison.OrdinalIgnoreCase)
            || endpoint?.Metadata.GetMetadata<RequireAdminKeyMetadata>() is not null;
        var headerName = isAdminRoute
            ? (string.IsNullOrWhiteSpace(opt.Value.AdminKeyHeaderName) ? "X-Admin-Key" : opt.Value.AdminKeyHeaderName)
            : (string.IsNullOrWhiteSpace(opt.Value.ApiKeyHeaderName) ? "X-Api-Key" : opt.Value.ApiKeyHeaderName);

        var requestId = ctx.GetRequestId();

        if (!ctx.Request.Headers.TryGetValue(headerName, out var keyVals))
        {
            _logger.LogWarning("Missing {Header} for {Path}", headerName, path);
            var msg = isAdminRoute ? "Missing admin key." : "Missing API key.";
            await WriteErrorResponseAsync(ctx, StatusCodes.Status401Unauthorized, msg, requestId);
            return;
        }

        var apiKey = keyVals.ToString().Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("Empty {Header} for {Path}", headerName, path);
            var msg = isAdminRoute ? "Empty admin key." : "Empty API key.";
            await WriteErrorResponseAsync(ctx, StatusCodes.Status401Unauthorized, msg, requestId);
            return;
        }

        var principal = await ApiKeyAuth.ResolvePrincipalAsync(ds, apiKey, opt.Value.Pepper, ctx.RequestAborted);

        if (principal is null)
        {
            _logger.LogWarning("Invalid {Header} for {Path}", headerName, path);
            var msg = isAdminRoute ? "Invalid admin key." : "Invalid API key.";
            await WriteErrorResponseAsync(ctx, StatusCodes.Status401Unauthorized, msg, requestId);
            return;
        }

        if (isAdminRoute && !principal.IsAdmin)
        {
            _logger.LogWarning("Non-admin key used on admin route {Path}", path);
            await WriteErrorResponseAsync(ctx, StatusCodes.Status403Forbidden, "Admin API key required.", requestId);
            return;
        }

        ctx.Items[ApiKeyAuth.TenantIdItemKey] = principal.TenantId;
        ctx.Items[ApiKeyAuth.ApiKeyIdItemKey] = principal.ApiKeyId;
        ctx.Items[ApiKeyAuth.IsAdminItemKey] = principal.IsAdmin;

        // M2.1: Ajouter tenant_id + api_key_id + actor_is_admin + user_id (si query userId)
        var userId = ctx.Request.Query.TryGetValue("userId", out var u) ? u.ToString() : null;

        var scope = new Dictionary<string, object>
        {
            { "tenant_id", principal.TenantId },
            { "api_key_id", principal.ApiKeyId },
            { "actor_is_admin", principal.IsAdmin }
        };

        if (!string.IsNullOrWhiteSpace(userId))
            scope["user_id"] = userId;

        using (_logger.BeginScope(scope))
        {
            await _next(ctx);
        }
    }

    private static async Task WriteErrorResponseAsync(HttpContext ctx, int statusCode, string message, string requestId)
    {
        if (ctx.Response.HasStarted)
        {
            return;
        }

        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers["X-Request-Id"] = requestId;

        var json = JsonSerializer.Serialize(new ErrorResponse(message, requestId), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await ctx.Response.WriteAsync(json);
    }
}

public static class TenantExtensions
{
    public static Guid GetTenantId(this HttpContext ctx)
        => (Guid)ctx.Items[ApiKeyAuth.TenantIdItemKey]!;

    public static Guid GetApiKeyId(this HttpContext ctx)
        => (Guid)ctx.Items[ApiKeyAuth.ApiKeyIdItemKey]!;

    public static Guid? GetApiKeyIdOrNull(this HttpContext ctx)
        => ctx.Items.TryGetValue(ApiKeyAuth.ApiKeyIdItemKey, out var v) && v is Guid g ? g : null;

    public static bool IsAdmin(this HttpContext ctx)
        => ctx.Items.TryGetValue(ApiKeyAuth.IsAdminItemKey, out var v) && v is bool b && b;
}
