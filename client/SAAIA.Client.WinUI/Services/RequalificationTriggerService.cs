using System;
using System.Collections.Generic;
using System.Linq;

namespace SAAIA.Client.WinUI.Services;

internal sealed record RequalificationDecision(
    bool Required,
    string Reason);

internal static class RequalificationTriggerService
{
    private const int DefaultRepeatedFailureThreshold = 3;
    private const int DefaultTimeoutMs = 120_000;
    private const double DefaultTokPerSecDropRatio = 0.35;
    private const double DefaultTtftIncreaseRatio = 0.50;

    public static RequalificationDecision EvaluateProfileDrift(
        AppSettings settings,
        QualifiedProfile? registeredReference = null)
    {
        if (settings.QualifiedProfile is null)
            return new RequalificationDecision(false, "qualified_profile_missing");

        var currentRuntime = DetectRuntimeKey(settings.LlamaExePath);
        if (!string.Equals(currentRuntime, settings.QualifiedProfile.Runtime, StringComparison.OrdinalIgnoreCase))
        {
            return new RequalificationDecision(
                true,
                $"runtime_changed:{settings.QualifiedProfile.Runtime}->{currentRuntime}");
        }

        var currentModelId = ModelCatalogStore.ResolveCanonicalModelId(settings.ModelId)
            ?? ModelCatalogStore.ResolveCanonicalModelId(settings.ModelPath is null ? null : System.IO.Path.GetFileName(settings.ModelPath))
            ?? settings.ModelId;

        if (!string.Equals(currentModelId, settings.QualifiedProfile.ModelId, StringComparison.OrdinalIgnoreCase))
        {
            return new RequalificationDecision(
                true,
                $"model_changed:{settings.QualifiedProfile.ModelId}->{currentModelId}");
        }

        var reference = registeredReference
                        ?? WarmupProfileStore.FindProfile(settings.QualifiedProfile.ProfileId)?.Candidate;
        if (reference is not null && HasProfileConfigurationDrift(settings.QualifiedProfile, reference))
        {
            return new RequalificationDecision(
                true,
                $"profile_changed:{settings.QualifiedProfile.ProfileId}");
        }

        return new RequalificationDecision(false, "profile_unchanged");
    }

    internal static bool HasProfileConfigurationDrift(QualifiedProfile stored, QualifiedProfile reference)
        => !string.Equals(stored.ProfileId, reference.ProfileId, StringComparison.OrdinalIgnoreCase)
           || !string.Equals(stored.Runtime, reference.Runtime, StringComparison.OrdinalIgnoreCase)
           || !string.Equals(stored.ModelId, reference.ModelId, StringComparison.OrdinalIgnoreCase)
           || stored.CtxSize != reference.CtxSize
           || stored.BatchSize != reference.BatchSize
           || stored.UbatchSize != reference.UbatchSize
           || stored.Threads != reference.Threads
           || stored.ThreadsBatch != reference.ThreadsBatch
           || stored.Ngl != reference.Ngl
           || stored.FlashAttn != reference.FlashAttn
           || stored.Mlock != reference.Mlock
           || !(stored.DeviceIds ?? Array.Empty<string>()).SequenceEqual(
               reference.DeviceIds ?? Array.Empty<string>(),
               StringComparer.OrdinalIgnoreCase)
           || !string.Equals(stored.SplitMode, reference.SplitMode, StringComparison.OrdinalIgnoreCase)
           || !(stored.TensorSplit ?? Array.Empty<double>()).SequenceEqual(
               reference.TensorSplit ?? Array.Empty<double>())
           || stored.MainGpu != reference.MainGpu
           || !string.Equals(stored.CacheTypeK, reference.CacheTypeK, StringComparison.OrdinalIgnoreCase)
           || !string.Equals(stored.CacheTypeV, reference.CacheTypeV, StringComparison.OrdinalIgnoreCase)
           || stored.Parallel != reference.Parallel
           || !string.Equals(stored.BatteryPolicyRef, reference.BatteryPolicyRef, StringComparison.OrdinalIgnoreCase)
           || !string.Equals(stored.FallbackProfileRef, reference.FallbackProfileRef, StringComparison.OrdinalIgnoreCase);

