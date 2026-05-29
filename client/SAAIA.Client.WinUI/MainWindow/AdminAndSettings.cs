namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow
{
    private async void ChatsSecretHotzone_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (_secretAdminFirstClickUtc == DateTimeOffset.MinValue || (now - _secretAdminFirstClickUtc) > SecretAdminClickWindow)
            {
                _secretAdminFirstClickUtc = now;
                _secretAdminClickCount = 1;
            }
            else
            {
                _secretAdminClickCount++;
            }

            if (_secretAdminClickCount < 5)
                return;

            _secretAdminClickCount = 0;
            _secretAdminFirstClickUtc = DateTimeOffset.MinValue;
            await ShowAdminSessionDialogAsync();
        }
        catch (Exception ex)
        {
            ClientLog.Exception("AdminSession.Open", ex);
            Status(LocalRuntimeText("Impossible d'ouvrir la fenetre admin. Le detail technique est dans les logs.", "Could not open the admin window. Technical detail is in the logs.", "No se pudo abrir la ventana admin. El detalle tecnico esta en los logs.", "Nao foi possivel abrir a janela admin. O detalhe tecnico esta nos logs.", "Admin-Fenster konnte nicht geoeffnet werden. Details stehen in den Logs.", "Impossibile aprire la finestra admin. I dettagli tecnici sono nei log.", UiLang));
        }
    }

    private async Task ShowAdminSessionDialogAsync()
    {
        var lang = _appSettings.UiLanguage;
        var passwordBox = new PasswordBox
        {
            PlaceholderText = ClientUiText.Get(_api.HasAdminKey ? "admin.session.placeholder.update" : "admin.session.placeholder.enter", lang),
            MinWidth = 340
        };

        var infoBanner = BuildDialogInfoBanner(
            ClientUiText.Get(_api.HasAdminKey ? "admin.session.active" : "admin.session.inactive", lang),
            _api.HasAdminKey);
        FontIcon? infoIcon = null;
        TextBlock? infoText = null;
        void SetInfo(string text, bool positive = false, bool isError = false)
        {
            var light = UseLightPalette();
            var foreground = light ? UiBrush(0x19, 0x24, 0x33) : UiBrush(0xF2, 0xF5, 0xFA);
            var iconForeground = positive
                ? (light ? UiBrush(0x2E, 0x7D, 0x5A) : UiBrush(0x66, 0xD1, 0x9E))
                : isError
                    ? (light ? UiBrush(0xB4, 0x23, 0x18) : UiBrush(0xFF, 0x8A, 0x80))
                    : (light ? UiBrush(0x5E, 0x7A, 0x97) : UiBrush(0x78, 0xB4, 0xF0));

            infoBanner.Background = positive
                ? (light ? UiBrush(0xEC, 0xF7, 0xF2) : UiBrush(0x12, 0x24, 0x1E))
                : isError
                    ? (light ? UiBrush(0xFD, 0xEF, 0xEE) : UiBrush(0x2A, 0x14, 0x16))
                    : (light ? UiBrush(0xF4, 0xF7, 0xFB) : UiBrush(0x14, 0x1B, 0x24));
            infoBanner.BorderBrush = positive
                ? (light ? UiBrush(0xC6, 0xE5, 0xD7) : UiBrush(0x2A, 0x54, 0x43))
                : isError
                    ? (light ? UiBrush(0xF1, 0xC7, 0xC3) : UiBrush(0x6A, 0x2C, 0x31))
                    : (light ? UiBrush(0xC9, 0xD4, 0xE1) : UiBrush(0x2B, 0x35, 0x41));

            if (infoIcon is not null)
            {
                infoIcon.Glyph = positive ? "" : isError ? "" : "";
                infoIcon.Foreground = iconForeground;
                infoIcon.Glyph = positive ? "\uE73E" : isError ? "\uEA39" : "\uE946";
            }

            if (infoText is not null)
            {
                infoText.Text = text;
                infoText.Foreground = foreground;
            }

            infoBanner.Child = BuildDialogInfoBannerContent(
                text,
                positive ? "\uE73E" : isError ? "\uEA39" : "\uE946",
                iconForeground,
                foreground);
        }

        var connectButton = BuildDialogFooterButton(ClientUiText.Get(_api.HasAdminKey ? "admin.session.update" : "admin.session.connect", lang), primary: true);
        var disconnectButton = BuildDialogFooterButton(ClientUiText.Get("admin.session.disconnect", lang));
        var closeButton = BuildDialogFooterButton(ClientUiText.Get("dialog.close", lang));
        var isBusy = false;

        void RefreshButtons()
        {
            var active = _api.HasAdminKey;
            passwordBox.PlaceholderText = ClientUiText.Get(active ? "admin.session.placeholder.update" : "admin.session.placeholder.enter", lang);
            connectButton.Content = ClientUiText.Get(active ? "admin.session.update" : "admin.session.connect", lang);
            connectButton.IsEnabled = !isBusy;
            disconnectButton.IsEnabled = active && !isBusy;
        }

        var footer = BuildDialogFooter(connectButton, disconnectButton, closeButton);

        OverlayDialogSession? overlay = null;

        connectButton.Click += async (_, __) =>
        {
            if (isBusy)
                return;

            var key = (passwordBox.Password ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                var message = ClientUiText.Get("admin.session.empty", lang);
                SetInfo(message, isError: true);
                Status(message);
                return;
            }

            isBusy = true;
            RefreshButtons();

            try
            {
                var validating = ClientUiText.Get("admin.session.validating", lang);
                SetInfo(validating);
                Status(validating);

                var (isValid, status) = await _api.TryActivateAdminSessionKeyAsync(key, CancellationToken.None);
                if (isValid)
                {
                    ClientLog.Info("Admin session key validated and activated for current app session.");
                    var message = ClientUiText.Get("admin.session.active", lang);
                    SetInfo(message, positive: true);
                    Status(message);
                    passwordBox.Password = string.Empty;
                    RefreshAdminJobsUiVisibility();
                }
                else if (string.Equals(status, "invalid", StringComparison.OrdinalIgnoreCase))
                {
                    ClientLog.Warn("Admin session key rejected by backend validation.");
                    var message = ClientUiText.Get("admin.session.invalid", lang);
                    SetInfo(message, isError: true);
                    Status(message);
                }
                else
                {
                    ClientLog.Warn("Admin session key could not be validated against the backend.");
                    var message = ClientUiText.Get("admin.session.validation_unavailable", lang);
                    SetInfo(message, isError: true);
                    Status(message);
                }
            }
            finally
            {
                isBusy = false;
                RefreshButtons();
            }
        };

        disconnectButton.Click += (_, __) =>
        {
            if (isBusy)
                return;

            _api.ClearAdminSessionKey();
            ClientLog.Info("Admin session key cleared.");
            var message = ClientUiText.Get("admin.session.inactive", lang);
            SetInfo(message);
            Status(message);
            RefreshButtons();
            RefreshAdminJobsUiVisibility();
        };

        closeButton.Click += (_, __) => overlay?.Close();

        RefreshButtons();

        overlay = ShowOverlayDialog(BuildCompactDialogShell(
            ClientUiText.Get("help.section.admin", lang),
            ClientUiText.Get("admin.session.title", lang),
            ClientUiText.Get("admin.session.subtitle", lang),
            new UIElement[]
            {
                infoBanner,
                BuildDialogPasswordBoxCard(passwordBox)
            },
            footer));

        await overlay.Completion;
    }

    private static T? FindDialogChild<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root is T typed)
            return typed;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var found = FindDialogChild<T>(child);
            if (found is not null)
                return found;
        }

        return null;
    }

    private void ApplyUserModeVisibility()
    {
        _appSettings = AppSettings.Load();

        var showAdv = _appSettings.ShowAdvancedUi;
        SetupButton.Visibility = (showAdv || NeedsSetupWizard()) ? Visibility.Visible : Visibility.Collapsed;
        HeaderHelpButton.Visibility = Visibility.Visible;
        //SetupButton.Visibility = Visibility.Visible;
        LlmSettingsButton.Visibility = showAdv ? Visibility.Visible : Visibility.Collapsed;
        ConnectButton.Visibility = showAdv ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RepairAssistantAsync()
    {
        await EnsureAssistantReadyIfNeededAsync(force: true);
    }

    private async void UserSettings_Click(object sender, RoutedEventArgs e)
{
    try
    {
        // Reload settings (they might have been provisioned or edited externally)
        _appSettings = AppSettings.Load();

        var dlg = new UserSettingsDialog(_appSettings, RepairAssistantAsync);
        var settingsDialogSize = GetDialogMaxSize(760, 760, horizontalMargin: 96, verticalMargin: 120);
        dlg.ApplyResponsiveLayout(settingsDialogSize.Width, settingsDialogSize.Height);

        OverlayDialogSession? overlay = null;
        var overlayContent = dlg.DetachContentForOverlay(() => overlay?.Close());
        overlay = ShowOverlayDialog(
            overlayContent,
            resizeHandler: _ =>
            {
                var size = GetDialogMaxSize(760, 760, horizontalMargin: 96, verticalMargin: 120);
                dlg.ApplyResponsiveLayout(size.Width, size.Height);
            });
        await overlay.Completion;

        if (dlg.WasApplied)
        {
            _appSettings = dlg.UpdatedSettings;
            _appSettings.Save();

            // Apply live to the running agent
            _agent?.ApplySettings(_appSettings);

            await Task.Yield();
            ApplyAppearanceTheme();
            ApplyUiLanguage();
            RefreshThemeSensitiveUi();
            Status(ClientUiText.Get("status.settings_applied", _appSettings.UiLanguage));
        }
    }
    catch (Exception ex)
    {
        ClientLog.Exception("Settings.Open", ex);
        Status(ClientUiText.Get("status.settings_failed", _appSettings.UiLanguage) + FormatLocalLlmUserActionError(ex, _appSettings.UiLanguage));
    }
}


}
