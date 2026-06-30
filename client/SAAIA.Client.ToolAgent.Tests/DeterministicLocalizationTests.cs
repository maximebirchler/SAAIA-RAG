using System;
using SAAIA.Client.WinUI.Localization;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class DeterministicLocalizationTests
{
    [Theory]
    [InlineData("fr", "J'analyse la demande…")]
    [InlineData("en", "I am reading the request…")]
    [InlineData("es", "Estoy leyendo la solicitud…")]
    [InlineData("pt", "Estou a ler o pedido…")]
    [InlineData("de", "Ich lese die Anfrage…")]
    [InlineData("it", "Sto leggendo la richiesta…")]
    public void Phase_router_is_localized(string language, string expected)
    {
        Assert.Equal(expected, DeterministicAgentText.PhaseRouter(language));
    }

    [Theory]
    [InlineData("es", "Actualmente hay 12 documento(s) indexado(s) en el servidor.")]
    [InlineData("pt", "Atualmente há 12 documento(s) indexado(s) no servidor.")]
    [InlineData("de", "Derzeit sind 12 Dokument(e) auf dem Server indexiert.")]
    [InlineData("it", "Attualmente ci sono 12 documento/i indicizzato/i sul server.")]
    public void Documents_count_answer_follows_language(string language, string expected)
    {
        Assert.Equal(expected, DeterministicAgentText.DocumentsCount(12, language));
    }

    [Theory]
    [InlineData("es", "No se encontraron carpetas vacías en el servidor.")]
    [InlineData("pt", "Nenhuma pasta vazia foi encontrada no servidor.")]
    [InlineData("de", "Auf dem Server wurden keine leeren Ordner gefunden.")]
    [InlineData("it", "Non sono state trovate cartelle vuote sul server.")]
    public void Empty_folder_fallback_is_localized(string language, string expected)
    {
        Assert.Equal(expected, DeterministicAgentText.NoEmptyFoldersFound(language));
    }

    [Theory]
    [InlineData("es", "Esta acción requiere una sesión de administrador activa en el cliente WinUI.")]
    [InlineData("pt", "Esta ação requer uma sessão de administrador ativa no cliente WinUI.")]
    [InlineData("de", "Diese Aktion erfordert eine aktive Admin-Sitzung im WinUI-Client.")]
    [InlineData("it", "Questa azione richiede una sessione admin attiva nel client WinUI.")]
    public void Tool_failure_admin_required_is_localized(string language, string expected)
    {
        Assert.Equal(expected, DeterministicAgentText.ToolFailureAdminRequired(language));
    }


    [Theory]
    [InlineData("es", "La sesión de administrador de WinUI es inválida o ya no está autorizada para esta acción.")]
    [InlineData("pt", "A sessão de administrador do WinUI é inválida ou não está mais autorizada para esta ação.")]
    [InlineData("de", "Die WinUI-Admin-Sitzung ist ungültig oder für diese Aktion nicht mehr berechtigt.")]
    [InlineData("it", "La sessione admin WinUI non è valida o non è più autorizzata per questa azione.")]
    public void Tool_failure_admin_invalid_or_forbidden_is_localized(string language, string expected)
    {
        Assert.Equal(expected, DeterministicAgentText.ToolFailureAdminInvalidOrForbidden(language));
    }

    [Theory]
    [InlineData("es", "Estoy comprobando si ya existe un resumen almacenado para PDF12…")]
    [InlineData("pt", "Estou verificando se já existe um resumo armazenado para PDF12…")]
    [InlineData("de", "Ich prüfe, ob bereits eine gespeicherte Zusammenfassung existiert für PDF12…")]
    [InlineData("it", "Sto verificando se esiste già un riassunto salvato per PDF12…")]
    public void Summary_progress_messages_include_localized_docref_suffix(string language, string expected)
    {
        Assert.Equal(expected, DeterministicAgentText.ProgressCheckStoredSummaryForDocument("PDF12", language));
    }

    [Theory]
    [InlineData("es", "Búsqueda de documentos…")]
    [InlineData("pt", "Pesquisa de documentos…")]
    [InlineData("de", "Dokumentsuche…")]
    [InlineData("it", "Ricerca documenti…")]
    public void Tool_phase_is_localized(string language, string expected)
    {
        Assert.Equal(expected, DeterministicAgentText.ToolPhase("documents.list", language));
    }

    [Theory]
    [InlineData("es", "- Carpetas de segundo nivel: 4")]
    [InlineData("pt", "- Pastas de segundo nível: 4")]
    [InlineData("de", "- Ordner der zweiten Ebene: 4")]
    [InlineData("it", "- Cartelle di secondo livello: 4")]
    public void Folder_depth_label_is_localized(string language, string expected)
    {
        Assert.Equal(expected, $"- {DeterministicAgentText.FolderDepthLabel(2, language)}: 4");
    }

    [Theory]
    [InlineData("es", "⚠️ La respuesta interna llegó con un formato inesperado. Vuelve a intentarlo o reformúlala.")]
    [InlineData("pt", "⚠️ A resposta interna chegou em um formato inesperado. Tente novamente ou reformule.")]
    [InlineData("de", "⚠️ Die interne Antwort kam in einem unerwarteten Format an. Bitte versuche es erneut oder formuliere die Frage um.")]
    [InlineData("it", "⚠️ La risposta interna è arrivata in un formato inatteso. Riprova o riformula la richiesta.")]
    public void Json_envelope_error_is_localized(string language, string expected)
    {
        Assert.Equal(expected, DeterministicAgentText.JsonEnvelopeError(language));
    }

    [Theory]
    [InlineData("fr", "  • Aucun dossier ni sous-dossier.")]
    [InlineData("en", "  • No folder or subfolder.")]
    public void Empty_structure_line_is_scope_free(string language, string expected)
    {
        Assert.Equal(expected, DeterministicAgentText.NoSubfoldersInScope(language));
    }


    [Theory]
    [InlineData("es", "No he encontrado el documento solicitado en el catálogo indexado.")]
    [InlineData("pt", "Não encontrei o documento solicitado no catálogo indexado.")]
    [InlineData("de", "Ich konnte das angeforderte Dokument im indizierten Katalog nicht finden.")]
    [InlineData("it", "Non ho trovato il documento richiesto nel catalogo indicizzato.")]
    public void Tool_failure_document_not_found_is_localized(string language, string expected)
    {
        Assert.Equal(expected, DeterministicAgentText.ToolFailureDocumentNotFound(language));
    }

    [Theory]
    [InlineData("es", "No he encontrado la fuente solicitada entre los documentos ya resueltos en esta conversación.")]
    [InlineData("pt", "Não encontrei a fonte solicitada entre os documentos já resolvidos nesta conversa.")]
    [InlineData("de", "Ich konnte die angeforderte Quelle nicht unter den in dieser Unterhaltung bereits aufgelösten Dokumenten finden.")]
    [InlineData("it", "Non ho trovato la fonte richiesta tra i documenti già risolti in questa conversazione.")]
    public void Tool_failure_source_not_found_is_localized(string language, string expected)
    {
        Assert.Equal(expected, DeterministicAgentText.ToolFailureSourceNotFound(language));
    }

    [Theory]
    [InlineData("fr", "Diagnostic qualite d'extraction :", "Diagnostic des pages pour sample.pdf :")]
    [InlineData("en", "Extraction quality diagnostics:", "Page diagnostics for sample.pdf:")]
    [InlineData("es", "Diagnostico de calidad de extraccion:", "Diagnostico de paginas para sample.pdf:")]
    [InlineData("pt", "Diagnostico de qualidade de extracao:", "Diagnostico de paginas para sample.pdf:")]
    [InlineData("de", "Diagnose der Extraktionsqualitaet:", "Seitendiagnose fuer sample.pdf:")]
    [InlineData("it", "Diagnostica qualita di estrazione:", "Diagnostica pagine per sample.pdf:")]
    public void Extraction_diagnostics_labels_are_localized(string language, string expectedQuality, string expectedPages)
    {
        Assert.Equal(expectedQuality, DeterministicAgentText.ExtractionQualityHeader(language));
        Assert.Equal(expectedPages, DeterministicAgentText.ExtractionPagesHeader("sample.pdf", language));
        Assert.False(string.IsNullOrWhiteSpace(DeterministicAgentText.ExtractionOcrRecommended(language)));
        Assert.False(string.IsNullOrWhiteSpace(DeterministicAgentText.ExtractionReviewRecommended(language)));
        Assert.DoesNotContain("_", DeterministicAgentText.ExtractionStatusLabel("ocr_required_but_disabled", language));
        Assert.DoesNotContain("_", DeterministicAgentText.ExtractionStatusLabel("scanned_pdf_not_indexable", language));
        Assert.DoesNotContain("_", DeterministicAgentText.ExtractionStatusLabel("ocr_disabled", language));
        foreach (var status in new[]
                 {
                     "image_ocr_applied_ok",
                     "ocr_applied_ok_with_page_warnings",
                     "ocr_applied_low_confidence",
                     "manual_review_empty_text",
                     "manual_review_low_text",
                     "extraction_ok_with_page_warnings",
                     "text_extraction_ok_with_images",
                     "extraction_ok",
                     "ocr_failed"
                 })
        {
            Assert.DoesNotContain("_", DeterministicAgentText.ExtractionStatusLabel(status, language));
        }

        Assert.False(string.IsNullOrWhiteSpace(DeterministicAgentText.ExtractionDocumentStatus("error", language)));
        Assert.False(string.IsNullOrWhiteSpace(DeterministicAgentText.ExtractionProcessingRunStatus("failed", language)));
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("pt")]
    [InlineData("de")]
    [InlineData("it")]
    public void Summary_status_suffixes_do_not_leak_server_tokens(string language)
    {
        var texts = new[]
        {
            DeterministicAgentText.SummaryStatusStaleSuffix(language),
            DeterministicAgentText.SummaryStatusActiveJobSuffix("enqueue_profile_refresh", language),
            DeterministicAgentText.SummaryStatusCapabilityActionSuffix("enqueue_profile_refresh", language),
            DeterministicAgentText.SummaryStatusPolicyBlockedSuffix("runtime_unqualified", language),
            DeterministicAgentText.SummaryStatusLastJobIssueSuffix("failed", "timeout while calling worker", language)
        };

        foreach (var text in texts)
        {
            Assert.DoesNotContain("Capability B", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("enqueue_profile_refresh", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("runtime_unqualified", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("timeout while calling worker", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("_", text);
        }
    }

}
