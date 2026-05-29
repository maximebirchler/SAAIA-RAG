namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow
{
    private async Task InitializeUserModeAsync()
    {
        try
        {
            ApplyUserModeVisibility();
            ShowStartupOverlay(ClientUiText.Get("startup.subtitle", _appSettings.UiLanguage), ClientUiText.Get("startup.status.checking_setup", _appSettings.UiLanguage));
            await ShowSetupWizardIfNeededAsync();

            if (_appSettings.AutoConnect && _agent is null && !NeedsSetupWizard())
            {
                ShowStartupOverlay(ClientUiText.Get("startup.subtitle", _appSettings.UiLanguage), ClientUiText.Get("startup.status.connecting", _appSettings.UiLanguage));
                await ConnectAsync();
            }

            // If auto-connect was supposed to run but the agent is still null, ConnectAsync
            // either threw and was swallowed, or the backend didn't answer. Keep the overlay
            // visible in error mode so the user has an obvious "not connected" indicator
            // instead of an empty chat with a vague status line.
            if (_agent is null && !NeedsSetupWizard() && _appSettings.AutoConnect)
            {
                ShowStartupOverlayError(
                    ClientUiText.Get("startup.error.not_connected", _appSettings.UiLanguage),
                    GetLastConnectErrorMessage() ?? ClientUiText.Get("startup.subtitle", _appSettings.UiLanguage));
                return;
            }

            HideStartupOverlay();
        }
        catch (Exception ex)
        {
            Status(ClientUiText.Get("status.init_failed", _appSettings.UiLanguage) + ex.Message);
            ShowStartupOverlayError(
                ClientUiText.Get("startup.error.not_connected", _appSettings.UiLanguage),
                ex.Message);
        }
    }

    // ConnectAsync stuffs failures into Status(...) text. We capture the most recent
    // failure here so the startup overlay can show something better than "Initialisation…".
    private string? _lastConnectErrorMessage;
    private string? GetLastConnectErrorMessage() => _lastConnectErrorMessage;


    private void Status(string s)
    {
        try
        {
            if (StatusText is not null)
                StatusText.Text = s;

            _statusHideTimer?.Stop();
            if (_isGenerating)
                return;

            _statusHideTimer = DispatcherQueue.CreateTimer();
            _statusHideTimer.Interval = TimeSpan.FromSeconds(1.8);
            _statusHideTimer.Tick += (_, __) =>
            {
                _statusHideTimer?.Stop();
                if (!_isGenerating && StatusText is not null)
                    StatusText.Text = string.Empty;
            };
            _statusHideTimer.Start();
        }
        catch
        {
        }
    }

    private void StageOutboundMessage(string wireText, string? displayText = null)
    {
        _pendingOutboundWireText = string.IsNullOrWhiteSpace(wireText) ? null : wireText.Trim();
        _pendingOutboundDisplayText = string.IsNullOrWhiteSpace(displayText) ? null : displayText.Trim();
        if (InputBox is not null)
            InputBox.Text = _pendingOutboundDisplayText ?? _pendingOutboundWireText ?? string.Empty;
    }

    private void ClearStagedOutboundMessage()
    {
        _pendingOutboundWireText = null;
        _pendingOutboundDisplayText = null;
    }


    private void ClearStatus()
    {
        try
        {
            _statusHideTimer?.Stop();
            _statusHideTimer = null;

            if (StatusText is not null)
                StatusText.Text = string.Empty;

            if (TypingText is not null)
            {
                TypingText.Text = string.Empty;
                TypingText.Visibility = Visibility.Collapsed;
            }
        }
        catch
        {
        }
    }

    private bool IsConnected => _agent is not null;

    private void UpdateUiState(bool isGenerating)
    {
        _isGenerating = isGenerating;
        if (!isGenerating)
            _isCancellingGeneration = false;

        InputBox.IsEnabled = IsConnected && !_isGenerating && !string.IsNullOrWhiteSpace(_sessionId);
        if (HeaderHelpButton is not null)
            HeaderHelpButton.IsEnabled = !_isGenerating;
        if (HeaderAdminConsoleButton is not null)
            HeaderAdminConsoleButton.IsEnabled = !_isGenerating && _api.HasAdminKey;
        RefreshAdminJobsUiVisibility();

        SessionsList.IsEnabled = IsConnected && !_isGenerating;
        NewChatButton.IsEnabled = IsConnected && !_isGenerating;
        ConnectButton.IsEnabled = !_isGenerating;

        UpdateSendCancelButtonVisualState();
    }

    private void LoadSettings()
    {
        _appSettings = AppSettings.Load();
        ServerUrlBox.Text = string.IsNullOrWhiteSpace(_appSettings.BackendUrl) ? ClientDefaults.BackendBaseUrl : _appSettings.BackendUrl;

        _userId = SecureLocalStore.GetOrCreateUserId();
        ApiKeyBox.Password = SecureLocalStore.GetServerApiKey() ?? "";

        // LLM endpoint is configured in settings (usually 127.0.0.1:1234/v1). Hidden in user mode.
        LlmUrlBox.Text = _appSettings.LlmBaseUrl;
        LlmModelBox.Text = string.IsNullOrWhiteSpace(_appSettings.ModelId) ? ClientDefaults.LlmModel : _appSettings.ModelId;

        _sessionId = string.IsNullOrWhiteSpace(_appSettings.LastSessionId) ? null : _appSettings.LastSessionId;
        ApplyAppearanceTheme();
        ApplyUiLanguage();
    }

    private void SaveSettings()
    {
        SecureLocalStore.SetServerApiKey(ApiKeyBox.Password.Trim());

        // Persist last session id in AppSettings (works both packaged and unpackaged)
        _appSettings ??= AppSettings.Load();
        _appSettings.LastSessionId = _sessionId;
        _appSettings.Save();
    }

    private bool NeedsSetupWizard()
    {
        var apiKey = (ApiKeyBox.Password ?? "").Trim();
        if (string.IsNullOrWhiteSpace(apiKey)) return true;
        return false;
    }


    private async Task<Microsoft.UI.Xaml.XamlRoot?> GetDialogXamlRootAsync()
    {
        // WinUI 3: ContentDialog requires a non-null XamlRoot.
        // In early startup it can briefly be null.
        Microsoft.UI.Xaml.XamlRoot? xamlRoot = null;
        xamlRoot = TrySoftUi("GetDialogXamlRootAsync.InitialProbe", () => Root?.XamlRoot ?? Content?.XamlRoot, fallback: xamlRoot);
        for (var i = 0; xamlRoot is null && i < 40; i++)
        {
            await Task.Delay(50);
            xamlRoot = TrySoftUi("GetDialogXamlRootAsync.RetryProbe", () => Root?.XamlRoot ?? Content?.XamlRoot, fallback: xamlRoot);
        }
        return xamlRoot;
    }


}
