using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

internal sealed record BatteryPoliciesArtifact(
    string Artifact,
    string CdcAlignment,
    IReadOnlyList<BatteryPolicyItem> Items);

internal sealed record BatteryPolicyItem(
    string Key,
    string Mode,
    int IdleTimeoutSecondsAc,
    int IdleTimeoutSecondsBattery,
    bool EagerLoadAllowedOnBattery,
    string? OnBatteryFallbackProfileRef);

internal sealed record BatteryPolicyDecision(
    bool RequiresRequalification,
    string Reason,
    string? RecommendedProfileRef,
    int? EffectiveIdleTimeoutSeconds);

internal static class BatteryPolicyStore
{
    public static BatteryPoliciesArtifact CreateDefaultPolicies() => new(
        GovernanceArtifactStore.BatteryPoliciesFile,
        "v3.1",
        new[]
        {
            new BatteryPolicyItem(
                Key: "client-perf",
                Mode: "perf",
                IdleTimeoutSecondsAc: 180,
                IdleTimeoutSecondsBattery: 90,
                EagerLoadAllowedOnBattery: true,
                OnBatteryFallbackProfileRef: null),
            new BatteryPolicyItem(
                Key: "client-balanced",
                Mode: "balanced",
                IdleTimeoutSecondsAc: 120,
                IdleTimeoutSecondsBattery: 60,
                EagerLoadAllowedOnBattery: false,
                OnBatteryFallbackProfileRef: "qwen3-4b-2507-q5km-cuda-4gb-stable"),
            new BatteryPolicyItem(
                Key: "client-eco",
                Mode: "eco",
                IdleTimeoutSecondsAc: 90,
                IdleTimeoutSecondsBattery: 30,
                EagerLoadAllowedOnBattery: false,
                OnBatteryFallbackProfileRef: "qwen3-4b-2507-q5km-cpu-safe")
        });

    public static async Task<BatteryPolicyDecision> EvaluateAsync(
        QualifiedProfile profile,
        string? root = null,
        CancellationToken ct = default)
    {
        var policies = await GovernanceArtifactStore.ReadAsync<BatteryPoliciesArtifact>(
            GovernanceArtifactStore.BatteryPoliciesFile,
            root,
            ct).ConfigureAwait(false);
        if (policies.Status != GovernanceArtifactReadStatus.Ok || policies.Value is null)
            return new BatteryPolicyDecision(false, "battery_policy_unavailable", null, null);

        var policy = policies.Value.Items.FirstOrDefault(item =>
            string.Equals(item.Key, profile.BatteryPolicyRef, StringComparison.OrdinalIgnoreCase));
        if (policy is null)
            return new BatteryPolicyDecision(false, "battery_policy_missing", null, null);

        var hardware = await GovernanceArtifactStore.ReadAsync<HardwareProbeArtifact>(
            GovernanceArtifactStore.HardwareProbeFile,
            root,
            ct).ConfigureAwait(false);
        if (hardware.Status != GovernanceArtifactReadStatus.Ok || hardware.Value is null)
            return new BatteryPolicyDecision(false, "hardware_probe_unavailable", null, null);

        var isOnBattery = TryGetBool(hardware.Value.Hardware, "isOnBattery");
        if (isOnBattery is null)
            return new BatteryPolicyDecision(false, "battery_state_unknown", null, null);

        if (isOnBattery.Value)
        {
            // Measured profiles carry a machine-specific fallback chain. Prefer it over
            // the historical reference-machine fallback embedded in the default policy.
            var fallback = !string.IsNullOrWhiteSpace(profile.FallbackProfileRef)
                ? profile.FallbackProfileRef
                : policy.OnBatteryFallbackProfileRef;
            var requiresRequalification = !string.IsNullOrWhiteSpace(fallback)
                && !string.Equals(fallback, profile.ProfileId, StringComparison.OrdinalIgnoreCase);
            return new BatteryPolicyDecision(
                requiresRequalification,
                requiresRequalification ? "battery_policy_recommends_fallback" : "battery_policy_on_battery",
                fallback,
                policy.IdleTimeoutSecondsBattery);
        }

        return new BatteryPolicyDecision(
            false,
            "battery_policy_on_ac",
            null,
            policy.IdleTimeoutSecondsAc);
    }

    private static bool? TryGetBool(IReadOnlyDictionary<string, object?> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ => null
        };
    }
}
