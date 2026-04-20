using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using SAAIA.Backend.Models;
using SAAIA.Backend.Shared;

namespace SAAIA.Backend;

internal static class RuntimeGovernanceService
{
    private const string CdcAlignment = "v3.0";
    private const string RuntimeCatalogArtifact = "runtime_catalog.json";
    private const string ModelCatalogArtifact = "model_catalog.json";
    private const string WarmupProfilesArtifact = "warmup_profiles.json";
    private const string CapabilityStateArtifact = "capability_state.json";
    private const string WarmupResultsArtifact = "warmup_results.json";
    private const string EventsArtifact = "runtime_events";
    private const string EventsJsonArtifact = "runtime_events.json";
    private const string DiagnosticsArtifact = "runtime_diagnostics";
    private const string DiagnosticsJsonArtifact = "diagnostics.json";
    private const string CapabilityACandidatesJsonArtifact = "capability_a_candidates.json";
    private const string CapabilityACampaignsJsonArtifact = "capability_a_campaigns.json";
    private const string CapabilityACampaignDetailJsonArtifact = "capability_a_campaign_detail.json";
    private const string CapabilityBCandidatesJsonArtifact = "capability_b_candidates.json";
    private const string CapabilityBCampaignsJsonArtifact = "capability_b_campaigns.json";
    private const string CapabilityBCampaignDetailJsonArtifact = "capability_b_campaign_detail.json";
    private const string AdminRuntimeActor = "admin_runtime_endpoint";
    private const string CoreRetrievalCapabilityKey = "core.retrieval";
    private const string CapabilityACorpusEnrichmentKey = "capability_a.corpus_enrichment";
    private const string CapabilityBBackofficeGenerationKey = "capability_b.backoffice_generation";

    internal static AdminRuntimeCatalogResponseDto BuildCatalog(
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHostEnvironment env)
    {
        var profiles = RuntimeCatalogBuilder.BuildWarmupProfiles(options);
        return new(
            CdcAlignment,
            env.EnvironmentName,
            RuntimeCatalogBuilder.BuildRuntimes(rag, profiles, CapabilityACorpusEnrichmentKey, CapabilityBBackofficeGenerationKey),
            profiles,
            RuntimeCapabilityRegistry.GetCapabilityCatalog());
    }

    internal static AdminRuntimeRuntimeCatalogArtifactDto BuildRuntimeCatalogArtifact(
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHostEnvironment env)
        => ExecuteArtifactRead(
            RuntimeCatalogArtifact,
            () =>
            {
                var profiles = RuntimeCatalogBuilder.BuildWarmupProfiles(options);
                return new AdminRuntimeRuntimeCatalogArtifactDto(
                    RuntimeCatalogArtifact,
                    CdcAlignment,
                    env.EnvironmentName,
                    DateTimeOffset.UtcNow,
                    RuntimeCatalogBuilder.BuildRuntimes(rag, profiles, CapabilityACorpusEnrichmentKey, CapabilityBBackofficeGenerationKey),
                    profiles,
                    RuntimeCapabilityRegistry.GetCapabilityCatalog());
            });

    internal static AdminRuntimeModelCatalogArtifactDto BuildModelCatalogArtifact(
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHostEnvironment env)
        => ExecuteArtifactRead(
            ModelCatalogArtifact,
            () =>
            {
                var profiles = RuntimeCatalogBuilder.BuildWarmupProfiles(options);
                var runtimes = RuntimeCatalogBuilder.BuildRuntimes(rag, profiles, CapabilityACorpusEnrichmentKey, CapabilityBBackofficeGenerationKey);
                return new AdminRuntimeModelCatalogArtifactDto(
                    ModelCatalogArtifact,
                    CdcAlignment,
                    env.EnvironmentName,
                    DateTimeOffset.UtcNow,
                    RuntimeCatalogBuilder.BuildModelCatalogEntries(runtimes));
            });

    internal static AdminRuntimeWarmupProfilesArtifactDto BuildWarmupProfilesArtifact(
        RuntimeGovernanceOptions options,
        IHostEnvironment env)
        => ExecuteArtifactRead(
            WarmupProfilesArtifact,
            () => new AdminRuntimeWarmupProfilesArtifactDto(
                WarmupProfilesArtifact,
                CdcAlignment,
                env.EnvironmentName,
                DateTimeOffset.UtcNow,
                RuntimeCatalogBuilder.BuildWarmupProfiles(options)));

    internal static async Task<AdminRuntimeCapabilitiesResponseDto> GetCapabilitiesAsync(
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHostEnvironment env,
        CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        var persisted = await RuntimeCapabilityStateStore.LoadPersistedStatesAsync(conn, ct);

        var items = RuntimeCapabilityRegistry.Definitions
            .Select(def => RuntimeCapabilityStateResolver.ResolveState(
                def,
                persisted.TryGetValue(def.Key, out var row) ? row : null,
                options,
                rag))
            .ToArray();

        var warmupResults = await RuntimeCapabilityHistoryStore.LoadWarmupResultsAsync(conn, capabilityKey: null, limit: 25, ct);

        return new AdminRuntimeCapabilitiesResponseDto(
            CdcAlignment,
            env.EnvironmentName,
            items,
            warmupResults);
    }

