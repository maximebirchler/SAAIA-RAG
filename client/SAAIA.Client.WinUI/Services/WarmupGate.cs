using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

internal enum WarmupGateStatus
{
    Pass,
    PassDegraded,
    FailBlock,
    FailFallback
}

internal sealed record WarmupMeasurement(
    int LoadMs,
    int TtftMs,
    double TokPerSec,
    bool Succeeded = true,
    string? Error = null,
    int? PeakRamMiB = null,
    int? PeakVramMiB = null,
    double? MsPerToken = null,
    IReadOnlyDictionary<string, double>? RuntimeMetrics = null,
    string? Scenario = null);

internal sealed record WarmupGateRequest(
    QualifiedProfile Profile,
    IReadOnlyList<WarmupMeasurement> Runs,
    string? DriverVersion = null,
    string? HardwareFingerprint = null,
    string? Trigger = null);

internal sealed record WarmupGateResult(
    WarmupGateStatus Status,
    QualifiedProfile? SelectedProfile,
    bool RollbackApplied,
    int PassCount,
    IReadOnlyList<string> Reasons);

internal sealed record WarmupResultsArtifact(
    string Artifact,
    string CdcAlignment,
    IReadOnlyList<WarmupResultItem> Items);

internal sealed record WarmupResultItem(
    DateTimeOffset At,
    string ProfileId,
    string Runtime,
    string ModelId,
    WarmupGateStatus Status,
    int PassCount,
    int WarmupPassCount,
    int? LastLoadMs,
    int? LastTtftMs,
    double? LastTokPerSec,
    double? LastMsPerToken,
    IReadOnlyDictionary<string, double>? RuntimeMetrics,
    IReadOnlyList<string> Reasons,
    string? HardwareFingerprint,
    string? Trigger);

internal static class WarmupGate
{
    public static async Task<WarmupGateResult> RunQualificationAsync(
        QualifiedProfile profile,
        string llmBaseUrl,
        string model,
        ILocalLlmWarmupHarness? harness = null,
        string? root = null,
        int? observedLoadMs = null,
        string? driverVersion = null,
        string? hardwareFingerprint = null,
        string? trigger = null,
        CancellationToken ct = default)
    {
        await GovernanceArtifactStore.EnsureDefaultArtifactsAsync(new AppSettings(), root, ct).ConfigureAwait(false);

        var profileItem = await LoadProfileAsync(profile.ProfileId, root, ct).ConfigureAwait(false);
        var runCount = profileItem?.Thresholds.WarmupPassCount ?? 3;
        harness ??= new LocalLlmWarmupHarness();

        var runs = new List<WarmupMeasurement>(capacity: runCount);
        for (var i = 0; i < runCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (harness is LocalLlmWarmupHarness scenarioHarness)
            {
                var scenarioRuns = await scenarioHarness.RunContractScenariosAsync(
                    llmBaseUrl,
                    model,
                    ct: ct).ConfigureAwait(false);

                runs.Add(ApplyObservedLoad(
                    LocalLlmWarmupHarness.AggregateScenarioMeasurements(scenarioRuns),
                    observedLoadMs));
                continue;
            }

            runs.Add(ApplyObservedLoad(
                await harness.RunOnceAsync(
                    llmBaseUrl,
                    model,
                    LocalLlmWarmupHarnessOptions.Default,
                    ct).ConfigureAwait(false),
                observedLoadMs));
        }

        return await EvaluateAsync(
            new WarmupGateRequest(
                profile,
                runs,
                driverVersion,
                hardwareFingerprint,
                trigger ?? "warmup_harness"),
            root,
            ct).ConfigureAwait(false);
    }

    private static WarmupMeasurement ApplyObservedLoad(WarmupMeasurement measurement, int? observedLoadMs)
    {
        if (observedLoadMs is null || observedLoadMs.Value <= 0)
            return measurement;

        var runtimeMetrics = measurement.RuntimeMetrics is null
            ? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, double>(measurement.RuntimeMetrics, StringComparer.OrdinalIgnoreCase);
        runtimeMetrics["runtime.observed_start_load_ms"] = observedLoadMs.Value;

        return measurement with
        {
            LoadMs = Math.Max(measurement.LoadMs, observedLoadMs.Value),
            RuntimeMetrics = runtimeMetrics
        };
    }

