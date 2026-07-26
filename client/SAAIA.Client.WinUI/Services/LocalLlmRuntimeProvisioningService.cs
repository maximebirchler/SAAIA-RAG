namespace SAAIA.Client.WinUI.Services;

internal sealed record LocalLlmRuntimeBackendPlan(
    string RuntimeId,
    string Backend,
    bool Required,
    string ApplicabilityReason);

internal sealed record LocalLlmRuntimeProvisioningResult(
    LocalLlmRuntimeBackendPlan Plan,
    bool Succeeded,
    string Message,
    string? ExecutablePath,
    string? MinimumBuild,
    bool TrackedForQualification);

internal sealed record LocalLlmRuntimeProvisioningOutcome(
    bool Succeeded,
    IReadOnlyList<LocalLlmRuntimeProvisioningResult> Results,
    IReadOnlyList<string> Reasons);

internal static class LocalLlmRuntimeProvisioningService
{
    internal delegate Task<(bool ok, string message, string? exePath)> Installer(
        string? minimumBuild,
        CancellationToken ct);

    public static IReadOnlyList<LocalLlmRuntimeBackendPlan> SelectApplicableBackends(
        IReadOnlyList<GpuInfo> gpus)
    {
        ArgumentNullException.ThrowIfNull(gpus);
        var plans = new List<LocalLlmRuntimeBackendPlan>
        {
            new(
                "llama.cpp-cpu",
                "cpu",
                Required: gpus.Count == 0,
                gpus.Count == 0
                    ? "cpu_only_machine"
                    : "portable_system_fallback")
        };
        if (gpus.Any(static gpu => gpu.Vendor == GpuVendor.Nvidia))
        {
            plans.Add(new LocalLlmRuntimeBackendPlan(
                "llama.cpp-cuda",
                "cuda",
                Required: false,
                "nvidia_adapter_detected"));
        }

        if (gpus.Any(static gpu => gpu.Vendor == GpuVendor.Intel))
        {
            plans.Add(new LocalLlmRuntimeBackendPlan(
                "llama.cpp-sycl",
                "sycl",
                Required: false,
                "intel_adapter_detected"));
        }

        if (gpus.Any(static gpu => gpu.Vendor == GpuVendor.Amd))
        {
            plans.Add(new LocalLlmRuntimeBackendPlan(
                "llama.cpp-hip",
                "hip",
                Required: false,
                "amd_adapter_detected"));
        }

        if (gpus.Count > 0)
        {
            plans.Add(new LocalLlmRuntimeBackendPlan(
                "llama.cpp-vulkan",
                "vulkan",
                Required: false,
                "windows_gpu_adapter_detected"));
        }

        return plans;
    }

    public static async Task<LocalLlmRuntimeProvisioningOutcome> ProvisionApplicableAsync(
        AppSettings settings,
        IReadOnlyList<GpuInfo> gpus,
        IProgress<DownloadManager.ProgressInfo>? progress = null,
        IReadOnlyDictionary<string, Installer>? installers = null,
        Func<string, string?, bool>? runtimeTracker = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(gpus);
        var downloader = new LlamaCppReleaseDownloader();
        installers ??= new Dictionary<string, Installer>(StringComparer.OrdinalIgnoreCase)
        {
            ["llama.cpp-cpu"] = (minimumBuild, token) =>
                downloader.EnsureWindowsCpuAsync(progress, minimumBuild, token),
            ["llama.cpp-cuda"] = (minimumBuild, token) =>
                downloader.EnsureWindowsCudaAsync(progress, minimumBuild, token),
            ["llama.cpp-sycl"] = (minimumBuild, token) =>
                downloader.EnsureWindowsSyclAsync(progress, minimumBuild, token),
            ["llama.cpp-hip"] = (minimumBuild, token) =>
                downloader.EnsureWindowsHipAsync(progress, minimumBuild, token),
            ["llama.cpp-vulkan"] = (minimumBuild, token) =>
                downloader.EnsureWindowsVulkanAsync(progress, minimumBuild, token)
        };
        runtimeTracker ??= LlamaCppReleaseDownloader.EnsureRuntimeTrackedForQualification;
        var model = ModelCatalogStore.TryGetItem(ResolveModelId(settings))
                    ?? ModelCatalogStore.TryGetItem(
                        string.IsNullOrWhiteSpace(settings.ModelPath)
                            ? null
                            : Path.GetFileName(settings.ModelPath));
        var results = new List<LocalLlmRuntimeProvisioningResult>();
        foreach (var plan in SelectApplicableBackends(gpus))
        {
            ct.ThrowIfCancellationRequested();
            if (!installers.TryGetValue(plan.RuntimeId, out var installer))
            {
                results.Add(new LocalLlmRuntimeProvisioningResult(
                    plan,
                    false,
                    "backend_installer_missing",
                    null,
                    null,
                    false));
                continue;
            }

            var existingPath = ResolveCurrentExecutable(plan.RuntimeId);
            var compatibility = RuntimeCompatibilityPolicyStore.Evaluate(
                plan.RuntimeId,
                RuntimeCompatibilityPolicyStore.ReadRuntimeBuild(existingPath),
                model);
            var minimumBuild = compatibility.RequiresUpgrade
                ? compatibility.RequiredBuild
                : null;
            var install = await installer(minimumBuild, ct).ConfigureAwait(false);
            var tracked = install.ok
                          && !string.IsNullOrWhiteSpace(install.exePath)
                          && File.Exists(install.exePath)
                          && runtimeTracker(
                              plan.RuntimeId,
                              install.exePath);
            results.Add(new LocalLlmRuntimeProvisioningResult(
                plan,
                install.ok && !string.IsNullOrWhiteSpace(install.exePath)
                           && File.Exists(install.exePath),
                install.message,
                install.exePath,
                minimumBuild,
                tracked));
        }

        var missingRequired = results
            .Where(static result => result.Plan.Required && !result.Succeeded)
            .Select(static result => "required_backend_failed:" + result.Plan.Backend)
            .ToArray();
        var successful = results.Count(static result => result.Succeeded);
        var reasons = new List<string>();
        reasons.AddRange(missingRequired);
        if (successful == 0)
            reasons.Add("no_runtime_backend_provisioned");
        foreach (var optionalFailure in results.Where(static result =>
                     !result.Plan.Required && !result.Succeeded))
        {
            reasons.Add("optional_backend_failed:" + optionalFailure.Plan.Backend);
        }

        return new LocalLlmRuntimeProvisioningOutcome(
            missingRequired.Length == 0 && successful > 0,
            results,
            reasons.Count == 0
                ? new[] { "applicable_runtime_backends_provisioned" }
                : reasons);
    }

    private static string ResolveModelId(AppSettings settings)
        => ModelCatalogStore.ResolveCanonicalModelId(settings.ModelId)
           ?? ModelCatalogStore.ResolveCanonicalModelId(
               string.IsNullOrWhiteSpace(settings.ModelPath)
                   ? null
                   : Path.GetFileName(settings.ModelPath))
           ?? settings.ModelId;

    private static string ResolveCurrentExecutable(string runtimeId)
        => runtimeId switch
        {
            "llama.cpp-cuda" => LlamaCppReleaseDownloader.CudaServerExePath,
            "llama.cpp-sycl" => LlamaCppReleaseDownloader.SyclServerExePath,
            "llama.cpp-hip" => LlamaCppReleaseDownloader.HipServerExePath,
            "llama.cpp-vulkan" => LlamaCppReleaseDownloader.VulkanServerExePath,
            _ => LlamaCppReleaseDownloader.CpuServerExePath
        };
}
