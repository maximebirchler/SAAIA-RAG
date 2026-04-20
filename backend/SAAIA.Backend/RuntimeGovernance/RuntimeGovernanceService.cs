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
            BuildCapabilityCatalog());
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
                    BuildCapabilityCatalog());
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

        var items = RuntimeCapabilities
            .Select(def => persisted.TryGetValue(def.Key, out var row)
                ? ProjectCapabilityState(def, row, options, rag)
                : BuildDefaultState(def, options, rag))
            .ToArray();

        var warmupResults = await LoadWarmupResultsAsync(conn, capabilityKey: null, limit: 25, ct);

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
            var capabilityBOperationalSummary = await LoadCapabilityBOperationalSummaryAsync(conn, options, ct);
            var items = RuntimeCapabilities
                .Select(def => persisted.TryGetValue(def.Key, out var row)
                    ? ProjectCapabilityState(def, row, options, rag)
                    : BuildDefaultState(def, options, rag))
                .Select(state => MapCapabilityDiagnostic(
                    state,
                    string.Equals(state.Key, CapabilityBBackofficeGenerationKey, StringComparison.Ordinal)
                        ? capabilityBOperationalSummary
                        : null))
                .ToArray();

            var summary = new AdminRuntimeDiagnosticsSummaryDto(
                TotalCapabilities: items.Length,
                ImplementedCapabilities: items.Count(item => item.Implemented),
                QualifiedCapabilities: items.Count(item => item.Qualified && !item.Stale),
                SelectedCapabilities: items.Count(item => item.Selected && !item.Stale),
                PersistedSelectedCapabilities: items.Count(item => item.PersistedSelected),
                BlockedCapabilities: items.Count(item => item.Blockers.Count > 0),
                StaleCapabilities: items.Count(item => item.Stale));

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
            var items = RuntimeCapabilities
                .Select(def => persisted.TryGetValue(def.Key, out var row)
                    ? ProjectCapabilityState(def, row, options, rag)
                    : BuildDefaultState(def, options, rag))
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
        var items = await LoadWarmupResultsAsync(conn, capabilityKey, Math.Clamp(limit, 1, 100), ct);
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
            var items = await LoadWarmupResultsAsync(conn, capabilityKey, Math.Clamp(limit, 1, 100), ct);
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
            var items = await LoadCapabilityEventsAsync(conn, capabilityKey, Math.Clamp(limit, 1, 200), ct);
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
            var items = await LoadCapabilityEventsAsync(conn, capabilityKey, Math.Clamp(limit, 1, 200), ct);
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

            var candidates = await LoadCapabilityBBackofficeCandidatesAsync(
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

            var candidates = await LoadCapabilityBBackofficeCandidatesAsync(
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

                await InsertCapabilityEventAsync(
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

            await InsertCapabilityEventAsync(
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

            CapabilityBExecutionJobRow? job;
            if (req?.JobId is Guid requestedJobId && requestedJobId != Guid.Empty)
            {
                job = await LoadCapabilityBExecutionJobAsync(conn, tenantId, requestedJobId, ct);
                if (job is null)
                {
                    sw.Stop();
                    RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                        activity,
                        "capability_b_claim",
                        success: false,
                        durationMs: sw.ElapsedMilliseconds,
                        errorReason: "capability_b_job_not_found");
                    return new RuntimeOperationResult<AdminRuntimeCapabilityBClaimResponseDto>(null, "capability_b_job_not_found");
                }

                if (!string.Equals(job.Status, "queued", StringComparison.Ordinal))
                {
                    sw.Stop();
                    RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                        activity,
                        "capability_b_claim",
                        success: false,
                        durationMs: sw.ElapsedMilliseconds,
                        errorReason: "capability_b_job_not_claimable");
                    return new RuntimeOperationResult<AdminRuntimeCapabilityBClaimResponseDto>(null, "capability_b_job_not_claimable");
                }
            }
            else
            {
                job = await LoadNextQueuedCapabilityBExecutionJobAsync(conn, tenantId, ct);
                if (job is null)
                {
                    sw.Stop();
                    RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                        activity,
                        "capability_b_claim",
                        success: false,
                        durationMs: sw.ElapsedMilliseconds,
                        errorReason: "capability_b_no_queued_jobs");
                    return new RuntimeOperationResult<AdminRuntimeCapabilityBClaimResponseDto>(null, "capability_b_no_queued_jobs");
                }
            }

            if (!job.DocId.HasValue || job.DocId.Value == Guid.Empty || string.IsNullOrWhiteSpace(job.DocPath))
            {
                sw.Stop();
                RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                    activity,
                    "capability_b_claim",
                    success: false,
                    durationMs: sw.ElapsedMilliseconds,
                    errorReason: "capability_b_job_missing_document_reference");
                return new RuntimeOperationResult<AdminRuntimeCapabilityBClaimResponseDto>(null, "capability_b_job_missing_document_reference");
            }

            var claimedAt = DateTimeOffset.UtcNow;
            var claimedBy = string.IsNullOrWhiteSpace(req?.ExecutorId)
                ? AdminRuntimeActor
                : req!.ExecutorId!.Trim();
            var leaseToken = Guid.NewGuid().ToString("N");
            var payload = new Dictionary<string, object?>(
                ParseDetails(job.PayloadJson) ?? new Dictionary<string, object?>(),
                StringComparer.Ordinal)
            {
                ["executionLeaseToken"] = leaseToken,
                ["executionClaimedAt"] = claimedAt,
                ["executionClaimedBy"] = claimedBy,
                ["executionClaimCapabilityStatus"] = job.RuntimeCapabilityStatus ?? "selected",
                ["executionClaimProfileKey"] = gate.State!.ProfileKey
            };

            const string claimSql = """
UPDATE admin_jobs
SET status='running',
    started_at=COALESCE(started_at, @claimedAt),
    payload=@payload::jsonb,
    last_error=NULL
WHERE tenant_id=@tenant AND job_id=@jobId AND status='queued';
""";

            var updated = await conn.ExecuteAsync(new CommandDefinition(
                claimSql,
                new
                {
                    tenant = tenantId,
                    jobId = job.JobId,
                    claimedAt = claimedAt.UtcDateTime,
                    payload = JsonSerializer.Serialize(payload)
                },
                cancellationToken: ct));

            if (updated == 0)
            {
                sw.Stop();
                RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                    activity,
                    "capability_b_claim",
                    success: false,
                    durationMs: sw.ElapsedMilliseconds,
                    errorReason: "capability_b_job_not_claimable");
                return new RuntimeOperationResult<AdminRuntimeCapabilityBClaimResponseDto>(null, "capability_b_job_not_claimable");
            }

            await InsertCapabilityEventAsync(
                conn,
                CreateCapabilityEvent(
                    capabilityKey: CapabilityBBackofficeGenerationKey,
                    profileKey: gate.State!.ProfileKey,
                    eventType: "capability_b_job_claimed",
                    reason: "execution_claimed",
                    details: new Dictionary<string, object?>
                    {
                        ["jobId"] = job.JobId,
                        ["docId"] = job.DocId,
                        ["docPath"] = job.DocPath,
                        ["level"] = job.Level,
                        ["campaignId"] = job.CampaignId,
                        ["claimedBy"] = claimedBy,
                        ["claimedAt"] = claimedAt,
                        ["leaseToken"] = leaseToken,
                        ["runtimeCapabilityStatus"] = job.RuntimeCapabilityStatus ?? "selected",
                        ["runtimeProfileKey"] = gate.State!.ProfileKey
                    }),
                ct);

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                activity,
                "capability_b_claim",
                success: true,
                durationMs: sw.ElapsedMilliseconds);

            return new RuntimeOperationResult<AdminRuntimeCapabilityBClaimResponseDto>(
                new AdminRuntimeCapabilityBClaimResponseDto(
                    CdcAlignment,
                    env.EnvironmentName,
                    CapabilityBBackofficeGenerationKey,
                    job.JobId,
                    job.DocId.Value,
                    job.DocPath!,
                    job.Level ?? "medium",
                    job.ExecutionMode ?? "server_backoffice",
                    job.RuntimeCapabilityKey ?? CapabilityBBackofficeGenerationKey,
                    job.RuntimeCapabilityStatus ?? "selected",
                    gate.State!.ProfileKey,
                    job.CampaignId,
                    leaseToken,
                    claimedBy,
                    claimedAt),
                null);
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

    internal static async Task<RuntimeOperationResult<CapabilityBExecutionContext>> ValidateCapabilityBExecutionLeaseAsync(
        Guid tenantId,
        NpgsqlConnection conn,
        Guid jobId,
        string? leaseToken,
        CancellationToken ct)
    {
        if (jobId == Guid.Empty)
            return new RuntimeOperationResult<CapabilityBExecutionContext>(null, "job_id_required");
        if (string.IsNullOrWhiteSpace(leaseToken))
            return new RuntimeOperationResult<CapabilityBExecutionContext>(null, "execution_lease_token_required");

        var job = await LoadCapabilityBExecutionJobAsync(conn, tenantId, jobId, ct);
        if (job is null)
            return new RuntimeOperationResult<CapabilityBExecutionContext>(null, "capability_b_job_not_found");
        if (!job.DocId.HasValue || job.DocId.Value == Guid.Empty || string.IsNullOrWhiteSpace(job.DocPath))
            return new RuntimeOperationResult<CapabilityBExecutionContext>(null, "capability_b_job_missing_document_reference");
        if (!string.Equals(job.Status, "running", StringComparison.Ordinal))
            return new RuntimeOperationResult<CapabilityBExecutionContext>(null, "capability_b_job_not_running");
        if (string.IsNullOrWhiteSpace(job.ExecutionLeaseToken))
            return new RuntimeOperationResult<CapabilityBExecutionContext>(null, "capability_b_execution_not_claimed");
        if (!string.Equals(job.ExecutionLeaseToken, leaseToken.Trim(), StringComparison.Ordinal))
            return new RuntimeOperationResult<CapabilityBExecutionContext>(null, "capability_b_invalid_execution_lease");

        return new RuntimeOperationResult<CapabilityBExecutionContext>(
            new CapabilityBExecutionContext(
                job.JobId,
                job.DocId.Value,
                job.DocPath!,
                job.Level ?? "medium",
                job.Status,
                job.ExecutionMode ?? "server_backoffice",
                job.RuntimeCapabilityKey ?? CapabilityBBackofficeGenerationKey,
                job.RuntimeCapabilityStatus,
                job.RuntimeCapabilitySelected,
                job.RuntimeProfileKey,
                job.CampaignId,
                job.EnqueueSource,
                job.ExecutionLeaseToken!,
                job.ExecutionClaimedBy ?? AdminRuntimeActor,
                job.ExecutionClaimedAt),
            null);
    }

    internal static async Task<RuntimeOperationResult<CapabilityBCompletionResult>> CompleteCapabilityBBackofficeExecutionAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        AdminRuntimeCapabilityBCompleteRequestDto? req,
        CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        var validation = await ValidateCapabilityBExecutionLeaseAsync(
            tenantId,
            conn,
            req?.JobId ?? Guid.Empty,
            req?.LeaseToken,
            ct);
        if (validation.Error is not null)
            return new RuntimeOperationResult<CapabilityBCompletionResult>(null, validation.Error);

        if (string.IsNullOrWhiteSpace(req?.SummaryText))
            return new RuntimeOperationResult<CapabilityBCompletionResult>(null, "summary_text_required");

        var execution = validation.Payload!;
        var level = string.IsNullOrWhiteSpace(execution.Level)
            ? "medium"
            : execution.Level.Trim();
        var completedBy = string.IsNullOrWhiteSpace(execution.ClaimedBy)
            ? AdminRuntimeActor
            : execution.ClaimedBy.Trim();

        var doc = await LoadCapabilityBDocumentAsync(conn, tenantId, execution.DocId, ct);
        if (doc is null)
            return new RuntimeOperationResult<CapabilityBCompletionResult>(null, "document_not_found");

        var sourceHash = string.IsNullOrWhiteSpace(req?.SourceHash)
            ? await ComputeCapabilityBDocumentSourceHashAsync(conn, tenantId, execution.DocId, ct)
            : req.SourceHash.Trim();
        if (string.IsNullOrWhiteSpace(sourceHash))
            return new RuntimeOperationResult<CapabilityBCompletionResult>(null, "source_hash_unavailable");

        var normalizedSummaryText = req!.SummaryText.Trim();
        var metaJson = req.Meta.HasValue
            ? req.Meta.Value.GetRawText()
            : null;

        const string upsertSummarySql = """
INSERT INTO document_summaries(tenant_id, doc_id, level, doc_language, source_hash, summary_text, summary_meta, created_at, updated_at)
VALUES(@tenant, @docId, @level, @docLanguage, @sourceHash, @summaryText, @summaryMeta::jsonb, now(), now())
ON CONFLICT (tenant_id, doc_id, level)
DO UPDATE SET
  doc_language = EXCLUDED.doc_language,
  source_hash = EXCLUDED.source_hash,
  summary_text = EXCLUDED.summary_text,
  summary_meta = EXCLUDED.summary_meta,
  updated_at = now();
""";

        await conn.ExecuteAsync(new CommandDefinition(
            upsertSummarySql,
            new
            {
                tenant = tenantId,
                docId = execution.DocId,
                level,
                docLanguage = string.IsNullOrWhiteSpace(req.DocLanguage) ? null : req.DocLanguage.Trim(),
                sourceHash,
                summaryText = normalizedSummaryText,
                summaryMeta = (object?)metaJson ?? DBNull.Value
            },
            cancellationToken: ct));

        var summaryLength = normalizedSummaryText.Length;
        var finishedAt = DateTimeOffset.UtcNow;
        var result = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["docId"] = execution.DocId,
            ["docPath"] = doc.DocPath,
            ["level"] = level,
            ["stored"] = true,
            ["sourceHash"] = sourceHash,
            ["summaryLength"] = summaryLength,
            ["completedBy"] = completedBy,
            ["executionMode"] = execution.ExecutionMode,
            ["runtimeCapabilityKey"] = execution.RuntimeCapabilityKey,
            ["runtimeCapabilityStatus"] = execution.RuntimeCapabilityStatus,
            ["runtimeCapabilitySelected"] = execution.RuntimeCapabilitySelected,
            ["runtimeProfileKey"] = execution.RuntimeProfileKey,
            ["source"] = execution.EnqueueSource,
            ["campaignId"] = execution.CampaignId,
            ["executionLeaseToken"] = execution.LeaseToken
        });

        const string completeJobSql = """
UPDATE admin_jobs
SET status='done',
    result=@result::jsonb,
    finished_at=@finishedAt,
    last_error=NULL
WHERE tenant_id=@tenant
  AND job_id=@jobId
  AND status='running';
""";

        var updated = await conn.ExecuteAsync(new CommandDefinition(
            completeJobSql,
            new
            {
                tenant = tenantId,
                jobId = execution.JobId,
                result,
                finishedAt = finishedAt.UtcDateTime
            },
            cancellationToken: ct));

        if (updated == 0)
            return new RuntimeOperationResult<CapabilityBCompletionResult>(null, "capability_b_job_not_running");

        await RecordCapabilityBSummaryCompletedAsync(
            conn,
            execution.JobId,
            execution.DocId,
            doc.DocPath,
            level,
            sourceHash,
            summaryLength,
            execution.RuntimeProfileKey,
            execution.CampaignId,
            execution.RuntimeCapabilityStatus,
            ct);

        return new RuntimeOperationResult<CapabilityBCompletionResult>(
            new CapabilityBCompletionResult(
                execution.JobId,
                execution.DocId,
                doc.DocPath,
                level,
                sourceHash,
                summaryLength,
                completedBy,
                execution.CampaignId),
            null);
    }

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
            await using var conn = await ds.OpenConnectionAsync(ct);
            var validation = await ValidateCapabilityBExecutionLeaseAsync(
                tenantId,
                conn,
                req?.JobId ?? Guid.Empty,
                req?.LeaseToken,
                ct);
            if (validation.Error is not null)
            {
                sw.Stop();
                RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                    activity,
                    "capability_b_fail",
                    success: false,
                    durationMs: sw.ElapsedMilliseconds,
                    errorReason: validation.Error);
                return new RuntimeOperationResult<AdminRuntimeCapabilityBFailResponseDto>(null, validation.Error);
            }

            if (string.IsNullOrWhiteSpace(req?.Error))
            {
                sw.Stop();
                RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                    activity,
                    "capability_b_fail",
                    success: false,
                    durationMs: sw.ElapsedMilliseconds,
                    errorReason: "summary_job_error_required");
                return new RuntimeOperationResult<AdminRuntimeCapabilityBFailResponseDto>(null, "summary_job_error_required");
            }

            var execution = validation.Payload!;
            var failedAt = DateTimeOffset.UtcNow;
            var lastError = req!.Error.Trim();
            var result = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["docId"] = execution.DocId,
                ["docPath"] = execution.DocPath,
                ["level"] = execution.Level,
                ["stored"] = false,
                ["failedBy"] = execution.ClaimedBy,
                ["executionMode"] = execution.ExecutionMode,
                ["runtimeCapabilityKey"] = execution.RuntimeCapabilityKey,
                ["runtimeCapabilityStatus"] = execution.RuntimeCapabilityStatus,
                ["runtimeCapabilitySelected"] = execution.RuntimeCapabilitySelected,
                ["runtimeProfileKey"] = execution.RuntimeProfileKey,
                ["source"] = execution.EnqueueSource,
                ["campaignId"] = execution.CampaignId,
                ["executionLeaseToken"] = execution.LeaseToken,
                ["error"] = lastError,
                ["details"] = req is not null && req.Details.HasValue
                    ? req.Details.Value.Clone()
                    : null
            });

            const string failSql = """
UPDATE admin_jobs
SET status='failed',
    result=@result::jsonb,
    finished_at=@failedAt,
    last_error=@lastError
WHERE tenant_id=@tenant AND job_id=@jobId AND status='running';
""";

            var updated = await conn.ExecuteAsync(new CommandDefinition(
                failSql,
                new
                {
                    tenant = tenantId,
                    jobId = execution.JobId,
                    result,
                    failedAt = failedAt.UtcDateTime,
                    lastError
                },
                cancellationToken: ct));

            if (updated == 0)
            {
                sw.Stop();
                RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                    activity,
                    "capability_b_fail",
                    success: false,
                    durationMs: sw.ElapsedMilliseconds,
                    errorReason: "capability_b_job_not_running");
                return new RuntimeOperationResult<AdminRuntimeCapabilityBFailResponseDto>(null, "capability_b_job_not_running");
            }

            await InsertCapabilityEventAsync(
                conn,
                CreateCapabilityEvent(
                    capabilityKey: CapabilityBBackofficeGenerationKey,
                    profileKey: execution.RuntimeProfileKey,
                    eventType: "capability_b_job_failed",
                    reason: lastError,
                    details: new Dictionary<string, object?>
                    {
                        ["jobId"] = execution.JobId,
                        ["docId"] = execution.DocId,
                        ["docPath"] = execution.DocPath,
                        ["level"] = execution.Level,
                        ["campaignId"] = execution.CampaignId,
                        ["failedBy"] = execution.ClaimedBy,
                        ["failedAt"] = failedAt,
                        ["leaseToken"] = execution.LeaseToken,
                        ["runtimeCapabilityStatus"] = execution.RuntimeCapabilityStatus,
                        ["error"] = lastError
                    }),
                ct);

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteCapabilityBOperation(
                activity,
                "capability_b_fail",
                success: true,
                durationMs: sw.ElapsedMilliseconds);

            return new RuntimeOperationResult<AdminRuntimeCapabilityBFailResponseDto>(
                new AdminRuntimeCapabilityBFailResponseDto(
                    CdcAlignment,
                    env.EnvironmentName,
                    CapabilityBBackofficeGenerationKey,
                    execution.JobId,
                    execution.DocId,
                    execution.DocPath,
                    execution.Level,
                    execution.CampaignId,
                    execution.LeaseToken,
                    execution.ClaimedBy,
                    lastError,
                    "failed",
                    failedAt),
                null);
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
        var definitions = ResolveCapabilitySelection(req?.CapabilityKey);
        RuntimeGovernanceTelemetry.RecordRequalifyRequest(
            string.IsNullOrWhiteSpace(req?.CapabilityKey) ? "all" : req!.CapabilityKey!,
            profile.Key,
            definitions.Count);

        var items = new List<AdminRuntimeCapabilityStateDto>(definitions.Count);
        var warmupResults = new List<AdminRuntimeWarmupResultDto>();

        await using var conn = await ds.OpenConnectionAsync(ct);
        var existingStates = await RuntimeCapabilityStateStore.LoadPersistedStatesAsync(conn, ct);

        foreach (var definition in definitions)
        {
            var evaluation = await EvaluateCapabilityAsync(
                definition,
                profile,
                existingStates.TryGetValue(definition.Key, out var existingState) ? existingState : null,
                req?.SelectWhenQualified,
                options,
                rag,
                httpFactory,
                ct);

            await UpsertCapabilityStateAsync(conn, evaluation.State, ct);
            items.Add(evaluation.State);

            if (evaluation.WarmupResult is not null)
            {
                await InsertWarmupResultAsync(conn, evaluation.WarmupResult, ct);
                warmupResults.Add(evaluation.WarmupResult);
            }

            await InsertCapabilityEventAsync(
                conn,
                CreateCapabilityEvent(
                    capabilityKey: evaluation.State.Key,
                    profileKey: evaluation.State.ProfileKey,
                    eventType: "requalified",
                    reason: evaluation.State.Qualified ? "qualified" : evaluation.State.LastError ?? "not_qualified",
                    details: new Dictionary<string, object?>
                    {
                        ["qualified"] = evaluation.State.Qualified,
                        ["authorized"] = evaluation.State.Authorized,
                        ["selected"] = evaluation.State.Selected,
                        ["passCount"] = evaluation.State.PassCount,
                        ["implemented"] = evaluation.State.Implemented,
                        ["warmupResultId"] = evaluation.WarmupResult?.WarmupResultId,
                        ["lastError"] = evaluation.State.LastError,
                        ["qualificationFingerprint"] = evaluation.State.QualificationFingerprint
                    }),
                ct);
        }

        return new AdminRuntimeRequalifyResponseDto(
            CdcAlignment,
            env.EnvironmentName,
            profile.Key,
            items,
            warmupResults);
    }

    internal static async Task<AdminRuntimeReconcileStaleResponseDto> ReconcileStaleAsync(
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHostEnvironment env,
        AdminRuntimeReconcileStaleRequestDto? req,
        CancellationToken ct)
    {
        var definitions = ResolveCapabilitySelection(req?.CapabilityKey);
        var items = new List<AdminRuntimeCapabilityStateDto>();

        await using var conn = await ds.OpenConnectionAsync(ct);
        var existingStates = await RuntimeCapabilityStateStore.LoadPersistedStatesAsync(conn, ct);

        foreach (var definition in definitions)
        {
            if (!existingStates.TryGetValue(definition.Key, out var row))
                continue;

            var current = ProjectCapabilityState(definition, row, options, rag);
            var reconciliation = RuntimeCapabilitySelectionCoordinator.BuildStaleReconciliation(current, AdminRuntimeActor);
            if (reconciliation is null)
                continue;

            var updated = reconciliation.UpdatedState;

            await UpsertCapabilityStateAsync(conn, updated, ct);
            await InsertCapabilityEventAsync(
                conn,
                CreateCapabilityEvent(
                    capabilityKey: updated.Key,
                    profileKey: updated.ProfileKey,
                    eventType: "stale_reconciled",
                    reason: reconciliation.StaleReason,
                    details: reconciliation.EventDetails),
                ct);
            RuntimeGovernanceTelemetry.RecordStaleQualificationReconciled(updated.Key, updated.ProfileKey, reconciliation.StaleReason);
            items.Add(updated);
        }

        return new AdminRuntimeReconcileStaleResponseDto(
            CdcAlignment,
            env.EnvironmentName,
            items.Count,
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
        var definition = RuntimeCapabilities.FirstOrDefault(def => string.Equals(def.Key, capabilityKey?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (definition is null)
            return new CapabilitySelectionUpdateResult(null, "unknown capability");

        using var selectionActivity = RuntimeGovernanceTelemetry.StartCapabilitySelectActivity(definition.Key);
        var sw = Stopwatch.StartNew();
        await using var conn = await ds.OpenConnectionAsync(ct);
        var existingStates = await RuntimeCapabilityStateStore.LoadPersistedStatesAsync(conn, ct);
        var current = existingStates.TryGetValue(definition.Key, out var row)
            ? ProjectCapabilityState(definition, row, options, rag)
            : BuildDefaultState(definition, options, rag);
        var decision = RuntimeCapabilitySelectionCoordinator.EvaluateSelectionUpdate(current, req);

        if (!decision.Accepted)
        {
            await InsertCapabilityEventAsync(
                conn,
                CreateCapabilityEvent(
                    capabilityKey: definition.Key,
                    profileKey: current.ProfileKey,
                    eventType: "selection_rejected",
                    reason: decision.Error,
                    details: decision.EventDetails),
                ct);

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteCapabilitySelect(
                selectionActivity,
                definition.Key,
                decision.DesiredEnabled,
                decision.Authorized,
                decision.Selected,
                success: false,
                durationMs: sw.ElapsedMilliseconds,
                errorReason: decision.Error);
            return new CapabilitySelectionUpdateResult(current, decision.Error);
        }

        var updated = decision.State;

        await UpsertCapabilityStateAsync(conn, updated, ct);
        await InsertCapabilityEventAsync(
            conn,
            CreateCapabilityEvent(
                capabilityKey: updated.Key,
                profileKey: updated.ProfileKey,
                eventType: "selection_updated",
                reason: "selection_updated",
                details: decision.EventDetails),
            ct);
        sw.Stop();
        RuntimeGovernanceTelemetry.CompleteCapabilitySelect(selectionActivity, definition.Key, updated.DesiredEnabled, updated.Authorized, updated.Selected, success: true, durationMs: sw.ElapsedMilliseconds);
        return new CapabilitySelectionUpdateResult(updated, null);
    }

    private static async Task<CapabilityEvaluation> EvaluateCapabilityAsync(
        RuntimeCapabilityDefinition definition,
        AdminRuntimeWarmupProfileDto profile,
        AdminRuntimeCapabilityStateDto? existingState,
        bool? selectWhenQualified,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHttpClientFactory httpFactory,
        CancellationToken ct)
    {
        if (!definition.Implemented)
        {
            var stubDetails = new Dictionary<string, object?>
            {
                ["status"] = "not_implemented",
                ["note"] = definition.StatusNote
            };

            return new CapabilityEvaluation(
                new AdminRuntimeCapabilityStateDto(
                    Key: definition.Key,
                    DisplayName: definition.DisplayName,
                    Family: definition.Family,
                    RuntimeKey: definition.RuntimeKey,
                    Implemented: false,
                    DesiredEnabled: existingState?.DesiredEnabled ?? definition.DefaultDesiredEnabled,
                    Installed: false,
                    Configured: false,
                    Healthy: false,
                    Qualified: false,
                    Authorized: false,
                    Selected: false,
                    ProfileKey: profile.Key,
                    PassCount: 0,
                    LastCheckedAt: DateTimeOffset.UtcNow,
                    LastQualifiedAt: null,
                    LastError: null,
                    Details: stubDetails,
                    Stale: false,
                    QualificationFingerprint: null,
                    StaleReason: null,
                    QualificationAgeHours: null,
                    QualificationExpiresAt: null,
            PersistedAuthorized: false,
            PersistedSelected: false,
            EffectiveAuthorized: false,
            EffectiveSelected: false),
                null);
        }

        if (string.Equals(definition.Key, CapabilityACorpusEnrichmentKey, StringComparison.Ordinal))
            return await EvaluateCapabilityAAsync(definition, profile, existingState, selectWhenQualified, options, rag, ct);

        if (string.Equals(definition.Key, CapabilityBBackofficeGenerationKey, StringComparison.Ordinal))
            return await EvaluateCapabilityBAsync(definition, profile, existingState, selectWhenQualified, options, rag, ct);

        var installed = !string.IsNullOrWhiteSpace(rag.QdrantBaseUrl)
            && !string.IsNullOrWhiteSpace(rag.EmbeddingsBaseUrl);
        var configured = installed
            && !string.IsNullOrWhiteSpace(rag.QdrantCollection)
            && !string.IsNullOrWhiteSpace(rag.EmbeddingsModel);
        var hardwareGate = EvaluateHardwareGate(profile.HardwareRequirements, options);
        var runtimeGates = EvaluateRuntimeSpecificGates(profile, options, rag);
        var profilePolicy = EvaluateProfilePolicy(profile, rag);

        var passDetails = new List<IReadOnlyDictionary<string, object?>>();
        var passesSucceeded = 0;
        string? lastError = null;
        var lastCheckedAt = DateTimeOffset.UtcNow;

        if (configured && hardwareGate.Passed && runtimeGates.Passed && profilePolicy.Passed)
        {
            for (var pass = 1; pass <= profile.PassCount; pass++)
            {
                var passResult = await RunCoreRetrievalWarmupPassAsync(definition.Key, profile, hardwareGate, runtimeGates, rag, httpFactory, pass, ct);
                passDetails.Add(passResult.Details);
                lastCheckedAt = passResult.MeasuredAt;
                if (passResult.Passed)
                {
                    passesSucceeded++;
                }
                else
                {
                    lastError = passResult.Error;
                }
            }
        }
        else if (configured && hardwareGate.Passed && runtimeGates.Passed)
        {
            lastError = profilePolicy.Error;
            passDetails.Add(profilePolicy.Details);
        }
        else if (configured && hardwareGate.Passed)
        {
            lastError = runtimeGates.Error;
            passDetails.Add(runtimeGates.Details);
        }
        else if (configured)
        {
            lastError = hardwareGate.Error;
            passDetails.Add(hardwareGate.Details);
        }
        else
        {
            lastError = installed
                ? "retrieval stack is installed but not fully configured"
                : "retrieval stack is not configured";
        }

        var desiredEnabled = existingState?.DesiredEnabled ?? definition.DefaultDesiredEnabled;
        var healthy = configured && passesSucceeded > 0;
        var qualified = configured && passesSucceeded == profile.PassCount;
        var authorizedPreference = existingState?.Authorized ?? options.AutoAuthorizeQualifiedCoreRetrieval;
        var authorized = qualified && desiredEnabled && authorizedPreference;
        var selectedPreference = selectWhenQualified ?? existingState?.Selected ?? options.AutoSelectQualifiedCoreRetrieval;
        var selected = qualified && authorized && desiredEnabled && selectedPreference;
        DateTimeOffset? lastQualifiedAt = qualified ? lastCheckedAt : null;
        var qualificationFingerprint = RuntimeCapabilityStateProjector.BuildQualificationFingerprint(definition.Key, profile, options, rag);

        var details = new Dictionary<string, object?>
        {
            ["status"] = qualified ? "qualified" : healthy ? "healthy" : configured ? "configured" : installed ? "installed" : "missing_dependencies",
            ["passesRequired"] = profile.PassCount,
            ["passesSucceeded"] = passesSucceeded,
            ["qdrantBaseUrl"] = rag.QdrantBaseUrl,
            ["qdrantCollection"] = rag.QdrantCollection,
            ["embeddingsBaseUrl"] = rag.EmbeddingsBaseUrl,
            ["embeddingsModel"] = rag.EmbeddingsModel,
            ["rerankEnabled"] = rag.EnableRerank,
            ["rerankBaseUrl"] = string.IsNullOrWhiteSpace(rag.RerankBaseUrl) ? rag.EmbeddingsBaseUrl : rag.RerankBaseUrl,
            ["hardGatesPassed"] = hardwareGate.Passed,
            ["hardware"] = hardwareGate.Details,
            ["runtimeGatesPassed"] = runtimeGates.Passed,
            ["runtimeGates"] = runtimeGates.Details,
            ["profilePolicyPassed"] = profilePolicy.Passed,
            ["profilePolicy"] = profilePolicy.Details,
            ["qualificationFingerprint"] = qualificationFingerprint.Hash,
            ["qualificationFingerprintInputs"] = qualificationFingerprint.Inputs,
            ["qualificationFingerprintGeneratedAt"] = lastCheckedAt,
            ["qualificationFreshness"] = qualified ? "fresh" : "candidate",
            ["freshnessPolicy"] = profile.FreshnessPolicy,
            ["runtimeEnvironment"] = BuildRuntimeEnvironmentSnapshot(),
            ["checks"] = passDetails
        };

        var state = new AdminRuntimeCapabilityStateDto(
            Key: definition.Key,
            DisplayName: definition.DisplayName,
            Family: definition.Family,
            RuntimeKey: definition.RuntimeKey,
            Implemented: true,
            DesiredEnabled: desiredEnabled,
            Installed: installed,
            Configured: configured,
            Healthy: healthy,
            Qualified: qualified,
            Authorized: authorized,
            Selected: selected,
            ProfileKey: profile.Key,
            PassCount: profile.PassCount,
            LastCheckedAt: lastCheckedAt,
            LastQualifiedAt: lastQualifiedAt,
            LastError: lastError,
            Details: details,
            Stale: false,
            QualificationFingerprint: qualificationFingerprint.Hash,
            StaleReason: null,
            QualificationAgeHours: 0d,
            QualificationExpiresAt: profile.FreshnessPolicy?.MaxQualificationAgeHours is long maxAgeHours
                ? lastQualifiedAt?.AddHours(maxAgeHours)
                : null,
            PersistedAuthorized: authorized,
            PersistedSelected: selected,
            EffectiveAuthorized: authorized,
            EffectiveSelected: selected);

        var warmupResult = new AdminRuntimeWarmupResultDto(
            WarmupResultId: Guid.NewGuid(),
            CapabilityKey: definition.Key,
            ProfileKey: profile.Key,
            PassCount: profile.PassCount,
            Passed: qualified,
            MeasuredAt: lastCheckedAt,
            Details: details);

        return new CapabilityEvaluation(state, warmupResult);
    }

    private static Task<CapabilityEvaluation> EvaluateCapabilityAAsync(
        RuntimeCapabilityDefinition definition,
        AdminRuntimeWarmupProfileDto profile,
        AdminRuntimeCapabilityStateDto? existingState,
        bool? selectWhenQualified,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var installed = true;
        var configured = !string.IsNullOrWhiteSpace(rag.QdrantBaseUrl)
            && !string.IsNullOrWhiteSpace(rag.QdrantCollection)
            && !string.IsNullOrWhiteSpace(rag.EmbeddingsBaseUrl)
            && !string.IsNullOrWhiteSpace(rag.EmbeddingsModel);
        var healthy = configured;
        var qualified = configured;
        var lastCheckedAt = DateTimeOffset.UtcNow;
        var selectionRequested = selectWhenQualified == true;
        var desiredEnabled = existingState?.DesiredEnabled ?? definition.DefaultDesiredEnabled;
        if (selectionRequested)
            desiredEnabled = true;
        var authorized = qualified && desiredEnabled && (existingState?.Authorized ?? selectionRequested);
        var selected = qualified && authorized && desiredEnabled && (existingState?.Selected ?? selectionRequested);
        var qualificationFingerprint = RuntimeCapabilityStateProjector.BuildQualificationFingerprint(definition.Key, profile, options, rag);

        var checks = new Dictionary<string, object?>
        {
            ["documentsCatalogAccessible"] = true,
            ["retrievalStackConfigured"] = configured,
            ["qdrantConfigured"] = !string.IsNullOrWhiteSpace(rag.QdrantBaseUrl) && !string.IsNullOrWhiteSpace(rag.QdrantCollection),
            ["embeddingsConfigured"] = !string.IsNullOrWhiteSpace(rag.EmbeddingsBaseUrl) && !string.IsNullOrWhiteSpace(rag.EmbeddingsModel)
        };

        var details = new Dictionary<string, object?>
        {
            ["status"] = qualified ? "qualified" : "configured",
            ["mode"] = "corpus_enrichment_admin",
            ["passesRequired"] = 1,
            ["passesSucceeded"] = qualified ? 1 : 0,
            ["checks"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["pass"] = 1,
                    ["status"] = qualified ? "passed" : "failed",
                    ["check"] = "capability_a.corpus_enrichment",
                    ["measuredAt"] = lastCheckedAt,
                    ["details"] = checks
                }
            },
            ["qualificationFingerprint"] = qualificationFingerprint.Hash,
            ["qualificationFingerprintInputs"] = qualificationFingerprint.Inputs,
            ["qualificationFingerprintGeneratedAt"] = lastCheckedAt,
            ["qualificationFreshness"] = qualified ? "fresh" : "candidate",
            ["freshnessPolicy"] = profile.FreshnessPolicy,
            ["runtimeEnvironment"] = BuildRuntimeEnvironmentSnapshot(),
            ["capabilityContract"] = new Dictionary<string, object?>
            {
                ["adminOnly"] = true,
                ["planEndpoint"] = "/admin/runtime/capabilities/capability_a.corpus_enrichment/candidates",
                ["enqueueEndpoint"] = "/admin/runtime/capabilities/capability_a.corpus_enrichment/enqueue",
                ["executionMode"] = "reindex_existing_ingestion_pipeline"
            }
        };

        var state = new AdminRuntimeCapabilityStateDto(
            Key: definition.Key,
            DisplayName: definition.DisplayName,
            Family: definition.Family,
            RuntimeKey: definition.RuntimeKey,
            Implemented: true,
            DesiredEnabled: desiredEnabled,
            Installed: installed,
            Configured: configured,
            Healthy: healthy,
            Qualified: qualified,
            Authorized: authorized,
            Selected: selected,
            ProfileKey: profile.Key,
            PassCount: 1,
            LastCheckedAt: lastCheckedAt,
            LastQualifiedAt: qualified ? lastCheckedAt : null,
            LastError: qualified ? null : "capability A requires the retrieval stack to be configured before it can enqueue enrichment jobs",
            Details: details,
            Stale: false,
            QualificationFingerprint: qualificationFingerprint.Hash,
            StaleReason: null,
            QualificationAgeHours: 0d,
            QualificationExpiresAt: profile.FreshnessPolicy?.MaxQualificationAgeHours is long maxAgeHours
                ? lastCheckedAt.AddHours(maxAgeHours)
                : null,
            PersistedAuthorized: authorized,
            PersistedSelected: selected,
            EffectiveAuthorized: authorized,
            EffectiveSelected: selected);

        var warmupResult = new AdminRuntimeWarmupResultDto(
            WarmupResultId: Guid.NewGuid(),
            CapabilityKey: definition.Key,
            ProfileKey: profile.Key,
            PassCount: 1,
            Passed: qualified,
            MeasuredAt: lastCheckedAt,
            Details: details);

        return Task.FromResult(new CapabilityEvaluation(state, warmupResult));
    }

    private static Task<CapabilityEvaluation> EvaluateCapabilityBAsync(
        RuntimeCapabilityDefinition definition,
        AdminRuntimeWarmupProfileDto profile,
        AdminRuntimeCapabilityStateDto? existingState,
        bool? selectWhenQualified,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var backofficeEnabled = RuntimeCatalogBuilder.IsBackofficeGenerationEnabled();
        var installed = true;
        var configured = backofficeEnabled;
        var healthy = configured;
        var qualified = configured;
        var lastCheckedAt = DateTimeOffset.UtcNow;
        var selectionRequested = selectWhenQualified == true;
        var desiredEnabled = existingState?.DesiredEnabled ?? definition.DefaultDesiredEnabled;
        if (selectionRequested)
            desiredEnabled = true;
        var authorized = qualified && desiredEnabled && (existingState?.Authorized ?? selectionRequested);
        var selected = qualified && authorized && desiredEnabled && (existingState?.Selected ?? selectionRequested);
        var qualificationFingerprint = RuntimeCapabilityStateProjector.BuildQualificationFingerprint(definition.Key, profile, options, rag);

        var checks = new Dictionary<string, object?>
        {
            ["adminJobsAvailable"] = true,
            ["summaryEndpointsAvailable"] = true,
            ["backofficeEnabled"] = backofficeEnabled
        };

        var details = new Dictionary<string, object?>
        {
            ["status"] = qualified ? "qualified" : "configured",
            ["mode"] = "summary_generation_admin",
            ["passesRequired"] = 1,
            ["passesSucceeded"] = qualified ? 1 : 0,
            ["checks"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["pass"] = 1,
                    ["status"] = qualified ? "passed" : "failed",
                    ["check"] = CapabilityBBackofficeGenerationKey,
                    ["measuredAt"] = lastCheckedAt,
                    ["details"] = checks
                }
            },
            ["qualificationFingerprint"] = qualificationFingerprint.Hash,
            ["qualificationFingerprintInputs"] = qualificationFingerprint.Inputs,
            ["qualificationFingerprintGeneratedAt"] = lastCheckedAt,
            ["qualificationFreshness"] = qualified ? "fresh" : "candidate",
            ["freshnessPolicy"] = profile.FreshnessPolicy,
            ["runtimeEnvironment"] = BuildRuntimeEnvironmentSnapshot(),
            ["capabilityContract"] = new Dictionary<string, object?>
            {
                ["adminOnly"] = true,
                ["planEndpoint"] = "/admin/runtime/capabilities/capability_b.backoffice_generation/candidates",
                ["enqueueEndpoint"] = "/admin/runtime/capabilities/capability_b.backoffice_generation/enqueue",
                ["executionMode"] = "server_backoffice_summary_jobs",
                ["requiresBackofficeLlmEnabled"] = true
            }
        };

        var state = new AdminRuntimeCapabilityStateDto(
            Key: definition.Key,
            DisplayName: definition.DisplayName,
            Family: definition.Family,
            RuntimeKey: definition.RuntimeKey,
            Implemented: true,
            DesiredEnabled: desiredEnabled,
            Installed: installed,
            Configured: configured,
            Healthy: healthy,
            Qualified: qualified,
            Authorized: authorized,
            Selected: selected,
            ProfileKey: profile.Key,
            PassCount: 1,
            LastCheckedAt: lastCheckedAt,
            LastQualifiedAt: qualified ? lastCheckedAt : null,
            LastError: qualified ? null : "capability B requires BACKOFFICE_LLM_ENABLED=true before it can enqueue server backoffice summary jobs",
            Details: details,
            Stale: false,
            QualificationFingerprint: qualificationFingerprint.Hash,
            StaleReason: null,
            QualificationAgeHours: 0d,
            QualificationExpiresAt: profile.FreshnessPolicy?.MaxQualificationAgeHours is long maxAgeHours
                ? lastCheckedAt.AddHours(maxAgeHours)
                : null,
            PersistedAuthorized: authorized,
            PersistedSelected: selected,
            EffectiveAuthorized: authorized,
            EffectiveSelected: selected);

        var warmupResult = new AdminRuntimeWarmupResultDto(
            WarmupResultId: Guid.NewGuid(),
            CapabilityKey: definition.Key,
            ProfileKey: profile.Key,
            PassCount: 1,
            Passed: qualified,
            MeasuredAt: lastCheckedAt,
            Details: details);

        return Task.FromResult(new CapabilityEvaluation(state, warmupResult));
    }

    private static async Task<WarmupPassResult> RunCoreRetrievalWarmupPassAsync(
        string capabilityKey,
        AdminRuntimeWarmupProfileDto profile,
        HardwareGateResult hardwareGate,
        RuntimeSpecificGateResult runtimeGates,
        RagOptions rag,
        IHttpClientFactory httpFactory,
        int passNumber,
        CancellationToken ct)
    {
        using var warmupActivity = RuntimeGovernanceTelemetry.StartWarmupCheckActivity(capabilityKey, profile.Key);
        var measuredAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var qdrantCheck = await CheckQdrantAsync(rag, httpFactory, ct);
        var teiCheck = await CheckEmbeddingsAsync(rag, httpFactory, ct);
        var rerankCheck = rag.EnableRerank
            ? await CheckRerankAsync(rag, httpFactory, ct)
            : BuildSkippedWarmupCheck("tei.rerank", new Dictionary<string, object?> { ["enabled"] = false });

        sw.Stop();
        var performanceBudget = EvaluatePerformanceBudget(profile, qdrantCheck, teiCheck, rerankCheck, sw.ElapsedMilliseconds, rag.EnableRerank);

        var passed = qdrantCheck.Passed && teiCheck.Passed && rerankCheck.Passed && performanceBudget.Passed;
        var details = new Dictionary<string, object?>
        {
            ["pass"] = passNumber,
            ["measuredAt"] = measuredAt,
            ["durationMs"] = sw.ElapsedMilliseconds,
            ["status"] = passed ? "passed" : "failed",
            ["hardGatesPassed"] = hardwareGate.Passed,
            ["hardware"] = hardwareGate.Details,
            ["runtimeGatesPassed"] = runtimeGates.Passed,
            ["runtimeGates"] = runtimeGates.Details,
            ["performanceBudgetPassed"] = performanceBudget.Passed,
            ["performanceBudgets"] = performanceBudget.Budgets,
            ["performanceBudgetViolations"] = performanceBudget.Violations,
            ["checksSummary"] = new Dictionary<string, object?>
            {
                ["qdrantPassed"] = qdrantCheck.Passed,
                ["teiEmbeddingsPassed"] = teiCheck.Passed,
                ["teiRerankPassed"] = rerankCheck.Passed
            },
            ["measurementSemantics"] = BuildMeasurementSemanticsSummary(),
            ["measurements"] = BuildWarmupPassMeasurements(
                totalDurationMs: sw.ElapsedMilliseconds,
                qdrantDurationMs: qdrantCheck.DurationMs,
                embeddingsDurationMs: teiCheck.DurationMs,
                rerankDurationMs: rag.EnableRerank ? rerankCheck.DurationMs : null),
            ["qdrant"] = qdrantCheck.Details,
            ["teiEmbeddings"] = teiCheck.Details,
            ["teiRerank"] = rerankCheck.Details
        };

        var error = passed
            ? null
            : qdrantCheck.Error ?? teiCheck.Error ?? rerankCheck.Error ?? performanceBudget.Error ?? "warmup check failed";

        RuntimeGovernanceTelemetry.CompleteWarmupCheck(
            warmupActivity,
            capabilityKey,
            profile.Key,
            passNumber,
            passed,
            hardwareGate.Passed,
            runtimeGates.Passed,
            performanceBudget.Passed,
            sw.ElapsedMilliseconds,
            qdrantCheck.DurationMs,
            teiCheck.DurationMs,
            rag.EnableRerank ? rerankCheck.DurationMs : 0L);

        return new WarmupPassResult(passed, measuredAt, details, error);
    }

    private static async Task<WarmupCheckResult> CheckQdrantAsync(
        RagOptions rag,
        IHttpClientFactory httpFactory,
        CancellationToken ct)
    {
        var measuredAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        try
        {
            var qdrant = httpFactory.CreateClient("qdrant");
            qdrant.BaseAddress = new Uri(rag.QdrantBaseUrl);
            using var response = await qdrant.GetAsync($"/collections/{rag.QdrantCollection}", ct);
            sw.Stop();

            var details = new Dictionary<string, object?>
            {
                ["check"] = "qdrant.collection",
                ["status"] = response.IsSuccessStatusCode ? "ok" : "http_error",
                ["measuredAt"] = measuredAt,
                ["durationMs"] = sw.ElapsedMilliseconds,
                ["measurements"] = BuildNonGenerativeMeasurements(sw.ElapsedMilliseconds),
                ["statusCode"] = (int)response.StatusCode,
                ["collection"] = rag.QdrantCollection,
                ["baseUrl"] = rag.QdrantBaseUrl
            };

            if (!response.IsSuccessStatusCode)
            {
                var body = await TryReadBodyAsync(response, ct);
                details["body"] = body;
                return new WarmupCheckResult(false, "qdrant collection check failed", sw.ElapsedMilliseconds, details, "qdrant collection check failed");
            }

            return new WarmupCheckResult(true, "ok", sw.ElapsedMilliseconds, details, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new WarmupCheckResult(
                false,
                ex.Message,
                sw.ElapsedMilliseconds,
                new Dictionary<string, object?>
                {
                    ["check"] = "qdrant.collection",
                    ["status"] = "exception",
                    ["measuredAt"] = measuredAt,
                    ["durationMs"] = sw.ElapsedMilliseconds,
                    ["measurements"] = BuildNonGenerativeMeasurements(sw.ElapsedMilliseconds),
                    ["baseUrl"] = rag.QdrantBaseUrl,
                    ["exception"] = ex.GetType().Name
                },
                ex.Message);
        }
    }

    private static async Task<WarmupCheckResult> CheckEmbeddingsAsync(
        RagOptions rag,
        IHttpClientFactory httpFactory,
        CancellationToken ct)
    {
        var measuredAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        try
        {
            var tei = httpFactory.CreateClient("tei");
            tei.BaseAddress = new Uri(rag.EmbeddingsBaseUrl);
            var dim = await TeiClient.GetVectorDimAsync(tei, rag.EmbeddingsModel, ct);
            sw.Stop();

            return new WarmupCheckResult(
                true,
                "ok",
                sw.ElapsedMilliseconds,
                new Dictionary<string, object?>
                {
                    ["check"] = "tei.embeddings",
                    ["status"] = "ok",
                    ["measuredAt"] = measuredAt,
                    ["durationMs"] = sw.ElapsedMilliseconds,
                    ["measurements"] = BuildNonGenerativeMeasurements(sw.ElapsedMilliseconds),
                    ["baseUrl"] = rag.EmbeddingsBaseUrl,
                    ["model"] = rag.EmbeddingsModel,
                    ["dimension"] = dim
                },
                null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new WarmupCheckResult(
                false,
                ex.Message,
                sw.ElapsedMilliseconds,
                new Dictionary<string, object?>
                {
                    ["check"] = "tei.embeddings",
                    ["status"] = "exception",
                    ["measuredAt"] = measuredAt,
                    ["durationMs"] = sw.ElapsedMilliseconds,
                    ["measurements"] = BuildNonGenerativeMeasurements(sw.ElapsedMilliseconds),
                    ["baseUrl"] = rag.EmbeddingsBaseUrl,
                    ["model"] = rag.EmbeddingsModel,
                    ["exception"] = ex.GetType().Name
                },
                ex.Message);
        }
    }

    private static async Task<WarmupCheckResult> CheckRerankAsync(
        RagOptions rag,
        IHttpClientFactory httpFactory,
        CancellationToken ct)
    {
        var measuredAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        try
        {
            var rerankBaseUrl = string.IsNullOrWhiteSpace(rag.RerankBaseUrl) ? rag.EmbeddingsBaseUrl : rag.RerankBaseUrl!;
            var tei = httpFactory.CreateClient("tei");
            tei.BaseAddress = new Uri(rerankBaseUrl);

            var results = await TeiClient.RerankAsync(
                tei,
                rag.RerankModel,
                "retrieval warmup",
                ["warmup alpha", "warmup beta"],
                ct);
            sw.Stop();

            return new WarmupCheckResult(
                results.Count > 0,
                results.Count > 0 ? "ok" : "rerank returned no scores",
                sw.ElapsedMilliseconds,
                new Dictionary<string, object?>
                {
                    ["check"] = "tei.rerank",
                    ["status"] = results.Count > 0 ? "ok" : "empty_results",
                    ["measuredAt"] = measuredAt,
                    ["durationMs"] = sw.ElapsedMilliseconds,
                    ["measurements"] = BuildNonGenerativeMeasurements(sw.ElapsedMilliseconds),
                    ["baseUrl"] = rerankBaseUrl,
                    ["model"] = rag.RerankModel,
                    ["results"] = results.Count
                },
                results.Count > 0 ? null : "rerank returned no scores");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new WarmupCheckResult(
                false,
                ex.Message,
                sw.ElapsedMilliseconds,
                new Dictionary<string, object?>
                {
                    ["check"] = "tei.rerank",
                    ["status"] = "exception",
                    ["measuredAt"] = measuredAt,
                    ["durationMs"] = sw.ElapsedMilliseconds,
                    ["measurements"] = BuildNonGenerativeMeasurements(sw.ElapsedMilliseconds),
                    ["baseUrl"] = rag.RerankBaseUrl ?? rag.EmbeddingsBaseUrl,
                    ["model"] = rag.RerankModel,
                    ["exception"] = ex.GetType().Name
                },
                ex.Message);
        }
    }

    private static Task UpsertCapabilityStateAsync(NpgsqlConnection conn, AdminRuntimeCapabilityStateDto state, CancellationToken ct)
        => RuntimeCapabilityStateStore.UpsertAsync(conn, state, ct);

    private static async Task InsertWarmupResultAsync(NpgsqlConnection conn, AdminRuntimeWarmupResultDto result, CancellationToken ct)
    {
        const string sql = """
INSERT INTO runtime_warmup_results(
  warmup_result_id,
  capability_key,
  profile_key,
  pass_count,
  passed,
  measured_at,
  details)
VALUES(
  @warmup_result_id,
  @capability_key,
  @profile_key,
  @pass_count,
  @passed,
  @measured_at,
  CAST(@details AS jsonb));
""";

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            warmup_result_id = result.WarmupResultId,
            capability_key = result.CapabilityKey,
            profile_key = result.ProfileKey,
            pass_count = result.PassCount,
            passed = result.Passed,
            measured_at = result.MeasuredAt.UtcDateTime,
            details = JsonSerializer.Serialize(result.Details ?? new Dictionary<string, object?>())
        }, cancellationToken: ct));
    }

    internal static async Task InsertCapabilityEventAsync(NpgsqlConnection conn, AdminRuntimeCapabilityEventDto evt, CancellationToken ct)
    {
        const string sql = """
INSERT INTO runtime_capability_events(
  event_id,
  capability_key,
  profile_key,
  event_type,
  actor,
  reason,
  details,
  occurred_at)
VALUES(
  @event_id,
  @capability_key,
  @profile_key,
  @event_type,
  @actor,
  @reason,
  CAST(@details AS jsonb),
  @occurred_at);
""";

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            event_id = evt.EventId,
            capability_key = evt.CapabilityKey,
            profile_key = evt.ProfileKey,
            event_type = evt.EventType,
            actor = evt.Actor,
            reason = evt.Reason,
            details = JsonSerializer.Serialize(evt.Details ?? new Dictionary<string, object?>()),
            occurred_at = evt.OccurredAt.UtcDateTime
        }, cancellationToken: ct));

        RuntimeGovernanceTelemetry.RecordCapabilityEventWritten(evt.CapabilityKey, evt.EventType);
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

    private static Task<CapabilityBExecutionJobRow?> LoadCapabilityBExecutionJobAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        Guid jobId,
        CancellationToken ct)
        => RuntimeCapabilityBExecutionStore.LoadCapabilityBExecutionJobAsync(conn, tenantId, jobId, ct);

    private static Task<CapabilityBExecutionJobRow?> LoadNextQueuedCapabilityBExecutionJobAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        CancellationToken ct)
        => RuntimeCapabilityBExecutionStore.LoadNextQueuedCapabilityBExecutionJobAsync(conn, tenantId, ct);

    internal static Task<CapabilityBDocumentRow?> LoadCapabilityBDocumentAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        Guid docId,
        CancellationToken ct)
        => RuntimeCapabilityBExecutionStore.LoadCapabilityBDocumentAsync(conn, tenantId, docId, ct);

    internal static Task<string?> ComputeCapabilityBDocumentSourceHashAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        Guid docId,
        CancellationToken ct)
        => RuntimeCapabilityBExecutionStore.ComputeCapabilityBDocumentSourceHashAsync(conn, tenantId, docId, ct);

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

    private static AdminRuntimeCapabilityStateDto BuildDefaultState(
        RuntimeCapabilityDefinition definition,
        RuntimeGovernanceOptions options,
        RagOptions rag)
    {
        if (definition.Key == CapabilityACorpusEnrichmentKey)
        {
            var profile = RuntimeCatalogBuilder.ResolveProfile(options.DefaultProfileKey, options);
            return new AdminRuntimeCapabilityStateDto(
                Key: definition.Key,
                DisplayName: definition.DisplayName,
                Family: definition.Family,
                RuntimeKey: definition.RuntimeKey,
                Implemented: true,
                DesiredEnabled: definition.DefaultDesiredEnabled,
                Installed: true,
                Configured: !string.IsNullOrWhiteSpace(rag.QdrantBaseUrl)
                    && !string.IsNullOrWhiteSpace(rag.QdrantCollection)
                    && !string.IsNullOrWhiteSpace(rag.EmbeddingsBaseUrl)
                    && !string.IsNullOrWhiteSpace(rag.EmbeddingsModel),
                Healthy: false,
                Qualified: false,
                Authorized: false,
                Selected: false,
                ProfileKey: profile.Key,
                PassCount: 0,
                LastCheckedAt: null,
                LastQualifiedAt: null,
                LastError: null,
                Details: new Dictionary<string, object?>
                {
                    ["status"] = "candidate",
                    ["mode"] = "corpus_enrichment_admin"
                },
                Stale: false,
                QualificationFingerprint: null,
                StaleReason: null,
                QualificationAgeHours: null,
                QualificationExpiresAt: null,
                PersistedAuthorized: false,
                PersistedSelected: false,
                EffectiveAuthorized: false,
                EffectiveSelected: false);
        }

        if (definition.Key == CapabilityBBackofficeGenerationKey)
        {
            var profile = RuntimeCatalogBuilder.ResolveProfile(options.DefaultProfileKey, options);
            var backofficeEnabled = RuntimeCatalogBuilder.IsBackofficeGenerationEnabled();
            return new AdminRuntimeCapabilityStateDto(
                Key: definition.Key,
                DisplayName: definition.DisplayName,
                Family: definition.Family,
                RuntimeKey: definition.RuntimeKey,
                Implemented: true,
                DesiredEnabled: definition.DefaultDesiredEnabled,
                Installed: true,
                Configured: backofficeEnabled,
                Healthy: false,
                Qualified: false,
                Authorized: false,
                Selected: false,
                ProfileKey: profile.Key,
                PassCount: 0,
                LastCheckedAt: null,
                LastQualifiedAt: null,
                LastError: null,
                Details: new Dictionary<string, object?>
                {
                    ["status"] = backofficeEnabled ? "configured_not_checked" : "backoffice_disabled",
                    ["mode"] = "summary_generation_admin",
                    ["backofficeEnabled"] = backofficeEnabled
                },
                Stale: false,
                QualificationFingerprint: null,
                StaleReason: null,
                QualificationAgeHours: null,
                QualificationExpiresAt: null,
                PersistedAuthorized: false,
                PersistedSelected: false,
                EffectiveAuthorized: false,
                EffectiveSelected: false);
        }

        if (definition.Key == CoreRetrievalCapabilityKey)
        {
            var installed = !string.IsNullOrWhiteSpace(rag.QdrantBaseUrl)
                && !string.IsNullOrWhiteSpace(rag.EmbeddingsBaseUrl);
            var configured = installed
                && !string.IsNullOrWhiteSpace(rag.QdrantCollection)
                && !string.IsNullOrWhiteSpace(rag.EmbeddingsModel);
            var profile = RuntimeCatalogBuilder.ResolveProfile(options.DefaultProfileKey, options);

            return new AdminRuntimeCapabilityStateDto(
                Key: definition.Key,
                DisplayName: definition.DisplayName,
                Family: definition.Family,
                RuntimeKey: definition.RuntimeKey,
                Implemented: true,
                DesiredEnabled: true,
                Installed: installed,
                Configured: configured,
                Healthy: false,
                Qualified: false,
                Authorized: false,
                Selected: false,
                ProfileKey: options.DefaultProfileKey,
                PassCount: options.WarmupPassCount,
                LastCheckedAt: null,
                LastQualifiedAt: null,
                LastError: null,
                Details: new Dictionary<string, object?>
                {
                    ["status"] = configured ? "configured_not_checked" : installed ? "installed_not_checked" : "missing_dependencies",
                    ["qdrantBaseUrl"] = rag.QdrantBaseUrl,
                    ["embeddingsBaseUrl"] = rag.EmbeddingsBaseUrl,
                    ["freshnessPolicy"] = profile.FreshnessPolicy
                },
                Stale: false,
                QualificationFingerprint: null,
                StaleReason: null,
                QualificationAgeHours: null,
                QualificationExpiresAt: null,
                PersistedAuthorized: false,
                PersistedSelected: false,
                EffectiveAuthorized: false,
                EffectiveSelected: false);
        }

        return new AdminRuntimeCapabilityStateDto(
            Key: definition.Key,
            DisplayName: definition.DisplayName,
            Family: definition.Family,
            RuntimeKey: definition.RuntimeKey,
            Implemented: false,
            DesiredEnabled: definition.DefaultDesiredEnabled,
            Installed: false,
            Configured: false,
            Healthy: false,
            Qualified: false,
            Authorized: false,
            Selected: false,
            ProfileKey: options.DefaultProfileKey,
            PassCount: 0,
            LastCheckedAt: null,
            LastQualifiedAt: null,
            LastError: null,
            Details: new Dictionary<string, object?>
            {
                ["status"] = "not_implemented",
                ["note"] = definition.StatusNote
            },
            Stale: false,
            QualificationFingerprint: null,
            StaleReason: null,
            QualificationAgeHours: null,
            QualificationExpiresAt: null,
            PersistedAuthorized: false,
            PersistedSelected: false,
            EffectiveAuthorized: false,
            EffectiveSelected: false);
    }

    private static AdminRuntimeCapabilityDiagnosticDto MapCapabilityDiagnostic(
        AdminRuntimeCapabilityStateDto state,
        AdminRuntimeCapabilityOperationalSummaryDto? operationalSummary = null)
    {
        var blockers = BuildBlockers(state);
        var recommendations = BuildRecommendations(state, blockers, operationalSummary);

        return new AdminRuntimeCapabilityDiagnosticDto(
            Key: state.Key,
            DisplayName: state.DisplayName,
            Family: state.Family,
            Status: ResolveDiagnosticStatus(state),
            ProfileKey: state.ProfileKey,
            Implemented: state.Implemented,
            Stale: state.Stale,
            Qualified: state.Qualified,
            Authorized: state.Authorized,
            Selected: state.Selected,
            PersistedAuthorized: state.PersistedAuthorized,
            PersistedSelected: state.PersistedSelected,
            StaleReason: state.StaleReason,
            QualificationAgeHours: state.QualificationAgeHours,
            QualificationExpiresAt: state.QualificationExpiresAt,
            Blockers: blockers,
            Recommendations: recommendations,
            LastCheckedAt: state.LastCheckedAt,
            LastQualifiedAt: state.LastQualifiedAt,
            LastError: state.LastError,
            OperationalSummary: operationalSummary);
    }

    private static AdminRuntimeCapabilityStateDto ProjectCapabilityState(
        RuntimeCapabilityDefinition definition,
        AdminRuntimeCapabilityStateDto state,
        RuntimeGovernanceOptions options,
        RagOptions rag)
        => RuntimeCapabilityStateProjector.ProjectState(
            definition.Key,
            definition.Implemented,
            state,
            options,
            rag);

    private static AdminRuntimeWarmupResultDto MapWarmupResultRow(WarmupResultRow row)
        => new(
            row.WarmupResultId,
            row.CapabilityKey,
            row.ProfileKey,
            row.PassCount,
            row.Passed,
            row.MeasuredAt,
            ParseDetails(row.DetailsJson));

    private static AdminRuntimeCapabilityEventDto MapCapabilityEventRow(CapabilityEventRow row)
        => new(
            row.EventId,
            row.CapabilityKey,
            row.ProfileKey,
            row.EventType,
            row.Actor,
            row.Reason,
            row.OccurredAt,
            ParseDetails(row.DetailsJson));

    internal static IReadOnlyList<AdminRuntimeCapabilityCatalogDto> GetCapabilityCatalog()
        => BuildCapabilityCatalog();

    private static IReadOnlyList<AdminRuntimeCapabilityCatalogDto> BuildCapabilityCatalog()
        => RuntimeCapabilities.Select(static def => new AdminRuntimeCapabilityCatalogDto(
            def.Key,
            def.DisplayName,
            def.Family,
            def.RuntimeKey,
            def.Implemented,
            def.DefaultDesiredEnabled,
            def.StatusNote)).ToArray();

    private static IReadOnlyList<RuntimeCapabilityDefinition> ResolveCapabilitySelection(string? capabilityKey)
    {
        if (string.IsNullOrWhiteSpace(capabilityKey))
            return RuntimeCapabilities;

        var match = RuntimeCapabilities.FirstOrDefault(def => string.Equals(def.Key, capabilityKey.Trim(), StringComparison.OrdinalIgnoreCase));
        return match is null ? RuntimeCapabilities : [match];
    }

    private static IReadOnlyDictionary<string, object?>? ParseDetails(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        using var doc = JsonDocument.Parse(json);
        return ConvertObject(doc.RootElement);
    }

    private static Dictionary<string, object?> ConvertObject(JsonElement element)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var property in element.EnumerateObject())
            result[property.Name] = ConvertValue(property.Value);

        return result;
    }

    private static object? ConvertValue(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Object => ConvertObject(element),
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertValue).ToArray(),
            JsonValueKind.String => element.TryGetDateTimeOffset(out var dto) ? dto : element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };

    private static async Task<string> TryReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static WarmupCheckResult BuildSkippedWarmupCheck(string checkName, IReadOnlyDictionary<string, object?> extraDetails)
    {
        var details = new Dictionary<string, object?>(extraDetails, StringComparer.Ordinal)
        {
            ["check"] = checkName,
            ["status"] = "skipped",
            ["measuredAt"] = DateTimeOffset.UtcNow,
            ["durationMs"] = 0L,
            ["measurements"] = BuildSkippedMeasurements()
        };

        return new WarmupCheckResult(true, "skipped", 0L, details, null);
    }

    private static PerformanceBudgetResult EvaluatePerformanceBudget(
        AdminRuntimeWarmupProfileDto profile,
        WarmupCheckResult qdrantCheck,
        WarmupCheckResult embeddingsCheck,
        WarmupCheckResult rerankCheck,
        long totalDurationMs,
        bool rerankEnabled)
    {
        if (profile.CheckPolicy is { EnforcePerformanceBudgets: false })
        {
            return new PerformanceBudgetResult(
                true,
                new Dictionary<string, object?>(),
                Array.Empty<string>(),
                null);
        }

        var budgets = profile.PerformanceBudgets;
        if (budgets is null)
        {
            return new PerformanceBudgetResult(
                true,
                new Dictionary<string, object?>(),
                Array.Empty<string>(),
                null);
        }

        var violations = new List<string>();

        if (budgets.MaxPassDurationMs is long maxPass && totalDurationMs > maxPass)
            violations.Add($"pass_duration_ms>{maxPass}");
        if (budgets.MaxQdrantCheckMs is long maxQdrant && qdrantCheck.DurationMs > maxQdrant)
            violations.Add($"qdrant_duration_ms>{maxQdrant}");
        if (budgets.MaxEmbeddingsCheckMs is long maxEmbeddings && embeddingsCheck.DurationMs > maxEmbeddings)
            violations.Add($"tei_embeddings_duration_ms>{maxEmbeddings}");
        if (rerankEnabled && budgets.MaxRerankCheckMs is long maxRerank && rerankCheck.DurationMs > maxRerank)
            violations.Add($"tei_rerank_duration_ms>{maxRerank}");

        var budgetSnapshot = new Dictionary<string, object?>
        {
            ["maxPassDurationMs"] = budgets.MaxPassDurationMs,
            ["maxQdrantCheckMs"] = budgets.MaxQdrantCheckMs,
            ["maxEmbeddingsCheckMs"] = budgets.MaxEmbeddingsCheckMs,
            ["maxRerankCheckMs"] = budgets.MaxRerankCheckMs
        };

        return violations.Count == 0
            ? new PerformanceBudgetResult(true, budgetSnapshot, Array.Empty<string>(), null)
            : new PerformanceBudgetResult(false, budgetSnapshot, violations.ToArray(), $"performance budget failed: {string.Join(", ", violations)}");
    }

    private static ProfilePolicyResult EvaluateProfilePolicy(AdminRuntimeWarmupProfileDto profile, RagOptions rag)
    {
        var policy = profile.CheckPolicy ?? new AdminRuntimeWarmupCheckPolicyDto(false, true, true);
        var requirements = profile.RuntimeRequirements ?? new AdminRuntimeWarmupRuntimeRequirementsDto(
            RequireQdrant: true,
            RequireEmbeddings: true,
            RequireRerank: false,
            RequireCollection: true,
            RequireEmbeddingsModel: true,
            RequireRerankModel: false);
        var violations = new List<string>();

        if (policy.RequireRerankEnabled && !rag.EnableRerank)
            violations.Add("rerank_required_but_disabled");
        if (requirements.RequireQdrant && string.IsNullOrWhiteSpace(rag.QdrantBaseUrl))
            violations.Add("qdrant_required_but_missing");
        if (requirements.RequireEmbeddings && string.IsNullOrWhiteSpace(rag.EmbeddingsBaseUrl))
            violations.Add("embeddings_required_but_missing");
        if (requirements.RequireRerank && string.IsNullOrWhiteSpace(rag.RerankBaseUrl) && string.IsNullOrWhiteSpace(rag.EmbeddingsBaseUrl))
            violations.Add("rerank_runtime_required_but_missing");
        if (requirements.RequireCollection && string.IsNullOrWhiteSpace(rag.QdrantCollection))
            violations.Add("qdrant_collection_required_but_missing");
        if (requirements.RequireEmbeddingsModel && string.IsNullOrWhiteSpace(rag.EmbeddingsModel))
            violations.Add("embeddings_model_required_but_missing");
        if (requirements.RequireRerankModel && string.IsNullOrWhiteSpace(rag.RerankModel))
            violations.Add("rerank_model_required_but_missing");

        var details = new Dictionary<string, object?>
        {
            ["requireRerankEnabled"] = policy.RequireRerankEnabled,
            ["allowSkippedChecks"] = policy.AllowSkippedChecks,
            ["enforcePerformanceBudgets"] = policy.EnforcePerformanceBudgets,
            ["runtimeRequirements"] = new Dictionary<string, object?>
            {
                ["requireQdrant"] = requirements.RequireQdrant,
                ["requireEmbeddings"] = requirements.RequireEmbeddings,
                ["requireRerank"] = requirements.RequireRerank,
                ["requireCollection"] = requirements.RequireCollection,
                ["requireEmbeddingsModel"] = requirements.RequireEmbeddingsModel,
                ["requireRerankModel"] = requirements.RequireRerankModel
            },
            ["rerankEnabled"] = rag.EnableRerank,
            ["violations"] = violations.ToArray()
        };

        return violations.Count == 0
            ? new ProfilePolicyResult(true, details, null)
            : new ProfilePolicyResult(false, details, $"profile policy failed: {string.Join(", ", violations)}");
    }

    private static RuntimeSpecificGateResult EvaluateRuntimeSpecificGates(
        AdminRuntimeWarmupProfileDto profile,
        RuntimeGovernanceOptions options,
        RagOptions rag)
    {
        var requirements = profile.RuntimeRequirements ?? new AdminRuntimeWarmupRuntimeRequirementsDto(
            RequireQdrant: true,
            RequireEmbeddings: true,
            RequireRerank: false,
            RequireCollection: true,
            RequireEmbeddingsModel: true,
            RequireRerankModel: false);

        var activeRerank = rag.EnableRerank || requirements.RequireRerank || requirements.RequireRerankModel;

        var qdrantRequirements = MergeHardwareRequirements(
            profile.HardwareRequirements,
            options.QdrantRuntimeMinCpuCores,
            options.QdrantRuntimeMinAvailableMemoryMb,
            options.Require64BitProcess);
        var embeddingsRequirements = MergeHardwareRequirements(
            profile.HardwareRequirements,
            options.EmbeddingsRuntimeMinCpuCores,
            options.EmbeddingsRuntimeMinAvailableMemoryMb,
            options.Require64BitProcess);
        var rerankRequirements = MergeHardwareRequirements(
            profile.HardwareRequirements,
            options.RerankRuntimeMinCpuCores,
            options.RerankRuntimeMinAvailableMemoryMb,
            options.Require64BitProcess);

        var qdrantGate = EvaluateHardwareGate(qdrantRequirements, options);
        var embeddingsGate = EvaluateHardwareGate(embeddingsRequirements, options);
        var rerankGate = activeRerank
            ? EvaluateHardwareGate(rerankRequirements, options)
            : new HardwareGateResult(
                true,
                new Dictionary<string, object?>
                {
                    ["status"] = "skipped",
                    ["active"] = false,
                    ["requirements"] = new Dictionary<string, object?>
                    {
                        ["minCpuCores"] = rerankRequirements.MinCpuCores,
                        ["minAvailableMemoryMb"] = rerankRequirements.MinAvailableMemoryMb,
                        ["require64BitProcess"] = rerankRequirements.Require64BitProcess
                    }
                },
                null);

        var failures = new List<string>();
        if (!qdrantGate.Passed)
            failures.Add("qdrant_runtime_gate_failed");
        if (!embeddingsGate.Passed)
            failures.Add("embeddings_runtime_gate_failed");
        if (!rerankGate.Passed)
            failures.Add("rerank_runtime_gate_failed");

        var details = new Dictionary<string, object?>
        {
            ["qdrant"] = qdrantGate.Details,
            ["teiEmbeddings"] = embeddingsGate.Details,
            ["teiRerank"] = rerankGate.Details,
            ["failures"] = failures.ToArray()
        };

        return failures.Count == 0
            ? new RuntimeSpecificGateResult(true, details, null)
            : new RuntimeSpecificGateResult(false, details, $"runtime gate failed: {string.Join(", ", failures)}");
    }

    private static AdminRuntimeHardwareRequirementsDto MergeHardwareRequirements(
        AdminRuntimeHardwareRequirementsDto? profileRequirements,
        int runtimeMinCpuCores,
        long runtimeMinAvailableMemoryMb,
        bool runtimeRequire64Bit)
        => new(
            MinCpuCores: Math.Max(profileRequirements?.MinCpuCores ?? 1, runtimeMinCpuCores),
            MinAvailableMemoryMb: Math.Max(profileRequirements?.MinAvailableMemoryMb ?? 1, runtimeMinAvailableMemoryMb),
            Require64BitProcess: (profileRequirements?.Require64BitProcess ?? false) || runtimeRequire64Bit);

    private static string ResolveDiagnosticStatus(AdminRuntimeCapabilityStateDto state)
    {
        if (!state.Implemented)
            return "not_implemented";
        if (state.Stale)
            return "stale";
        if (state.Selected)
            return "selected";
        if (state.Authorized)
            return "authorized";
        if (state.Qualified)
            return "qualified";
        if (state.Healthy)
            return "healthy";
        if (state.Configured)
            return "configured";
        if (state.Installed)
            return "installed";
        return "missing_dependencies";
    }

    private static string[] BuildBlockers(AdminRuntimeCapabilityStateDto state)
    {
        var blockers = new List<string>();

        if (!state.Implemented)
            blockers.Add("not_implemented");
        if (!state.Installed)
            blockers.Add("missing_dependencies");
        else if (!state.Configured)
            blockers.Add("not_configured");
        if (ContainsError(state, "hardware gate failed"))
            blockers.Add("hardware_gate_failed");
        if (ContainsError(state, "runtime gate failed"))
            blockers.Add("runtime_gate_failed");
        if (state.Stale)
            blockers.Add("stale_qualification");
        if (ContainsError(state, "performance budget failed"))
            blockers.Add("performance_budget_failed");
        if (ContainsError(state, "profile policy failed"))
            blockers.Add("profile_policy_failed");
        if (state.Configured && !state.Healthy && state.Implemented)
            blockers.Add("warmup_failed");
        if (state.Qualified && !state.Authorized)
            blockers.Add("not_authorized");
        if (state.Authorized && !state.Selected && state.DesiredEnabled)
            blockers.Add("not_selected");

        return blockers.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string[] BuildRecommendations(
        AdminRuntimeCapabilityStateDto state,
        IReadOnlyList<string> blockers,
        AdminRuntimeCapabilityOperationalSummaryDto? operationalSummary = null)
    {
        var recommendations = new List<string>();

        if (blockers.Contains("missing_dependencies", StringComparer.Ordinal))
            recommendations.Add("configure qdrant/tei endpoints and collection settings");
        if (blockers.Contains("not_configured", StringComparer.Ordinal))
            recommendations.Add("complete retrieval runtime configuration before requalifying");
        if (blockers.Contains("hardware_gate_failed", StringComparer.Ordinal))
            recommendations.Add("relax hardware gate thresholds or move qualification to a stronger host profile");
        if (blockers.Contains("runtime_gate_failed", StringComparer.Ordinal))
            recommendations.Add("align runtime-specific hardware thresholds with the selected profile or qualify on a host sized for qdrant/tei/rerank");
        if (blockers.Contains("stale_qualification", StringComparer.Ordinal))
            recommendations.Add("requalify this capability because its stored qualification no longer matches the active profile or runtime configuration");
        if (blockers.Contains("performance_budget_failed", StringComparer.Ordinal))
            recommendations.Add("use a less strict profile or improve runtime latency before selecting this capability");
        if (blockers.Contains("profile_policy_failed", StringComparer.Ordinal))
            recommendations.Add("pick a compatible profile or enable the required runtime features before requalifying");
        if (blockers.Contains("warmup_failed", StringComparer.Ordinal))
            recommendations.Add("inspect warmup check details and rerun qualification after runtime recovery");
        if (blockers.Contains("not_authorized", StringComparer.Ordinal))
            recommendations.Add("authorize the qualified capability before selecting it");
        if (blockers.Contains("not_selected", StringComparer.Ordinal))
            recommendations.Add("select the authorized capability if it should be active");
        if (!state.Implemented)
            recommendations.Add("keep this capability disabled until a real implementation exists");
        if (recommendations.Count == 0
            && state.Selected
            && string.Equals(state.Key, CapabilityACorpusEnrichmentKey, StringComparison.Ordinal))
        {
            recommendations.Add("review capability A semantic previews and enqueue controlled reindex jobs when appropriate");
        }

        if (recommendations.Count == 0
            && state.Selected
            && string.Equals(state.Key, CapabilityBBackofficeGenerationKey, StringComparison.Ordinal))
        {
            if (operationalSummary is not null && operationalSummary.CandidateCount > 0)
            {
                recommendations.Add(
                    $"review capability B candidates: {operationalSummary.ReadyToEnqueueCount} ready to enqueue, {operationalSummary.BlockedByActiveJobCount} blocked by active summary jobs, {operationalSummary.BlockedByCooldownCount} blocked by recent failure/cancellation cooldowns");
            }

            if (operationalSummary is not null && operationalSummary.ActiveCapabilityJobCount > 0)
            {
                recommendations.Add("monitor active capability B summary jobs and the latest campaign progress from diagnostics");
            }

            if (operationalSummary is not null
                && operationalSummary.CandidateCount == 0
                && operationalSummary.ActiveCapabilityJobCount == 0)
            {
                recommendations.Add("backoffice summary backlog is currently clear");
            }
        }

        if (recommendations.Count == 0
            && state.Selected
            && string.Equals(state.Key, CapabilityBBackofficeGenerationKey, StringComparison.Ordinal))
        {
            recommendations.Add("review capability B candidates and enqueue governed backoffice summary generation jobs when appropriate");
        }

        if (recommendations.Count == 0 && state.Selected)
            recommendations.Add("runtime is ready for the nominal path");

        return recommendations.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static bool ContainsError(AdminRuntimeCapabilityStateDto state, string fragment)
        => !string.IsNullOrWhiteSpace(state.LastError)
           && state.LastError.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, object?> BuildRuntimeEnvironmentSnapshot()
        => new Dictionary<string, object?>
        {
            ["machineName"] = Environment.MachineName,
            ["osVersion"] = Environment.OSVersion.VersionString,
            ["processArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["frameworkDescription"] = RuntimeInformation.FrameworkDescription,
            ["processorCount"] = Environment.ProcessorCount,
            ["is64BitProcess"] = Environment.Is64BitProcess
        };

    private static IReadOnlyDictionary<string, object?> BuildMeasurementSemanticsSummary()
        => new Dictionary<string, object?>
        {
            ["loadTimeMs"] = "measured",
            ["qdrantLoadTimeMs"] = "measured",
            ["embeddingsLoadTimeMs"] = "measured",
            ["rerankLoadTimeMs"] = "measured_if_rerank_enabled_otherwise_null",
            ["ttftMs"] = "not_applicable_for_retrieval_runtime",
            ["tokensPerSecond"] = "not_applicable_for_retrieval_runtime"
        };

    private static IReadOnlyDictionary<string, object?> BuildWarmupPassMeasurements(
        long totalDurationMs,
        long qdrantDurationMs,
        long embeddingsDurationMs,
        long? rerankDurationMs)
        => new Dictionary<string, object?>
        {
            ["loadTimeMs"] = totalDurationMs,
            ["qdrantLoadTimeMs"] = qdrantDurationMs,
            ["embeddingsLoadTimeMs"] = embeddingsDurationMs,
            ["rerankLoadTimeMs"] = rerankDurationMs,
            ["ttftMs"] = null,
            ["tokensPerSecond"] = null,
            ["applicability"] = BuildMeasurementSemanticsSummary()
        };

    private static IReadOnlyDictionary<string, object?> BuildNonGenerativeMeasurements(long durationMs)
        => new Dictionary<string, object?>
        {
            ["loadTimeMs"] = durationMs,
            ["qdrantLoadTimeMs"] = null,
            ["embeddingsLoadTimeMs"] = null,
            ["rerankLoadTimeMs"] = null,
            ["ttftMs"] = null,
            ["tokensPerSecond"] = null,
            ["applicability"] = BuildMeasurementSemanticsSummary()
        };

    private static IReadOnlyDictionary<string, object?> BuildSkippedMeasurements()
        => new Dictionary<string, object?>
        {
            ["loadTimeMs"] = 0L,
            ["qdrantLoadTimeMs"] = null,
            ["embeddingsLoadTimeMs"] = null,
            ["rerankLoadTimeMs"] = null,
            ["ttftMs"] = null,
            ["tokensPerSecond"] = null,
            ["applicability"] = new Dictionary<string, object?>
            {
                ["loadTimeMs"] = "skipped",
                ["qdrantLoadTimeMs"] = "not_applicable_for_skipped_check",
                ["embeddingsLoadTimeMs"] = "not_applicable_for_skipped_check",
                ["rerankLoadTimeMs"] = "not_applicable_for_skipped_check",
                ["ttftMs"] = "not_applicable_for_retrieval_runtime",
                ["tokensPerSecond"] = "not_applicable_for_retrieval_runtime"
            }
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

    private static readonly RuntimeCapabilityDefinition[] RuntimeCapabilities =
    [
        new(CoreRetrievalCapabilityKey, "Core retrieval", "core", "retrieval-stack", true, true, "Dense/sparse/exact retrieval stack qualified against Qdrant and TEI."),
        new(CapabilityACorpusEnrichmentKey, "Capability A - Corpus Enrichment", "A", "server-capability-a", true, false, "Capability A plans corpus enrichment candidates and enqueues controlled reindex jobs through the ingestion pipeline."),
        new(CapabilityBBackofficeGenerationKey, "Capability B - Backoffice Generation", "B", "server-capability-b", true, false, "Capability B plans and enqueues governed backoffice summary generation jobs through the admin summary pipeline."),
        new("capability_c.retrieval_intelligence", "Capability C - Retrieval Intelligence", "C", "server-capability-c", false, false, "Capability C remains intentionally unimplemented in v3.0 backend.")
    ];

    private sealed record RuntimeCapabilityDefinition(
        string Key,
        string DisplayName,
        string Family,
        string RuntimeKey,
        bool Implemented,
        bool DefaultDesiredEnabled,
        string StatusNote);

    private sealed record CapabilityEvaluation(
        AdminRuntimeCapabilityStateDto State,
        AdminRuntimeWarmupResultDto? WarmupResult);

    internal sealed record CapabilitySelectionUpdateResult(
        AdminRuntimeCapabilityStateDto? State,
        string? Error);

    private sealed record CapabilityGateResult(
        AdminRuntimeCapabilityStateDto? State,
        string? Error);

    private sealed record WarmupPassResult(
        bool Passed,
        DateTimeOffset MeasuredAt,
        IReadOnlyDictionary<string, object?> Details,
        string? Error);

    private sealed record WarmupCheckResult(
        bool Passed,
        string Status,
        long DurationMs,
        IReadOnlyDictionary<string, object?> Details,
        string? Error);

    private sealed record PerformanceBudgetResult(
        bool Passed,
        IReadOnlyDictionary<string, object?> Budgets,
        IReadOnlyList<string> Violations,
        string? Error);

    private sealed record ProfilePolicyResult(
        bool Passed,
        IReadOnlyDictionary<string, object?> Details,
        string? Error);

    private sealed record WarmupResultRow(
        Guid WarmupResultId,
        string CapabilityKey,
        string ProfileKey,
        int PassCount,
        bool Passed,
        DateTimeOffset MeasuredAt,
        string? DetailsJson);

    private sealed record CapabilityEventRow(
        Guid EventId,
        string CapabilityKey,
        string? ProfileKey,
        string EventType,
        string Actor,
        string? Reason,
        DateTimeOffset OccurredAt,
        string? DetailsJson);

    private sealed record CapabilityBBackofficeCandidateRow(
        Guid DocId,
        string DocPath,
        string DocName,
        string Category,
        string SummaryState,
        bool HasActiveJob,
        string? LastJobStatus,
        DateTimeOffset? LastJobFinishedAt,
        string? LastJobError);

    private sealed record CapabilityBCampaignOperationalRow(
        Guid CampaignId,
        string EventType,
        DateTimeOffset OccurredAt);

    private sealed record CapabilityBCampaignJobAggregateRow(
        Guid CampaignId,
        int ActiveJobCount,
        int TerminalJobCount,
        int StoredSummaryCount);

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

    private static async Task<AdminRuntimeWarmupResultDto[]> LoadWarmupResultsAsync(
        NpgsqlConnection conn,
        string? capabilityKey,
        int limit,
        CancellationToken ct)
        => (await conn.QueryAsync<WarmupResultRow>(new CommandDefinition("""
SELECT
  warmup_result_id AS "WarmupResultId",
  capability_key AS "CapabilityKey",
  profile_key AS "ProfileKey",
  pass_count AS "PassCount",
  passed AS "Passed",
  measured_at AS "MeasuredAt",
  details AS "DetailsJson"
FROM runtime_warmup_results
WHERE (@capability_key IS NULL OR capability_key = @capability_key)
ORDER BY measured_at DESC
LIMIT @limit;
""", new { capability_key = string.IsNullOrWhiteSpace(capabilityKey) ? null : capabilityKey.Trim(), limit }, cancellationToken: ct)))
            .Select(MapWarmupResultRow)
            .ToArray();

    private static async Task<CapabilityGateResult> EnsureCapabilityReadyAsync(
        NpgsqlConnection conn,
        string capabilityKey,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        CancellationToken ct)
    {
        var definition = RuntimeCapabilities.FirstOrDefault(def => string.Equals(def.Key, capabilityKey, StringComparison.Ordinal));
        if (definition is null)
            return new CapabilityGateResult(null, "unknown capability");

        var existingStates = await RuntimeCapabilityStateStore.LoadPersistedStatesAsync(conn, ct);
        var current = existingStates.TryGetValue(definition.Key, out var row)
            ? ProjectCapabilityState(definition, row, options, rag)
            : BuildDefaultState(definition, options, rag);

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

    private static async Task<AdminRuntimeCapabilityEventDto[]> LoadCapabilityEventsAsync(
        NpgsqlConnection conn,
        string? capabilityKey,
        int limit,
        CancellationToken ct)
        => (await conn.QueryAsync<CapabilityEventRow>(new CommandDefinition("""
SELECT
  event_id AS "EventId",
  capability_key AS "CapabilityKey",
  profile_key AS "ProfileKey",
  event_type AS "EventType",
  actor AS "Actor",
  reason AS "Reason",
  occurred_at AS "OccurredAt",
  details AS "DetailsJson"
FROM runtime_capability_events
WHERE (@capability_key IS NULL OR capability_key = @capability_key)
ORDER BY occurred_at DESC
LIMIT @limit;
""", new { capability_key = string.IsNullOrWhiteSpace(capabilityKey) ? null : capabilityKey.Trim(), limit }, cancellationToken: ct)))
            .Select(MapCapabilityEventRow)
            .ToArray();

    private static async Task<AdminRuntimeCapabilityBBackofficeCandidateDto[]> LoadCapabilityBBackofficeCandidatesAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string? category,
        int limit,
        RuntimeGovernanceOptions options,
        CancellationToken ct)
    {
        var categoryPath = await Endpoints.SummaryCategoryScopeResolver.ResolveScopeAsync(
            conn,
            tenantId,
            Endpoints.SummaryCategoryScopeResolver.NormalizeCategoryPathOrNull(category),
            Endpoints.SummaryCategoryScopeResolver.NormalizeCategoryRefOrNull(category),
            ct);

        var rows = await LoadCapabilityBBackofficeCandidateRowsAsync(
            conn,
            tenantId,
            categoryPath,
            Math.Clamp(limit, 1, 500),
            ct);

        return rows
            .Select(row => MapCapabilityBBackofficeCandidate(row, options))
            .OrderByDescending(candidate => candidate.PriorityScore)
            .ThenBy(candidate => candidate.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.DocPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static async Task<IReadOnlyDictionary<Guid, AdminRuntimeCapabilityBBackofficeCandidateDto>> LoadCapabilityBBackofficeCandidateLookupAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string? categoryPath,
        RuntimeGovernanceOptions options,
        CancellationToken ct)
    {
        var rows = await LoadCapabilityBBackofficeCandidateRowsAsync(
            conn,
            tenantId,
            categoryPath,
            limit: null,
            ct);

        return rows
            .Select(row => MapCapabilityBBackofficeCandidate(row, options))
            .GroupBy(candidate => candidate.DocId)
            .ToDictionary(group => group.Key, group => group.First());
    }

    private static async Task<CapabilityBBackofficeCandidateRow[]> LoadCapabilityBBackofficeCandidateRowsAsync(
        NpgsqlConnection conn,
        Guid? tenantId,
        string? categoryPath,
        int? limit,
        CancellationToken ct)
        => (await conn.QueryAsync<CapabilityBBackofficeCandidateRow>(new CommandDefinition(
            $"""
SELECT
  d.doc_id AS "DocId",
  d.doc_path AS "DocPath",
  d.doc_name AS "DocName",
  d.category AS "Category",
  CASE
    WHEN s.source_hash IS NULL THEN 'missing'
    WHEN s.source_hash <> COALESCE(encode(d.content_hash, 'hex'), md5(COALESCE(d.doc_path,'') || '|' || COALESCE(d.file_size::text,'') || '|' || COALESCE(d.file_mtime::text,''))) THEN 'stale'
    ELSE 'fresh'
  END AS "SummaryState",
  EXISTS (
    SELECT 1
    FROM admin_jobs a
    WHERE a.tenant_id = d.tenant_id
      AND a.doc_id = d.doc_id
      AND a.job_type IN ('summary.request','summary.generate')
      AND a.status IN ('queued','running','paused')
  ) AS "HasActiveJob",
  latest.status AS "LastJobStatus",
  latest.last_job_finished_at AS "LastJobFinishedAt",
  latest.last_error AS "LastJobError"
FROM documents d
LEFT JOIN document_summaries s
  ON s.tenant_id = d.tenant_id
 AND s.doc_id = d.doc_id
 AND s.level = 'medium'
LEFT JOIN LATERAL (
  SELECT
    a.status,
    COALESCE(a.finished_at, a.canceled_at, a.created_at) AS last_job_finished_at,
    a.last_error
  FROM admin_jobs a
  WHERE a.tenant_id = d.tenant_id
    AND a.doc_id = d.doc_id
    AND a.job_type IN ('summary.request','summary.generate')
  ORDER BY COALESCE(a.finished_at, a.canceled_at, a.created_at) DESC, a.created_at DESC
  LIMIT 1
) latest ON TRUE
WHERE (@tenant IS NULL OR d.tenant_id = @tenant)
  AND d.status='indexed'
  AND (@categoryPath IS NULL OR d.doc_path LIKE (@categoryPath || '/%'))
  AND (
    s.source_hash IS NULL
    OR s.source_hash <> COALESCE(encode(d.content_hash, 'hex'), md5(COALESCE(d.doc_path,'') || '|' || COALESCE(d.file_size::text,'') || '|' || COALESCE(d.file_mtime::text,'')))
  )
ORDER BY d.updated_at DESC
{(limit.HasValue ? "LIMIT @limit;" : ";")}
""",
            new { tenant = tenantId, categoryPath, limit },
            cancellationToken: ct))).ToArray();

    private static AdminRuntimeCapabilityBBackofficeCandidateDto MapCapabilityBBackofficeCandidate(
        CapabilityBBackofficeCandidateRow row,
        RuntimeGovernanceOptions options)
    {
        var reasons = new List<string>();
        if (string.Equals(row.SummaryState, "missing", StringComparison.OrdinalIgnoreCase))
            reasons.Add("summary_missing");
        if (string.Equals(row.SummaryState, "stale", StringComparison.OrdinalIgnoreCase))
            reasons.Add("summary_stale");
        if (row.HasActiveJob)
            reasons.Add("summary_job_active");

        var cooldownReason = ResolveCapabilityBCooldownReason(row, options);
        if (string.Equals(cooldownReason, "recent_summary_job_failure", StringComparison.Ordinal))
            reasons.Add("recent_summary_failure");
        else if (string.Equals(cooldownReason, "recent_summary_job_cancellation", StringComparison.Ordinal))
            reasons.Add("recent_summary_cancellation");

        var recommendedAction = row.HasActiveJob
            ? "review_active_summary_job"
            : string.Equals(cooldownReason, "recent_summary_job_failure", StringComparison.Ordinal)
                ? "inspect_recent_summary_failure"
                : string.Equals(cooldownReason, "recent_summary_job_cancellation", StringComparison.Ordinal)
                    ? "review_recent_summary_cancellation"
                    : "enqueue_summary_generation";

        return new AdminRuntimeCapabilityBBackofficeCandidateDto(
            row.DocId,
            row.DocPath,
            row.DocName,
            row.Category,
            row.SummaryState,
            row.HasActiveJob,
            recommendedAction,
            reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            PriorityScore: BuildCapabilityBPriorityScore(row, cooldownReason),
            PolicyBlocked: row.HasActiveJob || !string.IsNullOrWhiteSpace(cooldownReason),
            PolicyBlockReason: row.HasActiveJob ? "active_summary_job_exists" : cooldownReason,
            LastJobStatus: row.LastJobStatus,
            LastJobFinishedAt: row.LastJobFinishedAt,
            LastJobError: row.LastJobError);
    }

    private static string? ResolveCapabilityBCooldownReason(
        CapabilityBBackofficeCandidateRow row,
        RuntimeGovernanceOptions options)
    {
        if (!row.LastJobFinishedAt.HasValue || string.IsNullOrWhiteSpace(row.LastJobStatus))
            return null;

        var status = row.LastJobStatus.Trim();
        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
            && row.LastJobFinishedAt.Value >= DateTimeOffset.UtcNow.AddHours(-Math.Abs(options.CapabilityBRecentFailureCooldownHours)))
        {
            return "recent_summary_job_failure";
        }

        if ((string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase)
             || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase))
            && row.LastJobFinishedAt.Value >= DateTimeOffset.UtcNow.AddHours(-Math.Abs(options.CapabilityBRecentCancellationCooldownHours)))
        {
            return "recent_summary_job_cancellation";
        }

        return null;
    }

    private static int BuildCapabilityBPriorityScore(
        CapabilityBBackofficeCandidateRow row,
        string? cooldownReason)
    {
        var score = string.Equals(row.SummaryState, "missing", StringComparison.OrdinalIgnoreCase) ? 200 : 120;

        if (row.HasActiveJob)
            score -= 180;

        if (string.Equals(cooldownReason, "recent_summary_job_failure", StringComparison.Ordinal))
            score -= 120;
        else if (string.Equals(cooldownReason, "recent_summary_job_cancellation", StringComparison.Ordinal))
            score -= 80;

        return score;
    }

    private static async Task<AdminRuntimeCapabilityOperationalSummaryDto> LoadCapabilityBOperationalSummaryAsync(
        NpgsqlConnection conn,
        RuntimeGovernanceOptions options,
        CancellationToken ct)
    {
        var candidateRows = await LoadCapabilityBBackofficeCandidateRowsAsync(
            conn,
            tenantId: null,
            categoryPath: null,
            limit: null,
            ct);
        var candidates = candidateRows.Select(row => MapCapabilityBBackofficeCandidate(row, options)).ToArray();

        var candidateCount = candidates.Length;
        var readyToEnqueueCount = candidates.Count(static candidate => !candidate.HasActiveJob && string.IsNullOrWhiteSpace(candidate.PolicyBlockReason));
        var blockedByActiveJobCount = candidates.Count(static candidate => candidate.HasActiveJob);
        var blockedByCooldownCount = candidates.Count(static candidate =>
            string.Equals(candidate.PolicyBlockReason, "recent_summary_job_failure", StringComparison.Ordinal)
            || string.Equals(candidate.PolicyBlockReason, "recent_summary_job_cancellation", StringComparison.Ordinal));

        var activeCapabilityJobCount = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            """
SELECT COUNT(*)::int
FROM admin_jobs
WHERE payload ->> 'source' = 'capability_b'
  AND job_type = 'summary.generate'
  AND status IN ('queued', 'running', 'paused');
""",
            cancellationToken: ct));

        var campaignRows = (await conn.QueryAsync<CapabilityBCampaignOperationalRow>(new CommandDefinition(
            """
SELECT
  CAST(details ->> 'campaignId' AS uuid) AS "CampaignId",
  event_type AS "EventType",
  occurred_at AS "OccurredAt"
FROM runtime_capability_events
WHERE capability_key = @capabilityKey
  AND event_type IN ('capability_b_campaign_dry_run', 'capability_b_campaign_executed')
  AND details ? 'campaignId'
ORDER BY occurred_at DESC;
""",
            new { capabilityKey = CapabilityBBackofficeGenerationKey },
            cancellationToken: ct))).ToArray();

        var latestCampaignRow = campaignRows.FirstOrDefault();
        var totalCampaignCount = campaignRows.Length;

        var campaignStates = campaignRows.Length == 0
            ? Array.Empty<CapabilityBCampaignJobAggregateRow>()
            : (await conn.QueryAsync<CapabilityBCampaignJobAggregateRow>(new CommandDefinition(
                """
SELECT
  CAST(a.payload ->> 'campaignId' AS uuid) AS "CampaignId",
  SUM(CASE WHEN a.status IN ('queued', 'running', 'paused') THEN 1 ELSE 0 END)::int AS "ActiveJobCount",
  SUM(CASE WHEN a.status IN ('done', 'failed', 'canceled', 'cancelled') THEN 1 ELSE 0 END)::int AS "TerminalJobCount",
  SUM(
    CASE
      WHEN jsonb_typeof(a.result->'stored')='boolean' AND (a.result->>'stored')::boolean THEN 1
      ELSE 0
    END
  )::int AS "StoredSummaryCount"
FROM admin_jobs a
WHERE a.payload ->> 'source' = 'capability_b'
  AND a.payload ? 'campaignId'
GROUP BY CAST(a.payload ->> 'campaignId' AS uuid);
""",
                cancellationToken: ct))).ToArray();

        var campaignStateLookup = campaignStates.ToDictionary(static row => row.CampaignId);
        var activeCampaignCount = campaignStates.Count(static row => row.ActiveJobCount > 0);
        var terminalCapabilityJobCount = campaignStates.Sum(static row => row.TerminalJobCount);
        var storedSummaryCount = campaignStates.Sum(static row => row.StoredSummaryCount);

        int? latestCampaignProgressPercent = null;
        string? latestCampaignStatus = null;
        Guid? latestCampaignId = latestCampaignRow?.CampaignId;
        DateTimeOffset? latestCampaignOccurredAt = latestCampaignRow?.OccurredAt;
        if (latestCampaignRow is not null)
        {
            latestCampaignStatus = string.Equals(latestCampaignRow.EventType, "capability_b_campaign_dry_run", StringComparison.Ordinal)
                ? "dry_run"
                : "executed";

            latestCampaignProgressPercent = (await RuntimeCapabilityCampaignStore.LoadCapabilityBCampaignDetailAsync(
                conn,
                CapabilityBBackofficeGenerationKey,
                latestCampaignRow.CampaignId,
                ct))?.ProgressPercent;
        }

        return new AdminRuntimeCapabilityOperationalSummaryDto(
            CandidateCount: candidateCount,
            ReadyToEnqueueCount: readyToEnqueueCount,
            BlockedByActiveJobCount: blockedByActiveJobCount,
            BlockedByCooldownCount: blockedByCooldownCount,
            ActiveCapabilityJobCount: activeCapabilityJobCount,
            TotalCampaignCount: totalCampaignCount,
            ActiveCampaignCount: activeCampaignCount,
            TerminalCapabilityJobCount: terminalCapabilityJobCount,
            StoredSummaryCount: storedSummaryCount,
            LatestCampaignProgressPercent: latestCampaignProgressPercent,
            LatestCampaignId: latestCampaignId,
            LatestCampaignStatus: latestCampaignStatus,
            LatestCampaignOccurredAt: latestCampaignOccurredAt);
    }

    internal sealed record HardwareGateResult(
        bool Passed,
        IReadOnlyDictionary<string, object?> Details,
        string? Error);

    private sealed record RuntimeSpecificGateResult(
        bool Passed,
        IReadOnlyDictionary<string, object?> Details,
        string? Error);

}
