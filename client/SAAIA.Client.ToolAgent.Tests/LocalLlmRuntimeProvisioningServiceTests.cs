using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LocalLlmRuntimeProvisioningServiceTests
{
    [Fact]
    public void SelectApplicableBackends_covers_cpu_nvidia_and_vulkan_without_hardware_tier_tables()
    {
        var nvidia = LocalLlmRuntimeProvisioningService.SelectApplicableBackends(
            new[] { Gpu(GpuVendor.Nvidia, "GPU", integrated: false) });
        var intel = LocalLlmRuntimeProvisioningService.SelectApplicableBackends(
            new[] { Gpu(GpuVendor.Intel, "iGPU", integrated: true) });
        var amd = LocalLlmRuntimeProvisioningService.SelectApplicableBackends(
            new[] { Gpu(GpuVendor.Amd, "GPU", integrated: false) });
        var cpuOnly = LocalLlmRuntimeProvisioningService.SelectApplicableBackends(
            Array.Empty<GpuInfo>());

        Assert.Equal(
            new[] { "llama.cpp-cpu", "llama.cpp-cuda", "llama.cpp-vulkan" },
            nvidia.Select(static plan => plan.RuntimeId));
        Assert.Equal(
            new[] { "llama.cpp-cpu", "llama.cpp-sycl", "llama.cpp-vulkan" },
            intel.Select(static plan => plan.RuntimeId));
        Assert.Equal(
            new[] { "llama.cpp-cpu", "llama.cpp-hip", "llama.cpp-vulkan" },
            amd.Select(static plan => plan.RuntimeId));
        Assert.Equal(new[] { "llama.cpp-cpu" }, cpuOnly.Select(static plan => plan.RuntimeId));
        Assert.True(cpuOnly[0].Required);
        Assert.False(nvidia[0].Required);
        Assert.All(nvidia.Skip(1), static plan => Assert.False(plan.Required));
    }

    [Fact]
    public void SelectApplicableBackends_covers_heterogeneous_and_unknown_adapters_without_selecting_a_winner()
    {
        var heterogeneous = LocalLlmRuntimeProvisioningService.SelectApplicableBackends(
            new[]
            {
                Gpu(GpuVendor.Nvidia, "NVIDIA", integrated: false),
                Gpu(GpuVendor.Intel, "Intel iGPU", integrated: true),
                Gpu(GpuVendor.Amd, "AMD", integrated: false),
                Gpu(GpuVendor.Unknown, "Other", integrated: false)
            });
        var intelArc = LocalLlmRuntimeProvisioningService.SelectApplicableBackends(
            new[] { Gpu(GpuVendor.Intel, "Intel Arc", integrated: false) });
        var unknownOnly = LocalLlmRuntimeProvisioningService.SelectApplicableBackends(
            new[] { Gpu(GpuVendor.Unknown, "Unknown adapter", integrated: false) });

        Assert.Equal(
            new[]
            {
                "llama.cpp-cpu",
                "llama.cpp-cuda",
                "llama.cpp-sycl",
                "llama.cpp-hip",
                "llama.cpp-vulkan"
            },
            heterogeneous.Select(static plan => plan.RuntimeId));
        Assert.Equal(
            new[] { "llama.cpp-cpu", "llama.cpp-sycl", "llama.cpp-vulkan" },
            intelArc.Select(static plan => plan.RuntimeId));
        Assert.Equal(
            new[] { "llama.cpp-cpu", "llama.cpp-vulkan" },
            unknownOnly.Select(static plan => plan.RuntimeId));
        Assert.All(heterogeneous, static plan => Assert.False(plan.Required));
    }

    [Theory]
    [InlineData(@"C:\runtime\win-cuda-x64\llama-server.exe", "llama.cpp-cuda")]
    [InlineData(@"C:\runtime\win-vulkan-x64\llama-server.exe", "llama.cpp-vulkan")]
    [InlineData(@"C:\runtime\win-sycl-x64\llama-server.exe", "llama.cpp-sycl")]
    [InlineData(@"C:\runtime\win-hip-x64\llama-server.exe", "llama.cpp-hip")]
    [InlineData(@"C:\runtime\win-rocm-x64\llama-server.exe", "llama.cpp-hip")]
    [InlineData(@"C:\runtime\win-cpu-x64\llama-server.exe", "llama.cpp-cpu")]
    public void DetectRuntimeKey_covers_all_provisioned_windows_backends(
        string executablePath,
        string expectedRuntime)
    {
        Assert.Equal(
            expectedRuntime,
            RequalificationTriggerService.DetectRuntimeKey(executablePath));
    }

    [Fact]
    public async Task ProvisionApplicable_continues_after_optional_failure_but_requires_cpu_fallback()
    {
        var root = TempRoot();
        try
        {
            Directory.CreateDirectory(root);
            var cpuPath = Path.Combine(root, "win-cpu-x64", "llama-server.exe");
            var cudaPath = Path.Combine(root, "win-cuda-x64", "llama-server.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(cpuPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(cudaPath)!);
            await File.WriteAllTextAsync(cpuPath, "cpu");
            await File.WriteAllTextAsync(cudaPath, "cuda");
            var installers =
                new Dictionary<string, LocalLlmRuntimeProvisioningService.Installer>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["llama.cpp-cpu"] = (_, _) =>
                        Task.FromResult<(bool, string, string?)>((true, "ok", cpuPath)),
                    ["llama.cpp-cuda"] = (_, _) =>
                        Task.FromResult<(bool, string, string?)>((true, "ok", cudaPath)),
                    ["llama.cpp-vulkan"] = (_, _) =>
                        Task.FromResult<(bool, string, string?)>((false, "unsupported", null))
                };
            var outcome = await LocalLlmRuntimeProvisioningService.ProvisionApplicableAsync(
                new AppSettings { ModelId = "model" },
                new[] { Gpu(GpuVendor.Nvidia, "GPU", integrated: false) },
                installers: installers,
                runtimeTracker: (_, _) => true);

            Assert.True(outcome.Succeeded);
            Assert.Equal(3, outcome.Results.Count);
            Assert.Contains("optional_backend_failed:vulkan", outcome.Reasons);
            Assert.True(outcome.Results.Single(static result =>
                result.Plan.RuntimeId == "llama.cpp-cpu").TrackedForQualification);
            Assert.True(outcome.Results.Single(static result =>
                result.Plan.RuntimeId == "llama.cpp-cuda").TrackedForQualification);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static GpuInfo Gpu(GpuVendor vendor, string name, bool integrated)
        => new(
            vendor,
            name,
            integrated ? 0 : 8L * 1024 * 1024 * 1024,
            integrated,
            "test");

    private static string TempRoot()
        => Path.Combine(
            Path.GetTempPath(),
            "saaia-runtime-provisioning-tests-" + Guid.NewGuid().ToString("N"));
}
