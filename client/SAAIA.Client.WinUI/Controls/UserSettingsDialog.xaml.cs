using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SAAIA.Client.WinUI.Services;

namespace SAAIA.Client.WinUI.Controls;

internal sealed partial class UserSettingsDialog : ContentDialog
{
    private readonly AppSettings _settings;
    private readonly Func<Task>? _repairAssistantAsync;

    public AppSettings UpdatedSettings { get; private set; }

    public UserSettingsDialog(AppSettings settings, Func<Task>? repairAssistantAsync = null)
    {
        InitializeComponent();

        _settings = settings;
        _repairAssistantAsync = repairAssistantAsync;
        UpdatedSettings = settings;

        LoadFromSettings();

        PrimaryButtonClick += OnPrimaryButtonClick;
    }

    private async void RepairAssistant_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_repairAssistantAsync is null)
            {
                RepairStatusText.Text = "Non disponible.";
                return;
            }

            RepairAssistantButton.IsEnabled = false;
            RepairStatusText.Text = "Installation / réparation en cours…";

            await _repairAssistantAsync().ConfigureAwait(true);

            RepairStatusText.Text = "Terminé.";
        }
        catch (Exception ex)
        {
            RepairStatusText.Text = "Échec : " + ex.Message;
        }
        finally
        {
            RepairAssistantButton.IsEnabled = true;
        }
    }

    private void LoadFromSettings()
    {
        AssistantEnabledToggle.IsOn = _settings.UseLocalLlm;
        StrictModeToggle.IsOn = _settings.StrictMode;

        // Rag preset
        var preset = (_settings.RagQualityPreset ?? "balanced").Trim().ToLowerInvariant();
        RagQualityCombo.SelectedIndex = preset switch
        {
            "quick" => 0,
            "deep" => 2,
            _ => 1
        };

        // Style (map temperature -> 3 user-friendly buckets)
        var t = _settings.LlmTemperature;
        if (double.IsNaN(t) || double.IsInfinity(t)) t = 0.2;
        if (t <= 0.15) StyleCombo.SelectedIndex = 0;         // factuel
        else if (t <= 0.45) StyleCombo.SelectedIndex = 1;    // équilibré
        else StyleCombo.SelectedIndex = 2;                   // créatif

        // Answer length (map tokens -> buckets)
        var mt = _settings.LlmMaxOutputTokens;
        if (mt <= 650) AnswerLengthCombo.SelectedIndex = 0;
        else if (mt <= 1150) AnswerLengthCombo.SelectedIndex = 1;
        else AnswerLengthCombo.SelectedIndex = 2;
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Apply safe settings back to AppSettings.
        _settings.UseLocalLlm = AssistantEnabledToggle.IsOn;
        _settings.StrictMode = StrictModeToggle.IsOn;

        _settings.RagQualityPreset = RagQualityCombo.SelectedIndex switch
        {
            0 => "quick",
            2 => "deep",
            _ => "balanced"
        };

        _settings.LlmTemperature = StyleCombo.SelectedIndex switch
        {
            0 => 0.10, // factuel
            2 => 0.70, // créatif
            _ => 0.25  // équilibré
        };

        _settings.LlmMaxOutputTokens = AnswerLengthCombo.SelectedIndex switch
        {
            0 => 450,
            2 => 1400,
            _ => 900
        };

        UpdatedSettings = _settings;
    }

    private async void ExportSupport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ExportSupportButton.IsEnabled = false;
            ExportStatusText.Text = "Création du support-bundle…";

            var zipPath = await SupportBundleBuilder.BuildAsync(_settings).ConfigureAwait(true);

            ExportStatusText.Text = $"OK : {zipPath}";
        }
        catch (Exception ex)
        {
            ExportStatusText.Text = "Échec : " + ex.Message;
        }
        finally
        {
            ExportSupportButton.IsEnabled = true;
        }
    }

    private void OpenSupportFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = SupportBundleBuilder.SupportDir;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $""{dir}"",
                UseShellExecute = true
            });
        }
        catch
        {
            // ignore
        }
    }
}
