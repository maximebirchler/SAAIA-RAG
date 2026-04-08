using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace SAAIA.Client.WinUI;


public sealed partial class MainWindow
{
    private Window? _adminJobsWindow;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _adminJobsRefreshTimer;
    private AdminJobsOverlayContext? _adminJobsOverlayContext;

    private sealed class AdminJobsOverlayContext
    {
        public required Border Shell { get; init; }
        public required TextBlock SummaryText { get; init; }
        public required TextBlock SelectionText { get; init; }
        public required TextBox SearchBox { get; init; }
        public required ComboBox TypeCombo { get; init; }
        public required ComboBox StatusCombo { get; init; }
        public required ToggleSwitch AutoRefreshToggle { get; init; }
        public required Button RefreshButton { get; init; }
        public required Button DeleteSelectionButton { get; init; }
        public required Button PurgeButton { get; init; }
        public required StackPanel MetricsHost { get; init; }
        public required StackPanel GroupsHost { get; init; }
        public required Border DetailsCard { get; init; }
        public required StackPanel DetailsHost { get; init; }
        public required ColumnDefinition DetailsColumn { get; init; }
        public List<AdminJobListItem> Items { get; set; } = new();
        public HashSet<string> SelectedTerminalJobIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsRefreshing { get; set; }
        public int HistoryTake { get; set; } = 50;
        public string? SelectedJobId { get; set; }
        public string? LastVisibleRenderSignature { get; set; }
    }

    private sealed class AdminJobListItem
    {
        public string JobId { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public string JobType { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string? DocId { get; init; }
        public string? DocPath { get; init; }
        public string? Level { get; init; }
        public string? LastError { get; init; }
        public DateTimeOffset? CreatedAt { get; init; }
        public DateTimeOffset? StartedAt { get; init; }
        public DateTimeOffset? FinishedAt { get; init; }
        public string? ProgressPhase { get; init; }
        public int? ProgressCurrent { get; init; }
        public int? ProgressTotal { get; init; }
        public int? ProgressPercent { get; init; }
        public bool? CancelRequested { get; init; }
        public string? EnqueueSource { get; init; }
        public string? DocumentStatus { get; init; }
        public int? DocumentIngestionVersion { get; init; }
        public int? DocumentIndexedVersion { get; init; }
        public bool? DocumentAutoIngestPaused { get; init; }
        public string? DocumentAutoIngestPauseReason { get; init; }

        public bool IsTerminal => IsTrackedJobTerminalStatus(Status);
        public bool IsRunning
            => string.Equals(NormalizeTrackedJobStatus(Status), "running", StringComparison.OrdinalIgnoreCase)
               || string.Equals(NormalizeTrackedJobStatus(Status), "cancel_requested", StringComparison.OrdinalIgnoreCase);
        public bool IsQueued => string.Equals(NormalizeTrackedJobStatus(Status), "queued", StringComparison.OrdinalIgnoreCase);
        public bool IsPaused => string.Equals(NormalizeTrackedJobStatus(Status), "paused", StringComparison.OrdinalIgnoreCase);
        public bool IsFailedLike
            => string.Equals(NormalizeTrackedJobStatus(Status), "failed", StringComparison.OrdinalIgnoreCase)
               || string.Equals(NormalizeTrackedJobStatus(Status), "canceled", StringComparison.OrdinalIgnoreCase);
        public bool IsCanceled
            => string.Equals(NormalizeTrackedJobStatus(Status), "canceled", StringComparison.OrdinalIgnoreCase);
        public bool IsFailed
            => string.Equals(NormalizeTrackedJobStatus(Status), "failed", StringComparison.OrdinalIgnoreCase);

        public string DisplayTitle
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(DocPath))
                    return Path.GetFileName(DocPath) ?? DocPath!;
                if (!string.IsNullOrWhiteSpace(Level))
                    return Level!;
                return JobId;
            }
        }

