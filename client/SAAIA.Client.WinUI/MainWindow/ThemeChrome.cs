namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow
{
    private bool UseLightPalette()
    {
        var theme = AppSettings.NormalizeUiTheme(_appSettings.UiTheme);
        if (string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase))
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

    private ElementTheme GetElementTheme()
        => AppSettings.NormalizeUiTheme(_appSettings.UiTheme) switch
        {
            "light" => ElementTheme.Light,
            "system" => ElementTheme.Default,
            _ => ElementTheme.Dark
        };

    private void ApplyAppearanceTheme()
    {
        var theme = GetElementTheme();
        try { Root.RequestedTheme = theme; } catch { }
        try { AppTitleBar.RequestedTheme = theme; } catch { }
        try { ChatsPanel.RequestedTheme = theme; } catch { }
        try { MessagesPanelBorder.RequestedTheme = theme; } catch { }
        try { SessionsList.RequestedTheme = theme; } catch { }
        try { MessagesList.RequestedTheme = theme; } catch { }
        try { InputBox.RequestedTheme = theme; } catch { }
        try { SendCancelButton.RequestedTheme = theme; } catch { }
        ConfigureHeaderChrome();
        UpdateSendCancelButtonVisualState();
        RebuildStartupOverlayForTheme();
        RefreshDialogOverlayTheme();
        if (Root is not null)
        {
            TrySoftUi("ShowOverlayDialog.ResizeHandler.Initial", () => _dialogOverlayResizeHandler?.Invoke(new Size(Root.ActualWidth, Root.ActualHeight)));
        }
        ApplyWindowChrome();

        try
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try { UpdateSendCancelButtonVisualState(); } catch { }
                try { RefreshThemeSensitiveUi(); } catch { }
            });

            DispatcherQueue.TryEnqueue(() =>
            {
                try { UpdateSendCancelButtonVisualState(); } catch { }
                try { SendCancelButton?.UpdateLayout(); } catch { }
            });
        }
        catch { }
    }

    private static global::Windows.UI.Color WinColor(byte r, byte g, byte b, byte a = 0xFF)
        => global::Windows.UI.Color.FromArgb(a, r, g, b);

    private global::Windows.UI.Color TitleBarBackgroundColor => UseLightPalette() ? WinColor(241, 244, 248) : WinColor(15, 17, 20);
    private global::Windows.UI.Color TitleBarInactiveBackgroundColor => TitleBarBackgroundColor;
    private global::Windows.UI.Color TitleBarButtonHoverColor => UseLightPalette() ? WinColor(224, 232, 242) : WinColor(28, 31, 36);
    private global::Windows.UI.Color TitleBarButtonPressedColor => UseLightPalette() ? WinColor(205, 216, 232) : WinColor(43, 109, 182);
    private global::Windows.UI.Color TitleBarForegroundColor => UseLightPalette() ? WinColor(17, 24, 39) : WinColor(255, 255, 255);
    private static global::Windows.UI.Color TitleBarTransparentColor => global::Windows.UI.Color.FromArgb(0, 0, 0, 0);

    private void ConfigureHeaderChrome()
    {
        TrySoftUi("ConfigureHeaderChrome.ChatsToggleButton", () => ApplyHeaderButtonChrome(ChatsToggleButton));
        TrySoftUi("ConfigureHeaderChrome.SetupButton", () => ApplyHeaderButtonChrome(SetupButton));
        TrySoftUi("ConfigureHeaderChrome.HeaderHelpButton", () => ApplyHeaderButtonChrome(HeaderHelpButton));
        TrySoftUi("ConfigureHeaderChrome.HeaderSettingsButton", () => ApplyHeaderButtonChrome(HeaderSettingsButton));
    }

    private static SolidColorBrush UiBrush(byte r, byte g, byte b, byte a = 0xFF)
        => new(Microsoft.UI.ColorHelper.FromArgb(a, r, g, b));

    private void ApplyHeaderButtonChrome(Button button)
    {
        var light = UseLightPalette();

        if (double.IsNaN(button.Width) || button.Width < 40)
            button.Width = 40;
        if (double.IsNaN(button.Height) || button.Height < 40)
            button.Height = 40;

        button.Padding = new Thickness(0);
        button.CornerRadius = new CornerRadius(12);
        button.BorderThickness = new Thickness(1);
        button.Background = light ? UiBrush(0xF0, 0xF4, 0xFA) : UiBrush(0x15, 0x19, 0x23);
        button.BorderBrush = light ? UiBrush(0xCC, 0xD8, 0xE8) : UiBrush(0x2A, 0x31, 0x40);
        button.Foreground = light ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xFF, 0xFF, 0xFF);
        button.UseSystemFocusVisuals = false;

        button.Resources["ButtonBackgroundPointerOver"] = light ? UiBrush(0xE7, 0xEE, 0xF7) : UiBrush(0x1C, 0x22, 0x2B);
        button.Resources["ButtonBackgroundPressed"] = light ? UiBrush(0xDA, 0xE5, 0xF2) : UiBrush(0x2B, 0x6D, 0xB6);
        button.Resources["ButtonBackgroundDisabled"] = light ? UiBrush(0xEE, 0xF1, 0xF5) : UiBrush(0x10, 0x10, 0x12);
        button.Resources["ButtonBorderBrushPointerOver"] = light ? UiBrush(0xBE, 0xCD, 0xDF) : UiBrush(0x39, 0x45, 0x57);
        button.Resources["ButtonBorderBrushPressed"] = light ? UiBrush(0xA7, 0xC3, 0xE8) : UiBrush(0x56, 0xA7, 0xE7);
        button.Resources["ButtonBorderBrushDisabled"] = light ? UiBrush(0xD9, 0xDE, 0xE6) : UiBrush(0x1D, 0x1D, 0x20);
        button.Resources["ButtonForegroundPointerOver"] = button.Foreground;
        button.Resources["ButtonForegroundPressed"] = light ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xFF, 0xFF, 0xFF);
        button.Resources["ButtonForegroundDisabled"] = light ? UiBrush(0x8A, 0x96, 0xA6) : UiBrush(0x7A, 0x7A, 0x80);
    }

    private void UpdateSendCancelButtonVisualState()
    {
        if (SendCancelButton is null || SendCancelIcon is null)
            return;

        var light = UseLightPalette();
        var hasSession = !string.IsNullOrWhiteSpace(_sessionId);
        var hasText = !string.IsNullOrWhiteSpace(InputBox?.Text);

        var showCancelState = _isGenerating && !_isCancellingGeneration;
        var canInvoke = IsConnected && hasSession && (showCancelState || hasText);
        SendCancelButton.IsEnabled = true;
        SendCancelButton.IsHitTestVisible = canInvoke;
        SendCancelButton.Opacity = canInvoke ? 1d : 0.96d;
        SendCancelIcon.Glyph = showCancelState ? "" : "";
        ToolTipService.SetToolTip(SendCancelButton, showCancelState ? ClientUiText.Get("button.cancel", _appSettings.UiLanguage) : ClientUiText.Get("button.send", _appSettings.UiLanguage));

        SolidColorBrush background;
        SolidColorBrush border;
        SolidColorBrush foreground;
        SolidColorBrush hover;
        SolidColorBrush pressed;

        if (showCancelState)
        {
            background = light ? UiBrush(0xC0, 0x56, 0x5E) : UiBrush(0x91, 0x41, 0x48);
            border = light ? UiBrush(0xD3, 0x66, 0x6E) : UiBrush(0xA7, 0x51, 0x59);
            foreground = UiBrush(0xFF, 0xFF, 0xFF);
            hover = light ? UiBrush(0xD3, 0x66, 0x6E) : UiBrush(0xA7, 0x51, 0x59);
            pressed = light ? UiBrush(0xA5, 0x46, 0x4D) : UiBrush(0x77, 0x35, 0x3B);
        }
        else if (hasText)
        {
            background = light ? UiBrush(0x5E, 0x7A, 0x97) : UiBrush(0x3A, 0x84, 0xD8);
            border = light ? UiBrush(0x6B, 0x87, 0xA4) : UiBrush(0x56, 0xA7, 0xE7);
            foreground = UiBrush(0xFF, 0xFF, 0xFF);
            hover = light ? UiBrush(0x6B, 0x87, 0xA4) : UiBrush(0x4B, 0x95, 0xE6);
            pressed = light ? UiBrush(0x4F, 0x68, 0x82) : UiBrush(0x2B, 0x6D, 0xB6);
        }
        else
        {
            background = light ? UiBrush(0xF2, 0xF5, 0xF9) : UiBrush(0x15, 0x1B, 0x24);
            border = light ? UiBrush(0xC7, 0xD2, 0xDF) : UiBrush(0x2A, 0x32, 0x40);
            foreground = light ? UiBrush(0x7A, 0x89, 0x9D) : UiBrush(0x8E, 0x9B, 0xAF);
            hover = background;
            pressed = background;
        }

        SendCancelButton.Background = background;
        SendCancelButton.BorderBrush = border;
        SendCancelButton.Foreground = foreground;
        SendCancelButton.BorderThickness = new Thickness(1);
        SendCancelButton.Resources["ButtonBackgroundPointerOver"] = hover;
        SendCancelButton.Resources["ButtonBackgroundPressed"] = pressed;
        SendCancelButton.Resources["ButtonBorderBrushPointerOver"] = border;
        SendCancelButton.Resources["ButtonBorderBrushPressed"] = border;
        SendCancelButton.Resources["ButtonForegroundPointerOver"] = foreground;
        SendCancelButton.Resources["ButtonForegroundPressed"] = foreground;
        SendCancelButton.Resources["ButtonBackgroundDisabled"] = background;
        SendCancelButton.Resources["ButtonBorderBrushDisabled"] = border;
        SendCancelButton.Resources["ButtonForegroundDisabled"] = foreground;

        SendCancelIcon.Foreground = foreground;
        SendCancelIcon.Opacity = (_isGenerating || hasText) ? 1.0 : 0.88;
    }

    private void RefreshThemeSensitiveUi()
    {
        TrySoftUi("RefreshThemeSensitiveUi.ConfigureHeaderChrome", ConfigureHeaderChrome);
        TrySoftUi("RefreshThemeSensitiveUi.UpdateSendCancelButtonVisualState", UpdateSendCancelButtonVisualState);

        TrySoftUi("RefreshThemeSensitiveUi.RefreshSessions", () =>
        {
            var selectedSessionId = (SessionsList.SelectedItem as ChatSessionItem)?.SessionId;
            ApplySessionContainerSelectionState(selectedSessionId ?? _sessionId);
            SessionsList.ItemsSource = null;
            SessionsList.ItemsSource = _sessions;
            SyncSessionSelectionVisual(selectedSessionId ?? _sessionId);
        });

        TrySoftUi("RefreshThemeSensitiveUi.RefreshMessages", () =>
        {
            MessagesList.ItemsSource = null;
            MessagesList.ItemsSource = _messages;
        });

        TrySoftUi("RefreshThemeSensitiveUi.UpdateUiState", () => UpdateUiState(_isGenerating));
        TrySoftUi("RefreshThemeSensitiveUi.SessionsList.UpdateLayout", () => SessionsList.UpdateLayout());
        TrySoftUi("RefreshThemeSensitiveUi.MessagesList.UpdateLayout", () => MessagesList.UpdateLayout());
        TrySoftUi("RefreshThemeSensitiveUi.MessagesScroll.UpdateLayout", () => MessagesScroll.UpdateLayout());
        TrySoftUi("RefreshThemeSensitiveUi.InputBox.UpdateLayout", () => InputBox.UpdateLayout());
        TrySoftUi("RefreshThemeSensitiveUi.SendCancelButton.UpdateLayout", () => SendCancelButton.UpdateLayout());
        TrySoftUi("RefreshThemeSensitiveUi.Root.UpdateLayout", () => Root.UpdateLayout());
    }

    private void ConfigureDialogChrome(ContentDialog dialog)
    {
        dialog.RequestedTheme = GetElementTheme();
        dialog.Padding = new Thickness(0);
        dialog.MinWidth = 0;
        dialog.MaxWidth = 1400;
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        dialog.Background = transparent;
        dialog.BorderBrush = transparent;
        dialog.BorderThickness = new Thickness(0);
        dialog.Resources["ContentDialogBackground"] = transparent;
        dialog.Resources["ContentDialogBorderBrush"] = transparent;
        dialog.Resources["ContentDialogBorderThickness"] = new Thickness(0);
        dialog.Resources["ContentDialogMinWidth"] = 0d;
        dialog.Resources["ContentDialogMaxWidth"] = 1400d;
        dialog.Resources["ContentDialogPadding"] = new Thickness(0);
        dialog.Resources["DefaultContentDialogPadding"] = new Thickness(0);
        dialog.Resources["DefaultContentDialogBackground"] = transparent;
        dialog.Resources["ContentDialogBackgroundThemeBrush"] = transparent;
        dialog.Resources["SystemControlPageBackgroundMediumAltMediumBrush"] = transparent;
        dialog.Resources["DialogBorderThemeThickness"] = new Thickness(0);
        dialog.Resources["ContentDialogContentMinWidth"] = 0d;
        dialog.Resources["ContentDialogContentMaxWidth"] = 1400d;
        dialog.Resources["ContentDialogContentMinHeight"] = 0d;
        dialog.Resources["ContentDialogThemePadding"] = new Thickness(0);
        dialog.Resources["ContentDialogTopOverlay"] = transparent;
        dialog.Resources["ContentDialogSeparatorBorderBrush"] = transparent;
        dialog.Resources["ContentDialogSeparatorThickness"] = new Thickness(0);
        dialog.Resources["ContentDialogSmokeFill"] = UiBrush(0x00, 0x00, 0x00, UseLightPalette() ? (byte)0x08 : (byte)0x12);
        ApplyGlobalDialogThemeOverrides(transparent);
        dialog.Opened -= DialogChromeOpened;
        dialog.Opened += DialogChromeOpened;
    }

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

    private async void DialogChromeOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        sender.Opened -= DialogChromeOpened;

        FlattenContentDialogHost(sender);
        await Task.Yield();
        FlattenContentDialogHost(sender);
        await Task.Delay(1);
        FlattenContentDialogHost(sender);
        await Task.Delay(16);
        FlattenContentDialogHost(sender);
    }

    private void FlattenContentDialogHost(ContentDialog dialog)
    {
        try
        {
            var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            ApplyGlobalDialogThemeOverrides(transparent);

            FlattenDialogElement(FindDescendantByName<Border>(dialog, "BackgroundElement"), transparent, clearCornerRadius: false);
            FlattenDialogElement(FindDescendantByName<Border>(dialog, "Container"), transparent, clearCornerRadius: false);
            FlattenDialogElement(FindDescendantByName<Border>(dialog, "DialogSpace"), transparent, clearCornerRadius: false);
            FlattenDialogGrid(FindDescendantByName<Grid>(dialog, "LayoutRoot"), transparent);
            FlattenDialogGrid(FindDescendantByName<Grid>(dialog, "DialogSpace"), transparent);
            FlattenDialogGrid(FindDescendantByName<Grid>(dialog, "CommandSpace"), transparent);
            FlattenDialogScrollViewer(FindDescendantByName<ScrollViewer>(dialog, "ContentScrollViewer"), transparent);

            if (dialog.Content is FrameworkElement contentRoot)
            {
                var parent = VisualTreeHelper.GetParent(contentRoot);
                while (parent is not null && !ReferenceEquals(parent, dialog))
                {
                    if (parent is Border border)
                        FlattenDialogElement(border, transparent, clearCornerRadius: false);
                    else if (parent is Grid grid)
                        FlattenDialogGrid(grid, transparent);
                    else if (parent is Panel panel)
                        FlattenDialogPanel(panel, transparent);
                    else if (parent is ScrollViewer scrollViewer)
                        FlattenDialogScrollViewer(scrollViewer, transparent);

                    if (parent is UIElement uiElement)
                    {
                        uiElement.Shadow = null;
                        uiElement.Translation = Vector3.Zero;
                    }

                    if (parent is FrameworkElement framework)
                    {
                        framework.Margin = new Thickness(0);
                    }

                    parent = VisualTreeHelper.GetParent(parent);
                }
            }
        }
        catch
        {
            // non bloquant: le popup reste utilisable si la structure interne WinUI change
        }
    }

    private static void FlattenDialogElement(Border? border, Brush transparent, bool clearCornerRadius)
    {
        if (border is null)
            return;

        border.Background = transparent;
        border.BorderBrush = transparent;
        border.BorderThickness = new Thickness(0);
        border.Padding = new Thickness(0);
        border.Margin = new Thickness(0);
        border.Shadow = null;
        border.Translation = Vector3.Zero;

        if (clearCornerRadius)
            border.CornerRadius = new CornerRadius(0);
    }

    private static void FlattenDialogGrid(Grid? grid, Brush transparent)
    {
        if (grid is null)
            return;

        grid.Background = transparent;
        grid.Margin = new Thickness(0);
        grid.Shadow = null;
        grid.Translation = Vector3.Zero;
    }

    private static void FlattenDialogPanel(Panel? panel, Brush transparent)
    {
        if (panel is null)
            return;

        panel.Background = transparent;
        panel.Margin = new Thickness(0);
        panel.Shadow = null;
        panel.Translation = Vector3.Zero;
    }

    private static void FlattenDialogScrollViewer(ScrollViewer? scrollViewer, Brush transparent)
    {
        if (scrollViewer is null)
            return;

        scrollViewer.Background = transparent;
        scrollViewer.BorderBrush = transparent;
        scrollViewer.BorderThickness = new Thickness(0);
        scrollViewer.Padding = new Thickness(0);
        scrollViewer.Margin = new Thickness(0);
        scrollViewer.Shadow = null;
        scrollViewer.Translation = Vector3.Zero;
    }

    private static T? FindDescendantByName<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed && string.Equals(typed.Name, name, StringComparison.Ordinal))
                return typed;

            var nested = FindDescendantByName<T>(child, name);
            if (nested is not null)
                return nested;
        }

        return null;
    }
}
