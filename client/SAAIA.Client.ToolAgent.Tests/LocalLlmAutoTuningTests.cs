using System.Reflection;
using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LocalLlmAutoTuningTests
{
    [Fact]
    public void ApplyAutoTuningFlags_CudaRuntime_injects_bench_confirmed_flags()
    {
        var settings = new AppSettings
        {
            LlamaExePath = @"C:\Users\test\AppData\Local\SAAIA\llm\runtime\win-cuda-x64\llama-server.exe",
            ExtraArgs = "--ctx-size 3072",
            UbatchSize = 256,
            ThreadsBatch = 6,
            FlashAttn = null
        };
        var gpu = new GpuInfo(
            GpuVendor.Nvidia,
            "Quadro P520",
            4L * 1024 * 1024 * 1024,
            IsIntegrated: false,
            DetectionSource: "test");

        ApplyAutoTuningFlags(settings, gpu);

        Assert.Contains("--ctx-size 3072", settings.ExtraArgs);
        Assert.Contains("-b 512", settings.ExtraArgs);
        Assert.Contains("-ngl 24", settings.ExtraArgs);
        Assert.Contains("--ubatch-size 256", settings.ExtraArgs);
        Assert.Contains("--threads-batch 6", settings.ExtraArgs);
        Assert.Contains("--flash-attn on", settings.ExtraArgs);
    }

    [Fact]
    public void ApplyAutoTuningFlags_CudaRuntime_with_disabled_flash_attn_injects_explicit_off()
    {
        var settings = new AppSettings
        {
            LlamaExePath = @"C:\Users\test\AppData\Local\SAAIA\llm\runtime\win-cuda-x64\llama-server.exe",
            ExtraArgs = "--ctx-size 3072",
            FlashAttn = false
        };
        var gpu = new GpuInfo(
            GpuVendor.Nvidia,
            "Quadro P520",
            4L * 1024 * 1024 * 1024,
            IsIntegrated: false,
            DetectionSource: "test");

        ApplyAutoTuningFlags(settings, gpu);

        Assert.Contains("--flash-attn off", settings.ExtraArgs);
    }

    [Fact]
    public void ApplyAutoTuningFlags_CpuRuntime_does_not_inject_gpu_only_flags()
    {
        var settings = new AppSettings
        {
            LlamaExePath = @"C:\Users\test\AppData\Local\SAAIA\llm\runtime\win-cpu-x64\llama-server.exe",
            ExtraArgs = "--ctx-size 3072"
        };

        ApplyAutoTuningFlags(settings, gpu: null);

        Assert.DoesNotContain("--flash-attn", settings.ExtraArgs);
        Assert.DoesNotContain("-ngl", settings.ExtraArgs);
    }

    private static void ApplyAutoTuningFlags(AppSettings settings, GpuInfo? gpu)
    {
        var method = typeof(LocalLlmBootstrapper).GetMethod(
            "ApplyAutoTuningFlags",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        method!.Invoke(null, new object?[] { settings, gpu });
    }
}
