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
    /// <summary>
    /// Backward-compatible helper (older code expects this symbol).
    /// Returns true if NVIDIA GPU is detected via nvidia-smi.
    /// </summary>
    public static Task<bool> HasNvidiaGpuAsync()
        => Task.FromResult(TryGetNvidia(out _));

    /// <summary>
    /// Backward-compatible helper (some call sites pass a CancellationToken).
    /// Token is currently ignored because detection is fast and local.
    /// </summary>
    public static Task<bool> HasNvidiaGpuAsync(CancellationToken ct)
        => Task.FromResult(TryGetNvidia(out _));

    /// <summary>
    /// Try detect an NVIDIA GPU using nvidia-smi (name + total VRAM + driver version).
    /// </summary>
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

            // Example: "NVIDIA GeForce RTX 3070, 8192, 551.86"
            var parts = line.Split(',')
                            .Select(p => p.Trim())
                            .ToArray();

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
    /// Heuristic auto-tuning for llama.cpp server:
    /// - threads: CPU threads for compute
    /// - batch: token batch size
    /// - ngl: number of layers to offload to GPU (requires CUDA/Vulkan build)
    /// </summary>
    public static (int threads, int batch, int ngl) ComputeAutoTuning(NvidiaGpuInfo? nvidia)
    {
        var cpu = Environment.ProcessorCount;

        // Conservative defaults (safe)
        var threads = Math.Clamp(cpu - 2, 4, 12);
        var batch = 256;
        var ngl = 0;

        if (nvidia is not null && nvidia.VramMiB > 0)
        {
            // VRAM heuristic (very conservative)
            if (nvidia.VramMiB >= 16384) { batch = 1024; ngl = 99; threads = Math.Clamp(cpu - 4, 4, 10); }
            else if (nvidia.VramMiB >= 12288) { batch = 768; ngl = 80; threads = Math.Clamp(cpu - 4, 4, 10); }
            else if (nvidia.VramMiB >= 8192) { batch = 512; ngl = 60; threads = Math.Clamp(cpu - 3, 4, 10); }
            else if (nvidia.VramMiB >= 6144) { batch = 384; ngl = 40; threads = Math.Clamp(cpu - 2, 4, 10); }
            else { batch = 256; ngl = 20; threads = Math.Clamp(cpu - 2, 4, 10); }
        }

        return (threads, batch, ngl);
    }
}
