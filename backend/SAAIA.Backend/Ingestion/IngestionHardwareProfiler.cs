using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using SAAIA.Contracts.DocumentIntelligence;

internal static class IngestionHardwareProfiler
{
    public static IngestionHardwareProfile Capture()
    {
        var operatingSystem = RuntimeInformation.OSDescription.Trim();
        var cpuModel = ResolveCpuModel();
        var memoryBytes = ResolveMemoryBytes();
        var devices = new List<IngestionComputeDevice>
        {
            new()
            {
                DeviceId = "cpu:0",
                DeviceType = "cpu",
                Model = cpuModel,
                SharedMemoryBytes = memoryBytes,
                Runtime = RuntimeInformation.FrameworkDescription
            }
        };
        devices.AddRange(ParseNvidiaSmi(Run(
            "nvidia-smi",
            "--query-gpu=index,name,memory.total,driver_version --format=csv,noheader,nounits")));
        devices.AddRange(DiscoverLinuxDrmDevices());

        var orderedDevices = devices
            .GroupBy(static device => device.DeviceId, StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static device => device.DeviceId, StringComparer.Ordinal)
            .ToList();
        var profile = new IngestionHardwareProfile
        {
            OperatingSystem = operatingSystem,
            CpuModel = cpuModel,
            LogicalProcessorCount = Environment.ProcessorCount,
            MemoryBytes = memoryBytes,
            Devices = orderedDevices
        };
        profile.ProfileId = CanonicalStableId.Create(
            "hardware",
            operatingSystem,
            cpuModel,
            profile.LogicalProcessorCount.ToString(CultureInfo.InvariantCulture),
            memoryBytes.ToString(CultureInfo.InvariantCulture),
            string.Join(
                "\n",
                orderedDevices.Select(device =>
                    $"{device.DeviceId}|{device.DeviceType}|{device.Vendor}|{device.Model}|{device.DedicatedMemoryBytes}|{device.SharedMemoryBytes}|{device.DriverVersion}|{device.Runtime}")));
        return profile;
    }

    internal static IReadOnlyList<IngestionComputeDevice> ParseNvidiaSmi(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return [];

        var devices = new List<IngestionComputeDevice>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length != 4
                || !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                || !long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var memoryMiB))
            {
                continue;
            }

            devices.Add(new()
            {
                DeviceId = $"nvidia:{index}",
                DeviceType = "gpu",
                Vendor = "NVIDIA",
                Model = fields[1],
                DedicatedMemoryBytes = memoryMiB * 1024 * 1024,
                DriverVersion = fields[3],
                Runtime = "cuda"
            });
        }

        return devices;
    }

    private static IReadOnlyList<IngestionComputeDevice> DiscoverLinuxDrmDevices()
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/sys/class/drm"))
            return [];

        var devices = new List<IngestionComputeDevice>();
        foreach (var cardPath in Directory.GetDirectories("/sys/class/drm", "card*"))
        {
            var cardName = Path.GetFileName(cardPath);
            if (cardName.Contains('-', StringComparison.Ordinal))
                continue;

            var vendor = ReadTrimmed(Path.Combine(cardPath, "device", "vendor"));
            if (!string.Equals(vendor, "0x8086", StringComparison.OrdinalIgnoreCase))
                continue;

            var deviceCode = ReadTrimmed(Path.Combine(cardPath, "device", "device"));
            var driver = ResolveDriverName(Path.Combine(cardPath, "device", "driver"));
            devices.Add(new()
            {
                DeviceId = $"intel:{cardName}",
                DeviceType = "integrated_gpu",
                Vendor = "Intel",
                Model = string.IsNullOrWhiteSpace(deviceCode) ? cardName : deviceCode,
                DriverVersion = driver,
                Runtime = "drm"
            });
        }

        return devices;
    }

    private static string ResolveCpuModel()
    {
        if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
        {
            var line = File.ReadLines("/proc/cpuinfo")
                .FirstOrDefault(static item => item.StartsWith("model name", StringComparison.OrdinalIgnoreCase));
            var separator = line?.IndexOf(':') ?? -1;
            if (separator >= 0)
                return line![(separator + 1)..].Trim();
        }

        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")?.Trim()
               ?? RuntimeInformation.ProcessArchitecture.ToString();
    }

    private static long ResolveMemoryBytes()
    {
        var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (available > 0 && available < long.MaxValue)
            return available;

        if (OperatingSystem.IsLinux() && File.Exists("/proc/meminfo"))
        {
            var line = File.ReadLines("/proc/meminfo")
                .FirstOrDefault(static item => item.StartsWith("MemTotal:", StringComparison.Ordinal));
            var token = line?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
            if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kib))
                return kib * 1024;
        }

        return 1;
    }

    private static string Run(string fileName, string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new()
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            if (!process.Start())
                return "";
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(3000))
            {
                process.Kill(entireProcessTree: true);
                return "";
            }
            return process.ExitCode == 0 ? output : "";
        }
        catch
        {
            return "";
        }
    }

    private static string ReadTrimmed(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch
        {
            return "";
        }
    }

    private static string? ResolveDriverName(string path)
    {
        try
        {
            return Directory.Exists(path) || File.Exists(path)
                ? Path.GetFileName(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar))
                : null;
        }
        catch
        {
            return null;
        }
    }
}
