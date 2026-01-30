using Microsoft.Extensions.Options;
using Npgsql;

namespace SAAIA.Backend.Auth;

public sealed class ApiKeyAuthOptions
{
    public string ApiKeyHeaderName { get; set; } = "X-Api-Key";
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

        var tenantId = await ApiKeyAuth.ResolveTenantIdAsync(ds, apiKey, ctx.RequestAborted);
        if (tenantId is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsync("Invalid API key.");
            return;
        }

        ctx.Items[ApiKeyAuth.TenantIdItemKey] = tenantId.Value;

        await _next(ctx);
    }
}

public static class TenantExtensions
{
    public static Guid GetTenantId(this HttpContext ctx)
        => (Guid)ctx.Items[ApiKeyAuth.TenantIdItemKey]!;
}
