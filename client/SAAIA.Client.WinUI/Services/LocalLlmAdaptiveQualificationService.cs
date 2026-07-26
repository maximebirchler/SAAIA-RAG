using System.Text.Json;

namespace SAAIA.Client.WinUI.Services;

internal enum LocalLlmAdaptiveQualificationDepth
{
    Initial,
    Deep
}

internal sealed record LocalLlmAdaptiveQualificationOptions(
    LocalLlmAdaptiveQualificationDepth Depth,
    int MaxCandidates,
    LocalLlmQualificationBenchmarkScenario ScreeningScenario,
    IReadOnlyList<LocalLlmQualificationBenchmarkScenario> RefinementWorkload,
    LocalLlmQualificationRefinementOptions Refinement,
    LocalLlmQualificationFinalValidationOptions FinalValidation,
    int DedicatedDeviceMarginMiB,
    int SystemRamReserveMiB)
{
    public static LocalLlmAdaptiveQualificationOptions CreateInitial(int logicalProcessorCount)
        => new(
            LocalLlmAdaptiveQualificationDepth.Initial,
            64,
            new LocalLlmQualificationBenchmarkScenario(
                "initial-screen-p128-n32",
                PromptTokens: 128,
                GenerationTokens: 32),
            new[]
            {
                new LocalLlmQualificationBenchmarkScenario(
                    "initial-rag-control-p1024-n96",
                    PromptTokens: 1024,
                    GenerationTokens: 96,
                    Weight: 6),
                new LocalLlmQualificationBenchmarkScenario(
                    "initial-rag-writer-p2048-n256",
                    PromptTokens: 2048,
                    GenerationTokens: 256,
                    Weight: 1)
            },
            new LocalLlmQualificationRefinementOptions(
                TopTopologyCount: 2,
                MaxSuccessfulCandidatesPerTopology: 2,
                MaxAttemptsPerTopology: 8,
                LogicalProcessorCount: Math.Clamp(logicalProcessorCount, 1, 256)),
            LocalLlmQualificationFinalValidationOptions.Default,
            768,
            4096);

    public static LocalLlmAdaptiveQualificationOptions CreateDeep(int logicalProcessorCount)
        => new(
            LocalLlmAdaptiveQualificationDepth.Deep,
            96,
            new LocalLlmQualificationBenchmarkScenario(
                "deep-screen-p128-n32",
                PromptTokens: 128,
                GenerationTokens: 32),
            LocalLlmQualificationRefinement.CreateDefaultWorkload(),
            LocalLlmQualificationRefinementOptions.CreateDefault(logicalProcessorCount),
            LocalLlmQualificationFinalValidationOptions.Default,
            768,
            4096);
}

internal sealed record LocalLlmAdaptiveQualificationProgress(
    string Stage,
    int StageIndex,
    int StageCount,
    string Message);

internal sealed record LocalLlmAdaptiveQualificationDecision(
    bool Required,
    bool CanRun,
    IReadOnlyList<string> Reasons);

internal sealed record LocalLlmAdaptiveQualificationPreflight(
    HardwareProbeCapture Hardware,
    LocalLlmAdaptiveQualificationDecision Decision);

internal sealed record LocalLlmAdaptiveQualificationOutcome(
    bool Succeeded,
    bool Skipped,
    LocalLlmAdaptiveQualificationDepth Depth,
    LocalLlmAdaptiveQualificationDecision Decision,
    HardwareProbeCapture Hardware,
    IReadOnlyList<LocalLlmQualificationCandidate> Candidates,
    LocalLlmQualificationScreeningOutcome? Screening,
    LocalLlmQualificationRefinementOutcome? Refinement,
    LocalLlmQualificationFinalValidationOutcome? FinalValidation,
    LocalLlmQualificationPromotionOutcome? Promotion,
    IReadOnlyList<string> Reasons);

internal static class LocalLlmAdaptiveQualificationService
{
    private const int StageCount = 6;
    internal const string QualificationContractRef =
        "adaptive_hardware_contract:v2_cpu_cuda_vulkan_sycl_hip_fair";

