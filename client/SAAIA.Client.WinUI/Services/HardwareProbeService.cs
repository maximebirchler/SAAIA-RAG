using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

internal sealed record SystemMemorySnapshot(
    long TotalRamBytes,
    long AvailableRamBytes,
    string Source);

internal sealed record DxgiVideoMemorySnapshot(
    ulong BudgetBytes,
    ulong CurrentUsageBytes,
    ulong AvailableForReservationBytes,
    ulong CurrentReservationBytes,
    string Source);

internal sealed record PowerStatusSnapshot(
    bool? IsOnBattery,
    int? BatteryLifePercent,
    string Source);

internal sealed record VendorGpuTelemetrySnapshot(
    string Source,
    long? VramUsedMiB,
    long? VramTotalMiB,
    double? TemperatureC,
    double? CoreClockMHz,
    double? UtilizationPercent);

internal sealed record HardwareProbeChange(
    bool RequiresRequalification,
    string Reason,
    string? StoredFingerprint,
    string? CurrentFingerprint);

internal static class HardwareProbeService
{
    private static readonly SemaphoreSlim CacheLock = new(1, 1);
    private static HardwareProbeArtifact? CachedProbe;

    public static async Task<HardwareProbeArtifact> CaptureAsync(
        bool refresh = false,
        CancellationToken ct = default)
    {
        if (!refresh && CachedProbe is not null)
            return CachedProbe;

        await CacheLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!refresh && CachedProbe is not null)
                return CachedProbe;

            var gpu = await GpuDetector.TryGetBestGpuAsync(ct).ConfigureAwait(false);
            var driverVersion = TryGetDriverVersion(gpu);
            var memory = CaptureSystemMemory();
            var power = CapturePowerStatus();
            var dxgi = DxgiVideoMemoryProbe.TryQueryBestAdapter(gpu);
            var vendorTelemetry = CaptureVendorTelemetry(gpu);

            var probe = CreateArtifact(
                gpu,
                driverVersion,
                dxgi,
                memory,
                Environment.MachineName,
                Environment.ProcessorCount,
                Environment.Is64BitOperatingSystem,
                DateTimeOffset.UtcNow,
                power,
                vendorTelemetry);

