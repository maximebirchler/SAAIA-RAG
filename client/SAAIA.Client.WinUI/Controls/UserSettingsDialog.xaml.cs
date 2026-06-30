using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SAAIA.Client.WinUI.Services;

namespace SAAIA.Client.WinUI.Controls;

// Legacy reference-only XAML implementation. The compiled dialog is the code-built
// partial in UserSettingsDialog.cs; keep this file localized so it remains safe if
// it is ever inspected or re-enabled.
internal sealed partial class UserSettingsDialog : ContentDialog
{
    private readonly AppSettings _settings;
    private readonly Func<Task>? _repairAssistantAsync;
    private readonly string _uiLanguage;

    public AppSettings UpdatedSettings { get; private set; }

    public UserSettingsDialog(AppSettings settings, Func<Task>? repairAssistantAsync = null)
    {
        InitializeComponent();

        _settings = settings;
        _repairAssistantAsync = repairAssistantAsync;
        _uiLanguage = ClientUiText.NormalizeLanguage(settings.UiLanguage);
        UpdatedSettings = settings;

        ApplyUiText();
        LoadFromSettings();

        PrimaryButtonClick += OnPrimaryButtonClick;
    }

    public UserSettingsDialog(AppSettings settings, XamlRoot? xamlRoot, Func<Task>? repairAssistantAsync = null)
        : this(settings, repairAssistantAsync)
    {
        if (xamlRoot != null)
            XamlRoot = xamlRoot;
    }

    private string T(string key) => ClientUiText.Get(key, _uiLanguage);

    private void ApplyUiText()
    {
        Title = T("settings.title");
        PrimaryButtonText = T("settings.apply");
        CloseButtonText = ClientUiText.Get("dialog.close", _uiLanguage);

        SafeSettingsNoteText.Text = T("settings.safe_note");
        AssistantEnabledToggle.Header = T("settings.toggle.assistant");
        AssistantEnabledToggle.OnContent = T("settings.toggle.assistant.on");
        AssistantEnabledToggle.OffContent = T("settings.toggle.assistant.off");
        StrictModeToggle.Header = T("settings.toggle.strict");
        StrictModeToggle.OnContent = T("settings.toggle.strict.on");
        StrictModeToggle.OffContent = T("settings.toggle.strict.off");

        RagQualityLabelText.Text = T("settings.rag_quality");
        ResetComboItems(RagQualityCombo, T("settings.choice.quick"), T("settings.choice.balanced"), T("settings.choice.deep"));

        StyleLabelText.Text = T("settings.style");
        ResetComboItems(StyleCombo, T("settings.choice.precise"), T("settings.choice.balanced"), T("settings.choice.creative"));

        AnswerLengthLabelText.Text = T("settings.length");
        ResetComboItems(AnswerLengthCombo, T("settings.choice.short"), T("settings.choice.standard"), T("settings.choice.long"));

        AssistantSectionText.Text = T("settings.section.assistant");
        AssistantRepairNoteText.Text = T("settings.repair.note");
        RepairAssistantButton.Content = T("settings.repair.button");

        SupportSectionText.Text = T("settings.section.support");
        SupportNoteText.Text = T("settings.support.note");
        ExportSupportButton.Content = T("settings.support.button");
        OpenSupportFolderButton.Content = T("settings.support.open_folder");
    }

    private static void ResetComboItems(ComboBox combo, params string[] items)
    {
        var selectedIndex = combo.SelectedIndex;
        combo.Items.Clear();
        foreach (var item in items)
            combo.Items.Add(new ComboBoxItem { Content = item });

        if (selectedIndex >= 0 && selectedIndex < items.Length)
            combo.SelectedIndex = selectedIndex;
    }

    private async void RepairAssistant_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_repairAssistantAsync is null)
            {
                RepairStatusText.Text = T("settings.status.repair.unavailable");
                return;
            }

            RepairAssistantButton.IsEnabled = false;
            RepairStatusText.Text = T("settings.status.repair.running");

            await _repairAssistantAsync().ConfigureAwait(true);

            RepairStatusText.Text = T("settings.status.repair.done");
        }
        catch (Exception ex)
        {
            RepairStatusText.Text = T("settings.status.failed_prefix") + ex.Message;
        }
        finally
        {
            RepairAssistantButton.IsEnabled = true;
        }
    }

    private void LoadFromSettings()
    {
        AssistantEnabledToggle.IsOn = _settings.UseLocalLlm;
        StrictModeToggle.IsOn = string.Equals(_settings.ActiveMode, "strict", StringComparison.OrdinalIgnoreCase);

        var preset = (_settings.RagQualityPreset ?? "balanced").Trim().ToLowerInvariant();
        RagQualityCombo.SelectedIndex = preset switch
        {
            "quick" => 0,
            "deep" => 2,
            _ => 1
        };

        var t = _settings.LlmTemperature;
        if (double.IsNaN(t) || double.IsInfinity(t))
            t = 0.2;

        if (t <= 0.15)
            StyleCombo.SelectedIndex = 0;
        else if (t <= 0.45)
            StyleCombo.SelectedIndex = 1;
        else
            StyleCombo.SelectedIndex = 2;

        var mt = _settings.LlmMaxOutputTokens;
        if (mt <= 650)
            AnswerLengthCombo.SelectedIndex = 0;
        else if (mt <= 1600)
            AnswerLengthCombo.SelectedIndex = 1;
        else
            AnswerLengthCombo.SelectedIndex = 2;
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        _settings.UseLocalLlm = AssistantEnabledToggle.IsOn;
        _settings.ActiveMode = StrictModeToggle.IsOn ? "strict" : "auto";

        _settings.RagQualityPreset = RagQualityCombo.SelectedIndex switch
        {
            0 => "quick",
            2 => "deep",
            _ => "balanced"
        };

        _settings.LlmTemperature = StyleCombo.SelectedIndex switch
        {
            0 => 0.10,
            2 => 0.70,
            _ => 0.25
        };

        _settings.LlmMaxOutputTokens = AnswerLengthCombo.SelectedIndex switch
        {
            0 => 350,
            2 => 2200,
            _ => 1600
        };

        UpdatedSettings = _settings;
    }

    private async void ExportSupport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ExportSupportButton.IsEnabled = false;
            ExportStatusText.Text = T("settings.status.exporting");

            var zipPath = await SupportBundleBuilder.BuildAsync(_settings).ConfigureAwait(true);

            ExportStatusText.Text = T("settings.status.exported") + Environment.NewLine + zipPath;
        }
        catch (Exception ex)
        {
            ExportStatusText.Text = T("settings.status.export_failed") + Environment.NewLine + ex.Message;
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
                Arguments = $"\"{dir}\"",
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }
}
