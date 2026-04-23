using System.IO;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

[Collection("RuntimeRootSerial")]
public sealed class LocalLlmRuntimeDiagnosticsServiceTests
{
    [Fact]
    public async Task EvaluateAsync_returns_pending_runtime_state_and_pascal_flash_attn_override()
    {
        var root = NewTempRoot();
        var runtimeRoot = Path.Combine(root, "runtime");
        LlamaCppReleaseDownloader.RuntimeRootOverride = runtimeRoot;
        RuntimeEventLogStore.RootOverride = root;

        try
        {
            var previousExe = CreateRuntime(runtimeRoot, "win-cuda-x64", "b8149");
            var activeExe = CreateRuntime(runtimeRoot, "win-cuda-x64", "b8901");
            WriteActiveRuntimeManifest(runtimeRoot, activeExe, previousExe);

            var settings = new AppSettings
            {
                UseLocalLlm = true,
                ManageLocalLlmProcess = true,
                LlamaExePath = activeExe,
                ModelId = "gemma-4-e2b-it-q4-k-m",
                QualifiedProfile = WarmupProfileStore.CreateReferenceCudaProfile()
            };

            await GovernanceArtifactStore.EnsureDefaultArtifactsAsync(settings, root);
            await RuntimeEventLogStore.AppendAsync(new RuntimeEventLogItem(
                DateTimeOffset.Parse("2026-04-23T11:00:00Z"),
                "llama.cpp-cuda",
                "runtime_upgrade_activated",
                "b8901",
                "b8149",
                settings.ModelId,
                "test"), root);

            var gpu = new GpuInfo(
                GpuVendor.Nvidia,
                "Quadro P520",
                4L * 1024 * 1024 * 1024,
                IsIntegrated: false,
                DetectionSource: "test");

            var diagnostics = await LocalLlmRuntimeDiagnosticsService.EvaluateAsync(settings, gpu, root);

            Assert.Equal("llama.cpp-cuda", diagnostics.RuntimeId);
            Assert.Equal("b8901", diagnostics.ActiveBuild);
            Assert.Equal("pending_qualification", diagnostics.ActiveState);
            Assert.Equal("b8149", diagnostics.PreviousBuild);
            Assert.Equal(DateTimeOffset.Parse("2026-04-23T10:00:00Z"), diagnostics.ActivatedAtUtc);
            Assert.Null(diagnostics.QualifiedAtUtc);
            Assert.False(diagnostics.UpgradeRequired);
            Assert.Equal("gemma4", diagnostics.ModelFamily);
            Assert.False(diagnostics.ForcedFlashAttn);
            Assert.Single(diagnostics.RecentEvents);
            Assert.Equal("runtime_upgrade_activated", diagnostics.RecentEvents[0].EventKind);
        }
        finally
        {
            LlamaCppReleaseDownloader.RuntimeRootOverride = null;
            RuntimeEventLogStore.RootOverride = null;
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task EvaluateAsync_returns_required_build_for_legacy_gemma_runtime()
    {
        var root = NewTempRoot();
        var runtimeRoot = Path.Combine(root, "runtime");
        LlamaCppReleaseDownloader.RuntimeRootOverride = runtimeRoot;
        RuntimeEventLogStore.RootOverride = root;

        try
        {
            var legacyExe = CreateRuntime(runtimeRoot, "win-cuda-x64", "b8149");
            var settings = new AppSettings
            {
                UseLocalLlm = true,
                ManageLocalLlmProcess = true,
                LlamaExePath = legacyExe,
                ModelId = "gemma-4-e2b-it-q4-k-m",
                QualifiedProfile = WarmupProfileStore.CreateReferenceCudaProfile()
            };

            await GovernanceArtifactStore.EnsureDefaultArtifactsAsync(settings, root);

            var diagnostics = await LocalLlmRuntimeDiagnosticsService.EvaluateAsync(settings, gpu: null, root: root);

            Assert.Equal("b8149", diagnostics.ActiveBuild);
            Assert.True(diagnostics.UpgradeRequired);
            Assert.Equal("b8901", diagnostics.RequiredBuild);
            Assert.Null(diagnostics.ActiveState);
        }
        finally
        {
            LlamaCppReleaseDownloader.RuntimeRootOverride = null;
            RuntimeEventLogStore.RootOverride = null;
            DeleteTempRoot(root);
        }
    }

    private static string CreateRuntime(string runtimeRoot, string backendDir, string build)
    {
        var runtimeDir = Path.Combine(runtimeRoot, backendDir, build);
        Directory.CreateDirectory(runtimeDir);

        var exePath = Path.Combine(runtimeDir, "llama-server.exe");
        File.WriteAllText(exePath, "stub");
        File.WriteAllText(Path.Combine(runtimeDir, "runtime.tag"), build);
        return exePath;
    }

    private static void WriteActiveRuntimeManifest(string runtimeRoot, string activeExe, string previousExe)
    {
        Directory.CreateDirectory(runtimeRoot);

        var json = JsonSerializer.Serialize(new
        {
            artifact = "active-runtime.json",
            cdcAlignment = "v3.1",
            items = new[]
            {
                new
                {
                    runtimeId = "llama.cpp-cuda",
                    backend = "cuda",
                    build = "b8901",
                    directoryPath = Path.GetDirectoryName(activeExe),
                    exePath = activeExe,
                    assetName = "llama-b8901-bin-win-cuda-12.4-x64.zip",
                    activatedAtUtc = "2026-04-23T10:00:00Z",
                    status = "pending_qualification",
                    qualifiedAtUtc = (string?)null,
                    previous = new
                    {
                        build = "b8149",
                        directoryPath = Path.GetDirectoryName(previousExe),
                        exePath = previousExe,
                        assetName = "llama-b8149-bin-win-cuda-12.4-x64.zip"
                    }
                }
            }
        }, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(Path.Combine(runtimeRoot, "active-runtime.json"), json);
    }

    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "saaia-runtime-diagnostics-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