        public string? DisplaySubtitle
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(DocPath))
                {
                    var directory = Path.GetDirectoryName(DocPath!)?.Replace('\\', '/');
                    if (!string.IsNullOrWhiteSpace(directory) && !string.Equals(directory, ".", StringComparison.Ordinal))
                        return directory;
                }

                return null;
            }
        }
    }

    private void RefreshAdminJobsUiVisibility()
    {
        if (HeaderJobsButton is not null)
        {
            HeaderJobsButton.Visibility = _api.HasAdminKey ? Visibility.Visible : Visibility.Collapsed;
            HeaderJobsButton.IsEnabled = _api.HasAdminKey && !_isGenerating;
            TrySoftUi("RefreshAdminJobsUiVisibility.ApplyHeaderChrome", () => ApplyHeaderButtonChrome(HeaderJobsButton));
            HeaderJobsButton.Opacity = _api.HasAdminKey ? 1d : 0d;
            TrySoftUi("RefreshAdminJobsUiVisibility.UpdateLayout", () => HeaderJobsButton.UpdateLayout());
            try { DispatcherQueue.TryEnqueue(() => { try { ApplyHeaderButtonChrome(HeaderJobsButton); } catch { } try { HeaderJobsButton.UpdateLayout(); } catch { } }); } catch { }
        }

        if (!_api.HasAdminKey)
            CloseAdminJobsWindow();
    }

    private static bool ShouldDetachTrackedJobToAdminJobsPanel(Services.ToolAgent.DirectCommandTrackedJob? trackedJob)
        => trackedJob is not null
           && !string.IsNullOrWhiteSpace(trackedJob.JobId)
           && string.Equals((trackedJob.JobType ?? string.Empty).Trim(), "ingestion", StringComparison.OrdinalIgnoreCase);

    private string BuildDetachedAdminJobLaunchMessage(Services.ToolAgent.DirectCommandTrackedJob trackedJob)
    {
        var label = string.IsNullOrWhiteSpace(trackedJob.DisplayLabel)
            ? (!string.IsNullOrWhiteSpace(trackedJob.DocPath) ? trackedJob.DocPath! : trackedJob.JobId)
            : trackedJob.DisplayLabel;
        return ClientUiText.Format("admin.jobs.detached.launch", UiLang, label, trackedJob.JobId);
    }

    private async void HeaderJobsButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowAdminJobsOverlayAsync();
    }

    private async Task ShowAdminJobsOverlayAsync(string? focusJobId = null)
    {
        if (!_api.HasAdminKey)
        {
            Status(ClientUiText.Get("admin.jobs.no_admin", UiLang));
            return;
        }

        if (_adminJobsWindow is not null && _adminJobsOverlayContext is not null)
        {
            if (!string.IsNullOrWhiteSpace(focusJobId))
            {
                _adminJobsOverlayContext.SearchBox.Text = focusJobId!;
                await RefreshAdminJobsOverlayAsync(_adminJobsOverlayContext, CancellationToken.None).ConfigureAwait(true);
            }

            try
            {
                _adminJobsWindow.Activate();
            }
            catch
            {
            }
            return;
        }

        var searchBox = new TextBox
        {
            PlaceholderText = ClientUiText.Get("admin.jobs.search.placeholder", UiLang),
            MinWidth = 240
        };

        var typeCombo = new ComboBox
        {
            MinWidth = 170,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        typeCombo.Items.Add(new ComboBoxItem { Content = ClientUiText.Get("admin.jobs.filter.all", UiLang), Tag = "" });
        typeCombo.Items.Add(new ComboBoxItem { Content = ClientUiText.Get("admin.jobs.filter.ingestion", UiLang), Tag = "ingestion" });
        typeCombo.Items.Add(new ComboBoxItem { Content = ClientUiText.Get("admin.jobs.filter.summary", UiLang), Tag = "summary" });
        typeCombo.SelectedIndex = 0;

        var statusCombo = new ComboBox
        {
            MinWidth = 170,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        statusCombo.Items.Add(new ComboBoxItem { Content = ClientUiText.Get("admin.jobs.status_filter.all", UiLang), Tag = "all" });
        statusCombo.Items.Add(new ComboBoxItem { Content = ClientUiText.Get("admin.jobs.status_filter.active", UiLang), Tag = "active" });
        statusCombo.Items.Add(new ComboBoxItem { Content = ClientUiText.Get("admin.jobs.status.queued", UiLang), Tag = "queued" });
        statusCombo.Items.Add(new ComboBoxItem { Content = ClientUiText.Get("admin.jobs.status.running", UiLang), Tag = "running" });
        statusCombo.Items.Add(new ComboBoxItem { Content = ClientUiText.Get("admin.jobs.status.cancel_requested", UiLang), Tag = "cancel_requested" });
        statusCombo.Items.Add(new ComboBoxItem { Content = ClientUiText.Get("admin.jobs.status.paused", UiLang), Tag = "paused" });
        statusCombo.Items.Add(new ComboBoxItem { Content = ClientUiText.Get("admin.jobs.status.done", UiLang), Tag = "done" });
        statusCombo.Items.Add(new ComboBoxItem { Content = ClientUiText.Get("admin.jobs.status.failed", UiLang), Tag = "failed" });
        statusCombo.Items.Add(new ComboBoxItem { Content = ClientUiText.Get("admin.jobs.status.canceled", UiLang), Tag = "canceled" });
        statusCombo.SelectedIndex = 1;

        var autoRefreshToggle = new ToggleSwitch
        {
            Header = ClientUiText.Get("admin.jobs.auto_refresh", UiLang),
            IsOn = false,
            OnContent = ClientUiText.Get("admin.jobs.auto_on", UiLang),
            OffContent = ClientUiText.Get("admin.jobs.auto_off", UiLang),
            MinWidth = 136,
            VerticalAlignment = VerticalAlignment.Center
        };

        var refreshButton = BuildDialogFooterButton(ClientUiText.Get("admin.jobs.refresh", UiLang), primary: true);
        refreshButton.MinWidth = 140;
        refreshButton.HorizontalAlignment = HorizontalAlignment.Right;
        refreshButton.Visibility = Visibility.Visible;

        var deleteSelectionButton = BuildDialogFooterButton(ClientUiText.Get("admin.jobs.delete_selection", UiLang), destructive: true);
        deleteSelectionButton.MinWidth = 180;
        deleteSelectionButton.IsEnabled = false;
        deleteSelectionButton.HorizontalAlignment = HorizontalAlignment.Left;

        var purgeButton = BuildDialogFooterButton(ClientUiText.Get("admin.jobs.purge", UiLang));
        purgeButton.MinWidth = 160;
        purgeButton.HorizontalAlignment = HorizontalAlignment.Left;

        var summaryText = new TextBlock
        {
            Text = ClientUiText.Get("admin.jobs.loading", UiLang),
            TextWrapping = TextWrapping.WrapWholeWords,
            Foreground = UseLightPalette() ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE)
        };

        var selectionText = new TextBlock
        {
            Text = ClientUiText.Get("admin.jobs.selection.none", UiLang),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = UseLightPalette() ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE)
        };

        var metricsHost = new StackPanel { Spacing = 10 };
        var groupsHost = new StackPanel { Spacing = 16 };
        var scrollViewer = new ScrollViewer
        {
            Content = groupsHost,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        var detailsHost = new StackPanel { Spacing = 8 };
        var detailsCard = BuildDialogSurfaceCard(detailsHost, new Thickness(16));
        detailsCard.MinWidth = 340;
        detailsCard.VerticalAlignment = VerticalAlignment.Stretch;

        var toolbarGrid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        toolbarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
        toolbarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(searchBox, 0);
        Grid.SetColumn(typeCombo, 1);
        Grid.SetColumn(statusCombo, 2);
        Grid.SetColumn(autoRefreshToggle, 3);
        toolbarGrid.Children.Add(searchBox);
        toolbarGrid.Children.Add(typeCombo);
        toolbarGrid.Children.Add(statusCombo);
        toolbarGrid.Children.Add(autoRefreshToggle);

        var footerActions = new Grid { ColumnSpacing = 12 };
        footerActions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footerActions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(deleteSelectionButton, 0);
        Grid.SetColumn(purgeButton, 1);
        footerActions.Children.Add(deleteSelectionButton);
        footerActions.Children.Add(purgeButton);

        var footer = new Grid { ColumnSpacing = 16 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(selectionText, 0);
        Grid.SetColumn(footerActions, 1);
        footer.Children.Add(selectionText);
        footer.Children.Add(footerActions);
        var bodyGrid = new Grid { ColumnSpacing = 16 };
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var detailsColumn = new ColumnDefinition { Width = new GridLength(0) };
        bodyGrid.ColumnDefinitions.Add(detailsColumn);
        Grid.SetColumn(scrollViewer, 0);
        Grid.SetColumn(detailsCard, 1);
        detailsCard.Visibility = Visibility.Collapsed;
        bodyGrid.Children.Add(scrollViewer);
        bodyGrid.Children.Add(detailsCard);

        var pageHeaderGrid = new Grid { ColumnSpacing = 16 };
        pageHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pageHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleBox = new StackPanel { Spacing = 4 };
        titleBox.Children.Add(new TextBlock
        {
            Text = ClientUiText.Get("header.jobs", UiLang),
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            Foreground = UseLightPalette() ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xF5, 0xF7, 0xFB)
        });
        titleBox.Children.Add(summaryText);
        Grid.SetColumn(titleBox, 0);
        pageHeaderGrid.Children.Add(titleBox);

        refreshButton.MinWidth = 140;
        refreshButton.HorizontalAlignment = HorizontalAlignment.Right;
        refreshButton.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(refreshButton, 1);
        pageHeaderGrid.Children.Add(refreshButton);

        var pageRoot = new Grid
        {
            Background = UseLightPalette() ? UiBrush(0xE9, 0xEE, 0xF4) : UiBrush(0x0A, 0x0D, 0x12),
            Padding = new Thickness(18),
            RowSpacing = 16
        };
        pageRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pageRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pageRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pageRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        pageRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var metricsCard = BuildDialogSurfaceCard(metricsHost, new Thickness(14));
        var toolbarCard = BuildDialogSurfaceCard(toolbarGrid, new Thickness(14));
        var footerCard = BuildDialogSurfaceCard(footer, new Thickness(14));
        Grid.SetRow(pageHeaderGrid, 0);
        Grid.SetRow(metricsCard, 1);
        Grid.SetRow(toolbarCard, 2);
        Grid.SetRow(bodyGrid, 3);
        Grid.SetRow(footerCard, 4);
        pageRoot.Children.Add(pageHeaderGrid);
        pageRoot.Children.Add(metricsCard);
        pageRoot.Children.Add(toolbarCard);
        pageRoot.Children.Add(bodyGrid);
        pageRoot.Children.Add(footerCard);

        var titleBar = new Grid
        {
            Height = 40,
            Background = new SolidColorBrush(TitleBarBackgroundColor)
        };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });

        var brand = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0)
        };
        brand.Children.Add(new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Background = UseLightPalette() ? UiBrush(0xC9, 0xD8, 0xE6) : UiBrush(0x1A, 0x1F, 0x29),
            Child = new Image
            {
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/SAAIA_Icone.png")),
                Stretch = Stretch.Uniform,
                Opacity = 0.92
            }
        });
        brand.Children.Add(new TextBlock
        {
            Text = "SAAIA",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(TitleBarForegroundColor)
        });
        Grid.SetColumn(brand, 0);
        titleBar.Children.Add(brand);

        var windowRoot = new Grid
        {
            Background = UseLightPalette() ? UiBrush(0xE9, 0xEE, 0xF4) : UiBrush(0x0A, 0x0D, 0x12)
        };
        windowRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        windowRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(pageRoot, 1);
        windowRoot.Children.Add(titleBar);
        windowRoot.Children.Add(pageRoot);

        var window = new Window();
        window.Title = ClientUiText.Get("header.jobs", UiLang);
        window.Content = windowRoot;
        try
        {
            window.ExtendsContentIntoTitleBar = true;
            window.SetTitleBar(titleBar);
            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                var tb = window.AppWindow.TitleBar;
                tb.BackgroundColor = TitleBarBackgroundColor;
                tb.ForegroundColor = TitleBarForegroundColor;
                tb.InactiveBackgroundColor = TitleBarInactiveBackgroundColor;
                tb.InactiveForegroundColor = TitleBarForegroundColor;
                tb.ButtonBackgroundColor = TitleBarTransparentColor;
                tb.ButtonForegroundColor = TitleBarForegroundColor;
                tb.ButtonHoverBackgroundColor = TitleBarButtonHoverColor;
                tb.ButtonHoverForegroundColor = TitleBarForegroundColor;
                tb.ButtonPressedBackgroundColor = TitleBarButtonPressedColor;
                tb.ButtonPressedForegroundColor = TitleBarForegroundColor;
                tb.ButtonInactiveBackgroundColor = TitleBarTransparentColor;
                tb.ButtonInactiveForegroundColor = TitleBarForegroundColor;
            }
        }
        catch { }
        TryPositionAdminJobsWindow(window);

        var detailsHeader = new Grid { ColumnSpacing = 8 };
        detailsHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        detailsHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var detailsTitle = new TextBlock
        {
            Text = ClientUiText.Get("admin.jobs.detail.title", UiLang),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = UseLightPalette() ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xF5, 0xF7, 0xFB)
        };
        var closeDetailsButton = BuildDialogChromeIconButton(ClientUiText.Get("dialog.close", UiLang));
        Grid.SetColumn(detailsTitle, 0);
        Grid.SetColumn(closeDetailsButton, 1);
        detailsHeader.Children.Add(detailsTitle);
        detailsHeader.Children.Add(closeDetailsButton);
        detailsHost.Children.Add(detailsHeader);

        var context = new AdminJobsOverlayContext
        {
            Shell = new Border(),
            SummaryText = summaryText,
            SelectionText = selectionText,
            SearchBox = searchBox,
            TypeCombo = typeCombo,
            StatusCombo = statusCombo,
            AutoRefreshToggle = autoRefreshToggle,
            RefreshButton = refreshButton,
            DeleteSelectionButton = deleteSelectionButton,
            PurgeButton = purgeButton,
            MetricsHost = metricsHost,
            GroupsHost = groupsHost,
            DetailsCard = detailsCard,
            DetailsHost = detailsHost,
            DetailsColumn = detailsColumn
        };

        closeDetailsButton.Click += (_, __) =>
        {
            context.SelectedJobId = null;
            RenderAdminJobsDetails(context);
        };

        searchBox.TextChanged += (_, __) => RenderAdminJobsOverlay(context);
        typeCombo.SelectionChanged += async (_, __) => await RefreshAdminJobsOverlayAsync(context, CancellationToken.None).ConfigureAwait(true);
        statusCombo.SelectionChanged += (_, __) => RenderAdminJobsOverlay(context);
        autoRefreshToggle.Toggled += (_, __) =>
        {
            UpdateAdminJobsRefreshTimer();
            UpdateAdminJobsRefreshControls(context);
        };
        refreshButton.Click += async (_, __) => await RefreshAdminJobsOverlayAsync(context, CancellationToken.None).ConfigureAwait(true);
        deleteSelectionButton.Click += async (_, __) => await DeleteSelectedAdminJobsAsync(context).ConfigureAwait(true);
        var purgeFlyout = BuildAdminJobsPurgeFlyout(context);
        purgeButton.Click += (_, __) => purgeFlyout.ShowAt(purgeButton);
        window.Closed += (_, __) =>
        {
            try { _adminJobsRefreshTimer?.Stop(); } catch { }
            _adminJobsRefreshTimer = null;
            _adminJobsWindow = null;
            _adminJobsOverlayContext = null;
        };

        _adminJobsWindow = window;
        _adminJobsOverlayContext = context;
        UpdateAdminJobsRefreshControls(context);
        UpdateAdminJobsRefreshTimer();

        if (!string.IsNullOrWhiteSpace(focusJobId))
            searchBox.Text = focusJobId!;

        window.Activate();
        await RefreshAdminJobsOverlayAsync(context, CancellationToken.None).ConfigureAwait(true);
    }

    private MenuFlyout BuildAdminJobsPurgeFlyout(AdminJobsOverlayContext context)
    {
        var flyout = new MenuFlyout();

        var visibleDone = new MenuFlyoutItem { Text = ClientUiText.Get("admin.jobs.purge.visible_done", UiLang) };
        visibleDone.Click += async (_, __) =>
            await DeleteAdminJobsByPredicateAsync(context, item => item.IsTerminal && !item.IsFailed && !item.IsCanceled).ConfigureAwait(true);
        flyout.Items.Add(visibleDone);

        var visibleFailed = new MenuFlyoutItem { Text = ClientUiText.Get("admin.jobs.purge.visible_failed", UiLang) };
        visibleFailed.Click += async (_, __) =>
            await DeleteAdminJobsByPredicateAsync(context, item => item.IsTerminal && item.IsFailed).ConfigureAwait(true);
        flyout.Items.Add(visibleFailed);

        var visibleCanceled = new MenuFlyoutItem { Text = ClientUiText.Get("admin.jobs.purge.visible_canceled", UiLang) };
        visibleCanceled.Click += async (_, __) =>
            await DeleteAdminJobsByPredicateAsync(context, item => item.IsTerminal && item.IsCanceled).ConfigureAwait(true);
        flyout.Items.Add(visibleCanceled);

        var visibleAll = new MenuFlyoutItem { Text = ClientUiText.Get("admin.jobs.purge.visible_all", UiLang) };
        visibleAll.Click += async (_, __) =>
            await DeleteAdminJobsByPredicateAsync(context, item => item.IsTerminal).ConfigureAwait(true);
        flyout.Items.Add(visibleAll);

        return flyout;
    }


    private void TryPositionAdminJobsWindow(Window window)
    {
        try
        {
            var mainAppWindow = AppWindow;
            var adminAppWindow = window.AppWindow;
            var offset = 28;
            var x = mainAppWindow.Position.X + offset;
            var y = mainAppWindow.Position.Y + offset;
            var width = Math.Max(640, mainAppWindow.Size.Width);
            var height = Math.Max(480, mainAppWindow.Size.Height);
            adminAppWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(x, y, width, height));
            return;
        }
        catch
        {
        }

        try { window.AppWindow.Resize(new global::Windows.Graphics.SizeInt32(1360, 900)); } catch { }
    }

    private void UpdateAdminJobsRefreshControls(AdminJobsOverlayContext context)
    {
        context.RefreshButton.Visibility = context.AutoRefreshToggle.IsOn ? Visibility.Collapsed : Visibility.Visible;
        context.RefreshButton.IsEnabled = !context.IsRefreshing && !context.AutoRefreshToggle.IsOn;
    }

    private void UpdateAdminJobsRefreshTimer()
    {
        try
        {
            _adminJobsRefreshTimer?.Stop();
            _adminJobsRefreshTimer = null;

            if (_adminJobsWindow is null || _adminJobsOverlayContext is null)
                return;
            if (_adminJobsOverlayContext.AutoRefreshToggle.IsOn != true)
                return;

            _adminJobsRefreshTimer = DispatcherQueue.CreateTimer();
            _adminJobsRefreshTimer.Interval = TimeSpan.FromSeconds(8);
            _adminJobsRefreshTimer.Tick += async (_, __) =>
            {
                var context = _adminJobsOverlayContext;
                if (context is null || context.IsRefreshing)
                    return;
                await RefreshAdminJobsOverlayAsync(context, CancellationToken.None).ConfigureAwait(true);
            };
            _adminJobsRefreshTimer.Start();
        }
        catch
        {
        }
    }

    private void CloseAdminJobsOverlay() => CloseAdminJobsWindow();

    private void CloseAdminJobsWindow()
    {
        try
        {
            _adminJobsRefreshTimer?.Stop();
            _adminJobsRefreshTimer = null;
        }
        catch
        {
        }

        if (_adminJobsWindow is not null)
        {
            try { _adminJobsWindow.Close(); } catch { }
        }

        _adminJobsWindow = null;
        _adminJobsOverlayContext = null;
    }

    private async Task RefreshAdminJobsOverlayAsync(AdminJobsOverlayContext context, CancellationToken ct)
    {
        if (context.IsRefreshing)
            return;

        context.IsRefreshing = true;
        UpdateAdminJobsRefreshControls(context);
        var previousSummary = context.SummaryText.Text;
        context.SummaryText.Text = ClientUiText.Get("admin.jobs.loading", UiLang);

        try
        {
            var selected = context.TypeCombo.SelectedItem as ComboBoxItem;
            var type = (selected?.Tag as string) ?? string.Empty;
            var root = await _api.AdminJobsListAsync(string.IsNullOrWhiteSpace(type) ? null : type, 250, 0, ct).ConfigureAwait(true);
            var items = ParseAdminJobs(root)
                .OrderByDescending(item => GetTrackedJobStatusRank(item.Status))
                .ThenByDescending(item => item.CreatedAt ?? DateTimeOffset.MinValue)
                .ToList();

            context.Items = items;
            context.SelectedTerminalJobIds.IntersectWith(items.Where(x => x.IsTerminal).Select(x => x.JobId));
            RenderAdminJobsOverlay(context);
        }
        catch (Exception ex)
        {
            if (context.Items.Count > 0)
            {
                context.SummaryText.Text = previousSummary + " • " + ClientUiText.Get("admin.jobs.refresh_failed_soft", UiLang) + ex.Message;
                context.AutoRefreshToggle.IsOn = false;
                try { _adminJobsRefreshTimer?.Stop(); } catch { }
            }
            else
            {
                context.GroupsHost.Children.Clear();
                context.GroupsHost.Children.Add(new TextBlock
                {
                    Text = ClientUiText.Get("admin.jobs.refresh_failed", UiLang) + ex.Message,
                    TextWrapping = TextWrapping.WrapWholeWords,
                    Foreground = UseLightPalette() ? UiBrush(0xB4, 0x23, 0x18) : UiBrush(0xFF, 0x8A, 0x80)
                });
                context.SummaryText.Text = ClientUiText.Get("admin.jobs.refresh_failed", UiLang) + ex.Message;
                UpdateAdminJobsSelectionState(context, Array.Empty<AdminJobListItem>());
            }
        }
        finally
        {
            context.IsRefreshing = false;
            UpdateAdminJobsRefreshControls(context);
        }
    }

    private void RenderAdminJobsOverlay(AdminJobsOverlayContext context)
    {
        var visibleItems = ApplyAdminJobsFilters(context);
        var renderSignature = BuildAdminJobsRenderSignature(visibleItems);
        var shouldRebuildGroups = !string.Equals(context.LastVisibleRenderSignature, renderSignature, StringComparison.Ordinal);
        RenderAdminJobsMetrics(context, context.Items, visibleItems);
        if (shouldRebuildGroups)
        {
            RenderAdminJobsGroups(context, visibleItems);
            context.LastVisibleRenderSignature = renderSignature;
        }
        RenderAdminJobsDetails(context);
        UpdateAdminJobsSelectionState(context, visibleItems);
        context.SummaryText.Text = ClientUiText.Format("admin.jobs.summary", UiLang, visibleItems.Count, visibleItems.Count(x => !x.IsTerminal && !x.IsPaused), context.Items.Count);
    }

    private List<AdminJobListItem> ApplyAdminJobsFilters(AdminJobsOverlayContext context)
    {
        var term = (context.SearchBox.Text ?? string.Empty).Trim();
        var selectedStatus = ((context.StatusCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "active").Trim().ToLowerInvariant();

        IEnumerable<AdminJobListItem> items = context.Items;
        if (!string.IsNullOrWhiteSpace(term))
        {
            items = items.Where(item =>
                item.JobId.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(item.DocPath) && item.DocPath.Contains(term, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(item.LastError) && item.LastError.Contains(term, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(item.JobType) && item.JobType.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        items = selectedStatus switch
        {
            "active" => items.Where(item => !item.IsTerminal && !item.IsPaused),
            "queued" => items.Where(item => item.IsQueued),
            "running" => items.Where(item => item.IsRunning),
            "cancel_requested" => items.Where(item => string.Equals(NormalizeTrackedJobStatus(item.Status), "cancel_requested", StringComparison.OrdinalIgnoreCase)),
            "paused" => items.Where(item => item.IsPaused),
            "done" => items.Where(item => string.Equals(NormalizeTrackedJobStatus(item.Status), "done", StringComparison.OrdinalIgnoreCase)),
            "failed" => items.Where(item => string.Equals(NormalizeTrackedJobStatus(item.Status), "failed", StringComparison.OrdinalIgnoreCase)),
            "canceled" => items.Where(item => string.Equals(NormalizeTrackedJobStatus(item.Status), "canceled", StringComparison.OrdinalIgnoreCase)),
            _ => items
        };

        return items.OrderByDescending(item => item.IsRunning || item.IsQueued)
            .ThenByDescending(item => item.CreatedAt ?? DateTimeOffset.MinValue)
            .ToList();
    }

    private void RenderAdminJobsMetrics(AdminJobsOverlayContext context, IReadOnlyList<AdminJobListItem> allItems, IReadOnlyList<AdminJobListItem> visibleItems)
    {
        context.MetricsHost.Children.Clear();

        var grid = new Grid { ColumnSpacing = 10 };
        for (var i = 0; i < 6; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var metrics = new (string Title, int Value, string Accent)[]
        {
            (ClientUiText.Get("admin.jobs.metric.queued", UiLang), allItems.Count(x => x.IsQueued), "queued"),
            (ClientUiText.Get("admin.jobs.metric.running", UiLang), allItems.Count(x => x.IsRunning), "running"),
            (ClientUiText.Get("admin.jobs.metric.paused", UiLang), allItems.Count(x => x.IsPaused), "paused"),
            (ClientUiText.Get("admin.jobs.metric.failed", UiLang), allItems.Count(x => x.IsFailed), "failed"),
            (ClientUiText.Get("admin.jobs.metric.canceled", UiLang), allItems.Count(x => x.IsCanceled), "canceled"),
            (ClientUiText.Get("admin.jobs.metric.done", UiLang), allItems.Count(x => string.Equals(NormalizeTrackedJobStatus(x.Status), "done", StringComparison.OrdinalIgnoreCase)), "done")
        };

        for (var i = 0; i < metrics.Length; i++)
        {
            var card = BuildAdminMetricCard(metrics[i].Title, metrics[i].Value, metrics[i].Accent);
            Grid.SetColumn(card, i);
            grid.Children.Add(card);
        }

        context.MetricsHost.Children.Add(grid);
        context.MetricsHost.Children.Add(new TextBlock
        {
            Text = ClientUiText.Format("admin.jobs.visible_summary", UiLang, visibleItems.Count, allItems.Count),
            Foreground = UseLightPalette() ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE),
            TextWrapping = TextWrapping.WrapWholeWords
        });
    }

    private static string BuildAdminJobsRenderSignature(IReadOnlyList<AdminJobListItem> visibleItems)
    {
        var sb = new System.Text.StringBuilder(visibleItems.Count * 64);
        foreach (var item in visibleItems)
        {
            sb.Append(item.JobId).Append('|')
              .Append(item.Status).Append('|')
              .Append(item.ProgressPhase).Append('|')
              .Append(item.ProgressPercent?.ToString(CultureInfo.InvariantCulture) ?? "-").Append('|')
              .Append(item.ProgressCurrent?.ToString(CultureInfo.InvariantCulture) ?? "-").Append('|')
              .Append(item.ProgressTotal?.ToString(CultureInfo.InvariantCulture) ?? "-").Append('|')
              .Append(item.LastError).Append(';');
        }

        return sb.ToString();
    }

    private Border BuildAdminMetricCard(string title, int value, string accentStatus)
    {
        var light = UseLightPalette();
        return new Border
        {
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14, 12, 14, 12),
            Background = light ? UiBrush(0xF7, 0xFA, 0xFD) : UiBrush(0x11, 0x16, 0x1E),
            BorderBrush = GetAdminJobStatusBorder(accentStatus, light),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        Foreground = light ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE),
                        TextWrapping = TextWrapping.WrapWholeWords,
                        FontSize = 12,
                        CharacterSpacing = 20
                    },
                    new TextBlock
                    {
                        Text = value.ToString(CultureInfo.InvariantCulture),
                        FontSize = 24,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = GetAdminJobStatusForeground(accentStatus, light)
                    }
                }
            }
        };
    }

    private void RenderAdminJobsGroups(AdminJobsOverlayContext context, IReadOnlyList<AdminJobListItem> visibleItems)
    {
        context.GroupsHost.Children.Clear();
        if (visibleItems.Count == 0)
        {
            context.GroupsHost.Children.Add(BuildDialogInfoBanner(ClientUiText.Get("admin.jobs.empty", UiLang)));
            return;
        }

        var paused = visibleItems.Where(item => item.IsPaused).ToList();
        var active = visibleItems.Where(item => !item.IsTerminal && !item.IsPaused).ToList();
        var failed = visibleItems.Where(item => item.IsFailed).ToList();
        var canceled = visibleItems.Where(item => item.IsCanceled).ToList();
        var done = visibleItems.Where(item => item.IsTerminal && !item.IsFailed && !item.IsCanceled).ToList();

        if (active.Count > 0)
            context.GroupsHost.Children.Add(BuildAdminJobsGroupSection(ClientUiText.Get("admin.jobs.group.active", UiLang), active, context));
        if (paused.Count > 0)
            context.GroupsHost.Children.Add(BuildAdminJobsGroupSection(ClientUiText.Get("admin.jobs.group.paused", UiLang), paused, context));
        if (canceled.Count > 0)
            context.GroupsHost.Children.Add(BuildAdminJobsGroupSection(ClientUiText.Get("admin.jobs.group.canceled", UiLang), canceled, context));
        if (failed.Count > 0)
            context.GroupsHost.Children.Add(BuildAdminJobsGroupSection(ClientUiText.Get("admin.jobs.group.failed", UiLang), failed, context));
        if (done.Count > 0)
        {
            var take = Math.Max(50, context.HistoryTake);
            var shown = done.Take(take).ToList();
            context.GroupsHost.Children.Add(BuildAdminJobsGroupSection(ClientUiText.Get("admin.jobs.group.history", UiLang), shown, context));
            if (done.Count > shown.Count)
            {
                var loadMore = BuildDialogInlineButton($"{ClientUiText.Get("admin.jobs.show_more", UiLang)} (+50)");
                loadMore.HorizontalAlignment = HorizontalAlignment.Left;
                loadMore.Click += (_, __) =>
                {
                    context.HistoryTake += 50;
                    RenderAdminJobsOverlay(context);
                };
                context.GroupsHost.Children.Add(loadMore);
            }
        }
    }

    private UIElement BuildAdminJobsGroupSection(string title, IReadOnlyList<AdminJobListItem> items, AdminJobsOverlayContext context)
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(new TextBlock
        {
            Text = ClientUiText.Format("admin.jobs.group.title", UiLang, title, items.Count),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = UseLightPalette() ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xF5, 0xF7, 0xFB)
        });

        foreach (var item in items)
        {
            try
            {
                stack.Children.Add(BuildAdminJobCard(item, context));
            }
            catch (Exception ex)
            {
                ClientLog.Exception($"AdminJobs.BuildCard[{item.JobId}]", ex);
                stack.Children.Add(BuildDialogInfoBanner(ClientUiText.Get("admin.jobs.card_error", UiLang)));
            }
        }

        return stack;
    }

    private UIElement BuildAdminJobCard(AdminJobListItem item, AdminJobsOverlayContext context)
    {
        var light = UseLightPalette();
        var status = NormalizeTrackedJobStatus(item.Status);
        var progressPercent = item.ProgressPercent.HasValue ? Math.Clamp(item.ProgressPercent.Value, 0, 100) : (int?)null;
        var progressValue = progressPercent.HasValue ? progressPercent.Value : (status == "done" ? 100d : 0d);
        var progressText = BuildAdminJobProgressLine(item);

        var isSelected = string.Equals(context.SelectedJobId, item.JobId, StringComparison.OrdinalIgnoreCase);
        var outer = new Border
        {
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(14),
            Background = isSelected
                ? (light ? UiBrush(0xF1, 0xF6, 0xFD) : UiBrush(0x14, 0x1D, 0x28))
                : (light ? UiBrush(0xF7, 0xFA, 0xFD) : UiBrush(0x11, 0x16, 0x1E)),
            BorderBrush = isSelected ? GetAdminJobStatusBorder(status, light) : (light ? UiBrush(0xCC, 0xD6, 0xE4) : UiBrush(0x2E, 0x38, 0x45)),
            BorderThickness = new Thickness(isSelected ? 2 : 1)
        };

        var stack = new StackPanel { Spacing = 10 };

        var headerGrid = new Grid { ColumnSpacing = 12 };
        if (item.IsTerminal)
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var currentColumn = 0;
        if (item.IsTerminal)
        {
            var selector = new CheckBox
            {
                IsChecked = context.SelectedTerminalJobIds.Contains(item.JobId),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 0, 0)
            };
            ToolTipService.SetToolTip(selector, ClientUiText.Get("admin.jobs.select", UiLang));
            selector.Checked += (_, __) =>
            {
                context.SelectedTerminalJobIds.Add(item.JobId);
                UpdateAdminJobsSelectionState(context, ApplyAdminJobsFilters(context));
            };
            selector.Unchecked += (_, __) =>
            {
                context.SelectedTerminalJobIds.Remove(item.JobId);
                UpdateAdminJobsSelectionState(context, ApplyAdminJobsFilters(context));
            };
            Grid.SetColumn(selector, currentColumn++);
            headerGrid.Children.Add(selector);
        }

        var titleStack = new StackPanel { Spacing = 3 };
        titleStack.Children.Add(new TextBlock
        {
            Text = item.DisplayTitle,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.WrapWholeWords,
            Foreground = light ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xF7, 0xFA, 0xFE)
        });
        if (!string.IsNullOrWhiteSpace(item.DisplaySubtitle))
        {
            titleStack.Children.Add(new TextBlock
            {
                Text = item.DisplaySubtitle,
                FontSize = 12,
                TextWrapping = TextWrapping.WrapWholeWords,
                Foreground = light ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE)
            });
        }
        Grid.SetColumn(titleStack, currentColumn++);
        headerGrid.Children.Add(titleStack);

        var statusBadge = BuildAdminJobStatusBadge(status);
        Grid.SetColumn(statusBadge, currentColumn);
        headerGrid.Children.Add(statusBadge);
        stack.Children.Add(headerGrid);

        var progressGrid = new Grid { ColumnSpacing = 12 };
        progressGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        progressGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = progressValue,
            IsIndeterminate = !progressPercent.HasValue && (item.IsRunning || item.IsQueued),
            Height = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = GetAdminJobStatusForeground(status, light),
            Background = GetAdminJobStatusBackground(status, light),
            Transitions = null
        };
        Grid.SetColumn(progressBar, 0);
        progressGrid.Children.Add(progressBar);
        var progressLabel = new TextBlock
        {
            Text = progressText,
            FontSize = 12,
            Foreground = light ? UiBrush(0x21, 0x2D, 0x3D) : UiBrush(0xE3, 0xEA, 0xF5),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(progressLabel, 1);
        progressGrid.Children.Add(progressLabel);
        stack.Children.Add(progressGrid);

        stack.Children.Add(new TextBlock
        {
            Text = BuildAdminJobDatesLine(item),
            FontSize = 12,
            TextWrapping = TextWrapping.WrapWholeWords,
            Foreground = light ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE)
        });

        var docStateLine = BuildAdminJobDocumentStateLine(item);
        if (!string.IsNullOrWhiteSpace(docStateLine))
        {
            stack.Children.Add(new TextBlock
            {
                Text = docStateLine,
                FontSize = 12,
                TextWrapping = TextWrapping.WrapWholeWords,
                Foreground = light ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE)
            });
        }

        if (!string.IsNullOrWhiteSpace(item.LastError))
        {
            stack.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 10, 12, 10),
                Background = light ? UiBrush(0xFD, 0xEF, 0xEE) : UiBrush(0x2A, 0x14, 0x16),
                BorderBrush = light ? UiBrush(0xF1, 0xC7, 0xC3) : UiBrush(0x6A, 0x2C, 0x31),
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = BuildAdminJobErrorText(item),
                    TextWrapping = TextWrapping.WrapWholeWords,
                    Foreground = light ? UiBrush(0xB4, 0x23, 0x18) : UiBrush(0xFF, 0x8A, 0x80),
                    FontSize = 12
                }
            });
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };

        var copyButton = BuildDialogInlineButton(ClientUiText.Get("admin.jobs.copy_id", UiLang));
        copyButton.Click += (_, __) =>
        {
            try
            {
                var dp = new DataPackage();
                dp.SetText(item.JobId);
                Clipboard.SetContent(dp);
                Status(ClientUiText.Get("admin.jobs.id_copied", UiLang));
            }
            catch
            {
                Status(ClientUiText.Get("admin.jobs.id_copy_unavailable", UiLang));
            }
        };
        actions.Children.Add(copyButton);


        if (item.DocumentAutoIngestPaused == true)
        {
            var resumeButton = BuildDialogInlineButton(ClientUiText.Get("admin.jobs.resume", UiLang));
            resumeButton.Click += async (_, __) =>
            {
                try
                {
                    resumeButton.IsEnabled = false;
                    var response = await _api.AdminJobsResumeAsync(item.JobId, CancellationToken.None).ConfigureAwait(true);
                    var resumed = (TryGetBool(response, "resumed") ?? false);
                    var reason = (TryGetString(response, "reason") ?? string.Empty).Trim().ToLowerInvariant();
                    if (resumed)
                        Status(ClientUiText.Get("admin.jobs.resume_done", UiLang));
                    else if (reason == "not_paused")
                        Status(ClientUiText.Get("admin.jobs.resume_not_paused", UiLang));
                    else
                        Status(ClientUiText.Get("admin.jobs.resume_failed", UiLang) + reason);

                    await RefreshAdminJobsOverlayAsync(context, CancellationToken.None).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    Status(ClientUiText.Get("admin.jobs.resume_failed", UiLang) + ex.Message);
                }
                finally
                {
                    resumeButton.IsEnabled = true;
                }
            };
            actions.Children.Add(resumeButton);
        }

        if (!item.IsTerminal)
        {
            var cancelButton = BuildDialogInlineButton(ClientUiText.Get("admin.jobs.cancel", UiLang), destructive: true);
            cancelButton.Click += async (_, __) =>
            {
                try
                {
                    cancelButton.IsEnabled = false;
                    var response = await _api.AdminJobsCancelAsync(item.JobId, CancellationToken.None).ConfigureAwait(true);
                    await RefreshAdminJobsOverlayAsync(context, CancellationToken.None).ConfigureAwait(true);

                    var result = (TryGetString(response, "result") ?? string.Empty).Trim().ToLowerInvariant();
                    var status = NormalizeTrackedJobStatus(TryGetString(response, "status") ?? string.Empty);
                    var cancelRequested = (TryGetInt(response, "runningCancelRequested") ?? 0) > 0 || status == "cancel_requested" || result == "cancel_requested";
                    var canceled = TryGetPropertyIgnoreCase(response, "canceled", out var canceledEl)
                        && canceledEl.ValueKind is JsonValueKind.True or JsonValueKind.False
                        && canceledEl.GetBoolean();

                    if (cancelRequested)
                        Status(ClientUiText.Get("admin.jobs.cancel_requested", UiLang));
                    else if (status == "paused" || result == "paused")
                        Status(ClientUiText.Get("admin.jobs.cancel_done", UiLang));
                    else if (status is "canceled" or "cancelled" || result == "canceled")
                        Status(ClientUiText.Get("admin.jobs.cancel_done", UiLang));
                    else if (status is "done" or "failed" || result == "already_finished")
                        Status(ClientUiText.Get("admin.jobs.cancel_already_finished", UiLang));
                    else if (canceled)
                        Status(ClientUiText.Get("admin.jobs.cancel_done", UiLang));
                    else
                        Status(ClientUiText.Get("admin.jobs.cancel_nothing", UiLang));
                }
                catch (Exception ex)
                {
                    Status(ClientUiText.Get("admin.jobs.refresh_failed", UiLang) + ex.Message);
                }
                finally
                {
                    cancelButton.IsEnabled = true;
                }
            };
            actions.Children.Add(cancelButton);
        }

        stack.Children.Add(actions);


        outer.Child = stack;
        outer.Tapped += (_, args) =>
        {
            if (IsAdminJobsCardInteractiveSource(args.OriginalSource as DependencyObject, outer))
                return;

            context.SelectedJobId = string.Equals(context.SelectedJobId, item.JobId, StringComparison.OrdinalIgnoreCase)
                ? null
                : item.JobId;
            RenderAdminJobsOverlay(context);
        };
        return outer;
    }

    private void RenderAdminJobsDetails(AdminJobsOverlayContext context)
    {
        try
        {
            context.DetailsHost.Children.Clear();

            var header = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 8) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var headerTitle = new TextBlock
            {
                Text = ClientUiText.Get("admin.jobs.detail.title", UiLang),
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = UseLightPalette() ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xF5, 0xF7, 0xFB)
            };
            var closeButton = BuildDialogChromeIconButton(ClientUiText.Get("dialog.close", UiLang));
            closeButton.Click += (_, __) =>
            {
                context.SelectedJobId = null;
                RenderAdminJobsDetails(context);
            };
            Grid.SetColumn(headerTitle, 0);
            Grid.SetColumn(closeButton, 1);
            header.Children.Add(headerTitle);
            header.Children.Add(closeButton);
            context.DetailsHost.Children.Add(header);

            var selected = context.Items.FirstOrDefault(item => string.Equals(item.JobId, context.SelectedJobId, StringComparison.OrdinalIgnoreCase));
            if (selected is null)
            {
                context.DetailsColumn.Width = new GridLength(0);
                context.DetailsCard.Visibility = Visibility.Collapsed;
                return;
            }

            context.DetailsColumn.Width = new GridLength(380);
            context.DetailsCard.Visibility = Visibility.Visible;

            context.DetailsHost.Children.Add(new TextBlock
            {
                Text = selected.DisplayTitle,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.WrapWholeWords,
                Foreground = UseLightPalette() ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xF5, 0xF7, 0xFB)
            });
            if (!string.IsNullOrWhiteSpace(selected.DisplaySubtitle))
            {
                context.DetailsHost.Children.Add(new TextBlock
                {
                    Text = selected.DisplaySubtitle,
                    FontSize = 12,
                    TextWrapping = TextWrapping.WrapWholeWords,
                    Foreground = UseLightPalette() ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE)
                });
            }
            context.DetailsHost.Children.Add(BuildAdminJobDetailsPanel(selected));
            if (!string.IsNullOrWhiteSpace(selected.LastError))
                context.DetailsHost.Children.Add(BuildDialogInfoBanner(BuildAdminJobErrorText(selected)));
        }
        catch (Exception ex)
        {
            ClientLog.Exception("AdminJobs.RenderDetails", ex);
            context.DetailsHost.Children.Clear();
            context.DetailsColumn.Width = new GridLength(380);
            context.DetailsCard.Visibility = Visibility.Visible;
            context.DetailsHost.Children.Add(BuildDialogInfoBanner(ClientUiText.Get("admin.jobs.detail_error", UiLang)));
        }
    }

    private Border BuildAdminJobDetailsPanel(AdminJobListItem item)
    {
        var light = UseLightPalette();
        var facts = new StackPanel { Spacing = 6 };
        AddFact(facts, ClientUiText.Get("admin.jobs.details.job_id", UiLang), item.JobId);
        AddFact(facts, ClientUiText.Get("admin.jobs.details.type", UiLang), TranslateAdminJobFamily(item.Type));
        AddFact(facts, ClientUiText.Get("admin.jobs.details.job_type", UiLang), TranslateAdminJobType(item.JobType));
        AddFact(facts, ClientUiText.Get("admin.jobs.details.status", UiLang), ClientUiText.Get("admin.jobs.status." + NormalizeTrackedJobStatus(item.Status), UiLang));
        AddFact(facts, ClientUiText.Get("admin.jobs.details.doc_id", UiLang), item.DocId);
        AddFact(facts, ClientUiText.Get("admin.jobs.details.doc_path", UiLang), item.DocPath);
        AddFact(facts, ClientUiText.Get("admin.jobs.details.phase", UiLang), TranslateAdminJobPhase(item.ProgressPhase));
        AddFact(facts, ClientUiText.Get("admin.jobs.details.progress", UiLang), BuildAdminJobProgressLine(item));
        AddFact(facts, ClientUiText.Get("admin.jobs.details.cancel_requested_flag", UiLang), item.CancelRequested.HasValue ? (item.CancelRequested.Value ? ClientUiText.Get("admin.jobs.value.yes", UiLang) : ClientUiText.Get("admin.jobs.value.no", UiLang)) : null);
        AddFact(facts, ClientUiText.Get("admin.jobs.details.enqueue_source", UiLang), TranslateAdminJobEnqueueSource(item.EnqueueSource));
        AddFact(facts, ClientUiText.Get("admin.jobs.details.doc_status", UiLang), TranslateAdminJobDocumentStatus(item.DocumentStatus));
        AddFact(facts, ClientUiText.Get("admin.jobs.details.doc_versions", UiLang), BuildAdminJobDocumentVersionsLine(item));
        AddFact(facts, ClientUiText.Get("admin.jobs.details.auto_pause", UiLang), BuildAdminJobAutoPauseLine(item));
        AddFact(facts, ClientUiText.Get("admin.jobs.details.created", UiLang), item.CreatedAt?.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture));
        AddFact(facts, ClientUiText.Get("admin.jobs.details.started", UiLang), item.StartedAt?.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture));
        AddFact(facts, ClientUiText.Get("admin.jobs.details.finished", UiLang), item.FinishedAt?.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture));

        return new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12),
            Background = light ? UiBrush(0xF1, 0xF5, 0xFA) : UiBrush(0x0F, 0x13, 0x19),
            BorderBrush = light ? UiBrush(0xD5, 0xE0, 0xEB) : UiBrush(0x2A, 0x33, 0x40),
            BorderThickness = new Thickness(1),
            Child = facts
        };
    }

    private void AddFact(Panel host, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold,
            Foreground = UseLightPalette() ? UiBrush(0x5D, 0x6E, 0x82) : UiBrush(0xA8, 0xB5, 0xC7)
        };
        var valueBlock = new TextBlock
        {
            Text = value,
            TextWrapping = TextWrapping.WrapWholeWords,
            Foreground = UseLightPalette() ? UiBrush(0x19, 0x24, 0x33) : UiBrush(0xF2, 0xF5, 0xFA)
        };
        Grid.SetColumn(labelBlock, 0);
        Grid.SetColumn(valueBlock, 1);
        row.Children.Add(labelBlock);
        row.Children.Add(valueBlock);
        host.Children.Add(row);
    }

    private void UpdateAdminJobsSelectionState(AdminJobsOverlayContext context, IReadOnlyList<AdminJobListItem> visibleItems)
    {
        var selectableVisibleIds = visibleItems.Where(item => item.IsTerminal).Select(item => item.JobId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedVisibleCount = context.SelectedTerminalJobIds.Count(id => selectableVisibleIds.Contains(id));
        context.DeleteSelectionButton.IsEnabled = selectedVisibleCount > 0;
        context.SelectionText.Text = selectedVisibleCount > 0
            ? ClientUiText.Format("admin.jobs.selection.count", UiLang, selectedVisibleCount)
            : ClientUiText.Get("admin.jobs.selection.none", UiLang);
    }

    private async Task DeleteSelectedAdminJobsAsync(AdminJobsOverlayContext context)
    {
        var visibleIds = context.Items
            .Where(item => item.IsTerminal && context.SelectedTerminalJobIds.Contains(item.JobId))
            .Select(item => item.JobId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (visibleIds.Length == 0)
        {
            Status(ClientUiText.Get("admin.jobs.delete_selection_none", UiLang));
            return;
        }

        await DeleteAdminJobsAsync(context, visibleIds).ConfigureAwait(true);
    }

    private async Task DeleteAdminJobsByPredicateAsync(AdminJobsOverlayContext context, Func<AdminJobListItem, bool> predicate)
    {
        var visibleIds = ApplyAdminJobsFilters(context)
            .Where(predicate)
            .Select(item => item.JobId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (visibleIds.Length == 0)
        {
            Status(ClientUiText.Get("admin.jobs.delete_nothing", UiLang));
            return;
        }

        await DeleteAdminJobsAsync(context, visibleIds).ConfigureAwait(true);
    }

    private async Task DeleteAdminJobsAsync(AdminJobsOverlayContext context, IReadOnlyList<string> jobIds)
    {
        try
        {
            context.DeleteSelectionButton.IsEnabled = false;
            context.PurgeButton.IsEnabled = false;

            var response = await _api.AdminJobsDeleteHistoryAsync(jobIds, CancellationToken.None).ConfigureAwait(true);
            var deleted = TryGetInt(response, "deleted") ?? TryGetInt(response, "Deleted") ?? 0;
            var responseDeletedIds = ReadStringArray(response, "deletedIds");
            var deletedIds = new HashSet<string>(
                responseDeletedIds.Count > 0 ? responseDeletedIds : jobIds,
                StringComparer.OrdinalIgnoreCase);

            if (deletedIds.Count > 0 && deleted > 0)
            {
                context.Items = context.Items.Where(item => !deletedIds.Contains(item.JobId)).ToList();
                foreach (var id in deletedIds)
                    context.SelectedTerminalJobIds.Remove(id);
                if (!string.IsNullOrWhiteSpace(context.SelectedJobId) && deletedIds.Contains(context.SelectedJobId))
                    context.SelectedJobId = null;

                RenderAdminJobsOverlay(context);
                Status(ClientUiText.Format("admin.jobs.delete_done", UiLang, deleted));
            }
            else
            {
                Status(ClientUiText.Get("admin.jobs.delete_nothing", UiLang));
            }
        }
        catch (Exception ex)
        {
            ClientLog.Exception("AdminJobs.DeleteHistory", ex);
            Status(ClientUiText.Get("admin.jobs.delete_failed", UiLang) + ex.Message);
        }
        finally
        {
            context.PurgeButton.IsEnabled = true;
            UpdateAdminJobsSelectionState(context, ApplyAdminJobsFilters(context));
        }
    }

    private Button BuildDialogInlineButton(string text, bool destructive = false)
    {
        var button = BuildDialogFooterButton(text, destructive: destructive);
        button.MinWidth = 0;
        button.Padding = new Thickness(12, 8, 12, 8);
        button.CornerRadius = new CornerRadius(12);
        return button;
    }

    private List<AdminJobListItem> ParseAdminJobs(JsonElement root)
    {
        var items = new List<AdminJobListItem>();
        JsonElement source = root;
        if (TryGetPropertyIgnoreCase(root, "items", out var array) && array.ValueKind == JsonValueKind.Array)
            source = array;

        if (source.ValueKind != JsonValueKind.Array)
            return items;

        foreach (var item in source.EnumerateArray())
        {
            try
            {
                var status = NormalizeTrackedJobStatus(TryGetString(item, "Status") ?? TryGetString(item, "status"));
                var jobId = TryGetString(item, "JobId") ?? TryGetString(item, "jobId") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(jobId))
                    continue;

                items.Add(new AdminJobListItem
                {
                    JobId = jobId,
                    Type = TryGetString(item, "Type") ?? TryGetString(item, "type") ?? string.Empty,
                    JobType = TryGetString(item, "JobType") ?? TryGetString(item, "jobType") ?? string.Empty,
                    Status = status,
                    DocId = TryGetString(item, "DocId") ?? TryGetString(item, "docId"),
                    DocPath = TryGetString(item, "DocPath") ?? TryGetString(item, "docPath"),
                    Level = TryGetString(item, "Level") ?? TryGetString(item, "level"),
                    LastError = TryGetString(item, "LastError") ?? TryGetString(item, "lastError"),
                    CreatedAt = TryGetDateTimeOffset(item, "CreatedAt") ?? TryGetDateTimeOffset(item, "createdAt"),
                    StartedAt = TryGetDateTimeOffset(item, "StartedAt") ?? TryGetDateTimeOffset(item, "startedAt"),
                    FinishedAt = TryGetDateTimeOffset(item, "FinishedAt") ?? TryGetDateTimeOffset(item, "finishedAt"),
                    ProgressPhase = TryGetString(item, "ProgressPhase") ?? TryGetString(item, "progressPhase"),
                    ProgressCurrent = TryGetInt(item, "ProgressCurrent") ?? TryGetInt(item, "progressCurrent"),
                    ProgressTotal = TryGetInt(item, "ProgressTotal") ?? TryGetInt(item, "progressTotal"),
                    ProgressPercent = TryGetInt(item, "ProgressPercent") ?? TryGetInt(item, "progressPercent"),
                    CancelRequested = TryGetBool(item, "CancelRequested") ?? TryGetBool(item, "cancelRequested"),
                    EnqueueSource = TryGetString(item, "EnqueueSource") ?? TryGetString(item, "enqueueSource"),
                    DocumentStatus = TryGetString(item, "DocumentStatus") ?? TryGetString(item, "documentStatus"),
                    DocumentIngestionVersion = TryGetInt(item, "DocumentIngestionVersion") ?? TryGetInt(item, "documentIngestionVersion"),
                    DocumentIndexedVersion = TryGetInt(item, "DocumentIndexedVersion") ?? TryGetInt(item, "documentIndexedVersion"),
                    DocumentAutoIngestPaused = TryGetBool(item, "DocumentAutoIngestPaused") ?? TryGetBool(item, "documentAutoIngestPaused"),
                    DocumentAutoIngestPauseReason = TryGetString(item, "DocumentAutoIngestPauseReason") ?? TryGetString(item, "documentAutoIngestPauseReason")
                });
            }
            catch (Exception ex)
            {
                ClientLog.Exception("AdminJobs.ParseItem", ex);
            }
        }

        return items;
    }

    private static bool? TryGetBool(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private string BuildAdminJobErrorText(AdminJobListItem item)
    {
        var raw = item.LastError?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        return raw.ToLowerInvariant() switch
        {
            "timeout" => ClientUiText.Get("admin.jobs.error.timeout", UiLang),
            "timeout_or_canceled" => ClientUiText.Get("admin.jobs.error.timeout", UiLang),
            "canceled_by_admin" or "canceled_by_admin_token" or "canceled_by_admin_document" or "canceled_by_worker" => ClientUiText.Get("admin.jobs.error.canceled_by_admin", UiLang),
            "canceled_at_commit" => ClientUiText.Get("admin.jobs.error.canceled_after_commit", UiLang),
            "superseded_version" or "superseded_at_commit" => ClientUiText.Get("admin.jobs.error.superseded", UiLang),
            _ => raw
        };
    }

    private string BuildAdminJobProgressLine(AdminJobListItem item)
    {
        var status = NormalizeTrackedJobStatus(item.Status);
        if (status == "queued" || status == "paused")
            return ClientUiText.Get("admin.jobs.status." + status, UiLang);

        var bits = new List<string>();
        var phase = TranslateAdminJobPhase(item.ProgressPhase);
        if (!string.IsNullOrWhiteSpace(phase))
            bits.Add(phase!);
        if (item.ProgressPercent.HasValue)
            bits.Add($"{Math.Clamp(item.ProgressPercent.Value, 0, 100)}%");
        if (item.ProgressCurrent.HasValue || item.ProgressTotal.HasValue)
            bits.Add($"{item.ProgressCurrent?.ToString(CultureInfo.InvariantCulture) ?? "?"}/{item.ProgressTotal?.ToString(CultureInfo.InvariantCulture) ?? "?"}");
        if (bits.Count == 0)
            bits.Add(ClientUiText.Get("admin.jobs.status." + status, UiLang));
        return string.Join(" • ", bits);
    }

    private string BuildAdminJobDatesLine(AdminJobListItem item)
    {
        var bits = new List<string>();
        if (item.CreatedAt.HasValue)
            bits.Add(ClientUiText.Format("admin.jobs.date.created", UiLang, item.CreatedAt.Value.LocalDateTime.ToString("dd.MM HH:mm:ss", CultureInfo.InvariantCulture)));
        if (item.StartedAt.HasValue)
            bits.Add(ClientUiText.Format("admin.jobs.date.started", UiLang, item.StartedAt.Value.LocalDateTime.ToString("dd.MM HH:mm:ss", CultureInfo.InvariantCulture)));
        if (item.FinishedAt.HasValue)
            bits.Add(ClientUiText.Format("admin.jobs.date.finished", UiLang, item.FinishedAt.Value.LocalDateTime.ToString("dd.MM HH:mm:ss", CultureInfo.InvariantCulture)));
        return bits.Count == 0 ? ClientUiText.Get("admin.jobs.date.none", UiLang) : string.Join(" • ", bits);
    }

    private string? BuildAdminJobDocumentStateLine(AdminJobListItem item)
    {
        var bits = new List<string>();
        var documentStatus = TranslateAdminJobDocumentStatus(item.DocumentStatus);
        if (!string.IsNullOrWhiteSpace(documentStatus))
            bits.Add($"{ClientUiText.Get("admin.jobs.card.document_state", UiLang)} : {documentStatus}");

        var versions = BuildAdminJobDocumentVersionsLine(item);
        if (!string.IsNullOrWhiteSpace(versions))
            bits.Add(versions!);

        var pause = BuildAdminJobAutoPauseLine(item);
        if (!string.IsNullOrWhiteSpace(pause))
            bits.Add(pause!);

        return bits.Count == 0 ? null : string.Join(" • ", bits);
    }

    private string? BuildAdminJobDocumentVersionsLine(AdminJobListItem item)
    {
        if (!item.DocumentIngestionVersion.HasValue && !item.DocumentIndexedVersion.HasValue)
            return null;

        return ClientUiText.Format(
            "admin.jobs.card.document_versions",
            UiLang,
            item.DocumentIngestionVersion?.ToString(CultureInfo.InvariantCulture) ?? "?",
            item.DocumentIndexedVersion?.ToString(CultureInfo.InvariantCulture) ?? "?");
    }

    private string? BuildAdminJobAutoPauseLine(AdminJobListItem item)
    {
        if (!item.DocumentAutoIngestPaused.HasValue)
            return null;

        if (!item.DocumentAutoIngestPaused.Value)
            return ClientUiText.Get("admin.jobs.auto_pause.off", UiLang);

        var reason = TranslateAdminJobAutoPauseReason(item.DocumentAutoIngestPauseReason);
        if (!string.IsNullOrWhiteSpace(reason))
            return ClientUiText.Format("admin.jobs.auto_pause.on_reason", UiLang, reason);

        return ClientUiText.Get("admin.jobs.auto_pause.on", UiLang);
    }

    private string TranslateAdminJobFamily(string? type)
    {
        var normalized = (type ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "ingestion" => ClientUiText.Get("admin.jobs.type.ingestion", UiLang),
            "summary" => ClientUiText.Get("admin.jobs.type.summary", UiLang),
            _ => type ?? string.Empty
        };
    }

    private string TranslateAdminJobType(string? jobType)
    {
        var normalized = (jobType ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "upsert" => ClientUiText.Get("admin.jobs.job_type.upsert", UiLang),
            "delete" => ClientUiText.Get("admin.jobs.job_type.delete", UiLang),
            "summary" => ClientUiText.Get("admin.jobs.job_type.summary", UiLang),
            _ => jobType ?? string.Empty
        };
    }

    private string? TranslateAdminJobPhase(string? phase)
    {
        var normalized = (phase ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "extracting" => ClientUiText.Get("admin.jobs.phase.extracting", UiLang),
            "chunking" => ClientUiText.Get("admin.jobs.phase.chunking", UiLang),
            "embedding" => ClientUiText.Get("admin.jobs.phase.embedding", UiLang),
            "upserting" => ClientUiText.Get("admin.jobs.phase.upserting", UiLang),
            "deleting" => ClientUiText.Get("admin.jobs.phase.deleting", UiLang),
            "finalizing" or "finalize" => ClientUiText.Get("admin.jobs.phase.finalizing", UiLang),
            "completed" => ClientUiText.Get("admin.jobs.phase.completed", UiLang),
            _ => phase
        };
    }

    private string? TranslateAdminJobEnqueueSource(string? enqueueSource)
    {
        var normalized = (enqueueSource ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "admin" => ClientUiText.Get("admin.jobs.enqueue_source.admin", UiLang),
            "api" => ClientUiText.Get("admin.jobs.enqueue_source.api", UiLang),
            "scanner" => ClientUiText.Get("admin.jobs.enqueue_source.scanner", UiLang),
            "watcher" => ClientUiText.Get("admin.jobs.enqueue_source.watcher", UiLang),
            _ => enqueueSource
        };
    }

    private string? TranslateAdminJobDocumentStatus(string? documentStatus)
    {
        var normalized = (documentStatus ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "indexed" => ClientUiText.Get("admin.jobs.document_status.indexed", UiLang),
            "pending" => ClientUiText.Get("admin.jobs.document_status.pending", UiLang),
            "outdated" => ClientUiText.Get("admin.jobs.document_status.outdated", UiLang),
            "missing" => ClientUiText.Get("admin.jobs.document_status.missing", UiLang),
            "failed" => ClientUiText.Get("admin.jobs.document_status.failed", UiLang),
            "active" => ClientUiText.Get("admin.jobs.document_status.active", UiLang),
            "inactive" => ClientUiText.Get("admin.jobs.document_status.inactive", UiLang),
            _ => documentStatus
        };
    }

    private string? TranslateAdminJobAutoPauseReason(string? reason)
    {
        var normalized = (reason ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "admin_cancel" => ClientUiText.Get("admin.jobs.auto_pause.reason.admin_cancel", UiLang),
            "repeated_failures" => ClientUiText.Get("admin.jobs.auto_pause.reason.repeated_failures", UiLang),
            _ => reason
        };
    }

    private bool IsAdminJobsCardInteractiveSource(DependencyObject? source, DependencyObject cardRoot)
    {
        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, cardRoot))
                return false;
            if (current is ButtonBase)
                return true;
            current = global::Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private TextBlock BuildAdminJobStatusBadge(string status)
    {
        var light = UseLightPalette();
        var normalized = NormalizeTrackedJobStatus(status);
        return new TextBlock
        {
            Text = ClientUiText.Get("admin.jobs.status." + normalized, UiLang),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = GetAdminJobStatusForeground(normalized, light),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0)
        };
    }

    private Brush GetAdminJobStatusBackground(string status, bool light)
    {
        status = NormalizeTrackedJobStatus(status);
        return status switch
        {
            "done" => light ? UiBrush(0xEC, 0xF7, 0xF2) : UiBrush(0x12, 0x24, 0x1E),
            "failed" or "canceled" => light ? UiBrush(0xFD, 0xEF, 0xEE) : UiBrush(0x2A, 0x14, 0x16),
            "cancel_requested" => light ? UiBrush(0xF8, 0xF3, 0xE8) : UiBrush(0x2A, 0x22, 0x16),
            "paused" => light ? UiBrush(0xF3, 0xEE, 0xFB) : UiBrush(0x21, 0x18, 0x2B),
            "running" => light ? UiBrush(0xEC, 0xF3, 0xFB) : UiBrush(0x11, 0x23, 0x33),
            "queued" => light ? UiBrush(0xF8, 0xF3, 0xE8) : UiBrush(0x28, 0x20, 0x14),
            _ => light ? UiBrush(0xF4, 0xF7, 0xFB) : UiBrush(0x14, 0x1B, 0x24)
        };
    }

    private Brush GetAdminJobStatusBorder(string status, bool light)
    {
        status = NormalizeTrackedJobStatus(status);
        return status switch
        {
            "done" => light ? UiBrush(0xC6, 0xE5, 0xD7) : UiBrush(0x2A, 0x54, 0x43),
            "failed" or "canceled" => light ? UiBrush(0xF1, 0xC7, 0xC3) : UiBrush(0x6A, 0x2C, 0x31),
            "cancel_requested" => light ? UiBrush(0xE9, 0xD8, 0xBA) : UiBrush(0x6A, 0x4F, 0x24),
            "paused" => light ? UiBrush(0xD7, 0xC8, 0xEE) : UiBrush(0x57, 0x43, 0x72),
            "running" => light ? UiBrush(0xC5, 0xD8, 0xEA) : UiBrush(0x2B, 0x4B, 0x6B),
            "queued" => light ? UiBrush(0xE9, 0xD8, 0xBA) : UiBrush(0x5F, 0x46, 0x24),
            _ => light ? UiBrush(0xC9, 0xD4, 0xE1) : UiBrush(0x2B, 0x35, 0x41)
        };
    }

    private Brush GetAdminJobStatusForeground(string status, bool light)
    {
        status = NormalizeTrackedJobStatus(status);
        return status switch
        {
            "done" => light ? UiBrush(0x2E, 0x7D, 0x5A) : UiBrush(0x66, 0xD1, 0x9E),
            "failed" or "canceled" => light ? UiBrush(0xB4, 0x23, 0x18) : UiBrush(0xFF, 0x8A, 0x80),
            "cancel_requested" => light ? UiBrush(0x9A, 0x62, 0x00) : UiBrush(0xFF, 0xC7, 0x6A),
            "paused" => light ? UiBrush(0x6B, 0x46, 0xA7) : UiBrush(0xC4, 0xA7, 0xF2),
            "running" => light ? UiBrush(0x2B, 0x5D, 0x91) : UiBrush(0x78, 0xB4, 0xF0),
            "queued" => light ? UiBrush(0x9A, 0x62, 0x00) : UiBrush(0xFF, 0xC7, 0x6A),
            _ => light ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE)
        };
    }
}
