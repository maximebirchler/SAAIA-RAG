using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

internal sealed record NvidiaGpuInfo(string Name, int VramMiB, string DriverVersion);

internal enum GpuVendor
{
    Unknown = 0,
    Nvidia = 1,
    Amd = 2,
    Intel = 3,
}

internal sealed record GpuInfo(
    GpuVendor Vendor,
    string Name,
    long DedicatedVramBytes,
    bool IsIntegrated,
    string DetectionSource)
{
    public int DedicatedVramMiB => DedicatedVramBytes > 0 ? (int)(DedicatedVramBytes / 1024 / 1024) : 0;
}

internal static class GpuDetector
{
    // Backward-compatible helpers (some call sites pass CancellationToken)
    public static Task<bool> HasNvidiaGpuAsync() => Task.FromResult(TryGetNvidia(out _));
    public static Task<bool> HasNvidiaGpuAsync(CancellationToken ct) => Task.FromResult(TryGetNvidia(out _));

    public static bool TryGetNvidia(out NvidiaGpuInfo info)
    {
        info = new NvidiaGpuInfo("Unknown NVIDIA GPU", 0, "");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null) return false;

            var line = proc.StandardOutput.ReadLine();
            proc.WaitForExit(3000);

            if (string.IsNullOrWhiteSpace(line)) return false;

            var parts = line.Split(',').Select(p => p.Trim()).ToArray();
            if (parts.Length < 2) return false;