    public static RequalificationDecision EvaluateWarmupHistory(
        IReadOnlyList<WarmupResultItem> items,
        string profileId,
        int repeatedFailureThreshold = DefaultRepeatedFailureThreshold,
        int timeoutMs = DefaultTimeoutMs,
        double tokPerSecDropRatio = DefaultTokPerSecDropRatio,
        double ttftIncreaseRatio = DefaultTtftIncreaseRatio)
    {
        if (items.Count == 0)
            return new RequalificationDecision(false, "warmup_history_empty");

        var profileItems = items
            .Where(item => string.Equals(item.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.At)
            .ToArray();

        if (profileItems.Length == 0)
            return new RequalificationDecision(false, "warmup_history_profile_missing");

        var latest = profileItems[0];
        if (IsTimeout(latest, timeoutMs))
            return new RequalificationDecision(true, "timeout_threshold_exceeded");

        if (profileItems
                .Take(Math.Max(1, repeatedFailureThreshold))
                .Count(IsFailure) >= repeatedFailureThreshold)
        {
            return new RequalificationDecision(true, $"repeated_failures:{repeatedFailureThreshold}");
        }

        var baseline = profileItems
            .Skip(1)
            .FirstOrDefault(IsSuccessfulWithMetrics);

        if (baseline is null || !IsSuccessfulWithMetrics(latest))
            return new RequalificationDecision(false, "warmup_history_stable_or_insufficient");

        if (latest.LastTokPerSec is { } latestTok
            && baseline.LastTokPerSec is { } baselineTok
            && baselineTok > 0
            && latestTok < baselineTok * (1d - tokPerSecDropRatio))
        {
            return new RequalificationDecision(
                true,
                $"perf_drift_tok_per_sec:{baselineTok:0.###}->{latestTok:0.###}");
        }

        if (latest.LastTtftMs is { } latestTtft
            && baseline.LastTtftMs is { } baselineTtft
            && baselineTtft > 0
            && latestTtft > baselineTtft * (1d + ttftIncreaseRatio))
        {
            return new RequalificationDecision(
                true,
                $"perf_drift_ttft:{baselineTtft}->{latestTtft}");
        }

        return new RequalificationDecision(false, "warmup_history_stable");
    }

    public static RequalificationDecision EvaluateAdminAction(bool requested, string? requestedBy = null)
    {
        if (!requested)
            return new RequalificationDecision(false, "admin_action_not_requested");

        var suffix = string.IsNullOrWhiteSpace(requestedBy)
            ? string.Empty
            : ":" + requestedBy.Trim();
        return new RequalificationDecision(true, "admin_action" + suffix);
    }

    internal static string DetectRuntimeKey(string? exePath)
    {
        var path = (exePath ?? string.Empty).Trim();
        if (path.Contains("cuda", StringComparison.OrdinalIgnoreCase)
            || path.Contains("cu12", StringComparison.OrdinalIgnoreCase)
            || path.Contains("cublas", StringComparison.OrdinalIgnoreCase))
        {
            return "llama.cpp-cuda";
        }

        if (path.Contains("vulkan", StringComparison.OrdinalIgnoreCase))
            return "llama.cpp-vulkan";

        if (path.Contains("sycl", StringComparison.OrdinalIgnoreCase))
            return "llama.cpp-sycl";

        if (path.Contains("hip", StringComparison.OrdinalIgnoreCase)
            || path.Contains("rocm", StringComparison.OrdinalIgnoreCase))
        {
            return "llama.cpp-hip";
        }

        return "llama.cpp-cpu";
    }

    private static bool IsFailure(WarmupResultItem item)
        => item.Status is WarmupGateStatus.FailBlock or WarmupGateStatus.FailFallback
           || item.Reasons.Any(reason => reason.Contains("warmup_run_failed", StringComparison.OrdinalIgnoreCase))
           || item.LastTokPerSec is null or <= 0;

    private static bool IsTimeout(WarmupResultItem item, int timeoutMs)
        => item.LastLoadMs is { } loadMs && loadMs >= timeoutMs
           || item.LastTtftMs is { } ttftMs && ttftMs >= timeoutMs
           || item.Reasons.Any(reason => reason.Contains("timeout", StringComparison.OrdinalIgnoreCase));

    private static bool IsSuccessfulWithMetrics(WarmupResultItem item)
        => item.Status is WarmupGateStatus.Pass or WarmupGateStatus.PassDegraded
           && item.LastTokPerSec is > 0
           && item.LastTtftMs is > 0;
}
