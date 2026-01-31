using Microsoft.Extensions.Options;
using Npgsql;

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

    public ApiKeyAuthMiddleware(RequestDelegate next) => _next = next;

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

        if (!ctx.Request.Headers.TryGetValue(opt.Value.ApiKeyHeaderName, out var keyVals))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsync("Missing API key.");
            return;
        }

        var apiKey = keyVals.ToString().Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsync("Empty API key.");
            return;
        }

        var principal = await ApiKeyAuth.ResolvePrincipalAsync(ds, apiKey, opt.Value.Pepper, ctx.RequestAborted);
        if (principal is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsync("Invalid API key.");
            return;
        }

        ctx.Items[ApiKeyAuth.TenantIdItemKey] = principal.TenantId;
        ctx.Items[ApiKeyAuth.ApiKeyIdItemKey] = principal.ApiKeyId;
        ctx.Items[ApiKeyAuth.IsAdminItemKey] = principal.IsAdmin;

        await _next(ctx);
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
