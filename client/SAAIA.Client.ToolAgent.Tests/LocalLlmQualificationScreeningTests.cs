using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LocalLlmQualificationScreeningTests
{
    [Fact]
    public void BuildLadders_groups_runtime_device_and_split_mode_then_orders_safe_fallbacks()
    {
        var all = Candidate("all", "accelerator-all-model-layers", "CUDA0", ngl: 37);
        var full = Candidate("full", "accelerator-full-offload", "CUDA0", ngl: 36);
        var partial = Candidate("partial", "accelerator-partial-offload", "CUDA0", ngl: 18);
        var cpu = Candidate("cpu", "cpu-balanced", "none", ngl: 0, runtime: "llama.cpp-cpu");

        var ladders = LocalLlmQualificationScreening.BuildLadders(
            new[] { partial, cpu, full, all });

        Assert.Equal(2, ladders.Count);
        var cuda = Assert.Single(
            ladders,
            ladder => ladder.Candidates[0].Profile.Runtime == "llama.cpp-cuda");
        Assert.Equal(new[] { "all", "full", "partial" }, cuda.Candidates.Select(static item => item.CandidateId));
    }

    [Fact]
    public async Task RunAsync_falls_back_within_a_topology_then_ranks_only_measured_representatives()
    {
        var cudaAll = Candidate("cuda-all", "accelerator-all-model-layers", "CUDA0", ngl: 37);
        var cudaPartial = Candidate("cuda-partial", "accelerator-partial-offload", "CUDA0", ngl: 18);
        var vulkan = Candidate(
            "vulkan",
            "accelerator-all-model-layers",
            "Vulkan0",
            ngl: 37,
            runtime: "llama.cpp-vulkan");
        var scenario = new LocalLlmQualificationBenchmarkScenario("screen", 512, 64);
        var benchmarked = new List<string>();

        var outcome = await LocalLlmQualificationScreening.RunAsync(
            new[] { cudaPartial, vulkan, cudaAll },
            new[]
            {
                Runtime("llama.cpp-cuda", "CUDA0", 4095, 3376),
                Runtime("llama.cpp-vulkan", "Vulkan0", 16000, 15000, unified: true)
            },
            modelPath: @"C:\models\model.gguf",
            scenario,
            availableSystemRamMiB: 16000,
            dedicatedDeviceMarginMiB: 768,
            systemRamReserveMiB: 4096,
            fitRunner: (candidate, _) => Task.FromResult(
                candidate.CandidateId == "cuda-all"
                    ? Fit(candidate, new LocalLlmFitMemoryEstimate("CUDA0", 3000, 200, 200))
                    : Fit(
                        candidate,
                        new LocalLlmFitMemoryEstimate(candidate.Profile.DeviceIds[0], 1000, 100, 100),
                        new LocalLlmFitMemoryEstimate("Host", 200, 10, 10))),
            benchmarkRunner: (candidate, _) =>
            {
                benchmarked.Add(candidate.CandidateId);
                var prompt = candidate.CandidateId == "cuda-partial" ? 200d : 80d;
                return Task.FromResult(new LocalLlmQualificationBenchmarkResult(
                    candidate.CandidateId,
                    scenario.ScenarioId,
                    Succeeded: true,
                    TimedOut: false,
                    ExitCode: 0,
                    DurationMs: 10,
                    PromptTokensPerSecond: prompt,
                    GenerationTokensPerSecond: 12,
                    Diagnostic: string.Empty));
            });

        Assert.DoesNotContain("cuda-all", benchmarked);
        Assert.Contains("cuda-partial", benchmarked);
        Assert.Contains("vulkan", benchmarked);
        Assert.Equal("cuda-partial", outcome.RankedRepresentatives[0].Candidate.CandidateId);
        var rejected = Assert.Single(
            outcome.Entries,
            entry => entry.Candidate.CandidateId == "cuda-all");
        Assert.Equal(LocalLlmFitAssessmentStatus.DoesNotFit, rejected.FitAssessment.Status);
        Assert.Null(rejected.Benchmark);
    }

    [Fact]
    public async Task RunAsync_stops_a_ladder_after_an_explicit_topology_capability_failure()
    {
        var first = Candidate("row-capacity", "multi-accelerator-row-capacity", "Vulkan0", 37, "llama.cpp-vulkan");
        var second = Candidate("row-balanced", "multi-accelerator-row-balanced", "Vulkan0", 37, "llama.cpp-vulkan");
        first = first with
        {
            Profile = first.Profile with
            {
                DeviceIds = new[] { "Vulkan0", "Vulkan1" },
                SplitMode = "row",
                TensorSplit = new[] { 4d, 1d }
            }
        };
        second = second with
        {
            Profile = second.Profile with
            {
                DeviceIds = new[] { "Vulkan0", "Vulkan1" },
                SplitMode = "row",
                TensorSplit = new[] { 1d, 1d }
            }
        };
        var benchmarkCalls = 0;

        var outcome = await LocalLlmQualificationScreening.RunAsync(
            new[] { first, second },
            new[]
            {
                Runtime("llama.cpp-vulkan", "Vulkan0", 16000, 15000, unified: true)
            },
            modelPath: @"C:\models\model.gguf",
            new LocalLlmQualificationBenchmarkScenario("screen", 128, 32),
            availableSystemRamMiB: 16000,
            dedicatedDeviceMarginMiB: 768,
            systemRamReserveMiB: 4096,
            fitRunner: (candidate, _) => Task.FromResult(new LocalLlmFitParamsProbeResult(
                candidate.CandidateId,
                Succeeded: false,
                TimedOut: false,
                ExitCode: -1,
                DurationMs: 1,
                Estimates: Array.Empty<LocalLlmFitMemoryEstimate>(),
                Diagnostic: "device Vulkan0 does not support split buffers")),
            benchmarkRunner: (_, _) =>
            {
                benchmarkCalls++;
                throw new InvalidOperationException("Benchmark must not run for an unsupported topology.");
            });

        Assert.Equal(0, benchmarkCalls);
        Assert.Single(outcome.Entries);
        Assert.Equal("row-capacity", outcome.Entries[0].Candidate.CandidateId);
        Assert.Equal(LocalLlmFitAssessmentStatus.DoesNotFit, outcome.Entries[0].FitAssessment.Status);
    }

    private static LocalLlmQualificationCandidate Candidate(
        string id,
        string kind,
        string device,
        int ngl,
        string runtime = "llama.cpp-cuda")
        => new(
            id,
            kind,
            $@"C:\runtime\{runtime}\llama-server.exe",
            WarmupProfileStore.CreateQwen3Q5Cuda4GbProfile() with
            {
                ProfileId = id,
                Runtime = runtime,
                DeviceIds = new[] { device },
                Ngl = ngl
            },
            Array.Empty<string>());

    private static LocalLlmRuntimeCapabilityProbeResult Runtime(
        string runtime,
        string device,
        int memory,
        int free,
        bool unified = false)
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
                    ReportedMemoryMiB = memory,
                    ReportedFreeMemoryMiB = free,
                    ReportsSharedMemory = unified,
                    MemoryArchitecture = unified ? "unified" : "dedicated"
                }
            },
            Diagnostic: string.Empty);

    private static LocalLlmFitParamsProbeResult Fit(
        LocalLlmQualificationCandidate candidate,
        params LocalLlmFitMemoryEstimate[] estimates)
        => new(
            candidate.CandidateId,
            Succeeded: true,
            TimedOut: false,
            ExitCode: 0,
            DurationMs: 1,
            estimates,
            Diagnostic: string.Empty);
}
