using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

/// <summary>
/// Minimal GPU detection without extra dependencies.
/// - If nvidia-smi is available and returns GPUs, we treat it as "NVIDIA GPU present".
/// - Otherwise, we default to CPU.
///
/// This is intentionally conservative: the app must always work on CPU.
/// </summary>
internal static class GpuDetector
{
    public static async Task<bool> HasNvidiaGpuAsync(CancellationToken ct)
    {
        try
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "-L",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            if (!p.Start()) return false;

            // Hard timeout (we don't want to hang startup)
            var completed = await Task.Run(() => p.WaitForExit(1200), ct).ConfigureAwait(false);
            if (!completed) { try { p.Kill(); } catch { } return false; }

            var stdout = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            if (p.ExitCode != 0) return false;

            return stdout.IndexOf("GPU", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch
        {
            return false;
        }
    }
}
