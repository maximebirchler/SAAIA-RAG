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
}