            CachedProbe = probe;
            return probe;
        }
        catch (Exception ex)
        {
            return new HardwareProbeArtifact(
                GovernanceArtifactStore.HardwareProbeFile,
                "v3.1",
                "degraded",
                DateTimeOffset.UtcNow,
                ComputeFingerprint(Environment.MachineName, Environment.ProcessorCount, Environment.Is64BitOperatingSystem, null),
                new Dictionary<string, object?>
                {
                    ["error"] = ex.GetType().Name
                });
        }
        finally
        {
            CacheLock.Release();
        }
    }

    public static async Task<HardwareProbeChange> DetectHardwareChangeAsync(
        string? root = null,
        CancellationToken ct = default)
    {
        var stored = await GovernanceArtifactStore.ReadAsync<HardwareProbeArtifact>(
            GovernanceArtifactStore.HardwareProbeFile,
            root,
            ct).ConfigureAwait(false);
        var current = await CaptureAsync(refresh: true, ct).ConfigureAwait(false);

        if (stored.Status != GovernanceArtifactReadStatus.Ok || stored.Value is null)
        {
            return new HardwareProbeChange(
                RequiresRequalification: true,
                Reason: "hardware_probe_unavailable",
                StoredFingerprint: null,
                CurrentFingerprint: current.MachineFingerprint);
        }

        return Compare(stored.Value, current);
    }

    internal static HardwareProbeChange Compare(
        HardwareProbeArtifact stored,
        HardwareProbeArtifact current)
    {
        if (string.IsNullOrWhiteSpace(stored.MachineFingerprint))
        {
            return new HardwareProbeChange(
                true,
                "hardware_fingerprint_missing",
                stored.MachineFingerprint,
                current.MachineFingerprint);
        }

        var externalGpuChange = CompareExternalGpu(stored, current);
        if (externalGpuChange is not null)
            return externalGpuChange;

        if (!string.Equals(stored.MachineFingerprint, current.MachineFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return new HardwareProbeChange(
                true,
                "hardware_fingerprint_changed",
                stored.MachineFingerprint,
                current.MachineFingerprint);
        }

        var storedDriver = TryGetString(stored.Hardware, "gpuDriverVersion");
        var currentDriver = TryGetString(current.Hardware, "gpuDriverVersion");
        if (!string.IsNullOrWhiteSpace(storedDriver)
            && !string.IsNullOrWhiteSpace(currentDriver)
            && !string.Equals(storedDriver, currentDriver, StringComparison.OrdinalIgnoreCase))
        {
            return new HardwareProbeChange(
                true,
                $"gpu_driver_changed:{storedDriver}->{currentDriver}",
                stored.MachineFingerprint,
                current.MachineFingerprint);
        }

        return new HardwareProbeChange(
            false,
            "hardware_fingerprint_unchanged",
            stored.MachineFingerprint,
            current.MachineFingerprint);
    }

    internal static HardwareProbeArtifact CreateArtifact(
        GpuInfo? gpu,
        string? gpuDriverVersion,
        DxgiVideoMemorySnapshot? dxgi,
        SystemMemorySnapshot memory,
        string machineName,
        int processorCount,
        bool is64BitOperatingSystem,
        DateTimeOffset capturedAt,
        PowerStatusSnapshot? power = null,
        VendorGpuTelemetrySnapshot? vendorTelemetry = null)
    {
        var status = gpu is null
            ? "degraded"
            : dxgi is null && gpu.DedicatedVramBytes > 0
                ? "captured_without_dxgi"
                : "captured";

        var hardware = new Dictionary<string, object?>
        {
            ["cpuCount"] = processorCount,
            ["is64BitOperatingSystem"] = is64BitOperatingSystem,
            ["totalRamMiB"] = ToMiB(memory.TotalRamBytes),
            ["availableRamMiB"] = ToMiB(memory.AvailableRamBytes),
            ["systemMemorySource"] = memory.Source,
            ["isOnBattery"] = power?.IsOnBattery,
            ["batteryLifePercent"] = power?.BatteryLifePercent,
            ["powerStatusSource"] = power?.Source ?? "unavailable",
            ["gpuVendor"] = gpu?.Vendor.ToString().ToLowerInvariant(),
            ["gpuName"] = gpu?.Name,
            ["gpuDetectionSource"] = gpu?.DetectionSource,
            ["gpuDriverVersion"] = gpuDriverVersion,
            ["gpuDedicatedVramMiB"] = gpu?.DedicatedVramMiB ?? 0,
            ["gpuIsIntegrated"] = gpu?.IsIntegrated,
            ["gpuIsExternal"] = IsExternalGpu(gpu),
            ["gpuConnectionHint"] = GetGpuConnectionHint(gpu),
            ["dxgiStatus"] = dxgi is null ? "unavailable" : "captured",
            ["dxgiBudgetMiB"] = dxgi is null ? null : ToMiB(ClampToInt64(dxgi.BudgetBytes)),
            ["dxgiCurrentUsageMiB"] = dxgi is null ? null : ToMiB(ClampToInt64(dxgi.CurrentUsageBytes)),
            ["dxgiAvailableForReservationMiB"] = dxgi is null ? null : ToMiB(ClampToInt64(dxgi.AvailableForReservationBytes)),
            ["dxgiCurrentReservationMiB"] = dxgi is null ? null : ToMiB(ClampToInt64(dxgi.CurrentReservationBytes)),
            ["dxgiSource"] = dxgi?.Source,
            ["vendorTelemetryStatus"] = vendorTelemetry is null ? "unavailable" : "captured",
            ["vendorTelemetrySource"] = vendorTelemetry?.Source,
            ["gpuVramUsedMiB"] = vendorTelemetry?.VramUsedMiB,
            ["gpuVramTotalMiB"] = vendorTelemetry?.VramTotalMiB,
            ["gpuTemperatureC"] = vendorTelemetry?.TemperatureC,
            ["gpuCoreClockMHz"] = vendorTelemetry?.CoreClockMHz,
            ["gpuUtilizationPercent"] = vendorTelemetry?.UtilizationPercent
        };

        return new HardwareProbeArtifact(
            GovernanceArtifactStore.HardwareProbeFile,
            "v3.1",
            status,
            capturedAt,
            ComputeFingerprint(machineName, processorCount, is64BitOperatingSystem, gpu),
            hardware);
    }

    private static SystemMemorySnapshot CaptureSystemMemory()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var status = new MEMORYSTATUSEX
                {
                    dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
                };

                if (GlobalMemoryStatusEx(ref status))
                {
                    return new SystemMemorySnapshot(
                        ClampToInt64(status.ullTotalPhys),
                        ClampToInt64(status.ullAvailPhys),
                        "global_memory_status_ex");
                }
            }
            catch
            {
                // Fall through to managed telemetry below.
            }
        }

        try
        {
            var info = GC.GetGCMemoryInfo();
            var total = info.TotalAvailableMemoryBytes > 0 ? info.TotalAvailableMemoryBytes : 0;
            return new SystemMemorySnapshot(total, 0, "gc");
        }
        catch
        {
            return new SystemMemorySnapshot(0, 0, "unavailable");
        }
    }

    private static PowerStatusSnapshot CapturePowerStatus()
    {
        if (!OperatingSystem.IsWindows())
            return new PowerStatusSnapshot(null, null, "unsupported_os");

        try
        {
            if (!GetSystemPowerStatus(out var status))
                return new PowerStatusSnapshot(null, null, "unavailable");

            var isOnBattery = status.ACLineStatus switch
            {
                0 => true,
                1 => false,
                _ => (bool?)null
            };
            var batteryPercent = status.BatteryLifePercent <= 100
                ? status.BatteryLifePercent
                : (int?)null;

            return new PowerStatusSnapshot(isOnBattery, batteryPercent, "GetSystemPowerStatus");
        }
        catch
        {
            return new PowerStatusSnapshot(null, null, "unavailable");
        }
    }

    private static long ToMiB(long bytes) => bytes <= 0 ? 0 : bytes / 1024 / 1024;

    private static long ClampToInt64(ulong value)
        => value > long.MaxValue ? long.MaxValue : (long)value;

    private static VendorGpuTelemetrySnapshot? CaptureVendorTelemetry(GpuInfo? gpu)
    {
        if (gpu is null)
            return null;

        if (gpu.Vendor == GpuVendor.Amd)
        {
            var amdSmi = TryRunCli("amd-smi", "metric --json", TimeSpan.FromSeconds(3));
            var amdTelemetry = TryParseVendorTelemetryJson("amd-smi", amdSmi);
            if (amdTelemetry is not null)
                return amdTelemetry;

            var rocmSmi = TryRunCli("rocm-smi", "--showmeminfo vram --showtemp --showclocks --json", TimeSpan.FromSeconds(3));
            return TryParseVendorTelemetryJson("rocm-smi", rocmSmi);
        }

        if (gpu.Vendor == GpuVendor.Intel)
        {
            var xpuSmi = TryRunCli("xpu-smi", "dump -d 0 -m 0,1,2 -n 1 -j", TimeSpan.FromSeconds(3));
            return TryParseVendorTelemetryJson("intel-level-zero:xpu-smi", xpuSmi);
        }

        return null;
    }

    internal static VendorGpuTelemetrySnapshot? TryParseVendorTelemetryJson(string source, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            long? usedMiB = null;
            long? totalMiB = null;
            double? temperatureC = null;
            double? coreClockMHz = null;
            double? utilizationPercent = null;

            VisitTelemetryJson(
                doc.RootElement,
                ref usedMiB,
                ref totalMiB,
                ref temperatureC,
                ref coreClockMHz,
                ref utilizationPercent);

            return usedMiB is null
                   && totalMiB is null
                   && temperatureC is null
                   && coreClockMHz is null
                   && utilizationPercent is null
                ? null
                : new VendorGpuTelemetrySnapshot(
                    source,
                    usedMiB,
                    totalMiB,
                    temperatureC,
                    coreClockMHz,
                    utilizationPercent);
        }
        catch
        {
            return null;
        }
    }

    private static void VisitTelemetryJson(
        JsonElement element,
        ref long? usedMiB,
        ref long? totalMiB,
        ref double? temperatureC,
        ref double? coreClockMHz,
        ref double? utilizationPercent)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                CaptureTelemetryValue(
                    property.Name,
                    property.Value,
                    ref usedMiB,
                    ref totalMiB,
                    ref temperatureC,
                    ref coreClockMHz,
                    ref utilizationPercent);
                VisitTelemetryJson(
                    property.Value,
                    ref usedMiB,
                    ref totalMiB,
                    ref temperatureC,
                    ref coreClockMHz,
                    ref utilizationPercent);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                VisitTelemetryJson(
                    item,
                    ref usedMiB,
                    ref totalMiB,
                    ref temperatureC,
                    ref coreClockMHz,
                    ref utilizationPercent);
            }
        }
    }

    private static void CaptureTelemetryValue(
        string key,
        JsonElement value,
        ref long? usedMiB,
        ref long? totalMiB,
        ref double? temperatureC,
        ref double? coreClockMHz,
        ref double? utilizationPercent)
    {
        if (!TryGetDouble(value, out var number))
            return;

        var normalized = NormalizeTelemetryKey(key);
        if (usedMiB is null && (normalized.Contains("vramusedmib", StringComparison.Ordinal) || normalized.Contains("memoryusedmib", StringComparison.Ordinal)))
            usedMiB = (long)Math.Round(number);
        else if (totalMiB is null && (normalized.Contains("vramtotalmib", StringComparison.Ordinal) || normalized.Contains("memorytotalmib", StringComparison.Ordinal)))
            totalMiB = (long)Math.Round(number);
        else if (temperatureC is null && (normalized.Contains("temperaturec", StringComparison.Ordinal) || normalized.Contains("tempc", StringComparison.Ordinal) || normalized == "temperature"))
            temperatureC = number;
        else if (coreClockMHz is null && (normalized.Contains("coreclockmhz", StringComparison.Ordinal) || normalized.Contains("gfxclockmhz", StringComparison.Ordinal) || normalized.Contains("frequencymhz", StringComparison.Ordinal)))
            coreClockMHz = number;
        else if (utilizationPercent is null && (normalized.Contains("utilizationpercent", StringComparison.Ordinal) || normalized.Contains("gpuutil", StringComparison.Ordinal)))
            utilizationPercent = number;
    }

    private static bool TryGetDouble(JsonElement value, out double number)
    {
        if (value.ValueKind == JsonValueKind.Number)
            return value.TryGetDouble(out number);

        if (value.ValueKind == JsonValueKind.String)
            return double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);

        number = 0;
        return false;
    }

    private static string NormalizeTelemetryKey(string key)
        => new((key ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    private static string? TryRunCli(string fileName, string arguments, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return null;

            if (!proc.WaitForExit((int)Math.Max(1000, timeout.TotalMilliseconds)))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            return proc.ExitCode == 0
                ? proc.StandardOutput.ReadToEnd()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetDriverVersion(GpuInfo? gpu)
    {
        if (gpu?.Vendor != GpuVendor.Nvidia)
            return null;

        return GpuDetector.TryGetNvidia(out var nvidia) && !string.IsNullOrWhiteSpace(nvidia.DriverVersion)
            ? nvidia.DriverVersion
            : null;
    }

    private static HardwareProbeChange? CompareExternalGpu(
        HardwareProbeArtifact stored,
        HardwareProbeArtifact current)
    {
        var storedExternal = TryGetBool(stored.Hardware, "gpuIsExternal") == true;
        var currentExternal = TryGetBool(current.Hardware, "gpuIsExternal") == true;

        if (!storedExternal && !currentExternal)
            return null;

        var storedName = TryGetString(stored.Hardware, "gpuName");
        var currentName = TryGetString(current.Hardware, "gpuName");

        if (storedExternal && !currentExternal)
        {
            return new HardwareProbeChange(
                true,
                "external_gpu_disconnected",
                stored.MachineFingerprint,
                current.MachineFingerprint);
        }

        if (!storedExternal && currentExternal)
        {
            return new HardwareProbeChange(
                true,
                "external_gpu_connected",
                stored.MachineFingerprint,
                current.MachineFingerprint);
        }

        if (!string.Equals(storedName, currentName, StringComparison.OrdinalIgnoreCase))
        {
            return new HardwareProbeChange(
                true,
                "external_gpu_changed",
                stored.MachineFingerprint,
                current.MachineFingerprint);
        }

        return null;
    }

    private static bool IsExternalGpu(GpuInfo? gpu)
        => string.Equals(GetGpuConnectionHint(gpu), "external", StringComparison.Ordinal);

    private static string GetGpuConnectionHint(GpuInfo? gpu)
    {
        if (gpu is null)
            return "none";

        var pnp = (gpu.PnpDeviceId ?? string.Empty).ToUpperInvariant();
        var name = (gpu.Name ?? string.Empty).ToUpperInvariant();

        if (name.Contains("EGPU", StringComparison.Ordinal)
            || pnp.Contains("THUNDERBOLT", StringComparison.Ordinal)
            || pnp.Contains("USB4", StringComparison.Ordinal)
            || pnp.StartsWith("USB\\", StringComparison.Ordinal))
        {
            return "external";
        }

        if (pnp.StartsWith("PCI\\", StringComparison.Ordinal))
            return "pci";

        return "unknown";
    }

    private static string? TryGetString(IReadOnlyDictionary<string, object?> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            string s when !string.IsNullOrWhiteSpace(s) => s,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null
        };
    }

    private static bool? TryGetBool(IReadOnlyDictionary<string, object?> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ => null
        };
    }

    private static string ComputeFingerprint(
        string machineName,
        int processorCount,
        bool is64BitOperatingSystem,
        GpuInfo? gpu)
    {
        var text = string.Join(
            "|",
            machineName ?? string.Empty,
            processorCount.ToString(CultureInfo.InvariantCulture),
            is64BitOperatingSystem ? "x64" : "x86",
            gpu?.Vendor.ToString() ?? string.Empty,
            gpu?.Name ?? string.Empty,
            gpu?.DedicatedVramBytes.ToString(CultureInfo.InvariantCulture) ?? "0",
            gpu?.IsIntegrated.ToString(CultureInfo.InvariantCulture) ?? string.Empty);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}

internal static class DxgiVideoMemoryProbe
{
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const int SOk = 0;

    public static DxgiVideoMemorySnapshot? TryQueryBestAdapter(GpuInfo? preferredGpu)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        IDXGIFactory1? factory = null;
        try
        {
            var iid = typeof(IDXGIFactory1).GUID;
            var hr = CreateDXGIFactory1(ref iid, out factory);
            if (hr != SOk || factory is null)
                return null;

            IDXGIAdapter1? bestAdapter = null;
            DXGI_ADAPTER_DESC1 bestDesc = default;

            for (uint index = 0; ; index++)
            {
                IDXGIAdapter1? adapter = null;
                try
                {
                    hr = factory.EnumAdapters1(index, out adapter);
                    if (hr == DxgiErrorNotFound)
                        break;
                    if (hr != SOk || adapter is null)
                        continue;

                    adapter.GetDesc1(out var desc);
                    if (!IsBetterMatch(preferredGpu, desc, bestAdapter is null, bestDesc))
                    {
                        Marshal.FinalReleaseComObject(adapter);
                        continue;
                    }

                    if (bestAdapter is not null)
                        Marshal.FinalReleaseComObject(bestAdapter);

                    bestAdapter = adapter;
                    bestDesc = desc;
                }
                catch
                {
                    if (adapter is not null)
                        Marshal.FinalReleaseComObject(adapter);
                }
            }

            if (bestAdapter is null)
                return null;

            try
            {
                return TryQueryAdapter3(bestAdapter);
            }
            finally
            {
                Marshal.FinalReleaseComObject(bestAdapter);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (factory is not null)
                Marshal.FinalReleaseComObject(factory);
        }
    }

    private static bool IsBetterMatch(
        GpuInfo? preferredGpu,
        DXGI_ADAPTER_DESC1 candidate,
        bool noCurrent,
        DXGI_ADAPTER_DESC1 current)
    {
        if (noCurrent)
            return true;

        if (preferredGpu is not null)
        {
            var preferredName = Normalize(preferredGpu.Name);
            var candidateName = Normalize(candidate.Description);
            var currentName = Normalize(current.Description);
            var candidateMatches = candidateName.Contains(preferredName, StringComparison.Ordinal)
                || preferredName.Contains(candidateName, StringComparison.Ordinal);
            var currentMatches = currentName.Contains(preferredName, StringComparison.Ordinal)
                || preferredName.Contains(currentName, StringComparison.Ordinal);

            if (candidateMatches != currentMatches)
                return candidateMatches;
        }

        return candidate.DedicatedVideoMemory.ToUInt64() > current.DedicatedVideoMemory.ToUInt64();
    }

    private static DxgiVideoMemorySnapshot? TryQueryAdapter3(IDXGIAdapter1 adapter)
    {
        var unk = IntPtr.Zero;
        var adapter3Ptr = IntPtr.Zero;
        try
        {
            unk = Marshal.GetIUnknownForObject(adapter);
            var iid = typeof(IDXGIAdapter3).GUID;
            var hr = Marshal.QueryInterface(unk, ref iid, out adapter3Ptr);
            if (hr != SOk || adapter3Ptr == IntPtr.Zero)
                return null;

            var adapter3 = (IDXGIAdapter3)Marshal.GetObjectForIUnknown(adapter3Ptr);
            adapter3.QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP.Local, out var info);
            return new DxgiVideoMemorySnapshot(
                info.Budget,
                info.CurrentUsage,
                info.AvailableForReservation,
                info.CurrentReservation,
                "dxgi_query_video_memory_info");
        }
        catch
        {
            return null;
        }
        finally
        {
            if (adapter3Ptr != IntPtr.Zero)
                Marshal.Release(adapter3Ptr);
            if (unk != IntPtr.Zero)
                Marshal.Release(unk);
        }
    }

    private static string Normalize(string text)
        => new((text ?? string.Empty)
            .Where(static c => !char.IsWhiteSpace(c) && c != '\0')
            .Select(static c => char.ToUpperInvariant(c))
            .ToArray());

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IDXGIFactory1? ppFactory);

    [ComImport]
    [Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumAdapters(uint adapter, out IntPtr ppAdapter);
        void MakeWindowAssociation();
        void GetWindowAssociation();
        void CreateSwapChain();
        void CreateSoftwareAdapter();
        [PreserveSig]
        int EnumAdapters1(uint adapter, out IDXGIAdapter1? ppAdapter);
        void IsCurrent();
    }

    [ComImport]
    [Guid("29038f61-3839-4626-91fd-086879011a05")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumOutputs();
        void GetDesc();
        void CheckInterfaceSupport();
        void GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }

    [ComImport]
    [Guid("645967A4-1392-4310-A798-8053CE3E93FD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter3
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumOutputs();
        void GetDesc();
        void CheckInterfaceSupport();
        void GetDesc1(out DXGI_ADAPTER_DESC1 desc);
        void GetDesc2();
        void RegisterHardwareContentProtectionTeardownStatusEvent();
        void UnregisterHardwareContentProtectionTeardownStatus();
        void QueryVideoMemoryInfo(
            uint nodeIndex,
            DXGI_MEMORY_SEGMENT_GROUP memorySegmentGroup,
            out DXGI_QUERY_VIDEO_MEMORY_INFO videoMemoryInfo);
    }

    private enum DXGI_MEMORY_SEGMENT_GROUP
    {
        Local = 0,
        NonLocal = 1
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_QUERY_VIDEO_MEMORY_INFO
    {
        public ulong Budget;
        public ulong CurrentUsage;
        public ulong AvailableForReservation;
        public ulong CurrentReservation;
    }
}
