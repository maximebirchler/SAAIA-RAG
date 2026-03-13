using SAAIA.Client.WinUI.Localization;
using Xunit;

namespace SAAIA.Client.ToolAgent.Tests;

public sealed class DeterministicLocalizationTests
{
    [Theory]
    [InlineData("fr", "Routeur…")]
    [InlineData("en", "Router…")]
    [InlineData("es", "Enrutador…")]
    [InlineData("pt", "Roteador…")]
    [InlineData("de", "Router…")]
    [InlineData("it", "Router…")]
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
}
