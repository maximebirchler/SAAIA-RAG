using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

internal sealed record NvidiaGpuInfo(string Name, int VramMiB, string DriverVersion);

/// <summary>
/// GPU detection + conservative auto-tuning for llama.cpp server.
/// Notes:
/// - GPU usage requires a GPU-enabled llama.cpp runtime (CUDA/Vulkan). A CPU-only binary will never use the GPU.
/// - "CPU+GPU conjoint" is achieved by setting -ngl (n_gpu_layers) > 0 with a GPU-enabled runtime.
/// </summary>
internal static class GpuDetector
{
    // Backward-compatible helpers (some call sites expect these signatures)
    public static Task<bool> HasNvidiaGpuAsync() => Task.FromResult(TryGetNvidia(out _));
    public static Task<bool> HasNvidiaGpuAsync(CancellationToken ct) => Task.FromResult(TryGetNvidia(out _));

    public static bool TryGetNvidia(out NvidiaGpuInfo info)
    {
        info = new NvidiaGpuInfo("Unknown NVIDIA GPU", 0, "");

        try
        {
            // Example output:
            // "NVIDIA GeForce RTX 3070, 8192, 551.86"
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
    /// Conservative tuning, based on CPU cores + NVIDIA VRAM (MiB).
    /// - threads: CPU threads for compute
    /// - batch: token batch
    /// - ngl: GPU layers to offload (only effective with GPU runtime)
    /// </summary>
    public static (int threads, int batch, int ngl) ComputeAutoTuning(NvidiaGpuInfo? nvidia)
    {
        var cpu = Environment.ProcessorCount;

        // Safe defaults
        var threads = Math.Clamp(cpu - 2, 4, 12);
        var batch = 256;
        var ngl = 0;

        if (nvidia is not null && nvidia.VramMiB > 0)
        {
            // Very conservative presets. Low VRAM GPUs must keep batch low.
            if (nvidia.VramMiB >= 16384) { batch = 1024; ngl = 99; threads = Math.Clamp(cpu - 4, 4, 10); }
            else if (nvidia.VramMiB >= 12288) { batch = 768; ngl = 80; threads = Math.Clamp(cpu - 4, 4, 10); }
            else if (nvidia.VramMiB >= 8192) { batch = 512; ngl = 60; threads = Math.Clamp(cpu - 3, 4, 10); }
            else if (nvidia.VramMiB >= 6144) { batch = 384; ngl = 40; threads = Math.Clamp(cpu - 2, 4, 10); }
            else if (nvidia.VramMiB >= 4096) { batch = 128; ngl = 16; threads = Math.Clamp(cpu - 2, 4, 10); } // e.g. Quadro P520 4GB
            else { batch = 96; ngl = 12; threads = Math.Clamp(cpu - 2, 4, 10); }
        }

        return (threads, batch, ngl);
    }
}
