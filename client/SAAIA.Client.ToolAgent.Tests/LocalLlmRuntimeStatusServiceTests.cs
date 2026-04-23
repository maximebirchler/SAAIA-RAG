using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LocalLlmRuntimeStatusServiceTests
{
    [Fact]
    public async Task EvaluateAsync_returns_fallback_message_for_fallback_profile()
    {
        var root = NewTempRoot();
        try
        {
            var settings = new AppSettings
            {
                UseLocalLlm = true,
                ManageLocalLlmProcess = true,
                QualifiedProfile = WarmupProfileStore.CreateReferenceCudaFallbackProfile()
            };

            await GovernanceArtifactStore.EnsureDefaultArtifactsAsync(settings, root);
            var status = await LocalLlmRuntimeStatusService.EvaluateAsync(settings, root);

            Assert.NotNull(status);
            Assert.Equal("fallback_active", status!.Code);
            Assert.Equal("Profil de secours actif.", status.Message);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task EvaluateAsync_returns_degraded_message_for_pass_degraded_result()
    {
        var root = NewTempRoot();
        try
        {
            var profile = WarmupProfileStore.CreateReferenceCudaProfile();
            var settings = new AppSettings
            {
                UseLocalLlm = true,
                ManageLocalLlmProcess = true,
                QualifiedProfile = profile
            };

            await GovernanceArtifactStore.EnsureDefaultArtifactsAsync(settings, root);
            await GovernanceArtifactStore.WriteAsync(
                GovernanceArtifactStore.WarmupResultsFile,
                new WarmupResultsArtifact(
                    GovernanceArtifactStore.WarmupResultsFile,
                    "v3.1",
                    new[]
                    {
                        new WarmupResultItem(
                            DateTimeOffset.UtcNow,
                            profile.ProfileId,
                            profile.Runtime,
                            profile.ModelId,
                            WarmupGateStatus.PassDegraded,
                            2,
                            3,
                            25000,
                            14000,
                            4.2,
                            120,
                            null,
                            new[] { "pass_degraded_thresholds" },
                            "fp",
                            "test")
                    }),
                root);

            var status = await LocalLlmRuntimeStatusService.EvaluateAsync(settings, root);

            Assert.NotNull(status);
            Assert.Equal("performance_reduced", status!.Code);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task EvaluateAsync_returns_runtime_unavailable_for_fail_block()
    {
        var root = NewTempRoot();
        try
        {
            var profile = WarmupProfileStore.CreateReferenceCudaProfile();
            var settings = new AppSettings
            {
                UseLocalLlm = true,
                ManageLocalLlmProcess = true,
                QualifiedProfile = profile
            };

            await GovernanceArtifactStore.EnsureDefaultArtifactsAsync(settings, root);
            await GovernanceArtifactStore.WriteAsync(
                GovernanceArtifactStore.WarmupResultsFile,
                new WarmupResultsArtifact(
                    GovernanceArtifactStore.WarmupResultsFile,
                    "v3.1",
                    new[]
                    {
                        new WarmupResultItem(
                            DateTimeOffset.UtcNow,
                            profile.ProfileId,
                            profile.Runtime,
                            profile.ModelId,
                            WarmupGateStatus.FailBlock,
                            0,
                            3,
                            null,
                            null,
                            null,
                            null,
                            null,
                            new[] { "hard_gate_dxgi_budget_insufficient" },
                            "fp",
                            "test")
                    }),
                root);

            var status = await LocalLlmRuntimeStatusService.EvaluateAsync(settings, root);

            Assert.NotNull(status);
            Assert.Equal("runtime_unavailable", status!.Code);
            Assert.True(status.IsError);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "saaia-local-status-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