    internal static async Task<AdminRuntimeDiagnosticsResponseDto> GetDiagnosticsAsync(
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHostEnvironment env,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity(DiagnosticsArtifact);
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var persisted = await RuntimeCapabilityStateStore.LoadPersistedStatesAsync(conn, ct);
            var capabilityBCandidates = await RuntimeCapabilityBBackofficeStore.LoadCandidatesForDiagnosticsAsync(
                conn,
                options,
                ct);
            var capabilityBOperationalSummary = await RuntimeCapabilityDiagnosticsBuilder.LoadCapabilityBOperationalSummaryAsync(
                conn,
                CapabilityBBackofficeGenerationKey,
                capabilityBCandidates,
                ct);
            var items = RuntimeCapabilityRegistry.Definitions
                .Select(def => RuntimeCapabilityStateResolver.ResolveState(
                    def,
                    persisted.TryGetValue(def.Key, out var row) ? row : null,
                    options,
                    rag))
                .Select(state => RuntimeCapabilityDiagnosticsBuilder.BuildCapabilityDiagnostic(
                    state,
                    CapabilityACorpusEnrichmentKey,
                    CapabilityBBackofficeGenerationKey,
                    string.Equals(state.Key, CapabilityBBackofficeGenerationKey, StringComparison.Ordinal)
                        ? capabilityBOperationalSummary
                        : null))
                .ToArray();
            var summary = RuntimeCapabilityDiagnosticsBuilder.BuildDiagnosticsSummary(items);

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, DiagnosticsArtifact, success: true, durationMs: sw.ElapsedMilliseconds);
            return new AdminRuntimeDiagnosticsResponseDto(
                CdcAlignment,
                env.EnvironmentName,
                DateTimeOffset.UtcNow,
                summary,
                items);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, DiagnosticsArtifact, success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal static async Task<AdminRuntimeDiagnosticsArtifactDto> GetDiagnosticsArtifactAsync(
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHostEnvironment env,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity(DiagnosticsJsonArtifact);
        var sw = Stopwatch.StartNew();
        try
        {
            var diagnostics = await GetDiagnosticsAsync(ds, options, rag, env, ct);
            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, DiagnosticsJsonArtifact, success: true, durationMs: sw.ElapsedMilliseconds);
            return new AdminRuntimeDiagnosticsArtifactDto(
                DiagnosticsJsonArtifact,
                diagnostics.CdcAlignment,
                diagnostics.Environment,
                diagnostics.GeneratedAt,
                diagnostics.Summary,
                diagnostics.Items);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, DiagnosticsJsonArtifact, success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal static async Task<AdminRuntimeCapabilityStateArtifactDto> GetCapabilityStateArtifactAsync(
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHostEnvironment env,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity(CapabilityStateArtifact);
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var persisted = await RuntimeCapabilityStateStore.LoadPersistedStatesAsync(conn, ct);
            var items = RuntimeCapabilityRegistry.Definitions
                .Select(def => RuntimeCapabilityStateResolver.ResolveState(
                    def,
                    persisted.TryGetValue(def.Key, out var row) ? row : null,
                    options,
                    rag))
                .ToArray();

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityStateArtifact, success: true, durationMs: sw.ElapsedMilliseconds);
            return new AdminRuntimeCapabilityStateArtifactDto(
                CapabilityStateArtifact,
                CdcAlignment,
                env.EnvironmentName,
                DateTimeOffset.UtcNow,
                items);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityStateArtifact, success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal static async Task<AdminRuntimeWarmupResultsResponseDto> GetWarmupResultsAsync(
        NpgsqlDataSource ds,
        IHostEnvironment env,
        string? capabilityKey,
        int limit,
        CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        var items = await RuntimeCapabilityHistoryStore.LoadWarmupResultsAsync(conn, capabilityKey, Math.Clamp(limit, 1, 100), ct);
        return new AdminRuntimeWarmupResultsResponseDto(CdcAlignment, env.EnvironmentName, items);
    }

    internal static async Task<AdminRuntimeWarmupResultsArtifactDto> GetWarmupResultsArtifactAsync(
        NpgsqlDataSource ds,
        IHostEnvironment env,
        string? capabilityKey,
        int limit,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity(WarmupResultsArtifact);
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var items = await RuntimeCapabilityHistoryStore.LoadWarmupResultsAsync(conn, capabilityKey, Math.Clamp(limit, 1, 100), ct);
            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, WarmupResultsArtifact, success: true, durationMs: sw.ElapsedMilliseconds);
            return new AdminRuntimeWarmupResultsArtifactDto(
                WarmupResultsArtifact,
                CdcAlignment,
                env.EnvironmentName,
                DateTimeOffset.UtcNow,
                items);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, WarmupResultsArtifact, success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal static async Task<AdminRuntimeEventsResponseDto> GetEventsAsync(
        NpgsqlDataSource ds,
        IHostEnvironment env,
        string? capabilityKey,
        int limit,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity(EventsArtifact);
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var items = await RuntimeCapabilityHistoryStore.LoadCapabilityEventsAsync(conn, capabilityKey, Math.Clamp(limit, 1, 200), ct);
            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, EventsArtifact, success: true, durationMs: sw.ElapsedMilliseconds);
            return new AdminRuntimeEventsResponseDto(CdcAlignment, env.EnvironmentName, items);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, EventsArtifact, success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal static async Task<AdminRuntimeEventsArtifactDto> GetEventsArtifactAsync(
        NpgsqlDataSource ds,
        IHostEnvironment env,
        string? capabilityKey,
        int limit,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity(EventsJsonArtifact);
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var items = await RuntimeCapabilityHistoryStore.LoadCapabilityEventsAsync(conn, capabilityKey, Math.Clamp(limit, 1, 200), ct);
            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, EventsJsonArtifact, success: true, durationMs: sw.ElapsedMilliseconds);
            return new AdminRuntimeEventsArtifactDto(
                EventsJsonArtifact,
                CdcAlignment,
                env.EnvironmentName,
                DateTimeOffset.UtcNow,
                items);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, EventsJsonArtifact, success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal static async Task<RuntimeOperationResult<AdminRuntimeCapabilityAEnrichmentCandidatesResponseDto>> GetCapabilityAEnrichmentCandidatesAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IngestionOptions ingest,
        IHostEnvironment env,
        string? category,
        int limit,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartCapabilityAOperationActivity("capability_a_candidates");
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var gate = await EnsureCapabilityReadyAsync(conn, CapabilityACorpusEnrichmentKey, options, rag, ct);
            if (gate.Error is not null)
            {
                sw.Stop();
                RuntimeGovernanceTelemetry.CompleteCapabilityAOperation(
                    activity,
                    "capability_a_candidates",
                    success: false,
                    durationMs: sw.ElapsedMilliseconds,
                    candidateCount: 0,
                    errorReason: gate.Error);
                return new RuntimeOperationResult<AdminRuntimeCapabilityAEnrichmentCandidatesResponseDto>(null, gate.Error);
            }

            var candidates = await RuntimeCapabilityAEnrichmentStore.LoadCandidatesAsync(
                conn,
                tenantId,
                ingest,
                category,
                limit,
                reasonFilters: null,
                ct);

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteCapabilityAOperation(
                activity,
                "capability_a_candidates",
                success: true,
                durationMs: sw.ElapsedMilliseconds,
                candidateCount: candidates.Length);

            return new RuntimeOperationResult<AdminRuntimeCapabilityAEnrichmentCandidatesResponseDto>(
                new AdminRuntimeCapabilityAEnrichmentCandidatesResponseDto(
                    CdcAlignment,
                    env.EnvironmentName,
                    CapabilityACorpusEnrichmentKey,
                    gate.State!.ProfileKey,
                    candidates.Length,
                    candidates),
                null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteCapabilityAOperation(
                activity,
                "capability_a_candidates",
                success: false,
                durationMs: sw.ElapsedMilliseconds,
                candidateCount: 0,
                errorReason: ex.Message);
            throw;
        }
    }

