using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

internal sealed record NvidiaGpuInfo(string Name, int VramMiB, string DriverVersion);

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
    /// Conservative auto-tuning for llama.cpp server.
    /// Mix CPU+GPU happens when ngl > 0 AND the runtime supports GPU (CUDA/Vulkan build).
    /// For small VRAM GPUs (e.g., 4GB), keep batch/ngl conservative to avoid OOM and stalls.
    /// </summary>
    public static (int threads, int batch, int ngl) ComputeAutoTuning(NvidiaGpuInfo? nvidia)
    {
        var cpu = Environment.ProcessorCount;

        // Defaults (safe)
        var threads = Math.Clamp(cpu - 2, 4, 10);
        var batch = 128;
        var ngl = 0;

        if (nvidia is not null && nvidia.VramMiB > 0)
        {
            // VRAM heuristics:
            // 4GB: aim for modest offload + moderate batch (fits and avoids stalls)
            if (nvidia.VramMiB <= 5120) { batch = 192; ngl = 24; threads = Math.Clamp(cpu - 2, 4, 8); }
            else if (nvidia.VramMiB <= 7168) { batch = 256; ngl = 32; threads = Math.Clamp(cpu - 3, 4, 8); }
            else if (nvidia.VramMiB <= 10240) { batch = 384; ngl = 48; threads = Math.Clamp(cpu - 3, 4, 10); }
            else if (nvidia.VramMiB <= 14336) { batch = 512; ngl = 72; threads = Math.Clamp(cpu - 4, 4, 10); }
            else { batch = 768; ngl = 99; threads = Math.Clamp(cpu - 4, 4, 10); }
        }

        return (threads, batch, ngl);
    }
}
