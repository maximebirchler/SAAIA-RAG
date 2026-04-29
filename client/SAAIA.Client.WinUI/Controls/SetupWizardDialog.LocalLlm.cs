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
                LlmStatusText.Text = SZ("Le LLM local est désactivé.", "Local LLM is disabled.", "El LLM local está desactivado.", "O LLM local está desativado.", "Lokales LLM ist deaktiviert.", "Il LLM locale è disattivato.");
                return;
            }

            var baseUrl = s.LlmBaseUrl;
            var modelId = s.ModelId;

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                LlmStatusText.Text = SZ("URL de base LLM manquante.", "Missing LLM base url.", "Falta la URL base del LLM.", "Falta o URL base do LLM.", "LLM-Basis-URL fehlt.", "URL base LLM mancante.");
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
            LlmStatusText.Text = SZ("Échec /v1/models : ", "/v1/models failed: ", "Error /v1/models: ", "Falha /v1/models: ", "/v1/models fehlgeschlagen: ", "Errore /v1/models: ") + ex.Message;
        }
    }

    private async void StartLocalLlm_Click(object sender, RoutedEventArgs e)
    {
        LlmStatusText.Text = SZ("Démarrage…", "Starting…", "Iniciando…", "A iniciar…", "Startet…", "Avvio…");

        try
        {
            var s = ReadSettingsFromUi();
            s.UseLocalLlm = true;

            // Guard against the most common confusing failure ("Missing LLM runtime path"):
            // if the user has no llama-server.exe configured but tries to launch one, give a
            // friendly hint instead of leaking the internal error string.
            if (string.IsNullOrWhiteSpace(s.LlamaExePath))
            {
                LlmStatusText.Text = SZ(
                    "Aucun exécutable LLM local configuré. Si l'assistant tourne ailleurs (Docker ou serveur distant), décochez « Activer l'assistant (LLM) » — pas besoin de démarrer quoi que ce soit ici.",
                    "No local LLM executable configured. If the assistant runs elsewhere (Docker or remote server), uncheck 'Enable assistant (LLM)' — nothing to start here.",
                    "Sin ejecutable LLM local configurado. Si el asistente corre en otro lugar (Docker o servidor remoto), desmarque 'Activar asistente (LLM)' — no hay que iniciar nada aquí.",
                    "Sem executável LLM local configurado. Se o assistente correr noutro lado (Docker ou servidor remoto), desmarque 'Ativar assistente (LLM)' — nada para iniciar aqui.",
                    "Keine lokale LLM-Executable konfiguriert. Laeuft der Assistent woanders (Docker oder Remote-Server), deaktivieren Sie 'Assistenten aktivieren (LLM)' — hier ist nichts zu starten.",
                    "Nessun eseguibile LLM locale configurato. Se l'assistente gira altrove (Docker o server remoto), deseleziona 'Attiva assistente (LLM)' — niente da avviare qui.");
                return;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, s.StartupTimeoutSeconds)));
            var (ok, msg) = await _llmProc.StartAsync(s, cts.Token);

            LlmStatusText.Text = ok ? msg : SZ("Échec : ", "Failed: ", "Error: ", "Falha: ", "Fehler: ", "Errore: ") + msg;
        }
        catch (Exception ex)
        {
            LlmStatusText.Text = SZ("Échec du démarrage : ", "Start failed: ", "Error al iniciar: ", "Falha ao iniciar: ", "Start fehlgeschlagen: ", "Avvio non riuscito: ") + ex.Message;
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
            LlmStatusText.Text = SZ("Échec de l'arrêt : ", "Stop failed: ", "Error al detener: ", "Falha ao parar: ", "Stop fehlgeschlagen: ", "Arresto non riuscito: ") + ex.Message;
        }
    }
}
