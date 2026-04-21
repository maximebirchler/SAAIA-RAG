using System.Diagnostics;
using Npgsql;
using SAAIA.Backend.Models;
using SAAIA.Backend.Shared;

namespace SAAIA.Backend;

internal static class RuntimeGovernanceReadService
{
    private const string CdcAlignment = "v3.0";
    private const string CapabilityStateArtifact = "capability_state.json";
    private const string WarmupResultsArtifact = "warmup_results.json";
    private const string EventsArtifact = "runtime_events";
    private const string EventsJsonArtifact = "runtime_events.json";
    private const string DiagnosticsArtifact = "runtime_diagnostics";
    private const string DiagnosticsJsonArtifact = "diagnostics.json";
    private const string OperationalSummaryArtifact = "runtime_operational_summary";
    private const string OperationalSummaryJsonArtifact = "operational_summary.json";
    private const string CapabilityACorpusEnrichmentKey = "capability_a.corpus_enrichment";
    private const string CapabilityBBackofficeGenerationKey = "capability_b.backoffice_generation";

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
        Guid tenantId,
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
            var capabilityAOperationalSummary = await RuntimeCapabilityDiagnosticsBuilder.LoadCapabilityAOperationalSummaryAsync(
                conn,
                tenantId,
                CapabilityACorpusEnrichmentKey,
                ct);
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
                    string.Equals(state.Key, CapabilityACorpusEnrichmentKey, StringComparison.Ordinal)
                        ? capabilityAOperationalSummary
                        : string.Equals(state.Key, CapabilityBBackofficeGenerationKey, StringComparison.Ordinal)
                            ? capabilityBOperationalSummary
                            : null))
                .ToArray();
            var summary = RuntimeCapabilityDiagnosticsBuilder.BuildDiagnosticsSummary(
                items,
                CapabilityACorpusEnrichmentKey,
                CapabilityBBackofficeGenerationKey);

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
        Guid tenantId,
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHostEnvironment env,
        CancellationToken ct)
        => await ExecuteArtifactReadAsync(
            DiagnosticsJsonArtifact,
            async () =>
            {
                var diagnostics = await GetDiagnosticsAsync(tenantId, ds, options, rag, env, ct);
                return new AdminRuntimeDiagnosticsArtifactDto(
                    DiagnosticsJsonArtifact,
                    diagnostics.CdcAlignment,
                    diagnostics.Environment,
                    diagnostics.GeneratedAt,
                    diagnostics.Summary,
                    diagnostics.Items);
            });

    internal static async Task<AdminRuntimeOperationalSummaryResponseDto> GetOperationalSummaryAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHostEnvironment env,
        CancellationToken ct)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity(OperationalSummaryArtifact);
        var sw = Stopwatch.StartNew();
        try
        {
            var diagnostics = await GetDiagnosticsAsync(tenantId, ds, options, rag, env, ct);
            var summary = diagnostics.Summary.Operational ?? new AdminRuntimeDiagnosticsOperationalSummaryDto(
                CapabilityACandidateCount: 0,
                CapabilityAReadyToEnqueueCount: 0,
                CapabilityAOffsetBackfillCandidateCount: 0,
                CapabilityBBacklogCount: 0,
                CapabilityBReadyToEnqueueCount: 0,
                CapabilityBActiveJobCount: 0,
                CapabilityBLatestCampaignProgressPercent: null);
            var items = RuntimeCapabilityDiagnosticsBuilder.BuildOperationalItems(diagnostics.Items);

            sw.Stop();
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, OperationalSummaryArtifact, success: true, durationMs: sw.ElapsedMilliseconds);
            return new AdminRuntimeOperationalSummaryResponseDto(
                diagnostics.CdcAlignment,
                diagnostics.Environment,
                diagnostics.GeneratedAt,
                summary,
                items);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RuntimeGovernanceTelemetry.MarkError(activity, ex);
            RuntimeGovernanceTelemetry.CompleteArtifactRead(activity, OperationalSummaryArtifact, success: false, durationMs: sw.ElapsedMilliseconds);
            throw;
        }
    }

    internal static async Task<AdminRuntimeOperationalSummaryArtifactDto> GetOperationalSummaryArtifactAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHostEnvironment env,
        CancellationToken ct)
        => await ExecuteArtifactReadAsync(
            OperationalSummaryJsonArtifact,
            async () =>
            {
                var operational = await GetOperationalSummaryAsync(tenantId, ds, options, rag, env, ct);
                return new AdminRuntimeOperationalSummaryArtifactDto(
                    OperationalSummaryJsonArtifact,
                    operational.CdcAlignment,
                    operational.Environment,
                    operational.GeneratedAt,
                    operational.Summary,
                    operational.Items);
            });

    internal static async Task<AdminRuntimeCapabilityStateArtifactDto> GetCapabilityStateArtifactAsync(
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHostEnvironment env,
        CancellationToken ct)
        => await ExecuteArtifactReadAsync(
            CapabilityStateArtifact,
            async () =>
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

                return new AdminRuntimeCapabilityStateArtifactDto(
                    CapabilityStateArtifact,
                    CdcAlignment,
                    env.EnvironmentName,
                    DateTimeOffset.UtcNow,
                    items);
            });

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
        => await ExecuteArtifactReadAsync(
            WarmupResultsArtifact,
            async () =>
            {
                await using var conn = await ds.OpenConnectionAsync(ct);
                var items = await RuntimeCapabilityHistoryStore.LoadWarmupResultsAsync(conn, capabilityKey, Math.Clamp(limit, 1, 100), ct);
                return new AdminRuntimeWarmupResultsArtifactDto(
                    WarmupResultsArtifact,
                    CdcAlignment,
                    env.EnvironmentName,
                    DateTimeOffset.UtcNow,
                    items);
            });

    internal static async Task<AdminRuntimeEventsResponseDto> GetEventsAsync(
        NpgsqlDataSource ds,
        IHostEnvironment env,
        string? capabilityKey,
        int limit,
        CancellationToken ct)
        => await ExecuteArtifactReadAsync(
            EventsArtifact,
            async () =>
            {
                await using var conn = await ds.OpenConnectionAsync(ct);
                var items = await RuntimeCapabilityHistoryStore.LoadCapabilityEventsAsync(conn, capabilityKey, Math.Clamp(limit, 1, 200), ct);
                return new AdminRuntimeEventsResponseDto(CdcAlignment, env.EnvironmentName, items);
            });

    internal static async Task<AdminRuntimeEventsArtifactDto> GetEventsArtifactAsync(
        NpgsqlDataSource ds,
        IHostEnvironment env,
        string? capabilityKey,
        int limit,
        CancellationToken ct)
        => await ExecuteArtifactReadAsync(
            EventsJsonArtifact,
            async () =>
            {
                await using var conn = await ds.OpenConnectionAsync(ct);
                var items = await RuntimeCapabilityHistoryStore.LoadCapabilityEventsAsync(conn, capabilityKey, Math.Clamp(limit, 1, 200), ct);
                return new AdminRuntimeEventsArtifactDto(
                    EventsJsonArtifact,
                    CdcAlignment,
                    env.EnvironmentName,
                    DateTimeOffset.UtcNow,
                    items);
            });

    private static async Task<T> ExecuteArtifactReadAsync<T>(string artifactName, Func<Task<T>> build)
    {
        using var activity = RuntimeGovernanceTelemetry.StartArtifactReadActivity(artifactName);
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await build();
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
}
