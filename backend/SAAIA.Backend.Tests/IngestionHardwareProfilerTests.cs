using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class IngestionHardwareProfilerTests
{
    [Fact]
    public void Nvidia_inventory_parses_multiple_devices_without_locale_dependent_numbers()
    {
        const string output = """
0, NVIDIA T2000, 4096, 580.173
1, NVIDIA RTX 4000, 16384, 580.173
""";

        var devices = IngestionHardwareProfiler.ParseNvidiaSmi(output);

        Assert.Equal(2, devices.Count);
        Assert.Equal("nvidia:0", devices[0].DeviceId);
        Assert.Equal(4L * 1024 * 1024 * 1024, devices[0].DedicatedMemoryBytes);
        Assert.Equal("NVIDIA RTX 4000", devices[1].Model);
    }

    [Fact]
    public void Capture_always_declares_cpu_and_builds_stable_profile_shape()
    {
        var profile = IngestionHardwareProfiler.Capture();

        Assert.StartsWith("hardware_", profile.ProfileId, StringComparison.Ordinal);
        Assert.True(profile.LogicalProcessorCount > 0);
        Assert.True(profile.MemoryBytes > 0);
        Assert.Contains(profile.Devices, device => device.DeviceId == "cpu:0");
    }
}
