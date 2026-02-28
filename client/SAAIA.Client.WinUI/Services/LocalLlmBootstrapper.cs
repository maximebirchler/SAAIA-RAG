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
    // Default embedded model pack (auto-selected by VRAM)
// - <~5GB VRAM or CPU-only: Qwen 3B Q4 (fast + small)
// - <~7GB VRAM: Qwen 3B Q6 (better quality, still reasonable)
// - >=~8GB VRAM: Mistral 7B IQ3_M (best quality in this pack)
private const string MistralRepo = "bartowski/Mistral-7B-Instruct-v0.3-GGUF";
private const string MistralFile = "Mistral-7B-Instruct-v0.3-IQ3_M.gguf";
// SHA256 is known for this default (legacy); Qwen files are downloaded without hash enforcement for now.
private const string MistralSha256 = "4ea14c5a6c787ac2703505f04a4ee746f746d1ace3ffd907af28f6f179e6b224";

private const string QwenRepo = "bartowski/Qwen2.5-3B-Instruct-GGUF";
private const string QwenQ4File = "Qwen2.5-3B-Instruct-Q4_K_M.gguf";
private const string QwenQ6File = "Qwen2.5-3B-Instruct-Q6_K_L.gguf";

private sealed record ModelSpec(string Repo, string File, string? Sha256Hex);


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
        // Model auto-selection (VRAM-based) when the model isn't already configured/present.
        // This should trigger downloads automatically (and thus the UI popup) when necessary.
        AutoSelectModelIfNeeded(s);


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
                // Default: download model from HF (public) using our VRAM-based selection.
                // If the user configured a pack model id (Mistral/Qwen), respect it; otherwise select by hardware.
                var spec = SelectModelByHardware();
                if (!string.IsNullOrWhiteSpace(s.ModelId))
                {
                    if (string.Equals(s.ModelId, MistralFile, StringComparison.OrdinalIgnoreCase))
                        spec = new ModelSpec(MistralRepo, MistralFile, MistralSha256);
                    else if (string.Equals(s.ModelId, QwenQ6File, StringComparison.OrdinalIgnoreCase))
                        spec = new ModelSpec(QwenRepo, QwenQ6File, null);
                    else if (string.Equals(s.ModelId, QwenQ4File, StringComparison.OrdinalIgnoreCase))
                        spec = new ModelSpec(QwenRepo, QwenQ4File, null);
                }

                var url = $"https://huggingface.co/{spec.Repo}/resolve/main/{spec.File}";
                ClientLog.Info($"LLM bootstrap: downloading model '{spec.File}' from HF repo '{spec.Repo}'.");

                var modelAsset = new DownloadManager.AssetSpec(
                    Id: spec.File,
                    Url: url,
                    TargetRelativePath: $"Models/{spec.File}",
                    Sha256Hex: spec.Sha256Hex);

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

    
    private static ModelSpec SelectModelByHardware()
    {
        // Prefer NVIDIA VRAM data when available
        if (GpuDetector.TryGetNvidia(out var gpu) && gpu.VramMiB > 0)
        {
            // Thresholds are tuned for best quality while staying stable on common machines.
            // With 4GB VRAM (e.g. Quadro P520 ~4096 MiB), Qwen 3B Q6 usually works and gives a noticeable lift.
            // Q4 remains the safe fallback for very small GPUs or CPU-only.
            if (gpu.VramMiB <= 3584) return new ModelSpec(QwenRepo, QwenQ4File, null);
            if (gpu.VramMiB <= 7168) return new ModelSpec(QwenRepo, QwenQ6File, null);
            return new ModelSpec(MistralRepo, MistralFile, MistralSha256);
        }

        // CPU-only fallback
        return new ModelSpec(QwenRepo, QwenQ4File, null);
    }

    private static bool IsPackModel(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return false;
        return string.Equals(modelId, MistralFile, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(modelId, QwenQ4File, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(modelId, QwenQ6File, StringComparison.OrdinalIgnoreCase);
    }

    private static void AutoSelectModelIfNeeded(AppSettings s)
    {
        try
        {
            // If a real model path exists, keep it (user might have a custom model).
            if (!string.IsNullOrWhiteSpace(s.ModelPath) && File.Exists(s.ModelPath))
                return;

            // If the user set a custom ModelId that's not in our pack, keep it.
            if (!string.IsNullOrWhiteSpace(s.ModelId) && !IsPackModel(s.ModelId))
                return;

            var spec = SelectModelByHardware();
            var modelsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SAAIA", "Models");

            var desiredPath = Path.Combine(modelsDir, spec.File);

            // Only update settings if needed (avoid churn).
            if (!string.Equals(s.ModelId, spec.File, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(s.ModelPath, desiredPath, StringComparison.OrdinalIgnoreCase))
            {
                s.ModelId = spec.File;
                s.ModelPath = desiredPath;
                ClientLog.Info($"AutoModel: selected '{spec.File}' (repo={spec.Repo}).");
            }
        }
        catch
        {
            // ignore
        }
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

