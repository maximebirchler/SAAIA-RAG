using System.Net.Http;
using Npgsql;
using SAAIA.Backend.Models;

namespace SAAIA.Backend;

internal static class RuntimeCapabilityLifecycleCoordinator
{
    internal static async Task<RuntimeCapabilityRequalifyBatchResult> RequalifyAsync(
        NpgsqlConnection conn,
        IReadOnlyList<RuntimeCapabilityDefinition> definitions,
        AdminRuntimeWarmupProfileDto profile,
        IReadOnlyDictionary<string, AdminRuntimeCapabilityStateDto> existingStates,
        bool? selectWhenQualified,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        IHttpClientFactory httpFactory,
        CancellationToken ct)
    {
        var items = new List<AdminRuntimeCapabilityStateDto>(definitions.Count);
        var warmupResults = new List<AdminRuntimeWarmupResultDto>();

        foreach (var definition in definitions)
        {
            var evaluation = await EvaluateCapabilityAsync(
                definition,
                profile,
                existingStates.TryGetValue(definition.Key, out var existingState) ? existingState : null,
                selectWhenQualified,
                options,
                rag,
                httpFactory,
                ct);

            var requalifiedEvent = RuntimeGovernanceService.CreateCapabilityEvent(
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
                });

            await RuntimeCapabilityPersistenceStore.PersistEvaluationAsync(
                conn,
                evaluation.State,
                evaluation.WarmupResult,
                requalifiedEvent,
                ct);

            items.Add(evaluation.State);
            if (evaluation.WarmupResult is not null)
                warmupResults.Add(evaluation.WarmupResult);
        }

        return new RuntimeCapabilityRequalifyBatchResult(items.ToArray(), warmupResults.ToArray());
    }

    internal static async Task<AdminRuntimeCapabilityStateDto[]> ReconcileStaleAsync(
        NpgsqlConnection conn,
        IReadOnlyList<RuntimeCapabilityDefinition> definitions,
        IReadOnlyDictionary<string, AdminRuntimeCapabilityStateDto> existingStates,
        RuntimeGovernanceOptions options,
        RagOptions rag,
        string actor,
        CancellationToken ct)
    {
        var items = new List<AdminRuntimeCapabilityStateDto>();

        foreach (var definition in definitions)
        {
            if (!existingStates.TryGetValue(definition.Key, out var row))
                continue;

            var current = RuntimeCapabilityStateResolver.ResolveState(definition, row, options, rag);
            var reconciliation = RuntimeCapabilitySelectionCoordinator.BuildStaleReconciliation(current, actor);
            if (reconciliation is null)
                continue;

            var updated = reconciliation.UpdatedState;
            await RuntimeCapabilityPersistenceStore.PersistStateWithEventAsync(
                conn,
                updated,
                RuntimeGovernanceService.CreateCapabilityEvent(
                    capabilityKey: updated.Key,
                    profileKey: updated.ProfileKey,
                    eventType: "stale_reconciled",
                    reason: reconciliation.StaleReason,
                    details: reconciliation.EventDetails),
                ct);

            RuntimeGovernanceTelemetry.RecordStaleQualificationReconciled(updated.Key, updated.ProfileKey, reconciliation.StaleReason);
            items.Add(updated);
        }

        return items.ToArray();
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

        if (string.Equals(definition.Key, "capability_a.corpus_enrichment", StringComparison.Ordinal))
            return await RuntimeCapabilityAdminEvaluators.EvaluateCapabilityAAsync(
                definition,
                profile,
                existingState,
                selectWhenQualified,
                options,
                rag,
                "capability_a.corpus_enrichment",
                ct);

        if (string.Equals(definition.Key, "capability_b.backoffice_generation", StringComparison.Ordinal))
            return await RuntimeCapabilityAdminEvaluators.EvaluateCapabilityBAsync(
                definition,
                profile,
                existingState,
                selectWhenQualified,
                options,
                rag,
                "capability_b.backoffice_generation",
                ct);

        return await RuntimeCoreRetrievalWarmupEvaluator.EvaluateAsync(
            definition,
            profile,
            existingState,
            selectWhenQualified,
            options,
            rag,
            httpFactory,
            ct);
    }

    internal sealed record RuntimeCapabilityRequalifyBatchResult(
        IReadOnlyList<AdminRuntimeCapabilityStateDto> Items,
        IReadOnlyList<AdminRuntimeWarmupResultDto> WarmupResults);
}
