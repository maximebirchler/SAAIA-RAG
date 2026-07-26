using System.Collections.Generic;
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
                LlamaExePath = CreateCurrentRuntime(root),
                ModelId = "qwen3-4b-instruct-2507-q5-k-m",
                QualifiedProfile = WarmupProfileStore.CreateQwen3Q5Cuda4GbProfile()
            };

            await GovernanceArtifactStore.EnsureDefaultArtifactsAsync(settings, root);
            await GovernanceArtifactStore.WriteAsync(
                GovernanceArtifactStore.HardwareProbeFile,
                new HardwareProbeArtifact(
                    GovernanceArtifactStore.HardwareProbeFile,
                    "v3.1",
                    "captured",
                    DateTimeOffset.UtcNow,
                    "machine-test",
                    new Dictionary<string, object?>
                    {
                        ["isOnBattery"] = false,
                        ["batteryLifePercent"] = 100,
                        ["powerStatusSource"] = "test"
                    }),
                root);
            var status = await LocalLlmRuntimeStatusService.EvaluateAsync(settings, root);

            Assert.Null(status);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task EvaluateAsync_returns_runtime_upgrade_required_for_legacy_qwen3_runtime()
    {
        var root = NewTempRoot();
        var runtimeRoot = Path.Combine(root, "runtime");
        LlamaCppReleaseDownloader.RuntimeRootOverride = runtimeRoot;

        try
        {
            var runtimeDir = Path.Combine(runtimeRoot, "win-cuda-x64", "b8149");
            Directory.CreateDirectory(runtimeDir);
            var exePath = Path.Combine(runtimeDir, "llama-server.exe");
            await File.WriteAllTextAsync(exePath, "stub");
            await File.WriteAllTextAsync(Path.Combine(runtimeDir, "runtime.tag"), "b8149");

            var settings = new AppSettings
            {
                UseLocalLlm = true,
                ManageLocalLlmProcess = true,
                LlamaExePath = exePath,
                ModelId = "qwen3-4b-instruct-2507-q5-k-m",
                QualifiedProfile = WarmupProfileStore.CreateQwen3Q5Cuda4GbProfile()
            };

            await GovernanceArtifactStore.EnsureDefaultArtifactsAsync(settings, root);

            var status = await LocalLlmRuntimeStatusService.EvaluateAsync(settings, root);

            Assert.NotNull(status);
            Assert.Equal("runtime_upgrade_required", status!.Code);
            Assert.True(status.IsError);
        }
        finally
        {
            LlamaCppReleaseDownloader.RuntimeRootOverride = null;
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
                LlamaExePath = CreateCurrentRuntime(root),
                ModelId = "qwen3-4b-instruct-2507-q5-k-m",
                QualifiedProfile = WarmupProfileStore.CreateQwen3Q5Cuda4GbFallbackProfile()
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
            var profile = WarmupProfileStore.CreateQwen3Q5Cuda4GbProfile();
            var settings = new AppSettings
            {
                UseLocalLlm = true,
                ManageLocalLlmProcess = true,
                LlamaExePath = CreateCurrentRuntime(root),
                ModelId = "qwen3-4b-instruct-2507-q5-k-m",
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
            var profile = WarmupProfileStore.CreateQwen3Q5Cuda4GbProfile();
            var settings = new AppSettings
            {
                UseLocalLlm = true,
                ManageLocalLlmProcess = true,
                LlamaExePath = CreateCurrentRuntime(root),
                ModelId = "qwen3-4b-instruct-2507-q5-k-m",
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

    [Fact]
    public async Task EvaluateAsync_returns_quarantine_message_when_model_is_quarantined()
    {
        var root = NewTempRoot();
        var modelDir = Path.Combine(root, "models");
        Directory.CreateDirectory(modelDir);
        var modelPath = Path.Combine(modelDir, "Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf");

        try
        {
            await File.WriteAllTextAsync(ModelIntegrityService.QuarantinePath(modelPath), "quarantined");
            var settings = new AppSettings
            {
                UseLocalLlm = true,
                ManageLocalLlmProcess = true,
                ModelPath = modelPath,
                UiLanguage = "en",
                QualifiedProfile = WarmupProfileStore.CreateQwen3Q5Cuda4GbProfile()
            };

            await GovernanceArtifactStore.EnsureDefaultArtifactsAsync(settings, root);
            var status = await LocalLlmRuntimeStatusService.EvaluateAsync(settings, root);

            Assert.NotNull(status);
            Assert.Equal("model_quarantined", status!.Code);
            Assert.Equal(ModelIntegrityService.GetQuarantineUserMessage(settings.UiLanguage), status.Message);
            Assert.True(status.IsError);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task EvaluateAsync_returns_localized_quarantine_message()
    {
        var root = NewTempRoot();
        var modelDir = Path.Combine(root, "models");
        Directory.CreateDirectory(modelDir);
        var modelPath = Path.Combine(modelDir, "Qwen_Qwen3-4B-Instruct-2507-Q5_K_M.gguf");

        try
        {
            await File.WriteAllTextAsync(ModelIntegrityService.QuarantinePath(modelPath), "quarantined");
            var settings = new AppSettings
            {
                UseLocalLlm = true,
                ManageLocalLlmProcess = true,
                ModelPath = modelPath,
                UiLanguage = "en",
                QualifiedProfile = WarmupProfileStore.CreateQwen3Q5Cuda4GbProfile()
            };

            await GovernanceArtifactStore.EnsureDefaultArtifactsAsync(settings, root);
            var status = await LocalLlmRuntimeStatusService.EvaluateAsync(settings, root);

            Assert.NotNull(status);
            Assert.Equal("Model unavailable - contact the administrator.", status!.Message);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task EvaluateAsync_returns_runtime_upgrade_required_for_qwen3_with_legacy_runtime()
    {
        var root = NewTempRoot();
        var runtimeDir = Path.Combine(root, "runtime", "win-cuda-x64");
        Directory.CreateDirectory(runtimeDir);
        var exePath = Path.Combine(runtimeDir, "llama-server.exe");

        try
        {
            await File.WriteAllTextAsync(exePath, "stub");
            await File.WriteAllTextAsync(Path.Combine(runtimeDir, "runtime.tag"), "b8149");

            var settings = new AppSettings
            {
                UseLocalLlm = true,
                ManageLocalLlmProcess = true,
                LlamaExePath = exePath,
                ModelId = "qwen3-4b-instruct-2507-q5-k-m",
                QualifiedProfile = WarmupProfileStore.CreateQwen3Q5Cuda4GbProfile()
            };

            await GovernanceArtifactStore.EnsureDefaultArtifactsAsync(settings, root);
            var status = await LocalLlmRuntimeStatusService.EvaluateAsync(settings, root);

            Assert.NotNull(status);
            Assert.Equal("runtime_upgrade_required", status!.Code);
            Assert.Equal("Mise a niveau du runtime requise.", status.Message);
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

    private static string CreateCurrentRuntime(string root)
    {
        var runtimeDir = Path.Combine(root, "runtime-current", "win-cuda-x64");
        Directory.CreateDirectory(runtimeDir);
        var executable = Path.Combine(runtimeDir, "llama-server.exe");
        File.WriteAllText(executable, "stub");
        File.WriteAllText(Path.Combine(runtimeDir, "runtime.tag"), "b8901");
        return executable;
    }

    private static void DeleteTempRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
