using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LlamaCppProcessManagerTests
{
    [Fact]
    public async Task EnsureRunningAsync_skips_when_runtime_is_externally_managed()
    {
        var sut = new LlamaCppProcessManager();
        var settings = new AppSettings
        {
            UseLocalLlm = true,
            ManageLocalLlmProcess = false,
            LlamaExePath = "",
            ModelPath = ""
        };

        var result = await sut.EnsureRunningAsync(settings, CancellationToken.None);

        Assert.True(result.ok);
        Assert.Contains("externally", result.message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureRunningAsync_reports_missing_runtime_for_managed_embedded_mode()
    {
        var sut = new LlamaCppProcessManager();
        var settings = new AppSettings
        {
            UseLocalLlm = true,
            ManageLocalLlmProcess = true,
            LlamaExePath = "",
            ModelPath = ""
        };

        var result = await sut.EnsureRunningAsync(settings, CancellationToken.None);

        Assert.False(result.ok);
        Assert.Contains("runtime", result.message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildArgs_prefers_qualified_profile_over_conflicting_extra_args()
    {
        var settings = new AppSettings
        {
            Host = "127.0.0.1",
            Port = 1234,
            LlamaExePath = @"C:\runtime\win-cuda-x64\llama-server.exe",
            ModelPath = @"C:\models\Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf",
            ModelId = "Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf",
            ExtraArgs = "--ctx-size 8192 -t 6 -b 128 -ngl 16 --ubatch-size 128 --threads-batch 2 --flash-attn on --device Vulkan0 --split-mode row --cache-type-k q4_0 --parallel 2 --metrics",
            QualifiedProfile = WarmupProfileStore.CreateQwen3Q5Cuda4GbFallbackProfile()
        };

        var args = LlamaCppProcessManager.BuildArgs(settings);

        Assert.Contains("--ctx-size 3072", args);
        Assert.Contains("-b 256", args);
        Assert.Contains("-ngl 37", args);
        Assert.Contains("--ubatch-size 64", args);
        Assert.Contains("--threads-batch 4", args);
        Assert.Contains("--flash-attn off", args);
        Assert.Contains("--device CUDA0", args);
        Assert.Contains("--split-mode none", args);
        Assert.Contains("--cache-type-k q8_0", args);
        Assert.Contains("--cache-type-v q8_0", args);
        Assert.Contains("--parallel 1", args);
        Assert.Contains("--metrics", args);
        Assert.DoesNotContain("--ctx-size 8192", args);
        Assert.DoesNotContain("-b 128", args);
        Assert.DoesNotContain("-ngl 16", args);
        Assert.DoesNotContain("--device Vulkan0", args);
        Assert.DoesNotContain("--parallel 2", args);
    }

    [Fact]
    public void BuildArgs_materializes_a_measured_multi_gpu_and_quantized_kv_profile()
    {
        var settings = new AppSettings
        {
            Host = "127.0.0.1",
            Port = 1234,
            LlamaExePath = @"C:\runtime\win-cuda-x64\llama-server.exe",
            ModelPath = @"C:\models\Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf",
            ModelId = "Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf",
            QualifiedProfile = WarmupProfileStore.CreateQwen3Q5Cuda4GbProfile() with
            {
                ProfileId = "measured-multi-gpu",
                DeviceIds = new[] { "CUDA0", "CUDA1" },
                SplitMode = "row",
                TensorSplit = new[] { 3d, 1d },
                MainGpu = 1,
                CacheTypeK = "q8_0",
                CacheTypeV = "q4_0",
                Parallel = 2
            }
        };

        var args = LlamaCppProcessManager.BuildArgs(settings);

        Assert.Contains("--device CUDA0,CUDA1", args);
        Assert.Contains("--split-mode row", args);
        Assert.Contains("--tensor-split 3,1", args);
        Assert.Contains("--main-gpu 1", args);
        Assert.Contains("--cache-type-k q8_0", args);
        Assert.Contains("--cache-type-v q4_0", args);
        Assert.Contains("--parallel 2", args);
    }

    [Fact]
    public void BuildArgs_preserves_explicit_parallelism_when_user_supplies_it()
    {
        var settings = new AppSettings
        {
            Host = "127.0.0.1",
            Port = 1234,
            LlamaExePath = @"C:\runtime\win-cuda-x64\llama-server.exe",
            ModelPath = @"C:\models\Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf",
            ModelId = "Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf",
            ExtraArgs = "--ctx-size 3072 --parallel 2",
            QualifiedProfile = null
        };

        var args = LlamaCppProcessManager.BuildArgs(settings);

        Assert.Contains("--parallel 2", args);
        Assert.DoesNotContain("--parallel 1", args);
    }

    [Fact]
    public void BuildArgs_enables_jinja_for_qwen3_but_not_for_an_unrelated_custom_model()
    {
        var qwen3 = new AppSettings
        {
            ModelPath = @"C:\models\Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf",
            ModelId = "Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf",
            ExtraArgs = "--ctx-size 4096"
        };
        var custom = new AppSettings
        {
            ModelPath = @"C:\models\custom-model.gguf",
            ModelId = "custom-model.gguf",
            ExtraArgs = "--ctx-size 4096"
        };

        var qwen3Args = LlamaCppProcessManager.BuildArgs(qwen3);
        var customArgs = LlamaCppProcessManager.BuildArgs(custom);

        Assert.Contains("--jinja", qwen3Args);
        Assert.DoesNotContain("--jinja", customArgs);
    }

    [Fact]
    public void ExistingModelListContainsExpected_accepts_openai_data_id()
    {
        const string json = """
        {
          "object": "list",
          "data": [
            { "id": "Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf", "object": "model" }
          ]
        }
        """;

        Assert.True(LlamaCppProcessManager.ExistingModelListContainsExpected(
            json,
            "Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf"));
    }

    [Fact]
    public void ExistingModelListContainsExpected_rejects_other_model_on_same_port()
    {
        const string json = """
        {
          "models": [
            { "model": "TinyLlama-1.1B-Chat-v1.0-Q4_K_M.gguf" }
          ]
        }
        """;

        Assert.False(LlamaCppProcessManager.ExistingModelListContainsExpected(
            json,
            "Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf"));
    }

    [Fact]
    public async Task ResolveIdleTimeoutSecondsAsync_prefers_battery_policy_when_on_battery()
    {
        var root = NewTempRoot();
        try
        {
            var settings = new AppSettings
            {
                QualifiedProfile = WarmupProfileStore.CreateQwen3Q5Cuda4GbProfile(),
                StartupTimeoutSeconds = 90
            };

            await GovernanceArtifactStore.EnsureDefaultArtifactsAsync(settings, root);
            await GovernanceArtifactStore.WriteAsync(
                GovernanceArtifactStore.HardwareProbeFile,
                CreateHardwareProbe(isOnBattery: true),
                root);

            var idleTimeout = await LlamaCppProcessManager.ResolveIdleTimeoutSecondsAsync(settings, root);

            Assert.Equal(60, idleTimeout);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task ResolveIdleTimeoutSecondsAsync_falls_back_to_warmup_profile_on_ac()
    {
        var root = NewTempRoot();
        try
        {
            var settings = new AppSettings
            {
                QualifiedProfile = WarmupProfileStore.CreateQwen3Q5Cuda4GbProfile(),
                StartupTimeoutSeconds = 90
            };

            await GovernanceArtifactStore.EnsureDefaultArtifactsAsync(settings, root);
            await GovernanceArtifactStore.WriteAsync(
                GovernanceArtifactStore.HardwareProbeFile,
                CreateHardwareProbe(isOnBattery: false),
                root);

            var idleTimeout = await LlamaCppProcessManager.ResolveIdleTimeoutSecondsAsync(settings, root);

            Assert.Equal(120, idleTimeout);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task ResolveIdleTimeoutSecondsAsync_falls_back_to_startup_timeout_without_profile()
    {
        var settings = new AppSettings
        {
            StartupTimeoutSeconds = 75
        };

        var idleTimeout = await LlamaCppProcessManager.ResolveIdleTimeoutSecondsAsync(settings);

        Assert.Equal(75, idleTimeout);
    }

    [Fact]
    public async Task ApplyLastKnownGoodProfileForRuntimeStart_uses_compatible_profile_when_current_drifted()
    {
        var root = NewTempRoot();
        try
        {
            var lastGood = WarmupProfileStore.CreateQwen3Q5Cuda4GbProfile() with { CtxSize = 4096 };
            await RollbackManager.SaveLastKnownGoodAsync(lastGood, root);
            var settings = new AppSettings
            {
                LlamaExePath = @"C:\runtime\win-cuda-x64\llama-server.exe",
                ModelPath = @"C:\models\Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf",
                ModelId = "Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf",
                QualifiedProfile = lastGood with { CtxSize = 8192 }
            };

            await LlamaCppProcessManager.ApplyLastKnownGoodProfileForRuntimeStartAsync(settings, root);

            Assert.Equal(4096, settings.QualifiedProfile!.CtxSize);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static HardwareProbeArtifact CreateHardwareProbe(bool isOnBattery)
    {
        var gpu = new GpuInfo(
            GpuVendor.Nvidia,
            "Quadro P520",
            4096L * 1024 * 1024,
            IsIntegrated: false,
            DetectionSource: "test");
        var dxgi = new DxgiVideoMemorySnapshot(
            3072UL * 1024 * 1024,
            128UL * 1024 * 1024,
            2048UL * 1024 * 1024,
            0,
            "test");
        var memory = new SystemMemorySnapshot(
            16L * 1024 * 1024 * 1024,
            8L * 1024 * 1024 * 1024,
            "test");
        var power = new PowerStatusSnapshot(
            isOnBattery,
            75,
            "test");

        return HardwareProbeService.CreateArtifact(
            gpu: gpu,
            gpuDriverVersion: "573.71",
            dxgi: dxgi,
            memory: memory,
            machineName: "test-machine",
            processorCount: 8,
            is64BitOperatingSystem: true,
            capturedAt: DateTimeOffset.UtcNow,
            power: power);
    }

    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "saaia-llama-proc-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
