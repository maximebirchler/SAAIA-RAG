using System.Reflection;
using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LocalLlmRuntimeCapabilityProbeTests
{
    [Fact]
    public void ParseDevices_reads_cuda_and_vulkan_device_ids_without_treating_backend_logs_as_devices()
    {
        var cuda = LocalLlmRuntimeCapabilityProbe.ParseDevices(
            "llama.cpp-cuda",
            @"C:\runtime\cuda\llama-server.exe",
            """
            Available devices:
              CUDA0: Quadro P520 (4095 MiB, 3376 MiB free)
            """,
            """
            ggml_cuda_init: found 1 CUDA devices:
              Device 0: Quadro P520, compute capability 6.1
            load_backend: loaded CPU backend from ggml-cpu-haswell.dll
            """);
        var vulkan = LocalLlmRuntimeCapabilityProbe.ParseDevices(
            "llama.cpp-vulkan",
            @"C:\runtime\vulkan\llama-server.exe",
            """
            Available devices:
              Vulkan0: Intel(R) UHD Graphics (15360 MiB, 15000 MiB free)
              Vulkan1: NVIDIA Quadro P520 (4095 MiB)
            """,
            """
            ggml_vulkan: 0 = Intel(R) UHD Graphics (Intel Corporation) | uma: 1 | fp16: 1
            ggml_vulkan: 1 = Quadro P520 (NVIDIA) | uma: 0 | fp16: 0
            load_backend: loaded Vulkan backend
            """);

        var cudaDevice = Assert.Single(cuda);
        Assert.Equal("CUDA0", cudaDevice.DeviceId);
        Assert.Contains("3376 MiB free", cudaDevice.Description);
        Assert.Equal("llama.cpp --list-devices", cudaDevice.DiscoverySource);
        Assert.Equal(new[] { "Vulkan0", "Vulkan1" }, vulkan.Select(static device => device.DeviceId));
        Assert.True(vulkan[0].ReportsSharedMemory);
        Assert.Equal("unified", vulkan[0].MemoryArchitecture);
        Assert.False(vulkan[1].ReportsSharedMemory);
        Assert.Equal("dedicated", vulkan[1].MemoryArchitecture);
    }

    [Fact]
    public void ParseDevices_exposes_a_cpu_runtime_when_llama_reports_only_the_loaded_cpu_backend()
    {
        var devices = LocalLlmRuntimeCapabilityProbe.ParseDevices(
            "llama.cpp-cpu",
            @"C:\runtime\cpu\llama-server.exe",
            "Available devices:",
            "load_backend: loaded CPU backend from C:\\runtime\\cpu\\ggml-cpu-haswell.dll");

        var cpu = Assert.Single(devices);
        Assert.Equal("CPU", cpu.DeviceId);
        Assert.Contains("ggml-cpu-haswell.dll", cpu.Description);
        Assert.Equal("llama.cpp backend load", cpu.DiscoverySource);
        Assert.Equal("system", cpu.MemoryArchitecture);
    }

    [Fact]
    public void EnrichMemoryArchitecture_correlates_runtime_devices_with_integrated_and_discrete_windows_adapters()
    {
        var devices = LocalLlmRuntimeCapabilityProbe.ParseDevices(
            "llama.cpp-vulkan",
            @"C:\runtime\vulkan\llama-server.exe",
            """
            Available devices:
              Vulkan0: Intel(R) UHD Graphics (16270 MiB, 15502 MiB free)
              Vulkan1: Quadro P520 (4226 MiB, 3624 MiB free)
            """,
            string.Empty);
        var probe = new LocalLlmRuntimeCapabilityProbeResult(
            "llama.cpp-vulkan",
            @"C:\runtime\vulkan\llama-server.exe",
            Succeeded: true,
            TimedOut: false,
            ExitCode: 0,
            DurationMs: 10,
            Devices: devices,
            Diagnostic: string.Empty);
        var inventory = new[]
        {
            new GpuInfo(
                GpuVendor.Intel,
                "Intel(R) UHD Graphics",
                DedicatedVramBytes: 0,
                IsIntegrated: true,
                DetectionSource: "cim")
            {
                StableDeviceId = "intel-igpu"
            },
            new GpuInfo(
                GpuVendor.Nvidia,
                "NVIDIA Quadro P520",
                DedicatedVramBytes: 4095L * 1024 * 1024,
                IsIntegrated: false,
                DetectionSource: "nvidia-smi+cim")
            {
                StableDeviceId = "nvidia-p520"
            }
        };

        var enriched = Assert.Single(
            LocalLlmRuntimeCapabilityProbe.EnrichMemoryArchitecture(
                new[] { probe },
                inventory));

        Assert.Equal("unified", enriched.Devices[0].MemoryArchitecture);
        Assert.True(enriched.Devices[0].ReportsSharedMemory);
        Assert.Equal("intel-igpu", enriched.Devices[0].MatchedHardwareDeviceId);
        Assert.Equal("dedicated", enriched.Devices[1].MemoryArchitecture);
        Assert.False(enriched.Devices[1].ReportsSharedMemory);
        Assert.Equal("nvidia-p520", enriched.Devices[1].MatchedHardwareDeviceId);
        Assert.All(enriched.Devices, static device =>
            Assert.Equal("normalized_adapter_name", device.HardwareMatchSource));
    }

    [Theory]
    [InlineData(0, "[level_zero:gpu:0] Intel Arc", "", null)]
    [InlineData(0, "", "SYCL Exception: UR_RESULT_ERROR_UNINITIALIZED", "sycl_inventory_failed")]
    [InlineData(-1073741819, "", "access violation", "sycl_inventory_exit")]
    [InlineData(0, "", "", "sycl_device_inventory_empty")]
    public void EvaluateSyclInventory_rejects_unusable_drivers_but_accepts_reported_devices(
        int exitCode,
        string stdout,
        string stderr,
        string? expectedReason)
    {
        var method = typeof(LocalLlmRuntimeCapabilityProbe).GetMethod(
            "EvaluateSyclInventory",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var diagnostic = (string?)method!.Invoke(
            null,
            new object?[] { exitCode, stdout, stderr });

        if (expectedReason is null)
            Assert.Null(diagnostic);
        else
            Assert.StartsWith(expectedReason, diagnostic, StringComparison.Ordinal);
    }
}
