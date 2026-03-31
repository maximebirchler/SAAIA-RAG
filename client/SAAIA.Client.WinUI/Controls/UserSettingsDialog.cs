using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SAAIA.Client.WinUI.Services;

namespace SAAIA.Client.WinUI.Controls;

/// <summary>
/// User-safe settings dialog (no dangerous configuration).
/// Built in C# to avoid WinUI XamlCompiler fragility.
/// </summary>
internal sealed partial class UserSettingsDialog : ContentDialog
{
    private readonly AppSettings _original;
    private readonly AppSettings _working;
    private readonly Func<Task>? _repairAssistantAsync;
    private readonly DownloadManager _downloads = new();

    private bool _busy;
    private bool _showAdvanced;

    private readonly Button _applyButton = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _closeButton = new() { HorizontalAlignment = HorizontalAlignment.Stretch };

    private sealed record UiLanguageChoice(string Code, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record UiThemeChoice(string Code, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly ComboBox _uiLanguage = new();
    private readonly ComboBox _appearance = new();
    private readonly ToggleSwitch _assistantEnabled = new();
    private readonly ToggleSwitch _strictMode = new();
    private readonly ComboBox _ragQuality = new();
    private readonly ComboBox _style = new();
    private readonly ComboBox _length = new();

    private readonly TextBlock _assistantSectionTitle = new();
    private readonly TextBlock _repairSectionTitle = new();
    private readonly TextBlock _supportSectionTitle = new();
    private readonly TextBlock _interfaceSectionTitle = new();
    private readonly TextBlock _behaviorSectionTitle = new();
    private readonly TextBlock _heroTitle = new();
    private readonly TextBlock _heroSubtitle = new() { Opacity = 0.84, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _advancedSubtitle = new() { Opacity = 0.82, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _interfaceNote = new() { Opacity = 0.82, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _supportNote = new() { Opacity = 0.82, TextWrapping = TextWrapping.Wrap };
    private readonly Button _generalTabButton = new();
    private readonly Button _advancedTabButton = new();
    private readonly StackPanel _generalContent = new() { Spacing = 14 };
    private readonly StackPanel _advancedContent = new() { Spacing = 14 };

    private readonly TextBlock _assistantStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _assistantProgress = new() { Minimum = 0, Maximum = 100, Height = 6, Visibility = Visibility.Collapsed };
    private readonly Button _assistantRepairBtn = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button _exportBtn = new() { HorizontalAlignment = HorizontalAlignment.Left };

    private readonly ScrollViewer _layoutScroller = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };

    private readonly Border _shellHost = new();
    private Action? _overlayCloseAction;

    internal AppSettings UpdatedSettings { get; private set; }
    internal bool WasApplied { get; private set; }

    public UserSettingsDialog(AppSettings settings, Func<Task>? repairAssistantAsync = null)
    {
        _original = settings ?? throw new ArgumentNullException(nameof(settings));
        _working = _original.Clone();
        UpdatedSettings = _original;
        _repairAssistantAsync = repairAssistantAsync;

        DefaultButton = ContentDialogButton.None;
        RequestedTheme = ToElementTheme(_working.UiTheme);
        PrimaryButtonText = string.Empty;
        SecondaryButtonText = string.Empty;
        CloseButtonText = string.Empty;

        Closing += OnClosing;

        _generalTabButton.Click += (_, _) => SetSettingsView(showAdvanced: false);
        _advancedTabButton.Click += (_, _) => SetSettingsView(showAdvanced: true);
        _assistantRepairBtn.Click += async (_, _) => await RunRepairAsync().ConfigureAwait(true);
        _exportBtn.Click += async (_, _) => await ExportSupportBundleAsync().ConfigureAwait(true);
        _applyButton.Click += (_, _) => ApplyAndClose();
        _closeButton.Click += (_, _) =>
        {
            if (_busy)
            {
                _assistantStatus.Text = T("settings.status.busy");
                return;
            }

            RequestClose();
        };

        _assistantEnabled.IsOn = _working.UseLocalLlm;
        _strictMode.IsOn = _working.StrictMode;

        foreach (var option in ClientUiText.GetLanguageOptions())
            _uiLanguage.Items.Add(new UiLanguageChoice(option.Code, option.Label));

        var selectedUiLanguage = ClientUiText.NormalizeLanguage(_working.UiLanguage);
        var selectedIndex = ClientUiText.GetLanguageOptions().ToList().FindIndex(x => string.Equals(x.Code, selectedUiLanguage, StringComparison.OrdinalIgnoreCase));
        _uiLanguage.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
        _uiLanguage.SelectionChanged += (_, _) => RefreshUiTexts();

        PopulateAppearanceOptions();

        _ragQuality.SelectedIndex = MapRagQualityToIndex(_working.RagQualityPreset);
        _style.SelectedIndex = MapStyleToIndex(_working.Temperature);
        _length.SelectedIndex = MapLenToIndex(_working.MaxOutputTokens);

        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Background = transparent;
        BorderBrush = transparent;
        BorderThickness = new Thickness(0);
        Resources["ContentDialogBackground"] = transparent;
        Resources["ContentDialogBorderBrush"] = transparent;
        Resources["ContentDialogBorderThickness"] = new Thickness(0);
        Resources["ContentDialogPadding"] = new Thickness(0);
        Resources["DefaultContentDialogPadding"] = new Thickness(0);
        Resources["DefaultContentDialogBackground"] = transparent;
        Resources["ContentDialogBackgroundThemeBrush"] = transparent;
        Resources["ContentDialogThemePadding"] = new Thickness(0);
        Resources["ContentDialogMinWidth"] = 0d;
        Resources["ContentDialogMaxWidth"] = 1400d;
        Resources["SystemControlPageBackgroundMediumAltMediumBrush"] = transparent;
        Resources["DialogBorderThemeThickness"] = new Thickness(0);
        Resources["ContentDialogSeparatorBorderBrush"] = transparent;
        Resources["ContentDialogSeparatorThickness"] = new Thickness(0);
        Resources["ContentDialogSmokeFill"] = Brush(0x00, 0x00, 0x00, UseLightPalette() ? (byte)0x08 : (byte)0x12);
        ApplyGlobalDialogThemeOverrides(transparent);
        Opened += OnDialogOpened;

        RefreshUiTexts();
        Content = BuildUi();
    }

    public UserSettingsDialog(AppSettings settings, XamlRoot? xamlRoot)
        : this(settings, xamlRoot, null)
    {
    }

    public UserSettingsDialog(AppSettings settings, XamlRoot? xamlRoot, Func<Task>? repairAssistantAsync = null)
        : this(settings, repairAssistantAsync)
    {
        if (xamlRoot is not null)
            XamlRoot = xamlRoot;
    }

    private string UiLang => ClientUiText.NormalizeLanguage(GetSelectedLanguageCode());
    private string T(string key) => ClientUiText.Get(key, UiLang);

    private string GetSelectedLanguageCode()
        => _uiLanguage.SelectedItem is UiLanguageChoice choice ? choice.Code : ClientUiText.NormalizeLanguage(_working.UiLanguage);

    private string GetSelectedThemeCode()
        => _appearance.SelectedItem is UiThemeChoice choice ? choice.Code : AppSettings.NormalizeUiTheme(_working.UiTheme);

    private static ElementTheme ToElementTheme(string? theme)
        => AppSettings.NormalizeUiTheme(theme) switch
        {
            "light" => ElementTheme.Light,
            "system" => ElementTheme.Default,
            _ => ElementTheme.Dark
        };

    private bool UseLightPalette()
    {
        if (RequestedTheme == ElementTheme.Light)
            return true;
        if (RequestedTheme == ElementTheme.Dark)
            return false;

        try
        {
            return Application.Current.RequestedTheme == ApplicationTheme.Light;
        }
        catch
        {
            return false;
        }
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b, byte a = 0xFF)
        => new(Microsoft.UI.ColorHelper.FromArgb(a, r, g, b));

    private static void ApplyGlobalDialogThemeOverrides(Brush transparent)
    {
        try
        {
            if (Application.Current?.Resources is not ResourceDictionary resources)
                return;

            resources["ContentDialogTopOverlay"] = transparent;
            resources["ContentDialogSeparatorBorderBrush"] = transparent;
            resources["ContentDialogSeparatorThickness"] = new Thickness(0);
        }
        catch
        {
        }
    }
}
