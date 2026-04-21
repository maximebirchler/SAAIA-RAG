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
        app.MapGet("/admin/runtime/diagnostics", (
            HttpContext ctx,
            RuntimeDiagnosticsService diagnosticsService,
            IOptions<RuntimeGovernanceOptions> options,
            IOptions<RagOptions> ragOptions)
            => DiagnosticsAsync(ctx, diagnosticsService, options, ragOptions));
        app.MapGet("/admin/runtime/retrieval-kpis", (
            HttpContext ctx,
            RuntimeRetrievalKpiService retrievalKpiService,
            IOptions<RuntimeGovernanceOptions> options)
            => RetrievalKpisAsync(ctx, retrievalKpiService, options));
        app.MapGet("/admin/runtime/capability-b-kpis", (
            HttpContext ctx,
            RuntimeCapabilityBKpiService capabilityBKpiService,
            IOptions<RuntimeGovernanceOptions> options)
            => CapabilityBKpisAsync(ctx, capabilityBKpiService, options));
        app.MapGet("/admin/runtime/operational-summary", (
            HttpContext ctx,
            RuntimeDiagnosticsService diagnosticsService,
            IOptions<RuntimeGovernanceOptions> options,
            IOptions<RagOptions> ragOptions)
            => OperationalSummaryAsync(ctx, diagnosticsService, options, ragOptions));
        app.MapGet("/admin/runtime/events", EventsAsync);
        app.MapGet("/admin/runtime/warmup-results", WarmupResultsAsync);
        app.MapGet("/admin/runtime/capabilities/capability_a.corpus_enrichment/candidates", CapabilityAEnrichmentCandidatesAsync);
        app.MapGet("/admin/runtime/capabilities/capability_a.corpus_enrichment/campaigns", CapabilityACampaignsAsync);
        app.MapGet("/admin/runtime/capabilities/capability_a.corpus_enrichment/campaigns/{campaignId:guid}", CapabilityACampaignAsync);
        app.MapGet("/admin/runtime/capabilities/capability_b.backoffice_generation/candidates", CapabilityBBackofficeCandidatesAsync);
        app.MapGet("/admin/runtime/capabilities/capability_b.backoffice_generation/campaigns", CapabilityBCampaignsAsync);
        app.MapGet("/admin/runtime/capabilities/capability_b.backoffice_generation/campaigns/{campaignId:guid}", CapabilityBCampaignAsync);
        app.MapGet("/admin/runtime/capabilities/capability_b.backoffice_generation/quality-review", CapabilityBQualityReviewAsync);
        app.MapGet("/admin/runtime/capabilities/capability_b.backoffice_generation/quality-review-summary", CapabilityBQualityReviewSummaryAsync);
        app.MapPost("/admin/runtime/capabilities/capability_b.backoffice_generation/claim", CapabilityBClaimAsync);
        app.MapPost("/admin/runtime/capabilities/capability_b.backoffice_generation/complete", CapabilityBCompleteAsync);
        app.MapPost("/admin/runtime/capabilities/capability_b.backoffice_generation/fail", CapabilityBFailAsync);
        app.MapGet("/admin/runtime/artifacts/capability-a-candidates.json", CapabilityACandidatesArtifactAsync);
        app.MapGet("/admin/runtime/artifacts/capability-a-campaigns.json", CapabilityACampaignsArtifactAsync);
        app.MapGet("/admin/runtime/artifacts/capability-a-campaigns/{campaignId:guid}.json", CapabilityACampaignArtifactAsync);
        app.MapGet("/admin/runtime/artifacts/capability-b-candidates.json", CapabilityBCandidatesArtifactAsync);
        app.MapGet("/admin/runtime/artifacts/capability-b-campaigns.json", CapabilityBCampaignsArtifactAsync);
        app.MapGet("/admin/runtime/artifacts/capability-b-campaigns/{campaignId:guid}.json", CapabilityBCampaignArtifactAsync);
        app.MapGet("/admin/runtime/artifacts/capability-b-quality-review.json", CapabilityBQualityReviewArtifactAsync);
        app.MapGet("/admin/runtime/artifacts/capability-b-quality-review-summary.json", CapabilityBQualityReviewSummaryArtifactAsync);
        app.MapGet("/admin/runtime/artifacts/diagnostics.json", (
            HttpContext ctx,
            RuntimeDiagnosticsService diagnosticsService,
            IOptions<RuntimeGovernanceOptions> options,
            IOptions<RagOptions> ragOptions)
            => DiagnosticsArtifactAsync(ctx, diagnosticsService, options, ragOptions));
        app.MapGet("/admin/runtime/artifacts/retrieval-kpis.json", (
            HttpContext ctx,
            RuntimeRetrievalKpiService retrievalKpiService,
            IOptions<RuntimeGovernanceOptions> options)
            => RetrievalKpisArtifactAsync(ctx, retrievalKpiService, options));
        app.MapGet("/admin/runtime/artifacts/capability-b-kpis.json", (
            HttpContext ctx,
            RuntimeCapabilityBKpiService capabilityBKpiService,
            IOptions<RuntimeGovernanceOptions> options)
            => CapabilityBKpisArtifactAsync(ctx, capabilityBKpiService, options));
        app.MapGet("/admin/runtime/artifacts/operational-summary.json", (
            HttpContext ctx,
            RuntimeDiagnosticsService diagnosticsService,
            IOptions<RuntimeGovernanceOptions> options,
            IOptions<RagOptions> ragOptions)
            => OperationalSummaryArtifactAsync(ctx, diagnosticsService, options, ragOptions));
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
        IOptions<ChatOptions> chatOptions,
        IHostEnvironment env)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = RuntimeGovernanceCatalogService.BuildCatalog(options.Value, ragOptions.Value, chatOptions.Value, env);
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
        var response = await RuntimeGovernanceReadService.GetCapabilitiesAsync(ds, options.Value, ragOptions.Value, env, ctx.RequestAborted);
        return Results.Ok(response);
    }

    internal static async Task<IResult> DiagnosticsAsync(
        HttpContext ctx,
        RuntimeDiagnosticsService diagnosticsService,
        IOptions<RuntimeGovernanceOptions> options,
        IOptions<RagOptions> ragOptions)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await diagnosticsService.GetDiagnosticsAsync(ctx.GetTenantId(), options.Value, ragOptions.Value, ctx.RequestAborted);
        return Results.Ok(response);
    }

    internal static Task<IResult> DiagnosticsAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RuntimeGovernanceOptions> options,
        IOptions<RagOptions> ragOptions,
        IHostEnvironment env)
        => DiagnosticsAsync(
            ctx,
            new RuntimeDiagnosticsService(ds, env, ctx.RequestServices.GetService<IHttpClientFactory>()),
            options,
            ragOptions);

    internal static Task<IResult> RetrievalKpisAsync(
        HttpContext ctx,
        RuntimeRetrievalKpiService retrievalKpiService,
        IOptions<RuntimeGovernanceOptions> options)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = retrievalKpiService.GetKpis(options.Value);
        return Task.FromResult(Results.Ok(response));
    }

    internal static Task<IResult> RetrievalKpisAsync(
        HttpContext ctx,
        IHostEnvironment env,
        IOptions<RuntimeGovernanceOptions> options)
        => RetrievalKpisAsync(ctx, new RuntimeRetrievalKpiService(env), options);

    internal static Task<IResult> CapabilityBKpisAsync(
        HttpContext ctx,
        RuntimeCapabilityBKpiService capabilityBKpiService,
        IOptions<RuntimeGovernanceOptions> options)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = capabilityBKpiService.GetKpis(options.Value);
        return Task.FromResult(Results.Ok(response));
    }

    internal static Task<IResult> CapabilityBKpisAsync(
        HttpContext ctx,
        IHostEnvironment env,
        IOptions<RuntimeGovernanceOptions> options)
        => CapabilityBKpisAsync(ctx, new RuntimeCapabilityBKpiService(env), options);

    internal static async Task<IResult> OperationalSummaryAsync(
        HttpContext ctx,
        RuntimeDiagnosticsService diagnosticsService,
        IOptions<RuntimeGovernanceOptions> options,
        IOptions<RagOptions> ragOptions)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await diagnosticsService.GetOperationalSummaryAsync(ctx.GetTenantId(), options.Value, ragOptions.Value, ctx.RequestAborted);
        return Results.Ok(response);
    }

    internal static Task<IResult> OperationalSummaryAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RuntimeGovernanceOptions> options,
        IOptions<RagOptions> ragOptions,
        IHostEnvironment env)
        => OperationalSummaryAsync(
            ctx,
            new RuntimeDiagnosticsService(ds, env, ctx.RequestServices.GetService<IHttpClientFactory>()),
            options,
            ragOptions);

    internal static async Task<IResult> EventsAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IHostEnvironment env,
        string? capabilityKey,
        int? limit)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeGovernanceReadService.GetEventsAsync(
            ds,
            env,
            capabilityKey,
            limit ?? 50,
            ctx.RequestAborted);
        return Results.Ok(response);
    }

    internal static async Task<IResult> DiagnosticsArtifactAsync(
        HttpContext ctx,
        RuntimeDiagnosticsService diagnosticsService,
        IOptions<RuntimeGovernanceOptions> options,
        IOptions<RagOptions> ragOptions)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await diagnosticsService.GetDiagnosticsArtifactAsync(ctx.GetTenantId(), options.Value, ragOptions.Value, ctx.RequestAborted);
        return Results.Ok(response);
    }

    internal static Task<IResult> DiagnosticsArtifactAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RuntimeGovernanceOptions> options,
        IOptions<RagOptions> ragOptions,
        IHostEnvironment env)
        => DiagnosticsArtifactAsync(
            ctx,
            new RuntimeDiagnosticsService(ds, env, ctx.RequestServices.GetService<IHttpClientFactory>()),
            options,
            ragOptions);

    internal static Task<IResult> RetrievalKpisArtifactAsync(
        HttpContext ctx,
        RuntimeRetrievalKpiService retrievalKpiService,
        IOptions<RuntimeGovernanceOptions> options)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = retrievalKpiService.GetKpisArtifact(options.Value);
        return Task.FromResult(Results.Ok(response));
    }

    internal static Task<IResult> RetrievalKpisArtifactAsync(
        HttpContext ctx,
        IHostEnvironment env,
        IOptions<RuntimeGovernanceOptions> options)
        => RetrievalKpisArtifactAsync(ctx, new RuntimeRetrievalKpiService(env), options);

    internal static Task<IResult> CapabilityBKpisArtifactAsync(
        HttpContext ctx,
        RuntimeCapabilityBKpiService capabilityBKpiService,
        IOptions<RuntimeGovernanceOptions> options)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = capabilityBKpiService.GetKpisArtifact(options.Value);
        return Task.FromResult(Results.Ok(response));
    }

    internal static Task<IResult> CapabilityBKpisArtifactAsync(
        HttpContext ctx,
        IHostEnvironment env,
        IOptions<RuntimeGovernanceOptions> options)
        => CapabilityBKpisArtifactAsync(ctx, new RuntimeCapabilityBKpiService(env), options);

    internal static async Task<IResult> OperationalSummaryArtifactAsync(
        HttpContext ctx,
        RuntimeDiagnosticsService diagnosticsService,
        IOptions<RuntimeGovernanceOptions> options,
        IOptions<RagOptions> ragOptions)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await diagnosticsService.GetOperationalSummaryArtifactAsync(ctx.GetTenantId(), options.Value, ragOptions.Value, ctx.RequestAborted);
        return Results.Ok(response);
    }

    internal static Task<IResult> OperationalSummaryArtifactAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RuntimeGovernanceOptions> options,
        IOptions<RagOptions> ragOptions,
        IHostEnvironment env)
        => OperationalSummaryArtifactAsync(
            ctx,
            new RuntimeDiagnosticsService(ds, env, ctx.RequestServices.GetService<IHttpClientFactory>()),
            options,
            ragOptions);

    internal static async Task<IResult> EventsArtifactAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IHostEnvironment env,
        string? capabilityKey,
        int? limit)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeGovernanceReadService.GetEventsArtifactAsync(
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
        var response = await RuntimeGovernanceCommandService.RequalifyAsync(
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
        var response = await RuntimeGovernanceCommandService.ReconcileStaleAsync(
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
        var response = await RuntimeGovernanceReadService.GetWarmupResultsAsync(
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
        CapabilityAHypotheticalQuestionService hypotheticalQuestionService,
        IHostEnvironment env,
        string? category,
        int? limit)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeCapabilityAdminReadService.GetCapabilityAEnrichmentCandidatesAsync(
            ctx.GetTenantId(),
            ds,
            options.Value,
            ragOptions.Value,
            ingestionOptions.Value,
            hypotheticalQuestionService,
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
        CapabilityAHypotheticalQuestionService hypotheticalQuestionService,
        IHostEnvironment env,
        string? category,
        int? limit)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeCapabilityAdminReadService.GetCapabilityAEnrichmentCandidatesArtifactAsync(
            ctx.GetTenantId(),
            ds,
            options.Value,
            ragOptions.Value,
            ingestionOptions.Value,
            hypotheticalQuestionService,
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
        var response = await RuntimeCapabilityAdminReadService.GetCapabilityACampaignsAsync(
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
        var response = await RuntimeCapabilityAdminReadService.GetCapabilityACampaignsArtifactAsync(
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
        var response = await RuntimeCapabilityAdminReadService.GetCapabilityACampaignAsync(
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
        var response = await RuntimeCapabilityAdminReadService.GetCapabilityACampaignArtifactAsync(
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
        var response = await RuntimeCapabilityAdminReadService.GetCapabilityBBackofficeCandidatesAsync(
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
        var response = await RuntimeCapabilityAdminReadService.GetCapabilityBBackofficeCandidatesArtifactAsync(
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
        var response = await RuntimeCapabilityAdminReadService.GetCapabilityBCampaignsAsync(
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
        var response = await RuntimeCapabilityAdminReadService.GetCapabilityBCampaignsArtifactAsync(
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
        var response = await RuntimeCapabilityAdminReadService.GetCapabilityBCampaignAsync(
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
        var response = await RuntimeCapabilityAdminReadService.GetCapabilityBCampaignArtifactAsync(
            ds,
            env,
            campaignId,
            ctx.RequestAborted);
        return response is null
            ? Results.NotFound(new { error = "capability_b_campaign_not_found", capabilityKey = "capability_b.backoffice_generation", campaignId })
            : Results.Ok(response);
    }

    internal static async Task<IResult> CapabilityBQualityReviewAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RuntimeGovernanceOptions> options,
        IHostEnvironment env,
        int? limit)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeCapabilityAdminReadService.GetCapabilityBQualityReviewAsync(
            ctx.GetTenantId(),
            ds,
            options.Value,
            env,
            limit ?? 50,
            ctx.RequestAborted);
        return Results.Ok(response);
    }

    internal static async Task<IResult> CapabilityBQualityReviewArtifactAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RuntimeGovernanceOptions> options,
        IHostEnvironment env,
        int? limit)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeCapabilityAdminReadService.GetCapabilityBQualityReviewArtifactAsync(
            ctx.GetTenantId(),
            ds,
            options.Value,
            env,
            limit ?? 50,
            ctx.RequestAborted);
        return Results.Ok(response);
    }

    internal static async Task<IResult> CapabilityBQualityReviewSummaryAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RuntimeGovernanceOptions> options,
        IHostEnvironment env)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeCapabilityAdminReadService.GetCapabilityBQualityReviewSummaryAsync(
            ctx.GetTenantId(),
            ds,
            options.Value,
            env,
            ctx.RequestAborted);
        return Results.Ok(response);
    }

    internal static async Task<IResult> CapabilityBQualityReviewSummaryArtifactAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IOptions<RuntimeGovernanceOptions> options,
        IHostEnvironment env)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeCapabilityAdminReadService.GetCapabilityBQualityReviewSummaryArtifactAsync(
            ctx.GetTenantId(),
            ds,
            options.Value,
            env,
            ctx.RequestAborted);
        return Results.Ok(response);
    }

    internal static Task<IResult> RuntimeCatalogArtifactAsync(
        HttpContext ctx,
        IOptions<RuntimeGovernanceOptions> options,
        IOptions<RagOptions> ragOptions,
        IOptions<ChatOptions> chatOptions,
        IHostEnvironment env)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = RuntimeGovernanceCatalogService.BuildRuntimeCatalogArtifact(options.Value, ragOptions.Value, chatOptions.Value, env);
        return Task.FromResult(Results.Ok(response));
    }

    internal static Task<IResult> ModelCatalogArtifactAsync(
        HttpContext ctx,
        IOptions<RuntimeGovernanceOptions> options,
        IOptions<RagOptions> ragOptions,
        IOptions<ChatOptions> chatOptions,
        IHostEnvironment env)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = RuntimeGovernanceCatalogService.BuildModelCatalogArtifact(options.Value, ragOptions.Value, chatOptions.Value, env);
        return Task.FromResult(Results.Ok(response));
    }

    internal static Task<IResult> WarmupProfilesArtifactAsync(
        HttpContext ctx,
        IOptions<RuntimeGovernanceOptions> options,
        IHostEnvironment env)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = RuntimeGovernanceCatalogService.BuildWarmupProfilesArtifact(options.Value, env);
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
        var response = await RuntimeGovernanceReadService.GetCapabilityStateArtifactAsync(
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
        var response = await RuntimeGovernanceReadService.GetWarmupResultsArtifactAsync(
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
        var result = await RuntimeGovernanceCommandService.UpdateSelectionAsync(
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
        CapabilityAHypotheticalQuestionService hypotheticalQuestionService,
        IHostEnvironment env,
        AdminRuntimeCapabilityAEnqueueRequestDto? req)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = await RuntimeCapabilityAEnrichmentCommandService.EnqueueCapabilityAEnrichmentAsync(
            ctx.GetTenantId(),
            ds,
            options.Value,
            ragOptions.Value,
            ingestionOptions.Value,
            hypotheticalQuestionService,
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
        var response = await RuntimeCapabilityBBackofficeCommandService.EnqueueCapabilityBBackofficeAsync(
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
        var response = await RuntimeCapabilityBExecutionCommandService.ClaimCapabilityBBackofficeExecutionAsync(
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
        var completion = await RuntimeCapabilityBExecutionCommandService.CompleteCapabilityBBackofficeExecutionAsync(
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
        var response = await RuntimeCapabilityBExecutionCommandService.FailCapabilityBBackofficeExecutionAsync(
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
