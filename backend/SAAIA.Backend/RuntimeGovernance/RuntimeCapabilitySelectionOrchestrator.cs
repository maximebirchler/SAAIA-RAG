using Npgsql;
using SAAIA.Backend.Models;

namespace SAAIA.Backend;

using CapabilitySelectionUpdateResult = RuntimeGovernanceService.CapabilitySelectionUpdateResult;

internal static class RuntimeCapabilitySelectionOrchestrator
{
    internal static async Task<CapabilitySelectionUpdateResult> UpdateSelectionAsync(
        NpgsqlConnection conn,
        RuntimeCapabilityDefinition definition,
        IReadOnlyDictionary<string, AdminRuntimeCapabilityStateDto> existingStates,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        AdminRuntimeCapabilitySelectionRequestDto? req,
        CancellationToken ct)
    {
        var current = RuntimeCapabilityStateResolver.ResolveState(
            definition,
            existingStates.TryGetValue(definition.Key, out var row) ? row : null,
            options,
            rag);
        var decision = RuntimeCapabilitySelectionCoordinator.EvaluateSelectionUpdate(current, req);

        if (!decision.Accepted)
        {
            await RuntimeCapabilityPersistenceStore.InsertCapabilityEventAsync(
                conn,
                RuntimeGovernanceService.CreateCapabilityEvent(
                    capabilityKey: definition.Key,
                    profileKey: current.ProfileKey,
                    eventType: "selection_rejected",
                    reason: decision.Error,
                    details: decision.EventDetails),
                ct);

            return new CapabilitySelectionUpdateResult(current, decision.Error);
        }

        var updated = decision.State;
        await RuntimeCapabilityPersistenceStore.PersistStateWithEventAsync(
            conn,
            updated,
            RuntimeGovernanceService.CreateCapabilityEvent(
                capabilityKey: updated.Key,
                profileKey: updated.ProfileKey,
                eventType: "selection_updated",
                reason: "selection_updated",
                details: decision.EventDetails),
            ct);

        return new CapabilitySelectionUpdateResult(updated, null);
    }
}
