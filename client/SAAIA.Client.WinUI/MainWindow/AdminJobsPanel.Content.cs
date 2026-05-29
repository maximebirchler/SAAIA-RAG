using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls.Primitives;
using SAAIA.Client.WinUI.Localization;

namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow
{
    private List<AdminJobListItem> ParseAdminJobs(JsonElement root)
    {
        var items = new List<AdminJobListItem>();
        JsonElement source = root;
        if (TryGetPropertyIgnoreCase(root, "items", out var array) && array.ValueKind == JsonValueKind.Array)
            source = array;

        if (source.ValueKind != JsonValueKind.Array)
            return items;

        foreach (var item in source.EnumerateArray())
        {
            try
            {
                var status = NormalizeTrackedJobStatus(TryGetString(item, "Status") ?? TryGetString(item, "status"));
                var jobId = TryGetString(item, "JobId") ?? TryGetString(item, "jobId") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(jobId))
                    continue;

                items.Add(new AdminJobListItem
                {
                    JobId = jobId,
                    Type = TryGetString(item, "Type") ?? TryGetString(item, "type") ?? string.Empty,
                    JobType = TryGetString(item, "JobType") ?? TryGetString(item, "jobType") ?? string.Empty,
                    Status = status,
                    DocId = TryGetString(item, "DocId") ?? TryGetString(item, "docId"),
                    DocPath = TryGetString(item, "DocPath") ?? TryGetString(item, "docPath"),
                    Level = TryGetString(item, "Level") ?? TryGetString(item, "level"),
                    LastError = TryGetString(item, "LastError") ?? TryGetString(item, "lastError"),
                    CreatedAt = TryGetDateTimeOffset(item, "CreatedAt") ?? TryGetDateTimeOffset(item, "createdAt"),
                    StartedAt = TryGetDateTimeOffset(item, "StartedAt") ?? TryGetDateTimeOffset(item, "startedAt"),
                    FinishedAt = TryGetDateTimeOffset(item, "FinishedAt") ?? TryGetDateTimeOffset(item, "finishedAt"),
                    ProgressPhase = TryGetString(item, "ProgressPhase") ?? TryGetString(item, "progressPhase"),
                    ProgressCurrent = TryGetInt(item, "ProgressCurrent") ?? TryGetInt(item, "progressCurrent"),
                    ProgressTotal = TryGetInt(item, "ProgressTotal") ?? TryGetInt(item, "progressTotal"),
                    ProgressPercent = TryGetInt(item, "ProgressPercent") ?? TryGetInt(item, "progressPercent"),
                    CancelRequested = TryGetBool(item, "CancelRequested") ?? TryGetBool(item, "cancelRequested"),
                    EnqueueSource = TryGetString(item, "EnqueueSource") ?? TryGetString(item, "enqueueSource"),
                    RuntimeCapabilityKey = TryGetString(item, "RuntimeCapabilityKey") ?? TryGetString(item, "runtimeCapabilityKey"),
                    ExecutionMode = TryGetString(item, "ExecutionMode") ?? TryGetString(item, "executionMode"),
                    DocumentStatus = TryGetString(item, "DocumentStatus") ?? TryGetString(item, "documentStatus"),
                    DocumentIngestionVersion = TryGetInt(item, "DocumentIngestionVersion") ?? TryGetInt(item, "documentIngestionVersion"),
                    DocumentIndexedVersion = TryGetInt(item, "DocumentIndexedVersion") ?? TryGetInt(item, "documentIndexedVersion"),
                    DocumentAutoIngestPaused = TryGetBool(item, "DocumentAutoIngestPaused") ?? TryGetBool(item, "documentAutoIngestPaused"),
                    DocumentAutoIngestPauseReason = TryGetString(item, "DocumentAutoIngestPauseReason") ?? TryGetString(item, "documentAutoIngestPauseReason")
                });
            }
            catch (Exception ex)
            {
                ClientLog.Exception("AdminJobs.ParseItem", ex);
            }
        }

        return items;
    }

    private static bool? TryGetBool(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private string BuildAdminJobErrorText(AdminJobListItem item)
        => BuildAdminJobErrorTextForDiagnostics(item.LastError, item.IsCanceled, UiLang);

    internal static string BuildAdminJobErrorTextForDiagnostics(string? lastError, bool isCanceled, string uiLanguage)
    {
        var raw = lastError?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var normalized = raw.ToLowerInvariant();
        if (isCanceled
            && (normalized == "timeout_or_canceled"
                || normalized == "timeout"
                || normalized.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("bulkhead", StringComparison.OrdinalIgnoreCase)))
        {
            return ClientUiText.Get("admin.jobs.error.canceled_by_admin", uiLanguage);
        }

        var diagnosticReason = TranslateAdminJobDiagnosticReason(raw, uiLanguage);
        if (!string.IsNullOrWhiteSpace(diagnosticReason))
            return diagnosticReason!;

        return normalized switch
        {
            "timeout" => ClientUiText.Get("admin.jobs.error.timeout", uiLanguage),
            "timeout_or_canceled" => ClientUiText.Get("admin.jobs.error.timeout", uiLanguage),
            "canceled_by_admin" or "canceled_by_admin_token" or "canceled_by_admin_document" or "canceled_by_worker" or "canceled_by_admin_exception" => ClientUiText.Get("admin.jobs.error.canceled_by_admin", uiLanguage),
            "canceled_at_commit" => ClientUiText.Get("admin.jobs.error.canceled_after_commit", uiLanguage),
            "superseded_version" or "superseded_at_commit" => ClientUiText.Get("admin.jobs.error.superseded", uiLanguage),
            "coalesced_by_missing" => ClientUiText.Get("admin.jobs.error.coalesced_by_missing", uiLanguage),
            "coalesced_by_upsert" => ClientUiText.Get("admin.jobs.error.coalesced_by_upsert", uiLanguage),
            "file_missing" => ClientUiText.Get("admin.jobs.error.file_missing", uiLanguage),
            "canceled_stale_running" => ClientUiText.Get("admin.jobs.error.canceled_by_admin", uiLanguage),
            "requeued_stale_running" or "stale_running_scanner" => ClientUiText.Get("admin.jobs.error.stale_running", uiLanguage),
            "source_removed_during_ingestion" => ClientUiText.Get("admin.jobs.error.source_removed_during_ingestion", uiLanguage),
            _ when normalized.Contains("timeout", StringComparison.OrdinalIgnoreCase) => ClientUiText.Get("admin.jobs.error.timeout", uiLanguage),
            _ when normalized.Contains("bulkhead", StringComparison.OrdinalIgnoreCase) => ClientUiText.Get("admin.jobs.error.timeout", uiLanguage),
            _ => LocalRuntimeText(
                "Erreur non classee. Le detail technique est conserve dans les logs.",
                "Unclassified error. The technical detail was kept in the logs.",
                "Error no clasificado. El detalle tecnico queda en los logs.",
                "Erro nao classificado. O detalhe tecnico ficou nos logs.",
                "Nicht klassifizierter Fehler. Die technischen Details stehen in den Logs.",
                "Errore non classificato. Il dettaglio tecnico e nei log.",
                uiLanguage)
        };
    }

    private string BuildAdminJobProgressLine(AdminJobListItem item)
    {
        var status = NormalizeTrackedJobStatus(item.Status);
        List<string> bits;
        if (IsAdminPauseTransitionPending(item))
            return ClientUiText.Get("admin.jobs.pause", UiLang) + "...";

        if (status == "cancel_requested"
            && string.Equals(item.JobType, "upsert", StringComparison.OrdinalIgnoreCase)
            && (item.DocumentIndexedVersion ?? 0) <= 0
            && item.DocumentAutoIngestPaused == true)
        {
            return ClientUiText.Get("admin.jobs.pause", UiLang) + "...";
        }

        if (status == "queued" || status == "paused")
        {
            var pausedBits = new List<string> { TranslateAdminJobStatus(status) };
            var pausedPhase = TranslateAdminJobPhase(item.ProgressPhase);
            if (!string.IsNullOrWhiteSpace(pausedPhase))
                pausedBits.Add(pausedPhase!);
            if (item.ProgressPercent.HasValue)
                pausedBits.Add($"{Math.Clamp(item.ProgressPercent.Value, 0, 100)}%");
            if (item.ProgressCurrent.HasValue || item.ProgressTotal.HasValue)
                pausedBits.Add($"{item.ProgressCurrent?.ToString(CultureInfo.InvariantCulture) ?? "?"}/{item.ProgressTotal?.ToString(CultureInfo.InvariantCulture) ?? "?"}");
            if (pausedBits.Count == 1)
            {
                var action = TranslateAdminJobType(item.JobType)?.ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(action))
                    pausedBits.Add(action!);
            }

            bits = pausedBits;
            return string.Join(" • ", bits);
        }

        var activeBits = new List<string>();
        var activePhase = TranslateAdminJobPhase(item.ProgressPhase);
        if (!string.IsNullOrWhiteSpace(activePhase))
            activeBits.Add(activePhase!);
        if (item.ProgressPercent.HasValue)
            activeBits.Add($"{Math.Clamp(item.ProgressPercent.Value, 0, 100)}%");
        if (item.ProgressCurrent.HasValue || item.ProgressTotal.HasValue)
            activeBits.Add($"{item.ProgressCurrent?.ToString(CultureInfo.InvariantCulture) ?? "?"}/{item.ProgressTotal?.ToString(CultureInfo.InvariantCulture) ?? "?"}");
        if (activeBits.Count == 0)
            activeBits.Add(TranslateAdminJobStatus(status));
        bits = activeBits;
        return string.Join(" • ", bits);
    }

    private string BuildAdminJobDatesLine(AdminJobListItem item)
    {
        var bits = new List<string>();
        if (item.CreatedAt.HasValue)
            bits.Add(ClientUiText.Format("admin.jobs.date.created", UiLang, item.CreatedAt.Value.LocalDateTime.ToString("dd.MM HH:mm:ss", CultureInfo.InvariantCulture)));
        if (item.StartedAt.HasValue)
            bits.Add(ClientUiText.Format("admin.jobs.date.started", UiLang, item.StartedAt.Value.LocalDateTime.ToString("dd.MM HH:mm:ss", CultureInfo.InvariantCulture)));
        if (item.FinishedAt.HasValue)
            bits.Add(ClientUiText.Format("admin.jobs.date.finished", UiLang, item.FinishedAt.Value.LocalDateTime.ToString("dd.MM HH:mm:ss", CultureInfo.InvariantCulture)));
        return bits.Count == 0 ? ClientUiText.Get("admin.jobs.date.none", UiLang) : string.Join(" • ", bits);
    }

    private string? BuildAdminJobDocumentStateLine(AdminJobListItem item)
    {
        var bits = new List<string>();
        var documentStatus = TranslateAdminJobDocumentStatus(item.DocumentStatus);
        if (!string.IsNullOrWhiteSpace(documentStatus))
            bits.Add($"{ClientUiText.Get("admin.jobs.card.document_state", UiLang)} : {documentStatus}");

        var versions = BuildAdminJobDocumentVersionsLine(item);
        if (!string.IsNullOrWhiteSpace(versions))
            bits.Add(versions!);

        var pause = BuildAdminJobAutoPauseLine(item);
        if (!string.IsNullOrWhiteSpace(pause))
            bits.Add(pause!);

        return bits.Count == 0 ? null : string.Join(" • ", bits);
    }

    private string? BuildAdminJobDocumentVersionsLine(AdminJobListItem item)
    {
        if (!item.DocumentIngestionVersion.HasValue && !item.DocumentIndexedVersion.HasValue)
            return null;

        return ClientUiText.Format(
            "admin.jobs.card.document_versions",
            UiLang,
            item.DocumentIngestionVersion?.ToString(CultureInfo.InvariantCulture) ?? "?",
            item.DocumentIndexedVersion?.ToString(CultureInfo.InvariantCulture) ?? "?");
    }

    private string? BuildAdminJobAutoPauseLine(AdminJobListItem item)
    {
        if (!item.DocumentAutoIngestPaused.HasValue)
            return null;

        if (!item.DocumentAutoIngestPaused.Value)
            return null;

        if (IsAdminJobDocumentUnavailable(item))
            return null;

        var reason = TranslateAdminJobAutoPauseReason(item.DocumentAutoIngestPauseReason);
        if (!string.IsNullOrWhiteSpace(reason))
            return ClientUiText.Format("admin.jobs.auto_pause.on_reason", UiLang, reason);

        return ClientUiText.Get("admin.jobs.auto_pause.on", UiLang);
    }

    private string TranslateAdminJobFamily(string? type)
    {
        var normalized = NormalizeAdminUiToken(type);
        return normalized switch
        {
            "ingestion" => ClientUiText.Get("admin.jobs.type.ingestion", UiLang),
            "summary" => ClientUiText.Get("admin.jobs.type.summary", UiLang),
            _ => ClientUiText.Get("admin.jobs.type.unknown", UiLang)
        };
    }

    private string TranslateAdminJobType(string? jobType)
    {
        var normalized = NormalizeAdminUiToken(jobType);
        return normalized switch
        {
            "upsert" => ClientUiText.Get("admin.jobs.job_type.upsert", UiLang),
            "delete" => ClientUiText.Get("admin.jobs.job_type.delete", UiLang),
            "summary" => ClientUiText.Get("admin.jobs.job_type.summary", UiLang),
            _ => ClientUiText.Get("admin.jobs.job_type.unknown", UiLang)
        };
    }

    private string TranslateAdminJobStatus(string? status)
        => TranslateAdminJobStatusForDiagnostics(status, UiLang);

    internal static string TranslateAdminJobStatusForDiagnostics(string? status, string uiLanguage)
    {
        var normalized = NormalizeTrackedJobStatus(status);
        return normalized switch
        {
            "queued" => ClientUiText.Get("admin.jobs.status.queued", uiLanguage),
            "running" => ClientUiText.Get("admin.jobs.status.running", uiLanguage),
            "cancel_requested" => ClientUiText.Get("admin.jobs.status.cancel_requested", uiLanguage),
            "paused" => ClientUiText.Get("admin.jobs.status.paused", uiLanguage),
            "done" => ClientUiText.Get("admin.jobs.status.done", uiLanguage),
            "failed" => ClientUiText.Get("admin.jobs.status.failed", uiLanguage),
            "canceled" => ClientUiText.Get("admin.jobs.status.canceled", uiLanguage),
            _ => ClientUiText.Get("admin.jobs.status.unknown", uiLanguage)
        };
    }

    private string? TranslateAdminJobPhase(string? phase)
    {
        var normalized = NormalizeAdminUiToken(phase);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var compact = normalized
            .Replace('é', 'e')
            .Replace('è', 'e')
            .Replace('ê', 'e')
            .Replace('à', 'a')
            .Replace('ù', 'u')
            .Replace('ç', 'c');

        return compact switch
        {
            "preparing" => ClientUiText.Get("admin.jobs.phase.preparing", UiLang),
            "extracting" or "text_extraction" => ClientUiText.Get("admin.jobs.phase.extracting", UiLang),
            "ocr" or "ocr_extracting" => ClientUiText.Get("admin.jobs.phase.ocr", UiLang),
            "image_ocr" or "image-ocr" or "imageocr" => ClientUiText.Get("admin.jobs.phase.image_ocr", UiLang),
            "structuring" or "structure" or "structure_analysis" or "structure-analysis" => ClientUiText.Get("admin.jobs.phase.structuring", UiLang),
            "chunking" or "splitting" or "segmenting" => ClientUiText.Get("admin.jobs.phase.chunking", UiLang),
            "projecting" or "projection" or "contextualizing" or "contextualising" => ClientUiText.Get("admin.jobs.phase.projecting", UiLang),
            "embedding" or "tei" or "vectorizing" => ClientUiText.Get("admin.jobs.phase.embedding", UiLang),
            "upserting" or "indexing" or "index_update" or "index-update" => ClientUiText.Get("admin.jobs.phase.upserting", UiLang),
            "deleting" => ClientUiText.Get("admin.jobs.phase.deleting", UiLang),
            "resuming" => ClientUiText.Get("admin.jobs.phase.resuming", UiLang),
            "finalizing" or "finalize" or "finalisation" => ClientUiText.Get("admin.jobs.phase.finalizing", UiLang),
            "completed" => ClientUiText.Get("admin.jobs.phase.completed", UiLang),
            "failed_no_searchable_chunks" or "no_searchable_chunks" or "no-searchable-chunks" => ClientUiText.Get("admin.jobs.phase.no_searchable_text", UiLang),
            _ when compact.Contains("extract", StringComparison.OrdinalIgnoreCase) => ClientUiText.Get("admin.jobs.phase.extracting", UiLang),
            _ when compact.Contains("image", StringComparison.OrdinalIgnoreCase) && compact.Contains("ocr", StringComparison.OrdinalIgnoreCase) => ClientUiText.Get("admin.jobs.phase.image_ocr", UiLang),
            _ when compact.Contains("ocr", StringComparison.OrdinalIgnoreCase) => ClientUiText.Get("admin.jobs.phase.ocr", UiLang),
            _ when compact.Contains("structur", StringComparison.OrdinalIgnoreCase) => ClientUiText.Get("admin.jobs.phase.structuring", UiLang),
            _ when compact.Contains("chunk", StringComparison.OrdinalIgnoreCase) || compact.Contains("split", StringComparison.OrdinalIgnoreCase) || compact.Contains("segment", StringComparison.OrdinalIgnoreCase) => ClientUiText.Get("admin.jobs.phase.chunking", UiLang),
            _ when compact.Contains("project", StringComparison.OrdinalIgnoreCase) || compact.Contains("contextual", StringComparison.OrdinalIgnoreCase) => ClientUiText.Get("admin.jobs.phase.projecting", UiLang),
            _ when compact.Contains("embed", StringComparison.OrdinalIgnoreCase) || compact.Contains("tei", StringComparison.OrdinalIgnoreCase) || compact.Contains("vector", StringComparison.OrdinalIgnoreCase) => ClientUiText.Get("admin.jobs.phase.embedding", UiLang),
            _ when compact.Contains("upsert", StringComparison.OrdinalIgnoreCase) || compact.Contains("index", StringComparison.OrdinalIgnoreCase) || compact.Contains("mise a jour", StringComparison.OrdinalIgnoreCase) || compact.Contains("mise_a_jour", StringComparison.OrdinalIgnoreCase) => ClientUiText.Get("admin.jobs.phase.upserting", UiLang),
            _ when compact.Contains("summary", StringComparison.OrdinalIgnoreCase) || compact.Contains("resume", StringComparison.OrdinalIgnoreCase) || compact.Contains("summar", StringComparison.OrdinalIgnoreCase) => ClientUiText.Get("admin.jobs.phase.summarizing", UiLang),
            _ when compact.Contains("profile", StringComparison.OrdinalIgnoreCase) || compact.Contains("card", StringComparison.OrdinalIgnoreCase) || compact.Contains("enrich", StringComparison.OrdinalIgnoreCase) || compact.Contains("quality", StringComparison.OrdinalIgnoreCase) => ClientUiText.Get("admin.jobs.phase.enriching", UiLang),
            _ when compact.Contains("wait", StringComparison.OrdinalIgnoreCase) || compact.Contains("queued", StringComparison.OrdinalIgnoreCase) => ClientUiText.Get("admin.jobs.phase.waiting", UiLang),
            _ when compact.Contains("final", StringComparison.OrdinalIgnoreCase) || compact.Contains("commit", StringComparison.OrdinalIgnoreCase) => ClientUiText.Get("admin.jobs.phase.finalizing", UiLang),
            _ when compact.Contains("no_searchable", StringComparison.OrdinalIgnoreCase) || compact.Contains("searchable_chunks", StringComparison.OrdinalIgnoreCase) => ClientUiText.Get("admin.jobs.phase.no_searchable_text", UiLang),
            _ => ClientUiText.Get("admin.jobs.phase.processing", UiLang)
        };
    }

    private string? TranslateAdminJobEnqueueSource(string? enqueueSource)
    {
        var normalized = NormalizeAdminUiToken(enqueueSource);
        return normalized switch
        {
            "admin" => ClientUiText.Get("admin.jobs.enqueue_source.admin", UiLang),
            "api" => ClientUiText.Get("admin.jobs.enqueue_source.api", UiLang),
            "scanner" => ClientUiText.Get("admin.jobs.enqueue_source.scanner", UiLang),
            "watcher" => ClientUiText.Get("admin.jobs.enqueue_source.watcher", UiLang),
            _ => string.IsNullOrWhiteSpace(enqueueSource)
                ? null
                : LocalRuntimeText("Origine non reconnue", "Unknown origin", "Origen no reconocido", "Origem nao reconhecida", "Unbekannte Quelle", "Origine non riconosciuta", UiLang)
        };
    }

    private string? TranslateAdminJobDocumentStatus(string? documentStatus)
    {
        var normalized = NormalizeAdminUiToken(documentStatus);
        return normalized switch
        {
            "indexed" => ClientUiText.Get("admin.jobs.document_status.indexed", UiLang),
            "pending" => ClientUiText.Get("admin.jobs.document_status.pending", UiLang),
            "outdated" => ClientUiText.Get("admin.jobs.document_status.outdated", UiLang),
            "missing" => ClientUiText.Get("admin.jobs.document_status.missing", UiLang),
            "deleted" => ClientUiText.Get("admin.jobs.document_status.deleted", UiLang),
            "error" => ClientUiText.Get("admin.jobs.document_status.error", UiLang),
            "failed" => ClientUiText.Get("admin.jobs.document_status.failed", UiLang),
            "active" => ClientUiText.Get("admin.jobs.document_status.active", UiLang),
            "inactive" => ClientUiText.Get("admin.jobs.document_status.inactive", UiLang),
            _ => string.IsNullOrWhiteSpace(documentStatus)
                ? null
                : LocalRuntimeText("Etat document a verifier", "Document state to check", "Estado del documento por revisar", "Estado do documento a verificar", "Dokumentenstatus pruefen", "Stato documento da verificare", UiLang)
        };
    }

    private string? TranslateAdminJobRuntimeCapability(string? runtimeCapabilityKey)
    {
        var normalized = NormalizeAdminUiToken(runtimeCapabilityKey);
        return normalized switch
        {
            "capability_a.corpus_enrichment" => LocalRuntimeText(
                "Enrichissement de l'index (A)",
                "Index enrichment (A)",
                "Enriquecimiento del indice (A)",
                "Enriquecimento do indice (A)",
                "Index-Anreicherung (A)",
                "Arricchimento indice (A)",
                UiLang),
            "capability_b.backoffice_generation" => LocalRuntimeText(
                "Resumes serveur (B)",
                "Server summaries (B)",
                "Resumenes servidor (B)",
                "Resumos servidor (B)",
                "Server-Zusammenfassungen (B)",
                "Riepiloghi server (B)",
                UiLang),
            _ => string.IsNullOrWhiteSpace(runtimeCapabilityKey)
                ? null
                : LocalRuntimeText("Fonction serveur a verifier", "Server feature to review", "Funcion servidor por revisar", "Funcao servidor a verificar", "Serverfunktion pruefen", "Funzione server da verificare", UiLang)
        };
    }

    private string? TranslateAdminJobExecutionMode(string? executionMode)
    {
        var normalized = NormalizeAdminUiToken(executionMode);
        return normalized switch
        {
            "client_admin" => LocalRuntimeText(
                "Lance depuis le client admin",
                "Started from the admin client",
                "Lanzado desde el cliente admin",
                "Lançado pelo cliente admin",
                "Vom Admin-Client gestartet",
                "Avviato dal client admin",
                UiLang),
            "server_worker" or "backend_worker" or "worker" => LocalRuntimeText(
                "Traite par le serveur",
                "Processed by the server",
                "Procesado por el servidor",
                "Tratado pelo servidor",
                "Vom Server verarbeitet",
                "Elaborato dal server",
                UiLang),
            "idle_scheduler" or "auto_idle" => LocalRuntimeText(
                "Lance automatiquement quand l'ingestion est calme",
                "Started automatically when ingestion is quiet",
                "Lanzado automaticamente cuando la ingesta esta tranquila",
                "Lançado automaticamente quando a ingestao esta calma",
                "Automatisch gestartet, wenn die Ingestion ruhig ist",
                "Avviato automaticamente quando l'ingestione e tranquilla",
                UiLang),
            _ => string.IsNullOrWhiteSpace(executionMode)
                ? null
                : LocalRuntimeText("Origine a verifier", "Origin to review", "Origen por revisar", "Origem a verificar", "Quelle pruefen", "Origine da verificare", UiLang)
        };
    }

    private string? TranslateAdminJobAutoPauseReason(string? reason)
        => TranslateAdminJobAutoPauseReasonForDiagnostics(reason, UiLang);

    internal static string? TranslateAdminJobAutoPauseReasonForDiagnostics(string? reason, string uiLanguage)
    {
        var normalized = NormalizeAdminUiToken(reason);
        return normalized switch
        {
            "admin_cancel" => ClientUiText.Get("admin.jobs.auto_pause.reason.admin_cancel", uiLanguage),
            "admin_pause" => ClientUiText.Get("admin.jobs.auto_pause.reason.admin_pause", uiLanguage),
            "repeated_failures" => ClientUiText.Get("admin.jobs.auto_pause.reason.repeated_failures", uiLanguage),
            _ when IsKnownAdminJobExtractionDiagnosticReason(normalized) => DeterministicAgentText.ExtractionStatusLabel(normalized, uiLanguage),
            _ => string.IsNullOrWhiteSpace(reason)
                ? null
                : LocalRuntimeText("Pause automatique a verifier", "Automatic pause to check", "Pausa automatica por revisar", "Pausa automatica a verificar", "Automatische Pause pruefen", "Pausa automatica da verificare", uiLanguage)
        };
    }

    internal static string TranslateAdminJobResumeReasonForDiagnostics(string? reason, string uiLanguage)
    {
        var normalized = NormalizeAdminUiToken(reason);
        return normalized switch
        {
            "not_paused" => ClientUiText.Get("admin.jobs.resume_not_paused", uiLanguage),
            "missing_file" or "file_missing" or "source_missing" => ClientUiText.Get("admin.jobs.resume_missing_file", uiLanguage),
            "invalid_state" or "not_resumable" or "not_resumeable" => LocalRuntimeText(
                "Ce traitement ne peut pas etre repris dans son etat actuel.",
                "This processing item cannot be resumed in its current state.",
                "Este proceso no puede reanudarse en su estado actual.",
                "Este processamento nao pode ser retomado no estado atual.",
                "Diese Verarbeitung kann im aktuellen Zustand nicht fortgesetzt werden.",
                "Questa elaborazione non puo essere ripresa nello stato attuale.",
                uiLanguage),
            "not_owner" or "forbidden" or "unauthorized" => LocalRuntimeText(
                "La session admin actuelle ne peut pas reprendre ce traitement.",
                "The current admin session cannot resume this processing item.",
                "La sesion admin actual no puede reanudar este proceso.",
                "A sessao admin atual nao pode retomar este processamento.",
                "Die aktuelle Admin-Sitzung kann diese Verarbeitung nicht fortsetzen.",
                "La sessione admin corrente non puo riprendere questa elaborazione.",
                uiLanguage),
            _ => LocalRuntimeText(
                "raison serveur a verifier",
                "server reason to review",
                "motivo servidor por revisar",
                "motivo servidor a verificar",
                "Servergrund pruefen",
                "motivo server da verificare",
                uiLanguage)
        };
    }

    internal static string? TranslateAdminJobDiagnosticReason(string? value, string uiLanguage)
    {
        var reason = ResolveAdminJobDiagnosticReasonCode(value);
        if (string.IsNullOrWhiteSpace(reason))
            return null;

        return DeterministicAgentText.ExtractionStatusLabel(reason, uiLanguage);
    }

    internal static string? ResolveAdminJobDiagnosticReasonCode(string? value)
    {
        var normalized = NormalizeAdminUiToken(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        if (IsKnownAdminJobExtractionDiagnosticReason(normalized))
            return normalized;

        foreach (var property in new[] { "failure_reason", "ocr_failure", "text_status", "quality_status" })
        {
            var match = Regex.Match(
                normalized,
                $@"(?:^|[;\s,]){property}\s*[:=]\s*(?<code>[a-z0-9_]+)",
                RegexOptions.CultureInvariant);
            if (match.Success)
            {
                var code = match.Groups["code"].Value;
                if (IsKnownAdminJobExtractionDiagnosticReason(code))
                    return code;
            }
        }

        foreach (var code in AdminJobExtractionDiagnosticReasonCodes)
        {
            if (Regex.IsMatch(
                    normalized,
                    $@"(^|[^\p{{L}}\p{{N}}_]){Regex.Escape(code)}([^\p{{L}}\p{{N}}_]|$)",
                    RegexOptions.CultureInvariant))
            {
                return code;
            }
        }

        return null;
    }

    private static string NormalizeAdminUiToken(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (text.Length == 0)
            return string.Empty;

        var decomposed = text.Normalize(System.Text.NormalizationForm.FormD);
        var chars = new List<char>(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                chars.Add(ch);
        }

        return new string(chars.ToArray()).Normalize(System.Text.NormalizationForm.FormC);
    }

    private static bool IsKnownAdminJobExtractionDiagnosticReason(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && AdminJobExtractionDiagnosticReasonCodes.Contains(NormalizeAdminUiToken(value));

    private static readonly HashSet<string> AdminJobExtractionDiagnosticReasonCodes = new(StringComparer.Ordinal)
    {
        "ocr_required_but_disabled",
        "scanned_pdf_not_indexable",
        "no_indexable_text",
        "document_not_indexable",
        "ocr_disabled",
        "ocr_extraction_failed",
        "ocr_failed",
        "exit_code_non_zero",
        "ocr_output_missing",
        "no_novel_text",
        "low_text",
        "empty_text",
        "manual_review_empty_text",
        "manual_review_low_text",
        "ocr_failed_or_insufficient"
    };

    private bool IsAdminJobsCardInteractiveSource(DependencyObject? source, DependencyObject cardRoot)
    {
        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, cardRoot))
                return false;
            if (current is ButtonBase)
                return true;
            current = global::Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private FrameworkElement BuildAdminJobProgressBar(AdminJobListItem item)
    {
        var light = UseLightPalette();
        var normalizedStatus = NormalizeTrackedJobStatus(item.Status);
        var derivedPercent = item.ProgressPercent;
        if (!derivedPercent.HasValue && item.ProgressCurrent.HasValue && item.ProgressTotal.HasValue && item.ProgressTotal.Value > 0)
            derivedPercent = (int)Math.Round((double)item.ProgressCurrent.Value * 100d / item.ProgressTotal.Value);

        var resolvedPercent = derivedPercent.HasValue
            ? Math.Clamp(derivedPercent.Value, 0, 100)
            : normalizedStatus == "done"
                ? 100
                : 0;
        var isActiveLoadingState =
            (item.IsRunning || item.IsQueued || normalizedStatus == "cancel_requested")
            && resolvedPercent <= 0;
        var trackBrush = light ? UiBrush(0x22, 0x2C, 0x38) : UiBrush(0x1C, 0x25, 0x31);
        var accentBrush = GetAdminJobStatusForeground(normalizedStatus, light);
        const double barHeight = 4d;
        const double radius = 2d;

        if (isActiveLoadingState)
        {
            var loadingHost = new Grid
            {
                Height = barHeight,
                VerticalAlignment = VerticalAlignment.Center
            };

            loadingHost.Children.Add(new Border
            {
                Height = barHeight,
                CornerRadius = new CornerRadius(radius),
                Background = trackBrush,
                Opacity = light ? 0.18 : 0.9
            });

            var loadingBar = new ProgressBar
            {
                IsIndeterminate = true,
                Height = barHeight,
                BorderThickness = new Thickness(0),
                Foreground = accentBrush,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                VerticalAlignment = VerticalAlignment.Center
            };
            loadingHost.Children.Add(loadingBar);
            return loadingHost;
        }

        var host = new Grid
        {
            Height = barHeight,
            VerticalAlignment = VerticalAlignment.Center
        };

        host.Children.Add(new Border
        {
            Height = barHeight,
            CornerRadius = new CornerRadius(radius),
            Background = trackBrush,
            Opacity = light ? 0.16 : 0.88
        });

        if (resolvedPercent <= 0)
            return host;

        var fill = new Border
        {
            Height = barHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(radius),
            Background = accentBrush,
            Opacity = normalizedStatus is "done" or "failed" or "canceled" ? 0.95 : 1d
        };

        void UpdateFillWidth()
        {
            var width = host.ActualWidth * resolvedPercent / 100d;
            fill.Width = width <= 0 ? 0 : Math.Max(width, resolvedPercent >= 100 ? host.ActualWidth : 10d);
        }

        host.SizeChanged += (_, __) => UpdateFillWidth();
        host.Loaded += (_, __) => UpdateFillWidth();

        if (resolvedPercent < 100)
        {
            var sheen = new Border
            {
                Height = barHeight,
                Width = 28,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(radius),
                Opacity = light ? 0.18 : 0.12,
                Background = light ? UiBrush(0xFF, 0xFF, 0xFF) : UiBrush(0xE7, 0xEF, 0xFA),
                IsHitTestVisible = false
            };

            void UpdateSheen()
            {
                var width = fill.Width;
                sheen.Visibility = width > 20 ? Visibility.Visible : Visibility.Collapsed;
                if (width > 20)
                {
                    sheen.Margin = new Thickness(Math.Max(2d, width - sheen.Width - 4d), 0, 0, 0);
                }
            }

            host.SizeChanged += (_, __) => UpdateSheen();
            host.Loaded += (_, __) => UpdateSheen();
            host.Children.Add(fill);
            host.Children.Add(sheen);
            return host;
        }

        host.Children.Add(fill);
        return host;
    }

    private TextBlock BuildAdminJobStatusBadge(string status)
    {
        var light = UseLightPalette();
        var normalized = NormalizeTrackedJobStatus(status);
        var statusKey = "admin.jobs.status." + normalized;
        var statusLabel = ClientUiText.Get(statusKey, UiLang);
        if (string.Equals(statusLabel, statusKey, StringComparison.Ordinal))
            statusLabel = ClientUiText.Get("admin.jobs.status.unknown", UiLang);
        return new TextBlock
        {
            Text = statusLabel,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = GetAdminJobStatusForeground(normalized, light),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0)
        };
    }

    private Brush GetAdminJobStatusBackground(string status, bool light)
    {
        status = NormalizeTrackedJobStatus(status);
        return status switch
        {
            "done" => light ? UiBrush(0xEC, 0xF7, 0xF2) : UiBrush(0x12, 0x24, 0x1E),
            "failed" or "canceled" => light ? UiBrush(0xFD, 0xEF, 0xEE) : UiBrush(0x2A, 0x14, 0x16),
            "cancel_requested" => light ? UiBrush(0xF8, 0xF3, 0xE8) : UiBrush(0x2A, 0x22, 0x16),
            "paused" => light ? UiBrush(0xF3, 0xEE, 0xFB) : UiBrush(0x21, 0x18, 0x2B),
            "running" => light ? UiBrush(0xEC, 0xF3, 0xFB) : UiBrush(0x11, 0x23, 0x33),
            "queued" => light ? UiBrush(0xF8, 0xF3, 0xE8) : UiBrush(0x28, 0x20, 0x14),
            _ => light ? UiBrush(0xF4, 0xF7, 0xFB) : UiBrush(0x14, 0x1B, 0x24)
        };
    }

    private Brush GetAdminJobStatusBorder(string status, bool light)
    {
        status = NormalizeTrackedJobStatus(status);
        return status switch
        {
            "done" => light ? UiBrush(0xC6, 0xE5, 0xD7) : UiBrush(0x2A, 0x54, 0x43),
            "failed" or "canceled" => light ? UiBrush(0xF1, 0xC7, 0xC3) : UiBrush(0x6A, 0x2C, 0x31),
            "cancel_requested" => light ? UiBrush(0xE9, 0xD8, 0xBA) : UiBrush(0x6A, 0x4F, 0x24),
            "paused" => light ? UiBrush(0xD7, 0xC8, 0xEE) : UiBrush(0x57, 0x43, 0x72),
            "running" => light ? UiBrush(0xC5, 0xD8, 0xEA) : UiBrush(0x2B, 0x4B, 0x6B),
            "queued" => light ? UiBrush(0xE9, 0xD8, 0xBA) : UiBrush(0x5F, 0x46, 0x24),
            _ => light ? UiBrush(0xC9, 0xD4, 0xE1) : UiBrush(0x2B, 0x35, 0x41)
        };
    }

    private Brush GetAdminJobStatusForeground(string status, bool light)
    {
        status = NormalizeTrackedJobStatus(status);
        return status switch
        {
            "done" => light ? UiBrush(0x2E, 0x7D, 0x5A) : UiBrush(0x66, 0xD1, 0x9E),
            "failed" or "canceled" => light ? UiBrush(0xB4, 0x23, 0x18) : UiBrush(0xFF, 0x8A, 0x80),
            "cancel_requested" => light ? UiBrush(0x9A, 0x62, 0x00) : UiBrush(0xFF, 0xC7, 0x6A),
            "paused" => light ? UiBrush(0x6B, 0x46, 0xA7) : UiBrush(0xC4, 0xA7, 0xF2),
            "running" => light ? UiBrush(0x2B, 0x5D, 0x91) : UiBrush(0x78, 0xB4, 0xF0),
            "queued" => light ? UiBrush(0x9A, 0x62, 0x00) : UiBrush(0xFF, 0xC7, 0x6A),
            _ => light ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE)
        };
    }
}
