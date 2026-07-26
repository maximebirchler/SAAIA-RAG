using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LocalLlmQualificationPromotionTests
{
    [Fact]
    public async Task UpsertMeasuredProfiles_roundtrips_custom_profile_without_losing_defaults()
    {
        var root = TempRoot();
        try
        {
            await GovernanceArtifactStore.EnsureDefaultArtifactsAsync(new AppSettings(), root);
            var candidate = WarmupProfileStore.CreateQwen3Q5Cuda4GbProfile() with
            {
                ProfileId = "auto-measured-test",
                Threads = 4,
                ThreadsBatch = 4,
                Ngl = 37
            };
            var item = new WarmupProfileItem(
                candidate.ProfileId,
                candidate.ModelId,
                candidate.Runtime,
                "measured_nominal",
                candidate,
                new WarmupThresholds(20_000, 8_000, 7, 3, 120),
                new[] { "rotated_server_rounds" },
                "qwen3-4b-2507-q5km-cpu-safe");

            await WarmupProfileStore.UpsertMeasuredProfilesAsync(new[] { item }, root);
            var restored = await WarmupProfileStore.FindProfileAsync(candidate.ProfileId, root);
            var artifact = await GovernanceArtifactStore.ReadAsync<WarmupProfilesArtifact>(
                GovernanceArtifactStore.WarmupProfilesFile,
                root);

            Assert.NotNull(restored);
            Assert.Equal(37, restored!.Candidate.Ngl);
            Assert.Equal(4, restored.Candidate.Threads);
            Assert.Equal(GovernanceArtifactReadStatus.Ok, artifact.Status);
            Assert.Contains(artifact.Value!.Items, static profile =>
                profile.ProfileId == "qwen3-4b-2507-q5km-cuda-4gb-quality");
            Assert.Contains(artifact.Value.Items, static profile =>
                profile.ProfileId == "auto-measured-test");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildMeasuredProfiles_creates_nominal_alternate_and_distinct_runtime_fallback_chain()
    {
        var cuda4 = Score(Candidate("cuda4", "llama.cpp-cuda", "CUDA0", 4), finalSeconds: 10);
        var cuda6 = Score(Candidate("cuda6", "llama.cpp-cuda", "CUDA0", 6), finalSeconds: 11);
        var vulkan = Score(Candidate("vulkan", "llama.cpp-vulkan", "Vulkan1", 6), finalSeconds: 15);

        var profiles = LocalLlmQualificationPromotion.BuildMeasuredProfiles(
            new[] { vulkan, cuda6, cuda4 },
            "model");

        Assert.Equal(new[] { "cuda4", "cuda6", "vulkan" }, profiles.Select(static item => item.ProfileId));
        Assert.Equal("measured_nominal", profiles[0].Mode);
        Assert.Equal("vulkan", profiles[0].FallbackProfileRef);
        Assert.Equal("measured_alternate", profiles[1].Mode);
        Assert.Equal("vulkan", profiles[1].FallbackProfileRef);
        Assert.Equal("measured_fallback", profiles[2].Mode);
        Assert.Equal("vulkan", profiles[2].FallbackProfileRef);
        Assert.All(profiles, static item =>
        {
            Assert.Equal(3, item.Thresholds.WarmupPassCount);
            Assert.True(item.Thresholds.WarmupMaxLoadMs >= 15_000);
            Assert.True(item.Thresholds.WarmupMinTokPerSec > 0);
        });
    }

    [Fact]
    public async Task RunAsync_gates_profiles_marks_each_runtime_once_and_applies_winner_to_settings()
    {
        var root = TempRoot();
        try
        {
            var cuda4Candidate = Candidate("cuda4", "llama.cpp-cuda", "CUDA0", 4);
            var cuda6Candidate = Candidate("cuda6", "llama.cpp-cuda", "CUDA0", 6);
            var vulkanCandidate = Candidate("vulkan", "llama.cpp-vulkan", "Vulkan1", 6);
            var scores = new[]
            {
                Score(cuda4Candidate, 10),
                Score(cuda6Candidate, 11),
                Score(vulkanCandidate, 15)
            };
            var validation = new LocalLlmQualificationFinalValidationOutcome(
                scores.Select(static score => score.Candidate).ToArray(),
                scores.SelectMany(static score => score.Rounds).ToArray(),
                scores);
            var marked = new List<string>();
            var settings = new AppSettings();
            var hardwareProbe = HardwareProbeArtifact.Empty() with
            {
                Status = "captured",
                CapturedAt = DateTimeOffset.UtcNow,
                MachineFingerprint = "test-machine"
            };

            var outcome = await LocalLlmQualificationPromotion.RunAsync(
                validation,
                modelId: "model",
                modelPath: @"C:\models\model.gguf",
                settings,
                persistSettings: false,
                root,
                hardwareFingerprint: "test-machine",
                hardwareProbe: hardwareProbe,
                runtimeQualifier: runtime =>
                {
                    marked.Add(runtime);
                    return true;
                });

            Assert.True(outcome.Succeeded, string.Join(",", outcome.Reasons));
            Assert.Equal("cuda4", outcome.Winner!.ProfileId);
            Assert.Equal(new[] { "llama.cpp-vulkan", "llama.cpp-cuda" }, marked);
            Assert.Equal("cuda4", settings.QualifiedProfile!.ProfileId);
            Assert.Equal(cuda4Candidate.RuntimeExecutablePath, settings.LlamaExePath);
            Assert.All(outcome.Profiles, static profile =>
                Assert.Equal(WarmupGateStatus.Pass, profile.Gate.Status));

            var lastKnownGood = await RollbackManager.ReadLastKnownGoodAsync(root);
            Assert.Equal("cuda4", lastKnownGood!.ProfileId);
            var stored = await WarmupProfileStore.FindProfileAsync("cuda4", root);
            Assert.NotNull(stored);
            var storedHardware = await GovernanceArtifactStore.ReadAsync<HardwareProbeArtifact>(
                GovernanceArtifactStore.HardwareProbeFile,
                root);
            Assert.Equal(GovernanceArtifactReadStatus.Ok, storedHardware.Status);
            Assert.Equal("test-machine", storedHardware.Value!.MachineFingerprint);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static LocalLlmQualificationFinalCandidateScore Score(
        LocalLlmQualificationCandidate candidate,
        double finalSeconds)
    {
        var rounds = Enumerable.Range(0, 3)
            .Select(index => Round(candidate, index))
            .ToArray();
        return new LocalLlmQualificationFinalCandidateScore(
            candidate,
            Eligible: true,
            StageTwoProjectedSeconds: finalSeconds - 1,
            MedianStructuredProbeMs: 1000,
            MedianStartupLoadMs: 9000,
            WorstTtftMs: 3000,
            MinimumTokensPerSecond: 10,
            FinalScoreSeconds: finalSeconds,
            Reasons: new[] { "pass" },
            Rounds: rounds);
    }

    private static LocalLlmQualificationFinalRound Round(
        LocalLlmQualificationCandidate candidate,
        int index)
        => new(
            candidate.CandidateId,
            index,
            index,
            18000 + index,
            StartSucceeded: true,
            StartupLoadMs: 9000 + index * 100,
            new WarmupMeasurement(
                LoadMs: 9000 + index * 100,
                TtftMs: 3000 + index * 10,
                TokPerSec: 10 - index * 0.1,
                Succeeded: true),
            new LocalLlmStructuredQualificationProbeResult(
                Succeeded: true,
                DurationMs: 1000,
                Decisions: new Dictionary<string, string>(),
                Reasons: new[] { "pass" },
                Response: "{}"),
            Diagnostic: string.Empty);

    private static LocalLlmQualificationCandidate Candidate(
        string id,
        string runtime,
        string device,
        int threads)
        => new(
            id,
            "finalist",
            $@"C:\runtime\{runtime}\llama-server.exe",
            WarmupProfileStore.CreateQwen3Q5Cuda4GbProfile() with
            {
                ProfileId = id,
                Runtime = runtime,
                ModelId = "model",
                DeviceIds = new[] { device },
                Threads = threads,
                ThreadsBatch = threads,
                Ngl = runtime == "llama.cpp-cpu" ? 0 : 37
            },
            Array.Empty<string>());

    private static string TempRoot()
        => Path.Combine(Path.GetTempPath(), "saaia-promotion-tests-" + Guid.NewGuid().ToString("N"));
}
