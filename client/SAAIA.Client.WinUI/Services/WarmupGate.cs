using System;
using System.Collections.Generic;
using System.Linq;
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

                runs.Add(LocalLlmWarmupHarness.AggregateScenarioMeasurements(scenarioRuns));
                continue;
            }

            runs.Add(await harness.RunOnceAsync(
                llmBaseUrl,
                model,
                LocalLlmWarmupHarnessOptions.Default,
                ct).ConfigureAwait(false));
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
        var passCount = request.Runs.Count(run => IsNominalPass(run, thresholds));
        var hasEnoughRuns = request.Runs.Count >= thresholds.WarmupPassCount;
        var reasons = new List<string>();

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
