using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LiveRuntimeProvisioningProbeTests
{
    [Fact]
    [Trait("Category", "LiveHardware")]
    public async Task Provision_requested_backends_at_one_build_and_probe_their_devices()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SAAIA_LIVE_RUNTIME_PROVISIONING"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var requestedBackends = (Environment.GetEnvironmentVariable("SAAIA_RUNTIME_BACKENDS") ?? "vulkan")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static backend => backend.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var minBuild = Environment.GetEnvironmentVariable("SAAIA_RUNTIME_MIN_BUILD");
        var downloader = new LlamaCppReleaseDownloader();
        var results = new List<object>();

        foreach (var backend in requestedBackends)
        {
            var install = backend switch
            {
                "cuda" => await downloader.EnsureWindowsCudaAsync(
                    progress: null,
                    minBuild,
                    CancellationToken.None),
                "vulkan" => await downloader.EnsureWindowsVulkanAsync(
                    progress: null,
                    minBuild,
                    CancellationToken.None),
                "sycl" => await downloader.EnsureWindowsSyclAsync(
                    progress: null,
                    minBuild,
                    CancellationToken.None),
                "hip" => await downloader.EnsureWindowsHipAsync(
                    progress: null,
                    minBuild,
                    CancellationToken.None),
                "cpu" => await downloader.EnsureWindowsCpuAsync(
                    progress: null,
                    minBuild,
                    CancellationToken.None),
                _ => throw new InvalidOperationException($"Unsupported live provisioning backend '{backend}'.")
            };
            Assert.True(install.ok, $"{backend}: {install.message}");
            Assert.False(string.IsNullOrWhiteSpace(install.exePath));
            Assert.True(File.Exists(install.exePath));
            Assert.True(
                LlamaCppReleaseDownloader.TryMarkRuntimePendingQualification(
                    RequalificationTriggerService.DetectRuntimeKey(install.exePath)),
                $"{backend}: unable to mark the provisioned runtime pending qualification");

            var probe = await LocalLlmRuntimeCapabilityProbe.ProbeAsync(
                install.exePath!,
                TimeSpan.FromSeconds(20),
                CancellationToken.None);
            Assert.True(probe.Succeeded, $"{backend}: {probe.Diagnostic}");
            results.Add(new
            {
                backend,
                install.message,
                install.exePath,
                probe.RuntimeId,
                probe.DurationMs,
                Devices = probe.Devices.Select(device => new
                {
                    device.DeviceId,
                    device.Description,
                    device.ReportedMemoryMiB,
                    device.ReportedFreeMemoryMiB,
                    device.ReportsSharedMemory,
                    device.MemoryArchitecture
                }).ToArray()
            });
        }

        var artifactPath = Environment.GetEnvironmentVariable("SAAIA_RUNTIME_PROVISIONING_ARTIFACT");
        if (!string.IsNullOrWhiteSpace(artifactPath))
        {
            var fullPath = Path.GetFullPath(artifactPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(
                fullPath,
                JsonSerializer.Serialize(
                    results,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    {
                        WriteIndented = true
                    }));
        }
    }
}
