namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow
{
    private OverlayDialogSession? _activeHelpOverlay;

    private Grid? _startupOverlay;
    private TextBlock? _startupOverlayStatusText;
    private TextBlock? _startupOverlaySubtitleText;
    private TextBlock? _startupOverlayTitleText;
    private ProgressRing? _startupOverlayRing;
    private Border? _startupOverlayBrand;
    private FontIcon? _startupOverlayErrorIcon;
    private Button? _startupOverlayRetryButton;
    private Button? _startupOverlaySetupButton;
    private StackPanel? _startupOverlayActionsRow;

    private Grid? _dialogOverlayHost;
    private Border? _dialogOverlaySmoke;
    private ContentPresenter? _dialogOverlayPresenter;
    private TaskCompletionSource<bool>? _dialogOverlayCompletion;
    private bool _dialogOverlayCloseOnBackgroundTap;
    private Action<Size>? _dialogOverlayResizeHandler;

    private sealed class OverlayDialogSession
    {
        private readonly Task _completion;
        private readonly Action _close;

        internal OverlayDialogSession(Task completion, Action close)
        {
            _completion = completion;
            _close = close;
        }

        internal Task Completion => _completion;

        internal void Close()
        {
            _close();
        }
    }

    private void EnsureStartupOverlay()
    {
        if (_startupOverlay is not null || Root is null)
            return;

        var light = UseLightPalette();

        _startupOverlayBrand = new Border
        {
            Width = 74,
            Height = 74,
            CornerRadius = new CornerRadius(37),
            Background = light ? UiBrush(0xF8, 0xFB, 0xFE) : UiBrush(0x17, 0x1F, 0x2A),
            BorderBrush = light ? UiBrush(0xC6, 0xD2, 0xDE) : UiBrush(0x2C, 0x36, 0x43),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new Image
            {
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/SAAIA_Icone.png")),
                Stretch = Stretch.Uniform,
                Width = 42,
                Height = 42,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        // Error-state icon (red cross) shown in place of the brand circle when the
        // startup probe fails. Built once, shown/hidden via SetStartupOverlayError().
        _startupOverlayErrorIcon = new FontIcon
        {
            Glyph = "",
            FontSize = 56,
            Foreground = light ? UiBrush(0xC2, 0x3B, 0x44) : UiBrush(0xE2, 0x6A, 0x72),
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed
        };

        _startupOverlayRetryButton = new Button
        {
            Content = ClientUiText.Get("startup.retry", _appSettings.UiLanguage),
            Padding = new Thickness(20, 10, 20, 10),
            CornerRadius = new CornerRadius(14),
            Background = light ? UiBrush(0x5E, 0x7A, 0x97) : UiBrush(0x3A, 0x84, 0xD8),
            Foreground = UiBrush(0xFF, 0xFF, 0xFF),
            BorderThickness = new Thickness(0)
        };
        _startupOverlayRetryButton.Click += async (_, __) =>
        {
            HideStartupOverlay();
            await InitializeUserModeAsync();
        };

        // Secondary "Configurer" button: opens the setup wizard so the user can fix the
        // backend URL (or anything else) right from the error overlay — no need to dig
        // through Settings to reach the wrench/setup icon.
        _startupOverlaySetupButton = new Button
        {
            Content = ClientUiText.Get("startup.configure", _appSettings.UiLanguage),
            Padding = new Thickness(20, 10, 20, 10),
            CornerRadius = new CornerRadius(14),
            Background = light ? UiBrush(0xF1, 0xF4, 0xF8) : UiBrush(0x19, 0x1D, 0x26),
            Foreground = light ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xF5, 0xF7, 0xFB),
            BorderBrush = light ? UiBrush(0xCC, 0xD6, 0xE4) : UiBrush(0x36, 0x38, 0x40),
            BorderThickness = new Thickness(1)
        };
        _startupOverlaySetupButton.Click += async (_, __) =>
        {
            HideStartupOverlay();
            await ShowSetupWizardAsync();
            // After the wizard closes, retry the init so the new URL kicks in.
            await InitializeUserModeAsync();
        };

        _startupOverlayActionsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Children = { _startupOverlaySetupButton, _startupOverlayRetryButton }
        };

        _startupOverlaySubtitleText = new TextBlock
        {
            Text = ClientUiText.Get("startup.subtitle", _appSettings.UiLanguage),
            Opacity = 0.84,
            FontSize = 15,
            Foreground = light ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE),
            TextWrapping = TextWrapping.WrapWholeWords,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 560,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        _startupOverlayStatusText = new TextBlock
        {
            Text = ClientUiText.Get("startup.status.initializing", _appSettings.UiLanguage),
            Opacity = 0.9,
            FontSize = 14,
            Foreground = light ? UiBrush(0x3E, 0x4C, 0x5F) : UiBrush(0xD5, 0xDE, 0xEA),
            TextWrapping = TextWrapping.WrapWholeWords,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        _startupOverlayRing = new ProgressRing
        {
            IsActive = true,
            Width = 28,
            Height = 28,
            Foreground = light ? UiBrush(0x5E, 0x7A, 0x97) : UiBrush(0x56, 0xA7, 0xE7),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        _startupOverlayTitleText = new TextBlock
        {
            Text = ClientUiText.Get("startup.title", _appSettings.UiLanguage),
            FontSize = 28,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = light ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xF7, 0xFA, 0xFE),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };

        var card = new Border
        {
            CornerRadius = new CornerRadius(28),
            Padding = new Thickness(30, 28, 30, 26),
            Background = light ? UiBrush(0xF5, 0xF8, 0xFC, 0xF6) : UiBrush(0x12, 0x17, 0x20, 0xF4),
            BorderBrush = light ? UiBrush(0xC4, 0xD0, 0xDD) : UiBrush(0x2E, 0x38, 0x45),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Spacing = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    _startupOverlayBrand,
                    _startupOverlayErrorIcon,
                    _startupOverlayTitleText,
                    _startupOverlaySubtitleText,
                    _startupOverlayRing,
                    _startupOverlayStatusText,
                    _startupOverlayActionsRow
                }
            }
        };

        _startupOverlay = new Grid
        {
            Background = light ? UiBrush(0xE8, 0xEE, 0xF5, 0xD8) : UiBrush(0x08, 0x0B, 0x12, 0xD8),
            Visibility = Visibility.Visible,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            // Root has Padding="12,0,12,12" — bleed the smoke into that padding so the
            // overlay covers the entire window (no leftover slivers on the edges).
            Margin = new Thickness(-12, 0, -12, -12),
            Children = { card }
        };
        Grid.SetRow(_startupOverlay, 0);
        Grid.SetRowSpan(_startupOverlay, 3);
        Root.Children.Add(_startupOverlay);
    }

    private void RebuildStartupOverlayForTheme()
    {
        if (Root is null)
            return;

        var wasVisible = _startupOverlay?.Visibility == Visibility.Visible;
        var previousSubtitle = _startupOverlaySubtitleText?.Text ?? ClientUiText.Get("startup.subtitle", _appSettings.UiLanguage);
        var previousStatus = _startupOverlayStatusText?.Text ?? ClientUiText.Get("startup.status.initializing", _appSettings.UiLanguage);

        try
        {
            if (_startupOverlay is not null)
            {
                Root.Children.Remove(_startupOverlay);
                _startupOverlay = null;
                _startupOverlayStatusText = null;
                _startupOverlaySubtitleText = null;
                _startupOverlayTitleText = null;
                _startupOverlayRing = null;
                _startupOverlayBrand = null;
                _startupOverlayErrorIcon = null;
                _startupOverlayRetryButton = null;
                _startupOverlaySetupButton = null;
                _startupOverlayActionsRow = null;
            }
        }
        catch { }

        EnsureStartupOverlay();

        if (!wasVisible)
        {
            HideStartupOverlay();
        }
        else
        {
            ShowStartupOverlay(previousSubtitle, previousStatus);
        }
    }

    private void ShowStartupOverlay(string subtitle, string status)
    {
        EnsureStartupOverlay();
        if (_startupOverlay is null)
            return;

        _startupOverlay.Visibility = Visibility.Visible;

        // Reset to "loading" mode in case we're re-entering after a previous error.
        if (_startupOverlayTitleText is not null)
            _startupOverlayTitleText.Text = ClientUiText.Get("startup.title", _appSettings.UiLanguage);
        if (_startupOverlayBrand is not null)
            _startupOverlayBrand.Visibility = Visibility.Visible;
        if (_startupOverlayErrorIcon is not null)
            _startupOverlayErrorIcon.Visibility = Visibility.Collapsed;
        if (_startupOverlayActionsRow is not null)
            _startupOverlayActionsRow.Visibility = Visibility.Collapsed;
        if (_startupOverlayRing is not null)
        {
            _startupOverlayRing.Visibility = Visibility.Visible;
            _startupOverlayRing.IsActive = true;
        }

        if (_startupOverlaySubtitleText is not null)
            _startupOverlaySubtitleText.Text = subtitle;
        if (_startupOverlayStatusText is not null)
            _startupOverlayStatusText.Text = status;
    }

    // Switch the startup overlay into an error state: red cross instead of brand,
    // "Pas connecté au serveur" title, the error message as the status, and a retry
    // button that re-runs InitializeUserModeAsync.
    private void ShowStartupOverlayError(string title, string detail)
    {
        EnsureStartupOverlay();
        if (_startupOverlay is null)
            return;

        _startupOverlay.Visibility = Visibility.Visible;

        if (_startupOverlayTitleText is not null)
            _startupOverlayTitleText.Text = title;
        if (_startupOverlayBrand is not null)
            _startupOverlayBrand.Visibility = Visibility.Collapsed;
        if (_startupOverlayErrorIcon is not null)
            _startupOverlayErrorIcon.Visibility = Visibility.Visible;
        if (_startupOverlayRing is not null)
        {
            _startupOverlayRing.IsActive = false;
            _startupOverlayRing.Visibility = Visibility.Collapsed;
        }
        if (_startupOverlaySubtitleText is not null)
            _startupOverlaySubtitleText.Text = detail;
        if (_startupOverlayStatusText is not null)
            _startupOverlayStatusText.Text = string.Empty;
        if (_startupOverlayRetryButton is not null)
            _startupOverlayRetryButton.Content = ClientUiText.Get("startup.retry", _appSettings.UiLanguage);
        if (_startupOverlaySetupButton is not null)
            _startupOverlaySetupButton.Content = ClientUiText.Get("startup.configure", _appSettings.UiLanguage);
        if (_startupOverlayActionsRow is not null)
            _startupOverlayActionsRow.Visibility = Visibility.Visible;
    }

    private void HideStartupOverlay()
    {
        if (_startupOverlay is null)
            return;

        _startupOverlay.Visibility = Visibility.Collapsed;
        if (_startupOverlayRing is not null)
            _startupOverlayRing.IsActive = false;
    }


    private void EnsureDialogOverlay()
    {
        if (_dialogOverlayHost is not null || Root is null)
            return;

        _dialogOverlaySmoke = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = UseLightPalette()
                ? UiBrush(0x00, 0x00, 0x00, 0x08)
                : UiBrush(0x00, 0x00, 0x00, 0x12)
        };
        _dialogOverlaySmoke.PointerPressed += (_, __) =>
        {
            if (_dialogOverlayCloseOnBackgroundTap)
                CloseDialogOverlay();
        };

        var viewport = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(24, 20, 24, 24)
        };

        _dialogOverlayPresenter = new ContentPresenter
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        viewport.Children.Add(_dialogOverlayPresenter);

        _dialogOverlayHost = new Grid
        {
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        _dialogOverlayHost.KeyDown += DialogOverlayHost_KeyDown;
        _dialogOverlayHost.Children.Add(_dialogOverlaySmoke);
        _dialogOverlayHost.Children.Add(viewport);
        Grid.SetRow(_dialogOverlayHost, 1);
        Grid.SetRowSpan(_dialogOverlayHost, 2);
        Root.Children.Add(_dialogOverlayHost);
    }

    private void DialogOverlayHost_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != global::Windows.System.VirtualKey.Escape)
            return;

        e.Handled = true;
        CloseDialogOverlay();
    }

    private void RefreshDialogOverlayTheme()
    {
        if (_dialogOverlaySmoke is null)
            return;

        _dialogOverlaySmoke.Background = UseLightPalette()
            ? UiBrush(0x00, 0x00, 0x00, 0x08)
            : UiBrush(0x00, 0x00, 0x00, 0x12);
    }

    private OverlayDialogSession ShowOverlayDialog(UIElement content, bool closeOnBackgroundTap = false, Action<Size>? resizeHandler = null)
    {
        EnsureDialogOverlay();
        CloseDialogOverlay();

        if (_dialogOverlayHost is null || _dialogOverlayPresenter is null)
            throw new InvalidOperationException(LocalRuntimeText("L'hote de dialogue n'est pas disponible.", "Dialog overlay host is not available.", "El host del dialogo no esta disponible.", "O anfitriao do dialogo nao esta disponivel.", "Der Dialog-Host ist nicht verfuegbar.", "L'host del dialogo non e disponibile.", UiLang));

        _dialogOverlayPresenter.HorizontalAlignment = HorizontalAlignment.Center;
        _dialogOverlayPresenter.VerticalAlignment = VerticalAlignment.Center;
        _dialogOverlayCloseOnBackgroundTap = closeOnBackgroundTap;
        _dialogOverlayResizeHandler = resizeHandler;
        _dialogOverlayCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dialogOverlayPresenter.Content = content;
        _dialogOverlayHost.Visibility = Visibility.Visible;
        _dialogOverlayHost.IsHitTestVisible = true;
        RefreshDialogOverlayTheme();
        if (Root is not null)
        {
            TrySoftUi("ShowOverlayDialog.ResizeHandler.Initial", () => _dialogOverlayResizeHandler?.Invoke(new Size(Root.ActualWidth, Root.ActualHeight)));
        }

        try
        {
            _dialogOverlayHost.UpdateLayout();
            if (content is Control control)
            {
                control.Focus(FocusState.Programmatic);
            }
            else if (content is FrameworkElement element)
            {
                element.Focus(FocusState.Programmatic);
            }
        }
        catch
        {
            // non bloquant
        }

        return new OverlayDialogSession(_dialogOverlayCompletion.Task, CloseDialogOverlay);
    }

    private OverlayDialogSession ShowOverlayPage(UIElement content, bool closeOnBackgroundTap = false, Action<Size>? resizeHandler = null)
    {
        EnsureDialogOverlay();
        CloseDialogOverlay();

        if (_dialogOverlayHost is null || _dialogOverlayPresenter is null)
            throw new InvalidOperationException(LocalRuntimeText("L'hote de dialogue n'est pas disponible.", "Dialog overlay host is not available.", "El host del dialogo no esta disponible.", "O anfitriao do dialogo nao esta disponivel.", "Der Dialog-Host ist nicht verfuegbar.", "L'host del dialogo non e disponibile.", UiLang));

        _dialogOverlayPresenter.HorizontalAlignment = HorizontalAlignment.Stretch;
        _dialogOverlayPresenter.VerticalAlignment = VerticalAlignment.Stretch;
        _dialogOverlayCloseOnBackgroundTap = closeOnBackgroundTap;
        _dialogOverlayResizeHandler = resizeHandler;
        _dialogOverlayCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dialogOverlayPresenter.Content = content;
        _dialogOverlayHost.Visibility = Visibility.Visible;
        _dialogOverlayHost.IsHitTestVisible = true;
        RefreshDialogOverlayTheme();
        if (Root is not null)
        {
            TrySoftUi("ShowOverlayPage.ResizeHandler.Initial", () => _dialogOverlayResizeHandler?.Invoke(new Size(Root.ActualWidth, Root.ActualHeight)));
        }

        try
        {
            _dialogOverlayHost.UpdateLayout();
            if (content is FrameworkElement element)
                element.Focus(FocusState.Programmatic);
        }
        catch
        {
        }

        return new OverlayDialogSession(_dialogOverlayCompletion.Task, CloseDialogOverlay);
    }

    private void CloseDialogOverlay()
    {
        if (_dialogOverlayHost is null || _dialogOverlayPresenter is null)
            return;

        var completion = _dialogOverlayCompletion;
        if (completion is null)
            return;

        _dialogOverlayCompletion = null;
        _dialogOverlayCloseOnBackgroundTap = false;
        _dialogOverlayResizeHandler = null;
        _dialogOverlayPresenter.Content = null;
        _dialogOverlayPresenter.HorizontalAlignment = HorizontalAlignment.Center;
        _dialogOverlayPresenter.VerticalAlignment = VerticalAlignment.Center;
        _dialogOverlayHost.Visibility = Visibility.Collapsed;
        _dialogOverlayHost.IsHitTestVisible = false;
        completion.TrySetResult(true);
    }

    private void CloseTransientDialogs()
    {
        try
        {
            _activeHelpOverlay?.Close();
        }
        catch
        {
        }
        finally
        {
            _activeHelpOverlay = null;
        }

        try
        {
            CloseDialogOverlay();
        }
        catch
        {
        }
    }
}
