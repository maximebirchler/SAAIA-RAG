using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

internal sealed record LocalLlmRuntimeDiagnostics(
    string RuntimeId,
    string RuntimeLabel,
    string? ActiveBuild,
    string? RequiredBuild,
    bool UpgradeRequired,
    string CompatibilityReason,
    string? ActiveState,
    string? PreviousBuild,
    DateTimeOffset? ActivatedAtUtc,
    DateTimeOffset? QualifiedAtUtc,
    string? ActiveExePath,
    string ActiveManifestPath,
    string? ModelId,
    string? ModelFamily,
    string? QualifiedProfileId,
    WarmupGateStatus? LatestWarmupStatus,
    string? LatestWarmupReason,
    bool? ForcedFlashAttn);

internal static class LocalLlmRuntimeDiagnosticsService
{
    public static async Task<LocalLlmRuntimeDiagnostics> EvaluateAsync(
        AppSettings settings,
        GpuInfo? gpu = null,
        string? root = null,
        CancellationToken ct = default)
    {
        gpu ??= await GpuDetector.TryGetBestGpuAsync(ct).ConfigureAwait(false);

        var model = ModelCatalogStore.TryGetItem(settings.ModelId)
            ?? ModelCatalogStore.TryGetItem(settings.ModelPath is null ? null : Path.GetFileName(settings.ModelPath));
        var runtimeId = RequalificationTriggerService.DetectRuntimeKey(settings.LlamaExePath);
        var activeState = LlamaCppReleaseDownloader.TryGetActiveRuntimeState(runtimeId);
        var activeBuild = activeState?.Build ?? RuntimeCompatibilityPolicyStore.ReadRuntimeBuild(settings.LlamaExePath);
        var compatibility = RuntimeCompatibilityPolicyStore.Evaluate(runtimeId, activeBuild, model);
        var forcedFlashAttn = RuntimeCompatibilityPolicyStore.GetForcedFlashAttn(runtimeId, model, gpu);

        WarmupResultItem? latestWarmup = null;
        if (settings.QualifiedProfile is not null)
        {
            var warmupRead = await GovernanceArtifactStore.ReadAsync<WarmupResultsArtifact>(
                GovernanceArtifactStore.WarmupResultsFile,
                root,
                ct).ConfigureAwait(false);
            latestWarmup = warmupRead.Status == GovernanceArtifactReadStatus.Ok && warmupRead.Value is not null
                ? warmupRead.Value.Items.FirstOrDefault(item =>
                    string.Equals(item.ProfileId, settings.QualifiedProfile.ProfileId, StringComparison.OrdinalIgnoreCase))
                : null;
        }

        return new LocalLlmRuntimeDiagnostics(
            RuntimeId: runtimeId,
            RuntimeLabel: ResolveRuntimeLabel(runtimeId),
            ActiveBuild: activeBuild,
            RequiredBuild: compatibility.RequiredBuild,
            UpgradeRequired: compatibility.RequiresUpgrade,
            CompatibilityReason: compatibility.Reason,
            ActiveState: activeState?.Status,
            PreviousBuild: activeState?.PreviousBuild,
            ActivatedAtUtc: activeState?.ActivatedAtUtc,
            QualifiedAtUtc: activeState?.QualifiedAtUtc,
            ActiveExePath: activeState?.ExePath ?? settings.LlamaExePath,
            ActiveManifestPath: LlamaCppReleaseDownloader.ActiveRuntimeManifestPath,
            ModelId: model?.ModelId ?? settings.ModelId,
            ModelFamily: model?.Gguf.Architecture ?? model?.Family,
            QualifiedProfileId: settings.QualifiedProfile?.ProfileId,
            LatestWarmupStatus: latestWarmup?.Status,
            LatestWarmupReason: latestWarmup?.Reasons.FirstOrDefault(),
            ForcedFlashAttn: forcedFlashAttn);
    }

    private static string ResolveRuntimeLabel(string runtimeId)
        => runtimeId switch
        {
            "llama.cpp-cuda" => "llama.cpp CUDA",
            "llama.cpp-vulkan" => "llama.cpp Vulkan",
            "llama.cpp-cpu" => "llama.cpp CPU",
            _ => runtimeId
        };
}