    public static async Task<LocalLlmAdaptiveQualificationOutcome> QualifyIfRequiredAsync(
        AppSettings settings,
        LocalLlmAdaptiveQualificationOptions? options = null,
        bool force = false,
        string? root = null,
        IProgress<LocalLlmAdaptiveQualificationProgress>? progress = null,
        Func<string, LlamaCppReleaseDownloader.ActiveRuntimeState?>? runtimeStateResolver = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        options ??= LocalLlmAdaptiveQualificationOptions.CreateInitial(
            Environment.ProcessorCount);
        runtimeStateResolver ??= LlamaCppReleaseDownloader.TryGetActiveRuntimeState;

        Report(progress, "hardware_probe", 1, "Capturing hardware and runtime capabilities.");
        var preflight = await InspectAsync(
            settings,
            force,
            root,
            runtimeStateResolver,
            ct).ConfigureAwait(false);
        var hardware = preflight.Hardware;
        var decision = preflight.Decision;
        if (!decision.Required)
        {
            return EmptyOutcome(
                true,
                true,
                options.Depth,
                decision,
                hardware,
                decision.Reasons);
        }

        if (!decision.CanRun)
        {
            return EmptyOutcome(
                false,
                false,
                options.Depth,
                decision,
                hardware,
                decision.Reasons);
        }

        var modelId = ResolveModelId(settings);
        var usableRuntimeProbes = hardware.RuntimeProbes
            .Where(static probe => probe.Succeeded && File.Exists(probe.ExecutablePath))
            .ToArray();
        if (usableRuntimeProbes.Length == 0)
        {
            return EmptyOutcome(
                false,
                false,
                options.Depth,
                decision,
                hardware,
                decision.Reasons.Concat(new[] { "no_usable_runtime_probe" }).ToArray());
        }

        Report(progress, "candidate_factory", 2, "Building bounded profiles from measured devices.");
        var candidates = LocalLlmQualificationCandidateFactory.Build(
            modelId,
            settings.ModelPath,
            usableRuntimeProbes,
            Environment.ProcessorCount,
            options.MaxCandidates);
        if (candidates.Count == 0)
        {
            return new LocalLlmAdaptiveQualificationOutcome(
                false,
                false,
                options.Depth,
                decision,
                hardware,
                candidates,
                null,
                null,
                null,
                null,
                decision.Reasons.Concat(new[] { "no_qualification_candidate" }).ToArray());
        }

        var availableSystemRamMiB = TryGetInt(
            hardware.Artifact.Hardware,
            "availableRamMiB");
        Report(progress, "screening", 3, "Rejecting unsupported and unfit topologies.");
        var screening = await LocalLlmQualificationScreening.RunAsync(
            candidates,
            usableRuntimeProbes,
            settings.ModelPath,
            options.ScreeningScenario,
            availableSystemRamMiB,
            options.DedicatedDeviceMarginMiB,
            options.SystemRamReserveMiB,
            ct: ct).ConfigureAwait(false);
        if (!screening.RankedRepresentatives.Any(static score => score.Eligible))
        {
            return FailureAfterScreening(
                options.Depth,
                decision,
                hardware,
                candidates,
                screening,
                "screening_has_no_eligible_topology");
        }

        Report(progress, "refinement", 4, "Measuring control and writer workloads.");
        var refinement = await LocalLlmQualificationRefinement.RunAsync(
            screening,
            usableRuntimeProbes,
            settings.ModelPath,
            options.RefinementWorkload,
            availableSystemRamMiB,
            options.DedicatedDeviceMarginMiB,
            options.SystemRamReserveMiB,
            options.Refinement,
            ct: ct).ConfigureAwait(false);
        if (!refinement.RankedCandidates.Any(static score => score.Eligible))
        {
            return new LocalLlmAdaptiveQualificationOutcome(
                false,
                false,
                options.Depth,
                decision,
                hardware,
                candidates,
                screening,
                refinement,
                null,
                null,
                decision.Reasons.Concat(new[] { "refinement_has_no_eligible_candidate" }).ToArray());
        }

        Report(progress, "final_validation", 5, "Running rotated server and semantic contract rounds.");
        var finalValidation = await LocalLlmQualificationFinalValidation.RunAsync(
            refinement,
            settings.ModelPath,
            modelId,
            options.FinalValidation,
            ct: ct).ConfigureAwait(false);
        if (!finalValidation.RankedCandidates.Any(static score => score.Eligible))
        {
            return new LocalLlmAdaptiveQualificationOutcome(
                false,
                false,
                options.Depth,
                decision,
                hardware,
                candidates,
                screening,
                refinement,
                finalValidation,
                null,
                decision.Reasons.Concat(new[] { "final_validation_has_no_eligible_candidate" }).ToArray());
        }

        Report(progress, "promotion", 6, "Registering the measured winner and fallback profiles.");
        var promotion = await LocalLlmQualificationPromotion.RunAsync(
            finalValidation,
            modelId,
            settings.ModelPath,
            settings,
            persistSettings: true,
            root: root,
            driverVersion: TryGetString(
                hardware.Artifact.Hardware,
                "gpuDriverVersion"),
            hardwareFingerprint: hardware.Artifact.MachineFingerprint,
            hardwareProbe: hardware.Artifact,
            runtimeQualifier: null,
            ct: ct).ConfigureAwait(false);
        return new LocalLlmAdaptiveQualificationOutcome(
            promotion.Succeeded,
            false,
            options.Depth,
            decision,
            hardware,
            candidates,
            screening,
            refinement,
            finalValidation,
            promotion,
            promotion.Succeeded
                ? decision.Reasons.Concat(new[] { "adaptive_qualification_promoted" }).ToArray()
                : decision.Reasons.Concat(promotion.Reasons).Distinct(StringComparer.Ordinal).ToArray());
    }

