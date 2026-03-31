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
        ReadyStatusText.Text = "Testing…";
        ReadyRawBox.Visibility = Visibility.Collapsed;
        ReadyRawBox.Text = "";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var resp = await http.GetAsync(_backendUrl + "/ready");
            var raw = await resp.Content.ReadAsStringAsync();

            var ok = resp.IsSuccessStatusCode;

            // try to parse { ok: true/false }
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("ok", out var p) && p.ValueKind == JsonValueKind.True)
                    ok = true;
            }
            catch { }

            ReadyStatusText.Text = ok ? "✅ /ready OK" : $"❌ /ready failed (HTTP {(int)resp.StatusCode})";

            ReadyRawBox.Text = raw;
            ReadyRawBox.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ReadyStatusText.Text = "❌ /ready error: " + ex.Message;
        }
    }

    private async void TestApiKey_Click(object sender, RoutedEventArgs e)
    {
        ApiKeyStatusText.Text = "Testing…";

        try
        {
            var apiKey = (ApiKeyBox.Password ?? "").Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                ApiKeyStatusText.Text = "❌ Missing API key.";
                return;
            }

            if (string.IsNullOrWhiteSpace(_userId))
            {
                ApiKeyStatusText.Text = "❌ Missing userId (client).";
                return;
            }

            var api = new ApiClient();
            api.Configure(_backendUrl, apiKey, _userId);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            // Create then delete a temp session
            var created = await api.CreateSessionAsync("setup-test", clientUser: Environment.UserName, cts.Token);
            await api.DeleteSessionAsync(created.SessionId, cts.Token);

            ApiKeyStatusText.Text = "✅ API key OK (chat-store).";
        }
        catch (Exception ex)
        {
            ApiKeyStatusText.Text = "❌ API key failed: " + ex.Message;
        }
    }
}
