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

        var runtimeId = RequalificationTriggerService.DetectRuntimeKey(settings.LlamaExePath);
        var modelId = ModelCatalogStore.ResolveCanonicalModelId(settings.ModelId)
            ?? ModelCatalogStore.ResolveCanonicalModelId(Path.GetFileName(settings.ModelPath))
            ?? settings.ModelId;
        var reference = WarmupProfileStore.ResolveReferenceProfile(runtimeId, modelId);
        if (reference is null)
        {
            throw new InvalidOperationException(
                $"No warmup reference profile matches runtime '{runtimeId}' and model '{modelId}'.");
        }

        if (settings.QualifiedProfile is null
            || !string.Equals(settings.QualifiedProfile.Runtime, reference.Runtime, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(settings.QualifiedProfile.ModelId, reference.ModelId, StringComparison.OrdinalIgnoreCase))
        {
            settings.QualifiedProfile = reference.Candidate;
            settings.Save();
        }

        _ = LlamaCppReleaseDownloader.EnsureRuntimeTrackedForQualification(runtimeId, settings.LlamaExePath);

        var processManager = new LlamaCppProcessManager();
        try
        {
            var start = await processManager.StartAsync(settings, default).ConfigureAwait(false);
            if (!start.ok)
                throw new InvalidOperationException("Local runtime start failed: " + start.message);

            var result = await WarmupGate.RunQualificationAsync(
                settings.QualifiedProfile!,
                settings.LlmBaseUrl,
                settings.ModelId,
                root: root,
                observedLoadMs: processManager.LastStartupLoadMs,
                trigger: "maintenance_runtime_qualification",
                ct: default).ConfigureAwait(false);

            if (result.Status is WarmupGateStatus.Pass or WarmupGateStatus.PassDegraded)
            {
                _ = LlamaCppReleaseDownloader.TryMarkRuntimeQualified(runtimeId);
                settings.QualifiedProfile = result.SelectedProfile ?? settings.QualifiedProfile;
                settings.Save();
                ClientLog.Info($"[MaintenanceMode] Local runtime qualification passed ({result.Status}).");
                return;
            }

            if (LlamaCppReleaseDownloader.TryRollbackPendingRuntime(runtimeId, out var rollbackExe, out var rollbackBuild)
                && !string.IsNullOrWhiteSpace(rollbackExe))
            {
                settings.LlamaExePath = rollbackExe;
                settings.Save();
                throw new InvalidOperationException(
                    $"Local runtime qualification failed ({result.Status}); rolled back to '{rollbackBuild ?? "rollback"}'.");
            }

            throw new InvalidOperationException(
                $"Local runtime qualification failed ({result.Status}): {string.Join(", ", result.Reasons)}");
        }
        finally
        {
            processManager.Stop();
        }
    }
}
