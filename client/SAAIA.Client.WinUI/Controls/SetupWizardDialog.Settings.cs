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

        // Persist the backend URL the user actually typed in the wizard. Previously
        // forgotten — the URL field was visually editable but its value was never saved.
        var backendUrl = NormalizeBackendUrl((BackendUrlBox.Text ?? "").Trim().TrimEnd('/'));
        if (!string.IsNullOrWhiteSpace(backendUrl))
            s.BackendUrl = backendUrl;

        // Optional fallback URLs (one per line). Empty string is fine — clears any previous list.
        // Each line goes through NormalizeBackendUrl so .ts.net (Tailscale serve) URLs are
        // forced to https:// — http:// would hit a closed port 80.
        var alts = (BackendUrlAlternatesBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(alts))
        {
            s.BackendUrlAlternates = "";
        }
        else
        {
            var lines = alts.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < lines.Length; i++)
                lines[i] = NormalizeBackendUrl(lines[i].Trim().TrimEnd('/'));
            s.BackendUrlAlternates = string.Join("\n", lines);
        }

        s.UseLocalLlm = UseLocalLlmCheck.IsChecked ?? false;
        s.AutoStartOnConnect = AutoStartCheck.IsChecked ?? false;

        s.LlamaExePath = (LlamaExeBox.Text ?? "").Trim();
        s.ModelPath = (ModelPathBox.Text ?? "").Trim();

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

    // Force HTTPS for Tailscale MagicDNS hostnames (*.ts.net): Tailscale serve terminates
    // TLS on :443, port 80 is closed. Many users paste an http:// URL here by reflex and
    // then wonder why nothing connects (silent timeout). For other hostnames we leave the
    // user's choice alone.
    private static string NormalizeBackendUrl(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var url = raw.Trim();
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            var rest = url.Substring("http://".Length);
            // Detect Tailscale Magic DNS host (anything ending in .ts.net, with no explicit port).
            var hostAndRest = rest;
            var pathStart = hostAndRest.IndexOf('/');
            var host = pathStart >= 0 ? hostAndRest.Substring(0, pathStart) : hostAndRest;
            if (host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase) && !host.Contains(':'))
                url = "https://" + rest;
        }

        return url;
    }
}
