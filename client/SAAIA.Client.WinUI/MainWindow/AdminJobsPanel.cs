namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow
{
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
            Width = 360,
            MinWidth = 280,
            MaxWidth = 460,
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
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
        typeCombo.Visibility = Visibility.Collapsed;

        var ingestionTypeButton = BuildDialogInlineButton(ClientUiText.Get("admin.jobs.filter.ingestion", UiLang));
        ingestionTypeButton.MinWidth = 118;
        ingestionTypeButton.MinHeight = 44;
        ingestionTypeButton.VerticalAlignment = VerticalAlignment.Center;
        var summaryTypeButton = BuildDialogInlineButton(ClientUiText.Get("admin.jobs.filter.summary", UiLang));
        summaryTypeButton.MinWidth = 118;
        summaryTypeButton.MinHeight = 44;
        summaryTypeButton.VerticalAlignment = VerticalAlignment.Center;

        var dateFieldCombo = new ComboBox
        {
            MinWidth = 132,
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        dateFieldCombo.Items.Add(new ComboBoxItem { Content = ClientUiText.Get("admin.jobs.filter.date.finished", UiLang), Tag = "finished" });
        dateFieldCombo.Items.Add(new ComboBoxItem { Content = ClientUiText.Get("admin.jobs.filter.date.started", UiLang), Tag = "started" });
        dateFieldCombo.Items.Add(new ComboBoxItem { Content = ClientUiText.Get("admin.jobs.filter.date.created", UiLang), Tag = "created" });
        dateFieldCombo.SelectedIndex = 0;

        var datePresetCombo = new ComboBox
        {
            MinWidth = 118,
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        datePresetCombo.Items.Add(new ComboBoxItem { Content = ClientUiText.Get("admin.jobs.filter.range.all", UiLang), Tag = "all" });
        datePresetCombo.Items.Add(new ComboBoxItem { Content = ClientUiText.Get("admin.jobs.filter.range.today", UiLang), Tag = "today" });
        datePresetCombo.Items.Add(new ComboBoxItem { Content = ClientUiText.Get("admin.jobs.filter.range.custom", UiLang), Tag = "custom" });
        datePresetCombo.SelectedIndex = 0;

        var sortDirectionCombo = new ComboBox
        {
            MinWidth = 128,
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        sortDirectionCombo.Items.Add(new ComboBoxItem { Content = ClientUiText.Get("admin.jobs.filter.sort.newest", UiLang), Tag = "desc" });
        sortDirectionCombo.Items.Add(new ComboBoxItem { Content = ClientUiText.Get("admin.jobs.filter.sort.oldest", UiLang), Tag = "asc" });
        sortDirectionCombo.SelectedIndex = 0;

        var dateFromPicker = new CalendarDatePicker
        {
            PlaceholderText = ClientUiText.Get("admin.jobs.filter.from", UiLang),
            MinWidth = 138,
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };

        var dateToPicker = new CalendarDatePicker
        {
            PlaceholderText = ClientUiText.Get("admin.jobs.filter.to", UiLang),
            MinWidth = 138,
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };

        var statusCombo = new ComboBox
        {
            MinWidth = 170,
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed
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
        statusCombo.SelectedIndex = 0;

        var autoRefreshToggle = new ToggleSwitch
        {
            Header = string.Empty,
            IsOn = false,
            OnContent = ClientUiText.Get("admin.jobs.auto_on", UiLang),
            OffContent = ClientUiText.Get("admin.jobs.auto_off", UiLang),
            MinWidth = 110,
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
            Visibility = Visibility.Visible,
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
        var detailsScrollViewer = new ScrollViewer
        {
            Content = detailsHost,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Enabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            ZoomMode = ZoomMode.Disabled
        };
        var detailsCard = BuildDialogSurfaceCard(detailsScrollViewer, new Thickness(16));
        detailsCard.MinWidth = 340;
        detailsCard.VerticalAlignment = VerticalAlignment.Stretch;

        var typeFiltersHost = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        typeFiltersHost.Children.Add(ingestionTypeButton);
        typeFiltersHost.Children.Add(summaryTypeButton);

        var toolbarFiltersGrid = new Grid
        {
            ColumnSpacing = 10,
            VerticalAlignment = VerticalAlignment.Center
        };
        var toolbarSpacerColumns = new List<ColumnDefinition>();
        void AddToolbarControl(FrameworkElement element)
        {
            toolbarFiltersGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(element, toolbarFiltersGrid.ColumnDefinitions.Count - 1);
            toolbarFiltersGrid.Children.Add(element);
        }
        void AddToolbarSpacer()
        {
            var spacer = new ColumnDefinition { Width = GridLength.Auto };
            toolbarFiltersGrid.ColumnDefinitions.Add(spacer);
            toolbarSpacerColumns.Add(spacer);
        }

        AddToolbarControl(typeFiltersHost);
        AddToolbarSpacer();
        AddToolbarControl(dateFieldCombo);
        AddToolbarSpacer();
        AddToolbarControl(datePresetCombo);
        AddToolbarSpacer();
        AddToolbarControl(dateFromPicker);
        AddToolbarSpacer();
        AddToolbarControl(dateToPicker);
        AddToolbarSpacer();
        AddToolbarControl(sortDirectionCombo);

        var toolbarSearchViewport = new StackPanel
        {
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        toolbarSearchViewport.Children.Add(searchBox);
        toolbarSearchViewport.Children.Add(new Border { Height = 8, Opacity = 0 });

        var toolbarFiltersViewport = new StackPanel
        {
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        toolbarFiltersViewport.Children.Add(toolbarFiltersGrid);
        toolbarFiltersViewport.Children.Add(new Border { Height = 8, Opacity = 0 });

        var toolbarFiltersScrollViewer = new ScrollViewer
        {
            Content = toolbarFiltersViewport,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Disabled,
            ZoomMode = ZoomMode.Disabled,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center
        };

        var toolbarLayoutGrid = new Grid { ColumnSpacing = 12, VerticalAlignment = VerticalAlignment.Center };
        toolbarLayoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbarLayoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(toolbarSearchViewport, 0);
        Grid.SetColumn(toolbarFiltersScrollViewer, 1);
        toolbarLayoutGrid.Children.Add(toolbarSearchViewport);
        toolbarLayoutGrid.Children.Add(toolbarFiltersScrollViewer);

        double ComputeVisibleControlWidth(FrameworkElement control, double fallback)
            => control.Visibility == Visibility.Visible
                ? Math.Max(control.ActualWidth, control.MinWidth > 0 ? control.MinWidth : fallback)
                : 0d;

        void UpdateToolbarFiltersLayout()
        {
            var searchWidth = Math.Max(toolbarSearchViewport.ActualWidth, Math.Max(searchBox.ActualWidth, searchBox.Width));
            var availableWidth = toolbarLayoutGrid.ActualWidth - searchWidth - toolbarLayoutGrid.ColumnSpacing - 24;
            if (availableWidth <= 0)
                return;

            var minimumFiltersWidth =
                ingestionTypeButton.MinWidth +
                summaryTypeButton.MinWidth +
                typeFiltersHost.Spacing +
                ComputeVisibleControlWidth(dateFieldCombo, 132) +
                ComputeVisibleControlWidth(datePresetCombo, 118) +
                ComputeVisibleControlWidth(dateFromPicker, 138) +
                ComputeVisibleControlWidth(dateToPicker, 138) +
                ComputeVisibleControlWidth(sortDirectionCombo, 128) +
                (toolbarFiltersGrid.ColumnSpacing * 5);

            var canJustify = availableWidth >= minimumFiltersWidth + 72;
            foreach (var spacer in toolbarSpacerColumns)
                spacer.Width = canJustify ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            toolbarFiltersGrid.Width = canJustify ? availableWidth : double.NaN;
            toolbarFiltersScrollViewer.HorizontalScrollBarVisibility = canJustify ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto;
        }

        toolbarLayoutGrid.SizeChanged += (_, __) => UpdateToolbarFiltersLayout();

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

        refreshButton.MinWidth = 124;
        refreshButton.HorizontalAlignment = HorizontalAlignment.Right;
        refreshButton.VerticalAlignment = VerticalAlignment.Center;

        var autoRefreshHost = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        autoRefreshHost.Children.Add(new TextBlock
        {
            Text = ClientUiText.Get("admin.jobs.auto_refresh", UiLang),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = UseLightPalette() ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE)
        });
        autoRefreshHost.Children.Add(autoRefreshToggle);

        var headerActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        headerActions.Children.Add(refreshButton);
        headerActions.Children.Add(autoRefreshHost);
        Grid.SetColumn(headerActions, 1);
        pageHeaderGrid.Children.Add(headerActions);

        var pageRoot = new Grid
        {
            Background = UseLightPalette() ? UiBrush(0xE9, 0xEE, 0xF4) : UiBrush(0x0A, 0x0D, 0x12),
            Padding = new Thickness(18),
            RowSpacing = 12
        };
        pageRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pageRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pageRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pageRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        pageRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var metricsCard = BuildDialogSurfaceCard(metricsHost, new Thickness(10));
        var toolbarCard = BuildDialogSurfaceCard(toolbarLayoutGrid, new Thickness(12));
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
            DateFieldCombo = dateFieldCombo,
            DatePresetCombo = datePresetCombo,
            SortDirectionCombo = sortDirectionCombo,
            DateFromPicker = dateFromPicker,
            DateToPicker = dateToPicker,
            TypeCombo = typeCombo,
            IngestionCategoryButton = ingestionTypeButton,
            SummaryCategoryButton = summaryTypeButton,
            StatusCombo = statusCombo,
            AutoRefreshToggle = autoRefreshToggle,
            RefreshButton = refreshButton,
            DeleteSelectionButton = deleteSelectionButton,
            PurgeButton = purgeButton,
            MetricsHost = metricsHost,
            GroupsHost = groupsHost,
            DetailsCard = detailsCard,
            DetailsScrollViewer = detailsScrollViewer,
            DetailsHost = detailsHost,
            DetailsColumn = detailsColumn
        };

        closeDetailsButton.Click += (_, __) =>
        {
            context.SelectedJobId = null;
            RenderAdminJobsOverlay(context);
        };

        searchBox.TextChanged += (_, __) => RenderAdminJobsOverlay(context);
        void ApplyCategoryButtonVisuals()
        {
            var light = UseLightPalette();
            ingestionTypeButton.BorderThickness = new Thickness(context.IncludeIngestionCategory ? 2 : 1);
            summaryTypeButton.BorderThickness = new Thickness(context.IncludeSummaryCategory ? 2 : 1);
            ingestionTypeButton.Opacity = context.IncludeIngestionCategory ? 1d : 0.55d;
            summaryTypeButton.Opacity = context.IncludeSummaryCategory ? 1d : 0.55d;
            ingestionTypeButton.BorderBrush = context.IncludeIngestionCategory
                ? GetAdminJobStatusBorder("running", light)
                : (light ? UiBrush(0xC6, 0xD0, 0xDD) : UiBrush(0x3A, 0x45, 0x52));
            summaryTypeButton.BorderBrush = context.IncludeSummaryCategory
                ? GetAdminJobStatusBorder("running", light)
                : (light ? UiBrush(0xC6, 0xD0, 0xDD) : UiBrush(0x3A, 0x45, 0x52));
        }

        ingestionTypeButton.Click += async (_, __) =>
        {
            context.IncludeIngestionCategory = !context.IncludeIngestionCategory;
            context.SelectedJobId = null;
            ApplyCategoryButtonVisuals();
            RenderAdminJobsOverlay(context);
            if (context.IncludeIngestionCategory || context.IncludeSummaryCategory)
                await RefreshAdminJobsOverlayAsync(context, CancellationToken.None).ConfigureAwait(true);
        };
        summaryTypeButton.Click += async (_, __) =>
        {
            context.IncludeSummaryCategory = !context.IncludeSummaryCategory;
            context.SelectedJobId = null;
            ApplyCategoryButtonVisuals();
            RenderAdminJobsOverlay(context);
            if (context.IncludeIngestionCategory || context.IncludeSummaryCategory)
                await RefreshAdminJobsOverlayAsync(context, CancellationToken.None).ConfigureAwait(true);
        };
        ApplyCategoryButtonVisuals();
        statusCombo.SelectionChanged += (_, __) => RenderAdminJobsOverlay(context);
        dateFieldCombo.SelectionChanged += async (_, __) =>
        {
            SyncAdminJobsDateFilters(context);
            UpdateToolbarFiltersLayout();
            await RefreshAdminJobsOverlayAsync(context, CancellationToken.None).ConfigureAwait(true);
        };
        datePresetCombo.SelectionChanged += async (_, __) =>
        {
            SyncAdminJobsDateFilters(context);
            UpdateToolbarFiltersLayout();
            RenderAdminJobsOverlay(context);
            await RefreshAdminJobsOverlayAsync(context, CancellationToken.None).ConfigureAwait(true);
        };
        sortDirectionCombo.SelectionChanged += async (_, __) =>
        {
            SyncAdminJobsDateFilters(context);
            UpdateToolbarFiltersLayout();
            RenderAdminJobsOverlay(context);
            await RefreshAdminJobsOverlayAsync(context, CancellationToken.None).ConfigureAwait(true);
        };
        dateFromPicker.DateChanged += async (_, __) =>
        {
            SyncAdminJobsDateFilters(context);
            UpdateToolbarFiltersLayout();
            RenderAdminJobsOverlay(context);
            if (string.Equals(context.DatePreset, "custom", StringComparison.OrdinalIgnoreCase))
                await RefreshAdminJobsOverlayAsync(context, CancellationToken.None).ConfigureAwait(true);
        };
        dateToPicker.DateChanged += async (_, __) =>
        {
            SyncAdminJobsDateFilters(context);
            UpdateToolbarFiltersLayout();
            RenderAdminJobsOverlay(context);
            if (string.Equals(context.DatePreset, "custom", StringComparison.OrdinalIgnoreCase))
                await RefreshAdminJobsOverlayAsync(context, CancellationToken.None).ConfigureAwait(true);
        };
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
        SyncAdminJobsDateFilters(context);
        UpdateToolbarFiltersLayout();
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

        var visibleAll = new MenuFlyoutItem { Text = ClientUiText.Get("admin.jobs.purge.visible_all", UiLang) };
        visibleAll.Click += async (_, __) =>
            await DeleteAdminJobsByPredicateAsync(context, item => item.IsTerminal).ConfigureAwait(true);
        flyout.Items.Add(visibleAll);

        var purgeAllHistory = new MenuFlyoutItem { Text = ClientUiText.Get("admin.jobs.purge.all_history", UiLang) };
        purgeAllHistory.Click += async (_, __) =>
            await PurgeAdminJobsHistoryAsync(context, scope: "all").ConfigureAwait(true);
        flyout.Items.Add(purgeAllHistory);

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
            _adminJobsRefreshTimer.Interval = TimeSpan.FromSeconds(3);
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

        if (!context.IncludeIngestionCategory && !context.IncludeSummaryCategory)
        {
            RenderAdminJobsOverlay(context);
            return;
        }

        context.IsRefreshing = true;
        UpdateAdminJobsRefreshControls(context);
        var previousSummary = context.SummaryText.Text;
        context.SummaryText.Text = ClientUiText.Get("admin.jobs.loading", UiLang);

        try
        {
            SyncAdminJobsDateFilters(context);
            var useClientSideDateFiltering = IsAdminJobsDateFilterActive(context);
            if (HasInvalidAdminJobsDateRange(context))
            {
                context.Items = new List<AdminJobListItem>();
                context.HasMetricFilterInteraction = context.SelectedMetricFilters.Count == 0;
                context.SelectedTerminalJobIds.Clear();
                RenderAdminJobsOverlay(context);
                return;
            }

            var root = await _api.AdminJobsListAsync(
                    null,
                    500,
                    0,
                    context.DateField,
                    useClientSideDateFiltering ? null : FormatAdminJobsDateQueryValue(context.DateFrom),
                    useClientSideDateFiltering ? null : FormatAdminJobsDateQueryValue(context.DateTo),
                    context.SortDirection,
                    ct)
                .ConfigureAwait(true);
            var items = ParseAdminJobs(root);

            context.Items = items;
            context.HasMetricFilterInteraction = context.SelectedMetricFilters.Count == 0;
            context.SelectedTerminalJobIds.IntersectWith(items.Where(x => x.IsTerminal).Select(x => x.JobId));
            RenderAdminJobsOverlay(context);
        }
        catch (Exception ex)
        {
            ClientLog.Exception("AdminJobs.Refresh", ex);
            if (IsAdminJobsDateFilterActive(context))
            {
                try
                {
                    var fallbackRoot = await _api.AdminJobsListAsync(
                            null,
                            500,
                            0,
                            context.DateField,
                            null,
                            null,
                            context.SortDirection,
                            ct)
                        .ConfigureAwait(true);
                    var fallbackItems = ParseAdminJobs(fallbackRoot);
                    context.Items = fallbackItems;
                    context.HasMetricFilterInteraction = context.SelectedMetricFilters.Count == 0;
                    context.SelectedTerminalJobIds.IntersectWith(fallbackItems.Where(x => x.IsTerminal).Select(x => x.JobId));
                    RenderAdminJobsOverlay(context);
                }
                catch
                {
                    context.Items = new List<AdminJobListItem>();
                    context.HasMetricFilterInteraction = context.SelectedMetricFilters.Count == 0;
                    context.SelectedTerminalJobIds.Clear();
                    RenderAdminJobsOverlay(context);
                }
            }
            else
            {
                if (context.Items.Count > 0)
                {
                    context.SummaryText.Text = previousSummary + " • " + ClientUiText.Get("admin.jobs.refresh_failed_soft", UiLang) + ": " + ex.Message;
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
        }
        finally
        {
            context.IsRefreshing = false;
            UpdateAdminJobsRefreshControls(context);
        }
    }

    private async Task<string?> WaitForAdminJobCancellationSettlementAsync(
        AdminJobsOverlayContext context,
        string jobId,
        string requestedAction,
        CancellationToken ct)
    {
        const int maxAttempts = 20;
        const int delayMs = 350;

        string? lastStatus = null;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(delayMs, ct).ConfigureAwait(true);

            try
            {
                var response = await _api.AdminJobGetAsync(jobId, ct).ConfigureAwait(true);
                lastStatus = NormalizeTrackedJobStatus(TryGetString(response, "Status") ?? TryGetString(response, "status") ?? string.Empty);
                var cancelRequested = TryGetBool(response, "CancelRequested") ?? TryGetBool(response, "cancelRequested") ?? false;
                if (IsAdminJobCancellationSettled(lastStatus, cancelRequested, requestedAction))
                    break;
            }
            catch
            {
                break;
            }
        }

        await RefreshAdminJobsOverlayAsync(context, ct).ConfigureAwait(true);
        return lastStatus;
    }

    private static bool IsAdminJobCancellationSettled(string? status, bool cancelRequested, string requestedAction)
    {
        var normalized = NormalizeTrackedJobStatus(status);
        if (string.Equals(requestedAction, "pause", StringComparison.OrdinalIgnoreCase))
        {
            if (normalized == "paused")
                return true;

            return normalized is "canceled" or "failed" or "done";
        }

        return normalized is "canceled" or "failed" or "done";
    }

    private static bool IsAdminPauseTransitionPending(AdminJobListItem item)
    {
        var normalizedStatus = NormalizeTrackedJobStatus(item.Status);
        return normalizedStatus == "cancel_requested"
            && item.CancelRequested == true
            && string.Equals(item.JobType, "upsert", StringComparison.OrdinalIgnoreCase)
            && (item.DocumentIndexedVersion ?? 0) <= 0
            && item.DocumentAutoIngestPaused == true
            && IsAdminPauseReason(item.DocumentAutoIngestPauseReason);
    }

    private static bool IsAdminPauseReason(string? reason)
    {
        var normalized = (reason ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "admin_cancel" or "admin_pause";
    }

    private static bool IsAdminJobDocumentUnavailable(AdminJobListItem item)
    {
        var normalized = (item.DocumentStatus ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "missing" or "deleted";
    }

    private static bool CanResumeAdminJob(AdminJobListItem item, bool isCancelRequested)
        => item.IsPaused
           && item.DocumentAutoIngestPaused == true
           && !isCancelRequested
           && !IsAdminJobDocumentUnavailable(item);

    private void ApplyOptimisticAdminJobAction(AdminJobsOverlayContext context, AdminJobListItem sourceItem, string requestedAction)
    {
        var optimisticStatus = requestedAction == "pause"
            ? "paused"
            : sourceItem.IsRunning ? "cancel_requested" : "canceled";

        context.Items = context.Items
            .Select(item => string.Equals(item.JobId, sourceItem.JobId, StringComparison.OrdinalIgnoreCase)
                ? CloneAdminJobItem(
                    item,
                    status: optimisticStatus,
                    cancelRequested: requestedAction == "pause" || sourceItem.IsRunning,
                    documentAutoIngestPaused: requestedAction == "pause" ? true : item.DocumentAutoIngestPaused,
                    documentAutoIngestPauseReason: requestedAction == "pause" ? "admin_pause" : item.DocumentAutoIngestPauseReason)
                : item)
            .ToList();

        RenderAdminJobsOverlay(context);
    }

    private static AdminJobListItem CloneAdminJobItem(
        AdminJobListItem item,
        string? status = null,
        bool? cancelRequested = null,
        bool? documentAutoIngestPaused = null,
        string? documentAutoIngestPauseReason = null)
        => new()
        {
            JobId = item.JobId,
            Type = item.Type,
            JobType = item.JobType,
            Status = status ?? item.Status,
            DocId = item.DocId,
            DocPath = item.DocPath,
            Level = item.Level,
            LastError = item.LastError,
            CreatedAt = item.CreatedAt,
            StartedAt = item.StartedAt,
            FinishedAt = item.FinishedAt,
            ProgressPhase = item.ProgressPhase,
            ProgressCurrent = item.ProgressCurrent,
            ProgressTotal = item.ProgressTotal,
            ProgressPercent = item.ProgressPercent,
            CancelRequested = cancelRequested ?? item.CancelRequested,
            EnqueueSource = item.EnqueueSource,
            DocumentStatus = item.DocumentStatus,
            DocumentIngestionVersion = item.DocumentIngestionVersion,
            DocumentIndexedVersion = item.DocumentIndexedVersion,
            DocumentAutoIngestPaused = documentAutoIngestPaused ?? item.DocumentAutoIngestPaused,
            DocumentAutoIngestPauseReason = documentAutoIngestPauseReason ?? item.DocumentAutoIngestPauseReason
        };
}
