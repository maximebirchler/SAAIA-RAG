using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;

namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow
{
    private static readonly string[] CapabilityAOffsetBackfillReasons =
    {
        "retrieval_chunk_offsets_missing",
        "exact_match_offsets_missing"
    };

    private OverlayDialogSession? _activeAdminRuntimeOverlay;

    private sealed record AdminRuntimeOperationalSnapshot(
        string Environment,
        DateTimeOffset? GeneratedAt,
        AdminRuntimeOperationalTotals Summary,
        IReadOnlyList<AdminRuntimeOperationalItem> Items);

    private sealed record AdminRuntimeOperationalTotals(
        int CapabilityACandidateCount,
        int CapabilityAReadyToEnqueueCount,
        int CapabilityAOffsetBackfillCandidateCount,
        int CapabilityBBacklogCount,
        int CapabilityBReadyToEnqueueCount,
        int CapabilityBActiveJobCount,
        int? CapabilityBLatestCampaignProgressPercent,
        AdminRuntimeCapabilityBIdleScheduler? CapabilityBIdleScheduler);

    private sealed record AdminRuntimeCapabilityBIdleScheduler(
        bool Enabled,
        bool RequiresIngestionIdle,
        bool AutoEnqueueEnabled,
        bool IsIdle,
        string State,
        string Reason,
        int ActiveIngestionJobs,
        DateTimeOffset? LastIngestionActivityAt,
        int RequiredIdleSeconds,
        int? IdleForSeconds,
        int AutoEnqueueBatchSize,
        int RunningJobLeaseTimeoutSeconds);

    private sealed record AdminRuntimeOperationalItem(
        string Key,
        string DisplayName,
        string Status,
        string Family,
        bool Implemented,
        bool Qualified,
        bool Selected,
        bool Stale,
        AdminRuntimeOperationalItemSummary Summary,
        IReadOnlyList<string> Recommendations);

    private sealed record AdminRuntimeOperationalItemSummary(
        int CandidateCount,
        int ReadyToEnqueueCount,
        int BlockedByActiveJobCount,
        int BlockedByCooldownCount,
        int ActiveCapabilityJobCount,
        int TotalCampaignCount,
        int ActiveCampaignCount,
        int TerminalCapabilityJobCount,
        int StoredSummaryCount,
        IReadOnlyDictionary<string, int> ReasonCounts,
        int? OffsetBackfillCandidateCount,
        int? LatestCampaignProgressPercent,
        string? LatestCampaignId,
        string? LatestCampaignStatus,
        DateTimeOffset? LatestCampaignOccurredAt,
        IReadOnlyDictionary<string, int> LlmFailureCounts);

    private static AdminRuntimeOperationalSnapshot ParseAdminRuntimeOperationalSnapshot(JsonElement root)
    {
        var summary = TryGetPropertyIgnoreCase(root, "summary", out var summaryElement)
            ? new AdminRuntimeOperationalTotals(
                CapabilityACandidateCount: TryGetInt(summaryElement, "capabilityACandidateCount") ?? 0,
                CapabilityAReadyToEnqueueCount: TryGetInt(summaryElement, "capabilityAReadyToEnqueueCount") ?? 0,
                CapabilityAOffsetBackfillCandidateCount: TryGetInt(summaryElement, "capabilityAOffsetBackfillCandidateCount") ?? 0,
                CapabilityBBacklogCount: TryGetInt(summaryElement, "capabilityBBacklogCount") ?? 0,
                CapabilityBReadyToEnqueueCount: TryGetInt(summaryElement, "capabilityBReadyToEnqueueCount") ?? 0,
                CapabilityBActiveJobCount: TryGetInt(summaryElement, "capabilityBActiveJobCount") ?? 0,
                CapabilityBLatestCampaignProgressPercent: TryGetInt(summaryElement, "capabilityBLatestCampaignProgressPercent"),
                CapabilityBIdleScheduler: ParseCapabilityBIdleScheduler(summaryElement))
            : new AdminRuntimeOperationalTotals(0, 0, 0, 0, 0, 0, null, null);

        var items = new List<AdminRuntimeOperationalItem>();
        if (TryGetPropertyIgnoreCase(root, "items", out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemsElement.EnumerateArray())
            {
                if (!TryGetPropertyIgnoreCase(item, "summary", out var itemSummaryElement))
                    continue;

                var recommendations = ReadStringArray(item, "recommendations");
                items.Add(new AdminRuntimeOperationalItem(
                    Key: TryGetString(item, "key") ?? string.Empty,
                    DisplayName: TryGetString(item, "displayName") ?? TryGetString(item, "key") ?? string.Empty,
                    Status: TryGetString(item, "status") ?? string.Empty,
                    Family: TryGetString(item, "family") ?? string.Empty,
                    Implemented: TryGetBool(item, "implemented") ?? false,
                    Qualified: TryGetBool(item, "qualified") ?? false,
                    Selected: TryGetBool(item, "selected") ?? false,
                    Stale: TryGetBool(item, "stale") ?? false,
                    Summary: new AdminRuntimeOperationalItemSummary(
                        CandidateCount: TryGetInt(itemSummaryElement, "candidateCount") ?? 0,
                        ReadyToEnqueueCount: TryGetInt(itemSummaryElement, "readyToEnqueueCount") ?? 0,
                        BlockedByActiveJobCount: TryGetInt(itemSummaryElement, "blockedByActiveJobCount") ?? 0,
                        BlockedByCooldownCount: TryGetInt(itemSummaryElement, "blockedByCooldownCount") ?? 0,
                        ActiveCapabilityJobCount: TryGetInt(itemSummaryElement, "activeCapabilityJobCount") ?? 0,
                        TotalCampaignCount: TryGetInt(itemSummaryElement, "totalCampaignCount") ?? 0,
                        ActiveCampaignCount: TryGetInt(itemSummaryElement, "activeCampaignCount") ?? 0,
                        TerminalCapabilityJobCount: TryGetInt(itemSummaryElement, "terminalCapabilityJobCount") ?? 0,
                        StoredSummaryCount: TryGetInt(itemSummaryElement, "storedSummaryCount") ?? 0,
                        ReasonCounts: ReadIntDictionary(itemSummaryElement, "reasonCounts"),
                        OffsetBackfillCandidateCount: TryGetInt(itemSummaryElement, "offsetBackfillCandidateCount"),
                        LatestCampaignProgressPercent: TryGetInt(itemSummaryElement, "latestCampaignProgressPercent"),
                        LatestCampaignId: TryGetString(itemSummaryElement, "latestCampaignId"),
                        LatestCampaignStatus: TryGetString(itemSummaryElement, "latestCampaignStatus"),
                        LatestCampaignOccurredAt: TryGetDateTimeOffset(itemSummaryElement, "latestCampaignOccurredAt"),
                        LlmFailureCounts: ReadIntDictionary(itemSummaryElement, "llmFailureCounts")),
                    Recommendations: recommendations));
            }
        }

        return new AdminRuntimeOperationalSnapshot(
            Environment: TryGetString(root, "environment") ?? string.Empty,
            GeneratedAt: TryGetDateTimeOffset(root, "generatedAt"),
            Summary: summary,
            Items: items);
    }

    private static IReadOnlyDictionary<string, int> ReadIntDictionary(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var property) || property.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in property.EnumerateObject())
        {
            if (item.Value.ValueKind == JsonValueKind.Number && item.Value.TryGetInt32(out var numericValue))
            {
                result[item.Name] = numericValue;
                continue;
            }

            if (item.Value.ValueKind == JsonValueKind.String && int.TryParse(item.Value.GetString(), out var stringValue))
                result[item.Name] = stringValue;
        }

        return result;
    }

    private static string ResolveCapabilityStatusLabel(string? status, string lang)
        => status switch
        {
            "configured" => LocalRuntimeText("configuree", "configured", "configurada", "configurada", "konfiguriert", "configurata", lang),
            "qualified" => LocalRuntimeText("qualifiee", "qualified", "validada", "qualificada", "qualifiziert", "qualificata", lang),
            "selected" => LocalRuntimeText("activee", "enabled", "activada", "ativada", "aktiviert", "attivata", lang),
            "stale" => LocalRuntimeText("a reverifier", "needs recheck", "por revisar", "a reverificar", "erneut pruefen", "da ricontrollare", lang),
            "blocked" => LocalRuntimeText("bloquee", "blocked", "bloqueada", "bloqueada", "blockiert", "bloccata", lang),
            "unavailable" => LocalRuntimeText("indisponible", "unavailable", "no disponible", "indisponivel", "nicht verfuegbar", "non disponibile", lang),
            "not_configured" => LocalRuntimeText("non configuree", "not configured", "no configurada", "nao configurada", "nicht konfiguriert", "non configurata", lang),
            null or "" => "-",
            _ => LocalRuntimeText("etat a verifier", "state to review", "estado por revisar", "estado a verificar", "Status pruefen", "stato da verificare", lang)
        };

    private static string ResolveCapabilityRecommendationLabel(string? recommendation, string lang)
    {
        var normalized = (recommendation ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0)
            return string.Empty;

        if (normalized.Contains("requalify") || normalized.Contains("qualification"))
        {
            return LocalRuntimeText(
                "Relance une verification serveur : les donnees stockees ne correspondent plus exactement au profil actif.",
                "Run a server recheck: stored approval no longer matches the active profile.",
                "Relanza una verificacion del servidor: la validacion guardada ya no coincide con el perfil activo.",
                "Relanca uma verificacao do servidor: a validacao guardada ja nao corresponde ao perfil ativo.",
                "Server erneut pruefen: die gespeicherte Freigabe passt nicht mehr zum aktiven Profil.",
                "Rilancia una verifica server: la validazione salvata non corrisponde piu al profilo attivo.",
                lang);
        }

        if (normalized.Contains("authorize") || normalized.Contains("selected") || normalized.Contains("selecting"))
        {
            return LocalRuntimeText(
                "Active cette capacite seulement apres une qualification verte.",
                "Enable this capability only after a successful qualification.",
                "Activa esta capacidad solo despues de una validacion correcta.",
                "Ativa esta capacidade apenas depois de uma qualificacao bem-sucedida.",
                "Diese Faehigkeit erst nach erfolgreicher Qualifikation aktivieren.",
                "Attiva questa capacita solo dopo una qualificazione riuscita.",
                lang);
        }

        if (normalized.Contains("warmup"))
        {
            return LocalRuntimeText(
                "Controle le demarrage du moteur de résumés serveur et relance la verification apres correction.",
                "Check the server summary engine startup and rerun the check after fixing it.",
                "Comprueba el arranque del motor de resúmenes del servidor y relanza la validacion despues de corregirlo.",
                "Verifica o arranque do motor de resumos do servidor e relanca a verificacao depois da correcao.",
                "Start der Server-Zusammenfassungs-Engine pruefen und danach erneut pruefen.",
                "Controlla l'avvio del motore riassunti server e rilancia la verifica dopo la correzione.",
                lang);
        }

        if (normalized.Contains("cooldown") || normalized.Contains("policy"))
        {
            return LocalRuntimeText(
                "Attends la fin de la protection anti-boucle ou repare les etats bloques si elle reste active trop longtemps.",
                "Wait for the retry protection to expire, or repair blocked states if it stays active too long.",
                "Espera a que termine la proteccion anti-bucle o repara estados bloqueados si dura demasiado.",
                "Aguarda o fim da protecao contra ciclos ou repara estados bloqueados se durar demasiado.",
                "Wiederholschutz abwarten oder blockierte Zustaende reparieren, falls er zu lange aktiv bleibt.",
                "Attendi la fine della protezione anti-ciclo o ripara gli stati bloccati se dura troppo.",
                lang);
        }

        return LocalRuntimeText(
            "Verification conseillee : le serveur a signale un etat que l'interface ne classe pas encore.",
            "Recommended check: the server reported a state this interface does not classify yet.",
            "Revision recomendada: el servidor senalo un estado que esta interfaz aun no clasifica.",
            "Verificacao recomendada: o servidor sinalizou um estado que esta interface ainda nao classifica.",
            "Pruefung empfohlen: der Server meldete einen Zustand, den diese Ansicht noch nicht einordnet.",
            "Verifica consigliata: il server ha segnalato uno stato che l'interfaccia non classifica ancora.",
            lang);
    }

    private static string ResolveCampaignStatusLabel(string? status, string? lang)
        => status switch
        {
            "dry_run" => LocalRuntimeText("simulation", "simulation", "simulacion", "simulacao", "Simulation", "simulazione", lang),
            "executed" => LocalRuntimeText("execute", "executed", "ejecutado", "executado", "ausgefuehrt", "eseguito", lang),
            null or "" => "-",
            _ => LocalRuntimeText("etat a verifier", "state to review", "estado por revisar", "estado a verificar", "Status pruefen", "stato da verificare", lang)
        };

    private static string ResolveCapabilityBReasonLabel(string reason, string? lang)
        => reason switch
        {
            "summary_missing" => LocalRuntimeText("resume manquant", "summary missing", "resumen ausente", "resumo ausente", "Zusammenfassung fehlt", "riepilogo mancante", lang),
            "summary_stale" => LocalRuntimeText("resume obsolete", "summary stale", "resumen obsoleto", "resumo obsoleto", "Zusammenfassung veraltet", "riepilogo obsoleto", lang),
            "active_summary_job_exists" => LocalRuntimeText("job resume actif", "active summary job", "job resumen activo", "job resumo ativo", "aktiver Zusammenfassungsjob", "job riepilogo attivo", lang),
            "recent_summary_job_failure" => LocalRuntimeText("echec recent", "recent failure", "fallo reciente", "falha recente", "letzter Fehler", "errore recente", lang),
            "recent_summary_job_cancellation" => LocalRuntimeText("annulation recente", "recent cancellation", "cancelacion reciente", "cancelamento recente", "letzter Abbruch", "annullamento recente", lang),
            _ => LocalRuntimeText("raison a verifier", "reason to review", "motivo por revisar", "motivo a verificar", "Grund pruefen", "motivo da verificare", lang)
        };

    private static string HumanizeRuntimeIdentifier(string value)
    {
        var cleaned = value.Trim();
        if (cleaned.Length == 0)
            return "-";

        return cleaned.Replace('_', ' ').Replace('-', ' ');
    }

    private static string ShortenCampaignId(string campaignId)
    {
        var trimmed = campaignId.Trim();
        return trimmed.Length <= 8 ? trimmed : trimmed[..8];
    }

    private static AdminRuntimeCapabilityBIdleScheduler? ParseCapabilityBIdleScheduler(JsonElement summaryElement)
    {
        if (!TryGetPropertyIgnoreCase(summaryElement, "capabilityBIdleScheduler", out var scheduler)
            || scheduler.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new AdminRuntimeCapabilityBIdleScheduler(
            Enabled: TryGetBool(scheduler, "enabled") ?? false,
            RequiresIngestionIdle: TryGetBool(scheduler, "requiresIngestionIdle") ?? false,
            AutoEnqueueEnabled: TryGetBool(scheduler, "autoEnqueueEnabled") ?? false,
            IsIdle: TryGetBool(scheduler, "isIdle") ?? false,
            State: TryGetString(scheduler, "state") ?? string.Empty,
            Reason: TryGetString(scheduler, "reason") ?? string.Empty,
            ActiveIngestionJobs: TryGetInt(scheduler, "activeIngestionJobs") ?? 0,
            LastIngestionActivityAt: TryGetDateTimeOffset(scheduler, "lastIngestionActivityAt"),
            RequiredIdleSeconds: TryGetInt(scheduler, "requiredIdleSeconds") ?? 0,
            IdleForSeconds: TryGetInt(scheduler, "idleForSeconds"),
            AutoEnqueueBatchSize: TryGetInt(scheduler, "autoEnqueueBatchSize") ?? 0,
            RunningJobLeaseTimeoutSeconds: TryGetInt(scheduler, "runningJobLeaseTimeoutSeconds") ?? 0);
    }

    private static string BuildAdminRuntimeRequalifySummaryMessage(JsonElement root, string lang)
    {
        var itemCount = 0;
        var qualifiedCount = 0;
        var selectedCount = 0;
        var warmupCount = 0;

        if (TryGetPropertyIgnoreCase(root, "items", out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemsElement.EnumerateArray())
            {
                itemCount++;
                if (TryGetBool(item, "qualified") == true)
                    qualifiedCount++;
                if (TryGetBool(item, "selected") == true)
                    selectedCount++;
            }
        }

        if (TryGetPropertyIgnoreCase(root, "warmupResults", out var warmupsElement) && warmupsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var _ in warmupsElement.EnumerateArray())
                warmupCount++;
        }

        return lang switch
        {
            "en" => $"Server recheck completed: {itemCount} feature(s), {qualifiedCount} approved, {selectedCount} enabled, {warmupCount} engine check(s).",
            "de" => $"Serverpruefung abgeschlossen: {itemCount} Funktion(en), {qualifiedCount} freigegeben, {selectedCount} aktiv, {warmupCount} Engine-Pruefung(en).",
            "es" => $"Revision servidor terminada: {itemCount} funcion(es), {qualifiedCount} validada(s), {selectedCount} activa(s), {warmupCount} control(es) de motor.",
            "it" => $"Verifica server terminata: {itemCount} funzione/i, {qualifiedCount} validata/e, {selectedCount} attiva/e, {warmupCount} controllo/i motore.",
            "pt" => $"Reverificacao servidor concluida: {itemCount} funcao(oes), {qualifiedCount} validada(s), {selectedCount} ativa(s), {warmupCount} controlo(s) de motor.",
            _ => $"Verification serveur terminee : {itemCount} fonction(s), {qualifiedCount} validee(s), {selectedCount} active(s), {warmupCount} controle(s) moteur."
        };
    }
}