    public static async Task<LocalLlmAdaptiveQualificationPreflight> InspectAsync(
        AppSettings settings,
        bool force = false,
        string? root = null,
        Func<string, LlamaCppReleaseDownloader.ActiveRuntimeState?>? runtimeStateResolver = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var hardware = await HardwareProbeService.CaptureDetailedAsync(
            refresh: true,
            ct).ConfigureAwait(false);
        var decision = await EvaluateNeedAsync(
            settings,
            hardware.Artifact,
            force,
            root,
            runtimeStateResolver,
            ct).ConfigureAwait(false);
        return new LocalLlmAdaptiveQualificationPreflight(hardware, decision);
    }

    internal static async Task<LocalLlmAdaptiveQualificationDecision> EvaluateNeedAsync(
        AppSettings settings,
        HardwareProbeArtifact currentHardware,
        bool force = false,
        string? root = null,
        Func<string, LlamaCppReleaseDownloader.ActiveRuntimeState?>? runtimeStateResolver = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(currentHardware);
        runtimeStateResolver ??= LlamaCppReleaseDownloader.TryGetActiveRuntimeState;
        var reasons = new List<string>();

        if (!settings.UseLocalLlm || !settings.ManageLocalLlmProcess)
        {
            return new LocalLlmAdaptiveQualificationDecision(
                false,
                false,
                new[] { "local_llm_not_managed" });
        }

        var modelAvailable = !string.IsNullOrWhiteSpace(settings.ModelPath)
                             && File.Exists(settings.ModelPath);
        var runtimeAvailable = !string.IsNullOrWhiteSpace(settings.LlamaExePath)
                               && File.Exists(settings.LlamaExePath);
        if (!modelAvailable)
            reasons.Add("model_file_missing");
        if (!runtimeAvailable)
            reasons.Add("selected_runtime_missing");
        if (!modelAvailable || !runtimeAvailable)
        {
            return new LocalLlmAdaptiveQualificationDecision(
                true,
                false,
                reasons);
        }

        if (force)
            reasons.Add("forced_requalification");

        var profile = settings.QualifiedProfile;
        if (profile is null)
        {
            reasons.Add("qualified_profile_missing");
        }
        else
        {
            var registered = await WarmupProfileStore.FindProfileAsync(
                profile.ProfileId,
                root,
                ct).ConfigureAwait(false);
            if (registered is null)
            {
                reasons.Add("registered_profile_missing");
            }
            else
            {
                if (RequalificationTriggerService.HasProfileConfigurationDrift(
                        profile,
                        registered.Candidate))
                {
                    reasons.Add("registered_profile_configuration_drift");
                }

                if (!registered.HardGateRefs.Contains(
                        QualificationContractRef,
                        StringComparer.Ordinal))
                {
                    reasons.Add("qualification_contract_changed");
                }
            }

            var selectedRuntimeId = RequalificationTriggerService.DetectRuntimeKey(
                settings.LlamaExePath);
            var runtimeState = runtimeStateResolver(selectedRuntimeId);
            if (runtimeState is null)
            {
                reasons.Add("selected_runtime_untracked");
            }
            else if (!string.Equals(
                         runtimeState.Status,
                         "qualified",
                         StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("selected_runtime_not_qualified");
            }

            if (!string.Equals(
                    profile.Runtime,
                    selectedRuntimeId,
                    StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("selected_runtime_profile_mismatch");
            }

            var currentModelId = ResolveModelId(settings);
            if (!string.Equals(
                    profile.ModelId,
                    currentModelId,
                    StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("selected_model_profile_mismatch");
            }

            var warmupRead = await GovernanceArtifactStore.ReadAsync<WarmupResultsArtifact>(
                GovernanceArtifactStore.WarmupResultsFile,
                root,
                ct).ConfigureAwait(false);
            var hasPassingWarmup = warmupRead.Status == GovernanceArtifactReadStatus.Ok
                                   && warmupRead.Value is not null
                                   && warmupRead.Value.Items.Any(item =>
                                       string.Equals(
                                           item.ProfileId,
                                           profile.ProfileId,
                                           StringComparison.OrdinalIgnoreCase)
                                       && item.Status is WarmupGateStatus.Pass
                                           or WarmupGateStatus.PassDegraded);
            if (!hasPassingWarmup)
                reasons.Add("passing_warmup_missing");
        }

        var storedHardware = await GovernanceArtifactStore.ReadAsync<HardwareProbeArtifact>(
            GovernanceArtifactStore.HardwareProbeFile,
            root,
            ct).ConfigureAwait(false);
        if (storedHardware.Status != GovernanceArtifactReadStatus.Ok
            || storedHardware.Value is null)
        {
            reasons.Add("stored_hardware_probe_missing");
        }
        else
        {
            var change = HardwareProbeService.Compare(
                storedHardware.Value,
                currentHardware);
            if (change.RequiresRequalification)
                reasons.Add(change.Reason);
        }

        var distinct = reasons.Distinct(StringComparer.Ordinal).ToArray();
        return new LocalLlmAdaptiveQualificationDecision(
            force || distinct.Length > 0,
            true,
            distinct.Length == 0
                ? new[] { "qualification_cache_valid" }
                : distinct);
    }

    private static LocalLlmAdaptiveQualificationOutcome EmptyOutcome(
        bool succeeded,
        bool skipped,
        LocalLlmAdaptiveQualificationDepth depth,
        LocalLlmAdaptiveQualificationDecision decision,
        HardwareProbeCapture hardware,
        IReadOnlyList<string> reasons)
        => new(
            succeeded,
            skipped,
            depth,
            decision,
            hardware,
            Array.Empty<LocalLlmQualificationCandidate>(),
            null,
            null,
            null,
            null,
            reasons);

    private static LocalLlmAdaptiveQualificationOutcome FailureAfterScreening(
        LocalLlmAdaptiveQualificationDepth depth,
        LocalLlmAdaptiveQualificationDecision decision,
        HardwareProbeCapture hardware,
        IReadOnlyList<LocalLlmQualificationCandidate> candidates,
        LocalLlmQualificationScreeningOutcome screening,
        string reason)
        => new(
            false,
            false,
            depth,
            decision,
            hardware,
            candidates,
            screening,
            null,
            null,
            null,
            decision.Reasons.Concat(new[] { reason }).ToArray());

    private static string ResolveModelId(AppSettings settings)
        => ModelCatalogStore.ResolveCanonicalModelId(settings.ModelId)
           ?? ModelCatalogStore.ResolveCanonicalModelId(
               string.IsNullOrWhiteSpace(settings.ModelPath)
                   ? null
                   : Path.GetFileName(settings.ModelPath))
           ?? settings.ModelId;

    private static int? TryGetInt(
        IReadOnlyDictionary<string, object?> values,
        string key)
    {
        if (!values.TryGetValue(key, out var value) || value is null)
            return null;
        return value switch
        {
            int number => number,
            long number when number is >= 0 and <= int.MaxValue => (int)number,
            double number when number is >= 0 and <= int.MaxValue => (int)number,
            JsonElement { ValueKind: JsonValueKind.Number } json
                when json.TryGetInt32(out var number) => number,
            _ => null
        };
    }

    private static string? TryGetString(
        IReadOnlyDictionary<string, object?> values,
        string key)
    {
        if (!values.TryGetValue(key, out var value) || value is null)
            return null;
        if (value is JsonElement { ValueKind: JsonValueKind.String } json)
            return json.GetString();
        return Convert.ToString(value);
    }

    private static void Report(
        IProgress<LocalLlmAdaptiveQualificationProgress>? progress,
        string stage,
        int stageIndex,
        string message)
        => progress?.Report(new LocalLlmAdaptiveQualificationProgress(
            stage,
            stageIndex,
            StageCount,
            message));
}
