using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class GovernanceArtifactStoreTests
{
    [Fact]
    public void QualifiedProfile_roundtrips_with_required_cdc_v31_fields()
    {
        var profile = WarmupProfileStore.CreateQwen3Q5Cuda4GbProfile();

        var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var restored = JsonSerializer.Deserialize<QualifiedProfile>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(restored);
        Assert.Equal("llama.cpp-cuda", restored!.Runtime);
        Assert.Equal("qwen3-4b-instruct-2507-q5-k-m", restored.ModelId);
        Assert.Equal(4096, restored.CtxSize);
        Assert.Equal(512, restored.BatchSize);
        Assert.Equal(128, restored.UbatchSize);
        Assert.Equal(4, restored.ThreadsBatch);
        Assert.Equal(37, restored.Ngl);
        Assert.True(restored.FlashAttn);
        Assert.Equal(new[] { "CUDA0" }, restored.DeviceIds);
        Assert.Equal("none", restored.SplitMode);
        Assert.Equal("f16", restored.CacheTypeK);
        Assert.Equal("f16", restored.CacheTypeV);
        Assert.Equal(1, restored.Parallel);
        Assert.False(string.IsNullOrWhiteSpace(restored.FallbackProfileRef));
    }

    [Fact]
    public void AppSettings_normalizes_drifted_qualified_profile_to_current_reference()
    {
        var drifted = WarmupProfileStore.CreateQwen3Q5Cuda4GbProfile() with
        {
            CtxSize = 8192,
            BatchSize = 512
        };

        var normalized = AppSettings.NormalizeQualifiedProfileForCurrentReference(drifted);

        Assert.NotNull(normalized);
        Assert.Equal(
            JsonSerializer.Serialize(
                WarmupProfileStore.CreateQwen3Q5Cuda4GbProfile(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            JsonSerializer.Serialize(
                normalized,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public void AppSettings_preserves_unknown_auto_qualified_profile()
    {
        var autoProfile = WarmupProfileStore.CreateQwen3Q5Cuda4GbProfile() with
        {
            ProfileId = "auto-qwen3-4b-2507-q5km-cuda-rtx-workstation",
            CtxSize = 8192,
            BatchSize = 2048
        };

        var normalized = AppSettings.NormalizeQualifiedProfileForCurrentReference(autoProfile);

        Assert.Equal(autoProfile, normalized);
    }

    [Fact]
    public void Requalification_detects_runtime_device_and_kv_cache_profile_drift()
    {
        var reference = WarmupProfileStore.CreateQwen3Q5Cuda4GbProfile();
        var changed = reference with
        {
            DeviceIds = new[] { "CUDA1" },
            CacheTypeK = "q8_0"
        };

        Assert.True(RequalificationTriggerService.HasProfileConfigurationDrift(changed, reference));
    }

    [Fact]
    public void WarmupProfileStore_exposes_explicit_cpu_safe_profile()
    {
        var cpuSafe = WarmupProfileStore.FindProfile("qwen3-4b-2507-q5km-cpu-safe");

        Assert.NotNull(cpuSafe);
        Assert.Equal("llama.cpp-cpu", cpuSafe!.Runtime);
        Assert.Equal("safe", cpuSafe.Mode);
        Assert.Equal(0, cpuSafe.Candidate.Ngl);
        Assert.False(cpuSafe.Candidate.FlashAttn);
        Assert.Equal(6144, cpuSafe.Thresholds.MinAvailableRamMiB);
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
                item.ModelId == "qwen3-4b-instruct-2507-q5-k-m"
                && item.License.LicenseFamily == "apache-2.0"
                && item.License.CommercialUseThresholdMau is null
                && item.Gguf.HeadCountKv == 8);
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
                    .Select(item => item.ProfileId == "qwen3-4b-2507-q5km-cuda-4gb-quality"
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
                item.ProfileId == "qwen3-4b-2507-q5km-cuda-4gb-quality");

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
                rule.ModelFamily == "qwen3"
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
                    .Select(item => item.ModelId == "qwen3-4b-instruct-2507-q5-k-m"
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
            var qwen = Assert.Single(read.Value!.Items, item => item.ModelId == "qwen3-4b-instruct-2507-q5-k-m");

            Assert.Equal(GovernanceArtifactReadStatus.Ok, read.Status);
            Assert.Equal("66713ce35a58a82fe87642d4ec13425bf9b9a46800fff5c49a665ef5701439dc", qwen.ChecksumSha256);
            Assert.Equal("verified_reference_hash", qwen.ChecksumStatus);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task EnsureDefaultArtifacts_restores_qwen3_fallback_and_backend_collection_without_overwriting_existing_entries()
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
                    .Where(item => item.ModelId != "qwen3-4b-instruct-2507-q4-k-m")
                    .Select(item => item.ModelId == "qwen3-4b-instruct-2507-q5-k-m"
                        ? item with { SupportedScopes = new[] { "client", "capability_b_backoffice" } }
                        : item)
                    .ToArray());
            var staleCollections = new ModelCollectionsArtifact(
                GovernanceArtifactStore.ModelCollectionsFile,
                "v3.1",
                ModelCatalogStore.CreateDefaultCollections().Items
                    .Where(item => !string.Equals(
                        item.Key,
                        "backend-recommended-qwen3-4b-2507",
                        StringComparison.OrdinalIgnoreCase))
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
                item.ModelId == "qwen3-4b-instruct-2507-q4-k-m"
                && item.SourceRef == "hf-bartowski-qwen3-4b-2507");
            Assert.Contains(catalogRead.Value.Items, item =>
                item.ModelId == "qwen3-4b-instruct-2507-q5-k-m"
                && item.SupportedScopes.Contains("backend"));
            Assert.Contains(collectionsRead.Value!.Items, item =>
                item.Key == "backend-recommended-qwen3-4b-2507"
                && item.ModelIds.Contains("qwen3-4b-instruct-2507-q5-k-m")
                && item.ModelIds.Contains("qwen3-4b-instruct-2507-q4-k-m"));
            Assert.Contains(collectionsRead.Value.Items, item =>
                item.Key == "client-recommended-qwen3-4b-2507");
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task EnsureDefaultArtifacts_adds_qwen3_source_collection_profiles_and_runtime_rules_to_existing_installation()
    {
        var root = NewTempRoot();
        try
        {
            var defaults = ModelCatalogStore.CreateDefaultCatalog();
            var staleCatalog = defaults with
            {
                Items = defaults.Items
                    .Where(item => !item.ModelId.StartsWith("qwen3-4b-instruct-2507-", StringComparison.OrdinalIgnoreCase))
                    .ToArray()
            };
            var defaultCollections = ModelCatalogStore.CreateDefaultCollections();
            var staleCollections = defaultCollections with
            {
                Items = defaultCollections.Items
                    .Where(item => item.Key != "client-recommended-qwen3-4b-2507")
                    .ToArray()
            };
            var defaultSources = ModelCatalogStore.CreateDefaultSources();
            var staleSources = defaultSources with
            {
                Items = defaultSources.Items
                    .Where(item => item.Key != "hf-bartowski-qwen3-4b-2507")
                    .ToArray()
            };
            var defaultRuntimePolicy = RuntimeCompatibilityPolicyStore.CreateDefaultPolicy();
            var staleRuntimePolicy = defaultRuntimePolicy with
            {
                MinModelRules = defaultRuntimePolicy.MinModelRules
                    .Where(rule => rule.ModelFamily != "qwen3")
                    .ToArray()
            };

            await GovernanceArtifactStore.WriteAsync(GovernanceArtifactStore.ModelCatalogFile, staleCatalog, root);
            await GovernanceArtifactStore.WriteAsync(GovernanceArtifactStore.ModelCollectionsFile, staleCollections, root);
            await GovernanceArtifactStore.WriteAsync(GovernanceArtifactStore.ModelSourcesFile, staleSources, root);
            await GovernanceArtifactStore.WriteAsync(
                GovernanceArtifactStore.RuntimeCompatibilityPolicyFile,
                staleRuntimePolicy,
                root);

            await GovernanceArtifactStore.EnsureDefaultArtifactsAsync(new AppSettings(), root);

            var catalog = await GovernanceArtifactStore.ReadAsync<ModelCatalogArtifact>(
                GovernanceArtifactStore.ModelCatalogFile,
                root);
            var collections = await GovernanceArtifactStore.ReadAsync<ModelCollectionsArtifact>(
                GovernanceArtifactStore.ModelCollectionsFile,
                root);
            var sources = await GovernanceArtifactStore.ReadAsync<ModelSourcesArtifact>(
                GovernanceArtifactStore.ModelSourcesFile,
                root);
            var runtimePolicy = await GovernanceArtifactStore.ReadAsync<RuntimeCompatibilityPolicyArtifact>(
                GovernanceArtifactStore.RuntimeCompatibilityPolicyFile,
                root);
            var warmups = await GovernanceArtifactStore.ReadAsync<WarmupProfilesArtifact>(
                GovernanceArtifactStore.WarmupProfilesFile,
                root);

            Assert.Contains(catalog.Value!.Items, item => item.ModelId == "qwen3-4b-instruct-2507-q5-k-m");
            Assert.Contains(collections.Value!.Items, item => item.Key == "client-recommended-qwen3-4b-2507");
            Assert.Contains(sources.Value!.Items, item =>
                item.Key == "hf-bartowski-qwen3-4b-2507"
                && item.Revision == "ae44f08e1392f39c0e474af10c3ff8355c8b6688");
            Assert.Equal(
                5,
                runtimePolicy.Value!.MinModelRules.Count(rule => rule.ModelFamily == "qwen3"));
            Assert.Contains(
                warmups.Value!.Items,
                item => item.ProfileId == "qwen3-4b-2507-q5km-cuda-4gb-quality");
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
                        Key: "hf-bartowski-qwen3-4b-2507",
                        Kind: "huggingface",
                        Uri: "https://huggingface.co/acme/Qwen_Qwen3-4B-Instruct-2507-GGUF",
                        RequiresChecksum: true,
                        AllowedInAirGap: false)
                });

            await GovernanceArtifactStore.WriteAsync(GovernanceArtifactStore.ModelSourcesFile, customSources, root);

            var model = Assert.Single(
                ModelCatalogStore.GetEffectiveCatalog(root).Items,
                item => item.ModelId == "qwen3-4b-instruct-2507-q5-k-m");
            var url = ModelCatalogStore.TryBuildDownloadUrl(model, root);

            Assert.Equal(
                "https://huggingface.co/acme/Qwen_Qwen3-4B-Instruct-2507-GGUF/resolve/main/Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf",
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
                        Key: "client-recommended-qwen3-4b-2507",
                        Scope: "client",
                        VisibleInInstaller: true,
                        ModelIds: new[] { "qwen3-4b-instruct-2507-q4-k-m" })
                });

            await GovernanceArtifactStore.WriteAsync(GovernanceArtifactStore.ModelCollectionsFile, customCollections, root);

            var visible = ModelCatalogStore.GetInstallerVisibleClientModels(root);

            Assert.Single(visible);
            Assert.Equal("qwen3-4b-instruct-2507-q4-k-m", visible[0].ModelId);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void Effective_model_policy_blocks_non_catalog_client_models_when_discovery_is_disabled()
    {
        var root = NewTempRoot();
        try
        {
            Assert.False(ModelCatalogStore.IsDiscoveryAllowed(root));
            Assert.Equal(1, ModelCatalogStore.GetMaxActiveClientModels(root));
            Assert.Null(ModelCatalogStore.GetClientCatalogPolicyViolation(
                "Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf",
                root));
            Assert.NotNull(ModelCatalogStore.GetClientCatalogPolicyViolation(
                "custom-experimental-model.gguf",
                root));
        }
        finally
        {
            DeleteTempRoot(root);
        }
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
    public void HardwareProbeService_preserves_every_adapter_and_fingerprints_secondary_gpu_changes()
    {
        var nvidia = new GpuInfo(
            GpuVendor.Nvidia,
            "Quadro P520",
            4L * 1024 * 1024 * 1024,
            IsIntegrated: false,
            DetectionSource: "nvidia-smi+cim")
        {
            PnpDeviceId = @"PCI\VEN_10DE&DEV_1D33",
            StableDeviceId = "GPU-P520",
            RuntimeDeviceHint = "CUDA0",
            DriverVersion = "573.71"
        };
        var intel = new GpuInfo(
            GpuVendor.Intel,
            "Intel(R) UHD Graphics",
            DedicatedVramBytes: 0,
            IsIntegrated: true,
            DetectionSource: "cim")
        {
            PnpDeviceId = @"PCI\VEN_8086&DEV_9B41",
            StableDeviceId = @"PCI\VEN_8086&DEV_9B41",
            DriverVersion = "31.0.101.2140"
        };
        var memory = new SystemMemorySnapshot(
            TotalRamBytes: 32L * 1024 * 1024 * 1024,
            AvailableRamBytes: 16L * 1024 * 1024 * 1024,
            Source: "test");

        var primary = GpuDetector.SelectLegacyPrimaryGpu(new[] { intel, nvidia });
        Assert.Same(nvidia, primary);

        var multiGpu = HardwareProbeService.CreateArtifact(
            primary,
            gpuDriverVersion: "573.71",
            dxgi: null,
            memory,
            "machine-multi",
            processorCount: 8,
            is64BitOperatingSystem: true,
            DateTimeOffset.Parse("2026-07-23T21:00:00Z"),
            gpus: new[] { intel, nvidia });
        var singleGpu = HardwareProbeService.CreateArtifact(
            nvidia,
            gpuDriverVersion: "573.71",
            dxgi: null,
            memory,
            "machine-multi",
            processorCount: 8,
            is64BitOperatingSystem: true,
            DateTimeOffset.Parse("2026-07-23T21:00:00Z"),
            gpus: new[] { nvidia });

        Assert.Equal(2, multiGpu.Hardware["gpuCount"]);
        var adapters = Assert.IsType<Dictionary<string, object?>[]>(multiGpu.Hardware["gpus"]);
        Assert.Collection(
            adapters,
            integrated =>
            {
                Assert.Equal("intel", integrated["vendor"]);
                Assert.Equal(true, integrated["isIntegrated"]);
                Assert.Equal(0, integrated["dedicatedVramMiB"]);
                Assert.Equal(false, integrated["isLegacyPrimary"]);
            },
            discrete =>
            {
                Assert.Equal("nvidia", discrete["vendor"]);
                Assert.Equal(4096, discrete["dedicatedVramMiB"]);
                Assert.Equal("CUDA0", discrete["runtimeDeviceHint"]);
                Assert.Equal(true, discrete["isLegacyPrimary"]);
            });
        Assert.NotEqual(singleGpu.MachineFingerprint, multiGpu.MachineFingerprint);
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
            var profile = WarmupProfileStore.CreateQwen3Q5Cuda4GbProfile();
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
        var canonical = ModelCatalogStore.ResolveCanonicalModelId("Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf");

        Assert.Equal("qwen3-4b-instruct-2507-q5-k-m", canonical);
    }

    [Fact]
    public void ModelCatalogStore_returns_reference_checksum_for_qwen3_q5km()
    {
        var checksum = ModelCatalogStore.TryGetReferenceChecksum("Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf");
        var catalog = ModelCatalogStore.CreateDefaultCatalog();
        var qwen = Assert.Single(catalog.Items, item => item.ModelId == "qwen3-4b-instruct-2507-q5-k-m");

        Assert.Equal("66713ce35a58a82fe87642d4ec13425bf9b9a46800fff5c49a665ef5701439dc", checksum);
        Assert.Equal(checksum, qwen.ChecksumSha256);
        Assert.Equal("verified_reference_hash", qwen.ChecksumStatus);
    }

    [Fact]
    public void ClientDefaults_default_model_is_governed_and_visible_in_installer()
    {
        var catalog = ModelCatalogStore.CreateDefaultCatalog();
        var collections = ModelCatalogStore.CreateDefaultCollections();
        var item = catalog.Items.Single(candidate => string.Equals(
            candidate.FileName,
            ClientDefaults.LlmModel,
            StringComparison.OrdinalIgnoreCase));
        var visibleIds = collections.Items
            .Where(candidate => candidate.VisibleInInstaller
                                && string.Equals(candidate.Scope, "client", StringComparison.OrdinalIgnoreCase))
            .SelectMany(candidate => candidate.ModelIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.NotNull(item);
        Assert.Contains(item.ModelId, visibleIds);
        Assert.Equal("qwen3-4b-instruct-2507-q5-k-m", item.ModelId);
    }

    [Fact]
    public async Task Qwen3_recommended_model_has_pinned_source_verified_hash_and_apache_license()
    {
        var root = NewTempRoot();
        try
        {
            await GovernanceArtifactStore.WriteAsync(
                GovernanceArtifactStore.ModelSourcesFile,
                ModelCatalogStore.CreateDefaultSources(),
                root);
            var qwen3 = Assert.Single(
                ModelCatalogStore.CreateDefaultCatalog().Items,
                item => item.ModelId == "qwen3-4b-instruct-2507-q5-k-m");
            var url = ModelCatalogStore.TryBuildDownloadUrl(qwen3, root);

            Assert.Equal(
                "66713ce35a58a82fe87642d4ec13425bf9b9a46800fff5c49a665ef5701439dc",
                qwen3.ChecksumSha256);
            Assert.Equal("verified_reference_hash", qwen3.ChecksumStatus);
            Assert.Equal("apache-2.0", qwen3.License.LicenseFamily);
            Assert.Equal("qwen3", qwen3.Gguf.Architecture);
            Assert.Equal(36, qwen3.Gguf.BlockCount);
            Assert.Equal(262144, qwen3.Gguf.ContextLength);
            Assert.Equal(
                "https://huggingface.co/bartowski/Qwen_Qwen3-4B-Instruct-2507-GGUF/resolve/" +
                "ae44f08e1392f39c0e474af10c3ff8355c8b6688/" +
                "Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf",
                url);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void Retired_model_migration_only_recognizes_unknown_artifacts_in_managed_directory()
    {
        var managedPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SAAIA",
            "Models",
            "retired-model.gguf");

        Assert.True(LocalLlmBootstrapper.IsRetiredManagedModelSelection(new AppSettings
        {
            ModelId = "retired-model.gguf",
            ModelPath = managedPath
        }));
        Assert.False(LocalLlmBootstrapper.IsRetiredManagedModelSelection(new AppSettings
        {
            ModelId = "retired-model.gguf",
            ModelPath = Path.Combine(Path.GetTempPath(), "retired-model.gguf")
        }));
        Assert.False(LocalLlmBootstrapper.IsRetiredManagedModelSelection(new AppSettings
        {
            ModelId = ClientDefaults.LlmModel,
            ModelPath = Path.Combine(Path.GetDirectoryName(managedPath)!, ClientDefaults.LlmModel)
        }));
    }

    [Fact]
    public void ModelCatalogStore_includes_verified_hashes_for_local_model_pack()
    {
        var catalog = ModelCatalogStore.CreateDefaultCatalog();

        Assert.Contains(catalog.Items, item =>
            item.ModelId == "qwen3-4b-instruct-2507-q5-k-m"
            && item.ChecksumSha256 == "66713ce35a58a82fe87642d4ec13425bf9b9a46800fff5c49a665ef5701439dc"
            && item.ChecksumStatus == "verified_reference_hash");
        Assert.Contains(catalog.Items, item =>
            item.ModelId == "qwen3-4b-instruct-2507-q4-k-m"
            && item.ChecksumSha256 == "2fde00ce69dd4899c70d020845e2638353015bba0fdf161b3eb965f2bca4464e"
            && item.ChecksumStatus == "verified_reference_hash");
    }

    [Fact]
    public void RuntimeCompatibilityPolicy_requires_qwen3_capable_runtime_on_every_backend()
    {
        var qwen3 = Assert.Single(
            ModelCatalogStore.CreateDefaultCatalog().Items,
            item => item.ModelId == "qwen3-4b-instruct-2507-q5-k-m");
        var policy = RuntimeCompatibilityPolicyStore.CreateDefaultPolicy();

        foreach (var runtime in new[]
                 {
                     "llama.cpp-cuda",
                     "llama.cpp-vulkan",
                     "llama.cpp-sycl",
                     "llama.cpp-hip",
                     "llama.cpp-cpu"
                 })
        {
            var old = RuntimeCompatibilityPolicyStore.Evaluate(runtime, "b8149", qwen3, policy);
            var current = RuntimeCompatibilityPolicyStore.Evaluate(runtime, "b8901", qwen3, policy);

            Assert.False(old.Compatible);
            Assert.True(old.RequiresUpgrade);
            Assert.Equal("b8901", old.RequiredBuild);
            Assert.True(current.Compatible);
        }
    }

    [Fact]
    public void RequalificationTriggerService_requires_requalification_when_runtime_or_model_drift()
    {
        var settings = new AppSettings
        {
            QualifiedProfile = WarmupProfileStore.CreateQwen3Q5Cuda4GbProfile(),
            LlamaExePath = @"C:\llm\llama-server-vulkan.exe",
            ModelId = "Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf"
        };

        var runtimeDrift = RequalificationTriggerService.EvaluateProfileDrift(settings);

        settings.LlamaExePath = @"C:\llm\llama-server-cuda.exe";
        settings.ModelId = "retired-unknown.gguf";
        var modelDrift = RequalificationTriggerService.EvaluateProfileDrift(settings);

        Assert.True(runtimeDrift.Required);
        Assert.Contains("runtime_changed", runtimeDrift.Reason);
        Assert.True(modelDrift.Required);
        Assert.Contains("model_changed", modelDrift.Reason);
    }

    [Fact]
    public void RequalificationTriggerService_requires_requalification_when_profile_contract_changes()
    {
        var settings = new AppSettings
        {
            QualifiedProfile = WarmupProfileStore.CreateQwen3Q5Cuda4GbProfile() with
            {
                CtxSize = 8192
            },
            LlamaExePath = @"C:\llm\llama-server-cuda.exe",
            ModelId = "Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf"
        };

        var drift = RequalificationTriggerService.EvaluateProfileDrift(settings);

        Assert.True(drift.Required);
        Assert.Contains("profile_changed", drift.Reason);
    }

    [Fact]
    public void RequalificationTriggerService_detects_perf_drift_repeated_failures_timeout_and_admin_action()
    {
        var profileId = "qwen3-4b-2507-q5km-cuda-balanced";
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
            "qwen3-4b-instruct-2507-q5-k-m",
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
