using Microsoft.UI.Xaml;
using SAAIA.Client.WinUI.Services;
using Microsoft.UI.Xaml.Controls;

namespace SAAIA.Client.WinUI.Controls;

internal sealed partial class UserSettingsDialog
{
    private void ApplyAndClose()
    {
        if (_busy)
        {
            _assistantStatus.Text = T("settings.status.busy");
            return;
        }

        _working.UseLocalLlm = _assistantEnabled.IsOn;
        _working.RagQualityPreset = MapIndexToRagQuality(_ragQuality.SelectedIndex);
        _working.Temperature = MapIndexToTemp(_style.SelectedIndex);
        _working.MaxOutputTokens = MapIndexToMaxTokens(_length.SelectedIndex);
        _working.UiLanguage = UiLang;
        _working.UiTheme = AppSettings.NormalizeUiTheme(GetSelectedThemeCode());

        _original.CopyFrom(_working);
        _original.Save();
        UpdatedSettings = _original;
        WasApplied = true;
        RequestClose();
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        IsPrimaryButtonEnabled = !busy;
        IsSecondaryButtonEnabled = !busy;
        _applyButton.IsEnabled = !busy;
        _closeButton.IsEnabled = !busy;

        _uiLanguage.IsEnabled = !busy;
        _appearance.IsEnabled = !busy;
        _assistantEnabled.IsEnabled = !busy;
        _ragQuality.IsEnabled = !busy;
        _style.IsEnabled = !busy;
        _length.IsEnabled = !busy;
        _assistantRepairBtn.IsEnabled = !busy;
        _exportBtn.IsEnabled = !busy;

        if (status is not null)
            _assistantStatus.Text = status;
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (_busy)
        {
            args.Cancel = true;
            _assistantStatus.Text = T("settings.status.busy");
        }
    }

    private static int MapRagQualityToIndex(string? preset)
        => (preset ?? "balanced").ToLowerInvariant() switch
        {
            "quick" => 0,
            "balanced" => 1,
            "deep" => 2,
            _ => 1
        };

    private static string MapIndexToRagQuality(int idx)
        => idx switch
        {
            0 => "quick",
            2 => "deep",
            _ => "balanced"
        };

    private static int MapStyleToIndex(double temp)
    {
        if (temp <= 0.15) return 0;
        if (temp <= 0.55) return 1;
        return 2;
    }

    private static double MapIndexToTemp(int idx)
        => idx switch
        {
            0 => 0.1,
            2 => 0.8,
            _ => 0.3
        };

    private static int MapLenToIndex(int maxTokens)
    {
        if (maxTokens <= 450) return 0;
        if (maxTokens <= 1100) return 1;
        return 2;
    }

    private static int MapIndexToMaxTokens(int idx)
        => idx switch
        {
            0 => 350,
            2 => 1600,
            _ => 900
        };
}
