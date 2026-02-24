using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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

        // Seed UI from settings
        var s = settingsInitial ?? AppSettings.Load();

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

        ReadyStatusText.Text = "";
        ApiKeyStatusText.Text = "";
        LlmStatusText.Text = "";
    }

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

    private async void TestModels_Click(object sender, RoutedEventArgs e)
    {
        LlmStatusText.Text = "Testing…";

        try
        {
            var s = ReadSettingsFromUi();

            if (!s.UseLocalLlm)
            {
                LlmStatusText.Text = "ℹ️ Local LLM is disabled.";
                return;
            }

            var baseUrl = s.LlmBaseUrl;
            var modelId = s.ModelId;

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                LlmStatusText.Text = "❌ Missing LLM base url.";
                return;
            }

            var llm = new OpenAiLlmClient();
            llm.Configure(baseUrl, modelId);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var models = await llm.ListModelsAsync(cts.Token);

            if (models.Count == 0)
            {
                LlmStatusText.Text = "❌ No models returned.";
                return;
            }

            var ok = !string.IsNullOrWhiteSpace(modelId) && models.Any(m => string.Equals(m, modelId, StringComparison.OrdinalIgnoreCase));

            LlmStatusText.Text = ok
                ? $"✅ /v1/models OK (model found: {modelId})"
                : $"⚠️ /v1/models OK but modelId not found. First={models[0]}";
        }
        catch (Exception ex)
        {
            LlmStatusText.Text = "❌ /v1/models failed: " + ex.Message;
        }
    }

    private async void StartLocalLlm_Click(object sender, RoutedEventArgs e)
    {
        LlmStatusText.Text = "Starting…";

        try
        {
            var s = ReadSettingsFromUi();
            s.UseLocalLlm = true;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, s.StartupTimeoutSeconds)));
            var (ok, msg) = await _llmProc.StartAsync(s, cts.Token);

            LlmStatusText.Text = ok ? "✅ " + msg : "❌ " + msg;
        }
        catch (Exception ex)
        {
            LlmStatusText.Text = "❌ Start failed: " + ex.Message;
        }
    }

    private void StopLocalLlm_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _llmProc.Stop();
            LlmStatusText.Text = "Stopped.";
        }
        catch (Exception ex)
        {
            LlmStatusText.Text = "❌ Stop failed: " + ex.Message;
        }
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
