using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

internal sealed record StartupMaintenanceCommand(
    bool GovernanceInitOnly,
    bool QualifyLocalRuntimeOnly,
    string? GovernanceRoot);

internal static class StartupMaintenanceMode
{
    public static StartupMaintenanceCommand? Parse(string[]? args)
    {
        if (args is null || args.Length == 0)
            return null;

        var effectiveArgs = args.Skip(1).ToArray();
        if (effectiveArgs.Length == 0)
            return null;

        var governanceInitOnly = false;
        var qualifyLocalRuntimeOnly = false;
        string? governanceRoot = null;

        for (var i = 0; i < effectiveArgs.Length; i++)
        {
            var arg = effectiveArgs[i];
            if (string.Equals(arg, "--governance-init-only", StringComparison.OrdinalIgnoreCase))
            {
                governanceInitOnly = true;
                continue;
            }

            if (string.Equals(arg, "--qualify-local-runtime-only", StringComparison.OrdinalIgnoreCase))
            {
                qualifyLocalRuntimeOnly = true;
                continue;
            }

            if (string.Equals(arg, "--governance-root", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= effectiveArgs.Length)
                    throw new ArgumentException("Missing value for --governance-root.");

                governanceRoot = effectiveArgs[++i];
            }
        }

        return governanceInitOnly || qualifyLocalRuntimeOnly
            ? new StartupMaintenanceCommand(
                GovernanceInitOnly: governanceInitOnly,
                QualifyLocalRuntimeOnly: qualifyLocalRuntimeOnly,
                GovernanceRoot: string.IsNullOrWhiteSpace(governanceRoot) ? null : governanceRoot)
            : null;
    }

    public static async Task<bool> TryRunAsync(string[]? args)
    {
        StartupMaintenanceCommand? command;
        try
        {
            command = Parse(args);
        }
        catch (Exception ex)
        {
            ClientLog.Warn($"[MaintenanceMode] Invalid arguments: {ex.Message}");
            return false;
        }

        if (command is null)
            return false;

        var root = string.IsNullOrWhiteSpace(command.GovernanceRoot)
            ? GovernanceArtifactStore.DefaultRoot
            : Path.GetFullPath(command.GovernanceRoot);

        try
        {
            var settings = AppSettings.Load();
            if (command.GovernanceInitOnly)
            {
                ClientLog.Info($"[MaintenanceMode] Starting governance artifact regeneration (root={root}).");
                await GovernanceArtifactStore.EnsureDefaultArtifactsAsync(settings, root).ConfigureAwait(false);
                ClientLog.Info($"[MaintenanceMode] Governance artifact regeneration completed (root={root}).");
            }

            if (command.QualifyLocalRuntimeOnly)
            {
                await QualifyLocalRuntimeAsync(settings, root).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception ex)
        {
            ClientLog.Exception("[MaintenanceMode] Governance artifact regeneration failed", ex);
            throw;
        }
    }

    private static async Task QualifyLocalRuntimeAsync(AppSettings settings, string root)
    {
        ClientLog.Info($"[MaintenanceMode] Starting local runtime qualification (root={root}).");
        await GovernanceArtifactStore.EnsureDefaultArtifactsAsync(settings, root).ConfigureAwait(false);

        var bootstrapper = new LocalLlmBootstrapper();
        var (ok, message, _) = await bootstrapper.EnsureAsync(
            settings,
            force: false,
            progress: null,
            ct: default).ConfigureAwait(false);
        if (!ok)
            throw new InvalidOperationException("Local runtime bootstrap failed: " + message);

        var gpus = await GpuDetector.TryGetGpusAsync(default).ConfigureAwait(false);
        var provisioning = await LocalLlmRuntimeProvisioningService.ProvisionApplicableAsync(
            settings,
            gpus,
            progress: null).ConfigureAwait(false);
        foreach (var result in provisioning.Results)
        {
            ClientLog.Info(
                "[MaintenanceMode] Runtime backend provisioning: "
                + $"{result.Plan.Backend} ok={result.Succeeded} "
                + $"tracked={result.TrackedForQualification} message={result.Message}.");
        }

        if (!provisioning.Succeeded)
        {
            throw new InvalidOperationException(
                "Applicable runtime provisioning failed: "
                + string.Join(", ", provisioning.Reasons));
        }

        var progress = new Progress<LocalLlmAdaptiveQualificationProgress>(item =>
            ClientLog.Info(
                $"[MaintenanceMode] Adaptive qualification {item.StageIndex}/{item.StageCount}: "
                + $"{item.Stage} - {item.Message}"));
        var qualification = await LocalLlmAdaptiveQualificationService.QualifyIfRequiredAsync(
            settings,
            LocalLlmAdaptiveQualificationOptions.CreateDeep(Environment.ProcessorCount),
            force: true,
            root: root,
            progress: progress).ConfigureAwait(false);
        if (qualification.Succeeded && qualification.Promotion?.Winner is not null)
        {
            ClientLog.Info(
                "[MaintenanceMode] Adaptive local runtime qualification passed: "
                + $"{qualification.Promotion.Winner.ProfileId} "
                + $"runtime={qualification.Promotion.Winner.Runtime}.");
            return;
        }

        var runtimeId = RequalificationTriggerService.DetectRuntimeKey(settings.LlamaExePath);
        if (LlamaCppReleaseDownloader.TryRollbackPendingRuntime(
                runtimeId,
                out var rollbackExe,
                out var rollbackBuild)
            && !string.IsNullOrWhiteSpace(rollbackExe))
        {
            settings.LlamaExePath = rollbackExe;
            settings.Save();
            throw new InvalidOperationException(
                "Adaptive local runtime qualification failed; "
                + $"rolled back to '{rollbackBuild ?? "rollback"}'. "
                + string.Join(", ", qualification.Reasons));
        }

        throw new InvalidOperationException(
            "Adaptive local runtime qualification failed: "
            + string.Join(", ", qualification.Reasons));
    }
}
