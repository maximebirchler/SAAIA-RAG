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
        LlmStatusText.Text = SZ("Test en cours...", "Testing...", "Probando...", "A testar...", "Test wird ausgefuehrt...", "Test in corso...");

        try
        {
            var s = ReadSettingsFromUi();

            if (!s.UseLocalLlm)
            {
                LlmStatusText.Text = SZ("Le LLM local est desactive.", "Local LLM is disabled.", "El LLM local esta desactivado.", "O LLM local esta desativado.", "Lokales LLM ist deaktiviert.", "Il LLM locale e disattivato.");
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
                LlmStatusText.Text = SZ("Aucun modele retourne.", "No models returned.", "No se devolvio ningun modelo.", "Nenhum modelo foi devolvido.", "Keine Modelle zurueckgegeben.", "Nessun modello restituito.");
                return;
            }

            var ok = !string.IsNullOrWhiteSpace(modelId) && models.Any(m => string.Equals(m, modelId, StringComparison.OrdinalIgnoreCase));

            LlmStatusText.Text = ok
                ? SZ(
                    $"/v1/models OK (modele trouve : {modelId})",
                    $"/v1/models OK (model found: {modelId})",
                    $"/v1/models OK (modelo encontrado: {modelId})",
                    $"/v1/models OK (modelo encontrado: {modelId})",
                    $"/v1/models OK (Modell gefunden: {modelId})",
                    $"/v1/models OK (modello trovato: {modelId})")
                : SZ(
                    $"/v1/models OK mais modelId introuvable. Premier={models[0]}",
                    $"/v1/models OK but modelId not found. First={models[0]}",
                    $"/v1/models OK pero modelId no encontrado. Primero={models[0]}",
                    $"/v1/models OK mas modelId nao encontrado. Primeiro={models[0]}",
                    $"/v1/models OK, aber modelId nicht gefunden. Erstes={models[0]}",
                    $"/v1/models OK ma modelId non trovato. Primo={models[0]}");
        }
        catch (Exception ex)
        {
            LlmStatusText.Text = SZ("Echec /v1/models : ", "/v1/models failed: ", "Error /v1/models: ", "Falha /v1/models: ", "/v1/models fehlgeschlagen: ", "Errore /v1/models: ") + ex.Message;
        }
    }

    private async void StartLocalLlm_Click(object sender, RoutedEventArgs e)
    {
        LlmStatusText.Text = SZ("Demarrage...", "Starting...", "Iniciando...", "A iniciar...", "Startet...", "Avvio...");

        try
        {
            var s = ReadSettingsFromUi();
            s.UseLocalLlm = true;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, s.StartupTimeoutSeconds)));
            var (ok, msg) = await _llmProc.StartAsync(s, cts.Token);

            LlmStatusText.Text = ok ? msg : SZ("Echec : ", "Failed: ", "Error: ", "Falha: ", "Fehler: ", "Errore: ") + msg;
        }
        catch (Exception ex)
        {
            LlmStatusText.Text = SZ("Echec du demarrage : ", "Start failed: ", "Error al iniciar: ", "Falha ao iniciar: ", "Start fehlgeschlagen: ", "Avvio non riuscito: ") + ex.Message;
        }
    }

    private void StopLocalLlm_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _llmProc.Stop();
            LlmStatusText.Text = SZ("Arrete.", "Stopped.", "Detenido.", "Parado.", "Gestoppt.", "Fermato.");
        }
        catch (Exception ex)
        {
            LlmStatusText.Text = SZ("Echec de l'arret : ", "Stop failed: ", "Error al detener: ", "Falha ao parar: ", "Stop fehlgeschlagen: ", "Arresto non riuscito: ") + ex.Message;
        }
    }
}
