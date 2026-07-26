namespace SAAIA.Client.WinUI.Services;

internal sealed record LocalLlmQualificationPromotionProfileResult(
    WarmupProfileItem Profile,
    WarmupGateResult Gate,
    bool RuntimeMarkedQualified);

internal sealed record LocalLlmQualificationPromotionOutcome(
    bool Succeeded,
    QualifiedProfile? Winner,
    IReadOnlyList<LocalLlmQualificationPromotionProfileResult> Profiles,
    IReadOnlyList<string> Reasons);

internal static class LocalLlmQualificationPromotion
{
    public static async Task<LocalLlmQualificationPromotionOutcome> RunAsync(
        LocalLlmQualificationFinalValidationOutcome finalValidation,
        string modelId,
        string? modelPath = null,
        AppSettings? settings = null,
        bool persistSettings = false,
        string? root = null,
        string? driverVersion = null,
        string? hardwareFingerprint = null,
        HardwareProbeArtifact? hardwareProbe = null,
        Func<string, bool>? runtimeQualifier = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(finalValidation);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        runtimeQualifier ??= LlamaCppReleaseDownloader.TryMarkRuntimeQualified;

        var eligibleScores = finalValidation.RankedCandidates
            .Where(static score => score.Eligible)
            .OrderBy(static score => score.FinalScoreSeconds ?? double.MaxValue)
            .ToArray();
        if (eligibleScores.Length == 0)
        {
            return new LocalLlmQualificationPromotionOutcome(
                Succeeded: false,
                Winner: null,
                Profiles: Array.Empty<LocalLlmQualificationPromotionProfileResult>(),
                Reasons: new[] { "no_eligible_final_profile" });
        }

        var registrations = BuildMeasuredProfiles(eligibleScores, modelId);
        await WarmupProfileStore.UpsertMeasuredProfilesAsync(
            registrations,
            root,
            ct).ConfigureAwait(false);
        if (hardwareProbe is not null)
        {
            if (!string.IsNullOrWhiteSpace(hardwareFingerprint)
                && !string.IsNullOrWhiteSpace(hardwareProbe.MachineFingerprint)
                && !string.Equals(
                    hardwareFingerprint,
                    hardwareProbe.MachineFingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The promoted hardware probe does not match the final-validation fingerprint.");
            }

            hardwareFingerprint ??= hardwareProbe.MachineFingerprint;
            await GovernanceArtifactStore.WriteAsync(
                GovernanceArtifactStore.HardwareProbeFile,
                hardwareProbe,
                root,
                ct).ConfigureAwait(false);
        }

        var scoreByProfileId = eligibleScores.ToDictionary(
            static score => score.Candidate.CandidateId,
            StringComparer.OrdinalIgnoreCase);
        var results = new List<LocalLlmQualificationPromotionProfileResult>(registrations.Count);
        var markedRuntimes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Evaluate the winner last so the last-known-good artifact ends on the selected profile.
        foreach (var registration in registrations.Reverse())
        {
            ct.ThrowIfCancellationRequested();
            var score = scoreByProfileId[registration.ProfileId];
            var gate = await WarmupGate.EvaluateAsync(
                new WarmupGateRequest(
                    registration.Candidate,
                    score.Rounds.Select(static round => round.Warmup).ToArray(),
                    driverVersion,
                    hardwareFingerprint,
                    Trigger: "measured_hardware_auto_qualification"),
                root,
                ct).ConfigureAwait(false);
            var gatePassed = gate.Status is WarmupGateStatus.Pass or WarmupGateStatus.PassDegraded;
            var runtimeMarked = gatePassed
                                && (markedRuntimes.Contains(registration.Runtime)
                                    || runtimeQualifier(registration.Runtime));
            if (runtimeMarked)
                markedRuntimes.Add(registration.Runtime);
            results.Add(new LocalLlmQualificationPromotionProfileResult(
                registration,
                gate,
                runtimeMarked));
        }

        results.Reverse();
        var winnerRegistration = registrations[0];
        var winnerResult = results.First(result => string.Equals(
            result.Profile.ProfileId,
            winnerRegistration.ProfileId,
            StringComparison.OrdinalIgnoreCase));
        var succeeded = winnerResult.Gate.Status is WarmupGateStatus.Pass or WarmupGateStatus.PassDegraded
                        && winnerResult.RuntimeMarkedQualified;
        var reasons = new List<string>();
        if (!succeeded)
            reasons.AddRange(winnerResult.Gate.Reasons);
        if (!winnerResult.RuntimeMarkedQualified)
            reasons.Add("winner_runtime_not_marked_qualified");

        if (succeeded && settings is not null)
        {
            var winnerScore = eligibleScores[0];
            settings.UseLocalLlm = true;
            settings.ManageLocalLlmProcess = true;
            settings.LlamaExePath = winnerScore.Candidate.RuntimeExecutablePath;
            if (!string.IsNullOrWhiteSpace(modelPath))
                settings.ModelPath = Path.GetFullPath(modelPath);
            settings.ModelId = modelId;
            settings.QualifiedProfile = winnerRegistration.Candidate;
            if (persistSettings)
                settings.Save();
        }

        return new LocalLlmQualificationPromotionOutcome(
            succeeded,
            succeeded ? winnerRegistration.Candidate : null,
            results,
            reasons.Count == 0
                ? new[] { "winner_registered_gated_and_runtime_qualified" }
                : reasons.Distinct(StringComparer.Ordinal).ToArray());
    }

    internal static IReadOnlyList<WarmupProfileItem> BuildMeasuredProfiles(
        IReadOnlyList<LocalLlmQualificationFinalCandidateScore> eligibleScores,
        string modelId)
    {
        ArgumentNullException.ThrowIfNull(eligibleScores);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        var ranked = eligibleScores
            .Where(static score => score.Eligible)
            .OrderBy(static score => score.FinalScoreSeconds ?? double.MaxValue)
            .ToArray();
        var profiles = new List<WarmupProfileItem>(ranked.Length);
        for (var index = 0; index < ranked.Length; index++)
        {
            var score = ranked[index];
            var distinctFallback = ranked.Skip(index + 1).FirstOrDefault(other =>
                !string.Equals(
                    other.Candidate.Profile.Runtime,
                    score.Candidate.Profile.Runtime,
                    StringComparison.OrdinalIgnoreCase));
            var fallbackProfileId = distinctFallback?.Candidate.CandidateId
                                    // Never cross model families through a historical
                                    // hard-coded profile. If no measured alternate runtime
                                    // exists, the current measured profile is self-safe.
                                    ?? score.Candidate.CandidateId;
            var candidate = score.Candidate.Profile with
            {
                ModelId = modelId,
                FallbackProfileRef = fallbackProfileId
            };
            var thresholds = BuildMeasuredThresholds(score.Rounds);
            profiles.Add(new WarmupProfileItem(
                candidate.ProfileId,
                modelId,
                candidate.Runtime,
                index == 0
                    ? "measured_nominal"
                    : string.Equals(
                        candidate.Runtime,
                        ranked[0].Candidate.Profile.Runtime,
                        StringComparison.OrdinalIgnoreCase)
                        ? "measured_alternate"
                        : "measured_fallback",
                candidate,
                thresholds,
                new[]
                {
                    "checksum_verified",
                    "not_blacklisted",
                    "runtime_capability_probe",
                    "fit_params_memory_budget",
                    "multi_scenario_benchmark",
                    "rotated_server_rounds",
                    "structured_quality_contract",
                    LocalLlmAdaptiveQualificationService.QualificationContractRef
                },
                fallbackProfileId));
        }

        return profiles;
    }

    internal static WarmupThresholds BuildMeasuredThresholds(
        IReadOnlyList<LocalLlmQualificationFinalRound> rounds)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        var successful = rounds
            .Where(static round => round.StartSucceeded
                                   && round.Warmup.Succeeded
                                   && round.StructuredProbe.Succeeded)
            .ToArray();
        if (successful.Length == 0)
            throw new InvalidOperationException("Cannot derive thresholds without a successful final round.");

        var maxLoad = successful.Max(static round =>
            Math.Max(round.StartupLoadMs ?? 0, round.Warmup.LoadMs));
        var maxTtft = successful.Max(static round => round.Warmup.TtftMs);
        var minTokensPerSecond = successful.Min(static round => round.Warmup.TokPerSec);
        return new WarmupThresholds(
            WarmupMaxLoadMs: Math.Clamp(
                (int)Math.Ceiling(Math.Max(15_000d, maxLoad * 1.5d)),
                15_000,
                180_000),
            WarmupMaxTtftMs: Math.Clamp(
                (int)Math.Ceiling(Math.Max(5_000d, maxTtft * 1.5d)),
                5_000,
                120_000),
            WarmupMinTokPerSec: Math.Max(0.25d, minTokensPerSecond * 0.70d),
            WarmupPassCount: Math.Min(3, successful.Length),
            IdleTimeoutSeconds: 120);
    }
}
