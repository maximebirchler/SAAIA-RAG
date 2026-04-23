using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class GovernanceArtifactStoreTests
{
    [Fact]
    public void QualifiedProfile_roundtrips_with_required_cdc_v31_fields()
    {
        var profile = WarmupProfileStore.CreateReferenceCudaProfile();

        var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var restored = JsonSerializer.Deserialize<QualifiedProfile>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(restored);
        Assert.Equal("llama.cpp-cuda", restored!.Runtime);
        Assert.Equal("qwen2.5-3b-instruct-q4-k-m", restored.ModelId);
        Assert.Equal(3072, restored.CtxSize);
        Assert.Equal(1024, restored.BatchSize);
        Assert.Equal(256, restored.UbatchSize);
        Assert.Equal(6, restored.ThreadsBatch);
        Assert.Equal(36, restored.Ngl);
        Assert.True(restored.FlashAttn);
        Assert.False(string.IsNullOrWhiteSpace(restored.FallbackProfileRef));
    }

    [Fact]
    public async Task GovernanceArtifactStore_writes_reads_and_verifies_sha256_sidecar()
    {
        var root = NewTempRoot();
        try
        {
            var artifact = ModelCatalogStore.CreateDefaultCatalog();

            await GovernanceArtifactStore.WriteAsync(GovernanceArtifactStore.ModelCatalogFile, artifact, root);
            var read = await GovernanceArtifactStore.ReadAsync<ModelCatalogArtifact>(
                GovernanceArtifactStore.ModelCatalogFile,
                root);

            Assert.Equal(GovernanceArtifactReadStatus.Ok, read.Status);
            Assert.NotNull(read.Value);
            Assert.True(File.Exists(Path.Combine(root, GovernanceArtifactStore.ModelCatalogFile + ".sha256")));
            Assert.Contains(read.Value!.Items, item =>
                item.ModelId == "qwen2.5-3b-instruct-q4-k-m"
                && item.License.LicenseFamily == "qwen"
                && item.License.CommercialUseThresholdMau == 100000000
                && item.Gguf.HeadCountKv == 2);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task GovernanceArtifactStore_corrupted_artifact_returns_degraded_status_without_throwing()
    {
        var root = NewTempRoot();
        try
        {
            await GovernanceArtifactStore.WriteAsync(
                GovernanceArtifactStore.WarmupProfilesFile,
                WarmupProfileStore.CreateDefaultWarmupProfiles(),
                root);

            await File.AppendAllTextAsync(Path.Combine(root, GovernanceArtifactStore.WarmupProfilesFile), "\n{\"corrupt\":true}");

            var read = await GovernanceArtifactStore.ReadAsync<WarmupProfilesArtifact>(
                GovernanceArtifactStore.WarmupProfilesFile,
                root);

            Assert.Equal(GovernanceArtifactReadStatus.ChecksumMismatch, read.Status);
            Assert.Null(read.Value);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task EnsureDefaultArtifacts_creates_snake_case_governance_files_and_reference_profile()
    {
        var root = NewTempRoot();
        try
        {
            var settings = new AppSettings();

            await GovernanceArtifactStore.EnsureDefaultArtifactsAsync(settings, root);

            var expectedFiles = new[]
            {
                "model_catalog.json",
                "model_collections.json",
                "model_policy.json",
                "model_sources.json",
                "warmup_profiles.json",
                "warmup_results.json",
                "hardware_probe.json",
                "last_known_good_profile.json",
                "blacklist.json",
                "battery_policies.json",
                "capability_state.json",
                "acquisition_log.json"
            };

            foreach (var file in expectedFiles)
            {
                Assert.True(File.Exists(Path.Combine(root, file)), file);
                Assert.True(File.Exists(Path.Combine(root, file + ".sha256")), file + ".sha256");
            }

            Assert.False(File.Exists(Path.Combine(root, "model-catalog.json")));
            Assert.NotNull(settings.QualifiedProfile);
            Assert.Equal("qwen25-3b-q4km-cuda-p520-interactive", settings.QualifiedProfile!.ProfileId);

            var hardware = await GovernanceArtifactStore.ReadAsync<HardwareProbeArtifact>(
                GovernanceArtifactStore.HardwareProbeFile,
                root);
            Assert.Equal(GovernanceArtifactReadStatus.Ok, hardware.Status);
            Assert.NotEqual("not_captured", hardware.Value!.Status);
            Assert.False(string.IsNullOrWhiteSpace(hardware.Value.MachineFingerprint));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void HardwareProbeService_create_artifact_includes_observed_dxgi_budget()
    {
        var gpu = new GpuInfo(
            GpuVendor.Nvidia,
            "Quadro P520",
            4L * 1024 * 1024 * 1024,
            IsIntegrated: false,
            DetectionSource: "test");
        var dxgi = new DxgiVideoMemorySnapshot(
            BudgetBytes: 3UL * 1024 * 1024 * 1024,
            CurrentUsageBytes: 512UL * 1024 * 1024,
            AvailableForReservationBytes: 2UL * 1024 * 1024 * 1024,
            CurrentReservationBytes: 128UL * 1024 * 1024,
            Source: "test-dxgi");
        var memory = new SystemMemorySnapshot(
            TotalRamBytes: 16L * 1024 * 1024 * 1024,
            AvailableRamBytes: 8L * 1024 * 1024 * 1024,
            Source: "test");
        var power = new PowerStatusSnapshot(
            IsOnBattery: false,
            BatteryLifePercent: 88,
            Source: "test-power");

        var artifact = HardwareProbeService.CreateArtifact(
            gpu,
            dxgi,
            memory,
            "machine-a",
            processorCount: 8,
            is64BitOperatingSystem: true,
            DateTimeOffset.Parse("2026-04-23T10:00:00Z"),
            power);

        Assert.Equal("captured", artifact.Status);
        Assert.Equal("v3.1", artifact.CdcAlignment);
        Assert.False(string.IsNullOrWhiteSpace(artifact.MachineFingerprint));
        Assert.Equal("nvidia", artifact.Hardware["gpuVendor"]);
        Assert.Equal(4096, artifact.Hardware["gpuDedicatedVramMiB"]);
        Assert.Equal("captured", artifact.Hardware["dxgiStatus"]);
        Assert.Equal(3072L, artifact.Hardware["dxgiBudgetMiB"]);
        Assert.Equal(512L, artifact.Hardware["dxgiCurrentUsageMiB"]);
        Assert.Equal(16384L, artifact.Hardware["totalRamMiB"]);
        Assert.Equal(8192L, artifact.Hardware["availableRamMiB"]);
        Assert.Equal(false, artifact.Hardware["isOnBattery"]);
        Assert.Equal(88, artifact.Hardware["batteryLifePercent"]);
        Assert.Equal("test-power", artifact.Hardware["powerStatusSource"]);
    }

    [Fact]
    public void HardwareProbeService_compare_requests_requalification_on_fingerprint_change()
    {
        var stored = CreateHardwareProbe("Quadro P520", 4096, "machine-a");
        var current = CreateHardwareProbe("RTX A2000", 6144, "machine-a");

        var changed = HardwareProbeService.Compare(stored, current);
        var unchanged = HardwareProbeService.Compare(stored, stored);

        Assert.True(changed.RequiresRequalification);
        Assert.Equal("hardware_fingerprint_changed", changed.Reason);
        Assert.False(unchanged.RequiresRequalification);
        Assert.Equal("hardware_fingerprint_unchanged", unchanged.Reason);
    }

    [Fact]
    public async Task BatteryPolicyStore_recommends_fallback_when_balanced_profile_runs_on_battery()
    {
        var root = NewTempRoot();
        try
        {
            var profile = WarmupProfileStore.CreateReferenceCudaProfile();
            await GovernanceArtifactStore.EnsureDefaultArtifactsAsync(new AppSettings(), root);
            await GovernanceArtifactStore.WriteAsync(
                GovernanceArtifactStore.HardwareProbeFile,
                CreateHardwareProbe("Quadro P520", 4096, "machine-a", isOnBattery: true),
                root);

            var decision = await BatteryPolicyStore.EvaluateAsync(profile, root);

            Assert.True(decision.RequiresRequalification);
            Assert.Equal("battery_policy_recommends_fallback", decision.Reason);
            Assert.Equal(profile.FallbackProfileRef, decision.RecommendedProfileRef);
            Assert.Equal(60, decision.EffectiveIdleTimeoutSeconds);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static HardwareProbeArtifact CreateHardwareProbe(
        string gpuName,
        int vramMiB,
        string machineName,
        bool isOnBattery = false)
    {
        var gpu = new GpuInfo(
            GpuVendor.Nvidia,
            gpuName,
            (long)vramMiB * 1024 * 1024,
            IsIntegrated: false,
            DetectionSource: "test");
        var dxgi = new DxgiVideoMemorySnapshot(
            (ulong)vramMiB * 1024 * 1024,
            128UL * 1024 * 1024,
            (ulong)Math.Max(0, vramMiB - 512) * 1024 * 1024,
            0,
            "test");
        var memory = new SystemMemorySnapshot(
            16L * 1024 * 1024 * 1024,
            8L * 1024 * 1024 * 1024,
            "test");
        var power = new PowerStatusSnapshot(
            isOnBattery,
            BatteryLifePercent: 75,
            Source: "test-power");

        return HardwareProbeService.CreateArtifact(
            gpu,
            dxgi,
            memory,
            machineName,
            processorCount: 8,
            is64BitOperatingSystem: true,
            DateTimeOffset.UtcNow,
            power);
    }

    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "saaia-governance-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
