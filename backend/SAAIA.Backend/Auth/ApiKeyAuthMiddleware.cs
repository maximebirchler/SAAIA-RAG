using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Middleware;
using SAAIA.Backend.Models;

namespace SAAIA.Backend.Auth;

public sealed class ApiKeyAuthOptions
{
    public string ApiKeyHeaderName { get; set; } = "X-Api-Key";

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

        var requestId = ctx.GetRequestId();

        if (!ctx.Request.Headers.TryGetValue(opt.Value.ApiKeyHeaderName, out var keyVals))
        {
            _logger.LogWarning("Missing API key for {Path}", path);
            await WriteErrorResponseAsync(ctx, StatusCodes.Status401Unauthorized, "Missing API key.", requestId);
            return;
        }

        var apiKey = keyVals.ToString().Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("Empty API key for {Path}", path);
            await WriteErrorResponseAsync(ctx, StatusCodes.Status401Unauthorized, "Empty API key.", requestId);
            return;
        }

        var principal = await ApiKeyAuth.ResolvePrincipalAsync(ds, apiKey, opt.Value.Pepper, ctx.RequestAborted);
        if (principal is null)
        {
            _logger.LogWarning("Invalid API key for {Path}", path);
            await WriteErrorResponseAsync(ctx, StatusCodes.Status401Unauthorized, "Invalid API key.", requestId);
            return;
        }

        ctx.Items[ApiKeyAuth.TenantIdItemKey] = principal.TenantId;
        ctx.Items[ApiKeyAuth.ApiKeyIdItemKey] = principal.ApiKeyId;
        ctx.Items[ApiKeyAuth.IsAdminItemKey] = principal.IsAdmin;

        // M2.1: Ajouter tenant_id et user_id aux log scopes
        using (_logger.BeginScope(new Dictionary<string, object>
        {
            { "tenant_id", principal.TenantId }
        }))
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

