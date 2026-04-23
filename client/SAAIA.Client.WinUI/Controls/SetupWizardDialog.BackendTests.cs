using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;

using SAAIA.Client.WinUI.Services;

namespace SAAIA.Client.WinUI.Controls;

public sealed partial class SetupWizardDialog
{
    private async void TestReady_Click(object sender, RoutedEventArgs e)
    {
        ReadyStatusText.Text = SZ("Test en cours...", "Testing...", "Probando...", "A testar...", "Test wird ausgefuehrt...", "Test in corso...");
        ReadyRawBox.Visibility = Visibility.Collapsed;
        ReadyRawBox.Text = "";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var resp = await http.GetAsync(_backendUrl + "/ready");
            var raw = await resp.Content.ReadAsStringAsync();

            var ok = resp.IsSuccessStatusCode;

            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("ok", out var p) && p.ValueKind == JsonValueKind.True)
                    ok = true;
            }
            catch
            {
            }

            ReadyStatusText.Text = ok
                ? SZ("/ready OK", "/ready OK", "/ready OK", "/ready OK", "/ready OK", "/ready OK")
                : SZ(
                    $"Echec /ready (HTTP {(int)resp.StatusCode})",
                    $"/ready failed (HTTP {(int)resp.StatusCode})",
                    $"Error /ready (HTTP {(int)resp.StatusCode})",
                    $"Falha /ready (HTTP {(int)resp.StatusCode})",
                    $"Fehler bei /ready (HTTP {(int)resp.StatusCode})",
                    $"Errore /ready (HTTP {(int)resp.StatusCode})");

            ReadyRawBox.Text = raw;
            ReadyRawBox.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ReadyStatusText.Text = SZ("Erreur /ready : ", "/ready error: ", "Error /ready: ", "Erro /ready: ", "/ready-Fehler: ", "Errore /ready: ") + ex.Message;
        }
    }

    private async void TestApiKey_Click(object sender, RoutedEventArgs e)
    {
        ApiKeyStatusText.Text = SZ("Test en cours...", "Testing...", "Probando...", "A testar...", "Test wird ausgefuehrt...", "Test in corso...");

        try
        {
            var apiKey = (ApiKeyBox.Password ?? "").Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                ApiKeyStatusText.Text = SZ("Cle API manquante.", "Missing API key.", "Falta la clave API.", "Falta a chave API.", "API-Schluessel fehlt.", "Chiave API mancante.");
                return;
            }

            if (string.IsNullOrWhiteSpace(_userId))
            {
                ApiKeyStatusText.Text = SZ("userId manquant (client).", "Missing userId (client).", "Falta userId (cliente).", "Falta userId (cliente).", "userId fehlt (Client).", "userId mancante (client).");
                return;
            }

            var api = new ApiClient();
            api.Configure(_backendUrl, apiKey, _userId);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var created = await api.CreateSessionAsync("setup-test", clientUser: Environment.UserName, cts.Token);
            await api.DeleteSessionAsync(created.SessionId, cts.Token);

            ApiKeyStatusText.Text = SZ("Cle API OK (chat-store).", "API key OK (chat-store).", "Clave API OK (chat-store).", "Chave API OK (chat-store).", "API-Schluessel OK (chat-store).", "Chiave API OK (chat-store).");
        }
        catch (Exception ex)
        {
            ApiKeyStatusText.Text = SZ("Echec cle API : ", "API key failed: ", "Error de clave API: ", "Falha da chave API: ", "API-Schluessel fehlgeschlagen: ", "Errore chiave API: ") + ex.Message;
        }
    }
}
