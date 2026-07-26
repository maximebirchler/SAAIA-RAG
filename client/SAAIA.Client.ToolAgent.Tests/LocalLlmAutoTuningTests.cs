using System.Reflection;
using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LocalLlmAutoTuningTests
{
    private const string ReferenceModelFileName = "Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf";

    [Fact]
    public void GgufMetadataReader_reads_reference_qwen_model_when_present()
    {
        var modelPath = TryFindReferenceModelPath();
        if (modelPath is null)
            return;

        var metadata = GgufMetadataReader.TryRead(modelPath);

        Assert.NotNull(metadata);
        Assert.Equal(36u, metadata!.BlockCount);
        Assert.Equal(2u, metadata.HeadCountKv);
    }

    [Fact]
    public void ComputeAutoTuning_uses_real_gguf_block_count_when_present()
    {
        var modelPath = TryFindReferenceModelPath();
        if (modelPath is null)
            return;

        var gpu = new GpuInfo(
            GpuVendor.Nvidia,
            "Quadro P520",
            4L * 1024 * 1024 * 1024,
            IsIntegrated: false,
            DetectionSource: "test");

        var tuning = GpuDetector.ComputeAutoTuning(gpu, modelPath);
        var fallback = GpuDetector.ComputeAutoTuning(gpu, Path.Combine(Path.GetDirectoryName(modelPath)!, "missing.gguf"));

        Assert.Equal(36, tuning.ngl);
        Assert.True(tuning.batch >= 512);
        Assert.Equal(24, fallback.ngl);
        Assert.True(fallback.batch >= 512);
    }

    [Fact]
    public void ApplyAutoTuningFlags_CudaRuntime_with_real_gguf_uses_block_count()
    {
        var modelPath = TryFindReferenceModelPath();
        if (modelPath is null)
            return;

        var settings = new AppSettings
        {
            LlamaExePath = @"C:\Users\test\AppData\Local\SAAIA\llm\runtime\win-cuda-x64\llama-server.exe",
            ModelPath = modelPath,
            ExtraArgs = "--ctx-size 3072",
            UbatchSize = 256,
            ThreadsBatch = 6,
            FlashAttn = false
        };
        var gpu = new GpuInfo(
            GpuVendor.Nvidia,
            "Quadro P520",
            4L * 1024 * 1024 * 1024,
            IsIntegrated: false,
            DetectionSource: "test");

        ApplyAutoTuningFlags(settings, gpu);

        Assert.Contains("-ngl 36", settings.ExtraArgs);
        Assert.Contains("-b 512", settings.ExtraArgs);
        Assert.Contains("--ubatch-size 256", settings.ExtraArgs);
    }

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
    public void ApplyAutoTuningFlags_matching_qualified_profile_drives_runtime_args()
    {
        var settings = new AppSettings
        {
            LlamaExePath = @"C:\Users\test\AppData\Local\SAAIA\llm\runtime\win-cuda-x64\llama-server.exe",
            ModelId = "Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf",
            ExtraArgs = string.Empty,
            QualifiedProfile = WarmupProfileStore.CreateQwen3Q5Cuda4GbProfile() with
            {
                CtxSize = 2048,
                BatchSize = 768,
                UbatchSize = 192,
                Threads = 5,
                ThreadsBatch = 3,
                Ngl = 36,
                FlashAttn = false,
                Mlock = true
            }
        };
        var gpu = new GpuInfo(
            GpuVendor.Nvidia,
            "Quadro P520",
            4L * 1024 * 1024 * 1024,
            IsIntegrated: false,
            DetectionSource: "test");

        ApplyAutoTuningFlags(settings, gpu);

        Assert.Contains("--ctx-size 2048", settings.ExtraArgs);
        Assert.Contains("-t 5", settings.ExtraArgs);
        Assert.Contains("-b 768", settings.ExtraArgs);
        Assert.Contains("-ngl 36", settings.ExtraArgs);
        Assert.Contains("--ubatch-size 192", settings.ExtraArgs);
        Assert.Contains("--threads-batch 3", settings.ExtraArgs);
        Assert.Contains("--flash-attn off", settings.ExtraArgs);
        Assert.Contains("--mlock", settings.ExtraArgs);
    }

    [Fact]
    public void ApplyAutoTuningFlags_preserves_explicit_user_args_over_qualified_profile()
    {
        var settings = new AppSettings
        {
            LlamaExePath = @"C:\Users\test\AppData\Local\SAAIA\llm\runtime\win-cuda-x64\llama-server.exe",
            ModelId = "Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf",
            ExtraArgs = "--ctx-size 1024 -t 2 -b 256 -ngl 12 --ubatch-size 64 --threads-batch 1 --flash-attn on",
            QualifiedProfile = WarmupProfileStore.CreateQwen3Q5Cuda4GbProfile() with
            {
                CtxSize = 2048,
                BatchSize = 768,
                UbatchSize = 192,
                Threads = 5,
                ThreadsBatch = 3,
                Ngl = 36,
                FlashAttn = false
            }
        };
        var gpu = new GpuInfo(
            GpuVendor.Nvidia,
            "Quadro P520",
            4L * 1024 * 1024 * 1024,
            IsIntegrated: false,
            DetectionSource: "test");

        ApplyAutoTuningFlags(settings, gpu);

        Assert.Contains("--ctx-size 1024", settings.ExtraArgs);
        Assert.Contains("-t 2", settings.ExtraArgs);
        Assert.Contains("-b 256", settings.ExtraArgs);
        Assert.Contains("-ngl 12", settings.ExtraArgs);
        Assert.Contains("--ubatch-size 64", settings.ExtraArgs);
        Assert.Contains("--threads-batch 1", settings.ExtraArgs);
        Assert.Contains("--flash-attn on", settings.ExtraArgs);
        Assert.DoesNotContain("--ctx-size 2048", settings.ExtraArgs);
        Assert.DoesNotContain("-b 768", settings.ExtraArgs);
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

    private static string? TryFindReferenceModelPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "models", ReferenceModelFileName);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        return null;
    }
}
