using System.IO;
using System.Text.Json;
using System.Reflection;
using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

[Collection("RuntimeRootSerial")]
public sealed class LlamaCppReleaseDownloaderTests
{
    [Fact]
    public void TryMarkRuntimeQualified_clears_pending_upgrade_metadata()
    {
        var runtimeRoot = NewTempRoot();
        LlamaCppReleaseDownloader.RuntimeRootOverride = runtimeRoot;
        RuntimeEventLogStore.RootOverride = runtimeRoot;

        try
        {
            var previousExe = CreateRuntime(runtimeRoot, "win-cuda-x64", "b8149");
            var activeExe = CreateRuntime(runtimeRoot, "win-cuda-x64", "b8901");
            WriteActiveRuntimeManifest(runtimeRoot, activeExe, previousExe);

            var ok = LlamaCppReleaseDownloader.TryMarkRuntimeQualified("llama.cpp-cuda");

            Assert.True(ok);

            var state = LlamaCppReleaseDownloader.TryGetActiveRuntimeState("llama.cpp-cuda");
            Assert.NotNull(state);
            Assert.Equal("b8901", state!.Build);
            Assert.Equal("qualified", state.Status);
            Assert.Null(state.PreviousBuild);

            using var doc = JsonDocument.Parse(File.ReadAllText(LlamaCppReleaseDownloader.ActiveRuntimeManifestPath));
            var item = doc.RootElement.GetProperty("items")[0];
            Assert.Equal("qualified", item.GetProperty("status").GetString());
            Assert.True(item.TryGetProperty("previous", out var previous));
            Assert.Equal(JsonValueKind.Null, previous.ValueKind);
        }
        finally
        {
            LlamaCppReleaseDownloader.RuntimeRootOverride = null;
            RuntimeEventLogStore.RootOverride = null;
            DeleteTempRoot(runtimeRoot);
        }
    }

    [Fact]
    public void TryRollbackPendingRuntime_restores_previous_runtime_build()
    {
        var runtimeRoot = NewTempRoot();
        LlamaCppReleaseDownloader.RuntimeRootOverride = runtimeRoot;
        RuntimeEventLogStore.RootOverride = runtimeRoot;

        try
        {
            var previousExe = CreateRuntime(runtimeRoot, "win-cuda-x64", "b8149");
            var activeExe = CreateRuntime(runtimeRoot, "win-cuda-x64", "b8901");
            WriteActiveRuntimeManifest(runtimeRoot, activeExe, previousExe);

            var ok = LlamaCppReleaseDownloader.TryRollbackPendingRuntime(
                "llama.cpp-cuda",
                out var rollbackExe,
                out var rollbackBuild);

            Assert.True(ok);
            Assert.Equal(previousExe, rollbackExe);
            Assert.Equal("b8149", rollbackBuild);

            var state = LlamaCppReleaseDownloader.TryGetActiveRuntimeState("llama.cpp-cuda");
            Assert.NotNull(state);
            Assert.Equal("b8149", state!.Build);
            Assert.Equal(previousExe, state.ExePath);
            Assert.Equal("qualified", state.Status);
            Assert.Null(state.PreviousBuild);
        }
        finally
        {
            LlamaCppReleaseDownloader.RuntimeRootOverride = null;
            RuntimeEventLogStore.RootOverride = null;
            DeleteTempRoot(runtimeRoot);
        }
    }

    [Fact]
    public void TryResolveInstalledRuntime_falls_back_to_best_versioned_runtime_when_manifest_is_missing()
    {
        var runtimeRoot = NewTempRoot();
        LlamaCppReleaseDownloader.RuntimeRootOverride = runtimeRoot;

        try
        {
            var oldExe = CreateRuntime(runtimeRoot, "win-cuda-x64", "b8149");
            var newExe = CreateRuntime(runtimeRoot, "win-cuda-x64", "b8901");

            var ok = InvokeTryResolveInstalledRuntime(
                "llama.cpp-cuda",
                Path.Combine(runtimeRoot, "win-cuda-x64"),
                "b8901",
                out var exePath);

            Assert.True(ok);
            Assert.Equal(newExe, exePath);
            Assert.NotEqual(oldExe, exePath);
        }
        finally
        {
            LlamaCppReleaseDownloader.RuntimeRootOverride = null;
            DeleteTempRoot(runtimeRoot);
        }
    }

    [Fact]
    public void EnsureRuntimeTrackedForQualification_creates_pending_manifest_for_existing_legacy_runtime()
    {
        var runtimeRoot = NewTempRoot();
        LlamaCppReleaseDownloader.RuntimeRootOverride = runtimeRoot;
        RuntimeEventLogStore.RootOverride = runtimeRoot;

        try
        {
            var exePath = CreateRuntime(runtimeRoot, "win-cuda-x64", "b8149");

            var ok = LlamaCppReleaseDownloader.EnsureRuntimeTrackedForQualification("llama.cpp-cuda", exePath);

            Assert.True(ok);

            var state = LlamaCppReleaseDownloader.TryGetActiveRuntimeState("llama.cpp-cuda");
            Assert.NotNull(state);
            Assert.Equal("b8149", state!.Build);
            Assert.Equal(exePath, state.ExePath);
            Assert.Equal("pending_qualification", state.Status);
            Assert.True(File.Exists(LlamaCppReleaseDownloader.ActiveRuntimeManifestPath + ".sha256"));
        }
        finally
        {
            LlamaCppReleaseDownloader.RuntimeRootOverride = null;
            RuntimeEventLogStore.RootOverride = null;
            DeleteTempRoot(runtimeRoot);
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

    private static bool InvokeTryResolveInstalledRuntime(
        string runtimeId,
        string runtimeBaseDir,
        string? minBuild,
        out string exePath)
    {
        var method = typeof(LlamaCppReleaseDownloader).GetMethod(
            "TryResolveInstalledRuntime",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var args = new object?[] { runtimeId, runtimeBaseDir, minBuild, null };
        var ok = (bool)method!.Invoke(null, args)!;
        exePath = (string)args[3]!;
        return ok;
    }

    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "saaia-llama-runtime-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
