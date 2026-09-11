using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LocalLlmQualificationRefinementTests
{
    [Fact]
    public void Default_workload_covers_control_writer_and_real_evidence_inventory_context()
    {
        var workload = LocalLlmQualificationRefinement.CreateDefaultWorkload();

        Assert.Equal(3, workload.Count);
        Assert.Contains(workload, static scenario =>
            scenario.ScenarioId.StartsWith("rag-control", StringComparison.Ordinal));
        Assert.Contains(workload, static scenario =>
            scenario.ScenarioId.StartsWith("rag-writer", StringComparison.Ordinal));
        Assert.Contains(workload, static scenario =>
            scenario.ScenarioId.StartsWith("rag-evidence-inventory", StringComparison.Ordinal)
            && scenario.PromptTokens + scenario.GenerationTokens > 4096);
        Assert.All(workload, static scenario =>
            Assert.InRange(scenario.PromptTokens + scenario.GenerationTokens, 1, 8192));
    }

    [Fact]
    public void BuildCandidateQueue_compares_layer_context_thread_flash_and_batch_variants()
    {
        var all = Candidate("all", "accelerator-all-model-layers", context: 4096, ngl: 37);
        var full = Candidate("full", "accelerator-full-offload", context: 4096, ngl: 36);
        var longContext = Candidate(
            "long",
            "accelerator-long-context-q8-kv",
            context: 8192,
            ngl: 37,
            cache: "q8_0");
        var partial = Candidate("partial", "accelerator-partial-offload", context: 3072, ngl: 18);

        var queue = LocalLlmQualificationRefinement.BuildCandidateQueue(
            new[] { partial, longContext, full, all },
            LocalLlmQualificationRefinement.CreateDefaultWorkload(),
            logicalProcessorCount: 8,
            maxAttempts: 20);

        Assert.Equal("long", queue[0].CandidateId);
        Assert.DoesNotContain(queue, static item => item.CandidateId == "all");
        Assert.DoesNotContain(queue, static item => item.CandidateId == "full");
        Assert.DoesNotContain(queue, static item => item.CandidateId == "partial");
        Assert.All(queue, static item => Assert.Equal(8192, item.Profile.CtxSize));
        Assert.Contains(queue, static item => item.Profile.Threads == 6);
        Assert.Contains(queue, static item => !item.Profile.FlashAttn);
        Assert.Contains(queue, static item =>
            item.Profile.BatchSize == 512 && item.Profile.UbatchSize == 128);
        Assert.Contains(queue, static item =>
            item.Profile.CtxSize == 8192
            && item.Profile.CacheTypeK == "q8_0"
            && item.Profile.CacheTypeV == "q8_0");
        Assert.Equal(
            queue.Count,
            queue.Select(static item => item.CandidateId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void BuildCandidateQueue_validates_context_per_parallel_slot()
    {
        var dualSlotValid = Candidate(
            "dual-valid",
            "accelerator-dual-slot-long-context-q4-kv",
            context: 16384,
            ngl: 37,
            cache: "q4_0") with
        {
            Profile = Candidate(
                "dual-valid-profile",
                "accelerator-dual-slot-long-context-q4-kv",
                context: 16384,
                ngl: 37,
                cache: "q4_0").Profile with
            {
                ProfileId = "dual-valid",
                Parallel = 2
            }
        };
        var dualSlotTooSmall = Candidate(
            "dual-too-small",
            "accelerator-dual-slot-long-context-q4-kv",
            context: 8192,
            ngl: 37,
            cache: "q4_0") with
        {
            Profile = Candidate(
                "dual-too-small-profile",
                "accelerator-dual-slot-long-context-q4-kv",
                context: 8192,
                ngl: 37,
                cache: "q4_0").Profile with
            {
                ProfileId = "dual-too-small",
                Parallel = 2
            }
        };

        var queue = LocalLlmQualificationRefinement.BuildCandidateQueue(
            new[] { dualSlotTooSmall, dualSlotValid },
            LocalLlmQualificationRefinement.CreateDefaultWorkload(),
            logicalProcessorCount: 8,
            maxAttempts: 8);

        Assert.Contains(queue, static candidate =>
            candidate.Profile.Parallel == 2
            && candidate.Profile.CtxSize == 16384);
        Assert.DoesNotContain(queue, static candidate =>
            candidate.Profile.Parallel == 2
            && candidate.Profile.CtxSize == 8192);
    }

    [Fact]
    public async Task RunAsync_refines_only_top_screened_topologies_and_ranks_complete_measurements()
    {
        var cudaAll = Candidate("cuda-all", "accelerator-all-model-layers", 4096, 37, "CUDA0");
        var cudaFull = Candidate("cuda-full", "accelerator-full-offload", 4096, 36, "CUDA0");
        var vulkan = Candidate(
            "vulkan-all",
            "accelerator-all-model-layers",
            4096,
            37,
            "Vulkan1",
            "llama.cpp-vulkan");
        var cpu = Candidate(
            "cpu",
            "cpu-balanced",
            4096,
            0,
            "none",
            "llama.cpp-cpu");
        var screeningScenario = new LocalLlmQualificationBenchmarkScenario("screen", 128, 32);
        var screening = Screening(
            new[]
            {
                Ladder("topology-cuda", cudaAll, cudaFull),
                Ladder("topology-vulkan", vulkan),
                Ladder("topology-cpu", cpu)
            },
            new[]
            {
                Score(cudaAll, screeningScenario, projected: 3.0),
                Score(vulkan, screeningScenario, projected: 4.0),
                Score(cpu, screeningScenario, projected: 20.0)
            });
        var benchmarkCalls = new List<string>();
        var workload = new[]
        {
            new LocalLlmQualificationBenchmarkScenario("control", 1024, 64, Weight: 4),
            new LocalLlmQualificationBenchmarkScenario("writer", 2048, 256, Weight: 1)
        };

        var outcome = await LocalLlmQualificationRefinement.RunAsync(
            screening,
            new[]
            {
                Runtime("llama.cpp-cuda", "CUDA0"),
                Runtime("llama.cpp-vulkan", "Vulkan1"),
                Runtime("llama.cpp-cpu", "CPU")
            },
            modelPath: @"C:\models\model.gguf",
            workload,
            availableSystemRamMiB: 16000,
            dedicatedDeviceMarginMiB: 512,
            systemRamReserveMiB: 4096,
            new LocalLlmQualificationRefinementOptions(
                TopTopologyCount: 2,
                MaxSuccessfulCandidatesPerTopology: 2,
                MaxAttemptsPerTopology: 6,
                LogicalProcessorCount: 8),
            fitRunner: (candidate, _) => Task.FromResult(Fit(candidate)),
            benchmarkRunner: (candidate, scenario, _) =>
            {
                benchmarkCalls.Add(candidate.CandidateId + ":" + scenario.ScenarioId);
                var isCuda = candidate.Profile.Runtime == "llama.cpp-cuda";
                return Task.FromResult(Success(
                    candidate,
                    scenario,
                    prompt: isCuda ? 200 : 100,
                    generation: isCuda ? 12 : 10));
            });

        Assert.Equal(new[] { "topology-cuda", "topology-vulkan" }, outcome.SelectedTopologyIds);
        Assert.DoesNotContain(outcome.Entries, static entry => entry.TopologyId == "topology-cpu");
        Assert.All(outcome.Entries, entry =>
            Assert.Equal(workload.Length, entry.Benchmarks.Count));
        Assert.Equal(outcome.Entries.Count * workload.Length, benchmarkCalls.Count);
        Assert.True(outcome.RankedCandidates[0].Eligible);
        Assert.Equal("llama.cpp-cuda", outcome.RankedCandidates[0].Candidate.Profile.Runtime);
    }

    [Fact]
    public async Task RunAsync_uses_flash_off_fallback_when_flash_candidates_do_not_fit()
    {
        var accelerated = Candidate(
            "gpu-all",
            "accelerator-all-model-layers",
            4096,
            37,
            "GPU0");
        var scenario = new LocalLlmQualificationBenchmarkScenario("control", 512, 64);
        var screening = Screening(
            new[] { Ladder("topology-gpu", accelerated) },
            new[] { Score(accelerated, scenario, projected: 1) });

        var outcome = await LocalLlmQualificationRefinement.RunAsync(
            screening,
            new[] { Runtime("llama.cpp-cuda", "GPU0") },
            modelPath: @"C:\models\model.gguf",
            new[] { scenario },
            availableSystemRamMiB: 16000,
            dedicatedDeviceMarginMiB: 512,
            systemRamReserveMiB: 4096,
            new LocalLlmQualificationRefinementOptions(1, 1, 8, 8),
            fitRunner: (candidate, _) => Task.FromResult(
                candidate.Profile.FlashAttn
                    ? new LocalLlmFitParamsProbeResult(
                        candidate.CandidateId,
                        Succeeded: true,
                        TimedOut: false,
                        ExitCode: 0,
                        DurationMs: 1,
                        new[] { new LocalLlmFitMemoryEstimate("GPU0", 15000, 0, 0) },
                        Diagnostic: string.Empty)
                    : Fit(candidate)),
            benchmarkRunner: (candidate, benchmarkScenario, _) => Task.FromResult(
                Success(candidate, benchmarkScenario, prompt: 100, generation: 10)));

        Assert.Contains(outcome.Entries, static entry =>
            entry.FitAssessment.Status == LocalLlmFitAssessmentStatus.DoesNotFit);
        var winner = Assert.Single(outcome.RankedCandidates, static score => score.Eligible);
        Assert.False(winner.Candidate.Profile.FlashAttn);
    }

    private static LocalLlmQualificationCandidate Candidate(
        string id,
        string kind,
        int context,
        int ngl,
        string device = "CUDA0",
        string runtime = "llama.cpp-cuda",
        string cache = "f16")
        => new(
            id,
            kind,
            $@"C:\runtime\{runtime}\llama-server.exe",
            WarmupProfileStore.CreateQwen3Q5Cuda4GbProfile() with
            {
                ProfileId = id,
                Runtime = runtime,
                CtxSize = context,
                Ngl = ngl,
                Threads = 4,
                ThreadsBatch = 4,
                DeviceIds = new[] { device },
                CacheTypeK = cache,
                CacheTypeV = cache
            },
            Array.Empty<string>());

    private static LocalLlmQualificationCandidateLadder Ladder(
        string topology,
        params LocalLlmQualificationCandidate[] candidates)
        => new(topology, candidates);

    private static LocalLlmQualificationScreeningOutcome Screening(
        IReadOnlyList<LocalLlmQualificationCandidateLadder> ladders,
        IReadOnlyList<LocalLlmQualificationCandidateScore> scores)
    {
        var entries = ladders
            .Select(ladder => new LocalLlmQualificationScreeningEntry(
                ladder.TopologyId,
                ladder.Candidates[0],
                Fit(ladder.Candidates[0]),
                new LocalLlmFitAssessment(
                    LocalLlmFitAssessmentStatus.Fits,
                    new[] { "fits" }),
                scores.First(score => score.Candidate.CandidateId == ladder.Candidates[0].CandidateId)
                    .Measurements[0]))
            .ToArray();
        return new LocalLlmQualificationScreeningOutcome(ladders, entries, scores);
    }

    private static LocalLlmQualificationCandidateScore Score(
        LocalLlmQualificationCandidate candidate,
        LocalLlmQualificationBenchmarkScenario scenario,
        double projected)
        => new(
            candidate,
            Eligible: true,
            ProjectedWorkloadSeconds: projected,
            Reasons: new[] { "pass" },
            Measurements: new[] { Success(candidate, scenario, 100, 10) });

    private static LocalLlmFitParamsProbeResult Fit(LocalLlmQualificationCandidate candidate)
        => new(
            candidate.CandidateId,
            Succeeded: true,
            TimedOut: false,
            ExitCode: 0,
            DurationMs: 1,
            new[]
            {
                new LocalLlmFitMemoryEstimate(
                    candidate.Profile.DeviceIds[0] == "none" ? "Host" : candidate.Profile.DeviceIds[0],
                    1000,
                    100,
                    100)
            },
            Diagnostic: string.Empty);

    private static LocalLlmRuntimeCapabilityProbeResult Runtime(string runtime, string device)
        => new(
            runtime,
            $@"C:\runtime\{runtime}\llama-server.exe",
            Succeeded: true,
            TimedOut: false,
            ExitCode: 0,
            DurationMs: 1,
            Devices: new[]
            {
                new LocalLlmRuntimeDeviceInfo(
                    runtime,
                    device,
                    device,
                    $@"C:\runtime\{runtime}\llama-server.exe",
                    "test")
                {
                    ReportedMemoryMiB = 16000,
                    ReportedFreeMemoryMiB = 15000,
                    MemoryArchitecture = "dedicated"
                }
            },
            Diagnostic: string.Empty);

    private static LocalLlmQualificationBenchmarkResult Success(
        LocalLlmQualificationCandidate candidate,
        LocalLlmQualificationBenchmarkScenario scenario,
        double prompt,
        double generation)
        => new(
            candidate.CandidateId,
            scenario.ScenarioId,
            Succeeded: true,
            TimedOut: false,
            ExitCode: 0,
            DurationMs: 10,
            PromptTokensPerSecond: prompt,
            GenerationTokensPerSecond: generation,
            Diagnostic: string.Empty);
}