            var name = parts[0];
            _ = int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var vramMiB);
            var drv = parts.Length >= 3 ? parts[2] : "";

            info = new NvidiaGpuInfo(name, vramMiB, drv);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Try to detect the "best" GPU on the machine.
    /// - NVIDIA: uses nvidia-smi (reliable VRAM).
    /// - Otherwise: uses Win32_VideoController via PowerShell (CIM) to get Name + AdapterRAM + PNPDeviceID.
    /// Notes:
    /// - iGPU/APU often reports shared memory; we treat integrated GPUs as DedicatedVramBytes=0 (conservative).
    /// </summary>
    public static async Task<GpuInfo?> TryGetBestGpuAsync(CancellationToken ct)
    {
        // 1) NVIDIA
        if (TryGetNvidia(out var n) && n.VramMiB > 0)
        {
            return new GpuInfo(
                Vendor: GpuVendor.Nvidia,
                Name: n.Name,
                DedicatedVramBytes: (long)n.VramMiB * 1024L * 1024L,
                IsIntegrated: false,
                DetectionSource: "nvidia-smi");
        }

        // 2) CIM (PowerShell)
        try
        {
            // JSON list of video controllers
            // Using PowerShell avoids System.Management dependency.
            var cmd = "Get-CimInstance Win32_VideoController | Select-Object Name,AdapterRAM,PNPDeviceID | ConvertTo-Json";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{cmd}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            var json = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(json)) return null;

            using var doc = JsonDocument.Parse(json);

            // Could be a single object or an array
            var items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToArray()
                : new[] { doc.RootElement };

            if (items.Length == 0) return null;

            GpuInfo? best = null;

            foreach (var el in items)
            {
                var name = el.TryGetProperty("Name", out var pn) ? (pn.GetString() ?? "") : "";
                var pnp = el.TryGetProperty("PNPDeviceID", out var pp) ? (pp.GetString() ?? "") : "";

                long adapterRam = 0;
                if (el.TryGetProperty("AdapterRAM", out var pr))
                {
                    if (pr.ValueKind == JsonValueKind.Number)
                    {
                        _ = pr.TryGetInt64(out adapterRam);
                    }
                    else if (pr.ValueKind == JsonValueKind.String)
                    {
                        _ = long.TryParse(pr.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out adapterRam);
                    }
                }

                var vendor = ParseVendorFromPnp(pnp, name);
                var integrated = IsIntegratedHeuristic(vendor, name);

                var dedicated = integrated ? 0 : Math.Max(0, adapterRam);

                var info = new GpuInfo(vendor, string.IsNullOrWhiteSpace(name) ? "Unknown GPU" : name, dedicated, integrated, "cim");

                // Choose the controller with highest dedicated VRAM.
                if (best is null || info.DedicatedVramBytes > best.DedicatedVramBytes)
                    best = info;
            }

            return best;
        }
        catch
        {
            return null;
        }
    }

    private static GpuVendor ParseVendorFromPnp(string pnpDeviceId, string name)
    {
        var p = (pnpDeviceId ?? "").ToUpperInvariant();
        var n = (name ?? "").ToUpperInvariant();

        if (p.Contains("VEN_10DE") || n.Contains("NVIDIA")) return GpuVendor.Nvidia;
        if (p.Contains("VEN_1002") || p.Contains("VEN_1022") || n.Contains("AMD") || n.Contains("RADEON")) return GpuVendor.Amd;
        if (p.Contains("VEN_8086") || n.Contains("INTEL")) return GpuVendor.Intel;

        return GpuVendor.Unknown;
    }

    private static bool IsIntegratedHeuristic(GpuVendor vendor, string name)
    {
        var n = (name ?? "").ToUpperInvariant();

        // Intel iGPU : UHD / Iris / HD Graphics are typically integrated; Arc is discrete.
        if (vendor == GpuVendor.Intel)
        {
            if (n.Contains("ARC")) return false;
            return true;
        }

        // AMD iGPU / APU : often "Radeon(TM) Graphics" without RX/XT.
        if (vendor == GpuVendor.Amd)
        {
            if (n.Contains("RADEON(TM) GRAPHICS")) return true;
            if (n.Contains("VEGA")) return true;
            if (n.Contains("RX") || n.Contains("XT") || n.Contains("PRO")) return false;
        }

        return false;
    }

    /// <summary>
    /// Conservative auto-tuning for llama.cpp server.
    /// Mix CPU+GPU happens when ngl > 0 AND the runtime supports GPU (CUDA/Vulkan build).
    /// </summary>
    public static (int threads, int batch, int ngl) ComputeAutoTuning(NvidiaGpuInfo? nvidia)
    {
        // Keep old behavior for backward compatibility
        var cpu = Environment.ProcessorCount;

        var threads = Math.Clamp(cpu - 2, 4, 10);
        var batch = 128;
        var ngl = 0;

        if (nvidia is not null && nvidia.VramMiB > 0)
        {
            if (nvidia.VramMiB <= 5120) { batch = 192; ngl = 24; threads = Math.Clamp(cpu - 2, 4, 8); }
            else if (nvidia.VramMiB <= 7168) { batch = 256; ngl = 32; threads = Math.Clamp(cpu - 3, 4, 8); }
            else if (nvidia.VramMiB <= 10240) { batch = 384; ngl = 48; threads = Math.Clamp(cpu - 3, 4, 10); }
            else if (nvidia.VramMiB <= 14336) { batch = 512; ngl = 72; threads = Math.Clamp(cpu - 4, 4, 10); }
            else { batch = 768; ngl = 99; threads = Math.Clamp(cpu - 4, 4, 10); }
        }

        return (threads, batch, ngl);
    }

    /// <summary>
    /// Generic auto-tuning for non-NVIDIA GPUs (Vulkan) and for unified handling.
    /// </summary>
    public static (int threads, int batch, int ngl) ComputeAutoTuning(GpuInfo? gpu)
    {
        var cpu = Environment.ProcessorCount;

        var threads = Math.Clamp(cpu - 2, 4, 10);
        var batch = 128;
        var ngl = 0;

        var vramMiB = gpu?.DedicatedVramMiB ?? 0;

        if (gpu is not null && !gpu.IsIntegrated && vramMiB > 0)
        {
            if (vramMiB <= 5120) { batch = 192; ngl = 24; threads = Math.Clamp(cpu - 2, 4, 8); }
            else if (vramMiB <= 7168) { batch = 256; ngl = 32; threads = Math.Clamp(cpu - 3, 4, 8); }
            else if (vramMiB <= 10240) { batch = 384; ngl = 48; threads = Math.Clamp(cpu - 3, 4, 10); }
            else if (vramMiB <= 14336) { batch = 512; ngl = 72; threads = Math.Clamp(cpu - 4, 4, 10); }
            else { batch = 768; ngl = 99; threads = Math.Clamp(cpu - 4, 4, 10); }
        }

        return (threads, batch, ngl);
    }
}
