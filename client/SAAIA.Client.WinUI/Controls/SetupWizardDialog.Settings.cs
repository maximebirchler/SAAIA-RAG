using System;
using System.IO;

using Microsoft.UI.Xaml.Controls;

using SAAIA.Client.WinUI.Services;

namespace SAAIA.Client.WinUI.Controls;

public sealed partial class SetupWizardDialog
{
    private AppSettings ReadSettingsFromUi()
    {
        var s = AppSettings.Load();

        s.UseLocalLlm = (UseLocalLlmCheck.IsChecked ?? false);
        s.AutoStartOnConnect = (AutoStartCheck.IsChecked ?? false);

        s.LlamaExePath = (LlamaExeBox.Text ?? "").Trim();
        s.ModelPath = (ModelPathBox.Text ?? "").Trim();

        // If integrator provided exe+model, enable process management.
        // Otherwise default stays "endpoint already running".
        if (!string.IsNullOrWhiteSpace(s.LlamaExePath) && !string.IsNullOrWhiteSpace(s.ModelPath))
            s.ManageLocalLlmProcess = true;

        s.Host = string.IsNullOrWhiteSpace(HostBox.Text) ? "127.0.0.1" : HostBox.Text.Trim();

        if (!int.TryParse((PortBox.Text ?? "").Trim(), out var port) || port <= 0)
            port = 1234;
        s.Port = port;

        s.ModelId = (ModelIdBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(s.ModelId) && !string.IsNullOrWhiteSpace(s.ModelPath))
            s.ModelId = Path.GetFileName(s.ModelPath);

        s.ExtraArgs = (ExtraArgsBox.Text ?? "").Trim();

        return s;
    }

    private void Dialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        try
        {
            var apiKey = (ApiKeyBox.Password ?? "").Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                args.Cancel = true;
                ApiKeyStatusText.Text = "❌ API key required.";
                return;
            }

            // Save API key to secure store (DPAPI)
            SecureLocalStore.SetServerApiKey(apiKey);

            // Save non-sensitive settings
            var s = ReadSettingsFromUi();
            s.Save();

            Applied = true;
        }
        catch (Exception ex)
        {
            args.Cancel = true;
            ApiKeyStatusText.Text = "❌ Apply failed: " + ex.Message;
        }
    }
}
