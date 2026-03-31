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
}
