namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow
{
    public MainWindow()
    {
        InitializeComponent();
        ApplyAppearanceTheme();

        // Option B provisioning: installer/IT can drop a provisioning.json.
        // Apply it before loading settings so the user has nothing to configure.
        if (Provisioning.TryApplyIfPresent(out var provMsg))
        {
            ClientLog.Info(provMsg);
        }

        EnsureStartupOverlay();

        Root.Loaded += async (_, __) =>
        {
            UpdateMessagesClip();
            ShowStartupOverlay(ClientUiText.Get("startup.subtitle", _appSettings.UiLanguage), ClientUiText.Get("startup.status.initializing", _appSettings.UiLanguage));
            await InitializeUserModeAsync();
        };

        Activated += MainWindow_Activated;

        TryResize(1400, 820);
        ApplyWindowChrome();

        MessagesList.ItemsSource = _messages;
        SessionsList.ItemsSource = _sessions;

        Closed += MainWindow_Closed;

        LoadSettings();
        LoadLocalLlmUiFromSettings();
        UpdateUiState(isGenerating: false);
        Status(ClientUiText.Get("status.ready", _appSettings.UiLanguage));
    }
}
