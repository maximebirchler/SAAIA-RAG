param()

$ErrorActionPreference = "Stop"

function Backup-File($path) {
  if (Test-Path $path) {
    $ts = Get-Date -Format "yyyyMMdd_HHmmss"
    Copy-Item $path "$path.bak_$ts" -Force
  }
}

function Write-FileUtf8($path, $content) {
  $dir = Split-Path $path -Parent
  if ($dir -and !(Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
  Set-Content -Path $path -Value $content -Encoding UTF8
}

$repo = Resolve-Path (Join-Path $PSScriptRoot "..\..")
Set-Location $repo

# 1) Replace GpuDetector.cs
$gpuDetectorPath = "client\SAAIA.Client.WinUI\Services\GpuDetector.cs"
Backup-File $gpuDetectorPath
Write-FileUtf8 $gpuDetectorPath @"
using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace SAAIA.Client.WinUI.Services;

internal sealed record NvidiaGpuInfo(string Name, int VramMiB, string DriverVersion);

internal static class GpuDetector
{
    public static bool TryGetNvidia(out NvidiaGpuInfo info)
    {
        info = new NvidiaGpuInfo("Unknown NVIDIA GPU", 0, "");

        try
        {
            // 1) Quick existence check
            var p1 = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(p1);
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
            var vramMiB = 0;
            _ = int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out vramMiB);
            var drv = parts.Length >= 3 ? parts[2] : "";

            info = new NvidiaGpuInfo(name, vramMiB, drv);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static (int threads, int batch, int ngl) ComputeAutoTuning(NvidiaGpuInfo? nvidia)
    {
        var cpu = Environment.ProcessorCount;

        // Conservative defaults (safe)
        var threads = Math.Clamp(cpu - 2, 4, 12);
        var batch = 256;
        var ngl = 0;

        if (nvidia is not null && nvidia.VramMiB > 0)
        {
            // If CUDA runtime exists, we’ll offload layers.
            // VRAM heuristic:
            // >= 16GB: big batch
            // >= 12GB: medium
            // >= 8GB: smaller
            // >= 6GB: minimal
            if (nvidia.VramMiB >= 16384) { batch = 1024; ngl = 99; threads = Math.Clamp(cpu - 4, 4, 10); }
            else if (nvidia.VramMiB >= 12288) { batch = 768; ngl = 80; threads = Math.Clamp(cpu - 4, 4, 10); }
            else if (nvidia.VramMiB >= 8192) { batch = 512; ngl = 60; threads = Math.Clamp(cpu - 3, 4, 10); }
            else if (nvidia.VramMiB >= 6144) { batch = 384; ngl = 40; threads = Math.Clamp(cpu - 2, 4, 10); }
            else { batch = 256; ngl = 20; threads = Math.Clamp(cpu - 2, 4, 10); }
        }

        return (threads, batch, ngl);
    }
}
"@

# 2) Patch LlamaCppProcessManager: add auto args builder + prefer cuda exe
$mgrPath = "client\SAAIA.Client.WinUI\Services\LlamaCppProcessManager.cs"
Backup-File $mgrPath
$content = Get-Content $mgrPath -Raw

if ($content -notmatch "BuildArgsAutoTune")
{
  $insert = @"

    internal static string BuildArgsAutoTune(string host, int port, string modelPath, int threads, int batch, int ngl)
    {
        // Note: -ngl enables GPU offload if runtime supports it (CUDA/Vulkan build).
        // Keep flags minimal/compatible.
        return $"--host {host} --port {port} --model `"{modelPath}`" -t {threads} -b {batch} -ngl {ngl}";
    }

    internal static string ResolveRuntimeExePath(string runtimeDir)
    {
        // Prefer CUDA runtime if present
        var cuda = System.IO.Path.Combine(runtimeDir, "llama-server-cuda.exe");
        if (System.IO.File.Exists(cuda)) return cuda;

        var cpu = System.IO.Path.Combine(runtimeDir, "llama-server.exe");
        return cpu;
    }
"@

  # insert before last closing brace of class
  $content = $content -replace "\r?\n}\r?\n\z", "$insert`n}`n"
  Set-Content -Path $mgrPath -Value $content -Encoding UTF8
}

# 3) Patch LocalLlmBootstrapper: compute tuning + start with args
$bootPath = "client\SAAIA.Client.WinUI\Services\LocalLlmBootstrapper.cs"
Backup-File $bootPath
$boot = Get-Content $bootPath -Raw

# We patch only if marker exists (to avoid breaking)
if ($boot -match "Proceeding with bootstrap" -and $boot -notmatch "ComputeAutoTuning")
{
  # Replace the place where args/exe are decided:
  # We look for a line that sets exePath/runtimeDir and inject tuning.
  $boot = $boot -replace "(?s)(var\s+runtimeDir\s*=.*?;\s*)(var\s+exePath\s*=.*?;\s*)", "`$1`n        NvidiaGpuInfo? nvidia = null;`n        if (GpuDetector.TryGetNvidia(out var ni)) nvidia = ni;`n        var (threads, batch, ngl) = GpuDetector.ComputeAutoTuning(nvidia);`n`n        var exePath = LlamaCppProcessManager.ResolveRuntimeExePath(runtimeDir);`n"

  # Replace any existing args build if it exists, otherwise inject near Start
  if ($boot -match "BuildArgsAutoTune" -eq $false)
  {
    # Find first StartAsync call and add args variable before it
    $boot = $boot -replace "(?s)(await\s+_processManager\.StartAsync\()(.*?)(\);\s*)", "var args = LlamaCppProcessManager.BuildArgsAutoTune(`"127.0.0.1`", 1234, modelPath, threads, batch, ngl);`n        await _processManager.StartAsync(exePath, args);"
  }
}

Set-Content -Path $bootPath -Value $boot -Encoding UTF8

Write-Host "OK - M6 GPU autotune patch applied."
