using System.Diagnostics;
using Npgsql;
using SAAIA.Backend.Models;
using SAAIA.Backend.Shared;

namespace SAAIA.Backend;

internal static class RuntimeGovernanceReadService
{
    private const string CdcAlignment = "v3.1";
    private const string CapabilityStateArtifact = "capability_state.json";
    private const string WarmupResultsArtifact = "warmup_results.json";
    private const string EventsArtifact = "runtime_events";
    private const string EventsJsonArtifact = "runtime_events.json";
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
        => await new RuntimeDiagnosticsService(ds, env).GetDiagnosticsAsync(tenantId, options, rag, ct);

    internal static async Task<AdminRuntimeDiagnosticsArtifactDto> GetDiagnosticsArtifactAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHostEnvironment env,
        CancellationToken ct)
        => await new RuntimeDiagnosticsService(ds, env).GetDiagnosticsArtifactAsync(tenantId, options, rag, ct);

    internal static async Task<AdminRuntimeOperationalSummaryResponseDto> GetOperationalSummaryAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHostEnvironment env,
        CancellationToken ct)
        => await new RuntimeDiagnosticsService(ds, env).GetOperationalSummaryAsync(tenantId, options, rag, ct);

    internal static async Task<AdminRuntimeOperationalSummaryArtifactDto> GetOperationalSummaryArtifactAsync(
        Guid tenantId,
        NpgsqlDataSource ds,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHostEnvironment env,
        CancellationToken ct)
        => await new RuntimeDiagnosticsService(ds, env).GetOperationalSummaryArtifactAsync(tenantId, options, rag, ct);

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
