using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LocalLlmQualificationCandidateFactoryTests
{
    [Fact]
    public void Build_enumerates_cpu_single_accelerator_and_multi_accelerator_profiles_without_choosing_a_winner()
    {
        var probes = new[]
        {
            Probe(
                "llama.cpp-cpu",
                @"C:\runtime\cpu\llama-server.exe",
                Device("llama.cpp-cpu", "CPU", "CPU backend")),
            Probe(
                "llama.cpp-cuda",
                @"C:\runtime\cuda\llama-server.exe",
                Device("llama.cpp-cuda", "CUDA0", "Quadro P520 (4095 MiB, 3376 MiB free)", 4095, 3376)),
            Probe(
                "llama.cpp-vulkan",
                @"C:\runtime\vulkan\llama-server.exe",
                Device("llama.cpp-vulkan", "Vulkan0", "Intel UHD (8192 MiB shared)", 8192, 6000, shared: true),
                Device("llama.cpp-vulkan", "Vulkan1", "Quadro P520 (4095 MiB)", 4095, 3000))
        };

        var candidates = LocalLlmQualificationCandidateFactory.Build(
            "qwen-test-q4",
            modelPath: null,
            probes,
            logicalProcessorCount: 8,
            blockCountOverride: 40);

        Assert.Equal(37, candidates.Count);
        Assert.Equal(candidates.Count, candidates.Select(static candidate => candidate.CandidateId).Distinct().Count());
        Assert.Equal(3, candidates.Count(static candidate => candidate.Profile.Runtime == "llama.cpp-cpu"));
        Assert.All(
            candidates.Where(static candidate => candidate.Profile.Runtime == "llama.cpp-cpu"),
            candidate =>
            {
                Assert.Equal(0, candidate.Profile.Ngl);
                Assert.Equal(new[] { "none" }, candidate.Profile.DeviceIds);
            });
        Assert.Contains(candidates, candidate =>
            candidate.CandidateKind == "accelerator-full-offload"
            && candidate.Profile.DeviceIds.SequenceEqual(new[] { "CUDA0" })
            && candidate.Profile.Ngl == 40);
        Assert.Contains(candidates, candidate =>
            candidate.CandidateKind == "accelerator-all-model-layers"
            && candidate.Profile.DeviceIds.SequenceEqual(new[] { "CUDA0" })
            && candidate.Profile.Ngl == 41);
        Assert.Contains(candidates, candidate =>
            candidate.CandidateKind == "accelerator-long-context-partial-offload-q8-kv"
            && candidate.Profile.DeviceIds.SequenceEqual(new[] { "CUDA0" })
            && candidate.Profile.CtxSize == 8192
            && candidate.Profile.Ngl == 20
            && candidate.Profile.CacheTypeK == "q8_0"
            && candidate.Profile.CacheTypeV == "q8_0");
        Assert.Contains(candidates, candidate =>
            candidate.CandidateKind == "multi-accelerator-layer-capacity"
            && candidate.Profile.DeviceIds.SequenceEqual(new[] { "Vulkan0", "Vulkan1" })
            && candidate.Profile.TensorSplit.SequenceEqual(new[] { 6000d, 3000d })
            && candidate.Profile.Ngl == 41
            && candidate.Profile.MainGpu == 0);
        Assert.Contains(candidates, candidate =>
            candidate.CandidateKind == "multi-accelerator-row-balanced"
            && candidate.Profile.CacheTypeK == "q8_0"
            && candidate.Profile.CacheTypeV == "q8_0");
        Assert.Contains(candidates, candidate =>
            candidate.CandidateKind == "multi-accelerator-long-context-row-balanced"
            && candidate.Profile.CtxSize == 8192
            && candidate.Profile.CacheTypeK == "q8_0"
            && candidate.Profile.CacheTypeV == "q8_0");
        Assert.Contains(candidates, candidate =>
            candidate.CandidateKind == "multi-accelerator-layer-prefer-vulkan1"
            && candidate.Profile.TensorSplit.SequenceEqual(new[] { 1d, 4d })
            && candidate.Profile.MainGpu == 1);
        Assert.Contains(candidates, candidate =>
            candidate.Profile.DeviceIds.Contains("Vulkan0")
            && candidate.CapabilityRefs.Contains("memory:Vulkan0:unified"));
    }

    [Fact]
    public void Build_is_bounded_and_ignores_failed_runtime_probes()
    {
        var failed = new LocalLlmRuntimeCapabilityProbeResult(
            "llama.cpp-vulkan",
            @"C:\runtime\vulkan\llama-server.exe",
            Succeeded: false,
            TimedOut: true,
            ExitCode: null,
            DurationMs: 10000,
            Devices: new[] { Device("llama.cpp-vulkan", "Vulkan0", "GPU") },
            Diagnostic: "timeout");
        var cuda = Probe(
            "llama.cpp-cuda",
            @"C:\runtime\cuda\llama-server.exe",
            Device("llama.cpp-cuda", "CUDA0", "GPU 0"),
            Device("llama.cpp-cuda", "CUDA1", "GPU 1"));

        var candidates = LocalLlmQualificationCandidateFactory.Build(
            "model",
            modelPath: null,
            new[] { failed, cuda },
            logicalProcessorCount: 64,
            blockCountOverride: 80,
            maxCandidates: 5);

        Assert.Equal(5, candidates.Count);
        Assert.All(candidates, candidate => Assert.Equal("llama.cpp-cuda", candidate.Profile.Runtime));
        Assert.All(candidates, candidate => Assert.InRange(candidate.Profile.Threads, 1, 32));
    }

    [Fact]
    public void Build_round_robins_detected_topologies_before_deepening_profiles()
    {
        var probes = new[]
        {
            Probe(
                "llama.cpp-cpu",
                @"C:\runtime\cpu\llama-server.exe",
                Device("llama.cpp-cpu", "CPU", "CPU backend")),
            Probe(
                "llama.cpp-cuda",
                @"C:\runtime\cuda\llama-server.exe",
                Device("llama.cpp-cuda", "CUDA0", "GPU 0"),
                Device("llama.cpp-cuda", "CUDA1", "GPU 1")),
            Probe(
                "llama.cpp-vulkan",
                @"C:\runtime\vulkan\llama-server.exe",
                Device("llama.cpp-vulkan", "Vulkan0", "GPU 0"),
                Device("llama.cpp-vulkan", "Vulkan1", "GPU 1"))
        };

        var candidates = LocalLlmQualificationCandidateFactory.Build(
            "model",
            modelPath: null,
            probes,
            logicalProcessorCount: 16,
            blockCountOverride: 40,
            maxCandidates: 10);
        var topologyKeys = candidates
            .Select(candidate =>
                candidate.Profile.Runtime
                + "|"
                + string.Join(",", candidate.Profile.DeviceIds)
                + "|"
                + candidate.Profile.SplitMode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(10, candidates.Count);
        Assert.Contains("llama.cpp-cpu|none|none", topologyKeys);
        Assert.Contains("llama.cpp-cuda|CUDA0|none", topologyKeys);
        Assert.Contains("llama.cpp-cuda|CUDA1|none", topologyKeys);
        Assert.Contains("llama.cpp-cuda|CUDA0,CUDA1|layer", topologyKeys);
        Assert.Contains("llama.cpp-cuda|CUDA0,CUDA1|row", topologyKeys);
        Assert.Contains("llama.cpp-vulkan|Vulkan0|none", topologyKeys);
        Assert.Contains("llama.cpp-vulkan|Vulkan1|none", topologyKeys);
        Assert.Contains("llama.cpp-vulkan|Vulkan0,Vulkan1|layer", topologyKeys);
        Assert.Contains("llama.cpp-vulkan|Vulkan0,Vulkan1|row", topologyKeys);
    }

    private static LocalLlmRuntimeCapabilityProbeResult Probe(
        string runtimeId,
        string path,
        params LocalLlmRuntimeDeviceInfo[] devices)
        => new(
            runtimeId,
            path,
            Succeeded: true,
            TimedOut: false,
            ExitCode: 0,
            DurationMs: 10,
            Devices: devices,
            Diagnostic: string.Empty);

    private static LocalLlmRuntimeDeviceInfo Device(
        string runtimeId,
        string deviceId,
        string description,
        int? memoryMiB = null,
        int? freeMiB = null,
        bool shared = false)
        => new(
            runtimeId,
            deviceId,
            description,
            @"C:\runtime\llama-server.exe",
            "test")
        {
            ReportedMemoryMiB = memoryMiB,
            ReportedFreeMemoryMiB = freeMiB,
            ReportsSharedMemory = shared,
            MemoryArchitecture = shared ? "unified" : "unknown"
        };
}
