using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

internal sealed record LocalLlmRuntimeStatus(
    string Code,
    string Message,
    bool IsWarning = false,
    bool IsError = false);

internal static class LocalLlmRuntimeStatusService
{
    public static string? ResolveDisplayMessage(
        LocalLlmRuntimeStatus? status,
        bool isRunning,
        string? currentMessage = null)
    {
        if (status is not null)
            return status.Message;

        return isRunning ? string.Empty : currentMessage;
    }

    public static async Task<LocalLlmRuntimeStatus?> EvaluateAsync(
        AppSettings settings,
        string? root = null,
        CancellationToken ct = default)
    {
        if (!settings.UseLocalLlm || !settings.ManageLocalLlmProcess || settings.QualifiedProfile is null)
            return null;

        if (ModelIntegrityService.IsModelQuarantined(settings))
        {
            return new LocalLlmRuntimeStatus(
                "model_quarantined",
                ModelIntegrityService.GetQuarantineUserMessage(settings.UiLanguage),
                IsError: true);
        }

        var model = ModelCatalogStore.TryGetItem(settings.ModelId)
            ?? ModelCatalogStore.TryGetItem(settings.ModelPath is null ? null : Path.GetFileName(settings.ModelPath));
        var runtimeId = RequalificationTriggerService.DetectRuntimeKey(settings.LlamaExePath);
        var runtimeBuild = RuntimeCompatibilityPolicyStore.ReadRuntimeBuild(settings.LlamaExePath);
        var compatibility = RuntimeCompatibilityPolicyStore.Evaluate(runtimeId, runtimeBuild, model);
        if (!compatibility.Compatible)
        {
            return new LocalLlmRuntimeStatus(
                "runtime_upgrade_required",
                LT(settings.UiLanguage, "Mise a niveau du runtime requise.", "Runtime upgrade required.", "Se requiere actualizar el runtime.", "Atualizacao do runtime necessaria.", "Runtime-Aktualisierung erforderlich.", "Aggiornamento runtime richiesto."),
                IsError: true);
        }

        var warmupRead = await GovernanceArtifactStore.ReadAsync<WarmupResultsArtifact>(
            GovernanceArtifactStore.WarmupResultsFile,
            root,
            ct).ConfigureAwait(false);
        var latest = warmupRead.Status == GovernanceArtifactReadStatus.Ok && warmupRead.Value is not null
            ? warmupRead.Value.Items.FirstOrDefault(item =>
                string.Equals(item.ProfileId, settings.QualifiedProfile.ProfileId, StringComparison.OrdinalIgnoreCase))
            : null;

        if (latest?.Status == WarmupGateStatus.FailBlock)
        {
            return new LocalLlmRuntimeStatus(
                "runtime_unavailable",
                LT(settings.UiLanguage, "Assistant temporairement indisponible.", "Assistant temporarily unavailable.", "Asistente temporalmente no disponible.", "Assistente temporariamente indisponivel.", "Assistent voruebergehend nicht verfuegbar.", "Assistente temporaneamente non disponibile."),
                IsError: true);
        }

        if (latest?.Status == WarmupGateStatus.FailFallback)
        {
            return new LocalLlmRuntimeStatus(
                "fallback_required",
                LT(settings.UiLanguage, "Profil de secours requis.", "Fallback profile required.", "Se requiere perfil de respaldo.", "Perfil de contingencia necessario.", "Fallback-Profil erforderlich.", "Profilo di fallback richiesto."),
                IsWarning: true);
        }

        if (warmupRead.Status == GovernanceArtifactReadStatus.Ok && warmupRead.Value is not null)
        {
            var warmupDrift = RequalificationTriggerService.EvaluateWarmupHistory(
                warmupRead.Value.Items,
                settings.QualifiedProfile.ProfileId);
            if (warmupDrift.Required)
            {
                return new LocalLlmRuntimeStatus(
                    "requalification_required",
                    LT(settings.UiLanguage, "Requalification necessaire.", "Requalification required.", "Se requiere recalificacion.", "Requalificacao necessaria.", "Neuqualifizierung erforderlich.", "Ririqualificazione richiesta."),
                    IsWarning: true);
            }
        }

        var profile = WarmupProfileStore.FindProfile(settings.QualifiedProfile.ProfileId);
        if (profile?.Mode == "fallback")
        {
            return new LocalLlmRuntimeStatus(
                "fallback_active",
                LT(settings.UiLanguage, "Profil de secours actif.", "Fallback profile active.", "Perfil de respaldo activo.", "Perfil de contingencia ativo.", "Fallback-Profil aktiv.", "Profilo di fallback attivo."),
                IsWarning: true);
        }

        if (latest?.Status == WarmupGateStatus.PassDegraded)
        {
            return new LocalLlmRuntimeStatus(
                "performance_reduced",
                LT(settings.UiLanguage, "Mode performance reduite actif.", "Reduced performance mode active.", "Modo de rendimiento reducido activo.", "Modo de desempenho reduzido ativo.", "Modus mit reduzierter Leistung aktiv.", "Modalita a prestazioni ridotte attiva."),
                IsWarning: true);
        }

        var drift = RequalificationTriggerService.EvaluateProfileDrift(settings);
        if (drift.Required)
        {
            return new LocalLlmRuntimeStatus(
                "requalification_required",
                LT(settings.UiLanguage, "Requalification necessaire.", "Requalification required.", "Se requiere recalificacion.", "Requalificacao necessaria.", "Neuqualifizierung erforderlich.", "Ririqualificazione richiesta."),
                IsWarning: true);
        }

        var battery = await BatteryPolicyStore.EvaluateAsync(settings.QualifiedProfile, root, ct).ConfigureAwait(false);
        if (battery.RequiresRequalification)
        {
            return new LocalLlmRuntimeStatus(
                "battery_policy_warning",
                LT(settings.UiLanguage, "Mode performance reduite recommande sur batterie.", "Reduced performance mode recommended on battery.", "Se recomienda modo de rendimiento reducido con bateria.", "Modo de desempenho reduzido recomendado com bateria.", "Bei Akkubetrieb wird ein Modus mit reduzierter Leistung empfohlen.", "Su batteria e consigliata la modalita a prestazioni ridotte."),
                IsWarning: true);
        }

        return null;
    }

    private static string LT(string? language, string fr, string en, string es, string pt, string de, string it)
        => ClientUiText.NormalizeLanguage(language) switch
        {
            "en" => en,
            "es" => es,
            "pt" => pt,
            "de" => de,
            "it" => it,
            _ => fr
        };
}