    public static async Task<WarmupGateResult> EvaluateAsync(
        WarmupGateRequest request,
        string? root = null,
        CancellationToken ct = default)
    {
        await GovernanceArtifactStore.EnsureDefaultArtifactsAsync(new AppSettings(), root, ct).ConfigureAwait(false);

        var blacklistMatch = await BlacklistPolicy.FindMatchAsync(
            request.Profile,
            request.DriverVersion,
            root,
            ct).ConfigureAwait(false);

        if (blacklistMatch is not null)
        {
            var blocked = new WarmupGateResult(
                WarmupGateStatus.FailBlock,
                SelectedProfile: null,
                RollbackApplied: false,
                PassCount: 0,
                Reasons: new[] { $"blacklisted:{blacklistMatch.RuleId}", blacklistMatch.Reason });
            await PersistResultAsync(request, blocked, null, root, ct).ConfigureAwait(false);
            return blocked;
        }

        var profile = await LoadProfileAsync(request.Profile.ProfileId, root, ct).ConfigureAwait(false);
        if (profile is null)
        {
            var missingProfile = new WarmupGateResult(
                WarmupGateStatus.FailBlock,
                null,
                false,
                0,
                new[] { "warmup_profile_missing" });
            await PersistResultAsync(request, missingProfile, null, root, ct).ConfigureAwait(false);
            return missingProfile;
        }

        var thresholds = profile.Thresholds;
        var reasons = new List<string>();
        var hardGateReasons = await EvaluateHardGatesAsync(profile, root, ct).ConfigureAwait(false);
        if (hardGateReasons.Count > 0)
        {
            reasons.AddRange(hardGateReasons);
            return await FailWithOptionalFallbackAsync(
                request,
                thresholds,
                passCount: 0,
                reasons,
                root,
                ct).ConfigureAwait(false);
        }

        var passCount = request.Runs.Count(run => IsNominalPass(run, thresholds));
        var hasEnoughRuns = request.Runs.Count >= thresholds.WarmupPassCount;

        if (!hasEnoughRuns)
            reasons.Add("insufficient_runs");

        if (request.Runs.Any(static run => !run.Succeeded))
            reasons.Add("warmup_run_failed");

        if (passCount >= thresholds.WarmupPassCount)
        {
            var pass = new WarmupGateResult(
                WarmupGateStatus.Pass,
                request.Profile,
                false,
                passCount,
                reasons.Count == 0 ? new[] { "pass" } : reasons);
            await PersistResultAsync(request, pass, thresholds, root, ct).ConfigureAwait(false);
            await RollbackManager.SaveLastKnownGoodAsync(request.Profile, root, ct).ConfigureAwait(false);
            return pass;
        }

        if (hasEnoughRuns && request.Runs.All(run => IsDegradedPass(run, thresholds)))
        {
            reasons.Add("pass_degraded_thresholds");
            var degraded = new WarmupGateResult(
                WarmupGateStatus.PassDegraded,
                request.Profile,
                false,
                passCount,
                reasons);
            await PersistResultAsync(request, degraded, thresholds, root, ct).ConfigureAwait(false);
            return degraded;
        }

        reasons.AddRange(BuildThresholdFailureReasons(request.Runs, thresholds));

        return await FailWithOptionalFallbackAsync(
            request,
            thresholds,
            passCount,
            reasons,
            root,
            ct).ConfigureAwait(false);
    }

    private static async Task<WarmupGateResult> FailWithOptionalFallbackAsync(
        WarmupGateRequest request,
        WarmupThresholds? thresholds,
        int passCount,
        List<string> reasons,
        string? root,
        CancellationToken ct)
    {
        var lastKnownGood = await RollbackManager.ReadLastKnownGoodAsync(root, ct).ConfigureAwait(false);
        if (lastKnownGood is not null)
        {
            reasons.Add("rollback_to_last_known_good");
            await RollbackManager.AppendRollbackAsync(request.Profile, lastKnownGood, string.Join(";", reasons), root, ct).ConfigureAwait(false);
            var fallback = new WarmupGateResult(
                WarmupGateStatus.FailFallback,
                lastKnownGood,
                true,
                passCount,
                reasons);
            await PersistResultAsync(request, fallback, thresholds, root, ct).ConfigureAwait(false);
            return fallback;
        }

        reasons.Add("no_last_known_good");
        var block = new WarmupGateResult(
            WarmupGateStatus.FailBlock,
            null,
            false,
            passCount,
            reasons);
        await PersistResultAsync(request, block, thresholds, root, ct).ConfigureAwait(false);
        return block;
    }

    private static async Task<IReadOnlyList<string>> EvaluateHardGatesAsync(
        WarmupProfileItem profile,
        string? root,
        CancellationToken ct)
    {
        var reasons = new List<string>();
        var thresholds = profile.Thresholds;
        if (thresholds.MinDxgiBudgetMiB is null && thresholds.MinAvailableRamMiB is null)
            return reasons;

        var read = await GovernanceArtifactStore.ReadAsync<HardwareProbeArtifact>(
            GovernanceArtifactStore.HardwareProbeFile,
            root,
            ct).ConfigureAwait(false);

        if (read.Status != GovernanceArtifactReadStatus.Ok || read.Value is null)
        {
            reasons.Add("hard_gate_hardware_probe_unavailable");
            return reasons;
        }

        if (thresholds.MinDxgiBudgetMiB is { } minDxgi)
        {
            if (!TryGetLong(read.Value.Hardware, "dxgiBudgetMiB", out var dxgiBudgetMiB))
                reasons.Add("hard_gate_dxgi_budget_missing");
            else if (dxgiBudgetMiB < minDxgi)
                reasons.Add($"hard_gate_dxgi_budget_insufficient:{dxgiBudgetMiB}<{minDxgi}");
        }

        if (thresholds.MinAvailableRamMiB is { } minRam)
        {
            if (!TryGetLong(read.Value.Hardware, "availableRamMiB", out var availableRamMiB))
                reasons.Add("hard_gate_available_ram_missing");
            else if (availableRamMiB < minRam)
                reasons.Add($"hard_gate_available_ram_insufficient:{availableRamMiB}<{minRam}");
        }

        return reasons;
    }

