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
        Assert.Equal(4096, restored.CtxSize);
        Assert.Equal(1024, restored.BatchSize);
        Assert.Equal(256, restored.UbatchSize);
        Assert.Equal(6, restored.ThreadsBatch);
        Assert.Equal(36, restored.Ngl);
        Assert.True(restored.FlashAttn);
        Assert.False(string.IsNullOrWhiteSpace(restored.FallbackProfileRef));
    }

    [Fact]
    public void WarmupProfileStore_exposes_explicit_cpu_safe_profile()
    {
        var cpuSafe = WarmupProfileStore.FindProfile("qwen25-3b-q4km-cpu-safe");

        Assert.NotNull(cpuSafe);
        Assert.Equal("llama.cpp-cpu", cpuSafe!.Runtime);
        Assert.Equal("safe", cpuSafe.Mode);
        Assert.Equal(0, cpuSafe.Candidate.Ngl);
        Assert.False(cpuSafe.Candidate.FlashAttn);
        Assert.Equal(4096, cpuSafe.Thresholds.MinAvailableRamMiB);
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
    public async Task EnsureDefaultArtifacts_upgrades_warmup_profiles_when_default_contract_changes()
    {
        var root = NewTempRoot();
        try
        {
            var stale = WarmupProfileStore.CreateDefaultWarmupProfiles() with
            {
                Items = WarmupProfileStore.CreateDefaultWarmupProfiles().Items
                    .Select(item => item.ProfileId == "qwen25-3b-q4km-cuda-p520-interactive"
                        ? item with { Candidate = item.Candidate with { CtxSize = 3072 } }
                        : item)
                    .ToArray()
            };

            await GovernanceArtifactStore.WriteAsync(
                GovernanceArtifactStore.WarmupProfilesFile,
                stale,
                root);

            await GovernanceArtifactStore.EnsureDefaultArtifactsAsync(new AppSettings(), root);

            var upgraded = await GovernanceArtifactStore.ReadAsync<WarmupProfilesArtifact>(
                GovernanceArtifactStore.WarmupProfilesFile,
                root);
            var nominal = upgraded.Value!.Items.Single(item =>
                item.ProfileId == "qwen25-3b-q4km-cuda-p520-interactive");

            Assert.Equal(GovernanceArtifactReadStatus.Ok, upgraded.Status);
            Assert.Equal(4096, nominal.Candidate.CtxSize);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task EnsureDefaultArtifacts_creates_snake_case_governance_files_without_preseeding_qualified_profile()
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
                "runtime_compatibility_policy.json",
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
            Assert.Null(settings.QualifiedProfile);

            var runtimePolicy = await GovernanceArtifactStore.ReadAsync<RuntimeCompatibilityPolicyArtifact>(
                GovernanceArtifactStore.RuntimeCompatibilityPolicyFile,
                root);
            Assert.Equal(GovernanceArtifactReadStatus.Ok, runtimePolicy.Status);
            Assert.Contains(runtimePolicy.Value!.MinModelRules, rule =>
                rule.ModelFamily == "gemma4"
                && rule.RuntimeId == "llama.cpp-cuda"
                && rule.MinBuild == "b8901");

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
    public async Task EnsureDefaultArtifacts_upgrades_stale_model_catalog_checksum_when_reference_hash_is_now_known()
    {
        var root = NewTempRoot();
        try
        {
            var staleCatalog = ModelCatalogStore.CreateDefaultCatalog() with
            {
                Items = ModelCatalogStore.CreateDefaultCatalog().Items
                    .Select(item => item.ModelId == "qwen2.5-3b-instruct-q4-k-m"
                        ? item with
                        {
                            ChecksumSha256 = null,
                            ChecksumStatus = "pending_reference_hash"
                        }
                        : item)
                    .ToArray()
            };
            await GovernanceArtifactStore.WriteAsync(GovernanceArtifactStore.ModelCatalogFile, staleCatalog, root);

            await GovernanceArtifactStore.EnsureDefaultArtifactsAsync(new AppSettings(), root);

            var read = await GovernanceArtifactStore.ReadAsync<ModelCatalogArtifact>(
                GovernanceArtifactStore.ModelCatalogFile,
                root);
            var qwen = Assert.Single(read.Value!.Items, item => item.ModelId == "qwen2.5-3b-instruct-q4-k-m");

            Assert.Equal(GovernanceArtifactReadStatus.Ok, read.Status);
            Assert.Equal("9c9f56a391a3abbd5b89d0245bf6106081bcc3173119d4229235dd9d23253f94", qwen.ChecksumSha256);
            Assert.Equal("verified_reference_hash", qwen.ChecksumStatus);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task EnsureDefaultArtifacts_adds_new_default_server_models_and_backend_collections_without_overwriting_existing_entries()
    {
        var root = NewTempRoot();
        try
        {
            var staleCatalog = new ModelCatalogArtifact(
                GovernanceArtifactStore.ModelCatalogFile,
                "v3.1",
                "stale",
                DateTimeOffset.UtcNow,
                ModelCatalogStore.CreateDefaultCatalog().Items
                    .Where(item => !item.ModelId.StartsWith("qwen3.6-", StringComparison.OrdinalIgnoreCase))
                    .ToArray());
            var staleCollections = new ModelCollectionsArtifact(
                GovernanceArtifactStore.ModelCollectionsFile,
                "v3.1",
                ModelCatalogStore.CreateDefaultCollections().Items
                    .Where(item => !string.Equals(item.Key, "backend-qwen3.6", StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(item.Key, "backend-a3b", StringComparison.OrdinalIgnoreCase))
                    .ToArray());

            await GovernanceArtifactStore.WriteAsync(GovernanceArtifactStore.ModelCatalogFile, staleCatalog, root);
            await GovernanceArtifactStore.WriteAsync(GovernanceArtifactStore.ModelCollectionsFile, staleCollections, root);

            await GovernanceArtifactStore.EnsureDefaultArtifactsAsync(new AppSettings(), root);

            var catalogRead = await GovernanceArtifactStore.ReadAsync<ModelCatalogArtifact>(
                GovernanceArtifactStore.ModelCatalogFile,
                root);
            var collectionsRead = await GovernanceArtifactStore.ReadAsync<ModelCollectionsArtifact>(
                GovernanceArtifactStore.ModelCollectionsFile,
                root);

            Assert.Equal(GovernanceArtifactReadStatus.Ok, catalogRead.Status);
            Assert.Equal(GovernanceArtifactReadStatus.Ok, collectionsRead.Status);
            Assert.Contains(catalogRead.Value!.Items, item =>
                item.ModelId == "qwen3.6-27b-q4-k-m"
                && item.SourceRef == "local-bundle");
            Assert.Contains(catalogRead.Value.Items, item =>
                item.ModelId == "qwen3.6-35b-a3b-ud-q4-k-m"
                && item.Family == "qwen3.6-a3b");
            Assert.Contains(collectionsRead.Value!.Items, item =>
                item.Key == "backend-qwen3.6"
                && item.ModelIds.Contains("qwen3.6-27b-q4-k-m"));
            Assert.Contains(collectionsRead.Value.Items, item =>
                item.Key == "backend-a3b"
                && item.ModelIds.Contains("qwen3.6-35b-a3b-ud-iq4-xs"));
            Assert.Contains(collectionsRead.Value.Items, item => item.Key == "client-baseline");
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void Default_model_collections_reference_known_model_ids_only()
    {
        var catalogIds = ModelCatalogStore.CreateDefaultCatalog().Items
            .Select(item => item.ModelId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var collections = ModelCatalogStore.CreateDefaultCollections();

        foreach (var modelId in collections.Items.SelectMany(item => item.ModelIds))
            Assert.Contains(modelId, catalogIds);
    }

    [Fact]
    public void Default_model_sources_cover_all_catalog_source_refs()
    {
        var sourceKeys = ModelCatalogStore.CreateDefaultSources().Items
            .Select(item => item.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var catalog = ModelCatalogStore.CreateDefaultCatalog();

        foreach (var item in catalog.Items)
            Assert.Contains(item.SourceRef, sourceKeys);
    }

    [Fact]
    public void Default_model_policy_matches_v31_client_constraints()
    {
        var policy = ModelCatalogStore.CreateDefaultPolicy();

        Assert.False(policy.AllowDiscovery);
        Assert.True(policy.RequireChecksum);
        Assert.Equal(1, policy.MaxActiveModelsClient);
        Assert.Equal(GovernanceArtifactStore.BlacklistFile, policy.BlacklistRef);
        Assert.Contains("one_active_client_model", policy.Rules);
        Assert.Contains("checksum_required_before_qualification", policy.Rules);
        Assert.Contains("warmup_required_before_selection", policy.Rules);
    }

    [Fact]
    public async Task Effective_model_policy_reads_governance_override_when_checksum_is_valid()
    {
        var root = NewTempRoot();
        try
        {
            var custom = new ModelPolicyArtifact(
                GovernanceArtifactStore.ModelPolicyFile,
                "v3.1",
                AllowDiscovery: true,
                RequireChecksum: false,
                MaxActiveModelsClient: 2,
                BlacklistRef: "custom_blacklist.json",
                Rules: new[] { "custom_rule" });

            await GovernanceArtifactStore.WriteAsync(GovernanceArtifactStore.ModelPolicyFile, custom, root);

            var effective = ModelCatalogStore.GetEffectivePolicy(root);

            Assert.True(effective.AllowDiscovery);
            Assert.False(effective.RequireChecksum);
            Assert.Equal(2, effective.MaxActiveModelsClient);
            Assert.Equal("custom_blacklist.json", effective.BlacklistRef);
            Assert.Contains("custom_rule", effective.Rules);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task Effective_model_sources_override_is_used_for_download_url_resolution()
    {
        var root = NewTempRoot();
        try
        {
            var customSources = new ModelSourcesArtifact(
                GovernanceArtifactStore.ModelSourcesFile,
                "v3.1",
                new[]
                {
                    new ModelSourceItem(
                        Key: "hf-bartowski-qwen25-3b",
                        Kind: "huggingface",
                        Uri: "https://huggingface.co/acme/Qwen2.5-3B-Instruct-GGUF",
                        RequiresChecksum: true,
                        AllowedInAirGap: false)
                });

            await GovernanceArtifactStore.WriteAsync(GovernanceArtifactStore.ModelSourcesFile, customSources, root);

            var model = Assert.Single(
                ModelCatalogStore.GetEffectiveCatalog(root).Items,
                item => item.ModelId == "qwen2.5-3b-instruct-q4-k-m");
            var url = ModelCatalogStore.TryBuildDownloadUrl(model, root);

            Assert.Equal(
                "https://huggingface.co/acme/Qwen2.5-3B-Instruct-GGUF/resolve/main/Qwen2.5-3B-Instruct-Q4_K_M.gguf",
                url);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task Effective_installer_visible_models_follow_governance_collections_override()
    {
        var root = NewTempRoot();
        try
        {
            var customCollections = new ModelCollectionsArtifact(
                GovernanceArtifactStore.ModelCollectionsFile,
                "v3.1",
                new[]
                {
                    new ModelCollectionItem(
                        Key: "client-baseline",
                        Scope: "client",
                        VisibleInInstaller: true,
                        ModelIds: new[] { "gemma-4-e2b-it-q4-k-m" })
                });

            await GovernanceArtifactStore.WriteAsync(GovernanceArtifactStore.ModelCollectionsFile, customCollections, root);

            var visible = ModelCatalogStore.GetInstallerVisibleClientModels(root);

            Assert.Single(visible);
            Assert.Equal("gemma-4-e2b-it-q4-k-m", visible[0].ModelId);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void Effective_model_policy_blocks_non_catalog_client_models_when_discovery_is_disabled()
    {
        Assert.False(ModelCatalogStore.IsDiscoveryAllowed());
        Assert.Equal(1, ModelCatalogStore.GetMaxActiveClientModels());
        Assert.Null(ModelCatalogStore.GetClientCatalogPolicyViolation("Qwen2.5-3B-Instruct-Q4_K_M.gguf"));
        Assert.NotNull(ModelCatalogStore.GetClientCatalogPolicyViolation("custom-experimental-model.gguf"));
    }

    [Fact]
    public async Task Effective_model_policy_override_can_allow_non_catalog_client_models()
    {
        var root = NewTempRoot();
        try
        {
            var custom = new ModelPolicyArtifact(
                GovernanceArtifactStore.ModelPolicyFile,
                "v3.1",
                AllowDiscovery: true,
                RequireChecksum: true,
                MaxActiveModelsClient: 2,
                BlacklistRef: GovernanceArtifactStore.BlacklistFile,
                Rules: new[] { "custom_allow_discovery" });

            await GovernanceArtifactStore.WriteAsync(GovernanceArtifactStore.ModelPolicyFile, custom, root);

            Assert.True(ModelCatalogStore.IsDiscoveryAllowed(root));
            Assert.Equal(2, ModelCatalogStore.GetMaxActiveClientModels(root));
            Assert.Null(ModelCatalogStore.GetClientCatalogPolicyViolation("custom-experimental-model.gguf", root));
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
            "573.71",
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
        Assert.Equal("573.71", artifact.Hardware["gpuDriverVersion"]);
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
    public void HardwareProbeService_create_artifact_includes_vendor_gpu_telemetry()
    {
        var gpu = new GpuInfo(
            GpuVendor.Amd,
            "Radeon RX 7800 XT",
            16L * 1024 * 1024 * 1024,
            IsIntegrated: false,
            DetectionSource: "test");
        var memory = new SystemMemorySnapshot(
            TotalRamBytes: 32L * 1024 * 1024 * 1024,
            AvailableRamBytes: 16L * 1024 * 1024 * 1024,
            Source: "test");
        var telemetry = new VendorGpuTelemetrySnapshot(
            Source: "amd-smi",
            VramUsedMiB: 2048,
            VramTotalMiB: 16384,
            TemperatureC: 62.5,
            CoreClockMHz: 2250,
            UtilizationPercent: 41);

        var artifact = HardwareProbeService.CreateArtifact(
            gpu,
            gpuDriverVersion: null,
            dxgi: null,
            memory,
            "machine-a",
            processorCount: 12,
            is64BitOperatingSystem: true,
            DateTimeOffset.Parse("2026-04-23T10:00:00Z"),
            vendorTelemetry: telemetry);

        Assert.Equal("captured", artifact.Hardware["vendorTelemetryStatus"]);
        Assert.Equal("amd-smi", artifact.Hardware["vendorTelemetrySource"]);
        Assert.Equal(2048L, artifact.Hardware["gpuVramUsedMiB"]);
        Assert.Equal(16384L, artifact.Hardware["gpuVramTotalMiB"]);
        Assert.Equal(62.5, artifact.Hardware["gpuTemperatureC"]);
        Assert.Equal(2250d, artifact.Hardware["gpuCoreClockMHz"]);
        Assert.Equal(41d, artifact.Hardware["gpuUtilizationPercent"]);
    }

    [Fact]
    public void HardwareProbeService_parses_vendor_gpu_telemetry_json()
    {
        const string json = """
        {
          "card0": {
            "vram_used_mib": 512,
            "vram_total_mib": 4096,
            "temperature_c": 63,
            "gfx_clock_mhz": 1225,
            "gpu_util": 47
          }
        }
        """;

        var telemetry = HardwareProbeService.TryParseVendorTelemetryJson("amd-smi", json);

        Assert.NotNull(telemetry);
        Assert.Equal("amd-smi", telemetry!.Source);
        Assert.Equal(512, telemetry.VramUsedMiB);
        Assert.Equal(4096, telemetry.VramTotalMiB);
        Assert.Equal(63, telemetry.TemperatureC);
        Assert.Equal(1225, telemetry.CoreClockMHz);
        Assert.Equal(47, telemetry.UtilizationPercent);
    }

    [Fact]
    public void HardwareProbeService_parses_intel_level_zero_style_metrics()
    {
        const string json = """
        {
          "device_level": [
            {
              "memory_used_mib": "768",
              "memory_total_mib": "8192",
              "temperature": "54",
              "frequency_mhz": "1450",
              "utilization_percent": "36"
            }
          ]
        }
        """;

        var telemetry = HardwareProbeService.TryParseVendorTelemetryJson("intel-level-zero:xpu-smi", json);

        Assert.NotNull(telemetry);
        Assert.Equal("intel-level-zero:xpu-smi", telemetry!.Source);
        Assert.Equal(768, telemetry.VramUsedMiB);
        Assert.Equal(8192, telemetry.VramTotalMiB);
        Assert.Equal(54, telemetry.TemperatureC);
        Assert.Equal(1450, telemetry.CoreClockMHz);
        Assert.Equal(36, telemetry.UtilizationPercent);
    }

    [Fact]
    public void HardwareProbeService_create_artifact_marks_external_gpu_from_connection_hint()
    {
        var gpu = new GpuInfo(
            GpuVendor.Nvidia,
            "RTX 3080 eGPU",
            10L * 1024 * 1024 * 1024,
            IsIntegrated: false,
            DetectionSource: "test")
        {
            PnpDeviceId = @"USB\VID_1234&PID_5678"
        };
        var memory = new SystemMemorySnapshot(
            TotalRamBytes: 16L * 1024 * 1024 * 1024,
            AvailableRamBytes: 8L * 1024 * 1024 * 1024,
            Source: "test");

        var artifact = HardwareProbeService.CreateArtifact(
            gpu,
            "573.71",
            dxgi: null,
            memory,
            "machine-a",
            processorCount: 8,
            is64BitOperatingSystem: true,
            DateTimeOffset.Parse("2026-04-23T10:00:00Z"));

        Assert.Equal(true, artifact.Hardware["gpuIsExternal"]);
        Assert.Equal("external", artifact.Hardware["gpuConnectionHint"]);
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
    public void HardwareProbeService_compare_requests_requalification_on_driver_change()
    {
        var stored = CreateHardwareProbe("Quadro P520", 4096, "machine-a", driverVersion: "573.71");
        var current = CreateHardwareProbe("Quadro P520", 4096, "machine-a", driverVersion: "574.01");

        var changed = HardwareProbeService.Compare(stored, current);

        Assert.True(changed.RequiresRequalification);
        Assert.Contains("gpu_driver_changed", changed.Reason);
    }

    [Fact]
    public void HardwareProbeService_compare_requests_requalification_when_external_gpu_disconnected()
    {
        var stored = CreateHardwareProbe(
            "RTX 3080 eGPU",
            10240,
            "machine-a",
            pnpDeviceId: @"USB\VID_1234&PID_5678");
        var current = CreateHardwareProbe(
            "Quadro P520",
            4096,
            "machine-a",
            pnpDeviceId: @"PCI\VEN_10DE&DEV_1C30");

        var changed = HardwareProbeService.Compare(stored, current);

        Assert.True(changed.RequiresRequalification);
        Assert.Equal("external_gpu_disconnected", changed.Reason);
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

    [Fact]
    public void ModelCatalogStore_resolves_canonical_model_id_from_file_name()
    {
        var canonical = ModelCatalogStore.ResolveCanonicalModelId("Qwen2.5-3B-Instruct-Q4_K_M.gguf");

        Assert.Equal("qwen2.5-3b-instruct-q4-k-m", canonical);
    }

    [Fact]
    public void ModelCatalogStore_returns_reference_checksum_for_qwen_q4km()
    {
        var checksum = ModelCatalogStore.TryGetReferenceChecksum("Qwen2.5-3B-Instruct-Q4_K_M.gguf");
        var catalog = ModelCatalogStore.CreateDefaultCatalog();
        var qwen = Assert.Single(catalog.Items, item => item.ModelId == "qwen2.5-3b-instruct-q4-k-m");

        Assert.Equal("9c9f56a391a3abbd5b89d0245bf6106081bcc3173119d4229235dd9d23253f94", checksum);
        Assert.Equal(checksum, qwen.ChecksumSha256);
        Assert.Equal("verified_reference_hash", qwen.ChecksumStatus);
    }

    [Fact]
    public void ClientDefaults_default_model_is_governed_and_visible_in_installer()
    {
        var item = ModelCatalogStore.TryGetItem(ClientDefaults.LlmModel);
        var installerVisible = ModelCatalogStore.GetInstallerVisibleClientModels();

        Assert.NotNull(item);
        Assert.Contains(installerVisible, candidate => string.Equals(candidate.ModelId, item!.ModelId, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ModelCatalogStore_includes_verified_hashes_for_local_model_pack()
    {
        var catalog = ModelCatalogStore.CreateDefaultCatalog();

        Assert.Contains(catalog.Items, item =>
            item.ModelId == "qwen2.5-3b-instruct-q6-k-l"
            && item.ChecksumSha256 == "930d792ba9cebbb98faaef6755c62b47cb24bb2d16fb10a338ac80d721b81796"
            && item.ChecksumStatus == "verified_reference_hash");
        Assert.Contains(catalog.Items, item =>
            item.ModelId == "qwen2.5-3b-instruct-q8-0"
            && item.ChecksumSha256 == "12491ec9f03aab7f0b96cdb7742695e6583d17ee129de48332d04b9cf6acd960"
            && item.ChecksumStatus == "verified_reference_hash");
        Assert.Contains(catalog.Items, item =>
            item.ModelId == "mistral-7b-instruct-v0.3-iq3-m"
            && item.ChecksumSha256 == "4ea14c5a6c787ac2703505f04a4ee746f746d1ace3ffd907af28f6f179e6b224"
            && item.License.LicenseFamily == "apache-2.0");
        Assert.Contains(catalog.Items, item =>
            item.ModelId == "mistral-7b-instruct-v0.3-q4-k-m"
            && item.ChecksumSha256 == "56d2db1ee4e4330338433c3a2d1f98f3d647db9cef785fd6e640061e1c98dde2"
            && item.SourceRef == "hf-bartowski-mistral-7b-v03");
    }

    [Fact]
    public void ModelCatalogStore_includes_gemma4_as_apache_test_family_without_bypassing_checksum_policy()
    {
        var catalog = ModelCatalogStore.CreateDefaultCatalog();
        var sources = ModelCatalogStore.CreateDefaultSources();
        var collections = ModelCatalogStore.CreateDefaultCollections();

        Assert.Contains(catalog.Items, item =>
            item.ModelId == "gemma-4-e2b-it-q4-k-m"
            && item.License.LicenseFamily == "apache-2.0"
            && item.Gguf.Architecture == "gemma4"
            && item.ChecksumSha256 == "ac0069ebccd39925d836f24a88c0f0c858d20578c29b21ab7cedce66ee576845"
            && item.ChecksumStatus == "verified_reference_hash");
        Assert.Contains(catalog.Items, item =>
            item.ModelId == "gemma-4-e2b-it-q8-0"
            && item.License.LicenseFamily == "apache-2.0"
            && item.Gguf.Architecture == "gemma4"
            && item.ChecksumSha256 == "6db0088e7e2b6459dfb29fa59b0b1d7299d249ef28debc464d4d564caf444511"
            && item.ChecksumStatus == "verified_reference_hash");
        Assert.Contains(catalog.Items, item =>
            item.ModelId == "gemma-4-e4b-it-q4-k-m"
            && item.License.LicenseFamily == "apache-2.0"
            && item.Gguf.BlockCount == 42
            && item.ChecksumSha256 == "dff0ffba4c90b4082d70214d53ce9504a28d4d8d998276dcb3b8881a656c742a"
            && item.ChecksumStatus == "verified_reference_hash");
        Assert.Contains(sources.Items, item =>
            item.Key == "hf-unsloth-gemma4-e2b"
            && item.RequiresChecksum);
        Assert.Contains(collections.Items, item =>
            item.Key == "apache-test-family"
            && item.ModelIds.Contains("gemma-4-e2b-it-q4-k-m")
            && item.ModelIds.Contains("gemma-4-e4b-it-q4-k-m"));
    }

    [Fact]
    public void ModelCatalogStore_includes_qwen36_server_models_in_backend_only_collections()
    {
        var catalog = ModelCatalogStore.CreateDefaultCatalog();
        var collections = ModelCatalogStore.CreateDefaultCollections();
        var installerVisible = ModelCatalogStore.GetInstallerVisibleClientModels();

        Assert.Contains(catalog.Items, item =>
            item.ModelId == "qwen3.6-27b-q4-k-m"
            && item.SourceRef == "local-bundle"
            && item.License.LicenseFamily == "qwen"
            && item.SupportedScopes.Contains("backend")
            && !item.SupportedScopes.Contains("client")
            && item.ChecksumSha256 == "5ed60d0af4650a854b1755bd392f9aef4872643dc25a254bc68043fa638392a0");
        Assert.Contains(catalog.Items, item =>
            item.ModelId == "qwen3.6-35b-a3b-ud-q4-k-m"
            && item.SourceRef == "local-bundle"
            && item.Family == "qwen3.6-a3b"
            && item.Gguf.Architecture == "qwen3"
            && item.SupportTier == "backend-a3b"
            && item.ChecksumSha256 == "ac0e2c1189e055faa36eff361580e79c5bd6f8e76bffb4ce547f167d53e31a61");
        Assert.Contains(catalog.Items, item =>
            item.ModelId == "qwen3.6-35b-a3b-ud-iq4-xs"
            && item.SourceRef == "local-bundle"
            && item.ChecksumSha256 == "649d7508507b84638732c4f52c24c8b15843c6dca2f3ff793ae07c14a67ebbb3");
        Assert.Contains(collections.Items, item =>
            item.Key == "backend-qwen3.6"
            && !item.VisibleInInstaller
            && item.ModelIds.Contains("qwen3.6-27b-q4-k-m")
            && item.ModelIds.Contains("qwen3.6-35b-a3b-ud-q5-k-m"));
        Assert.Contains(collections.Items, item =>
            item.Key == "backend-a3b"
            && !item.VisibleInInstaller
            && item.ModelIds.Contains("qwen3.6-35b-a3b-ud-q3-k-s")
            && item.ModelIds.Contains("qwen3.6-35b-a3b-ud-iq4-xs"));
        Assert.DoesNotContain(installerVisible, item =>
            item.ModelId.StartsWith("qwen3.6-", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RuntimeCompatibilityPolicy_requires_b8901_for_gemma4_and_forces_flash_attn_off_on_pascal()
    {
        var policy = RuntimeCompatibilityPolicyStore.CreateDefaultPolicy();
        var gemma = ModelCatalogStore.TryGetItem("gemma-4-e2b-it-q4-k-m");
        var gpu = new GpuInfo(
            GpuVendor.Nvidia,
            "Quadro P520",
            4L * 1024 * 1024 * 1024,
            IsIntegrated: false,
            DetectionSource: "test");

        var oldRuntime = RuntimeCompatibilityPolicyStore.Evaluate(
            "llama.cpp-cuda",
            "b8149",
            gemma,
            policy);
        var newRuntime = RuntimeCompatibilityPolicyStore.Evaluate(
            "llama.cpp-cuda",
            "b8901",
            gemma,
            policy);
        var forcedFlashAttn = RuntimeCompatibilityPolicyStore.GetForcedFlashAttn(
            "llama.cpp-cuda",
            gemma,
            gpu,
            policy);

        Assert.NotNull(gemma);
        Assert.False(oldRuntime.Compatible);
        Assert.True(oldRuntime.RequiresUpgrade);
        Assert.Equal("b8901", oldRuntime.RequiredBuild);
        Assert.True(newRuntime.Compatible);
        Assert.False(forcedFlashAttn);
    }

    [Fact]
    public void RequalificationTriggerService_requires_requalification_when_runtime_or_model_drift()
    {
        var settings = new AppSettings
        {
            QualifiedProfile = WarmupProfileStore.CreateReferenceCudaProfile(),
            LlamaExePath = @"C:\llm\llama-server-vulkan.exe",
            ModelId = "Qwen2.5-3B-Instruct-Q4_K_M.gguf"
        };

        var runtimeDrift = RequalificationTriggerService.EvaluateProfileDrift(settings);

        settings.LlamaExePath = @"C:\llm\llama-server-cuda.exe";
        settings.ModelId = "mistral-unknown.gguf";
        var modelDrift = RequalificationTriggerService.EvaluateProfileDrift(settings);

        Assert.True(runtimeDrift.Required);
        Assert.Contains("runtime_changed", runtimeDrift.Reason);
        Assert.True(modelDrift.Required);
        Assert.Contains("model_changed", modelDrift.Reason);
    }

    [Fact]
    public void RequalificationTriggerService_detects_perf_drift_repeated_failures_timeout_and_admin_action()
    {
        var profileId = "qwen2.5-3b-q4km-cuda-balanced";
        var baseline = WarmupItem(profileId, WarmupGateStatus.Pass, tokPerSec: 10, ttftMs: 4000, minutesAgo: 20);
        var drift = WarmupItem(profileId, WarmupGateStatus.Pass, tokPerSec: 5.5, ttftMs: 4100, minutesAgo: 1);
        var failures = new[]
        {
            WarmupItem(profileId, WarmupGateStatus.FailBlock, tokPerSec: 0, ttftMs: 0, minutesAgo: 1, "warmup_run_failed"),
            WarmupItem(profileId, WarmupGateStatus.FailFallback, tokPerSec: 0, ttftMs: 0, minutesAgo: 2, "warmup_run_failed"),
            WarmupItem(profileId, WarmupGateStatus.FailBlock, tokPerSec: 0, ttftMs: 0, minutesAgo: 3, "warmup_run_failed")
        };
        var timeout = WarmupItem(profileId, WarmupGateStatus.PassDegraded, tokPerSec: 2, ttftMs: 130000, minutesAgo: 1);

        var perfDrift = RequalificationTriggerService.EvaluateWarmupHistory(new[] { drift, baseline }, profileId);
        var repeatedFailures = RequalificationTriggerService.EvaluateWarmupHistory(failures, profileId);
        var timeoutDecision = RequalificationTriggerService.EvaluateWarmupHistory(new[] { timeout, baseline }, profileId);
        var adminAction = RequalificationTriggerService.EvaluateAdminAction(requested: true, requestedBy: "ops");

        Assert.True(perfDrift.Required);
        Assert.Contains("perf_drift_tok_per_sec", perfDrift.Reason);
        Assert.True(repeatedFailures.Required);
        Assert.Contains("repeated_failures", repeatedFailures.Reason);
        Assert.True(timeoutDecision.Required);
        Assert.Equal("timeout_threshold_exceeded", timeoutDecision.Reason);
        Assert.True(adminAction.Required);
        Assert.Equal("admin_action:ops", adminAction.Reason);
    }

    private static HardwareProbeArtifact CreateHardwareProbe(
        string gpuName,
        int vramMiB,
        string machineName,
        bool isOnBattery = false,
        string? driverVersion = "573.71",
        string? pnpDeviceId = null)
    {
        var gpu = new GpuInfo(
            GpuVendor.Nvidia,
            gpuName,
            (long)vramMiB * 1024 * 1024,
            IsIntegrated: false,
            DetectionSource: "test")
        {
            PnpDeviceId = pnpDeviceId
        };
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
            driverVersion,
            dxgi,
            memory,
            machineName,
            processorCount: 8,
            is64BitOperatingSystem: true,
            DateTimeOffset.UtcNow,
            power);
    }

    private static WarmupResultItem WarmupItem(
        string profileId,
        WarmupGateStatus status,
        double tokPerSec,
        int ttftMs,
        int minutesAgo,
        params string[] reasons)
        => new(
            DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
            profileId,
            "llama.cpp-cuda",
            "qwen2.5-3b-instruct-q4-k-m",
            status,
            status == WarmupGateStatus.Pass ? 3 : 0,
            3,
            LastLoadMs: 1000,
            LastTtftMs: ttftMs,
            LastTokPerSec: tokPerSec,
            LastMsPerToken: tokPerSec > 0 ? 1000d / tokPerSec : null,
            RuntimeMetrics: null,
            Reasons: reasons,
            HardwareFingerprint: "machine-a",
            Trigger: "test");

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
