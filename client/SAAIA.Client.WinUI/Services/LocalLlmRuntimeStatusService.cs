using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

internal sealed record LocalLlmRuntimeStatus(
    string Code,
    string Message,
    bool IsWarning = false,
    bool IsError = false);

internal static class LocalLlmRuntimeStatusService
{
    public static async Task<LocalLlmRuntimeStatus?> EvaluateAsync(
        AppSettings settings,
        string? root = null,
        CancellationToken ct = default)
    {
        if (!settings.UseLocalLlm || !settings.ManageLocalLlmProcess || settings.QualifiedProfile is null)
            return null;

        var warmupRead = await GovernanceArtifactStore.ReadAsync<WarmupResultsArtifact>(
            GovernanceArtifactStore.WarmupResultsFile,
            root,
            ct).ConfigureAwait(false);
        var latest = warmupRead.Status == GovernanceArtifactReadStatus.Ok && warmupRead.Value is not null
            ? warmupRead.Value.Items.FirstOrDefault(item =>
                string.Equals(item.ProfileId, settings.QualifiedProfile.ProfileId, StringComparison.OrdinalIgnoreCase))
            : null;

        if (latest?.Status == WarmupGateStatus.FailBlock)
        {
            return new LocalLlmRuntimeStatus(
                "runtime_unavailable",
                "Assistant temporairement indisponible.",
                IsError: true);
        }

        var profile = WarmupProfileStore.FindProfile(settings.QualifiedProfile.ProfileId);
        if (profile?.Mode == "fallback")
        {
            return new LocalLlmRuntimeStatus(
                "fallback_active",
                "Profil de secours actif.",
                IsWarning: true);
        }

        if (latest?.Status == WarmupGateStatus.PassDegraded)
        {
            return new LocalLlmRuntimeStatus(
                "performance_reduced",
                "Mode performance reduite actif.",
                IsWarning: true);
        }

        var drift = RequalificationTriggerService.EvaluateProfileDrift(settings);
        if (drift.Required)
        {
            return new LocalLlmRuntimeStatus(
                "requalification_required",
                "Requalification necessaire.",
                IsWarning: true);
        }

        var battery = await BatteryPolicyStore.EvaluateAsync(settings.QualifiedProfile, root, ct).ConfigureAwait(false);
        if (battery.RequiresRequalification)
        {
            return new LocalLlmRuntimeStatus(
                "battery_policy_warning",
                "Mode performance reduite recommande sur batterie.",
                IsWarning: true);
        }

        return null;
    }
}
