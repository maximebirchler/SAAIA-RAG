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
        }
        finally
        {
            DeleteTempRoot(root);
        }
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