    private static bool TryGetLong(
        IReadOnlyDictionary<string, object?> values,
        string key,
        out long result)
    {
        result = 0;
        if (!values.TryGetValue(key, out var value) || value is null)
            return false;

        switch (value)
        {
            case long l:
                result = l;
                return true;
            case int i:
                result = i;
                return true;
            case double d when d >= 0:
                result = (long)d;
                return true;
            case JsonElement { ValueKind: JsonValueKind.Number } json:
                return json.TryGetInt64(out result);
            default:
                return false;
        }
    }

    private static bool IsNominalPass(WarmupMeasurement run, WarmupThresholds thresholds)
        => run.Succeeded
           && run.LoadMs <= thresholds.WarmupMaxLoadMs
           && run.TtftMs <= thresholds.WarmupMaxTtftMs
           && run.TokPerSec >= thresholds.WarmupMinTokPerSec;

    private static bool IsDegradedPass(WarmupMeasurement run, WarmupThresholds thresholds)
        => run.Succeeded
           && run.LoadMs <= (int)(thresholds.WarmupMaxLoadMs * 1.5)
           && run.TtftMs <= (int)(thresholds.WarmupMaxTtftMs * 1.25)
           && run.TokPerSec >= thresholds.WarmupMinTokPerSec * 0.75;

    private static IReadOnlyList<string> BuildThresholdFailureReasons(
        IReadOnlyList<WarmupMeasurement> runs,
        WarmupThresholds thresholds)
    {
        var reasons = new List<string>();
        foreach (var run in runs)
        {
            var scenario = string.IsNullOrWhiteSpace(run.Scenario) ? "warmup" : SanitizeReasonValue(run.Scenario);
            if (!run.Succeeded)
            {
                reasons.Add($"warmup_run_failed:{scenario}:{SanitizeReasonValue(run.Error ?? "unknown")}");
                continue;
            }

            if (run.LoadMs > thresholds.WarmupMaxLoadMs)
                reasons.Add($"warmup_load_ms_above:{scenario}:{run.LoadMs}>{thresholds.WarmupMaxLoadMs}");
            if (run.TtftMs > thresholds.WarmupMaxTtftMs)
                reasons.Add($"warmup_ttft_ms_above:{scenario}:{run.TtftMs}>{thresholds.WarmupMaxTtftMs}");
            if (run.TokPerSec < thresholds.WarmupMinTokPerSec)
                reasons.Add($"warmup_tok_per_sec_below:{scenario}:{run.TokPerSec:0.##}<{thresholds.WarmupMinTokPerSec:0.##}");
        }

        return reasons
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
    }

    private static string SanitizeReasonValue(string value)
        => new(value
            .Trim()
            .Select(static ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' ? ch : '_')
            .ToArray());

    private static async Task<WarmupProfileItem?> LoadProfileAsync(
        string profileId,
        string? root,
        CancellationToken ct)
    {
        var read = await GovernanceArtifactStore.ReadAsync<WarmupProfilesArtifact>(
            GovernanceArtifactStore.WarmupProfilesFile,
            root,
            ct).ConfigureAwait(false);

        return read.Status == GovernanceArtifactReadStatus.Ok
            ? read.Value!.Items.FirstOrDefault(item => string.Equals(item.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    private static async Task PersistResultAsync(
        WarmupGateRequest request,
        WarmupGateResult result,
        WarmupThresholds? thresholds,
        string? root,
        CancellationToken ct)
    {
        var read = await GovernanceArtifactStore.ReadAsync<WarmupResultsArtifact>(
            GovernanceArtifactStore.WarmupResultsFile,
            root,
            ct).ConfigureAwait(false);

        var items = read.Status == GovernanceArtifactReadStatus.Ok && read.Value is not null
            ? read.Value.Items.ToList()
            : new List<WarmupResultItem>();

        var last = request.Runs.LastOrDefault();
        items.Insert(0, new WarmupResultItem(
            DateTimeOffset.UtcNow,
            request.Profile.ProfileId,
            request.Profile.Runtime,
            request.Profile.ModelId,
            result.Status,
            result.PassCount,
            thresholds?.WarmupPassCount ?? 0,
            last?.LoadMs,
            last?.TtftMs,
            last?.TokPerSec,
            last?.MsPerToken,
            last?.RuntimeMetrics,
            result.Reasons,
            request.HardwareFingerprint,
            request.Trigger));

        await GovernanceArtifactStore.WriteAsync(
            GovernanceArtifactStore.WarmupResultsFile,
            new WarmupResultsArtifact(
                GovernanceArtifactStore.WarmupResultsFile,
                "v3.1",
                items.Take(100).ToArray()),
            root,
            ct).ConfigureAwait(false);
    }
}
