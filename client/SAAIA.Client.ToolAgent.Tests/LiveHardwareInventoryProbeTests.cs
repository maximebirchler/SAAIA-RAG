using System.Text.Json;
using SAAIA.Client.WinUI.Services;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class LiveHardwareInventoryProbeTests
{
    [Fact]
    [Trait("Category", "LiveHardware")]
    public async Task Capture_reports_every_expected_local_gpu()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SAAIA_LIVE_HARDWARE_INVENTORY"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var adapters = await GpuDetector.TryGetGpusAsync(CancellationToken.None);
        Assert.NotEmpty(adapters);

        var expectedNames = (Environment.GetEnvironmentVariable("SAAIA_EXPECT_GPU_NAMES") ?? string.Empty)
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var expectedName in expectedNames)
        {
            Assert.Contains(
                adapters,
                adapter => adapter.Name.Contains(expectedName, StringComparison.OrdinalIgnoreCase));
        }

        var artifact = await HardwareProbeService.CaptureAsync(
            refresh: true,
            CancellationToken.None);
        Assert.Equal(adapters.Count, Assert.IsType<int>(artifact.Hardware["gpuCount"]));
        var runtimeProbes = Assert.IsType<Dictionary<string, object?>[]>(artifact.Hardware["runtimeProbes"]);
        Assert.All(runtimeProbes, static probe =>
            Assert.All(
                Assert.IsType<Dictionary<string, object?>[]>(probe["devices"]),
                static device =>
                {
                    Assert.True(device.ContainsKey("memoryArchitecture"));
                    Assert.True(device.ContainsKey("hardwareMatchSource"));
                    Assert.NotEqual("unknown", device["memoryArchitecture"] as string);
                }));
        var expectedRuntimeDevices = (Environment.GetEnvironmentVariable("SAAIA_EXPECT_RUNTIME_DEVICES") ?? string.Empty)
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var expected in expectedRuntimeDevices)
        {
            var separator = expected.IndexOf(':');
            Assert.True(separator > 0, $"Invalid expected runtime device '{expected}'. Use runtimeId:deviceId.");
            var expectedRuntime = expected[..separator];
            var expectedDevice = expected[(separator + 1)..];
            Assert.Contains(
                runtimeProbes,
                probe => string.Equals(probe["runtimeId"] as string, expectedRuntime, StringComparison.OrdinalIgnoreCase)
                         && Assert.IsType<Dictionary<string, object?>[]>(probe["devices"])
                             .Any(device => string.Equals(
                                 device["deviceId"] as string,
                                 expectedDevice,
                                 StringComparison.OrdinalIgnoreCase)));
        }

        var artifactPath = Environment.GetEnvironmentVariable("SAAIA_HARDWARE_INVENTORY_PROBE_ARTIFACT");
        if (!string.IsNullOrWhiteSpace(artifactPath))
        {
            var fullPath = Path.GetFullPath(artifactPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(
                fullPath,
                JsonSerializer.Serialize(
                    artifact,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    {
                        WriteIndented = true
                    }));
        }
    }
}
