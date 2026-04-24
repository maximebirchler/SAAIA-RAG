using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class ModelIntegrityServiceTests
{
    [Fact]
    public async Task VerifyModelAsync_quarantines_model_when_catalog_checksum_mismatches()
    {
        var root = NewTempRoot();
        var modelDir = Path.Combine(root, "models");
        Directory.CreateDirectory(modelDir);
        var modelPath = Path.Combine(modelDir, "Qwen2.5-3B-Instruct-Q4_K_M.gguf");

        try
        {
            await File.WriteAllTextAsync(modelPath, "tampered-model");
            var settings = new AppSettings
            {
                ModelPath = modelPath,
                ModelId = "qwen2.5-3b-instruct-q4-k-m",
                UiLanguage = "en"
            };

            await GovernanceArtifactStore.EnsureDefaultArtifactsAsync(settings, root);
            var catalog = ModelCatalogStore.CreateDefaultCatalog();
            var patchedCatalog = catalog with
            {
                Items = catalog.Items.Select(item => item.ModelId == "qwen2.5-3b-instruct-q4-k-m"
                    ? item with
                    {
                        ChecksumSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                        ChecksumStatus = "verified_reference_hash"
                    }
                    : item).ToArray()
            };
            await GovernanceArtifactStore.WriteAsync(GovernanceArtifactStore.ModelCatalogFile, patchedCatalog, root);

            var result = await ModelIntegrityService.VerifyModelAsync(settings, root);

            Assert.True(result.Blocked);
            Assert.Equal("checksum_mismatch_quarantined", result.Reason);
            Assert.Equal(ModelIntegrityService.GetQuarantineUserMessage(settings.UiLanguage), result.UserMessage);
            Assert.False(File.Exists(modelPath));
            Assert.True(File.Exists(ModelIntegrityService.QuarantinePath(modelPath)));

            var log = await GovernanceArtifactStore.ReadAsync<AcquisitionLogArtifact>(
                GovernanceArtifactStore.AcquisitionLogFile,
                root);
            Assert.Equal(GovernanceArtifactReadStatus.Ok, log.Status);
            Assert.Equal("quarantined", log.Value!.Items[0].Action);
            Assert.Equal("checksum_mismatch", log.Value.Items[0].Reason);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void GetQuarantineUserMessage_returns_localized_message()
    {
        Assert.Equal("Model unavailable - contact the administrator.", ModelIntegrityService.GetQuarantineUserMessage("en"));
        Assert.Equal("Modele non disponible - contactez l'administrateur.", ModelIntegrityService.GetQuarantineUserMessage("fr"));
    }

    [Fact]
    public async Task VerifyModelAsync_skips_when_no_reference_checksum_is_available()
    {
        var root = NewTempRoot();
        var modelDir = Path.Combine(root, "models");
        Directory.CreateDirectory(modelDir);
        var modelPath = Path.Combine(modelDir, "custom.gguf");

        try
        {
            await File.WriteAllTextAsync(modelPath, "custom-model");
            var settings = new AppSettings
            {
                ModelPath = modelPath,
                ModelId = "custom.gguf"
            };

            await GovernanceArtifactStore.EnsureDefaultArtifactsAsync(settings, root);
            var result = await ModelIntegrityService.VerifyModelAsync(settings, root);

            Assert.False(result.Blocked);
            Assert.Equal("checksum_unavailable", result.Reason);
            Assert.True(File.Exists(modelPath));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "saaia-model-integrity-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
