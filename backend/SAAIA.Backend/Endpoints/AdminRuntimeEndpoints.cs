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
        app.MapGet("/admin/runtime/diagnostics", DiagnosticsAsync);
        app.MapGet("/admin/runtime/events", EventsAsync);
        app.MapGet("/admin/runtime/warmup-results", WarmupResultsAsync);
        app.MapGet("/admin/runtime/capabilities/capability_a.corpus_enrichment/candidates", CapabilityAEnrichmentCandidatesAsync);
        app.MapGet("/admin/runtime/capabilities/capability_a.corpus_enrichment/campaigns", CapabilityACampaignsAsync);
        app.MapGet("/admin/runtime/capabilities/capability_a.corpus_enrichment/campaigns/{campaignId:guid}", CapabilityACampaignAsync);
        app.MapGet("/admin/runtime/capabilities/capability_b.backoffice_generation/candidates", CapabilityBBackofficeCandidatesAsync);
        app.MapGet("/admin/runtime/capabilities/capability_b.backoffice_generation/campaigns", CapabilityBCampaignsAsync);
        app.MapGet("/admin/runtime/capabilities/capability_b.backoffice_generation/campaigns/{campaignId:guid}", CapabilityBCampaignAsync);
        app.MapPost("/admin/runtime/capabilities/capability_b.backoffice_generation/claim", CapabilityBClaimAsync);
        app.MapPost("/admin/runtime/capabilities/capability_b.backoffice_generation/complete", CapabilityBCompleteAsync);
        app.MapPost("/admin/runtime/capabilities/capability_b.backoffice_generation/fail", CapabilityBFailAsync);
        app.MapGet("/admin/runtime/artifacts/capability-a-candidates.json", CapabilityACandidatesArtifactAsync);
        app.MapGet("/admin/runtime/artifacts/capability-a-campaigns.json", CapabilityACampaignsArtifactAsync);
        app.MapGet("/admin/runtime/artifacts/capability-a-campaigns/{campaignId:guid}.json", CapabilityACampaignArtifactAsync);
        app.MapGet("/admin/runtime/artifacts/capability-b-candidates.json", CapabilityBCandidatesArtifactAsync);
        app.MapGet("/admin/runtime/artifacts/capability-b-campaigns.json", CapabilityBCampaignsArtifactAsync);
        app.MapGet("/admin/runtime/artifacts/capability-b-campaigns/{campaignId:guid}.json", CapabilityBCampaignArtifactAsync);
        app.MapGet("/admin/runtime/artifacts/diagnostics.json", DiagnosticsArtifactAsync);
        app.MapGet("/admin/runtime/artifacts/runtime-events.json", EventsArtifactAsync);
        app.MapGet("/admin/runtime/artifacts/runtime-catalog.json", RuntimeCatalogArtifactAsync);
        app.MapGet("/admin/runtime/artifacts/model-catalog.json", ModelCatalogArtifactAsync);
        app.MapGet("/admin/runtime/artifacts/warmup-profiles.json", WarmupProfilesArtifactAsync);
        app.MapGet("/admin/runtime/artifacts/capability-state.json", CapabilityStateArtifactAsync);
        app.MapGet("/admin/runtime/artifacts/warmup-results.json", WarmupResultsArtifactAsync);
        app.MapPost("/admin/runtime/requalify", RequalifyAsync);
        app.MapPost("/admin/runtime/reconcile-stale", ReconcileStaleAsync);
        app.MapPost("/admin/runtime/capabilities/{capabilityKey}/selection", UpdateSelectionAsync);
        app.MapPost("/admin/runtime/capabilities/capability_a.corpus_enrichment/enqueue", CapabilityAEnqueueAsync);
        app.MapPost("/admin/runtime/capabilities/capability_b.backoffice_generation/enqueue", CapabilityBEnqueueAsync);
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

    internal static async Task<IResult> DiagnosticsAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RuntimeGovernanceOptions> options,
        IOptions<RagOptions> ragOptions,
        IHostEnvironment env)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeGovernanceService.GetDiagnosticsAsync(ds, options.Value, ragOptions.Value, env, ctx.RequestAborted);
        return Results.Ok(response);
    }

    internal static async Task<IResult> EventsAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IHostEnvironment env,
        string? capabilityKey,
        int? limit)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeGovernanceService.GetEventsAsync(
            ds,
            env,
            capabilityKey,
            limit ?? 50,
            ctx.RequestAborted);
        return Results.Ok(response);
    }

    internal static async Task<IResult> DiagnosticsArtifactAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RuntimeGovernanceOptions> options,
        IOptions<RagOptions> ragOptions,
        IHostEnvironment env)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeGovernanceService.GetDiagnosticsArtifactAsync(ds, options.Value, ragOptions.Value, env, ctx.RequestAborted);
        return Results.Ok(response);
    }

    internal static async Task<IResult> EventsArtifactAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IHostEnvironment env,
        string? capabilityKey,
        int? limit)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeGovernanceService.GetEventsArtifactAsync(
            ds,
            env,
            capabilityKey,
            limit ?? 50,
            ctx.RequestAborted);
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

    internal static async Task<IResult> ReconcileStaleAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RuntimeGovernanceOptions> options,
        IOptions<RagOptions> ragOptions,
        IHostEnvironment env,
        AdminRuntimeReconcileStaleRequestDto? req)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeGovernanceService.ReconcileStaleAsync(
            ds,
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

    internal static async Task<IResult> CapabilityAEnrichmentCandidatesAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RuntimeGovernanceOptions> options,
        IOptions<RagOptions> ragOptions,
        IOptions<IngestionOptions> ingestionOptions,
        IHostEnvironment env,
        string? category,
        int? limit)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeGovernanceService.GetCapabilityAEnrichmentCandidatesAsync(
            ctx.GetTenantId(),
            ds,
            options.Value,
            ragOptions.Value,
            ingestionOptions.Value,
            env,
            category,
            limit ?? 50,
            ctx.RequestAborted);
        return response.Error is null
            ? Results.Ok(response.Payload)
            : Results.BadRequest(new { error = response.Error, capabilityKey = "capability_a.corpus_enrichment" });
    }

    internal static async Task<IResult> CapabilityACandidatesArtifactAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RuntimeGovernanceOptions> options,
        IOptions<RagOptions> ragOptions,
        IOptions<IngestionOptions> ingestionOptions,
        IHostEnvironment env,
        string? category,
        int? limit)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeGovernanceService.GetCapabilityAEnrichmentCandidatesArtifactAsync(
            ctx.GetTenantId(),
            ds,
            options.Value,
            ragOptions.Value,
            ingestionOptions.Value,
            env,
            category,
            limit ?? 50,
            ctx.RequestAborted);
        return response.Error is null
            ? Results.Ok(response.Payload)
            : Results.BadRequest(new { error = response.Error, capabilityKey = "capability_a.corpus_enrichment" });
    }

    internal static async Task<IResult> CapabilityACampaignsAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IHostEnvironment env,
        int? limit)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeGovernanceService.GetCapabilityACampaignsAsync(
            ds,
            env,
            limit ?? 20,
            ctx.RequestAborted);
        return Results.Ok(response);
    }

    internal static async Task<IResult> CapabilityACampaignsArtifactAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IHostEnvironment env,
        int? limit)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeGovernanceService.GetCapabilityACampaignsArtifactAsync(
            ds,
            env,
            limit ?? 20,
            ctx.RequestAborted);
        return Results.Ok(response);
    }

    internal static async Task<IResult> CapabilityACampaignAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IHostEnvironment env,
        Guid campaignId)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeGovernanceService.GetCapabilityACampaignAsync(
            ds,
            env,
            campaignId,
            ctx.RequestAborted);
        return response is null
            ? Results.NotFound(new { error = "capability_a_campaign_not_found", capabilityKey = "capability_a.corpus_enrichment", campaignId })
            : Results.Ok(response);
    }

    internal static async Task<IResult> CapabilityACampaignArtifactAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IHostEnvironment env,
        Guid campaignId)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeGovernanceService.GetCapabilityACampaignArtifactAsync(
            ds,
            env,
            campaignId,
            ctx.RequestAborted);
        return response is null
            ? Results.NotFound(new { error = "capability_a_campaign_not_found", capabilityKey = "capability_a.corpus_enrichment", campaignId })
            : Results.Ok(response);
    }

    internal static async Task<IResult> CapabilityBBackofficeCandidatesAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RuntimeGovernanceOptions> options,
        IOptions<RagOptions> ragOptions,
        IHostEnvironment env,
        string? category,
        int? limit)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeGovernanceService.GetCapabilityBBackofficeCandidatesAsync(
            ctx.GetTenantId(),
            ds,
            options.Value,
            ragOptions.Value,
            env,
            category,
            limit ?? 50,
            ctx.RequestAborted);
        return response.Error is null
            ? Results.Ok(response.Payload)
            : Results.BadRequest(new { error = response.Error, capabilityKey = "capability_b.backoffice_generation" });
    }

    internal static async Task<IResult> CapabilityBCandidatesArtifactAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RuntimeGovernanceOptions> options,
        IOptions<RagOptions> ragOptions,
        IHostEnvironment env,
        string? category,
        int? limit)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeGovernanceService.GetCapabilityBBackofficeCandidatesArtifactAsync(
            ctx.GetTenantId(),
            ds,
            options.Value,
            ragOptions.Value,
            env,
            category,
            limit ?? 50,
            ctx.RequestAborted);
        return response.Error is null
            ? Results.Ok(response.Payload)
            : Results.BadRequest(new { error = response.Error, capabilityKey = "capability_b.backoffice_generation" });
    }

    internal static async Task<IResult> CapabilityBCampaignsAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IHostEnvironment env,
        int? limit)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeGovernanceService.GetCapabilityBCampaignsAsync(
            ds,
            env,
            limit ?? 20,
            ctx.RequestAborted);
        return Results.Ok(response);
    }

    internal static async Task<IResult> CapabilityBCampaignsArtifactAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IHostEnvironment env,
        int? limit)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeGovernanceService.GetCapabilityBCampaignsArtifactAsync(
            ds,
            env,
            limit ?? 20,
            ctx.RequestAborted);
        return Results.Ok(response);
    }

    internal static async Task<IResult> CapabilityBCampaignAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IHostEnvironment env,
        Guid campaignId)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeGovernanceService.GetCapabilityBCampaignAsync(
            ds,
            env,
            campaignId,
            ctx.RequestAborted);
        return response is null
            ? Results.NotFound(new { error = "capability_b_campaign_not_found", capabilityKey = "capability_b.backoffice_generation", campaignId })
            : Results.Ok(response);
    }

    internal static async Task<IResult> CapabilityBCampaignArtifactAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IHostEnvironment env,
        Guid campaignId)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeGovernanceService.GetCapabilityBCampaignArtifactAsync(
            ds,
            env,
            campaignId,
            ctx.RequestAborted);
        return response is null
            ? Results.NotFound(new { error = "capability_b_campaign_not_found", capabilityKey = "capability_b.backoffice_generation", campaignId })
            : Results.Ok(response);
    }

    internal static Task<IResult> RuntimeCatalogArtifactAsync(
        HttpContext ctx,
        IOptions<RuntimeGovernanceOptions> options,
        IOptions<RagOptions> ragOptions,
        IHostEnvironment env)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = RuntimeGovernanceService.BuildRuntimeCatalogArtifact(options.Value, ragOptions.Value, env);
        return Task.FromResult(Results.Ok(response));
    }

    internal static Task<IResult> ModelCatalogArtifactAsync(
        HttpContext ctx,
        IOptions<RuntimeGovernanceOptions> options,
        IOptions<RagOptions> ragOptions,
        IHostEnvironment env)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = RuntimeGovernanceService.BuildModelCatalogArtifact(options.Value, ragOptions.Value, env);
        return Task.FromResult(Results.Ok(response));
    }

    internal static Task<IResult> WarmupProfilesArtifactAsync(
        HttpContext ctx,
        IOptions<RuntimeGovernanceOptions> options,
        IHostEnvironment env)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = RuntimeGovernanceService.BuildWarmupProfilesArtifact(options.Value, env);
        return Task.FromResult(Results.Ok(response));
    }

    internal static async Task<IResult> CapabilityStateArtifactAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RuntimeGovernanceOptions> options,
        IOptions<RagOptions> ragOptions,
        IHostEnvironment env)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeGovernanceService.GetCapabilityStateArtifactAsync(
            ds,
            options.Value,
            ragOptions.Value,
            env,
            ctx.RequestAborted);
        return Results.Ok(response);
    }

    internal static async Task<IResult> WarmupResultsArtifactAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IHostEnvironment env,
        string? capabilityKey,
        int? limit)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeGovernanceService.GetWarmupResultsArtifactAsync(
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

    internal static async Task<IResult> CapabilityAEnqueueAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RuntimeGovernanceOptions> options,
        IOptions<RagOptions> ragOptions,
        IOptions<IngestionOptions> ingestionOptions,
        IHostEnvironment env,
        AdminRuntimeCapabilityAEnqueueRequestDto? req)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeGovernanceService.EnqueueCapabilityAEnrichmentAsync(
            ctx.GetTenantId(),
            ds,
            options.Value,
            ragOptions.Value,
            ingestionOptions.Value,
            env,
            req,
            ctx.RequestAborted);
        return response.Error is null
            ? Results.Ok(response.Payload)
            : Results.BadRequest(new { error = response.Error, capabilityKey = "capability_a.corpus_enrichment" });
    }

    internal static async Task<IResult> CapabilityBEnqueueAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RuntimeGovernanceOptions> options,
        IOptions<RagOptions> ragOptions,
        IHostEnvironment env,
        AdminRuntimeCapabilityBEnqueueRequestDto? req)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeGovernanceService.EnqueueCapabilityBBackofficeAsync(
            ctx.GetTenantId(),
            ds,
            options.Value,
            ragOptions.Value,
            env,
            req,
            ctx.RequestAborted);
        return response.Error is null
            ? Results.Ok(response.Payload)
            : Results.BadRequest(new { error = response.Error, capabilityKey = "capability_b.backoffice_generation" });
    }

    internal static async Task<IResult> CapabilityBClaimAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RuntimeGovernanceOptions> options,
        IOptions<RagOptions> ragOptions,
        IHostEnvironment env,
        AdminRuntimeCapabilityBClaimRequestDto? req)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeGovernanceService.ClaimCapabilityBBackofficeExecutionAsync(
            ctx.GetTenantId(),
            ds,
            options.Value,
            ragOptions.Value,
            env,
            req,
            ctx.RequestAborted);
        return response.Error is null
            ? Results.Ok(response.Payload)
            : Results.BadRequest(new { error = response.Error, capabilityKey = "capability_b.backoffice_generation" });
    }

    internal static async Task<IResult> CapabilityBCompleteAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        AdminRuntimeCapabilityBCompleteRequestDto? req)
    {
        AdminAuth.EnsureAdmin(ctx);
        var completion = await RuntimeGovernanceService.CompleteCapabilityBBackofficeExecutionAsync(
            ctx.GetTenantId(),
            ds,
            req,
            ctx.RequestAborted);

        if (completion.Error is not null)
            return Results.BadRequest(new { error = completion.Error, capabilityKey = "capability_b.backoffice_generation" });

        var payload = completion.Payload!;
        return Results.Ok(new
        {
            stored = true,
            docId = payload.DocId,
            level = payload.Level,
            sourceHash = payload.SourceHash
        });
    }

    internal static async Task<IResult> CapabilityBFailAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IHostEnvironment env,
        AdminRuntimeCapabilityBFailRequestDto? req)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeGovernanceService.FailCapabilityBBackofficeExecutionAsync(
            ctx.GetTenantId(),
            ds,
            env,
            req,
            ctx.RequestAborted);
        return response.Error is null
            ? Results.Ok(response.Payload)
            : Results.BadRequest(new { error = response.Error, capabilityKey = "capability_b.backoffice_generation" });
    }
}
