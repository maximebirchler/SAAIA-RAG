using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LocalLlmRuntimeStatusServiceTests
{
    [Fact]
    public async Task EvaluateAsync_returns_null_for_nominal_profile_without_alerts()
    {
        var root = NewTempRoot();
        try
        {
            var settings = new AppSettings
            {
                UseLocalLlm = true,
                ManageLocalLlmProcess = true,
                LlamaExePath = @"C:\runtime\win-cuda-x64\llama-server.exe",
                ModelId = "qwen2.5-3b-instruct-q4-k-m",
                QualifiedProfile = WarmupProfileStore.CreateReferenceCudaProfile()
            };

            await GovernanceArtifactStore.EnsureDefaultArtifactsAsync(settings, root);
            var status = await LocalLlmRuntimeStatusService.EvaluateAsync(settings, root);

            Assert.Null(status);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void ResolveDisplayMessage_clears_status_when_runtime_is_running_and_nominal()
    {
        var message = LocalLlmRuntimeStatusService.ResolveDisplayMessage(
            status: null,
            isRunning: true,
            currentMessage: "Verification de compatibilite en cours...");

        Assert.Equal(string.Empty, message);
    }

    [Fact]
    public void ResolveDisplayMessage_preserves_current_message_when_runtime_is_stopped_and_no_alert_exists()
    {
        var message = LocalLlmRuntimeStatusService.ResolveDisplayMessage(
            status: null,
            isRunning: false,
            currentMessage: "Stopped.");

        Assert.Equal("Stopped.", message);
    }

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
