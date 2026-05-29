using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;

using SAAIA.Client.WinUI.Services;

namespace SAAIA.Client.WinUI.Controls;

public sealed partial class SetupWizardDialog
{
    private async void TestModels_Click(object sender, RoutedEventArgs e)
    {
        LlmStatusText.Text = SZ("Test en cours…", "Testing…", "Probando…", "A testar…", "Test wird ausgefuehrt…", "Test in corso…");

        try
        {
            var s = ReadSettingsFromUi();

            if (!s.UseLocalLlm)
            {
                LlmStatusText.Text = SZ("L'assistant local est désactivé.", "Local assistant is disabled.", "El asistente local está desactivado.", "O assistente local está desativado.", "Lokaler Assistent ist deaktiviert.", "L'assistente locale è disattivato.");
                return;
            }

            var baseUrl = s.LlmBaseUrl;
            var modelId = s.ModelId;

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                LlmStatusText.Text = SZ("URL de l'assistant local manquante.", "Missing local assistant URL.", "Falta la URL del asistente local.", "Falta o URL do assistente local.", "URL des lokalen Assistenten fehlt.", "URL assistente locale mancante.");
                return;
            }

            var llm = new OpenAiLlmClient();
            llm.Configure(baseUrl, modelId);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var models = await llm.ListModelsAsync(cts.Token);

            if (models.Count == 0)
            {
                LlmStatusText.Text = SZ("Aucun modèle retourné.", "No models returned.", "No se devolvió ningún modelo.", "Nenhum modelo foi devolvido.", "Keine Modelle zurueckgegeben.", "Nessun modello restituito.");
                return;
            }

            var ok = !string.IsNullOrWhiteSpace(modelId) && models.Any(m => string.Equals(m, modelId, StringComparison.OrdinalIgnoreCase));

            LlmStatusText.Text = ok
                ? SZ(
                    $"/v1/models OK (modèle trouvé : {modelId})",
                    $"/v1/models OK (model found: {modelId})",
                    $"/v1/models OK (modelo encontrado: {modelId})",
                    $"/v1/models OK (modelo encontrado: {modelId})",
                    $"/v1/models OK (Modell gefunden: {modelId})",
                    $"/v1/models OK (modello trovato: {modelId})")
                : SZ(
                    $"/v1/models OK mais modelId introuvable. Premier={models[0]}",
                    $"/v1/models OK but modelId not found. First={models[0]}",
                    $"/v1/models OK pero modelId no encontrado. Primero={models[0]}",
                    $"/v1/models OK mas modelId não encontrado. Primeiro={models[0]}",
                    $"/v1/models OK, aber modelId nicht gefunden. Erstes={models[0]}",
                    $"/v1/models OK ma modelId non trovato. Primo={models[0]}");
        }
        catch (Exception ex)
        {
            ClientLog.Exception("SetupWizard.LocalLlm.TestModels", ex);
            LlmStatusText.Text = SZ("Impossible de joindre l'assistant local. Vérifiez qu'il est démarré, puis réessayez.", "Could not reach the local assistant. Check that it is running, then try again.", "No se pudo contactar con el asistente local. Comprueba que esté iniciado y vuelve a intentarlo.", "Não foi possível contactar o assistente local. Verifica se está iniciado e tenta novamente.", "Der lokale Assistent ist nicht erreichbar. Prüfe, ob er läuft, und versuche es erneut.", "Impossibile raggiungere l'assistente locale. Verifica che sia avviato, poi riprova.");
        }
    }

    private async void StartLocalLlm_Click(object sender, RoutedEventArgs e)
    {
        LlmStatusText.Text = SZ("Démarrage…", "Starting…", "Iniciando…", "A iniciar…", "Startet…", "Avvio…");

        try
        {
            var s = ReadSettingsFromUi();
            s.UseLocalLlm = true;

            // Guard against the most common confusing failure ("Missing local engine path"):
            // if the user has no llama-server.exe configured but tries to launch one, give a
            // friendly hint instead of leaking the internal error string.
            if (string.IsNullOrWhiteSpace(s.LlamaExePath))
            {
                LlmStatusText.Text = SZ(
                    "Aucun moteur local configuré. Si l'assistant tourne ailleurs (Docker ou serveur distant), décochez « Activer l'assistant local » : rien n'est à démarrer ici.",
                    "No local engine is configured. If the assistant runs elsewhere (Docker or remote server), uncheck 'Enable local assistant': there is nothing to start here.",
                    "No hay motor local configurado. Si el asistente corre en otro lugar (Docker o servidor remoto), desmarca 'Activar asistente local': aquí no hay nada que iniciar.",
                    "Sem motor local configurado. Se o assistente corre noutro lado (Docker ou servidor remoto), desmarca 'Ativar assistente local': aqui não há nada para iniciar.",
                    "Keine lokale Engine konfiguriert. Laeuft der Assistent woanders (Docker oder Remote-Server), deaktiviere 'Lokalen Assistenten aktivieren': hier ist nichts zu starten.",
                    "Nessun motore locale configurato. Se l'assistente gira altrove (Docker o server remoto), deseleziona 'Attiva assistente locale': qui non c'è nulla da avviare.");
                return;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, s.StartupTimeoutSeconds)));
            var (ok, msg) = await _llmProc.StartAsync(s, cts.Token);

            if (ok)
            {
                LlmStatusText.Text = SZ("Assistant local démarré.", "Local assistant started.", "Asistente local iniciado.", "Assistente local iniciado.", "Lokaler Assistent gestartet.", "Assistente locale avviato.");
            }
            else
            {
                ClientLog.Warn("[SetupWizard.LocalLlm.Start] " + msg);
                LlmStatusText.Text = SZ("L'assistant local n'a pas pu démarrer. Vérifiez le chemin du moteur, le modèle et le port.", "The local assistant could not start. Check the engine path, model and port.", "El asistente local no pudo iniciarse. Revisa la ruta del motor, el modelo y el puerto.", "O assistente local não conseguiu iniciar. Verifica o caminho do motor, o modelo e a porta.", "Der lokale Assistent konnte nicht starten. Prüfe Engine-Pfad, Modell und Port.", "L'assistente locale non è riuscito ad avviarsi. Controlla percorso del motore, modello e porta.");
            }
        }
        catch (Exception ex)
        {
            ClientLog.Exception("SetupWizard.LocalLlm.Start", ex);
            LlmStatusText.Text = SZ("L'assistant local n'a pas pu démarrer. Le détail technique est dans les logs.", "The local assistant could not start. Technical details are in the logs.", "El asistente local no pudo iniciarse. El detalle técnico está en los logs.", "O assistente local não conseguiu iniciar. O detalhe técnico está nos logs.", "Der lokale Assistent konnte nicht starten. Details stehen in den Logs.", "L'assistente locale non è riuscito ad avviarsi. I dettagli tecnici sono nei log.");
        }
    }

    private void StopLocalLlm_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _llmProc.Stop();
            LlmStatusText.Text = SZ("Arrêté.", "Stopped.", "Detenido.", "Parado.", "Gestoppt.", "Fermato.");
        }
        catch (Exception ex)
        {
            ClientLog.Exception("SetupWizard.LocalLlm.Stop", ex);
            LlmStatusText.Text = SZ("Impossible d'arrêter l'assistant local. Le détail technique est dans les logs.", "Could not stop the local assistant. Technical details are in the logs.", "No se pudo detener el asistente local. El detalle técnico está en los logs.", "Não foi possível parar o assistente local. O detalhe técnico está nos logs.", "Der lokale Assistent konnte nicht gestoppt werden. Details stehen in den Logs.", "Impossibile arrestare l'assistente locale. I dettagli tecnici sono nei log.");
        }
    }
}
