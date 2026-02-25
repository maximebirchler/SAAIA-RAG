using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

/// <summary>
/// Ensures the local LLM runtime requirements are present:
/// - llama.cpp server executable (ideally shipped by installer; can be downloaded via provisioning plan)
/// - GGUF model file (downloaded automatically)
///
/// This service does NOT start the process; that's handled by LlamaCppProcessManager.
/// </summary>
internal sealed class LocalLlmBootstrapper
{
    // Default model used in infra/scripts/llm/install-llm.ps1
    private const string DefaultModelRepo = "bartowski/Mistral-7B-Instruct-v0.3-GGUF";
    private const string DefaultModelFile = "Mistral-7B-Instruct-v0.3-IQ3_M.gguf";
    private const string DefaultModelSha256 = "4ea14c5a6c787ac2703505f04a4ee746f746d1ace3ffd907af28f6f179e6b224";

    private readonly DownloadManager _dl = new();
    private readonly LlamaCppReleaseDownloader _llamaDl = new();

    public async Task<(bool ok, string message, IReadOnlyList<string> installedPaths)> EnsureAsync(
        AppSettings s,
        bool force,
        IProgress<DownloadManager.ProgressInfo>? progress,
        CancellationToken ct)
    {
        if (!s.UseLocalLlm)
            return (true, "LLM disabled (search-only).", Array.Empty<string>());

        // Prefer embedded mode; docker/external are handled elsewhere.
        var mode = (s.LlmMode ?? "embedded").Trim().ToLowerInvariant();
        if (mode != "embedded")
            return (true, $"LLM mode is '{mode}' (no embedded bootstrap).", Array.Empty<string>());

        var installed = new List<string>();

        // 0) Auto-detect an existing model in %LOCALAPPDATA%\SAAIA\Models (helps after manual copy).
        TryAutoDetectExistingModel(s);

        // 1) Resolve executable (installer should ship it).
        var hasNvidia = await GpuDetector.HasNvidiaGpuAsync(ct).ConfigureAwait(false);
        ResolveExePath(s, hasNvidia);

        // Prefer GPU runtime when available (NVIDIA -> CUDA, fallback Vulkan).
        // Important: even if a CPU runtime is already installed, we upgrade to GPU runtime when a compatible GPU is present.
        if (hasNvidia && (force || IsCpuRuntimePath(s.LlamaExePath) || string.IsNullOrWhiteSpace(s.LlamaExePath)))
        {
            // 1) Try CUDA
            if (!File.Exists(LlamaCppReleaseDownloader.CudaServerExePath))
            {
                var (okCuda, _, _) = await _llamaDl.EnsureWindowsCudaAsync(progress, ct).ConfigureAwait(false);
                _ = okCuda; // best-effort
            }
            if (File.Exists(LlamaCppReleaseDownloader.CudaServerExePath))
            {
                s.LlamaExePath = LlamaCppReleaseDownloader.CudaServerExePath;
                installed.Add(s.LlamaExePath);
            }

            // 2) Fallback Vulkan (still uses GPU via Vulkan backend)
            if (IsCpuRuntimePath(s.LlamaExePath) || string.IsNullOrWhiteSpace(s.LlamaExePath) || !File.Exists(s.LlamaExePath))
            {
                if (!File.Exists(LlamaCppReleaseDownloader.VulkanServerExePath))
                {
                    var (okVk, _, _) = await _llamaDl.EnsureWindowsVulkanAsync(progress, ct).ConfigureAwait(false);
                    _ = okVk;
                }
                if (File.Exists(LlamaCppReleaseDownloader.VulkanServerExePath))
                {
                    s.LlamaExePath = LlamaCppReleaseDownloader.VulkanServerExePath;
                    installed.Add(s.LlamaExePath);
                }
            }
        }


        // If still missing, attempt to download a CPU runtime from official llama.cpp releases.
        // (This makes the MVP fully automatic without Docker; installer can later ship the exe.)
        if (string.IsNullOrWhiteSpace(s.LlamaExePath) || !File.Exists(s.LlamaExePath) || force)
        {
            // If provisioning provides a downloads plan, we prefer it (it may include custom binaries).
            // Otherwise, fallback to downloading llama.cpp windows CPU runtime.
            var useProvisionedPlanForExe = Provisioning.TryGetDownloadAssets(out var assets2, out var auto2) && auto2 &&
                                           assets2.Any(a => a.TargetRelativePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

            if (!useProvisionedPlanForExe)
            {
                // Prefer CUDA runtime when NVIDIA is available (CPU+GPU conjoint via -ngl).
                if (hasNvidia)
                {
                    var (okCuda, _, cudaPath) = await _llamaDl.EnsureWindowsCudaAsync(progress, ct).ConfigureAwait(false);
                    if (okCuda && !string.IsNullOrWhiteSpace(cudaPath) && File.Exists(cudaPath))
                    {
                        s.LlamaExePath = cudaPath;
                        installed.Add(cudaPath);
                    }
                }

                // Fallback to CPU runtime
                if (string.IsNullOrWhiteSpace(s.LlamaExePath) || !File.Exists(s.LlamaExePath))
                {
                    var (okExe, _, exePath) = await _llamaDl.EnsureWindowsCpuAsync(progress, ct).ConfigureAwait(false);
                    if (okExe && !string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
                    {
                        s.LlamaExePath = exePath;
                        installed.Add(exePath);
                    }
                }
            }
        }

        // 2) Resolve model path (download if missing)
        TryAutoDetectExistingModel(s);

        if (string.IsNullOrWhiteSpace(s.ModelPath) || !File.Exists(s.ModelPath) || force)
        {
            // Prefer provisioning downloads plan if present and enabled.
            var useProvisionedPlan = Provisioning.TryGetDownloadAssets(out var assets, out var auto) && auto;

            if (useProvisionedPlan)
            {
                var paths = await _dl.EnsureAssetsAsync(assets.ToList(), progress, ct).ConfigureAwait(false);
                installed.AddRange(paths);
            }
            else
            {
                // Default: download model from HF (public)
                var url = $"https://huggingface.co/{DefaultModelRepo}/resolve/main/{DefaultModelFile}";
                var modelAsset = new DownloadManager.AssetSpec(
                    Id: "model",
                    Url: url,
                    TargetRelativePath: $"Models/{DefaultModelFile}",
                    Sha256Hex: DefaultModelSha256);

                var paths = await _dl.EnsureAssetsAsync(new[] { modelAsset }, progress, ct).ConfigureAwait(false);
                installed.AddRange(paths);
            }

            ApplyInstalledAssetsToSettings(s, installed);
            s.Save();
        }

        if (string.IsNullOrWhiteSpace(s.LlamaExePath) || !File.Exists(s.LlamaExePath))
            return (false, "Server executable not found (llama.cpp).", installed);

        if (string.IsNullOrWhiteSpace(s.ModelPath) || !File.Exists(s.ModelPath))
            return (false, "Model file not found (.gguf).", installed);

        // Apply conservative auto-tuning (threads/batch/ngl) based on hardware.
        // - Always applies threads+batch if not already specified by ExtraArgs.
        // - Applies -ngl only when a GPU-enabled runtime is selected (CUDA/Vulkan); otherwise forces -ngl 0.
        ApplyAutoTuningFlags(s, hasNvidia);

        // Ensure minimal runtime flags
        s.ManageLocalLlmProcess = true;
        s.AutoStartOnConnect = true;
        s.UseLocalLlm = true;
        s.LlmMode = "embedded";
        s.Host = "127.0.0.1";
        s.Port = 1234;
        s.Save();

        return (true, "OK", installed);
    }

    private static void ResolveExePath(AppSettings s, bool hasNvidiaGpu)
    {
        // If already set and exists, keep.
        if (!string.IsNullOrWhiteSpace(s.LlamaExePath) && File.Exists(s.LlamaExePath))
            return;

        // Prefer downloaded runtime under %LOCALAPPDATA%\SAAIA\llm\runtime (MVP auto).
        // If NVIDIA is detected and a CUDA runtime was downloaded, use it.
        if (hasNvidiaGpu && File.Exists(LlamaCppReleaseDownloader.CudaServerExePath))
        {
            s.LlamaExePath = LlamaCppReleaseDownloader.CudaServerExePath;
            return;
        }

        if (File.Exists(LlamaCppReleaseDownloader.VulkanServerExePath))
        {
            s.LlamaExePath = LlamaCppReleaseDownloader.VulkanServerExePath;
            return;
        }

        if (File.Exists(LlamaCppReleaseDownloader.CpuServerExePath))
        {
            s.LlamaExePath = LlamaCppReleaseDownloader.CpuServerExePath;
            return;
        }

        // Prefer shipped binaries under app directory.
        var baseDir = AppContext.BaseDirectory;
        var llmDir = Path.Combine(baseDir, "llm");

        // GPU auto-select if a GPU binary is shipped AND NVIDIA detected.
        var gpuExeCandidates = new[]
        {
            Path.Combine(llmDir, "llama-server-cuda.exe"),
            Path.Combine(llmDir, "llama-server-cu12.exe"),
            Path.Combine(llmDir, "llama-server-cublas.exe")
        };

        var cpuExeCandidates = new[]
        {
            Path.Combine(llmDir, "llama-server.exe"),
            Path.Combine(baseDir, "llama-server.exe")
        };

        // Try GPU first (optional)
        if (hasNvidiaGpu)
        {
            var gpuExe = gpuExeCandidates.FirstOrDefault(File.Exists);
            if (!string.IsNullOrWhiteSpace(gpuExe))
            {
                s.LlamaExePath = gpuExe;
                return;
            }
        }

        var cpuExe = cpuExeCandidates.FirstOrDefault(File.Exists);
        if (!string.IsNullOrWhiteSpace(cpuExe))
        {
            s.LlamaExePath = cpuExe;
            return;
        }
    }

    private static void TryAutoDetectExistingModel(AppSettings s)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(s.ModelPath) && File.Exists(s.ModelPath))
                return;

            var modelsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SAAIA", "Models");

            if (!Directory.Exists(modelsDir))
                return;

            var gguf = Directory.GetFiles(modelsDir, "*.gguf", SearchOption.TopDirectoryOnly)
                                .OrderByDescending(File.GetLastWriteTimeUtc)
                                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(gguf) && File.Exists(gguf))
            {
                s.ModelPath = gguf;
                s.ModelId = Path.GetFileName(gguf);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void ApplyInstalledAssetsToSettings(AppSettings s, IReadOnlyList<string> installed)
    {
        var exe = installed.FirstOrDefault(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        var gguf = installed.FirstOrDefault(p => p.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
            s.LlamaExePath = exe;

        if (!string.IsNullOrWhiteSpace(gguf) && File.Exists(gguf))
        {
            s.ModelPath = gguf;
            s.ModelId = Path.GetFileName(gguf);
        }

        if (!string.IsNullOrWhiteSpace(s.LlamaExePath) && !string.IsNullOrWhiteSpace(s.ModelPath))
        {
            s.ManageLocalLlmProcess = true;
            s.AutoStartOnConnect = true;
            s.UseLocalLlm = true;
            s.LlmMode = "embedded";

            s.Host = "127.0.0.1";
            s.Port = 1234;
        }
    }

    // === M6 GPU helpers (auto-upgrade + safe autotuning) ===
    private static bool IsCpuRuntimePath(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return true;
        return exePath.Contains(System.IO.Path.Combine("llm", "runtime", "win-cpu-x64"), StringComparison.OrdinalIgnoreCase)
            || (exePath.EndsWith("llama-server.exe", StringComparison.OrdinalIgnoreCase) && exePath.Contains("win-cpu", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsGpuRuntimePath(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        return exePath.Contains(System.IO.Path.Combine("llm", "runtime", "win-cuda-x64"), StringComparison.OrdinalIgnoreCase)
            || exePath.Contains(System.IO.Path.Combine("llm", "runtime", "win-vulkan-x64"), StringComparison.OrdinalIgnoreCase)
            || exePath.Contains("cuda", StringComparison.OrdinalIgnoreCase)
            || exePath.Contains("vulkan", StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyAutoTuningFlags(AppSettings s, bool hasNvidia)
    {
        // Goal: CPU+GPU mixed usage when GPU runtime is selected.
        // We only add flags if user did not already specify them in ExtraArgs.
        try
        {
            NvidiaGpuInfo? nvidia = null;
            if (hasNvidia && GpuDetector.TryGetNvidia(out var info))
                nvidia = info;

            var isGpuRuntime = IsGpuRuntimePath(s.LlamaExePath);

            var (threads, batch, ngl) = GpuDetector.ComputeAutoTuning(nvidia);

            // GPU offload only makes sense if we are using a GPU-enabled runtime (CUDA/Vulkan build).
            if (!isGpuRuntime) ngl = 0;

            var extra = (s.ExtraArgs ?? "").Trim();

            if (!ContainsArg(extra, "-t") && !ContainsArg(extra, "--threads"))
                extra = AppendArg(extra, "-t", threads.ToString());

            if (!ContainsArg(extra, "-b") && !ContainsArg(extra, "--batch") && !ContainsArg(extra, "--batch-size"))
                extra = AppendArg(extra, "-b", batch.ToString());

            // -ngl => mixed CPU+GPU (n_gpu_layers). With 4GB VRAM, we keep it conservative.
            if (!ContainsArg(extra, "-ngl") && !ContainsArg(extra, "--n-gpu-layers"))
                extra = AppendArg(extra, "-ngl", ngl.ToString());

            s.ExtraArgs = extra;
        }
        catch
        {
            // Never fail bootstrap due to tuning.
        }
    }

    private static bool ContainsArg(string extra, string token)
        => extra.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static string AppendArg(string extra, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(extra))
            return $"{key} {value}";
        return extra + " " + key + " " + value;
    }
}

