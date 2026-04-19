using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Auth;
using SAAIA.Backend.Models;

namespace SAAIA.Backend.Endpoints;

public static class AdminRuntimeEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/admin/runtime/catalog", CatalogAsync);
        app.MapGet("/admin/runtime/capabilities", CapabilitiesAsync);
        app.MapGet("/admin/runtime/warmup-results", WarmupResultsAsync);
        app.MapPost("/admin/runtime/requalify", RequalifyAsync);
        app.MapPost("/admin/runtime/capabilities/{capabilityKey}/selection", UpdateSelectionAsync);
    }

    internal static Task<IResult> CatalogAsync(
        HttpContext ctx,
        IOptions<RuntimeGovernanceOptions> options,
        IOptions<RagOptions> ragOptions,
        IHostEnvironment env)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = RuntimeGovernanceService.BuildCatalog(options.Value, ragOptions.Value, env);
        return Task.FromResult(Results.Ok(response));
    }

    internal static async Task<IResult> CapabilitiesAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RuntimeGovernanceOptions> options,
        IOptions<RagOptions> ragOptions,
        IHostEnvironment env)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeGovernanceService.GetCapabilitiesAsync(ds, options.Value, ragOptions.Value, env, ctx.RequestAborted);
        return Results.Ok(response);
    }

    internal static async Task<IResult> RequalifyAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IHttpClientFactory httpFactory,
        IOptions<RuntimeGovernanceOptions> options,
        IOptions<RagOptions> ragOptions,
        IHostEnvironment env,
        AdminRuntimeRequalifyRequestDto? req)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeGovernanceService.RequalifyAsync(
            ds,
            httpFactory,
            options.Value,
            ragOptions.Value,
            env,
            req,
            ctx.RequestAborted);

        return Results.Ok(response);
    }

    internal static async Task<IResult> WarmupResultsAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IHostEnvironment env,
        string? capabilityKey,
        int? limit)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeGovernanceService.GetWarmupResultsAsync(
            ds,
            env,
            capabilityKey,
            limit ?? 25,
            ctx.RequestAborted);
        return Results.Ok(response);
    }

    internal static async Task<IResult> UpdateSelectionAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RuntimeGovernanceOptions> options,
        IOptions<RagOptions> ragOptions,
        string capabilityKey,
        AdminRuntimeCapabilitySelectionRequestDto? req)
    {
        AdminAuth.EnsureAdmin(ctx);
        var result = await RuntimeGovernanceService.UpdateSelectionAsync(
            ds,
            options.Value,
            ragOptions.Value,
            capabilityKey,
            req,
            ctx.RequestAborted);

        return result.Error is null
            ? Results.Ok(result.State)
            : Results.BadRequest(new { error = result.Error, capabilityKey });
    }
}
