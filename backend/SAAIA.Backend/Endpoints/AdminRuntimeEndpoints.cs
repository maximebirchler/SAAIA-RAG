using System.IO.Compression;
using System.Text.Json;
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
        app.MapGet("/admin/runtime/capability-a-kpis", (
            HttpContext ctx,
            RuntimeCapabilityAKpiService capabilityAKpiService,
            IOptions<RuntimeGovernanceOptions> options)
            => CapabilityAKpisAsync(ctx, capabilityAKpiService, options));
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
        app.MapGet("/admin/runtime/artifacts/capability-a-kpis.json", (
            HttpContext ctx,
            RuntimeCapabilityAKpiService capabilityAKpiService,
            IOptions<RuntimeGovernanceOptions> options)
            => CapabilityAKpisArtifactAsync(ctx, capabilityAKpiService, options));
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
        app.MapPost("/admin/support/bundle", SupportBundleAsync);
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

    internal static Task<IResult> CapabilityAKpisAsync(
        HttpContext ctx,
        RuntimeCapabilityAKpiService capabilityAKpiService,
        IOptions<RuntimeGovernanceOptions> options)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = capabilityAKpiService.GetKpis(options.Value);
        return Task.FromResult(Results.Ok(response));
    }

    internal static Task<IResult> CapabilityAKpisAsync(
        HttpContext ctx,
        IHostEnvironment env,
        IOptions<RuntimeGovernanceOptions> options)
        => CapabilityAKpisAsync(ctx, new RuntimeCapabilityAKpiService(env), options);

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

    internal static Task<IResult> CapabilityAKpisArtifactAsync(
        HttpContext ctx,
        RuntimeCapabilityAKpiService capabilityAKpiService,
        IOptions<RuntimeGovernanceOptions> options)
    {
        AdminAuth.EnsureAdmin(ctx);
        var response = capabilityAKpiService.GetKpisArtifact(options.Value);
        return Task.FromResult(Results.Ok(response));
    }

    internal static Task<IResult> CapabilityAKpisArtifactAsync(
        HttpContext ctx,
        IHostEnvironment env,
        IOptions<RuntimeGovernanceOptions> options)
        => CapabilityAKpisArtifactAsync(ctx, new RuntimeCapabilityAKpiService(env), options);

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

    /// <summary>
    /// POST /admin/support/bundle
    /// Packages runtime governance artifacts into a timestamped ZIP.
    /// Canonical backend artifacts are generated on demand from the current runtime state.
    /// Optional companion files still present on disk are copied when available and reported
    /// in <c>missingArtifacts</c> when absent.
    /// CDC v3.1 Â§14.2 â€” admin governance bundle (distinct from the lightweight client bundle).
    /// </summary>
    internal static async Task<IResult> SupportBundleAsync(
        HttpContext ctx,
        NpgsqlDataSource ds,
        IHostEnvironment env,
        IOptions<RuntimeGovernanceOptions> options,
        IOptions<RagOptions> ragOptions,
        IOptions<ChatOptions> chatOptions)
    {
        AdminAuth.EnsureAdmin(ctx);

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss");
        var bundleDir = Path.Combine(env.ContentRootPath, "support-bundles");
        Directory.CreateDirectory(bundleDir);
        var bundlePath = Path.Combine(bundleDir, $"admin-bundle_{stamp}_{Guid.NewGuid():N}.zip");

        var staging = Path.Combine(bundleDir, $"staging_{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);

        var artifacts = new List<string>();
        var missing = new List<string>();
        var artifactErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tenantId = ctx.GetTenantId();
        var diagnosticsService = new RuntimeDiagnosticsService(ds, env, ctx.RequestServices.GetService<IHttpClientFactory>());

        var generatedArtifacts = new Dictionary<string, Func<CancellationToken, Task<object>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["runtime-catalog.json"] = _ => Task.FromResult<object>(
                RuntimeGovernanceCatalogService.BuildRuntimeCatalogArtifact(options.Value, ragOptions.Value, chatOptions.Value, env)),
            ["model-catalog.json"] = _ => Task.FromResult<object>(
                RuntimeGovernanceCatalogService.BuildModelCatalogArtifact(options.Value, ragOptions.Value, chatOptions.Value, env)),
            ["warmup-profiles.json"] = _ => Task.FromResult<object>(
                RuntimeGovernanceCatalogService.BuildWarmupProfilesArtifact(options.Value, env)),
            ["capability-state.json"] = async ct => await RuntimeGovernanceReadService.GetCapabilityStateArtifactAsync(
                ds,
                options.Value,
                ragOptions.Value,
                env,
                ct).ConfigureAwait(false),
            ["warmup-results.json"] = async ct => await RuntimeGovernanceReadService.GetWarmupResultsArtifactAsync(
                ds,
                env,
                capabilityKey: null,
                limit: 50,
                ct).ConfigureAwait(false),
            ["runtime-events.json"] = async ct => await RuntimeGovernanceReadService.GetEventsArtifactAsync(
                ds,
                env,
                capabilityKey: null,
                limit: 50,
                ct).ConfigureAwait(false),
            ["diagnostics.json"] = async ct => await diagnosticsService.GetDiagnosticsArtifactAsync(
                tenantId,
                options.Value,
                ragOptions.Value,
                ct).ConfigureAwait(false),
            ["operational-summary.json"] = async ct => await diagnosticsService.GetOperationalSummaryArtifactAsync(
                tenantId,
                options.Value,
                ragOptions.Value,
                ct).ConfigureAwait(false),
            ["retrieval-kpis.json"] = _ => Task.FromResult<object>(
                new RuntimeRetrievalKpiService(env).GetKpisArtifact(options.Value)),
            ["capability-a-kpis.json"] = _ => Task.FromResult<object>(
                new RuntimeCapabilityAKpiService(env).GetKpisArtifact(options.Value)),
            ["capability-b-kpis.json"] = _ => Task.FromResult<object>(
                new RuntimeCapabilityBKpiService(env).GetKpisArtifact(options.Value))
        };

        var governanceFileNames = new[]
        {
            "hardware_probe.json",
            "last_known_good_profile.json",
            "rollback_log.json",
            "blacklist_applied.json",
            "acquisition_log.json",
            "runtime_compatibility_policy.json",
            "runtime_event_log.json",
            "battery_policies.json",
            "blacklist.json"
        };

        try
        {
            await File.WriteAllTextAsync(Path.Combine(staging, "README.txt"),
                $"SAAIA admin support bundle -- {stamp}\r\n" +
                "Runtime governance artifacts for support/audit. Sensitive fields are masked.\r\n" +
                "Canonical backend artifacts are generated from the current runtime state.\r\n" +
                "missingArtifacts = optional companion files absent on this deployment or artifact generation failures.\r\n",
                ctx.RequestAborted).ConfigureAwait(false);

            foreach (var artifact in generatedArtifacts)
            {
                try
                {
                    var payload = await artifact.Value(ctx.RequestAborted).ConfigureAwait(false);
                    await WriteBundleJsonAsync(staging, artifact.Key, payload, ctx.RequestAborted).ConfigureAwait(false);
                    artifacts.Add(artifact.Key);
                }
                catch (Exception ex)
                {
                    missing.Add(artifact.Key);
                    artifactErrors[artifact.Key] = ex.Message;
                }
            }

            CopyOptionalSupportBundleGovernanceFiles(staging, env, governanceFileNames, artifacts, missing);

            CopyOptionalSupportBundleRuntimeFiles(staging, env, artifacts);

            CopyOptionalSupportBundleLlmLogs(staging, env, artifacts, missing);

            var contractMissingArtifacts = EvaluateSupportBundleContractMissingArtifacts(artifacts);
            var contractComplete = contractMissingArtifacts.Count == 0;

            var configSnapshot = new
            {
                timestamp = stamp,
                cdcAlignment = "v3.1",
                governanceRoot = "contentRoot/governance",
                runtimeGovernance = new
                {
                    options.Value.WarmupPassCount,
                    options.Value.DefaultProfileKey,
                    options.Value.SelectQualifiedCoreRetrieval,
                    options.Value.AutoAuthorizeQualifiedCoreRetrieval,
                    options.Value.AutoSelectQualifiedCoreRetrieval,
                    options.Value.MinCpuCores,
                    options.Value.MinAvailableMemoryMb,
                    options.Value.Require64BitProcess,
                    options.Value.StrictProfileMinCpuCores,
                    options.Value.StrictProfileMinAvailableMemoryMb,
                    options.Value.StrictRerankProfileMinCpuCores,
                    options.Value.StrictRerankProfileMinAvailableMemoryMb,
                    options.Value.MaxQualificationAgeHours,
                    options.Value.StrictProfileMaxQualificationAgeHours,
                    options.Value.StrictRerankProfileMaxQualificationAgeHours,
                    options.Value.MaxWarmupPassDurationMs,
                    options.Value.MaxQdrantCheckMs,
                    options.Value.MaxEmbeddingsCheckMs,
                    options.Value.MaxRerankCheckMs,
                    options.Value.RetrievalP95TargetMs,
                    options.Value.RerankP95TargetMs,
                    options.Value.ZeroResultRateTargetPercent,
                    options.Value.CapabilityAOperationP95TargetMs,
                    options.Value.CapabilityBGenerationP95TargetMs,
                    options.Value.CapabilityBQualityScoreTarget
                },
                includedFiles = artifacts,
                missingArtifacts = missing,
                contractComplete,
                contractMissingArtifacts,
                artifactErrors = artifactErrors.Count == 0 ? null : artifactErrors
            };
            await File.WriteAllTextAsync(
                Path.Combine(staging, "runtime-config.json"),
                JsonSerializer.Serialize(configSnapshot, new JsonSerializerOptions { WriteIndented = true }),
                ctx.RequestAborted).ConfigureAwait(false);
            artifacts.Add("runtime-config.json");

            if (File.Exists(bundlePath)) File.Delete(bundlePath);
            ZipFile.CreateFromDirectory(staging, bundlePath, CompressionLevel.Fastest, includeBaseDirectory: false);

            return Results.Ok(new
            {
                bundlePath,
                artifacts,
                missingArtifacts = missing,
                contractComplete,
                contractMissingArtifacts,
                artifactErrors = artifactErrors.Count == 0 ? null : artifactErrors
            });
        }
        catch (Exception ex) when (ex is not UnauthorizedAccessException)
        {
            return Results.Problem(
                detail: ex.Message,
                title: "Failed to build admin support bundle",
                statusCode: 500);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    private static Task WriteBundleJsonAsync(
        string stagingRoot,
        string fileName,
        object payload,
        CancellationToken ct)
        => File.WriteAllTextAsync(
            Path.Combine(stagingRoot, fileName),
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
            ct);

    internal static void CopyOptionalSupportBundleGovernanceFiles(
        string stagingRoot,
        IHostEnvironment env,
        IEnumerable<string> fileNames,
        List<string> artifacts,
        List<string> missing,
        string? localAppDataRoot = null)
    {
        var sourceRoots = GetOptionalSupportBundleGovernanceRoots(env, localAppDataRoot);
        foreach (var fileName in fileNames)
        {
            var src = sourceRoots
                .Select(root => Path.Combine(root, fileName))
                .FirstOrDefault(File.Exists);

            if (src is not null)
            {
                File.Copy(src, Path.Combine(stagingRoot, fileName), overwrite: true);
                artifacts.Add(fileName);
            }
            else
            {
                missing.Add(fileName);
            }
        }
    }

    internal static void CopyOptionalSupportBundleRuntimeFiles(
        string stagingRoot,
        IHostEnvironment env,
        List<string> artifacts,
        string? localAppDataRoot = null)
    {
        foreach (var src in GetOptionalSupportBundleRuntimeFiles(env, localAppDataRoot))
        {
            var destination = Path.Combine(stagingRoot, src.relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(src.fullPath, destination, overwrite: true);
            artifacts.Add(src.relativePath.Replace('\\', '/'));

            var checksumPath = src.fullPath + ".sha256";
            if (File.Exists(checksumPath))
            {
                var checksumDestination = destination + ".sha256";
                File.Copy(checksumPath, checksumDestination, overwrite: true);
                artifacts.Add((src.relativePath + ".sha256").Replace('\\', '/'));
            }
        }
    }

    internal static void CopyOptionalSupportBundleLlmLogs(
        string stagingRoot,
        IHostEnvironment env,
        List<string> artifacts,
        List<string> missing,
        string? localAppDataRoot = null)
    {
        var copiedLlmLogs = 0;
        foreach (var llmLogSource in GetOptionalSupportBundleLlmLogRoots(env, localAppDataRoot))
        {
            if (!Directory.Exists(llmLogSource))
                continue;

            var outDir = Path.Combine(stagingRoot, "llm-logs");
            Directory.CreateDirectory(outDir);
            foreach (var file in Directory.EnumerateFiles(llmLogSource)
                         .OrderByDescending(File.GetLastWriteTimeUtc)
                         .Take(20))
            {
                var entryName = Path.Combine("llm-logs", Path.GetFileName(file));
                File.Copy(file, Path.Combine(stagingRoot, entryName), overwrite: true);
                artifacts.Add(entryName.Replace('\\', '/'));
                copiedLlmLogs++;
            }
        }

        if (copiedLlmLogs == 0)
            missing.Add("llm-logs/");
    }

    internal static IReadOnlyList<string> GetOptionalSupportBundleGovernanceRoots(
        IHostEnvironment env,
        string? localAppDataRoot = null)
    {
        var roots = new List<string> { Path.Combine(env.ContentRootPath, "governance") };
        var resolvedLocalAppData = string.IsNullOrWhiteSpace(localAppDataRoot)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : localAppDataRoot;
        if (!string.IsNullOrWhiteSpace(resolvedLocalAppData))
        {
            roots.Add(Path.Combine(resolvedLocalAppData, "SAAIA", "governance"));
        }

        return roots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<string> GetOptionalSupportBundleLlmLogRoots(
        IHostEnvironment env,
        string? localAppDataRoot = null)
    {
        var roots = new List<string>
        {
            Path.Combine(env.ContentRootPath, "logs", "llm"),
            Path.Combine(env.ContentRootPath, "llm-logs")
        };
        var resolvedLocalAppData = string.IsNullOrWhiteSpace(localAppDataRoot)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : localAppDataRoot;
        if (!string.IsNullOrWhiteSpace(resolvedLocalAppData))
        {
            roots.Add(Path.Combine(resolvedLocalAppData, "SAAIA", "logs", "llm"));
            roots.Add(Path.Combine(resolvedLocalAppData, "SAAIA", "logs"));
        }

        return roots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<(string relativePath, string fullPath)> GetOptionalSupportBundleRuntimeFiles(
        IHostEnvironment env,
        string? localAppDataRoot = null)
    {
        var resolvedLocalAppData = string.IsNullOrWhiteSpace(localAppDataRoot)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : localAppDataRoot;
        if (string.IsNullOrWhiteSpace(resolvedLocalAppData))
            return Array.Empty<(string relativePath, string fullPath)>();

        var candidates = new[]
        {
            ("llm/active-runtime.json", Path.Combine(resolvedLocalAppData, "SAAIA", "llm", "runtime", "active-runtime.json")),
            ("llm/runtime_event_log.json", Path.Combine(resolvedLocalAppData, "SAAIA", "governance", "runtime_event_log.json")),
            ("llm/runtime_compatibility_policy.json", Path.Combine(resolvedLocalAppData, "SAAIA", "governance", "runtime_compatibility_policy.json"))
        };

        return candidates
            .Where(item => File.Exists(item.Item2))
            .Select(item => (item.Item1, item.Item2))
            .ToArray();
    }

    internal static IReadOnlyList<string> EvaluateSupportBundleContractMissingArtifacts(
        IReadOnlyCollection<string> artifacts)
    {
        static bool HasArtifact(IReadOnlyCollection<string> items, string expected)
            => items.Contains(expected, StringComparer.OrdinalIgnoreCase);

        static bool HasRuntimeLogs(IReadOnlyCollection<string> items)
            => items.Any(item => item.StartsWith("llm-logs/", StringComparison.OrdinalIgnoreCase));

        var missing = new List<string>();

        foreach (var required in new[]
                 {
                     "hardware_probe.json",
                     "capability-state.json",
                     "warmup-results.json",
                     "last_known_good_profile.json",
                     "rollback_log.json",
                     "blacklist_applied.json",
                     "acquisition_log.json",
                     "runtime-config.json"
                 })
        {
            if (!HasArtifact(artifacts, required))
                missing.Add(required);
        }

        if (!HasRuntimeLogs(artifacts))
            missing.Add("llm-logs/");

        return missing;
    }
}
