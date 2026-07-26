using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LocalLlmAdaptiveQualificationServiceTests
{
    [Fact]
    public async Task EvaluateNeed_requires_qualification_when_no_profile_is_registered()
    {
        var root = TempRoot();
        try
        {
            Directory.CreateDirectory(root);
            var modelPath = Path.Combine(root, "model.gguf");
            var runtimePath = Path.Combine(root, "win-cuda-x64", "llama-server.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(runtimePath)!);
            await File.WriteAllTextAsync(modelPath, "model");
            await File.WriteAllTextAsync(runtimePath, "runtime");
            var hardware = Hardware("machine-a");
            await GovernanceArtifactStore.WriteAsync(
                GovernanceArtifactStore.HardwareProbeFile,
                hardware,
                root);
            var settings = new AppSettings
            {
                UseLocalLlm = true,
                ManageLocalLlmProcess = true,
                ModelPath = modelPath,
                ModelId = "model",
                LlamaExePath = runtimePath
            };

            var decision = await LocalLlmAdaptiveQualificationService.EvaluateNeedAsync(
                settings,
                hardware,
                root: root,
                runtimeStateResolver: _ => QualifiedRuntime(runtimePath));

            Assert.True(decision.Required);
            Assert.True(decision.CanRun);
            Assert.Contains("qualified_profile_missing", decision.Reasons);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EvaluateNeed_accepts_matching_registered_profile_and_detects_hardware_change()
    {
        var root = TempRoot();
        try
        {
            Directory.CreateDirectory(root);
            var modelPath = Path.Combine(root, "model.gguf");
            var runtimePath = Path.Combine(root, "win-cuda-x64", "llama-server.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(runtimePath)!);
            await File.WriteAllTextAsync(modelPath, "model");
            await File.WriteAllTextAsync(runtimePath, "runtime");
            var hardware = Hardware("machine-a");
            await GovernanceArtifactStore.WriteAsync(
                GovernanceArtifactStore.HardwareProbeFile,
                hardware,
                root);
            var profile = WarmupProfileStore.CreateQwen3Q5Cuda4GbProfile() with
            {
                ProfileId = "measured-machine-a",
                ModelId = "model",
                Runtime = "llama.cpp-cuda",
                Threads = 4,
                ThreadsBatch = 4,
                Ngl = 37
            };
            await WarmupProfileStore.UpsertMeasuredProfilesAsync(
                new[]
                {
                    new WarmupProfileItem(
                        profile.ProfileId,
                        profile.ModelId,
                        profile.Runtime,
                        "measured_nominal",
                        profile,
                        new WarmupThresholds(20_000, 8_000, 5, 3, 120),
                        new[]
                        {
                            "rotated_server_rounds",
                            LocalLlmAdaptiveQualificationService.QualificationContractRef
                        },
                        "qwen3-4b-2507-q5km-cpu-safe")
                },
                root);
            var gate = await WarmupGate.EvaluateAsync(
                new WarmupGateRequest(
                    profile,
                    new[]
                    {
                        new WarmupMeasurement(9000, 2500, 10),
                        new WarmupMeasurement(9100, 2600, 9.8),
                        new WarmupMeasurement(9050, 2550, 9.9)
                    },
                    HardwareFingerprint: hardware.MachineFingerprint),
                root);
            Assert.Equal(WarmupGateStatus.Pass, gate.Status);
            var settings = new AppSettings
            {
                UseLocalLlm = true,
                ManageLocalLlmProcess = true,
                ModelPath = modelPath,
                ModelId = "model",
                LlamaExePath = runtimePath,
                QualifiedProfile = profile
            };

            var current = await LocalLlmAdaptiveQualificationService.EvaluateNeedAsync(
                settings,
                hardware,
                root: root,
                runtimeStateResolver: _ => QualifiedRuntime(runtimePath));
            var changed = await LocalLlmAdaptiveQualificationService.EvaluateNeedAsync(
                settings,
                Hardware("machine-b"),
                root: root,
                runtimeStateResolver: _ => QualifiedRuntime(runtimePath));

            Assert.False(current.Required, string.Join(", ", current.Reasons));
            Assert.Equal(new[] { "qualification_cache_valid" }, current.Reasons);
            Assert.True(changed.Required);
            Assert.Contains("hardware_fingerprint_changed", changed.Reasons);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EvaluateNeed_invalidates_a_measured_profile_when_the_hardware_contract_changes()
    {
        var root = TempRoot();
        try
        {
            Directory.CreateDirectory(root);
            var modelPath = Path.Combine(root, "model.gguf");
            var runtimePath = Path.Combine(root, "win-cuda-x64", "llama-server.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(runtimePath)!);
            await File.WriteAllTextAsync(modelPath, "model");
            await File.WriteAllTextAsync(runtimePath, "runtime");
            var hardware = Hardware("machine-a");
            await GovernanceArtifactStore.WriteAsync(
                GovernanceArtifactStore.HardwareProbeFile,
                hardware,
                root);
            var profile = WarmupProfileStore.CreateQwen3Q5Cuda4GbProfile() with
            {
                ProfileId = "measured-old-contract",
                ModelId = "model",
                Runtime = "llama.cpp-cuda"
            };
            await WarmupProfileStore.UpsertMeasuredProfilesAsync(
                new[]
                {
                    new WarmupProfileItem(
                        profile.ProfileId,
                        profile.ModelId,
                        profile.Runtime,
                        "measured_nominal",
                        profile,
                        new WarmupThresholds(20_000, 8_000, 5, 3, 120),
                        new[] { "rotated_server_rounds" },
                        "qwen3-4b-2507-q5km-cpu-safe")
                },
                root);
            await WarmupGate.EvaluateAsync(
                new WarmupGateRequest(
                    profile,
                    new[]
                    {
                        new WarmupMeasurement(9000, 2500, 10),
                        new WarmupMeasurement(9100, 2600, 9.8),
                        new WarmupMeasurement(9050, 2550, 9.9)
                    },
                    HardwareFingerprint: hardware.MachineFingerprint),
                root);
            var settings = new AppSettings
            {
                UseLocalLlm = true,
                ManageLocalLlmProcess = true,
                ModelPath = modelPath,
                ModelId = "model",
                LlamaExePath = runtimePath,
                QualifiedProfile = profile
            };

            var decision = await LocalLlmAdaptiveQualificationService.EvaluateNeedAsync(
                settings,
                hardware,
                root: root,
                runtimeStateResolver: _ => QualifiedRuntime(runtimePath));

            Assert.True(decision.Required);
            Assert.Contains("qualification_contract_changed", decision.Reasons);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Initial_and_deep_options_keep_initial_work_bounded_without_weakening_final_rounds()
    {
        var initial = LocalLlmAdaptiveQualificationOptions.CreateInitial(12);
        var deep = LocalLlmAdaptiveQualificationOptions.CreateDeep(12);

        Assert.Equal(LocalLlmAdaptiveQualificationDepth.Initial, initial.Depth);
        Assert.Equal(LocalLlmAdaptiveQualificationDepth.Deep, deep.Depth);
        Assert.True(
            initial.Refinement.MaxSuccessfulCandidatesPerTopology
            < deep.Refinement.MaxSuccessfulCandidatesPerTopology);
        Assert.True(
            initial.RefinementWorkload.Sum(static scenario =>
                scenario.PromptTokens + scenario.GenerationTokens)
            < deep.RefinementWorkload.Sum(static scenario =>
                scenario.PromptTokens + scenario.GenerationTokens));
        Assert.Equal(3, initial.FinalValidation.RoundCount);
        Assert.Equal(3, deep.FinalValidation.RoundCount);
    }

    private static HardwareProbeArtifact Hardware(string fingerprint)
        => new(
            GovernanceArtifactStore.HardwareProbeFile,
            "v3.1",
            "captured",
            DateTimeOffset.UtcNow,
            fingerprint,
            new Dictionary<string, object?>
            {
                ["availableRamMiB"] = 16_384,
                ["gpuIsExternal"] = false,
                ["gpuName"] = "Test GPU",
                ["gpuDriverVersion"] = "1.0"
            });

    private static LlamaCppReleaseDownloader.ActiveRuntimeState QualifiedRuntime(
        string runtimePath)
        => new(
            "llama.cpp-cuda",
            "b10098",
            runtimePath,
            "qualified",
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

    private static string TempRoot()
        => Path.Combine(
            Path.GetTempPath(),
            "saaia-adaptive-qualification-tests-" + Guid.NewGuid().ToString("N"));
}