    internal static async Task<RuntimeOperationResult<AdminRuntimeCapabilityAEnrichmentCandidatesArtifactDto>> GetCapabilityAEnrichmentCandidatesArtifactAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IngestionOptions ingest,
        IHostEnvironment env,
        string? category,
        int limit,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity(CapabilityACandidatesJsonArtifact);
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await GetCapabilityAEnrichmentCandidatesAsync(
                tenantId,
                ds,
                options,
                rag,
                ingest,
                env,
                category,
                limit,
                ct);

            if (result.Error is not null || result.Payload is null)
            {
                sw.Stop();
                RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityACandidatesJsonArtifact, success: false, durationMs: sw.ElapsedMilliseconds);
                return new RuntimeOperationResult<AdminRuntimeCapabilityAEnrichmentCandidatesArtifactDto>(null, result.Error ?? "capability_a_candidates_unavailable");
            }

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityACandidatesJsonArtifact, success: true, durationMs: sw.ElapsedMilliseconds);
            return new RuntimeOperationResult<AdminRuntimeCapabilityAEnrichmentCandidatesArtifactDto>(
                new AdminRuntimeCapabilityAEnrichmentCandidatesArtifactDto(
                    CapabilityACandidatesJsonArtifact,
                    result.Payload.CdcAlignment,
                    result.Payload.Environment,
                    DateTimeOffset.UtcNow,
                    result.Payload.CapabilityKey,
                    result.Payload.ProfileKey,
                    result.Payload.TotalCandidates,
                    result.Payload.Items),
                null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityACandidatesJsonArtifact, success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal static async Task<AdminRuntimeCapabilityACampaignsResponseDto> GetCapabilityACampaignsAsync(
        NpgsqlDataSource ds,
        IHostEnvironment env,
        int limit,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity("capability_a_campaigns");
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var items = await RuntimeCapabilityCampaignStore.LoadCapabilityACampaignsAsync(
                conn,
                CapabilityACorpusEnrichmentKey,
                Math.Clamp(limit, 1, 100),
                ct);
            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, "capability_a_campaigns", success: true, durationMs: sw.ElapsedMilliseconds);
            return new AdminRuntimeCapabilityACampaignsResponseDto(
                CdcAlignment,
                env.EnvironmentName,
                CapabilityACorpusEnrichmentKey,
                items);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, "capability_a_campaigns", success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal static async Task<AdminRuntimeCapabilityACampaignsArtifactDto> GetCapabilityACampaignsArtifactAsync(
        NpgsqlDataSource ds,
        IHostEnvironment env,
        int limit,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity(CapabilityACampaignsJsonArtifact);
        var sw = Stopwatch.StartNew();
        try
        {
            var campaigns = await GetCapabilityACampaignsAsync(ds, env, limit, ct);
            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityACampaignsJsonArtifact, success: true, durationMs: sw.ElapsedMilliseconds);
            return new AdminRuntimeCapabilityACampaignsArtifactDto(
                CapabilityACampaignsJsonArtifact,
                campaigns.CdcAlignment,
                campaigns.Environment,
                DateTimeOffset.UtcNow,
                campaigns.CapabilityKey,
                campaigns.Items);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityACampaignsJsonArtifact, success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal static async Task<AdminRuntimeCapabilityACampaignDetailResponseDto?> GetCapabilityACampaignAsync(
        NpgsqlDataSource ds,
        IHostEnvironment env,
        Guid campaignId,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity("capability_a_campaign_detail");
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var item = await RuntimeCapabilityCampaignStore.LoadCapabilityACampaignDetailAsync(
                conn,
                CapabilityACorpusEnrichmentKey,
                campaignId,
                ct);
            if (item is null)
            {
                sw.Stop();
                RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, "capability_a_campaign_detail", success: false, durationMs: sw.ElapsedMilliseconds);
                return null;
            }

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, "capability_a_campaign_detail", success: true, durationMs: sw.ElapsedMilliseconds);
            return new AdminRuntimeCapabilityACampaignDetailResponseDto(
                CdcAlignment,
                env.EnvironmentName,
                CapabilityACorpusEnrichmentKey,
                item);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, "capability_a_campaign_detail", success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal static async Task<AdminRuntimeCapabilityACampaignDetailArtifactDto?> GetCapabilityACampaignArtifactAsync(
        NpgsqlDataSource ds,
        IHostEnvironment env,
        Guid campaignId,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity(CapabilityACampaignDetailJsonArtifact);
        var sw = Stopwatch.StartNew();
        try
        {
            var campaign = await GetCapabilityACampaignAsync(ds, env, campaignId, ct);
            if (campaign is null)
            {
                sw.Stop();
                RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityACampaignDetailJsonArtifact, success: false, durationMs: sw.ElapsedMilliseconds);
                return null;
            }

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityACampaignDetailJsonArtifact, success: true, durationMs: sw.ElapsedMilliseconds);
            return new AdminRuntimeCapabilityACampaignDetailArtifactDto(
                CapabilityACampaignDetailJsonArtifact,
                campaign.CdcAlignment,
                campaign.Environment,
                DateTimeOffset.UtcNow,
                campaign.CapabilityKey,
                campaign.Item);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityACampaignDetailJsonArtifact, success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal static async Task<RuntimeOperationResult<AdminRuntimeCapabilityBBackofficeCandidatesResponseDto>> GetCapabilityBBackofficeCandidatesAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHostEnvironment env,
        string? category,
        int limit,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartCapabilityBOperationActivity("capability_b_candidates");
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var gate = await EnsureCapabilityReadyAsync(conn, CapabilityBBackofficeGenerationKey, options, rag, ct);
            if (gate.Error is not null)
            {
                sw.Stop();
                RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                    activity,
                    "capability_b_candidates",
                    success: false,
                    durationMs: sw.ElapsedMilliseconds,
                    candidateCount: 0,
                    errorReason: gate.Error);
                return new RuntimeOperationResult<AdminRuntimeCapabilityBBackofficeCandidatesResponseDto>(null, gate.Error);
            }

            var candidates = await RuntimeCapabilityBBackofficeStore.LoadCandidatesAsync(
                conn,
                tenantId,
                category,
                limit,
                options,
                ct);

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                activity,
                "capability_b_candidates",
                success: true,
                durationMs: sw.ElapsedMilliseconds,
                candidateCount: candidates.Length);

            return new RuntimeOperationResult<AdminRuntimeCapabilityBBackofficeCandidatesResponseDto>(
                new AdminRuntimeCapabilityBBackofficeCandidatesResponseDto(
                    CdcAlignment,
                    env.EnvironmentName,
                    CapabilityBBackofficeGenerationKey,
                    gate.State!.ProfileKey,
                    candidates.Length,
                    candidates),
                null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                activity,
                "capability_b_candidates",
                success: false,
                durationMs: sw.ElapsedMilliseconds,
                candidateCount: 0,
                errorReason: ex.Message);
            throw;
        }
    }

    internal static async Task<RuntimeOperationResult<AdminRuntimeCapabilityBBackofficeCandidatesArtifactDto>> GetCapabilityBBackofficeCandidatesArtifactAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHostEnvironment env,
        string? category,
        int limit,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity(CapabilityBCandidatesJsonArtifact);
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await GetCapabilityBBackofficeCandidatesAsync(
                tenantId,
                ds,
                options,
                rag,
                env,
                category,
                limit,
                ct);

            if (result.Error is not null || result.Payload is null)
            {
                sw.Stop();
                RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityBCandidatesJsonArtifact, success: false, durationMs: sw.ElapsedMilliseconds);
                return new RuntimeOperationResult<AdminRuntimeCapabilityBBackofficeCandidatesArtifactDto>(null, result.Error ?? "capability_b_candidates_unavailable");
            }

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityBCandidatesJsonArtifact, success: true, durationMs: sw.ElapsedMilliseconds);
            return new RuntimeOperationResult<AdminRuntimeCapabilityBBackofficeCandidatesArtifactDto>(
                new AdminRuntimeCapabilityBBackofficeCandidatesArtifactDto(
                    CapabilityBCandidatesJsonArtifact,
                    result.Payload.CdcAlignment,
                    result.Payload.Environment,
                    DateTimeOffset.UtcNow,
                    result.Payload.CapabilityKey,
                    result.Payload.ProfileKey,
                    result.Payload.TotalCandidates,
                    result.Payload.Items),
                null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityBCandidatesJsonArtifact, success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal static async Task<AdminRuntimeCapabilityBCampaignsResponseDto> GetCapabilityBCampaignsAsync(
        NpgsqlDataSource ds,
        IHostEnvironment env,
        int limit,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity("capability_b_campaigns");
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var items = await RuntimeCapabilityCampaignStore.LoadCapabilityBCampaignsAsync(
                conn,
                CapabilityBBackofficeGenerationKey,
                Math.Clamp(limit, 1, 100),
                ct);
            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, "capability_b_campaigns", success: true, durationMs: sw.ElapsedMilliseconds);
            return new AdminRuntimeCapabilityBCampaignsResponseDto(
                CdcAlignment,
                env.EnvironmentName,
                CapabilityBBackofficeGenerationKey,
                items);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, "capability_b_campaigns", success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal static async Task<AdminRuntimeCapabilityBCampaignsArtifactDto> GetCapabilityBCampaignsArtifactAsync(
        NpgsqlDataSource ds,
        IHostEnvironment env,
        int limit,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity(CapabilityBCampaignsJsonArtifact);
        var sw = Stopwatch.StartNew();
        try
        {
            var campaigns = await GetCapabilityBCampaignsAsync(ds, env, limit, ct);
            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityBCampaignsJsonArtifact, success: true, durationMs: sw.ElapsedMilliseconds);
            return new AdminRuntimeCapabilityBCampaignsArtifactDto(
                CapabilityBCampaignsJsonArtifact,
                campaigns.CdcAlignment,
                campaigns.Environment,
                DateTimeOffset.UtcNow,
                campaigns.CapabilityKey,
                campaigns.Items);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityBCampaignsJsonArtifact, success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal static async Task<AdminRuntimeCapabilityBCampaignDetailResponseDto?> GetCapabilityBCampaignAsync(
        NpgsqlDataSource ds,
        IHostEnvironment env,
        Guid campaignId,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity("capability_b_campaign_detail");
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var item = await RuntimeCapabilityCampaignStore.LoadCapabilityBCampaignDetailAsync(
                conn,
                CapabilityBBackofficeGenerationKey,
                campaignId,
                ct);
            if (item is null)
            {
                sw.Stop();
                RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, "capability_b_campaign_detail", success: false, durationMs: sw.ElapsedMilliseconds);
                return null;
            }

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, "capability_b_campaign_detail", success: true, durationMs: sw.ElapsedMilliseconds);
            return new AdminRuntimeCapabilityBCampaignDetailResponseDto(
                CdcAlignment,
                env.EnvironmentName,
                CapabilityBBackofficeGenerationKey,
                item);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, "capability_b_campaign_detail", success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal static async Task<AdminRuntimeCapabilityBCampaignDetailArtifactDto?> GetCapabilityBCampaignArtifactAsync(
        NpgsqlDataSource ds,
        IHostEnvironment env,
        Guid campaignId,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity(CapabilityBCampaignDetailJsonArtifact);
        var sw = Stopwatch.StartNew();
        try
        {
            var campaign = await GetCapabilityBCampaignAsync(ds, env, campaignId, ct);
            if (campaign is null)
            {
                sw.Stop();
                RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityBCampaignDetailJsonArtifact, success: false, durationMs: sw.ElapsedMilliseconds);
                return null;
            }

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityBCampaignDetailJsonArtifact, success: true, durationMs: sw.ElapsedMilliseconds);
            return new AdminRuntimeCapabilityBCampaignDetailArtifactDto(
                CapabilityBCampaignDetailJsonArtifact,
                campaign.CdcAlignment,
                campaign.Environment,
                DateTimeOffset.UtcNow,
                campaign.CapabilityKey,
                campaign.Item);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, CapabilityBCampaignDetailJsonArtifact, success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal static async Task<RuntimeOperationResult<AdminRuntimeCapabilityBEnqueueResponseDto>> EnqueueCapabilityBBackofficeAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHostEnvironment env,
        AdminRuntimeCapabilityBEnqueueRequestDto? req,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartCapabilityBOperationActivity("capability_b_enqueue");
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var gate = await EnsureCapabilityReadyAsync(conn, CapabilityBBackofficeGenerationKey, options, rag, ct);
            if (gate.Error is not null)
            {
                sw.Stop();
                RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                    activity,
                    "capability_b_enqueue",
                    success: false,
                    durationMs: sw.ElapsedMilliseconds,
                    queuedCount: 0,
                    skippedCount: 0,
                    errorReason: gate.Error);
                return new RuntimeOperationResult<AdminRuntimeCapabilityBEnqueueResponseDto>(null, gate.Error);
            }

            var selectedDocIds = (req?.DocIds ?? Array.Empty<Guid>())
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToHashSet();
            var selectedDocPaths = (req?.DocPaths ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim().Replace('\\', '/'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var candidates = await RuntimeCapabilityBBackofficeStore.LoadCandidatesAsync(
                conn,
                tenantId,
                req?.Category,
                Math.Clamp(req?.MaxCandidates ?? 200, 1, 500),
                options,
                ct);

            if (selectedDocIds.Count > 0 || selectedDocPaths.Count > 0)
            {
                candidates = candidates
                    .Where(candidate => selectedDocIds.Contains(candidate.DocId) || selectedDocPaths.Contains(candidate.DocPath))
                    .ToArray();
            }

            var items = new List<AdminRuntimeCapabilityBEnqueueItemDto>(candidates.Length);
            var reasonCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var campaignId = Guid.NewGuid();
            var plannedCount = 0;

            foreach (var candidate in candidates)
            {
                if (candidate.HasActiveJob)
                {
                    items.Add(new AdminRuntimeCapabilityBEnqueueItemDto(candidate.DocId, candidate.DocPath, Queued: false, Reason: "active_summary_job_exists"));
                    IncrementCapabilityAReasonCounts(reasonCounts, candidate.Reasons);
                    IncrementCapabilityAReasonCounts(reasonCounts, ["blocked:active_summary_job_exists"]);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(candidate.PolicyBlockReason) && req?.Force != true)
                {
                    items.Add(new AdminRuntimeCapabilityBEnqueueItemDto(candidate.DocId, candidate.DocPath, Queued: false, Reason: candidate.PolicyBlockReason));
                    IncrementCapabilityAReasonCounts(reasonCounts, candidate.Reasons);
                    IncrementCapabilityAReasonCounts(reasonCounts, [$"blocked:{candidate.PolicyBlockReason}"]);
                    continue;
                }

                plannedCount++;
                IncrementCapabilityAReasonCounts(reasonCounts, candidate.Reasons);

                if (req?.DryRun == true)
                {
                    items.Add(new AdminRuntimeCapabilityBEnqueueItemDto(candidate.DocId, candidate.DocPath, Queued: false, Reason: "dry_run_preview"));
                    continue;
                }

                var jobId = await InsertCapabilityBAdminJobAsync(conn, tenantId, candidate.DocId, candidate.DocPath, req?.Force == true, campaignId, gate.State!.ProfileKey, ct);
                items.Add(new AdminRuntimeCapabilityBEnqueueItemDto(candidate.DocId, candidate.DocPath, Queued: true, JobId: jobId));

                await RuntimeCapabilityPersistenceStore.InsertCapabilityEventAsync(
                    conn,
                    CreateCapabilityEvent(
                        capabilityKey: CapabilityBBackofficeGenerationKey,
                        profileKey: gate.State!.ProfileKey,
                        eventType: "capability_b_enqueued",
                        reason: string.Join(",", candidate.Reasons),
                        details: new Dictionary<string, object?>
                        {
                            ["docId"] = candidate.DocId,
                            ["docPath"] = candidate.DocPath,
                            ["category"] = candidate.Category,
                            ["summaryState"] = candidate.SummaryState,
                            ["jobId"] = jobId,
                            ["reasons"] = candidate.Reasons.ToArray(),
                            ["policyBlocked"] = candidate.PolicyBlocked,
                            ["policyBlockReason"] = candidate.PolicyBlockReason,
                            ["campaignId"] = campaignId
                        }),
                    ct);
            }

            var queuedCount = items.Count(item => item.Queued);
            var skippedCount = items.Count(item => !item.Queued);

            await RuntimeCapabilityPersistenceStore.InsertCapabilityEventAsync(
                conn,
                CreateCapabilityEvent(
                    capabilityKey: CapabilityBBackofficeGenerationKey,
                    profileKey: gate.State!.ProfileKey,
                    eventType: req?.DryRun == true ? "capability_b_campaign_dry_run" : "capability_b_campaign_executed",
                    reason: req?.DryRun == true ? "dry_run_preview" : "campaign_completed",
                    details: new Dictionary<string, object?>
                    {
                        ["campaignId"] = campaignId,
                        ["candidateCount"] = candidates.Length,
                        ["plannedCount"] = plannedCount,
                        ["queuedCount"] = queuedCount,
                        ["skippedCount"] = skippedCount,
                        ["dryRun"] = req?.DryRun == true,
                        ["force"] = req?.Force == true,
                        ["reasonCounts"] = reasonCounts,
                        ["items"] = items.Select(static item => new Dictionary<string, object?>
                        {
                            ["docId"] = item.DocId,
                            ["docPath"] = item.DocPath,
                            ["queued"] = item.Queued,
                            ["jobId"] = item.JobId,
                            ["reason"] = item.Reason
                        }).ToArray(),
                        ["docIds"] = selectedDocIds.ToArray(),
                        ["docPaths"] = selectedDocPaths.ToArray()
                    }),
                ct);

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                activity,
                "capability_b_enqueue",
                success: true,
                durationMs: sw.ElapsedMilliseconds,
                candidateCount: candidates.Length,
                plannedCount: plannedCount,
                queuedCount: queuedCount,
                skippedCount: skippedCount,
                dryRun: req?.DryRun == true);

            return new RuntimeOperationResult<AdminRuntimeCapabilityBEnqueueResponseDto>(
                new AdminRuntimeCapabilityBEnqueueResponseDto(
                    CdcAlignment,
                    env.EnvironmentName,
                    CapabilityBBackofficeGenerationKey,
                    campaignId,
                    req?.DryRun == true,
                    req?.Force == true,
                    candidates.Length,
                    plannedCount,
                    queuedCount,
                    skippedCount,
                    reasonCounts,
                    items),
                null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                activity,
                "capability_b_enqueue",
                success: false,
                durationMs: sw.ElapsedMilliseconds,
                queuedCount: 0,
                skippedCount: 0,
                dryRun: req?.DryRun == true,
                errorReason: ex.Message);
            throw;
        }
    }

    internal static async Task<RuntimeOperationResult<AdminRuntimeCapabilityBClaimResponseDto>> ClaimCapabilityBBackofficeExecutionAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHostEnvironment env,
        AdminRuntimeCapabilityBClaimRequestDto? req,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartCapabilityBOperationActivity("capability_b_claim");
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var gate = await EnsureCapabilityReadyAsync(conn, CapabilityBBackofficeGenerationKey, options, rag, ct);
            if (gate.Error is not null)
            {
                sw.Stop();
                RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                    activity,
                    "capability_b_claim",
                    success: false,
                    durationMs: sw.ElapsedMilliseconds,
                    errorReason: gate.Error);
                return new RuntimeOperationResult<AdminRuntimeCapabilityBClaimResponseDto>(null, gate.Error);
            }

            var result = await RuntimeCapabilityBExecutionCoordinator.ClaimAsync(
                tenantId,
                conn,
                CdcAlignment,
                env.EnvironmentName,
                CapabilityBBackofficeGenerationKey,
                AdminRuntimeActor,
                gate.State!.ProfileKey,
                req,
                ct);

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                activity,
                "capability_b_claim",
                success: result.Error is null,
                durationMs: sw.ElapsedMilliseconds,
                errorReason: result.Error);

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                activity,
                "capability_b_claim",
                success: false,
                durationMs: sw.ElapsedMilliseconds,
                errorReason: ex.Message);
            throw;
        }
    }

    internal static Task<RuntimeOperationResult<CapabilityBExecutionContext>> ValidateCapabilityBExecutionLeaseAsync(
        Guid tenantId,
        NpgsqlConnection conn,
        Guid jobId,
        string? leaseToken,
        CancellationToken ct)
        => RuntimeCapabilityBExecutionCoordinator.ValidateLeaseAsync(
            tenantId,
            conn,
            CapabilityBBackofficeGenerationKey,
            AdminRuntimeActor,
            jobId,
            leaseToken,
            ct);

    internal static Task<RuntimeOperationResult<CapabilityBCompletionResult>> CompleteCapabilityBBackofficeExecutionAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        AdminRuntimeCapabilityBCompleteRequestDto? req,
        CancellationToken ct)
        => RuntimeCapabilityBExecutionCoordinator.CompleteAsync(
            tenantId,
            ds,
            AdminRuntimeActor,
            CapabilityBBackofficeGenerationKey,
            req,
            ct);

    internal static async Task<RuntimeOperationResult<AdminRuntimeCapabilityBFailResponseDto>> FailCapabilityBBackofficeExecutionAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        IHostEnvironment env,
        AdminRuntimeCapabilityBFailRequestDto? req,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartCapabilityBOperationActivity("capability_b_fail");
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await RuntimeCapabilityBExecutionCoordinator.FailAsync(
                tenantId,
                ds,
                CdcAlignment,
                env.EnvironmentName,
                CapabilityBBackofficeGenerationKey,
                AdminRuntimeActor,
                req,
                ct);

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                activity,
                "capability_b_fail",
                success: result.Error is null,
                durationMs: sw.ElapsedMilliseconds,
                errorReason: result.Error);

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                activity,
                "capability_b_fail",
                success: false,
                durationMs: sw.ElapsedMilliseconds,
                errorReason: ex.Message);
            throw;
        }
    }

    internal static async Task<RuntimeOperationResult<AdminRuntimeCapabilityAEnqueueResponseDto>> EnqueueCapabilityAEnrichmentAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IngestionOptions ingest,
        IHostEnvironment env,
        AdminRuntimeCapabilityAEnqueueRequestDto? req,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartCapabilityAOperationActivity("capability_a_enqueue");
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var gate = await EnsureCapabilityReadyAsync(conn, CapabilityACorpusEnrichmentKey, options, rag, ct);
            if (gate.Error is not null)
            {
                sw.Stop();
                RuntimeGovernanceTelemetry.CompleteCapabilityAOperation(
                    activity,
                    "capability_a_enqueue",
                    success: false,
                    durationMs: sw.ElapsedMilliseconds,
                    queuedCount: 0,
                    skippedCount: 0,
                    errorReason: gate.Error);
                return new RuntimeOperationResult<AdminRuntimeCapabilityAEnqueueResponseDto>(null, gate.Error);
            }

            if (string.IsNullOrWhiteSpace(ingest.DocumentsRoot))
            {
                sw.Stop();
                RuntimeGovernanceTelemetry.CompleteCapabilityAOperation(
                    activity,
                    "capability_a_enqueue",
                    success: false,
                    durationMs: sw.ElapsedMilliseconds,
                    queuedCount: 0,
                    skippedCount: 0,
                    errorReason: "documents_root_not_configured");
                return new RuntimeOperationResult<AdminRuntimeCapabilityAEnqueueResponseDto>(null, "documents_root_not_configured");
            }

            if (!Directory.Exists(ingest.DocumentsRoot))
            {
                sw.Stop();
                RuntimeGovernanceTelemetry.CompleteCapabilityAOperation(
                    activity,
                    "capability_a_enqueue",
                    success: false,
                    durationMs: sw.ElapsedMilliseconds,
                    queuedCount: 0,
                    skippedCount: 0,
                    errorReason: "documents_root_not_found");
                return new RuntimeOperationResult<AdminRuntimeCapabilityAEnqueueResponseDto>(null, "documents_root_not_found");
            }

            var candidates = await RuntimeCapabilityAEnrichmentStore.LoadCandidatesAsync(
                conn,
                tenantId,
                ingest,
                req?.Category,
                req?.MaxCandidates ?? 50,
                req?.ReasonFilters,
                ct);
            var response = await RuntimeCapabilityAEnrichmentCoordinator.EnqueueAsync(
                conn,
                tenantId,
                CdcAlignment,
                CapabilityACorpusEnrichmentKey,
                env.EnvironmentName,
                gate.State!.ProfileKey,
                ingest,
                req,
                candidates,
                ct);

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteCapabilityAOperation(
                activity,
                "capability_a_enqueue",
                success: true,
                durationMs: sw.ElapsedMilliseconds,
                candidateCount: response.CandidateCount,
                plannedCount: response.PlannedCount,
                queuedCount: response.QueuedCount,
                skippedCount: response.SkippedCount,
                dryRun: req?.DryRun == true);

            return new RuntimeOperationResult<AdminRuntimeCapabilityAEnqueueResponseDto>(
                response,
                null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteCapabilityAOperation(
                activity,
                "capability_a_enqueue",
                success: false,
                durationMs: sw.ElapsedMilliseconds,
                candidateCount: 0,
                plannedCount: 0,
                queuedCount: 0,
                skippedCount: 0,
                dryRun: req?.DryRun == true,
                errorReason: ex.Message);
            throw;
        }
    }

    internal static async Task<AdminRuntimeRequalifyResponseDto> RequalifyAsync(
        NpgsqlDataSource ds,
        IHttpClientFactory httpFactory,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHostEnvironment env,
        AdminRuntimeRequalifyRequestDto? req,
        CancellationToken ct)
    {
        var profile = RuntimeCatalogBuilder.ResolveProfile(req?.ProfileKey, options);
        var definitions = RuntimeCapabilityRegistry.ResolveSelection(req?.CapabilityKey);
        RuntimeGovernanceTelemetry.RecordRequalifyRequest(
            string.IsNullOrWhiteSpace(req?.CapabilityKey) ? "all" : req!.CapabilityKey!,
            profile.Key,
            definitions.Count);

        await using var conn = await ds.OpenConnectionAsync(ct);
        var existingStates = await RuntimeCapabilityStateStore.LoadPersistedStatesAsync(conn, ct);
        var batch = await RuntimeCapabilityLifecycleCoordinator.RequalifyAsync(
            conn,
            definitions,
            profile,
            existingStates,
            req?.SelectWhenQualified,
            options,
            rag,
            httpFactory,
            ct);

        return new AdminRuntimeRequalifyResponseDto(
            CdcAlignment,
            env.EnvironmentName,
            profile.Key,
            batch.Items,
            batch.WarmupResults);
    }

    internal static async Task<AdminRuntimeReconcileStaleResponseDto> ReconcileStaleAsync(
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHostEnvironment env,
        AdminRuntimeReconcileStaleRequestDto? req,
        CancellationToken ct)
    {
        var definitions = RuntimeCapabilityRegistry.ResolveSelection(req?.CapabilityKey);
        await using var conn = await ds.OpenConnectionAsync(ct);
        var existingStates = await RuntimeCapabilityStateStore.LoadPersistedStatesAsync(conn, ct);
        var items = await RuntimeCapabilityLifecycleCoordinator.ReconcileStaleAsync(
            conn,
            definitions,
            existingStates,
            options,
            rag,
            AdminRuntimeActor,
            ct);

        return new AdminRuntimeReconcileStaleResponseDto(
            CdcAlignment,
            env.EnvironmentName,
            items.Length,
            items);
    }

    internal static async Task<CapabilitySelectionUpdateResult> UpdateSelectionAsync(
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        string capabilityKey,
        AdminRuntimeCapabilitySelectionRequestDto? req,
        CancellationToken ct)
    {
        var definition = RuntimeCapabilityRegistry.FindDefinition(capabilityKey);
        if (definition is null)
            return new CapabilitySelectionUpdateResult(null, "unknown capability");

        using var selectionActivity = RuntimeGovernanceTelemetry.StartCapabilitySelectActivity(definition.Key);
        var sw = Stopwatch.StartNew();
        await using var conn = await ds.OpenConnectionAsync(ct);
        var existingStates = await RuntimeCapabilityStateStore.LoadPersistedStatesAsync(conn, ct);
        var current = RuntimeCapabilityStateResolver.ResolveState(
            definition,
            existingStates.TryGetValue(definition.Key, out var row) ? row : null,
            options,
            rag);
        var result = await RuntimeCapabilitySelectionOrchestrator.UpdateSelectionAsync(
            conn,
            definition,
            existingStates,
            options,
            rag,
            req,
            ct);
        sw.Stop();
        RuntimeGovernanceTelemetry.CompleteCapabilitySelect(
            selectionActivity,
            definition.Key,
            result.Error is null ? result.State!.DesiredEnabled : current.DesiredEnabled,
            result.Error is null ? result.State!.Authorized : current.Authorized,
            result.Error is null ? result.State!.Selected : current.Selected,
            success: result.Error is null,
            durationMs: sw.ElapsedMilliseconds,
            errorReason: result.Error);
        return result;
    }

    private static Task<Guid> InsertCapabilityBAdminJobAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        Guid docId,
        string docPath,
        bool force,
        Guid campaignId,
        string? profileKey,
        CancellationToken ct)
        => RuntimeCapabilityBExecutionStore.InsertCapabilityBAdminJobAsync(conn, tenantId, docId, docPath, force, campaignId, profileKey, ct);

    internal static Task<CapabilityBDocumentRow?> LoadCapabilityBDocumentAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        Guid docId,
        CancellationToken ct)
        => RuntimeCapabilityBExecutionStore.LoadCapabilityBDocumentAsync(conn, tenantId, docId, ct);

    internal static async Task<string[]> LoadCapabilityBSectionTitlesAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        Guid docId,
        int indexedVersion,
        int limit,
        CancellationToken ct)
        => (await conn.QueryAsync<string>(new CommandDefinition(
            """
SELECT ds.title
FROM document_revisions dr
JOIN document_sections ds ON ds.revision_id = dr.revision_id
WHERE dr.tenant_id=@tenant
  AND dr.doc_id=@docId
  AND dr.indexed_version=@indexedVersion
ORDER BY ds.ordinal
LIMIT @limit;
""",
            new
            {
                tenant = tenantId,
                docId,
                indexedVersion,
                limit = Math.Clamp(limit, 1, 20)
            },
            cancellationToken: ct)))
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Select(title => title.Trim())
            .ToArray();

    internal static async Task<string[]> LoadCapabilityBUnitExcerptsAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        Guid docId,
        int indexedVersion,
        int limit,
        CancellationToken ct)
        => (await conn.QueryAsync<string>(new CommandDefinition(
            """
SELECT du.text_content
FROM document_revisions dr
JOIN document_units du ON du.revision_id = dr.revision_id
WHERE dr.tenant_id=@tenant
  AND dr.doc_id=@docId
  AND dr.indexed_version=@indexedVersion
ORDER BY du.ordinal
LIMIT @limit;
""",
            new
            {
                tenant = tenantId,
                docId,
                indexedVersion,
                limit = Math.Clamp(limit, 1, 20)
            },
            cancellationToken: ct)))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text.Trim())
            .ToArray();

    internal static Task RecordCapabilityBSummaryCompletedAsync(
        NpgsqlConnection conn,
        Guid jobId,
        Guid docId,
        string docPath,
        string level,
        string sourceHash,
        int summaryLength,
        string? profileKey,
        Guid? campaignId,
        string? runtimeCapabilityStatus,
        CancellationToken ct)
        => RuntimeCapabilityBExecutionStore.RecordCapabilityBSummaryCompletedAsync(
            conn,
            jobId,
            docId,
            docPath,
            level,
            sourceHash,
            summaryLength,
            profileKey,
            campaignId,
            runtimeCapabilityStatus,
            ct);

    internal static AdminRuntimeCapabilityEventDto CreateCapabilityEvent(
        string capabilityKey,
        string? profileKey,
        string eventType,
        string? reason,
        IReadOnlyDictionary<string, object?>? details = null)
        => new(
            EventId: Guid.NewGuid(),
            CapabilityKey: capabilityKey,
            ProfileKey: profileKey,
            EventType: eventType,
            Actor: AdminRuntimeActor,
            Reason: reason,
            OccurredAt: DateTimeOffset.UtcNow,
            Details: details);

    internal static IReadOnlyDictionary<string, object?> BuildRuntimeEnvironmentSnapshot()
        => new Dictionary<string, object?>
        {
            ["machineName"] = Environment.MachineName,
            ["osVersion"] = Environment.OSVersion.VersionString,
            ["processArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["frameworkDescription"] = RuntimeInformation.FrameworkDescription,
            ["processorCount"] = Environment.ProcessorCount,
            ["is64BitProcess"] = Environment.Is64BitProcess
        };

    internal static IReadOnlyList<string> BuildCapabilityAHypotheticalQuestions(
        string docName,
        IReadOnlyList<string> sectionTitles,
        IReadOnlyList<string> excerpts)
        => RuntimeCapabilityAEnrichmentStore.BuildHypotheticalQuestions(
            docName,
            sectionTitles,
            excerpts);

    private static void IncrementCapabilityAReasonCounts(
        IDictionary<string, int> counts,
        IEnumerable<string> reasons)
    {
        foreach (var reason in reasons.Where(reason => !string.IsNullOrWhiteSpace(reason)))
        {
            counts[reason] = counts.TryGetValue(reason, out var current)
                ? current + 1
                : 1;
        }
    }

    private static T ExecuteArtifactRead<T>(string artifactName, Func<T> build)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity(artifactName);
        var sw = Stopwatch.StartNew();
        try
        {
            var result = build();
            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, artifactName, success: true, durationMs: sw.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, artifactName, success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal sealed record CapabilityEvaluation(
        AdminRuntimeCapabilityStateDto State,
        AdminRuntimeWarmupResultDto? WarmupResult);

    internal sealed record CapabilitySelectionUpdateResult(
        AdminRuntimeCapabilityStateDto? State,
        string? Error);

    private sealed record CapabilityGateResult(
        AdminRuntimeCapabilityStateDto? State,
        string? Error);

    internal sealed record WarmupPassResult(
        bool Passed,
        DateTimeOffset MeasuredAt,
        IReadOnlyDictionary<string, object?> Details,
        string? Error);

    internal sealed record WarmupCheckResult(
        bool Passed,
        string Status,
        long DurationMs,
        IReadOnlyDictionary<string, object?> Details,
        string? Error);

    internal sealed record PerformanceBudgetResult(
        bool Passed,
        IReadOnlyDictionary<string, object?> Budgets,
        IReadOnlyList<string> Violations,
        string? Error);

    internal sealed record ProfilePolicyResult(
        bool Passed,
        IReadOnlyDictionary<string, object?> Details,
        string? Error);

    internal static HardwareGateResult EvaluateHardwareGate(RuntimeGovernanceOptions options)
        => EvaluateHardwareGate(null, options);

    internal static HardwareGateResult EvaluateHardwareGate(
        AdminRuntimeHardwareRequirementsDto? requirements,
        RuntimeGovernanceOptions options)
    {
        var processors = Environment.ProcessorCount;
        var availableMemoryMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        var is64Bit = Environment.Is64BitProcess;
        var architecture = RuntimeInformation.ProcessArchitecture.ToString();
        var minCpuCores = requirements?.MinCpuCores ?? options.MinCpuCores;
        var minAvailableMemoryMb = requirements?.MinAvailableMemoryMb ?? options.MinAvailableMemoryMb;
        var require64BitProcess = requirements?.Require64BitProcess ?? options.Require64BitProcess;

        var reasons = new List<string>();
        if (processors < Math.Max(1, minCpuCores))
            reasons.Add($"cpu_cores<{minCpuCores}");
        if (availableMemoryMb < Math.Max(1, minAvailableMemoryMb))
            reasons.Add($"available_memory_mb<{minAvailableMemoryMb}");
        if (require64BitProcess && !is64Bit)
            reasons.Add("process_not_64bit");

        var details = new Dictionary<string, object?>
        {
            ["cpuCores"] = processors,
            ["availableMemoryMb"] = availableMemoryMb,
            ["is64BitProcess"] = is64Bit,
            ["architecture"] = architecture,
            ["minCpuCores"] = minCpuCores,
            ["minAvailableMemoryMb"] = minAvailableMemoryMb,
            ["require64BitProcess"] = require64BitProcess,
            ["source"] = requirements is null ? "global_defaults" : "profile",
            ["reasons"] = reasons.ToArray()
        };

        return reasons.Count == 0
            ? new HardwareGateResult(true, details, null)
            : new HardwareGateResult(false, details, $"hardware gate failed: {string.Join(", ", reasons)}");
    }

    private static async Task<CapabilityGateResult> EnsureCapabilityReadyAsync(
        NpgsqlConnection conn,
        string capabilityKey,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        CancellationToken ct)
    {
        var definition = RuntimeCapabilityRegistry.FindDefinition(capabilityKey);
        if (definition is null)
            return new CapabilityGateResult(null, "unknown capability");

        var existingStates = await RuntimeCapabilityStateStore.LoadPersistedStatesAsync(conn, ct);
        var current = RuntimeCapabilityStateResolver.ResolveState(
            definition,
            existingStates.TryGetValue(definition.Key, out var row) ? row : null,
            options,
            rag);

        if (!current.Implemented)
            return new CapabilityGateResult(current, "capability_not_implemented");
        if (!current.DesiredEnabled)
            return new CapabilityGateResult(current, "capability_not_desired_enabled");
        if (!current.Qualified)
            return new CapabilityGateResult(current, "capability_not_qualified");
        if (!current.Authorized || !current.EffectiveAuthorized)
            return new CapabilityGateResult(current, "capability_not_authorized");
        if (!current.Selected || !current.EffectiveSelected)
            return new CapabilityGateResult(current, "capability_not_selected");

        return new CapabilityGateResult(current, null);
    }

    internal static Task<IReadOnlyDictionary<Guid, AdminRuntimeCapabilityBBackofficeCandidateDto>> LoadCapabilityBBackofficeCandidateLookupAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string? categoryPath,
        RuntimeGovernanceOptions options,
        CancellationToken ct)
        => RuntimeCapabilityBBackofficeStore.LoadCandidateLookupAsync(
            conn,
            tenantId,
            categoryPath,
            options,
            ct);

    internal sealed record HardwareGateResult(
        bool Passed,
        IReadOnlyDictionary<string, object?> Details,
        string? Error);

    internal sealed record RuntimeSpecificGateResult(
        bool Passed,
        IReadOnlyDictionary<string, object?> Details,
        string? Error);

}
