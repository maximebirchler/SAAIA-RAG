using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LlamaCppProcessManagerTests
{
    [Fact]
    public void BuildArgs_prefers_qualified_profile_over_conflicting_extra_args()
    {
        var settings = new AppSettings
        {
            Host = "127.0.0.1",
            Port = 1234,
            LlamaExePath = @"C:\runtime\win-cuda-x64\llama-server.exe",
            ModelPath = @"C:\models\Qwen2.5-3B-Instruct-Q4_K_M.gguf",
            ModelId = "Qwen2.5-3B-Instruct-Q4_K_M.gguf",
            ExtraArgs = "--ctx-size 4096 -t 6 -b 128 -ngl 16 --ubatch-size 128 --threads-batch 2 --flash-attn on --metrics",
            QualifiedProfile = WarmupProfileStore.CreateReferenceCudaFallbackProfile()
        };

        var args = LlamaCppProcessManager.BuildArgs(settings);

        Assert.Contains("--ctx-size 3072", args);
        Assert.Contains("-b 1024", args);
        Assert.Contains("-ngl 36", args);
        Assert.Contains("--ubatch-size 256", args);
        Assert.Contains("--threads-batch 6", args);
        Assert.Contains("--flash-attn off", args);
        Assert.Contains("--metrics", args);
        Assert.DoesNotContain("--ctx-size 4096", args);
        Assert.DoesNotContain("-b 128", args);
        Assert.DoesNotContain("-ngl 16", args);
    }

    [Fact]
    public void ExistingModelListContainsExpected_accepts_openai_data_id()
    {
        const string json = """
        {
          "object": "list",
          "data": [
            { "id": "Qwen2.5-3B-Instruct-Q4_K_M.gguf", "object": "model" }
          ]
        }
        """;

        Assert.True(LlamaCppProcessManager.ExistingModelListContainsExpected(
            json,
            "Qwen2.5-3B-Instruct-Q4_K_M.gguf"));
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
            "Qwen2.5-3B-Instruct-Q4_K_M.gguf"));
    }

    [Fact]
    public async Task ResolveIdleTimeoutSecondsAsync_prefers_battery_policy_when_on_battery()
    {
        var root = NewTempRoot();
        try
        {
            var settings = new AppSettings
            {
                QualifiedProfile = WarmupProfileStore.CreateReferenceCudaProfile(),
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
                QualifiedProfile = WarmupProfileStore.CreateReferenceCudaProfile(),
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
