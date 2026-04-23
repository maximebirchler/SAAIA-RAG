using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LlamaCppProcessManagerTests
{
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
