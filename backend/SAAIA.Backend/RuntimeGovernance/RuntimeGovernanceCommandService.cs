using System.Diagnostics;
using Npgsql;
using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal static class RuntimeGovernanceCommandService
{
    private const string CdcAlignment = "v3.1";
    private const string AdminRuntimeActor = "admin_runtime_endpoint";

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
}
