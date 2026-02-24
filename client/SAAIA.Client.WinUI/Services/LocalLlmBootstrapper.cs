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

        // 1) Resolve executable (installer should ship it).
        var hasNvidia = await GpuDetector.HasNvidiaGpuAsync(ct).ConfigureAwait(false);
        ResolveExePath(s, hasNvidia);

        // 2) Resolve model path (download if missing)
        var installed = new List<string>();

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
            return (false, "Server executable not found (llama.cpp). The installer must ship it, or provisioning downloads must include it.", installed);

        if (string.IsNullOrWhiteSpace(s.ModelPath) || !File.Exists(s.ModelPath))
            return (false, "Model file not found (.gguf).", installed);

        return (true, "OK", installed);
    }

    private static void ResolveExePath(AppSettings s, bool hasNvidiaGpu)
    {
        // If already set and exists, keep.
        if (!string.IsNullOrWhiteSpace(s.LlamaExePath) && File.Exists(s.LlamaExePath))
            return;

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
