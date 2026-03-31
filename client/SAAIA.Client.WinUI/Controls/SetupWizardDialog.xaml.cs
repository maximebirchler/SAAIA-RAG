using System;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SAAIA.Client.WinUI.Services;

namespace SAAIA.Client.WinUI.Controls;

public sealed partial class SetupWizardDialog : ContentDialog
{
    private readonly string _backendUrl;
    private readonly string _userId;
    private readonly LlamaCppProcessManager _llmProc;

    public bool Applied { get; private set; }

    // NOTE: ctor is internal because it takes internal service types (AppSettings, LlamaCppProcessManager).
    // This dialog is only instantiated from within the WinUI client assembly.
    internal SetupWizardDialog(
        string backendUrl,
        string userId,
        string apiKeyInitial,
        AppSettings settingsInitial,
        LlamaCppProcessManager llmProc)
    {
        InitializeComponent();

        _backendUrl = (backendUrl ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(_backendUrl)) _backendUrl = "http://localhost:5122";

        _userId = (userId ?? "").Trim();
        _llmProc = llmProc;

        BackendUrlBox.Text = _backendUrl;
        ApiKeyBox.Password = apiKeyInitial ?? "";

        SeedUiFromSettings(settingsInitial ?? AppSettings.Load());
        ResetStatusTexts();
    }

    private void SeedUiFromSettings(AppSettings settings)
    {
        var s = settings ?? AppSettings.Load();

        // End-user mode: hide advanced LLM section (paths/process management).
        // Integrator mode: show it.
        LlmSection.Visibility = s.ShowAdvancedUi ? Visibility.Visible : Visibility.Collapsed;

        UseLocalLlmCheck.IsChecked = s.UseLocalLlm;
        AutoStartCheck.IsChecked = s.AutoStartOnConnect;
        LlamaExeBox.Text = s.LlamaExePath ?? "";
        ModelPathBox.Text = s.ModelPath ?? "";
        HostBox.Text = string.IsNullOrWhiteSpace(s.Host) ? "127.0.0.1" : s.Host;
        PortBox.Text = s.Port <= 0 ? "1234" : s.Port.ToString();
        ModelIdBox.Text = s.ModelId ?? "";
        ExtraArgsBox.Text = s.ExtraArgs ?? "";
    }

    private void ResetStatusTexts()
    {
        ReadyStatusText.Text = "";
        ApiKeyStatusText.Text = "";
        LlmStatusText.Text = "";
    }
}
