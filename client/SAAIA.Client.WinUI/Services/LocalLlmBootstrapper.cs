using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

/// <summary>
/// Ensures the local LLM runtime requirements are present:
/// - llama.cpp server executable (downloaded automatically if missing)
/// - GGUF model file (downloaded automatically)
///
/// This service does NOT start the process; that's handled by LlamaCppProcessManager.
/// </summary>
internal sealed class LocalLlmBootstrapper
{
    private sealed record ModelSpec(string ModelId, string SourceRef, string File, string Url, string? Sha256Hex);

    private readonly DownloadManager _dl = new();
    private readonly LlamaCppReleaseDownloader _llamaDl = new();

    private static ModelSpec? Spec(string modelIdOrFileName)
    {
        var item = ModelCatalogStore.TryGetItem(modelIdOrFileName);
        if (item is null)
            return null;

        var url = ModelCatalogStore.TryBuildDownloadUrl(item);
        if (string.IsNullOrWhiteSpace(url))
            return null;

        return new ModelSpec(item.ModelId, item.SourceRef, item.FileName, url, item.ChecksumSha256);
    }

    public async Task<(bool ok, string message, IReadOnlyList<string> installedPaths)> EnsureAsync(
        AppSettings s,
        bool force,
        IProgress<DownloadManager.ProgressInfo>? progress,
        CancellationToken ct)
    {
        if (!s.UseLocalLlm)
            return (true, "LLM disabled (search-only).", Array.Empty<string>());

        var mode = (s.LlmMode ?? "embedded").Trim().ToLowerInvariant();
        if (mode != "embedded")
            return (true, $"LLM mode is '{mode}' (no embedded bootstrap).", Array.Empty<string>());

        var installed = new List<string>();

        // Detect GPU (NVIDIA / AMD / Intel / iGPU)
        var bestGpu = await GpuDetector.TryGetBestGpuAsync(ct).ConfigureAwait(false);
        var hasNvidia = bestGpu?.Vendor == GpuVendor.Nvidia && bestGpu.DedicatedVramBytes > 0 && !bestGpu.IsIntegrated;
        var hasDiscreteGpu = bestGpu is not null && bestGpu.DedicatedVramBytes > 0 && !bestGpu.IsIntegrated;

        // Auto-select model based on VRAM thresholds (only if user didn't pick a custom model).
        await AutoSelectModelIfNeededAsync(s, bestGpu, ct).ConfigureAwait(false);

        // 0) Auto-detect an existing model in %LOCALAPPDATA%\SAAIA\Models (helps after manual copy).
        TryAutoDetectExistingModel(s);

        var catalogPolicyViolation = GetSelectedClientModelPolicyViolation(s);
        if (!string.IsNullOrWhiteSpace(catalogPolicyViolation))
            return (false, catalogPolicyViolation, installed);

        // 1) Resolve executable (installer should ship it).
        ResolveExePath(s, hasNvidia);

        // Prefer GPU runtime when available:
        // - NVIDIA -> CUDA (fallback Vulkan)
        // - AMD/Intel discrete -> Vulkan
        if (hasDiscreteGpu && (force || IsCpuRuntimePath(s.LlamaExePath) || string.IsNullOrWhiteSpace(s.LlamaExePath)))
        {
            if (hasNvidia)
            {
                // 1) Try CUDA
                if (!File.Exists(LlamaCppReleaseDownloader.CudaServerExePath))
                {
                    var (okCuda, _, _) = await _llamaDl.EnsureWindowsCudaAsync(progress, minBuild: null, ct).ConfigureAwait(false);
                    _ = okCuda; // best-effort
                }
                if (File.Exists(LlamaCppReleaseDownloader.CudaServerExePath))
                {
                    s.LlamaExePath = LlamaCppReleaseDownloader.CudaServerExePath;
                    installed.Add(s.LlamaExePath);
                }
            }

            // 2) Vulkan (works for NVIDIA too; required for AMD/Intel discrete)
            if (IsCpuRuntimePath(s.LlamaExePath) || string.IsNullOrWhiteSpace(s.LlamaExePath) || !File.Exists(s.LlamaExePath))
            {
                if (!File.Exists(LlamaCppReleaseDownloader.VulkanServerExePath))
                {
                    var (okVk, _, _) = await _llamaDl.EnsureWindowsVulkanAsync(progress, minBuild: null, ct).ConfigureAwait(false);
                    _ = okVk;
                }
                if (File.Exists(LlamaCppReleaseDownloader.VulkanServerExePath))
                {
                    s.LlamaExePath = LlamaCppReleaseDownloader.VulkanServerExePath;
                    installed.Add(s.LlamaExePath);
                }
            }
        }

        // If still missing, attempt CPU runtime.
        if (string.IsNullOrWhiteSpace(s.LlamaExePath) || !File.Exists(s.LlamaExePath) || force)
        {
            var useProvisionedPlanForExe = Provisioning.TryGetDownloadAssets(out var assets2, out var auto2) && auto2 &&
                                           assets2.Any(a => a.TargetRelativePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

            if (!useProvisionedPlanForExe)
            {
                if (hasNvidia)
                {
                    var (okCuda, _, cudaPath) = await _llamaDl.EnsureWindowsCudaAsync(progress, minBuild: null, ct).ConfigureAwait(false);
                    if (okCuda && !string.IsNullOrWhiteSpace(cudaPath) && File.Exists(cudaPath))
                    {
                        s.LlamaExePath = cudaPath;
                        installed.Add(cudaPath);
                    }
                }

                if (string.IsNullOrWhiteSpace(s.LlamaExePath) || !File.Exists(s.LlamaExePath))
                {
                    var (okExe, _, exePath) = await _llamaDl.EnsureWindowsCpuAsync(progress, minBuild: null, ct).ConfigureAwait(false);
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
            var useProvisionedPlan = Provisioning.TryGetDownloadAssets(out var assets, out var auto) && auto;

            if (useProvisionedPlan)
            {
                var paths = await _dl.EnsureAssetsAsync(assets.ToList(), progress, ct).ConfigureAwait(false);
                installed.AddRange(paths);
            }
            else
            {
                // Build candidates in priority order
                var candidates = GetModelCandidates(bestGpu, s.ModelId);
                if (candidates.Count == 0)
                    return (false, "No governed model candidate is currently downloadable for the active policy.", installed);

                var picked = await PickFirstReachableSpecAsync(candidates, ct).ConfigureAwait(false);
                if (picked is null)
                    return (false, "No reachable model file found on Hugging Face for the current pack.", installed);

                // CDC v3.1 LLM-008: SHA-256 must be known for all pack models.
                // Until the checksums are computed and populated, log a warning so it appears in support bundles.
                if (picked.Sha256Hex is null)
                    ClientLog.Warn($"[Bootstrap] No SHA-256 known for '{picked.File}' — integrity check skipped.");
                ClientLog.Info($"LLM bootstrap: downloading model '{picked.File}' from source '{picked.SourceRef}'.");

                var modelAsset = new DownloadManager.AssetSpec(
                    Id: picked.File,
                    Url: picked.Url,
                    TargetRelativePath: $"Models/{picked.File}",
                    Sha256Hex: picked.Sha256Hex);

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

        var runtimeCompatibility = await EnsureRuntimeCompatibilityAsync(
            s,
            bestGpu,
            hasNvidia,
            progress,
            ct).ConfigureAwait(false);
        if (!runtimeCompatibility.ok)
            return (false, runtimeCompatibility.message, installed);
        if (!string.IsNullOrWhiteSpace(runtimeCompatibility.exePath) && File.Exists(runtimeCompatibility.exePath))
        {
            s.LlamaExePath = runtimeCompatibility.exePath;
            if (!installed.Contains(runtimeCompatibility.exePath, StringComparer.OrdinalIgnoreCase))
                installed.Add(runtimeCompatibility.exePath);
        }

        // Apply conservative auto-tuning based on hardware.
        ApplyAutoTuningFlags(s, bestGpu);
        try
        {
            await GovernanceArtifactStore.EnsureDefaultArtifactsAsync(s, ct: ct).ConfigureAwait(false);
            var hardwareChange = await HardwareProbeService.DetectHardwareChangeAsync(ct: ct).ConfigureAwait(false);
            if (hardwareChange.RequiresRequalification)
            {
                ClientLog.Warn(
                    "[Governance] Requalification required: "
                    + $"{hardwareChange.Reason} "
                    + $"(stored={hardwareChange.StoredFingerprint ?? "none"}, current={hardwareChange.CurrentFingerprint ?? "none"}).");
            }

            if (s.QualifiedProfile is not null)
            {
                var profileDrift = RequalificationTriggerService.EvaluateProfileDrift(s);
                if (profileDrift.Required)
                {
                    ClientLog.Warn($"[Governance] Requalification required: {profileDrift.Reason}.");
                }

                var batteryPolicy = await BatteryPolicyStore.EvaluateAsync(s.QualifiedProfile, ct: ct).ConfigureAwait(false);
                if (batteryPolicy.RequiresRequalification)
                {
                    ClientLog.Warn(
                        "[Governance] Requalification required: "
                        + $"{batteryPolicy.Reason} "
                        + $"(recommendedProfile={batteryPolicy.RecommendedProfileRef ?? "none"}, "
                        + $"idleTimeoutSeconds={batteryPolicy.EffectiveIdleTimeoutSeconds?.ToString() ?? "unknown"}).");
                }

                var warmupRead = await GovernanceArtifactStore.ReadAsync<WarmupResultsArtifact>(
                    GovernanceArtifactStore.WarmupResultsFile,
                    ct: ct).ConfigureAwait(false);
                if (warmupRead.Status == GovernanceArtifactReadStatus.Ok && warmupRead.Value is not null)
                {
                    var warmupSignals = RequalificationTriggerService.EvaluateWarmupHistory(
                        warmupRead.Value.Items,
                        s.QualifiedProfile.ProfileId);
                    if (warmupSignals.Required)
                        ClientLog.Warn($"[Governance] Requalification required: {warmupSignals.Reason}.");
                }
            }
        }
        catch (Exception ex)
        {
            ClientLog.Warn($"LLM governance artifacts unavailable: {ex.GetType().Name}: {ex.Message}");
        }

        // Ensure minimal runtime flags
        s.ManageLocalLlmProcess = true;
        s.AutoStartOnConnect = true;
        s.EagerLoad = true;
        s.UseLocalLlm = true;
        s.LlmMode = "embedded";
        s.Host = "127.0.0.1";
        s.Port = 1234;
        s.Save();

        return (true, "OK", installed);
    }

    private async Task<(bool ok, string message, string? exePath)> EnsureRuntimeCompatibilityAsync(
        AppSettings s,
        GpuInfo? gpu,
        bool hasNvidia,
        IProgress<DownloadManager.ProgressInfo>? progress,
        CancellationToken ct)
    {
        var model = ResolveCurrentModel(s);
        if (model is null)
            return (true, "runtime_compatibility_skipped", s.LlamaExePath);

        var runtimeId = RequalificationTriggerService.DetectRuntimeKey(s.LlamaExePath);
        var runtimeBuild = RuntimeCompatibilityPolicyStore.ReadRuntimeBuild(s.LlamaExePath);
        var decision = RuntimeCompatibilityPolicyStore.Evaluate(runtimeId, runtimeBuild, model);
        if (decision.Compatible)
            return (true, "compatible", s.LlamaExePath);

        ClientLog.Warn($"[RuntimeCompatibility] {decision.Reason} (model={model.ModelId}, runtime={runtimeId}, currentBuild={decision.CurrentBuild ?? "unknown"}, requiredBuild={decision.RequiredBuild ?? "none"}).");

        if (!decision.RequiresUpgrade || string.IsNullOrWhiteSpace(decision.RequiredBuild))
            return (false, $"Runtime incompatible for model '{model.DisplayName}' ({decision.Reason}).", null);

        if (string.Equals(runtimeId, "llama.cpp-cuda", StringComparison.OrdinalIgnoreCase))
        {
            if (!hasNvidia)
                return (false, $"Runtime upgrade required for '{model.DisplayName}', but no NVIDIA backend is available.", null);

            return await _llamaDl.EnsureWindowsCudaAsync(progress, decision.RequiredBuild, ct).ConfigureAwait(false);
        }

        if (string.Equals(runtimeId, "llama.cpp-cpu", StringComparison.OrdinalIgnoreCase))
            return await _llamaDl.EnsureWindowsCpuAsync(progress, decision.RequiredBuild, ct).ConfigureAwait(false);

        if (string.Equals(runtimeId, "llama.cpp-vulkan", StringComparison.OrdinalIgnoreCase))
            return await _llamaDl.EnsureWindowsVulkanAsync(progress, decision.RequiredBuild, ct).ConfigureAwait(false);

        return (false, $"Runtime '{runtimeId}' incompatible with '{model.DisplayName}' and cannot be upgraded automatically.", null);
    }

    private static IReadOnlyList<ModelSpec> GetModelCandidates(GpuInfo? gpu, string? requestedModelId)
    {
        var candidates = new List<ModelSpec>();

        void AddCandidate(string? modelIdOrFileName)
        {
            var spec = Spec(modelIdOrFileName ?? string.Empty);
            if (spec is null)
                return;

            if (candidates.Any(existing => string.Equals(existing.File, spec.File, StringComparison.OrdinalIgnoreCase)))
                return;

            candidates.Add(spec);
        }

        if (!string.IsNullOrWhiteSpace(requestedModelId))
            AddCandidate(requestedModelId);

        foreach (var collectionKey in GetPreferredCollectionKeys(gpu))
        {
            foreach (var item in ModelCatalogStore.GetCollectionModels(collectionKey))
                AddCandidate(item.ModelId);
        }

        if (candidates.Count == 0)
        {
            foreach (var fallback in ModelCatalogStore.GetInstallerVisibleClientModels())
                AddCandidate(fallback.ModelId);
        }

        return candidates;
    }

    private static async Task<ModelSpec?> PickFirstReachableSpecAsync(IReadOnlyList<ModelSpec> candidates, CancellationToken ct)
    {
        foreach (var c in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (await UrlExistsAsync(c.Url, ct).ConfigureAwait(false))
                return c;
        }
        return null;
    }

    private static async Task<bool> UrlExistsAsync(string url, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
            {
                Timeout = TimeSpan.FromSeconds(10)
            };

            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)resp.StatusCode >= 200 && (int)resp.StatusCode < 400) return true;

            // Some hosts don't support HEAD; try a small GET range.
            using var get = new HttpRequestMessage(HttpMethod.Get, url);
            get.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var resp2 = await http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return (int)resp2.StatusCode >= 200 && (int)resp2.StatusCode < 400;
        }
        catch
        {
            return false;
        }
    }

    private static async Task AutoSelectModelIfNeededAsync(AppSettings s, GpuInfo? gpu, CancellationToken ct)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(s.ModelPath) && File.Exists(s.ModelPath))
                return;

            // If the user set a custom ModelId that's not in our pack, keep it.
            if (!string.IsNullOrWhiteSpace(s.ModelId) && !IsPackModel(s.ModelId) && ModelCatalogStore.IsDiscoveryAllowed())
                return;

            var candidates = GetModelCandidates(gpu, s.ModelId);
            var picked = await PickFirstReachableSpecAsync(candidates, ct).ConfigureAwait(false) ?? candidates.First();

            var modelsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SAAIA", "Models");

            var desiredPath = Path.Combine(modelsDir, picked.File);

            if (!string.Equals(s.ModelId, picked.File, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(s.ModelPath, desiredPath, StringComparison.OrdinalIgnoreCase))
            {
                s.ModelId = picked.File;
                s.ModelPath = desiredPath;
                ClientLog.Info($"AutoModel: selected '{picked.File}' (source={picked.SourceRef}, vramMiB={(gpu?.DedicatedVramMiB ?? 0)}, integrated={(gpu?.IsIntegrated ?? true)})." );
            }
        }
        catch
        {
            // ignore
        }
    }

