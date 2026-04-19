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
    private const string AdminRuntimeActor = "admin_runtime_endpoint";
    private const string CoreRetrievalCapabilityKey = "core.retrieval";
    private const string CapabilityACorpusEnrichmentKey = "capability_a.corpus_enrichment";

    internal static AdminRuntimeCatalogResponseDto BuildCatalog(
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHostEnvironment env)
    {
        var profiles = BuildWarmupProfiles(options);
        return new(
            CdcAlignment,
            env.EnvironmentName,
            BuildRuntimes(rag, profiles),
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
                var profiles = BuildWarmupProfiles(options);
                return new AdminRuntimeRuntimeCatalogArtifactDto(
                    RuntimeCatalogArtifact,
                    CdcAlignment,
                    env.EnvironmentName,
                    DateTimeOffset.UtcNow,
                    BuildRuntimes(rag, profiles),
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
                var profiles = BuildWarmupProfiles(options);
                return new AdminRuntimeModelCatalogArtifactDto(
                    ModelCatalogArtifact,
                    CdcAlignment,
                    env.EnvironmentName,
                    DateTimeOffset.UtcNow,
                    BuildRuntimes(rag, profiles)
                        .Select(runtime => new AdminRuntimeModelCatalogEntryDto(
                            Key: runtime.Key,
                            Label: runtime.Label,
                            RuntimeKey: runtime.Key,
                            Kind: runtime.Kind,
                            Enabled: runtime.Enabled,
                            Implemented: true,
                            BaseUrl: runtime.BaseUrl,
                            Model: runtime.Model,
                            StatusNote: runtime.ReadinessStatus,
                            ReadinessStatus: runtime.ReadinessStatus,
                            ConfigurationSource: runtime.ConfigurationSource,
                            UsedByProfileKeys: runtime.UsedByProfileKeys,
                            RequiredByProfileKeys: runtime.RequiredByProfileKeys,
                            ExpectedCapabilityKeys: runtime.ExpectedCapabilityKeys,
                            RequiredSettingKeys: runtime.RequiredSettingKeys,
                            MissingSettingKeys: runtime.MissingSettingKeys))
                        .ToArray());
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
                BuildWarmupProfiles(options)));

    internal static async Task<AdminRuntimeCapabilitiesResponseDto> GetCapabilitiesAsync(
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHostEnvironment env,
        CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        var persisted = await LoadCapabilityStateRowsAsync(conn, ct);

        var items = RuntimeCapabilities
            .Select(def => persisted.TryGetValue(def.Key, out var row)
                ? ProjectCapabilityState(def, MapStateRow(row), options, rag)
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
            var persisted = await LoadCapabilityStateRowsAsync(conn, ct);
            var items = RuntimeCapabilities
                .Select(def => persisted.TryGetValue(def.Key, out var row)
                    ? ProjectCapabilityState(def, MapStateRow(row), options, rag)
                    : BuildDefaultState(def, options, rag))
                .Select(MapCapabilityDiagnostic)
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
            var persisted = await LoadCapabilityStateRowsAsync(conn, ct);
            var items = RuntimeCapabilities
                .Select(def => persisted.TryGetValue(def.Key, out var row)
                    ? ProjectCapabilityState(def, MapStateRow(row), options, rag)
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

            var candidates = await LoadCapabilityAEnrichmentCandidatesAsync(
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
            var items = await LoadCapabilityACampaignsAsync(conn, Math.Clamp(limit, 1, 100), ct);
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
            var row = await LoadCapabilityACampaignRowAsync(conn, campaignId, ct);
            if (row is null)
            {
                sw.Stop();
                RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, "capability_a_campaign_detail", success: false, durationMs: sw.ElapsedMilliseconds);
                return null;
            }

            var item = await BuildCapabilityACampaignDetailAsync(conn, row, ct);
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

            var candidates = await LoadCapabilityAEnrichmentCandidatesAsync(
                conn,
                tenantId,
                ingest,
                req?.Category,
                req?.MaxCandidates ?? 50,
                req?.ReasonFilters,
                ct);
            var campaignId = Guid.NewGuid();

            var selectedDocIds = req?.DocIds?.Where(id => id != Guid.Empty).ToHashSet() ?? [];
            var selectedDocPaths = req?.DocPaths?
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(PathUtil.NormalizeRelativePath)
                .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);

            if (selectedDocIds.Count > 0 || selectedDocPaths.Count > 0)
            {
                candidates = candidates
                    .Where(candidate => selectedDocIds.Contains(candidate.DocId) || selectedDocPaths.Contains(candidate.DocPath))
                    .ToArray();
            }

            var items = new List<AdminRuntimeCapabilityAEnqueueItemDto>(candidates.Length);
            var plannedCount = 0;
            var reasonCounts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var candidate in candidates)
            {
                var unsafeReasons = ResolveCapabilityAUnsafeReasons(candidate);
                if (req?.AllowUnsafeCandidates != true && unsafeReasons.Length > 0)
                {
                    var policyReason = $"policy_blocked:{string.Join(",", unsafeReasons.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}";
                    items.Add(new AdminRuntimeCapabilityAEnqueueItemDto(candidate.DocId, candidate.DocPath, Queued: false, Reason: policyReason));
                    IncrementCapabilityAReasonCounts(reasonCounts, candidate.Reasons);
                    IncrementCapabilityAReasonCounts(reasonCounts, unsafeReasons.Select(reason => $"blocked:{reason}"));
                    continue;
                }

                if (!candidate.FileExists)
                {
                    items.Add(new AdminRuntimeCapabilityAEnqueueItemDto(candidate.DocId, candidate.DocPath, Queued: false, Reason: "document_file_not_found"));
                    IncrementCapabilityAReasonCounts(reasonCounts, candidate.Reasons);
                    IncrementCapabilityAReasonCounts(reasonCounts, ["blocked:document_file_not_found"]);
                    continue;
                }

                var activeJob = await conn.QueryFirstOrDefaultAsync<Guid?>(new CommandDefinition(
                    """
SELECT job_id
FROM ingestion_jobs
WHERE tenant_id=@tenant
  AND doc_path=@docPath
  AND status IN ('queued','running','paused')
ORDER BY created_at DESC
LIMIT 1;
""",
                    new { tenant = tenantId, docPath = candidate.DocPath },
                    cancellationToken: ct));

                if (activeJob.HasValue)
                {
                    items.Add(new AdminRuntimeCapabilityAEnqueueItemDto(candidate.DocId, candidate.DocPath, Queued: false, JobId: activeJob.Value, Reason: "active_job_exists"));
                    IncrementCapabilityAReasonCounts(reasonCounts, candidate.Reasons);
                    IncrementCapabilityAReasonCounts(reasonCounts, ["blocked:active_job_exists"]);
                    continue;
                }

                plannedCount++;
                IncrementCapabilityAReasonCounts(reasonCounts, candidate.Reasons);

                if (req?.DryRun == true)
                {
                    items.Add(new AdminRuntimeCapabilityAEnqueueItemDto(candidate.DocId, candidate.DocPath, Queued: false, Reason: "dry_run_preview"));
                    continue;
                }

                var absPath = DocPathNormalizer.ToAbsoluteFromRelative(candidate.DocPath, ingest.DocumentsRoot);
                if (!File.Exists(absPath))
                {
                    items.Add(new AdminRuntimeCapabilityAEnqueueItemDto(candidate.DocId, candidate.DocPath, Queued: false, Reason: "document_file_not_found"));
                    IncrementCapabilityAReasonCounts(reasonCounts, ["blocked:document_file_not_found"]);
                    continue;
                }

                var fi = new FileInfo(absPath);
                var queued = await IngestionEnqueue.EnqueueUpsertAsync(conn, tenantId, candidate.DocPath, candidate.Category, fi, ct, enqueueSource: "capability_a");
                items.Add(new AdminRuntimeCapabilityAEnqueueItemDto(candidate.DocId, candidate.DocPath, Queued: true, JobId: queued.JobId));

                await InsertCapabilityEventAsync(
                    conn,
                    CreateCapabilityEvent(
                        capabilityKey: CapabilityACorpusEnrichmentKey,
                        profileKey: gate.State!.ProfileKey,
                        eventType: "capability_a_enqueued",
                        reason: string.Join(",", candidate.Reasons),
                        details: new Dictionary<string, object?>
                        {
                        ["docId"] = candidate.DocId,
                        ["docPath"] = candidate.DocPath,
                        ["category"] = candidate.Category,
                        ["jobId"] = queued.JobId,
                        ["reasons"] = candidate.Reasons.ToArray(),
                        ["campaignId"] = campaignId
                    }),
                ct);
            }

            var queuedCount = items.Count(item => item.Queued);
            var skippedCount = items.Count(item => !item.Queued);
            await InsertCapabilityEventAsync(
                conn,
                CreateCapabilityEvent(
                    capabilityKey: CapabilityACorpusEnrichmentKey,
                    profileKey: gate.State!.ProfileKey,
                    eventType: req?.DryRun == true ? "capability_a_campaign_dry_run" : "capability_a_campaign_executed",
                    reason: req?.DryRun == true ? "dry_run_preview" : "campaign_completed",
                    details: new Dictionary<string, object?>
                    {
                        ["campaignId"] = campaignId,
                        ["candidateCount"] = candidates.Length,
                        ["plannedCount"] = plannedCount,
                        ["queuedCount"] = queuedCount,
                        ["skippedCount"] = skippedCount,
                        ["dryRun"] = req?.DryRun == true,
                        ["allowUnsafeCandidates"] = req?.AllowUnsafeCandidates == true,
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
            RuntimeGovernanceTelemetry.CompleteCapabilityAOperation(
                activity,
                "capability_a_enqueue",
                success: true,
                durationMs: sw.ElapsedMilliseconds,
                candidateCount: candidates.Length,
                plannedCount: plannedCount,
                queuedCount: queuedCount,
                skippedCount: skippedCount,
                dryRun: req?.DryRun == true);

            return new RuntimeOperationResult<AdminRuntimeCapabilityAEnqueueResponseDto>(
                new AdminRuntimeCapabilityAEnqueueResponseDto(
                    CdcAlignment,
                    env.EnvironmentName,
                    CapabilityACorpusEnrichmentKey,
                    CampaignId: campaignId,
                    DryRun: req?.DryRun == true,
                    AllowUnsafeCandidates: req?.AllowUnsafeCandidates == true,
                    CandidateCount: candidates.Length,
                    PlannedCount: plannedCount,
                    QueuedCount: queuedCount,
                    SkippedCount: skippedCount,
                    ReasonCounts: reasonCounts,
                    Items: items),
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
        var profile = ResolveProfile(req?.ProfileKey, options);
        var definitions = ResolveCapabilitySelection(req?.CapabilityKey);
        RuntimeGovernanceTelemetry.RecordRequalifyRequest(
            string.IsNullOrWhiteSpace(req?.CapabilityKey) ? "all" : req!.CapabilityKey!,
            profile.Key,
            definitions.Count);

        var items = new List<AdminRuntimeCapabilityStateDto>(definitions.Count);
        var warmupResults = new List<AdminRuntimeWarmupResultDto>();

        await using var conn = await ds.OpenConnectionAsync(ct);
        var existingStates = await LoadCapabilityStateRowsAsync(conn, ct);

        foreach (var definition in definitions)
        {
            var evaluation = await EvaluateCapabilityAsync(
                definition,
                profile,
                existingStates.TryGetValue(definition.Key, out var existingState) ? MapStateRow(existingState) : null,
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
        var existingStates = await LoadCapabilityStateRowsAsync(conn, ct);

        foreach (var definition in definitions)
        {
            if (!existingStates.TryGetValue(definition.Key, out var row))
                continue;

            var current = ProjectCapabilityState(definition, MapStateRow(row), options, rag);
            if (!current.Stale)
                continue;

            var staleReason = current.Details is not null
                && current.Details.TryGetValue("staleQualificationReason", out var value)
                && value is string reason
                    ? reason
                    : "qualification_inputs_changed";

            var details = new Dictionary<string, object?>(current.Details ?? new Dictionary<string, object?>(), StringComparer.Ordinal)
            {
                ["staleReconciledAt"] = DateTimeOffset.UtcNow,
                ["staleReconciledBy"] = AdminRuntimeActor
            };

            var updated = current with
            {
                Authorized = false,
                Selected = false,
                LastError = "qualification stale: requalify required",
                Details = details
            };

            await UpsertCapabilityStateAsync(conn, updated, ct);
            await InsertCapabilityEventAsync(
                conn,
                CreateCapabilityEvent(
                    capabilityKey: updated.Key,
                    profileKey: updated.ProfileKey,
                    eventType: "stale_reconciled",
                    reason: staleReason,
                    details: new Dictionary<string, object?>
                    {
                        ["persistedAuthorized"] = current.PersistedAuthorized,
                        ["persistedSelected"] = current.PersistedSelected,
                        ["effectiveAuthorized"] = updated.EffectiveAuthorized,
                        ["effectiveSelected"] = updated.EffectiveSelected,
                        ["lastError"] = updated.LastError
                    }),
                ct);
            RuntimeGovernanceTelemetry.RecordStaleQualificationReconciled(updated.Key, updated.ProfileKey, staleReason);
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
        var existingStates = await LoadCapabilityStateRowsAsync(conn, ct);
        var current = existingStates.TryGetValue(definition.Key, out var row)
            ? ProjectCapabilityState(definition, MapStateRow(row), options, rag)
            : BuildDefaultState(definition, options, rag);

        var desiredEnabled = req?.DesiredEnabled ?? current.DesiredEnabled;
        var authorized = req?.Authorized ?? current.Authorized;
        var selected = req?.Selected ?? current.Selected;
        var explicitlyRequestedAuthorized = req?.Authorized == true;
        var explicitlyRequestedSelected = req?.Selected == true;
        var authorizationContext = req?.Authorized ?? current.Authorized;

        async Task<CapabilitySelectionUpdateResult> RejectAsync(string error)
        {
            await InsertCapabilityEventAsync(
                conn,
                CreateCapabilityEvent(
                    capabilityKey: definition.Key,
                    profileKey: current.ProfileKey,
                    eventType: "selection_rejected",
                    reason: error,
                    details: new Dictionary<string, object?>
                    {
                        ["requestedDesiredEnabled"] = req?.DesiredEnabled,
                        ["requestedAuthorized"] = req?.Authorized,
                        ["requestedSelected"] = req?.Selected,
                        ["currentDesiredEnabled"] = current.DesiredEnabled,
                        ["currentAuthorized"] = current.Authorized,
                        ["currentSelected"] = current.Selected,
                        ["currentStale"] = current.Stale,
                        ["currentQualified"] = current.Qualified
                    }),
                ct);

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteCapabilitySelect(selectionActivity, definition.Key, desiredEnabled, authorized, selected, success: false, durationMs: sw.ElapsedMilliseconds, errorReason: error);
            return new CapabilitySelectionUpdateResult(current, error);
        }

        if (explicitlyRequestedAuthorized && !desiredEnabled)
            return await RejectAsync("capability must be desired-enabled before it can be authorized");

        if (explicitlyRequestedSelected && !desiredEnabled)
            return await RejectAsync("capability must be desired-enabled before it can be selected");

        if (explicitlyRequestedSelected && !authorizationContext)
            return await RejectAsync("capability must be authorized before it can be selected");

        if (!desiredEnabled)
        {
            authorized = false;
            selected = false;
        }
        else if (!authorized)
        {
            selected = false;
        }

        if (current.Stale && (explicitlyRequestedAuthorized || explicitlyRequestedSelected))
            return await RejectAsync("capability must be requalified because its qualification is stale");

        if (authorized && !desiredEnabled)
            return await RejectAsync("capability must be desired-enabled before it can be authorized");

        if (authorized && !current.Qualified)
            return await RejectAsync("capability must be qualified before it can be authorized");

        if (selected && !desiredEnabled)
            return await RejectAsync("capability must be desired-enabled before it can be selected");

        if (selected && !authorized)
            return await RejectAsync("capability must be authorized before it can be selected");

        if (selected && !current.Qualified)
            return await RejectAsync("capability must be qualified before it can be selected");

        var details = new Dictionary<string, object?>(current.Details ?? new Dictionary<string, object?>(), StringComparer.Ordinal)
        {
            ["selectionUpdatedAt"] = DateTimeOffset.UtcNow,
            ["selectionPolicy"] = new Dictionary<string, object?>
            {
                ["desiredEnabled"] = desiredEnabled,
                ["authorized"] = authorized,
                ["selected"] = selected,
                ["requiresQualifiedForAuthorization"] = true,
                ["requiresDesiredEnabledForAuthorization"] = true,
                ["requiresDesiredEnabledForSelection"] = true
            }
        };

        var updated = current with
        {
            DesiredEnabled = desiredEnabled,
            Authorized = authorized && current.Qualified,
            Selected = selected && current.Qualified && authorized && desiredEnabled,
            Details = details
        };

        await UpsertCapabilityStateAsync(conn, updated, ct);
        await InsertCapabilityEventAsync(
            conn,
            CreateCapabilityEvent(
                capabilityKey: updated.Key,
                profileKey: updated.ProfileKey,
                eventType: "selection_updated",
                reason: "selection_updated",
                details: new Dictionary<string, object?>
                {
                    ["previousDesiredEnabled"] = current.DesiredEnabled,
                    ["previousAuthorized"] = current.PersistedAuthorized,
                    ["previousSelected"] = current.PersistedSelected,
                    ["desiredEnabled"] = updated.DesiredEnabled,
                    ["authorized"] = updated.Authorized,
                    ["selected"] = updated.Selected,
                    ["stale"] = updated.Stale
                }),
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
        var qualificationFingerprint = BuildQualificationFingerprint(definition, profile, options, rag);

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
        var qualificationFingerprint = BuildQualificationFingerprint(definition, profile, options, rag);

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

    private static async Task UpsertCapabilityStateAsync(NpgsqlConnection conn, AdminRuntimeCapabilityStateDto state, CancellationToken ct)
    {
        const string sql = """
INSERT INTO runtime_capability_state(
  capability_key,
  capability_family,
  display_name,
  runtime_key,
  profile_key,
  implemented,
  desired_enabled,
  installed,
  configured,
  healthy,
  qualified,
  authorized,
  selected,
  pass_count,
  last_checked_at,
  last_qualified_at,
  last_error,
  details,
  updated_at)
VALUES(
  @capability_key,
  @capability_family,
  @display_name,
  @runtime_key,
  @profile_key,
  @implemented,
  @desired_enabled,
  @installed,
  @configured,
  @healthy,
  @qualified,
  @authorized,
  @selected,
  @pass_count,
  @last_checked_at,
  @last_qualified_at,
  @last_error,
  CAST(@details AS jsonb),
  now())
ON CONFLICT (capability_key) DO UPDATE SET
  capability_family = EXCLUDED.capability_family,
  display_name = EXCLUDED.display_name,
  runtime_key = EXCLUDED.runtime_key,
  profile_key = EXCLUDED.profile_key,
  implemented = EXCLUDED.implemented,
  desired_enabled = EXCLUDED.desired_enabled,
  installed = EXCLUDED.installed,
  configured = EXCLUDED.configured,
  healthy = EXCLUDED.healthy,
  qualified = EXCLUDED.qualified,
  authorized = EXCLUDED.authorized,
  selected = EXCLUDED.selected,
  pass_count = EXCLUDED.pass_count,
  last_checked_at = EXCLUDED.last_checked_at,
  last_qualified_at = EXCLUDED.last_qualified_at,
  last_error = EXCLUDED.last_error,
  details = EXCLUDED.details,
  updated_at = now();
""";

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            capability_key = state.Key,
            capability_family = state.Family,
            display_name = state.DisplayName,
            runtime_key = state.RuntimeKey,
            profile_key = state.ProfileKey,
            implemented = state.Implemented,
            desired_enabled = state.DesiredEnabled,
            installed = state.Installed,
            configured = state.Configured,
            healthy = state.Healthy,
            qualified = state.Qualified,
            authorized = state.Authorized,
            selected = state.Selected,
            pass_count = state.PassCount,
            last_checked_at = state.LastCheckedAt?.UtcDateTime,
            last_qualified_at = state.LastQualifiedAt?.UtcDateTime,
            last_error = state.LastError,
            details = JsonSerializer.Serialize(state.Details ?? new Dictionary<string, object?>())
        }, cancellationToken: ct));
    }

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

    private static async Task InsertCapabilityEventAsync(NpgsqlConnection conn, AdminRuntimeCapabilityEventDto evt, CancellationToken ct)
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

    private static AdminRuntimeCapabilityEventDto CreateCapabilityEvent(
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
            var profile = ResolveProfile(options.DefaultProfileKey, options);
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

        if (definition.Key == CoreRetrievalCapabilityKey)
        {
            var installed = !string.IsNullOrWhiteSpace(rag.QdrantBaseUrl)
                && !string.IsNullOrWhiteSpace(rag.EmbeddingsBaseUrl);
            var configured = installed
                && !string.IsNullOrWhiteSpace(rag.QdrantCollection)
                && !string.IsNullOrWhiteSpace(rag.EmbeddingsModel);
            var profile = ResolveProfile(options.DefaultProfileKey, options);

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

    private static AdminRuntimeCapabilityStateDto MapStateRow(CapabilityStateRow row)
    {
        var details = ParseDetails(row.DetailsJson);
        return new(
            Key: row.CapabilityKey,
            DisplayName: row.DisplayName,
            Family: row.CapabilityFamily,
            RuntimeKey: row.RuntimeKey,
            Implemented: row.Implemented,
            DesiredEnabled: row.DesiredEnabled,
            Installed: row.Installed,
            Configured: row.Configured,
            Healthy: row.Healthy,
            Qualified: row.Qualified,
            Authorized: row.Authorized,
            Selected: row.Selected,
            ProfileKey: row.ProfileKey,
            PassCount: row.PassCount,
            LastCheckedAt: row.LastCheckedAt,
            LastQualifiedAt: row.LastQualifiedAt,
            LastError: row.LastError,
            Details: details,
            Stale: false,
            QualificationFingerprint: ResolveQualificationFingerprint(details),
            StaleReason: null,
            QualificationAgeHours: null,
            QualificationExpiresAt: null,
            PersistedAuthorized: row.Authorized,
            PersistedSelected: row.Selected,
            EffectiveAuthorized: row.Authorized,
            EffectiveSelected: row.Selected);
    }

    private static AdminRuntimeCapabilityDiagnosticDto MapCapabilityDiagnostic(AdminRuntimeCapabilityStateDto state)
    {
        var blockers = BuildBlockers(state);
        var recommendations = BuildRecommendations(state, blockers);

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
            LastError: state.LastError);
    }

    private static AdminRuntimeCapabilityStateDto ProjectCapabilityState(
        RuntimeCapabilityDefinition definition,
        AdminRuntimeCapabilityStateDto state,
        RuntimeGovernanceOptions options,
        RagOptions rag)
    {
        if (!state.Implemented || (!string.Equals(definition.Key, CoreRetrievalCapabilityKey, StringComparison.Ordinal)
                                   && !string.Equals(definition.Key, CapabilityACorpusEnrichmentKey, StringComparison.Ordinal)))
            return state with
            {
                Stale = false,
                QualificationFingerprint = ResolveQualificationFingerprint(state.Details),
                StaleReason = null,
                QualificationAgeHours = null,
                QualificationExpiresAt = null,
                PersistedAuthorized = state.PersistedAuthorized,
                PersistedSelected = state.PersistedSelected,
                EffectiveAuthorized = state.PersistedAuthorized,
                EffectiveSelected = state.PersistedSelected
            };

        var profile = ResolveProfile(state.ProfileKey, options);
        var currentFingerprint = BuildQualificationFingerprint(definition, profile, options, rag);
        var storedFingerprint = ResolveQualificationFingerprint(state.Details);
        var staleReason = ResolveStaleQualificationReason(state, profile, storedFingerprint, currentFingerprint.Hash, out var freshness);
        var stale = staleReason is not null;

        var details = new Dictionary<string, object?>(state.Details ?? new Dictionary<string, object?>(), StringComparer.Ordinal)
        {
            ["qualificationFingerprintCurrent"] = currentFingerprint.Hash,
            ["qualificationFreshness"] = stale ? "stale" : "fresh",
            ["freshnessPolicy"] = profile.FreshnessPolicy,
            ["qualificationAgeHours"] = freshness.QualificationAgeHours,
            ["qualificationExpiresAt"] = freshness.ExpiresAt
        };

        if (stale && staleReason is not null)
        {
            details["qualificationFingerprintStored"] = storedFingerprint;
            details["staleQualificationReason"] = staleReason;
            details["staleQualificationDetectedAt"] = DateTimeOffset.UtcNow;
            details["qualificationFingerprintInputsCurrent"] = currentFingerprint.Inputs;
            details["persistedAuthorized"] = state.Authorized;
            details["persistedSelected"] = state.Selected;
            RuntimeGovernanceTelemetry.RecordStaleQualificationDetected(definition.Key, profile.Key, staleReason);
        }
        else if (storedFingerprint is not null)
        {
            details["qualificationFingerprintStored"] = storedFingerprint;
        }

        return state with
        {
            Details = details,
            Stale = stale,
            StaleReason = staleReason,
            QualificationAgeHours = freshness.QualificationAgeHours,
            QualificationExpiresAt = freshness.ExpiresAt,
            Authorized = stale ? false : state.Authorized,
            Selected = stale ? false : state.Selected,
            QualificationFingerprint = storedFingerprint ?? currentFingerprint.Hash,
            PersistedAuthorized = state.PersistedAuthorized,
            PersistedSelected = state.PersistedSelected,
            EffectiveAuthorized = stale ? false : state.PersistedAuthorized,
            EffectiveSelected = stale ? false : state.PersistedSelected
        };
    }

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

    private static IReadOnlyList<AdminRuntimeCatalogRuntimeDto> BuildRuntimes(
        RagOptions rag,
        IReadOnlyList<AdminRuntimeWarmupProfileDto> profiles)
        =>
        [
            BuildRuntimeDescriptor(
                runtimeKey: "qdrant",
                label: "Qdrant vector store",
                kind: "vector_store",
                enabled: !string.IsNullOrWhiteSpace(rag.QdrantBaseUrl),
                baseUrl: rag.QdrantBaseUrl,
                model: rag.QdrantCollection,
                configurationSource: "rag_options.qdrant_base_url",
                expectedCapabilityKeys: ["core.retrieval"],
                requiredSettingKeys: ["qdrant_base_url", "qdrant_collection"],
                missingSettingKeys: ResolveMissingSettingKeys(
                    ("qdrant_base_url", rag.QdrantBaseUrl),
                    ("qdrant_collection", rag.QdrantCollection)),
                profiles: profiles),
            BuildRuntimeDescriptor(
                runtimeKey: "tei-embeddings",
                label: "TEI embeddings",
                kind: "embedding_runtime",
                enabled: !string.IsNullOrWhiteSpace(rag.EmbeddingsBaseUrl),
                baseUrl: rag.EmbeddingsBaseUrl,
                model: rag.EmbeddingsModel,
                configurationSource: "rag_options.embeddings_base_url",
                expectedCapabilityKeys: ["core.retrieval"],
                requiredSettingKeys: ["embeddings_base_url", "embeddings_model"],
                missingSettingKeys: ResolveMissingSettingKeys(
                    ("embeddings_base_url", rag.EmbeddingsBaseUrl),
                    ("embeddings_model", rag.EmbeddingsModel)),
                profiles: profiles),
            BuildRuntimeDescriptor(
                runtimeKey: "tei-rerank",
                label: "TEI rerank",
                kind: "rerank_runtime",
                enabled: rag.EnableRerank,
                baseUrl: string.IsNullOrWhiteSpace(rag.RerankBaseUrl) ? rag.EmbeddingsBaseUrl : rag.RerankBaseUrl,
                model: rag.RerankModel,
                configurationSource: ResolveRerankConfigurationSource(rag),
                expectedCapabilityKeys: ["core.retrieval"],
                requiredSettingKeys: ["rerank_enabled", "rerank_base_url_or_embeddings_base_url", "rerank_model"],
                missingSettingKeys: rag.EnableRerank
                    ? ResolveMissingSettingKeys(
                        ("rerank_base_url_or_embeddings_base_url", string.IsNullOrWhiteSpace(rag.RerankBaseUrl) ? rag.EmbeddingsBaseUrl : rag.RerankBaseUrl),
                        ("rerank_model", rag.RerankModel))
                    : [],
                profiles: profiles),
            BuildRuntimeDescriptor(
                runtimeKey: "server-capability-a",
                label: "Server capability A",
                kind: "backoffice_capability",
                enabled: true,
                baseUrl: null,
                model: "corpus_enrichment_admin",
                configurationSource: "backend_internal",
                expectedCapabilityKeys: [CapabilityACorpusEnrichmentKey],
                requiredSettingKeys: [],
                missingSettingKeys: [],
                profiles: profiles)
        ];

    private static IReadOnlyList<AdminRuntimeWarmupProfileDto> BuildWarmupProfiles(RuntimeGovernanceOptions options)
        =>
        [
            new(
                Key: options.DefaultProfileKey,
                Label: "Default local qualification",
                PassCount: Math.Max(1, options.WarmupPassCount),
                Checks:
                [
                    "qdrant.collection",
                    "tei.embeddings",
                    "tei.rerank_if_enabled"
                ],
                HardwareRequirements: new AdminRuntimeHardwareRequirementsDto(
                    options.MinCpuCores,
                    options.MinAvailableMemoryMb,
                    options.Require64BitProcess),
                PerformanceBudgets: BuildPerformanceBudgets(options),
                CheckPolicy: new AdminRuntimeWarmupCheckPolicyDto(
                    RequireRerankEnabled: false,
                    AllowSkippedChecks: true,
                    EnforcePerformanceBudgets: true),
                RuntimeRequirements: new AdminRuntimeWarmupRuntimeRequirementsDto(
                    RequireQdrant: true,
                    RequireEmbeddings: true,
                    RequireRerank: false,
                    RequireCollection: true,
                    RequireEmbeddingsModel: true,
                    RequireRerankModel: false),
                FreshnessPolicy: new AdminRuntimeWarmupFreshnessPolicyDto(
                    MaxQualificationAgeHours: options.MaxQualificationAgeHours,
                    RequireRequalificationWhenExpired: true)),
            new(
                Key: "strict-local",
                Label: "Strict local qualification",
                PassCount: Math.Max(1, options.WarmupPassCount),
                Checks:
                [
                    "qdrant.collection",
                    "tei.embeddings",
                    "tei.rerank_if_enabled"
                ],
                HardwareRequirements: new AdminRuntimeHardwareRequirementsDto(
                    Math.Max(options.StrictProfileMinCpuCores, 4),
                    Math.Max(options.StrictProfileMinAvailableMemoryMb, 4096),
                    true),
                PerformanceBudgets: BuildStrictPerformanceBudgets(options),
                CheckPolicy: new AdminRuntimeWarmupCheckPolicyDto(
                    RequireRerankEnabled: false,
                    AllowSkippedChecks: true,
                    EnforcePerformanceBudgets: true),
                RuntimeRequirements: new AdminRuntimeWarmupRuntimeRequirementsDto(
                    RequireQdrant: true,
                    RequireEmbeddings: true,
                    RequireRerank: false,
                    RequireCollection: true,
                    RequireEmbeddingsModel: true,
                    RequireRerankModel: false),
                FreshnessPolicy: new AdminRuntimeWarmupFreshnessPolicyDto(
                    MaxQualificationAgeHours: options.StrictProfileMaxQualificationAgeHours,
                    RequireRequalificationWhenExpired: true)),
            new(
                Key: "strict-rerank",
                Label: "Strict local qualification with rerank",
                PassCount: Math.Max(1, options.WarmupPassCount),
                Checks:
                [
                    "qdrant.collection",
                    "tei.embeddings",
                    "tei.rerank_required"
                ],
                HardwareRequirements: new AdminRuntimeHardwareRequirementsDto(
                    Math.Max(options.StrictRerankProfileMinCpuCores, 4),
                    Math.Max(options.StrictRerankProfileMinAvailableMemoryMb, 4096),
                    true),
                PerformanceBudgets: BuildStrictPerformanceBudgets(options),
                CheckPolicy: new AdminRuntimeWarmupCheckPolicyDto(
                    RequireRerankEnabled: true,
                    AllowSkippedChecks: false,
                    EnforcePerformanceBudgets: true),
                RuntimeRequirements: new AdminRuntimeWarmupRuntimeRequirementsDto(
                    RequireQdrant: true,
                    RequireEmbeddings: true,
                    RequireRerank: true,
                    RequireCollection: true,
                    RequireEmbeddingsModel: true,
                    RequireRerankModel: true),
                FreshnessPolicy: new AdminRuntimeWarmupFreshnessPolicyDto(
                    MaxQualificationAgeHours: options.StrictRerankProfileMaxQualificationAgeHours,
                    RequireRequalificationWhenExpired: true))
        ];

    private static AdminRuntimeWarmupProfileDto ResolveProfile(string? requestedProfileKey, RuntimeGovernanceOptions options)
    {
        var profiles = BuildWarmupProfiles(options);
        return profiles.FirstOrDefault(profile => string.Equals(profile.Key, requestedProfileKey, StringComparison.OrdinalIgnoreCase))
            ?? profiles[0];
    }

    private static QualificationFingerprint BuildQualificationFingerprint(
        RuntimeCapabilityDefinition definition,
        AdminRuntimeWarmupProfileDto profile,
        RuntimeGovernanceOptions options,
        RagOptions rag)
    {
        var inputs = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["capabilityKey"] = definition.Key,
            ["profile"] = new Dictionary<string, object?>
            {
                ["key"] = profile.Key,
                ["passCount"] = profile.PassCount,
                ["checks"] = profile.Checks.ToArray(),
                ["hardwareRequirements"] = profile.HardwareRequirements,
                ["performanceBudgets"] = profile.PerformanceBudgets,
                ["checkPolicy"] = profile.CheckPolicy,
                ["runtimeRequirements"] = profile.RuntimeRequirements
            },
            ["rag"] = new Dictionary<string, object?>
            {
                ["qdrantBaseUrl"] = rag.QdrantBaseUrl,
                ["qdrantCollection"] = rag.QdrantCollection,
                ["embeddingsBaseUrl"] = rag.EmbeddingsBaseUrl,
                ["embeddingsModel"] = rag.EmbeddingsModel,
                ["enableRerank"] = rag.EnableRerank,
                ["rerankBaseUrl"] = rag.RerankBaseUrl,
                ["rerankBaseUrlEffective"] = string.IsNullOrWhiteSpace(rag.RerankBaseUrl) ? rag.EmbeddingsBaseUrl : rag.RerankBaseUrl,
                ["rerankModel"] = rag.RerankModel
            },
            ["runtimeGates"] = new Dictionary<string, object?>
            {
                ["qdrantMinCpuCores"] = options.QdrantRuntimeMinCpuCores,
                ["qdrantMinAvailableMemoryMb"] = options.QdrantRuntimeMinAvailableMemoryMb,
                ["embeddingsMinCpuCores"] = options.EmbeddingsRuntimeMinCpuCores,
                ["embeddingsMinAvailableMemoryMb"] = options.EmbeddingsRuntimeMinAvailableMemoryMb,
                ["rerankMinCpuCores"] = options.RerankRuntimeMinCpuCores,
                ["rerankMinAvailableMemoryMb"] = options.RerankRuntimeMinAvailableMemoryMb
            }
        };

        if (string.Equals(definition.Key, CapabilityACorpusEnrichmentKey, StringComparison.Ordinal))
        {
            inputs["capabilityA"] = new Dictionary<string, object?>
            {
                ["mode"] = "corpus_enrichment_admin",
                ["requiresRetrievalStack"] = true,
                ["planEndpoint"] = "/admin/runtime/capabilities/capability_a.corpus_enrichment/candidates",
                ["enqueueEndpoint"] = "/admin/runtime/capabilities/capability_a.corpus_enrichment/enqueue"
            };
        }

        var json = JsonSerializer.Serialize(inputs);
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(json));
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        return new QualificationFingerprint(hash, inputs);
    }

    private static string? ResolveStaleQualificationReason(
        AdminRuntimeCapabilityStateDto state,
        AdminRuntimeWarmupProfileDto profile,
        string? storedFingerprint,
        string currentFingerprint,
        out QualificationFreshness freshness)
    {
        freshness = EvaluateQualificationFreshness(state, profile);
        var shouldEvaluateFreshness = state.Qualified || state.Authorized || state.Selected || state.LastQualifiedAt is not null;
        if (!shouldEvaluateFreshness)
            return null;

        if (freshness.IsExpired && profile.FreshnessPolicy?.RequireRequalificationWhenExpired != false)
            return "qualification_expired";

        if (string.IsNullOrWhiteSpace(storedFingerprint))
            return "missing_qualification_fingerprint";

        return string.Equals(storedFingerprint, currentFingerprint, StringComparison.Ordinal)
            ? null
            : "qualification_inputs_changed";
    }

    private static QualificationFreshness EvaluateQualificationFreshness(
        AdminRuntimeCapabilityStateDto state,
        AdminRuntimeWarmupProfileDto profile)
    {
        var policy = profile.FreshnessPolicy;
        if (policy?.MaxQualificationAgeHours is not long maxAgeHours || state.LastQualifiedAt is null)
            return new QualificationFreshness(false, null, null);

        var qualifiedAt = state.LastQualifiedAt.Value;
        var expiresAt = qualifiedAt.AddHours(maxAgeHours);
        var ageHours = (DateTimeOffset.UtcNow - qualifiedAt).TotalHours;
        return new QualificationFreshness(DateTimeOffset.UtcNow > expiresAt, ageHours, expiresAt);
    }

    private static AdminRuntimeCatalogRuntimeDto BuildRuntimeDescriptor(
        string runtimeKey,
        string label,
        string kind,
        bool enabled,
        string? baseUrl,
        string? model,
        string configurationSource,
        IReadOnlyList<string> expectedCapabilityKeys,
        IReadOnlyList<string> requiredSettingKeys,
        IReadOnlyList<string> missingSettingKeys,
        IReadOnlyList<AdminRuntimeWarmupProfileDto> profiles)
    {
        var usedByProfiles = ResolveProfilesForRuntime(runtimeKey, profiles, requiredOnly: false);
        var requiredByProfiles = ResolveProfilesForRuntime(runtimeKey, profiles, requiredOnly: true);
        var readinessStatus = ResolveRuntimeReadinessStatus(runtimeKey, enabled, missingSettingKeys);

        return new AdminRuntimeCatalogRuntimeDto(
            Key: runtimeKey,
            Label: label,
            Kind: kind,
            Enabled: enabled,
            BaseUrl: baseUrl,
            Model: model,
            ReadinessStatus: readinessStatus,
            ConfigurationSource: configurationSource,
            UsedByProfileKeys: usedByProfiles,
            RequiredByProfileKeys: requiredByProfiles,
            ExpectedCapabilityKeys: expectedCapabilityKeys,
            RequiredSettingKeys: requiredSettingKeys,
            MissingSettingKeys: missingSettingKeys);
    }

    private static IReadOnlyList<string> ResolveProfilesForRuntime(
        string runtimeKey,
        IReadOnlyList<AdminRuntimeWarmupProfileDto> profiles,
        bool requiredOnly)
        => profiles
            .Where(profile => RuntimeMatchesProfile(runtimeKey, profile, requiredOnly))
            .Select(profile => profile.Key)
            .ToArray();

    private static bool RuntimeMatchesProfile(
        string runtimeKey,
        AdminRuntimeWarmupProfileDto profile,
        bool requiredOnly)
    {
        var requirements = profile.RuntimeRequirements ?? new AdminRuntimeWarmupRuntimeRequirementsDto(
            RequireQdrant: true,
            RequireEmbeddings: true,
            RequireRerank: false,
            RequireCollection: true,
            RequireEmbeddingsModel: true,
            RequireRerankModel: false);

        return runtimeKey switch
        {
            "qdrant" => requirements.RequireQdrant || requirements.RequireCollection,
            "tei-embeddings" => requirements.RequireEmbeddings || requirements.RequireEmbeddingsModel,
            "tei-rerank" => requiredOnly
                ? requirements.RequireRerank || requirements.RequireRerankModel || profile.CheckPolicy?.RequireRerankEnabled == true
                : profile.Checks.Any(static check => check.Contains("tei.rerank", StringComparison.OrdinalIgnoreCase))
                  || requirements.RequireRerank
                  || requirements.RequireRerankModel
                  || profile.CheckPolicy?.RequireRerankEnabled == true,
            _ => false
        };
    }

    private static IReadOnlyList<string> ResolveMissingSettingKeys(params (string Key, string? Value)[] settings)
        => settings
            .Where(static setting => string.IsNullOrWhiteSpace(setting.Value))
            .Select(static setting => setting.Key)
            .ToArray();

    private static string ResolveRerankConfigurationSource(RagOptions rag)
    {
        if (!rag.EnableRerank)
            return "rag_options.rerank_disabled";

        return string.IsNullOrWhiteSpace(rag.RerankBaseUrl)
            ? "derived_from_rag_options.embeddings_base_url"
            : "rag_options.rerank_base_url";
    }

    private static string ResolveRuntimeReadinessStatus(
        string runtimeKey,
        bool enabled,
        IReadOnlyList<string> missingSettingKeys)
    {
        if (runtimeKey == "tei-rerank" && !enabled)
            return "disabled";

        if (!enabled)
            return "not_configured";

        return missingSettingKeys.Count == 0
            ? "configured"
            : "partially_configured";
    }

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

    private static string? ResolveQualificationFingerprint(IReadOnlyDictionary<string, object?>? details)
        => details is not null
           && details.TryGetValue("qualificationFingerprint", out var value)
           && value is string fingerprint
            ? fingerprint
            : null;

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

    private static AdminRuntimeWarmupPerformanceBudgetsDto BuildPerformanceBudgets(RuntimeGovernanceOptions options)
        => new(
            MaxPassDurationMs: options.MaxWarmupPassDurationMs,
            MaxQdrantCheckMs: options.MaxQdrantCheckMs,
            MaxEmbeddingsCheckMs: options.MaxEmbeddingsCheckMs,
            MaxRerankCheckMs: options.MaxRerankCheckMs);

    private static AdminRuntimeWarmupPerformanceBudgetsDto BuildStrictPerformanceBudgets(RuntimeGovernanceOptions options)
        => new(
            MaxPassDurationMs: TightenBudget(options.MaxWarmupPassDurationMs),
            MaxQdrantCheckMs: TightenBudget(options.MaxQdrantCheckMs),
            MaxEmbeddingsCheckMs: TightenBudget(options.MaxEmbeddingsCheckMs),
            MaxRerankCheckMs: TightenBudget(options.MaxRerankCheckMs));

    private static long TightenBudget(long value)
        => Math.Max(1, (long)Math.Floor(value * 0.75d));

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

    private static string[] BuildRecommendations(AdminRuntimeCapabilityStateDto state, IReadOnlyList<string> blockers)
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
            recommendations.Add("review capability A candidates and enqueue controlled reindex jobs when appropriate");
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

    private static List<string> BuildCapabilityAReasons(CapabilityAEnrichmentCandidateRow row)
    {
        var reasons = new List<string>();

        if (row.IndexedVersion <= 0)
            reasons.Add("never_indexed");
        if (row.IngestionVersion > row.IndexedVersion)
            reasons.Add("indexed_version_outdated");
        if (row.IndexedVersion > 0 && !row.HasRevision)
            reasons.Add("revision_missing");
        if (row.IndexedVersion > 0 && row.HasRevision && !row.HasRetrievalChunks)
            reasons.Add("retrieval_chunks_missing");
        if (row.IndexedVersion > 0 && row.HasRevision && !row.HasExactMatchEntries)
            reasons.Add("exact_match_entries_missing");
        if (row.IndexedVersion > 0 && row.HasRevision && !row.HasContextualTextEntries)
            reasons.Add("contextual_text_entries_missing");
        if (row.AutoIngestPaused)
            reasons.Add("auto_ingest_paused");

        return reasons;
    }

    private static string[] ResolveCapabilityAUnsafeReasons(AdminRuntimeCapabilityAEnrichmentCandidateDto candidate)
        => candidate.Reasons
            .Where(reason =>
                string.Equals(reason, "auto_ingest_paused", StringComparison.OrdinalIgnoreCase)
                || string.Equals(reason, "revision_missing", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

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

    private static IReadOnlyDictionary<string, int> ParseCapabilityAReasonCounts(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
            return new Dictionary<string, int>(StringComparer.Ordinal);

        try
        {
            using var doc = JsonDocument.Parse(detailsJson);
            if (!doc.RootElement.TryGetProperty("reasonCounts", out var reasonCountsElement)
                || reasonCountsElement.ValueKind != JsonValueKind.Object)
                return new Dictionary<string, int>(StringComparer.Ordinal);

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var prop in reasonCountsElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var value))
                    counts[prop.Name] = value;
            }

            return counts;
        }
        catch
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }
    }

    private static AdminRuntimeCapabilityAEnqueueItemDto[] ParseCapabilityACampaignItems(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
            return [];

        using var doc = JsonDocument.Parse(detailsJson);
        if (!doc.RootElement.TryGetProperty("items", out var itemsElement) || itemsElement.ValueKind != JsonValueKind.Array)
            return [];

        var items = new List<AdminRuntimeCapabilityAEnqueueItemDto>();
        foreach (var itemElement in itemsElement.EnumerateArray())
        {
            if (itemElement.ValueKind != JsonValueKind.Object)
                continue;

            Guid? docId = null;
            if (itemElement.TryGetProperty("docId", out var docIdElement)
                && docIdElement.ValueKind == JsonValueKind.String
                && Guid.TryParse(docIdElement.GetString(), out var parsedDocId))
            {
                docId = parsedDocId;
            }

            Guid? jobId = null;
            if (itemElement.TryGetProperty("jobId", out var jobIdElement)
                && jobIdElement.ValueKind == JsonValueKind.String
                && Guid.TryParse(jobIdElement.GetString(), out var parsedJobId))
            {
                jobId = parsedJobId;
            }

            var docPath = itemElement.TryGetProperty("docPath", out var docPathElement) && docPathElement.ValueKind == JsonValueKind.String
                ? docPathElement.GetString()
                : null;
            var reason = itemElement.TryGetProperty("reason", out var reasonElement) && reasonElement.ValueKind == JsonValueKind.String
                ? reasonElement.GetString()
                : null;
            var queued = itemElement.TryGetProperty("queued", out var queuedElement) && queuedElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? queuedElement.GetBoolean()
                : false;

            items.Add(new AdminRuntimeCapabilityAEnqueueItemDto(
                DocId: docId,
                DocPath: docPath,
                Queued: queued,
                JobId: jobId,
                Reason: reason));
        }

        return items.ToArray();
    }

    private static int BuildCapabilityAPriorityScore(IReadOnlyCollection<string> reasons, CapabilityAEnrichmentCandidateRow row)
    {
        var score = reasons.Count;
        if (row.IndexedVersion <= 0)
            score += 5;
        if (row.IngestionVersion > row.IndexedVersion)
            score += 3;
        if (reasons.Contains("retrieval_chunks_missing", StringComparer.OrdinalIgnoreCase))
            score += 3;
        if (reasons.Contains("exact_match_entries_missing", StringComparer.OrdinalIgnoreCase))
            score += 2;
        if (reasons.Contains("contextual_text_entries_missing", StringComparer.OrdinalIgnoreCase))
            score += 2;
        if (reasons.Contains("document_file_not_found", StringComparer.OrdinalIgnoreCase))
            score += 1;
        return score;
    }

    private static bool ResolveCandidateFileExists(IngestionOptions ingest, string docPath)
    {
        if (string.IsNullOrWhiteSpace(ingest.DocumentsRoot))
            return false;

        try
        {
            var absolutePath = DocPathNormalizer.ToAbsoluteFromRelative(docPath, ingest.DocumentsRoot);
            return File.Exists(absolutePath);
        }
        catch
        {
            return false;
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
        new("capability_b.backoffice_generation", "Capability B - Backoffice Generation", "B", "server-capability-b", false, false, "Capability B remains intentionally unimplemented in v3.0 backend."),
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

    internal sealed record RuntimeOperationResult<T>(
        T? Payload,
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

    private sealed record CapabilityStateRow(
        string CapabilityKey,
        string CapabilityFamily,
        string DisplayName,
        string RuntimeKey,
        string ProfileKey,
        bool Implemented,
        bool DesiredEnabled,
        bool Installed,
        bool Configured,
        bool Healthy,
        bool Qualified,
        bool Authorized,
        bool Selected,
        int PassCount,
        DateTimeOffset? LastCheckedAt,
        DateTimeOffset? LastQualifiedAt,
        string? LastError,
        string? DetailsJson);

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

    private sealed record CapabilityAEnrichmentCandidateRow(
        Guid DocId,
        string DocPath,
        string DocName,
        string Category,
        string Status,
        int IngestionVersion,
        int IndexedVersion,
        bool AutoIngestPaused,
        string? AutoIngestPauseReason,
        bool HasRevision,
        bool HasRetrievalChunks,
        bool HasExactMatchEntries,
        bool HasContextualTextEntries);

    private sealed record CapabilityACampaignRow(
        Guid CampaignId,
        string CapabilityKey,
        string? ProfileKey,
        string EventType,
        bool DryRun,
        bool AllowUnsafeCandidates,
        int CandidateCount,
        int PlannedCount,
        int QueuedCount,
        int SkippedCount,
        DateTimeOffset OccurredAt,
        string? DetailsJson);

    private sealed record CapabilityACampaignItemRow(
        Guid? DocId,
        string? DocPath,
        Guid? JobId,
        DateTimeOffset OccurredAt);

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

    private static async Task<Dictionary<string, CapabilityStateRow>> LoadCapabilityStateRowsAsync(NpgsqlConnection conn, CancellationToken ct)
        => (await conn.QueryAsync<CapabilityStateRow>(new CommandDefinition("""
SELECT
  capability_key AS "CapabilityKey",
  capability_family AS "CapabilityFamily",
  display_name AS "DisplayName",
  runtime_key AS "RuntimeKey",
  profile_key AS "ProfileKey",
  implemented AS "Implemented",
  desired_enabled AS "DesiredEnabled",
  installed AS "Installed",
  configured AS "Configured",
  healthy AS "Healthy",
  qualified AS "Qualified",
  authorized AS "Authorized",
  selected AS "Selected",
  pass_count AS "PassCount",
  last_checked_at AS "LastCheckedAt",
  last_qualified_at AS "LastQualifiedAt",
  last_error AS "LastError",
  details AS "DetailsJson"
FROM runtime_capability_state
ORDER BY capability_key;
""", cancellationToken: ct))).ToDictionary(static row => row.CapabilityKey, StringComparer.Ordinal);

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

        var existingStates = await LoadCapabilityStateRowsAsync(conn, ct);
        var current = existingStates.TryGetValue(definition.Key, out var row)
            ? ProjectCapabilityState(definition, MapStateRow(row), options, rag)
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

    private static async Task<AdminRuntimeCapabilityAEnrichmentCandidateDto[]> LoadCapabilityAEnrichmentCandidatesAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        IngestionOptions ingest,
        string? category,
        int limit,
        IReadOnlyList<string>? reasonFilters,
        CancellationToken ct)
    {
        var normalizedCategory = string.IsNullOrWhiteSpace(category) ? null : category.Trim().ToLowerInvariant();
        var rows = await conn.QueryAsync<CapabilityAEnrichmentCandidateRow>(new CommandDefinition(
            """
WITH current_docs AS (
  SELECT
    d.doc_id AS "DocId",
    d.doc_path AS "DocPath",
    d.doc_name AS "DocName",
    d.category AS "Category",
    d.status AS "Status",
    COALESCE(d.ingestion_version, 0) AS "IngestionVersion",
    COALESCE(d.indexed_version, 0) AS "IndexedVersion",
    COALESCE(d.auto_ingest_paused, false) AS "AutoIngestPaused",
    d.auto_ingest_pause_reason AS "AutoIngestPauseReason"
  FROM documents d
  WHERE d.tenant_id = @tenant
    AND (@category IS NULL OR d.category = @category)
    AND COALESCE(d.status, '') NOT IN ('missing', 'deleted')
)
SELECT
  cd."DocId",
  cd."DocPath",
  cd."DocName",
  cd."Category",
  cd."Status",
  cd."IngestionVersion",
  cd."IndexedVersion",
  cd."AutoIngestPaused",
  cd."AutoIngestPauseReason",
  EXISTS(
    SELECT 1
    FROM document_revisions dr
    WHERE dr.tenant_id = @tenant
      AND dr.doc_id = cd."DocId"
      AND dr.indexed_version = cd."IndexedVersion") AS "HasRevision",
  EXISTS(
    SELECT 1
    FROM document_revisions dr
    JOIN retrieval_chunks rc ON rc.revision_id = dr.revision_id
    WHERE dr.tenant_id = @tenant
      AND dr.doc_id = cd."DocId"
      AND dr.indexed_version = cd."IndexedVersion") AS "HasRetrievalChunks",
  EXISTS(
    SELECT 1
    FROM document_revisions dr
    JOIN exact_match_entries eme ON eme.revision_id = dr.revision_id
    WHERE dr.tenant_id = @tenant
      AND dr.doc_id = cd."DocId"
      AND dr.indexed_version = cd."IndexedVersion") AS "HasExactMatchEntries",
  EXISTS(
    SELECT 1
    FROM document_revisions dr
    JOIN contextual_text_entries cte ON cte.revision_id = dr.revision_id
    WHERE dr.tenant_id = @tenant
      AND dr.doc_id = cd."DocId"
      AND dr.indexed_version = cd."IndexedVersion") AS "HasContextualTextEntries"
FROM current_docs cd
ORDER BY cd."Category", cd."DocPath"
LIMIT @limit;
""",
            new { tenant = tenantId, category = normalizedCategory, limit = Math.Clamp(limit, 1, 200) },
            cancellationToken: ct));

        var reasonFilterSet = reasonFilters?
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .Select(reason => reason.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = new List<AdminRuntimeCapabilityAEnrichmentCandidateDto>();
        foreach (var row in rows)
        {
            var reasons = BuildCapabilityAReasons(row);
            if (reasons.Count == 0)
                continue;

            if (reasonFilterSet is not null && reasonFilterSet.Count > 0 && !reasons.Any(reason => reasonFilterSet.Contains(reason)))
                continue;

            var fileExists = ResolveCandidateFileExists(ingest, row.DocPath);
            if (!fileExists)
                reasons.Add("document_file_not_found");

            var recommendedAction = fileExists ? "enqueue_reindex" : "inspect_document_source";
            var priorityScore = BuildCapabilityAPriorityScore(reasons, row);

            candidates.Add(new AdminRuntimeCapabilityAEnrichmentCandidateDto(
                DocId: row.DocId,
                DocPath: row.DocPath,
                DocName: row.DocName,
                Category: row.Category,
                Status: row.Status,
                IngestionVersion: row.IngestionVersion,
                IndexedVersion: row.IndexedVersion,
                PriorityScore: priorityScore,
                FileExists: fileExists,
                RecommendedAction: recommendedAction,
                Reasons: reasons
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(reason => reason.Equals("document_file_not_found", StringComparison.OrdinalIgnoreCase))
                    .ThenBy(reason => reason, StringComparer.OrdinalIgnoreCase)
                    .ToArray()));
        }

        return candidates
            .OrderByDescending(candidate => candidate.PriorityScore)
            .ThenBy(candidate => candidate.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.DocPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    private static async Task<AdminRuntimeCapabilityACampaignDto[]> LoadCapabilityACampaignsAsync(
        NpgsqlConnection conn,
        int limit,
        CancellationToken ct)
        => (await conn.QueryAsync<CapabilityACampaignRow>(new CommandDefinition(
            """
SELECT
  CAST(details ->> 'campaignId' AS uuid) AS "CampaignId",
  capability_key AS "CapabilityKey",
  profile_key AS "ProfileKey",
  event_type AS "EventType",
  COALESCE((details ->> 'dryRun')::boolean, false) AS "DryRun",
  COALESCE((details ->> 'allowUnsafeCandidates')::boolean, false) AS "AllowUnsafeCandidates",
  COALESCE((details ->> 'candidateCount')::integer, 0) AS "CandidateCount",
  COALESCE((details ->> 'plannedCount')::integer, 0) AS "PlannedCount",
  COALESCE((details ->> 'queuedCount')::integer, 0) AS "QueuedCount",
  COALESCE((details ->> 'skippedCount')::integer, 0) AS "SkippedCount",
  occurred_at AS "OccurredAt",
  details AS "DetailsJson"
FROM runtime_capability_events
WHERE capability_key = @capabilityKey
  AND event_type IN ('capability_a_campaign_dry_run', 'capability_a_campaign_executed')
  AND details ? 'campaignId'
ORDER BY occurred_at DESC
LIMIT @limit;
""",
            new { capabilityKey = CapabilityACorpusEnrichmentKey, limit },
            cancellationToken: ct)))
            .Select(MapCapabilityACampaignRow)
            .ToArray();

    private static async Task<CapabilityACampaignRow?> LoadCapabilityACampaignRowAsync(
        NpgsqlConnection conn,
        Guid campaignId,
        CancellationToken ct)
        => await conn.QueryFirstOrDefaultAsync<CapabilityACampaignRow>(new CommandDefinition(
            """
SELECT
  CAST(details ->> 'campaignId' AS uuid) AS "CampaignId",
  capability_key AS "CapabilityKey",
  profile_key AS "ProfileKey",
  event_type AS "EventType",
  COALESCE((details ->> 'dryRun')::boolean, false) AS "DryRun",
  COALESCE((details ->> 'allowUnsafeCandidates')::boolean, false) AS "AllowUnsafeCandidates",
  COALESCE((details ->> 'candidateCount')::integer, 0) AS "CandidateCount",
  COALESCE((details ->> 'plannedCount')::integer, 0) AS "PlannedCount",
  COALESCE((details ->> 'queuedCount')::integer, 0) AS "QueuedCount",
  COALESCE((details ->> 'skippedCount')::integer, 0) AS "SkippedCount",
  occurred_at AS "OccurredAt",
  details AS "DetailsJson"
FROM runtime_capability_events
WHERE capability_key = @capabilityKey
  AND event_type IN ('capability_a_campaign_dry_run', 'capability_a_campaign_executed')
  AND details ->> 'campaignId' = @campaignId
LIMIT 1;
""",
            new
            {
                capabilityKey = CapabilityACorpusEnrichmentKey,
                campaignId = campaignId.ToString()
            },
            cancellationToken: ct));

    private static async Task<AdminRuntimeCapabilityACampaignDetailDto> BuildCapabilityACampaignDetailAsync(
        NpgsqlConnection conn,
        CapabilityACampaignRow row,
        CancellationToken ct)
    {
        var items = await LoadCapabilityACampaignItemsAsync(conn, row.CampaignId, row.DetailsJson, ct);
        var summary = MapCapabilityACampaignRow(row);
        return new AdminRuntimeCapabilityACampaignDetailDto(
            CampaignId: summary.CampaignId,
            CapabilityKey: summary.CapabilityKey,
            ProfileKey: summary.ProfileKey,
            Status: summary.Status,
            DryRun: summary.DryRun,
            AllowUnsafeCandidates: summary.AllowUnsafeCandidates,
            CandidateCount: summary.CandidateCount,
            PlannedCount: summary.PlannedCount,
            QueuedCount: summary.QueuedCount,
            SkippedCount: summary.SkippedCount,
            ReasonCounts: summary.ReasonCounts,
            OccurredAt: summary.OccurredAt,
            Items: items);
    }

    private static AdminRuntimeCapabilityACampaignDto MapCapabilityACampaignRow(CapabilityACampaignRow row)
        => new(
            CampaignId: row.CampaignId,
            CapabilityKey: row.CapabilityKey,
            ProfileKey: row.ProfileKey,
            Status: string.Equals(row.EventType, "capability_a_campaign_dry_run", StringComparison.Ordinal)
                ? "dry_run"
                : "executed",
            DryRun: row.DryRun,
            AllowUnsafeCandidates: row.AllowUnsafeCandidates,
            CandidateCount: row.CandidateCount,
            PlannedCount: row.PlannedCount,
            QueuedCount: row.QueuedCount,
            SkippedCount: row.SkippedCount,
            ReasonCounts: ParseCapabilityAReasonCounts(row.DetailsJson),
            OccurredAt: row.OccurredAt);

    private static async Task<AdminRuntimeCapabilityAEnqueueItemDto[]> LoadCapabilityACampaignItemsAsync(
        NpgsqlConnection conn,
        Guid campaignId,
        string? detailsJson,
        CancellationToken ct)
    {
        var items = ParseCapabilityACampaignItems(detailsJson);
        if (items.Length > 0)
            return items;

        return (await conn.QueryAsync<CapabilityACampaignItemRow>(new CommandDefinition(
            """
SELECT
  CASE
    WHEN details ? 'docId' AND NULLIF(details ->> 'docId', '') IS NOT NULL
      THEN CAST(details ->> 'docId' AS uuid)
    ELSE NULL
  END AS "DocId",
  details ->> 'docPath' AS "DocPath",
  CASE
    WHEN details ? 'jobId' AND NULLIF(details ->> 'jobId', '') IS NOT NULL
      THEN CAST(details ->> 'jobId' AS uuid)
    ELSE NULL
  END AS "JobId",
  occurred_at AS "OccurredAt"
FROM runtime_capability_events
WHERE capability_key = @capabilityKey
  AND event_type = 'capability_a_enqueued'
  AND details ->> 'campaignId' = @campaignId
ORDER BY occurred_at ASC;
""",
            new
            {
                capabilityKey = CapabilityACorpusEnrichmentKey,
                campaignId = campaignId.ToString()
            },
            cancellationToken: ct)))
            .Select(static row => new AdminRuntimeCapabilityAEnqueueItemDto(
                DocId: row.DocId,
                DocPath: row.DocPath,
                Queued: true,
                JobId: row.JobId))
            .ToArray();
    }

    internal sealed record HardwareGateResult(
        bool Passed,
        IReadOnlyDictionary<string, object?> Details,
        string? Error);

    private sealed record RuntimeSpecificGateResult(
        bool Passed,
        IReadOnlyDictionary<string, object?> Details,
        string? Error);

    private sealed record QualificationFingerprint(
        string Hash,
        IReadOnlyDictionary<string, object?> Inputs);

    private sealed record QualificationFreshness(
        bool IsExpired,
        double? QualificationAgeHours,
        DateTimeOffset? ExpiresAt);
}
