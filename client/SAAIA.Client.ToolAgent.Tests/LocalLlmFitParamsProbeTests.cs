using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LocalLlmFitParamsProbeTests
{
    [Fact]
    public void BuildStartInfo_materializes_profile_and_fit_budget_for_multi_device_runtime()
    {
        var profile = WarmupProfileStore.CreateQwen3Q5Cuda4GbProfile() with
        {
            DeviceIds = new[] { "Vulkan0", "Vulkan1" },
            SplitMode = "layer",
            TensorSplit = new[] { 1d, 4d },
            MainGpu = 1,
            CacheTypeK = "q8_0",
            CacheTypeV = "q8_0",
            Ngl = 37
        };

        var startInfo = LocalLlmFitParamsProbe.BuildStartInfo(
            @"C:\runtime\llama-fit-params.exe",
            @"C:\models\model.gguf",
            profile,
            targetMarginMiB: 768,
            minimumContextSize: 2048);
        var arguments = startInfo.ArgumentList.ToArray();

        AssertPair(arguments, "-dev", "Vulkan0,Vulkan1");
        AssertPair(arguments, "-ts", "1,4");
        AssertPair(arguments, "-sm", "layer");
        AssertPair(arguments, "-mg", "1");
        AssertPair(arguments, "-ngl", "37");
        AssertPair(arguments, "-fit", "on");
        AssertPair(arguments, "-fitt", "768");
        AssertPair(arguments, "-fitc", "2048");
        AssertPair(arguments, "-fitp", "on");
    }

    [Fact]
    public void ParseEstimates_reads_dedicated_unified_and_host_rows()
    {
        var estimates = LocalLlmFitParamsProbe.ParseEstimates(
            """
            Vulkan0 353 17 45
            Vulkan1 1434 57 150
            Host 290 2 6
            """);

        Assert.Equal(3, estimates.Count);
        Assert.Equal(415, estimates[0].TotalMiB);
        Assert.Equal(1641, estimates[1].TotalMiB);
        Assert.Equal(298, estimates[2].TotalMiB);
    }

    [Fact]
    public void Assess_keeps_unified_memory_in_the_system_ram_budget_and_dedicated_memory_separate()
    {
        var probeResult = Result(
            new LocalLlmFitMemoryEstimate("Vulkan0", 353, 17, 45),
            new LocalLlmFitMemoryEstimate("Vulkan1", 1434, 57, 150),
            new LocalLlmFitMemoryEstimate("Host", 290, 2, 6));
        var runtime = Runtime(
            Device("Vulkan0", 16000, 15000, "unified"),
            Device("Vulkan1", 4095, 3376, "dedicated"));

        var assessment = LocalLlmFitParamsProbe.Assess(
            probeResult,
            runtime,
            availableSystemRamMiB: 8000,
            dedicatedDeviceMarginMiB: 768,
            systemRamReserveMiB: 4096);

        Assert.Equal(LocalLlmFitAssessmentStatus.Fits, assessment.Status);
    }

    [Fact]
    public void Assess_rejects_a_dedicated_device_without_the_requested_margin()
    {
        var assessment = LocalLlmFitParamsProbe.Assess(
            Result(
                new LocalLlmFitMemoryEstimate("CUDA0", 2800, 200, 200),
                new LocalLlmFitMemoryEstimate("Host", 200, 10, 10)),
            Runtime(Device("CUDA0", 4095, 3376, "dedicated")),
            availableSystemRamMiB: 16000,
            dedicatedDeviceMarginMiB: 768,
            systemRamReserveMiB: 4096);

        Assert.Equal(LocalLlmFitAssessmentStatus.DoesNotFit, assessment.Status);
        Assert.Contains(assessment.Reasons, reason => reason.StartsWith("dedicated_budget_exceeded:CUDA0", StringComparison.Ordinal));
    }

    [Fact]
    public void Assess_reports_indeterminate_instead_of_inventing_a_missing_budget()
    {
        var assessment = LocalLlmFitParamsProbe.Assess(
            Result(new LocalLlmFitMemoryEstimate("SYCL0", 1000, 100, 100)),
            Runtime(Device("SYCL0", null, null, "unknown")),
            availableSystemRamMiB: null,
            dedicatedDeviceMarginMiB: 768,
            systemRamReserveMiB: 4096);

        Assert.Equal(LocalLlmFitAssessmentStatus.Indeterminate, assessment.Status);
        Assert.Contains("device_budget_unknown:SYCL0", assessment.Reasons);
    }

    [Fact]
    public void Assess_rejects_a_topology_the_runtime_explicitly_reports_as_unsupported()
    {
        var failed = new LocalLlmFitParamsProbeResult(
            "candidate",
            Succeeded: false,
            TimedOut: false,
            ExitCode: -1,
            DurationMs: 10,
            Estimates: Array.Empty<LocalLlmFitMemoryEstimate>(),
            Diagnostic: "device Vulkan0 does not support split buffers");

        var assessment = LocalLlmFitParamsProbe.Assess(
            failed,
            Runtime(Device("Vulkan0", 16000, 15000, "unified")),
            availableSystemRamMiB: 16000,
            dedicatedDeviceMarginMiB: 768,
            systemRamReserveMiB: 4096);

        Assert.Equal(LocalLlmFitAssessmentStatus.DoesNotFit, assessment.Status);
        Assert.Contains("runtime_topology_unsupported", assessment.Reasons);
    }

    private static LocalLlmFitParamsProbeResult Result(params LocalLlmFitMemoryEstimate[] estimates)
        => new(
            "candidate",
            Succeeded: true,
            TimedOut: false,
            ExitCode: 0,
            DurationMs: 10,
            estimates,
            Diagnostic: string.Empty);

    private static LocalLlmRuntimeCapabilityProbeResult Runtime(params LocalLlmRuntimeDeviceInfo[] devices)
        => new(
            "llama.cpp-vulkan",
            @"C:\runtime\llama-server.exe",
            Succeeded: true,
            TimedOut: false,
            ExitCode: 0,
            DurationMs: 10,
            devices,
            Diagnostic: string.Empty);

    private static LocalLlmRuntimeDeviceInfo Device(
        string id,
        int? memory,
        int? free,
        string architecture)
        => new(
            "llama.cpp-vulkan",
            id,
            id,
            @"C:\runtime\llama-server.exe",
            "test")
        {
            ReportedMemoryMiB = memory,
            ReportedFreeMemoryMiB = free,
            ReportsSharedMemory = string.Equals(architecture, "unified", StringComparison.Ordinal),
            MemoryArchitecture = architecture
        };

    private static void AssertPair(IReadOnlyList<string> arguments, string name, string value)
    {
        var index = Array.IndexOf(arguments.ToArray(), name);
        Assert.InRange(index, 0, arguments.Count - 2);
        Assert.Equal(value, arguments[index + 1]);
    }
}