    private static bool IsPackModel(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return false;
        var canonical = ModelCatalogStore.ResolveCanonicalModelId(modelId);
        return ModelCatalogStore.IsInstallerVisibleClientModel(canonical ?? modelId);
    }

    private static IEnumerable<string> GetPreferredCollectionKeys(GpuInfo? gpu)
    {
        var vramMiB = gpu?.DedicatedVramMiB ?? 0;
        var integrated = gpu?.IsIntegrated ?? true;

        if (integrated || vramMiB <= 0)
        {
            yield return "4gb-vram";
            yield return "client-baseline";
            yield break;
        }

        if (vramMiB <= 3584)
        {
            yield return "4gb-vram";
            yield return "client-baseline";
            yield break;
        }

        if (vramMiB <= 8192)
        {
            yield return "8gb-vram";
            yield return "4gb-vram";
            yield return "client-baseline";
            yield break;
        }

        yield return "10gb-vram";
        yield return "8gb-vram";
        yield return "client-baseline";
    }

    private static void ResolveExePath(AppSettings s, bool hasNvidiaGpu)
    {
        if (!string.IsNullOrWhiteSpace(s.LlamaExePath) && File.Exists(s.LlamaExePath))
            return;

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

        var baseDir = AppContext.BaseDirectory;
        var llmDir = Path.Combine(baseDir, "llm");

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

            var candidates = Directory.EnumerateFiles(modelsDir, "*.gguf", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => new FileInfo(f).Length)
                .ToList();

            if (candidates.Count == 0)
                return;

            // Prefer an exact pack match if present
            foreach (var f in candidates)
            {
                var file = Path.GetFileName(f);
                if (IsPackModel(file))
                {
                    s.ModelPath = f;
                    s.ModelId = file;
                    return;
                }
            }

            if (!ModelCatalogStore.IsDiscoveryAllowed())
                return;

            // Otherwise keep the largest model (best quality)
            var best = candidates[0];
            s.ModelPath = best;
            s.ModelId = Path.GetFileName(best);
        }
        catch
        {
            // ignore
        }
    }

    private static void ApplyInstalledAssetsToSettings(AppSettings s, List<string> installed)
    {
        try
        {
            // If we downloaded a model, point ModelPath to it.
            var model = installed.FirstOrDefault(p => p.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(model) && File.Exists(model))
            {
                s.ModelPath = model;
                s.ModelId = Path.GetFileName(model);
            }

            // If we downloaded a llama-server exe, point LlamaExePath to it.
            var exe = installed.FirstOrDefault(p => p.EndsWith("llama-server.exe", StringComparison.OrdinalIgnoreCase) ||
                                                    p.EndsWith("llama-server-cuda.exe", StringComparison.OrdinalIgnoreCase) ||
                                                    p.EndsWith("llama-server-cu12.exe", StringComparison.OrdinalIgnoreCase) ||
                                                    p.EndsWith("llama-server-cublas.exe", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
                s.LlamaExePath = exe;
        }
        catch
        {
            // ignore
        }
    }

    private static void ApplyAutoTuningFlags(AppSettings s, GpuInfo? gpu)
    {
        try
        {
            var isGpuRuntime = IsGpuRuntimePath(s.LlamaExePath);
            // Pass ModelPath so ngl is read from llm.block_count in GGUF metadata (CDC v3.1 LLM-005).
            var (threads, batch, ngl) = GpuDetector.ComputeAutoTuning(gpu, s.ModelPath);
            var profile = GetApplicableQualifiedProfile(s);
            var ctxSize = profile?.CtxSize;
            var ubatch = profile?.UbatchSize ?? s.UbatchSize;
            var threadsBatch = profile?.ThreadsBatch ?? s.ThreadsBatch;
            var flashAttn = profile?.FlashAttn ?? (s.FlashAttn != false);
            var mlock = profile?.Mlock == true;
            var runtimeId = RequalificationTriggerService.DetectRuntimeKey(s.LlamaExePath);
            var model = ResolveCurrentModel(s);
            if (RuntimeCompatibilityPolicyStore.GetForcedFlashAttn(runtimeId, model, gpu) is { } forcedFlashAttn)
                flashAttn = forcedFlashAttn;
            if (profile is not null)
            {
                threads = profile.Threads;
                batch = profile.BatchSize;
                ngl = profile.Ngl;
            }

            if (!isGpuRuntime) ngl = 0;

            var extra = (s.ExtraArgs ?? "").Trim();

            if (ctxSize is int ctx && !ContainsArg(extra, "--ctx-size") && !ContainsArg(extra, "-c"))
                extra = AppendArg(extra, "--ctx-size", ctx.ToString());

            if (!ContainsArg(extra, "-t") && !ContainsArg(extra, "--threads"))
                extra = AppendArg(extra, "-t", threads.ToString());

            if (!ContainsArg(extra, "-b") && !ContainsArg(extra, "--batch") && !ContainsArg(extra, "--batch-size"))
                extra = AppendArg(extra, "-b", batch.ToString());

            if (isGpuRuntime && !ContainsArg(extra, "-ngl") && !ContainsArg(extra, "--n-gpu-layers"))
                extra = AppendArg(extra, "-ngl", ngl.ToString());

            // ubatch-size (CDC v3.1 LLM-010)
            if (!ContainsArg(extra, "--ubatch-size") && !ContainsArg(extra, "-ub"))
                extra = AppendArg(extra, "--ubatch-size", ubatch.ToString());

            // threads-batch (CDC v3.1 LLM-010)
            if (!ContainsArg(extra, "--threads-batch") && !ContainsArg(extra, "-tb"))
                extra = AppendArg(extra, "--threads-batch", threadsBatch.ToString());

            if (mlock && !ContainsArg(extra, "--mlock"))
                extra = AppendFlag(extra, "--mlock");

            // flash-attn: CUDA builds only. The bundled llama-server build expects an
            // explicit value ("on"/"off"), as confirmed by the local Phase 0 bench.
            var isCuda = IsGpuRuntimePath(s.LlamaExePath) &&
                         s.LlamaExePath.Contains("cuda", StringComparison.OrdinalIgnoreCase);
            if (isCuda)
            {
                if (!ContainsArg(extra, "--flash-attn") && !ContainsArg(extra, "-fa"))
                    extra = AppendArg(extra, "--flash-attn", flashAttn ? "on" : "off");
            }

            s.ExtraArgs = extra;
        }
        catch
        {
            // Never fail bootstrap due to tuning.
        }
    }

    private static QualifiedProfile? GetApplicableQualifiedProfile(AppSettings s)
    {
        var profile = s.QualifiedProfile;
        if (profile is null)
            return null;

        var runtime = RequalificationTriggerService.DetectRuntimeKey(s.LlamaExePath);
        if (!string.Equals(runtime, profile.Runtime, StringComparison.OrdinalIgnoreCase))
            return null;

        var currentModelId =
            ModelCatalogStore.ResolveCanonicalModelId(s.ModelId)
            ?? ModelCatalogStore.ResolveCanonicalModelId(string.IsNullOrWhiteSpace(s.ModelPath) ? null : Path.GetFileName(s.ModelPath))
            ?? s.ModelId;

        return string.Equals(currentModelId, profile.ModelId, StringComparison.OrdinalIgnoreCase)
            ? profile
            : null;
    }

    private static ModelCatalogItem? ResolveCurrentModel(AppSettings s)
    {
        var currentModelId =
            ModelCatalogStore.ResolveCanonicalModelId(s.ModelId)
            ?? ModelCatalogStore.ResolveCanonicalModelId(string.IsNullOrWhiteSpace(s.ModelPath) ? null : Path.GetFileName(s.ModelPath))
            ?? s.ModelId;

        return ModelCatalogStore.TryGetItem(currentModelId)
            ?? ModelCatalogStore.TryGetItem(string.IsNullOrWhiteSpace(s.ModelPath) ? null : Path.GetFileName(s.ModelPath));
    }

    private static string? GetSelectedClientModelPolicyViolation(AppSettings s)
    {
        var selectedModel =
            ModelCatalogStore.ResolveCanonicalModelId(s.ModelId)
            ?? ModelCatalogStore.ResolveCanonicalModelId(string.IsNullOrWhiteSpace(s.ModelPath) ? null : Path.GetFileName(s.ModelPath))
            ?? (string.IsNullOrWhiteSpace(s.ModelPath) ? s.ModelId : Path.GetFileName(s.ModelPath));

        return ModelCatalogStore.GetClientCatalogPolicyViolation(selectedModel);
    }

    private static bool IsCpuRuntimePath(string exePath)
        => string.IsNullOrWhiteSpace(exePath) || exePath.Contains("cpu", StringComparison.OrdinalIgnoreCase);

    private static bool IsGpuRuntimePath(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        var p = exePath.ToLowerInvariant();
        return p.Contains("cuda") || p.Contains("cu12") || p.Contains("cublas") || p.Contains("vulkan");
    }

    private static bool ContainsArg(string extra, string token)
        => extra.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static string AppendArg(string extra, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(extra))
            return $"{key} {value}";
        return extra + " " + key + " " + value;
    }

    private static string AppendFlag(string extra, string key)
    {
        if (string.IsNullOrWhiteSpace(extra))
            return key;
        return extra + " " + key;
    }
}
