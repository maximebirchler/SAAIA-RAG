using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SAAIA.Client.WinUI.Services;
using SAAIA.Client.WinUI.Services.ToolAgent;

namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow
{
    private enum AdminConsoleSection
    {
        Overview = 0,
        Jobs = 1,
        Index = 2,
        RetrievalLab = 3,
        Summaries = 4,
        LocalAssistant = 5,
        DocumentTools = 6
    }

    private Window? _adminConsoleWindow;
    private ContentPresenter? _adminConsoleContentHost;
    private CancellationTokenSource? _adminConsoleCts;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _adminConsoleJobsRefreshTimer;
    private AdminJobsOverlayContext? _adminConsoleJobsContext;
    private AdminConsoleSection _adminConsoleActiveSection = AdminConsoleSection.Overview;
    private long _adminConsoleRenderVersion;
    private readonly Dictionary<AdminConsoleSection, Button> _adminConsoleNavButtons = new();

    private async Task ShowAdminConsoleWindowAsync(
        AdminConsoleSection section = AdminConsoleSection.Overview,
        AdminJobsLaunchMode jobsLaunchMode = AdminJobsLaunchMode.Default)
    {
        if (!_api.HasAdminKey)
        {
            Status(ClientUiText.Get("admin.jobs.no_admin", UiLang));
            return;
        }

        if (_adminConsoleWindow is not null && _adminConsoleContentHost is not null)
        {
            try { _adminConsoleWindow.Activate(); } catch { }
            await SelectAdminConsoleSectionAsync(section, jobsLaunchMode).ConfigureAwait(true);
            return;
        }

        CloseAdminConsoleWindow();

        var lang = UiLang;
        _adminConsoleCts = new CancellationTokenSource();

        var window = new Window
        {
            Title = ClientUiText.Get("admin.console.title", lang)
        };

        var titleBar = BuildAdminConsoleTitleBar(lang);
        var navigation = BuildAdminConsoleNavigation(lang);
        _adminConsoleContentHost = new ContentPresenter();

        var shell = new Grid
        {
            Background = UseLightPalette() ? UiBrush(0xE8, 0xF0, 0xF7) : UiBrush(0x08, 0x0D, 0x13)
        };
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var body = new Grid { ColumnSpacing = 0 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1);

        Grid.SetColumn(navigation, 0);
        Grid.SetColumn(_adminConsoleContentHost, 1);
        body.Children.Add(navigation);
        body.Children.Add(_adminConsoleContentHost);

        shell.Children.Add(titleBar);
        shell.Children.Add(body);
        window.Content = shell;

        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            window.ExtendsContentIntoTitleBar = true;
            window.SetTitleBar(titleBar);
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

        window.Closed += (_, __) =>
        {
            StopAdminConsoleJobsRefreshTimer();
            try { _adminConsoleCts?.Cancel(); } catch { }
            _adminConsoleCts?.Dispose();
            _adminConsoleCts = null;
            _adminConsoleWindow = null;
            _adminConsoleContentHost = null;
            _adminConsoleJobsContext = null;
            _adminConsoleNavButtons.Clear();
        };

        _adminConsoleWindow = window;
        window.Activate();
        TryPositionAdminJobsWindow(window);
        await SelectAdminConsoleSectionAsync(section, jobsLaunchMode).ConfigureAwait(true);
    }

    private void CloseAdminConsoleWindow()
    {
        StopAdminConsoleJobsRefreshTimer();
        try { _adminConsoleCts?.Cancel(); } catch { }
        _adminConsoleCts?.Dispose();
        _adminConsoleCts = null;
        _adminConsoleJobsContext = null;

        if (_adminConsoleWindow is not null)
        {
            try { _adminConsoleWindow.Close(); } catch { }
        }

        _adminConsoleWindow = null;
        _adminConsoleContentHost = null;
        _adminConsoleNavButtons.Clear();
    }

    private FrameworkElement BuildAdminConsoleTitleBar(string lang)
    {
        var titleBar = new Grid
        {
            Height = 46,
            Padding = new Thickness(16, 0, 16, 0),
            Background = UseLightPalette() ? UiBrush(0xF2, 0xF7, 0xFC) : UiBrush(0x0B, 0x10, 0x17)
        };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var logo = new Image
        {
            Source = new BitmapImage(new Uri("ms-appx:///Assets/SAAIA_Main.png")),
            Width = 24,
            Height = 24,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var title = new TextBlock
        {
            Text = ClientUiText.Get("admin.console.title", lang),
            FontWeight = FontWeights.SemiBold,
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = UseLightPalette() ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xF5, 0xF7, 0xFB)
        };

        Grid.SetColumn(logo, 0);
        Grid.SetColumn(title, 1);
        titleBar.Children.Add(logo);
        titleBar.Children.Add(title);
        return titleBar;
    }

    private FrameworkElement BuildAdminConsoleNavigation(string lang)
    {
        _adminConsoleNavButtons.Clear();
        var shell = new Border
        {
            Padding = new Thickness(14),
            BorderThickness = new Thickness(0, 0, 1, 0),
            BorderBrush = UseLightPalette() ? UiBrush(0xC8, 0xD5, 0xE3) : UiBrush(0x20, 0x29, 0x35),
            Background = UseLightPalette() ? UiBrush(0xF4, 0xF8, 0xFC) : UiBrush(0x0D, 0x13, 0x1B)
        };

        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(new TextBlock
        {
            Text = ClientUiText.Get("admin.console.subtitle", lang),
            TextWrapping = TextWrapping.WrapWholeWords,
            FontSize = 12,
            Foreground = UseLightPalette() ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xA8, 0xB5, 0xC7),
            Margin = new Thickness(0, 0, 0, 8)
        });

        AddAdminConsoleNavButton(stack, AdminConsoleSection.Overview, ClientUiText.Get("admin.console.nav.overview", lang));
        AddAdminConsoleNavButton(stack, AdminConsoleSection.Jobs, ClientUiText.Get("admin.console.nav.jobs", lang));
        AddAdminConsoleNavButton(stack, AdminConsoleSection.Index, ClientUiText.Get("admin.console.nav.index", lang));
        AddAdminConsoleNavButton(stack, AdminConsoleSection.RetrievalLab, ClientUiText.Get("admin.console.nav.retrieval", lang));
        AddAdminConsoleNavButton(stack, AdminConsoleSection.Summaries, ClientUiText.Get("admin.console.nav.summaries", lang));
        AddAdminConsoleNavButton(stack, AdminConsoleSection.LocalAssistant, ClientUiText.Get("admin.console.nav.local_assistant", lang));
        AddAdminConsoleNavButton(stack, AdminConsoleSection.DocumentTools, ClientUiText.Get("admin.console.nav.tools", lang));

        shell.Child = stack;
        return shell;
    }

    private void AddAdminConsoleNavButton(Panel host, AdminConsoleSection section, string label)
    {
        var button = BuildDialogInlineButton(label);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.MinHeight = 42;
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        button.Click += async (_, __) => await SelectAdminConsoleSectionAsync(section).ConfigureAwait(true);
        _adminConsoleNavButtons[section] = button;
        host.Children.Add(button);
    }

    private async Task SelectAdminConsoleSectionAsync(
        AdminConsoleSection section,
        AdminJobsLaunchMode jobsLaunchMode = AdminJobsLaunchMode.Default)
    {
        if (_adminConsoleContentHost is null)
            return;

        _adminConsoleActiveSection = section;
        _adminConsoleRenderVersion++;
        SyncAdminConsoleNavigationState();
        StopAdminConsoleJobsRefreshTimer();

        switch (section)
        {
            case AdminConsoleSection.Jobs:
                await RenderAdminConsoleJobsAsync(jobsLaunchMode).ConfigureAwait(true);
                break;
            case AdminConsoleSection.Index:
                await RenderAdminConsoleIndexAsync().ConfigureAwait(true);
                break;
            case AdminConsoleSection.RetrievalLab:
                RenderAdminConsoleRetrievalLab();
                break;
            case AdminConsoleSection.Summaries:
                await RenderAdminConsoleSummariesAsync().ConfigureAwait(true);
                break;
            case AdminConsoleSection.LocalAssistant:
                await RenderAdminConsoleLocalAssistantAsync().ConfigureAwait(true);
                break;
            case AdminConsoleSection.DocumentTools:
                RenderAdminConsoleDocumentTools();
                break;
            default:
                await RenderAdminConsoleOverviewAsync().ConfigureAwait(true);
                break;
        }
    }

    private void SyncAdminConsoleNavigationState()
    {
        foreach (var pair in _adminConsoleNavButtons)
        {
            var selected = pair.Key == _adminConsoleActiveSection;
            pair.Value.BorderThickness = new Thickness(selected ? 2 : 1);
            pair.Value.Opacity = selected ? 1d : 0.86d;
            if (selected)
            {
                ApplyInlineButtonStatusAccent(pair.Value, "running");
            }
            else
            {
                var light = UseLightPalette();
                var background = light ? UiBrush(0xF7, 0xFA, 0xFD) : UiBrush(0x14, 0x19, 0x22);
                var border = light ? UiBrush(0xC8, 0xD5, 0xE3) : UiBrush(0x2E, 0x38, 0x45);
                var foreground = light ? UiBrush(0x19, 0x24, 0x33) : UiBrush(0xE6, 0xEC, 0xF4);
                pair.Value.Background = background;
                pair.Value.BorderBrush = border;
                pair.Value.Foreground = foreground;
                pair.Value.Resources["ButtonBackgroundPointerOver"] = background;
                pair.Value.Resources["ButtonBackgroundPressed"] = background;
                pair.Value.Resources["ButtonBorderBrushPointerOver"] = border;
                pair.Value.Resources["ButtonBorderBrushPressed"] = border;
                pair.Value.Resources["ButtonForegroundPointerOver"] = foreground;
                pair.Value.Resources["ButtonForegroundPressed"] = foreground;
            }
        }
    }

    private async Task RenderAdminConsoleOverviewAsync()
    {
        var lang = UiLang;
        var renderVersion = _adminConsoleRenderVersion;
        SetAdminConsoleLoading(ClientUiText.Get("admin.runtime.loading", lang));
        try
        {
            var snapshot = ParseAdminRuntimeOperationalSnapshot(
                await _api.AdminRuntimeOperationalSummaryAsync(AdminConsoleToken).ConfigureAwait(true));
            if (!IsAdminConsoleRenderCurrent(AdminConsoleSection.Overview, renderVersion))
                return;

            var body = new StackPanel { Spacing = 14 };
            body.Children.Add(BuildDialogInfoBanner(ClientUiText.Get("admin.console.overview.help", lang)));
            body.Children.Add(BuildAdminConsoleMetricsGrid(new[]
            {
                (ClientUiText.Get("admin.runtime.metric.a_backlog", lang), snapshot.Summary.CapabilityACandidateCount.ToString(CultureInfo.InvariantCulture)),
                (ClientUiText.Get("admin.runtime.metric.a_ready", lang), snapshot.Summary.CapabilityAReadyToEnqueueCount.ToString(CultureInfo.InvariantCulture)),
                (ClientUiText.Get("admin.runtime.metric.b_backlog", lang), snapshot.Summary.CapabilityBBacklogCount.ToString(CultureInfo.InvariantCulture)),
                (ClientUiText.Get("admin.runtime.metric.b_active", lang), snapshot.Summary.CapabilityBActiveJobCount.ToString(CultureInfo.InvariantCulture))
            }));

            var actions = new StackPanel { Spacing = 10, Orientation = Orientation.Horizontal };
            var jobsButton = BuildDialogFooterButton(ClientUiText.Get("admin.console.nav.jobs", lang));
            jobsButton.Click += async (_, __) => await SelectAdminConsoleSectionAsync(AdminConsoleSection.Jobs).ConfigureAwait(true);
            var indexButton = BuildDialogFooterButton(ClientUiText.Get("admin.console.nav.index", lang));
            indexButton.Click += async (_, __) => await SelectAdminConsoleSectionAsync(AdminConsoleSection.Index).ConfigureAwait(true);
            var retrievalButton = BuildDialogFooterButton(ClientUiText.Get("admin.console.nav.retrieval", lang));
            retrievalButton.Click += async (_, __) => await SelectAdminConsoleSectionAsync(AdminConsoleSection.RetrievalLab).ConfigureAwait(true);
            var summariesButton = BuildDialogFooterButton(ClientUiText.Get("admin.console.nav.summaries", lang));
            summariesButton.Click += async (_, __) => await SelectAdminConsoleSectionAsync(AdminConsoleSection.Summaries).ConfigureAwait(true);
            var recheckButton = BuildDialogFooterButton(ClientUiText.Get("admin.runtime.requalify", lang), primary: true);
            recheckButton.Click += async (_, __) => await RequalifyAdminConsoleRuntimeAsync().ConfigureAwait(true);
            actions.Children.Add(jobsButton);
            actions.Children.Add(indexButton);
            actions.Children.Add(retrievalButton);
            actions.Children.Add(summariesButton);
            actions.Children.Add(recheckButton);
            body.Children.Add(actions);

            if (snapshot.Items.Count > 0)
            {
                var capabilities = new StackPanel { Spacing = 10 };
                foreach (var item in snapshot.Items.Take(4))
                    capabilities.Children.Add(BuildAdminConsoleCapabilityCard(item));
                body.Children.Add(capabilities);
            }

            SetAdminConsolePage(
                ClientUiText.Get("admin.console.overview.title", lang),
                ClientUiText.Format("admin.runtime.generated", lang, FormatAdminConsoleTimestamp(snapshot.GeneratedAt)),
                body);
        }
        catch (Exception ex)
        {
            if (!IsAdminConsoleRenderCurrent(AdminConsoleSection.Overview, renderVersion))
                return;
            ClientLog.Exception("AdminConsole.Overview", ex);
            SetAdminConsoleError(ClientUiText.Get("admin.runtime.load_failed", lang), FormatAdminLoadErrorForUser(ex, "/admin/runtime/operational-summary", lang));
        }
    }

    private async Task RenderAdminConsoleIndexAsync()
    {
        var lang = UiLang;
        var renderVersion = _adminConsoleRenderVersion;
        SetAdminConsoleLoading(ClientUiText.Get("admin.runtime.kpi_a.loading", lang));
        try
        {
            var snapshot = ParseAdminRuntimeOperationalSnapshot(
                await _api.AdminRuntimeOperationalSummaryAsync(AdminConsoleToken).ConfigureAwait(true));
            if (!IsAdminConsoleRenderCurrent(AdminConsoleSection.Index, renderVersion))
                return;

            var item = snapshot.Items.FirstOrDefault(x => string.Equals(x.Key, "capability_a.corpus_enrichment", StringComparison.OrdinalIgnoreCase));
            var body = new StackPanel { Spacing = 14 };
            body.Children.Add(BuildDialogInfoBanner(ClientUiText.Get("admin.runtime.kpi_a.help.body", lang), positive: true));
            body.Children.Add(BuildAdminConsoleMetricsGrid(new[]
            {
                (ClientUiText.Get("admin.runtime.kpi_a.metric.current_backlog", lang), snapshot.Summary.CapabilityACandidateCount.ToString(CultureInfo.InvariantCulture)),
                (ClientUiText.Get("admin.runtime.kpi_a.metric.current_ready", lang), snapshot.Summary.CapabilityAReadyToEnqueueCount.ToString(CultureInfo.InvariantCulture)),
                (ClientUiText.Get("admin.runtime.kpi_a.metric.current_offset_share", lang), snapshot.Summary.CapabilityAOffsetBackfillCandidateCount.ToString(CultureInfo.InvariantCulture)),
                (ClientUiText.Get("admin.runtime.fact.blocked_active", lang), (item?.Summary.BlockedByActiveJobCount ?? 0).ToString(CultureInfo.InvariantCulture))
            }));
            if (item is not null)
                body.Children.Add(BuildAdminConsoleCapabilityCard(item));

            var actions = new StackPanel { Spacing = 10, Orientation = Orientation.Horizontal };
            var previewButton = BuildDialogFooterButton(ClientUiText.Get("admin.runtime.action.preview_offsets", lang));
            previewButton.Click += async (_, __) => await PreviewAdminConsoleIndexFixesAsync().ConfigureAwait(true);
            var jobsButton = BuildDialogFooterButton(ClientUiText.Get("admin.console.open_jobs_a", lang), primary: true);
            jobsButton.Click += async (_, __) => await SelectAdminConsoleSectionAsync(AdminConsoleSection.Jobs, AdminJobsLaunchMode.CapabilityAEnrichment).ConfigureAwait(true);
            actions.Children.Add(previewButton);
            actions.Children.Add(jobsButton);
            body.Children.Add(actions);

            SetAdminConsolePage(ClientUiText.Get("admin.runtime.kpi_a.title", lang), ClientUiText.Get("admin.runtime.kpi_a.subtitle", lang), body);
        }
        catch (Exception ex)
        {
            if (!IsAdminConsoleRenderCurrent(AdminConsoleSection.Index, renderVersion))
                return;
            ClientLog.Exception("AdminConsole.Index", ex);
            SetAdminConsoleError(ClientUiText.Get("admin.runtime.kpi_a.load_failed", lang), FormatAdminLoadErrorForUser(ex, "/admin/runtime/operational-summary", lang));
        }
    }

    private void RenderAdminConsoleRetrievalLab()
    {
        var lang = UiLang;
        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(BuildDialogInfoBanner(ClientUiText.Get("admin.console.retrieval.help", lang), positive: true));

        var queryBox = new TextBox
        {
            PlaceholderText = ClientUiText.Get("admin.console.retrieval.query.placeholder", lang),
            MinHeight = 72,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.WrapWholeWords,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var categoryBox = new TextBox
        {
            PlaceholderText = ClientUiText.Get("admin.console.retrieval.category.placeholder", lang),
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var modeCombo = BuildAdminConsoleCombo(
            (ClientUiText.Get("admin.console.retrieval.mode.balanced", lang), "balanced"),
            (ClientUiText.Get("admin.console.retrieval.mode.broad", lang), "broad"),
            (ClientUiText.Get("admin.console.retrieval.mode.focused", lang), "focused"));
        var topKCombo = BuildAdminConsoleCombo(("8", "8"), ("12", "12"), ("20", "20"), ("30", "30"));
        topKCombo.SelectedIndex = 1;
        ApplyDialogInputChrome(queryBox);
        ApplyDialogInputChrome(categoryBox);
        ApplyDialogInputChrome(modeCombo);
        ApplyDialogInputChrome(topKCombo);

        var formGrid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        formGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        formGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetColumnSpan(queryBox, 3);
        Grid.SetRow(queryBox, 0);
        Grid.SetRow(categoryBox, 1);
        Grid.SetColumn(categoryBox, 0);
        Grid.SetRow(modeCombo, 1);
        Grid.SetColumn(modeCombo, 1);
        Grid.SetRow(topKCombo, 1);
        Grid.SetColumn(topKCombo, 2);
        formGrid.Children.Add(queryBox);
        formGrid.Children.Add(categoryBox);
        formGrid.Children.Add(modeCombo);
        formGrid.Children.Add(topKCombo);

        var resultHost = new StackPanel { Spacing = 12 };
        var runButton = BuildDialogFooterButton(ClientUiText.Get("admin.console.retrieval.run", lang), primary: true);
        runButton.HorizontalAlignment = HorizontalAlignment.Left;
        runButton.Click += async (_, __) => await RunAdminConsoleRetrievalLabAsync(
            queryBox,
            categoryBox,
            modeCombo,
            topKCombo,
            runButton,
            resultHost).ConfigureAwait(true);

        var form = new StackPanel { Spacing = 12 };
        form.Children.Add(formGrid);
        form.Children.Add(runButton);
        body.Children.Add(BuildDialogSurfaceCard(form, new Thickness(14)));
        resultHost.Children.Add(BuildDialogInfoBanner(ClientUiText.Get("admin.console.retrieval.empty", lang)));
        body.Children.Add(resultHost);

        SetAdminConsolePage(
            ClientUiText.Get("admin.console.retrieval.title", lang),
            ClientUiText.Get("admin.console.retrieval.subtitle", lang),
            body);
    }

    private async Task RunAdminConsoleRetrievalLabAsync(
        TextBox queryBox,
        TextBox categoryBox,
        ComboBox modeCombo,
        ComboBox topKCombo,
        Button runButton,
        Panel resultHost)
    {
        var lang = UiLang;
        var query = (queryBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            resultHost.Children.Clear();
            resultHost.Children.Add(BuildDialogInfoBanner(ClientUiText.Get("admin.console.retrieval.no_query", lang)));
            return;
        }

        var renderVersion = _adminConsoleRenderVersion;
        runButton.IsEnabled = false;
        resultHost.Children.Clear();
        resultHost.Children.Add(BuildDialogInfoBanner(ClientUiText.Get("admin.console.retrieval.loading", lang)));

        try
        {
            var topK = int.TryParse(GetAdminConsoleComboTag(topKCombo), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTopK)
                ? parsedTopK
                : 12;
            var response = await _api.AdminRagTestRetrievalAsync(
                query,
                string.IsNullOrWhiteSpace(categoryBox.Text) ? null : categoryBox.Text.Trim(),
                topK,
                GetAdminConsoleComboTag(modeCombo),
                AdminConsoleToken).ConfigureAwait(true);

            if (!IsAdminConsoleRenderCurrent(AdminConsoleSection.RetrievalLab, renderVersion))
                return;

            resultHost.Children.Clear();
            resultHost.Children.Add(BuildAdminConsoleRetrievalResult(response, lang));
        }
        catch (Exception ex)
        {
            if (!IsAdminConsoleRenderCurrent(AdminConsoleSection.RetrievalLab, renderVersion))
                return;
            ClientLog.Exception("AdminConsole.RetrievalLab", ex);
            resultHost.Children.Clear();
            resultHost.Children.Add(BuildDialogInfoBanner(ClientUiText.Get("admin.console.retrieval.failed", lang)));
            resultHost.Children.Add(BuildDialogInfoBanner(FormatAdminLoadErrorForUser(ex, "/admin/rag/test-retrieval", lang)));
        }
        finally
        {
            if (IsAdminConsoleRenderCurrent(AdminConsoleSection.RetrievalLab, renderVersion))
                runButton.IsEnabled = true;
        }
    }

    private FrameworkElement BuildAdminConsoleRetrievalResult(JsonElement response, string lang)
    {
        var stack = new StackPanel { Spacing = 12 };
        if (response.ValueKind != JsonValueKind.Object)
        {
            stack.Children.Add(BuildDialogInfoBanner(ClientUiText.Get("admin.console.retrieval.no_result", lang)));
            return BuildDialogSurfaceCard(stack, new Thickness(14));
        }

        var metrics = response.TryGetProperty("metrics", out var metricsElement) && metricsElement.ValueKind == JsonValueKind.Object
            ? metricsElement
            : default;
        var retrievers = metrics.ValueKind == JsonValueKind.Object && metrics.TryGetProperty("retrieversUsed", out var retrieversElement) && retrieversElement.ValueKind == JsonValueKind.Array
            ? FormatAdminConsoleRetrievalMethodList(retrieversElement, lang)
            : "-";
        var degraded = metrics.ValueKind == JsonValueKind.Object && metrics.TryGetProperty("degradedRetrievers", out var degradedElement) && degradedElement.ValueKind == JsonValueKind.Array
            ? FormatAdminConsoleRetrievalMethodList(degradedElement, lang)
            : "-";

        stack.Children.Add(BuildAdminConsoleMetricsGrid(new[]
        {
            (ClientUiText.Get("admin.console.retrieval.metric.duration", lang), FormatAdminConsoleMetric(metrics.ValueKind == JsonValueKind.Object ? TryGetInt(metrics, "tookMs") : null, "ms")),
            (ClientUiText.Get("admin.console.retrieval.metric.returned", lang), FormatAdminConsoleMetric(metrics.ValueKind == JsonValueKind.Object ? TryGetInt(metrics, "returned") : null)),
            (ClientUiText.Get("admin.console.retrieval.metric.candidates", lang), FormatAdminConsoleMetric(metrics.ValueKind == JsonValueKind.Object ? TryGetInt(metrics, "candidatesEvaluated") : TryGetInt(response, "candidates"))),
            (ClientUiText.Get("admin.console.retrieval.metric.degraded", lang), string.IsNullOrWhiteSpace(degraded) ? "-" : degraded)
        }));

        if (metrics.ValueKind == JsonValueKind.Object)
            stack.Children.Add(BuildAdminConsoleRetrievalPhaseBreakdown(metrics, lang));

        var diagnosticsCard = BuildAdminConsoleRetrievalDiagnosticsCard(response, lang);
        if (diagnosticsCard is not null)
            stack.Children.Add(diagnosticsCard);

        var guidanceCard = BuildAdminConsoleRetrievalGuidanceCard(response, lang);
        if (guidanceCard is not null)
            stack.Children.Add(guidanceCard);

        if (!string.IsNullOrWhiteSpace(retrievers) && retrievers != "-")
            stack.Children.Add(BuildDialogInfoBanner(ClientUiText.Format("admin.console.retrieval.retrievers", lang, retrievers)));

        if (metrics.ValueKind == JsonValueKind.Object
            && metrics.TryGetProperty("degradedRetrieverErrors", out var errorsElement)
            && errorsElement.ValueKind == JsonValueKind.Object)
        {
            var errorLines = errorsElement
                .EnumerateObject()
                .Select(prop => $"{HumanizeAdminConsoleRetrievalMethod(prop.Name, lang)}: {TrimAdminConsoleText(prop.Value.GetString() ?? prop.Value.ToString(), 220)}")
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .ToArray();
            if (errorLines.Length > 0)
                stack.Children.Add(BuildDialogInfoBanner(ClientUiText.Format("admin.console.retrieval.degraded_errors", lang, string.Join(Environment.NewLine, errorLines))));
        }

        if (!response.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            stack.Children.Add(BuildDialogInfoBanner(ClientUiText.Get("admin.console.retrieval.no_result", lang)));
            return BuildDialogSurfaceCard(stack, new Thickness(14));
        }

        var itemList = items.EnumerateArray().Take(12).ToArray();
        if (itemList.Length == 0)
        {
            stack.Children.Add(BuildDialogInfoBanner(ClientUiText.Get("admin.console.retrieval.no_sources", lang)));
            return BuildDialogSurfaceCard(stack, new Thickness(14));
        }

        stack.Children.Add(BuildAdminConsoleSubHeader(ClientUiText.Get("admin.console.retrieval.sources", lang)));
        stack.Children.Add(BuildAdminConsoleRetrievalDiversityCard(itemList, lang));
        foreach (var item in itemList)
            stack.Children.Add(BuildAdminConsoleRetrievalItemCard(item, lang));

        return BuildDialogSurfaceCard(stack, new Thickness(14));
    }

    private FrameworkElement? BuildAdminConsoleRetrievalDiagnosticsCard(JsonElement response, string lang)
    {
        if (!response.TryGetProperty("diagnostics", out var diagnostics) || diagnostics.ValueKind != JsonValueKind.Object)
            return null;
        if (!diagnostics.TryGetProperty("phases", out var phases) || phases.ValueKind != JsonValueKind.Array)
            return null;

        var phaseRows = phases.EnumerateArray()
            .Where(static phase => phase.ValueKind == JsonValueKind.Object)
            .Take(10)
            .Select(phase =>
            {
                var name = HumanizeAdminConsoleRetrievalPhaseName(TryGetString(phase, "name"), lang);
                var retriever = HumanizeAdminConsoleRetrievalMethod(TryGetString(phase, "retriever"), lang) ?? "-";
                var returned = TryGetInt(phase, "returned")?.ToString("N0", CultureInfo.CurrentCulture) ?? "-";
                var duration = FormatAdminConsoleMetric(TryGetInt(phase, "durationMs"), "ms");
                var firstCandidate = ExtractAdminConsoleFirstDiagnosticCandidate(phase, lang);
                return ClientUiText.Format(
                    "admin.console.retrieval.diagnostics.phase_row",
                    lang,
                    name,
                    retriever,
                    returned,
                    duration,
                    firstCandidate);
            })
            .Where(static row => !string.IsNullOrWhiteSpace(row))
            .ToArray();

        if (phaseRows.Length == 0)
            return null;

        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(BuildAdminConsoleSubHeader(ClientUiText.Get("admin.console.retrieval.diagnostics.title", lang)));
        stack.Children.Add(new TextBlock
        {
            Text = ClientUiText.Get("admin.console.retrieval.diagnostics.help", lang),
            Foreground = UseLightPalette() ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE),
            TextWrapping = TextWrapping.WrapWholeWords
        });
        stack.Children.Add(new TextBlock
        {
            Text = string.Join(Environment.NewLine, phaseRows),
            Foreground = UseLightPalette() ? UiBrush(0x19, 0x24, 0x33) : UiBrush(0xF2, 0xF5, 0xFA),
            TextWrapping = TextWrapping.WrapWholeWords
        });

        if (diagnostics.TryGetProperty("selection", out var selection) && selection.ValueKind == JsonValueKind.Object)
        {
            stack.Children.Add(BuildAdminConsoleMetricsGrid(new[]
            {
                (ClientUiText.Get("admin.console.retrieval.diagnostics.selection_returned", lang), FormatAdminConsoleMetric(TryGetInt(selection, "returned"))),
                (ClientUiText.Get("admin.console.retrieval.diagnostics.selection_duplicates", lang), FormatAdminConsoleMetric(TryGetInt(selection, "duplicatePageOrDocPressure"))),
                (ClientUiText.Get("admin.console.retrieval.diagnostics.selection_max_doc", lang), FormatAdminConsoleMetric(TryGetInt(selection, "maxPerDoc"))),
                (ClientUiText.Get("admin.console.retrieval.diagnostics.selection_max_page", lang), FormatAdminConsoleMetric(TryGetInt(selection, "maxPerPage")))
            }));
        }

        return BuildDialogSurfaceCard(stack, new Thickness(14));
    }

    private static string ExtractAdminConsoleFirstDiagnosticCandidate(JsonElement phase, string lang)
    {
        if (!phase.TryGetProperty("topCandidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
            return ClientUiText.Get("admin.console.retrieval.diagnostics.no_candidate", lang);

        var candidate = candidates.EnumerateArray().FirstOrDefault(static item => item.ValueKind == JsonValueKind.Object);
        if (candidate.ValueKind != JsonValueKind.Object)
            return ClientUiText.Get("admin.console.retrieval.diagnostics.no_candidate", lang);

        var doc = TryGetString(candidate, "docName") ?? TryGetString(candidate, "docPath") ?? ClientUiText.Get("ui.not_available", lang);
        var page = TryGetInt(candidate, "pageStart");
        var score = FormatAdminConsoleScore(TryGetDouble(candidate, "score"));
        var pageText = page is null
            ? doc
            : ClientUiText.Format("admin.console.retrieval.item.title_page", lang, doc, page.Value.ToString(CultureInfo.InvariantCulture));
        return ClientUiText.Format("admin.console.retrieval.diagnostics.first_candidate", lang, pageText, score);
    }

    private FrameworkElement BuildAdminConsoleRetrievalPhaseBreakdown(JsonElement metrics, string lang)
    {
        var phaseMetrics = new List<(string Label, string Value)>
        {
            (ClientUiText.Get("admin.console.retrieval.phase.exact", lang), FormatAdminConsoleMetric(SumAdminConsoleMetric(metrics, "exactMs", "quotedTitleMs", "localTitleTokenMs", "titleAnchorRouteMs"), "ms")),
            (ClientUiText.Get("admin.console.retrieval.phase.sparse", lang), FormatAdminConsoleMetric(SumAdminConsoleMetric(metrics, "sparsePhaseMs", "sparseMs"), "ms")),
            (ClientUiText.Get("admin.console.retrieval.phase.dense", lang), FormatAdminConsoleMetric(SumAdminConsoleMetric(metrics, "denseMs", "qdrantMs", "teiMs"), "ms")),
            (ClientUiText.Get("admin.console.retrieval.phase.profile", lang), FormatAdminConsoleMetric(TryGetInt(metrics, "profileMs"), "ms")),
            (ClientUiText.Get("admin.console.retrieval.phase.linked", lang), FormatAdminConsoleMetric(TryGetInt(metrics, "linkedMs"), "ms")),
            (ClientUiText.Get("admin.console.retrieval.phase.fusion", lang), FormatAdminConsoleMetric(TryGetInt(metrics, "fusionMs"), "ms")),
            (ClientUiText.Get("admin.console.retrieval.phase.rerank", lang), FormatAdminConsoleMetric(SumAdminConsoleMetric(metrics, "rerankPhaseMs", "rerankMs"), "ms")),
            (ClientUiText.Get("admin.console.retrieval.phase.selection", lang), FormatAdminConsoleMetric(TryGetInt(metrics, "selectionMs"), "ms"))
        };

        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(BuildAdminConsoleSubHeader(ClientUiText.Get("admin.console.retrieval.phases", lang)));
        stack.Children.Add(new TextBlock
        {
            Text = ClientUiText.Get("admin.console.retrieval.phases.help", lang),
            Foreground = UseLightPalette() ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE),
            TextWrapping = TextWrapping.WrapWholeWords
        });
        stack.Children.Add(BuildAdminConsoleMetricsGrid(phaseMetrics));
        return BuildDialogSurfaceCard(stack, new Thickness(14));
    }

    private FrameworkElement? BuildAdminConsoleRetrievalGuidanceCard(JsonElement response, string lang)
    {
        if (!response.TryGetProperty("guidance", out var guidance) || guidance.ValueKind != JsonValueKind.Object)
            return null;

        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(BuildAdminConsoleSubHeader(ClientUiText.Get("admin.console.retrieval.guidance.title", lang)));
        AddAdminConsoleFact(
            stack,
            ClientUiText.Get("admin.console.retrieval.guidance.behavior", lang),
            HumanizeAdminConsoleGuidanceBehavior(TryGetString(guidance, "behavior"), lang));
        AddAdminConsoleFact(
            stack,
            ClientUiText.Get("admin.console.retrieval.guidance.shape", lang),
            HumanizeAdminConsoleResponseShape(TryGetString(guidance, "responseShape"), lang));
        AddAdminConsoleFact(
            stack,
            ClientUiText.Get("admin.console.retrieval.guidance.reason", lang),
            HumanizeAdminConsoleGuidanceReason(TryGetString(guidance, "reason"), lang));
        AddAdminConsoleFact(
            stack,
            ClientUiText.Get("admin.console.retrieval.guidance.question", lang),
            TryGetString(guidance, "clarifyingQuestion"));
        AddAdminConsoleFact(
            stack,
            ClientUiText.Get("admin.console.retrieval.guidance.note", lang),
            TryGetString(guidance, "qualificationNote"));

        if (guidance.TryGetProperty("matchedDocHints", out var hints) && hints.ValueKind == JsonValueKind.Array)
        {
            var hintLines = hints.EnumerateArray()
                .Select(static item => item.GetString())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(value => TrimAdminConsoleText(value!, 160))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToArray();
            if (hintLines.Length > 0)
            {
                AddAdminConsoleFact(
                    stack,
                    ClientUiText.Get("admin.console.retrieval.guidance.hints", lang),
                    string.Join(Environment.NewLine, hintLines));
            }
        }

        return BuildDialogSurfaceCard(stack, new Thickness(14));
    }

    private FrameworkElement BuildAdminConsoleRetrievalDiversityCard(IReadOnlyList<JsonElement> items, string lang)
    {
        var docs = items
            .Select(item => TryGetString(item, "docPath") ?? TryGetString(item, "docName"))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var pages = items
            .Select(item =>
            {
                var doc = TryGetString(item, "docPath") ?? TryGetString(item, "docName") ?? string.Empty;
                var page = TryGetInt(item, "pageStart")?.ToString(CultureInfo.InvariantCulture) ?? "-";
                return $"{doc}#{page}";
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var methods = items
            .Select(item => HumanizeAdminConsoleRetrievalMethod(TryGetString(item, "retriever"), lang))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(BuildAdminConsoleSubHeader(ClientUiText.Get("admin.console.retrieval.diversity.title", lang)));
        stack.Children.Add(BuildAdminConsoleMetricsGrid(new[]
        {
            (ClientUiText.Get("admin.console.retrieval.diversity.docs", lang), docs.ToString("N0", CultureInfo.CurrentCulture)),
            (ClientUiText.Get("admin.console.retrieval.diversity.pages", lang), pages.ToString("N0", CultureInfo.CurrentCulture)),
            (ClientUiText.Get("admin.console.retrieval.diversity.methods", lang), methods.Length.ToString("N0", CultureInfo.CurrentCulture)),
            (ClientUiText.Get("admin.console.retrieval.diversity.duplicates", lang), Math.Max(0, items.Count - pages).ToString("N0", CultureInfo.CurrentCulture))
        }));
        stack.Children.Add(new TextBlock
        {
            Text = methods.Length == 0
                ? ClientUiText.Get("admin.console.retrieval.diversity.no_methods", lang)
                : ClientUiText.Format("admin.console.retrieval.diversity.method_list", lang, string.Join(", ", methods)),
            Foreground = UseLightPalette() ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE),
            TextWrapping = TextWrapping.WrapWholeWords
        });

        return BuildDialogSurfaceCard(stack, new Thickness(14));
    }

    private FrameworkElement BuildAdminConsoleRetrievalItemCard(JsonElement item, string lang)
    {
        var stack = new StackPanel { Spacing = 8 };
        var docName = TryGetString(item, "docName") ?? TryGetString(item, "docPath") ?? ClientUiText.Get("ui.not_available", lang);
        var page = TryGetInt(item, "pageStart");
        stack.Children.Add(BuildAdminConsoleSubHeader(page is null
            ? docName
            : ClientUiText.Format("admin.console.retrieval.item.title_page", lang, docName, page.Value.ToString(CultureInfo.InvariantCulture))));

        AddAdminConsoleFact(stack, ClientUiText.Get("admin.console.retrieval.item.score", lang), FormatAdminConsoleScore(TryGetDouble(item, "score")));
        AddAdminConsoleFact(stack, ClientUiText.Get("admin.console.retrieval.item.rerank_score", lang), FormatAdminConsoleScore(TryGetDouble(item, "rerankScore")));
        AddAdminConsoleFact(stack, ClientUiText.Get("admin.console.retrieval.item.retriever", lang), HumanizeAdminConsoleRetrievalMethod(TryGetString(item, "retriever"), lang));
        AddAdminConsoleFact(stack, ClientUiText.Get("admin.console.retrieval.item.heading", lang), TryGetString(item, "headingPath") ?? TryGetString(item, "sectionTitle"));
        AddAdminConsoleFact(stack, ClientUiText.Get("admin.console.retrieval.item.chunk", lang), TryGetString(item, "chunkId"));
        AddAdminConsoleFact(stack, ClientUiText.Get("admin.console.retrieval.item.role", lang), HumanizeAdminConsoleContentRole(
            TryGetStringFromNested(item, "context", "contentRole"),
            lang));
        AddAdminConsoleFact(stack, ClientUiText.Get("admin.console.retrieval.item.quality", lang),
            HumanizeAdminConsoleQualityStatus(
                TryGetStringFromNested(item, "extractionQuality", "pageQualityStatus")
                ?? TryGetStringFromNested(item, "extractionQuality", "documentQualityStatus"),
                lang));
        AddAdminConsoleFact(stack, ClientUiText.Get("admin.console.retrieval.item.hash", lang), ShortenAdminConsoleHash(TryGetString(item, "sourceHash")));

        var snippet = TryGetString(item, "contextualSnippet")
            ?? TryGetString(item, "snippet")
            ?? TryGetString(item, "text");
        if (!string.IsNullOrWhiteSpace(snippet))
        {
            stack.Children.Add(new TextBlock
            {
                Text = ClientUiText.Get("admin.console.retrieval.item.snippet", lang),
                FontWeight = FontWeights.SemiBold,
                Foreground = UseLightPalette() ? UiBrush(0x5D, 0x6E, 0x82) : UiBrush(0xA8, 0xB5, 0xC7),
                TextWrapping = TextWrapping.WrapWholeWords
            });
            stack.Children.Add(new TextBlock
            {
                Text = TrimAdminConsoleText(snippet, 650),
                Foreground = UseLightPalette() ? UiBrush(0x19, 0x24, 0x33) : UiBrush(0xF2, 0xF5, 0xFA),
                TextWrapping = TextWrapping.WrapWholeWords
            });
        }

        return BuildDialogSurfaceCard(stack, new Thickness(14));
    }

    private async Task RenderAdminConsoleSummariesAsync()
    {
        var lang = UiLang;
        var renderVersion = _adminConsoleRenderVersion;
        SetAdminConsoleLoading(ClientUiText.Get("admin.runtime.quality.loading", lang));
        try
        {
            var snapshot = ParseAdminRuntimeOperationalSnapshot(
                await _api.AdminRuntimeOperationalSummaryAsync(AdminConsoleToken).ConfigureAwait(true));
            AdminRuntimeCapabilityBQualitySnapshot? qualitySnapshot = null;
            string? qualityLoadError = null;
            try
            {
                var qualitySummaryJson = await _api.AdminRuntimeCapabilityBQualityReviewSummaryAsync(AdminConsoleToken).ConfigureAwait(true);
                var qualityReviewJson = await _api.AdminRuntimeCapabilityBQualityReviewAsync(5, AdminConsoleToken).ConfigureAwait(true);
                qualitySnapshot = ParseAdminRuntimeCapabilityBQualitySnapshot(qualitySummaryJson, qualityReviewJson);
            }
            catch (OperationCanceledException) when (AdminConsoleToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception qualityEx)
            {
                ClientLog.Exception("AdminConsole.SummariesQuality", qualityEx);
                qualityLoadError = FormatAdminLoadErrorForUser(qualityEx, "/admin/runtime/capabilities/capability_b.backoffice_generation/quality-review", lang);
            }

            if (!IsAdminConsoleRenderCurrent(AdminConsoleSection.Summaries, renderVersion))
                return;

            var item = snapshot.Items.FirstOrDefault(x => string.Equals(x.Key, "capability_b.backoffice_generation", StringComparison.OrdinalIgnoreCase));
            var body = new StackPanel { Spacing = 14 };
            body.Children.Add(BuildDialogInfoBanner(ClientUiText.Get("admin.runtime.quality.help.body", lang), positive: true));
            var metrics = new List<(string Label, string Value)>
            {
                (ClientUiText.Get("admin.runtime.metric.b_backlog", lang), snapshot.Summary.CapabilityBBacklogCount.ToString(CultureInfo.InvariantCulture)),
                (ClientUiText.Get("admin.runtime.metric.b_ready", lang), snapshot.Summary.CapabilityBReadyToEnqueueCount.ToString(CultureInfo.InvariantCulture)),
                (ClientUiText.Get("admin.runtime.metric.b_active", lang), snapshot.Summary.CapabilityBActiveJobCount.ToString(CultureInfo.InvariantCulture)),
                (ClientUiText.Get("admin.console.summaries.stored", lang), (item?.Summary.StoredSummaryCount ?? 0).ToString(CultureInfo.InvariantCulture))
            };
            if (qualitySnapshot is not null)
            {
                metrics.Add((ClientUiText.Get("admin.runtime.quality.metric.low_count", lang), qualitySnapshot.Summary.TotalLowQualitySummaries.ToString(CultureInfo.InvariantCulture)));
                metrics.Add((ClientUiText.Get("admin.runtime.quality.metric.fallbacks", lang), qualitySnapshot.Summary.FallbackSummaryCount.ToString(CultureInfo.InvariantCulture)));
                metrics.Add((ClientUiText.Get("admin.runtime.quality.metric.runtime_unavailable", lang), qualitySnapshot.Summary.RuntimeUnavailableCount.ToString(CultureInfo.InvariantCulture)));
                metrics.Add((ClientUiText.Get("admin.runtime.quality.metric.lowest_score", lang), qualitySnapshot.Summary.LowestQualityScore?.ToString("0.00", CultureInfo.InvariantCulture) ?? ClientUiText.Get("ui.not_available", lang)));
            }
            body.Children.Add(BuildAdminConsoleMetricsGrid(metrics));
            if (item is not null)
                body.Children.Add(BuildAdminConsoleCapabilityCard(item));

            if (qualitySnapshot is not null)
                body.Children.Add(BuildAdminConsoleQualityReviewPreview(qualitySnapshot, lang));
            else
                body.Children.Add(BuildDialogInfoBanner(ClientUiText.Get("admin.runtime.quality.load_failed", lang) + " " + qualityLoadError));

            var actions = new StackPanel { Spacing = 10, Orientation = Orientation.Horizontal };
            var qualityButton = BuildDialogFooterButton(ClientUiText.Get("admin.runtime.quality.refresh", lang));
            qualityButton.Click += async (_, __) => await SelectAdminConsoleSectionAsync(AdminConsoleSection.Summaries).ConfigureAwait(true);
            var jobsButton = BuildDialogFooterButton(ClientUiText.Get("admin.console.open_jobs_b", lang), primary: true);
            jobsButton.Click += async (_, __) => await SelectAdminConsoleSectionAsync(AdminConsoleSection.Jobs, AdminJobsLaunchMode.CapabilityBBackoffice).ConfigureAwait(true);
            actions.Children.Add(qualityButton);
            actions.Children.Add(jobsButton);
            body.Children.Add(actions);

            SetAdminConsolePage(ClientUiText.Get("admin.runtime.quality.title", lang), ClientUiText.Get("admin.runtime.quality.subtitle", lang), body);
        }
        catch (Exception ex)
        {
            if (!IsAdminConsoleRenderCurrent(AdminConsoleSection.Summaries, renderVersion))
                return;
            ClientLog.Exception("AdminConsole.Summaries", ex);
            SetAdminConsoleError(ClientUiText.Get("admin.runtime.quality.load_failed", lang), FormatAdminLoadErrorForUser(ex, "/admin/runtime/capabilities/capability_b.backoffice_generation/quality-review", lang));
        }
    }

    private async Task RenderAdminConsoleLocalAssistantAsync()
    {
        var lang = UiLang;
        var renderVersion = _adminConsoleRenderVersion;
        SetAdminConsoleLoading(ClientUiText.Get("admin.console.local.loading", lang));
        try
        {
            var diagnostics = await LocalLlmRuntimeDiagnosticsService.EvaluateAsync(AppSettings.Load(), ct: AdminConsoleToken).ConfigureAwait(true);
            if (!IsAdminConsoleRenderCurrent(AdminConsoleSection.LocalAssistant, renderVersion))
                return;

            var body = new StackPanel { Spacing = 14 };
            body.Children.Add(BuildDialogInfoBanner(ClientUiText.Get("admin.console.local.help", lang), positive: true));
            body.Children.Add(BuildAdminConsoleMetricsGrid(new[]
            {
                (ClientUiText.Get("admin.console.local.state", lang), ResolveRuntimeStateLabel(diagnostics.ActiveState, lang)),
                (ClientUiText.Get("admin.console.local.warmup", lang), ResolveWarmupStateLabel(diagnostics.LatestWarmupStatus, lang)),
                (ClientUiText.Get("admin.console.local.build", lang), diagnostics.ActiveBuild ?? "-"),
                (ClientUiText.Get("admin.console.local.model", lang), diagnostics.ModelId ?? "-")
            }));

            var details = new StackPanel { Spacing = 8 };
            AddAdminConsoleFact(details, ClientUiText.Get("admin.console.local.profile", lang), diagnostics.QualifiedProfileId);
            AddAdminConsoleFact(details, ClientUiText.Get("admin.console.local.previous_build", lang), diagnostics.PreviousBuild);
            AddAdminConsoleFact(details, ClientUiText.Get("admin.console.local.required_build", lang), diagnostics.RequiredBuild);
            AddAdminConsoleFact(details, ClientUiText.Get("admin.console.local.qualified_at", lang), FormatAdminConsoleTimestamp(diagnostics.QualifiedAtUtc));
            if (diagnostics.RecentEvents.Count > 0)
            {
                details.Children.Add(BuildAdminConsoleSubHeader(ClientUiText.Get("admin.console.local.events", lang)));
                foreach (var item in diagnostics.RecentEvents.Take(5))
                {
                    details.Children.Add(new TextBlock
                    {
                        Text = $"{item.At.ToLocalTime():g} | {ResolveRuntimeEventLabel(item.EventKind, lang)} | {item.Build ?? "-"}",
                        Foreground = UseLightPalette() ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE),
                        TextWrapping = TextWrapping.WrapWholeWords
                    });
                }
            }

            body.Children.Add(BuildDialogSurfaceCard(details, new Thickness(14)));
            SetAdminConsolePage(ClientUiText.Get("admin.console.local.title", lang), ClientUiText.Get("admin.console.local.subtitle", lang), body);
        }
        catch (Exception ex)
        {
            if (!IsAdminConsoleRenderCurrent(AdminConsoleSection.LocalAssistant, renderVersion))
                return;
            ClientLog.Exception("AdminConsole.LocalAssistant", ex);
            SetAdminConsoleError(ClientUiText.Get("admin.console.local.failed", lang), FormatAdminLoadErrorForUser(ex, "/local/runtime", lang));
        }
    }

    private void RenderAdminConsoleDocumentTools()
    {
        var lang = UiLang;
        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(BuildDialogInfoBanner(ClientUiText.Get("admin.console.tools.help", lang), positive: true));

        var toolsGrid = new Grid
        {
            ColumnSpacing = 12,
            RowSpacing = 12
        };
        toolsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        AddAdminConsoleDocumentToolCard(
            toolsGrid,
            0,
            0,
            ClientUiText.Get("cmd.catalog.categories", lang),
            ClientUiText.Get("admin.console.tools.categories.help", lang),
            ClientUiText.Get("admin.console.tools.open_in_chat", lang),
            async () => await RunAdminConsoleDocumentToolAsync(
                DirectCommandCatalog.CatalogCategoriesList,
                new { },
                ClientUiText.BuildPromptCategories(lang)).ConfigureAwait(true),
            primary: true);

        AddAdminConsoleDocumentToolCard(
            toolsGrid,
            0,
            1,
            ClientUiText.Get("cmd.catalog.tree", lang),
            ClientUiText.Get("admin.console.tools.tree.help", lang),
            ClientUiText.Get("admin.console.tools.open_in_chat", lang),
            async () => await RunAdminConsoleDocumentToolAsync(
                DirectCommandCatalog.CatalogTreeView,
                new { depth = 12, format = "markdown" },
                ClientUiText.BuildPromptCatalogTree(lang)).ConfigureAwait(true));

        AddAdminConsoleDocumentToolCard(
            toolsGrid,
            1,
            0,
            ClientUiText.Get("cmd.catalog.stats", lang),
            ClientUiText.Get("admin.console.tools.stats.help", lang),
            ClientUiText.Get("admin.console.tools.open_in_chat", lang),
            async () => await RunAdminConsoleDocumentToolAsync(
                DirectCommandCatalog.CatalogStatsView,
                new { },
                ClientUiText.BuildPromptCatalogStats(lang)).ConfigureAwait(true));

        AddAdminConsoleDocumentToolCard(
            toolsGrid,
            1,
            1,
            ClientUiText.Get("cmd.guided.document_search", lang),
            ClientUiText.Get("admin.console.tools.search.help", lang),
            ClientUiText.Get("admin.console.tools.open_help", lang),
            async () =>
            {
                CloseAdminConsoleWindow();
                HelpButton_Click(this, new RoutedEventArgs());
                await Task.CompletedTask.ConfigureAwait(true);
            });

        body.Children.Add(toolsGrid);
        body.Children.Add(BuildDialogInfoBanner(ClientUiText.Get("admin.console.tools.note", lang)));
        SetAdminConsolePage(ClientUiText.Get("admin.console.tools.title", lang), ClientUiText.Get("admin.console.tools.subtitle", lang), body);
    }

    private void AddAdminConsoleDocumentToolCard(
        Grid host,
        int row,
        int column,
        string title,
        string description,
        string buttonText,
        Func<Task> onClick,
        bool primary = false)
    {
        while (host.RowDefinitions.Count <= row)
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(BuildAdminConsoleSubHeader(title));
        stack.Children.Add(new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.WrapWholeWords,
            Foreground = UseLightPalette() ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE)
        });

        var button = BuildDialogFooterButton(buttonText, primary: primary);
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.MinWidth = 180;
        button.Click += async (_, __) => await onClick().ConfigureAwait(true);
        stack.Children.Add(button);

        var card = BuildDialogSurfaceCard(stack, new Thickness(14));
        Grid.SetRow(card, row);
        Grid.SetColumn(card, column);
        host.Children.Add(card);
    }

    private async Task RunAdminConsoleDocumentToolAsync(string commandId, object args, string displayText)
    {
        CloseAdminConsoleWindow();
        await TryExecuteHelpDirectCommandAsync(commandId, args, displayText).ConfigureAwait(true);
    }

    private async Task RenderAdminConsoleJobsAsync(AdminJobsLaunchMode launchMode)
    {
        var lang = UiLang;
        if (_adminConsoleContentHost is null)
            return;

        _adminConsoleJobsContext = BuildAdminConsoleJobsPage(lang);
        ApplyAdminJobsLaunchMode(_adminConsoleJobsContext, launchMode, null);
        UpdateAdminConsoleJobsCategoryButtonState(_adminConsoleJobsContext);
        UpdateAdminJobsRefreshControls(_adminConsoleJobsContext);
        UpdateAdminConsoleJobsRefreshTimer();
        await RefreshAdminJobsOverlayAsync(_adminConsoleJobsContext, AdminConsoleToken).ConfigureAwait(true);
    }

    private AdminJobsOverlayContext BuildAdminConsoleJobsPage(string lang)
    {
        var searchBox = new TextBox
        {
            PlaceholderText = ClientUiText.Get("admin.jobs.search.placeholder", lang),
            MinWidth = 260,
            MaxWidth = 460,
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var dateFieldCombo = BuildAdminConsoleCombo(
            (ClientUiText.Get("admin.jobs.filter.date.finished", lang), "finished"),
            (ClientUiText.Get("admin.jobs.filter.date.started", lang), "started"),
            (ClientUiText.Get("admin.jobs.filter.date.created", lang), "created"));
        var datePresetCombo = BuildAdminConsoleCombo(
            (ClientUiText.Get("admin.jobs.filter.range.all", lang), "all"),
            (ClientUiText.Get("admin.jobs.filter.range.today", lang), "today"),
            (ClientUiText.Get("admin.jobs.filter.range.custom", lang), "custom"));
        var sortDirectionCombo = BuildAdminConsoleCombo(
            (ClientUiText.Get("admin.jobs.filter.sort.newest", lang), "desc"),
            (ClientUiText.Get("admin.jobs.filter.sort.oldest", lang), "asc"));

        var typeCombo = BuildAdminConsoleCombo(
            (ClientUiText.Get("admin.jobs.filter.all", lang), string.Empty),
            (ClientUiText.Get("admin.jobs.filter.ingestion", lang), "ingestion"),
            (ClientUiText.Get("admin.jobs.filter.summary", lang), "summary"));
        typeCombo.Visibility = Visibility.Collapsed;

        var statusCombo = BuildAdminConsoleCombo((ClientUiText.Get("admin.jobs.filter.all", lang), "all"));
        statusCombo.Visibility = Visibility.Collapsed;

        var dateFromPicker = new CalendarDatePicker
        {
            PlaceholderText = ClientUiText.Get("admin.jobs.filter.from", lang),
            MinWidth = 140,
            MinHeight = 44,
            Visibility = Visibility.Collapsed
        };
        var dateToPicker = new CalendarDatePicker
        {
            PlaceholderText = ClientUiText.Get("admin.jobs.filter.to", lang),
            MinWidth = 140,
            MinHeight = 44,
            Visibility = Visibility.Collapsed
        };
        ApplyDialogInputChrome(searchBox);
        ApplyDialogInputChrome(dateFieldCombo);
        ApplyDialogInputChrome(datePresetCombo);
        ApplyDialogInputChrome(sortDirectionCombo);
        ApplyDialogInputChrome(typeCombo);
        ApplyDialogInputChrome(statusCombo);
        ApplyDialogInputChrome(dateFromPicker);
        ApplyDialogInputChrome(dateToPicker);

        var ingestionCategoryButton = BuildDialogInlineButton(ClientUiText.Get("admin.jobs.filter.ingestion", lang));
        var summaryCategoryButton = BuildDialogInlineButton(ClientUiText.Get("admin.jobs.filter.summary", lang));
        var autoRefreshToggle = new ToggleSwitch
        {
            Header = ClientUiText.Get("admin.jobs.auto_refresh", lang),
            IsOn = true,
            MinWidth = 160,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var refreshButton = BuildDialogFooterButton(ClientUiText.Get("button.refresh", lang), primary: true);
        var capabilityAKpiButton = BuildDialogInlineButton(ClientUiText.Get("admin.runtime.action.open_a_kpis", lang));
        var capabilityBQualityButton = BuildDialogInlineButton(ClientUiText.Get("admin.runtime.action.open_b_quality", lang));
        var deleteSelectionButton = BuildDialogFooterButton(ClientUiText.Get("admin.jobs.delete_selection", lang), destructive: true);
        var purgeButton = BuildDialogFooterButton(ClientUiText.Get("admin.jobs.purge", lang));
        var summaryText = new TextBlock
        {
            Text = ClientUiText.Get("admin.jobs.loading", lang),
            Foreground = UseLightPalette() ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE),
            TextWrapping = TextWrapping.WrapWholeWords
        };
        var selectionText = new TextBlock
        {
            Text = ClientUiText.Get("admin.jobs.selection.none", lang),
            Foreground = UseLightPalette() ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.WrapWholeWords
        };
        var metricsHost = new StackPanel { Spacing = 8 };
        var groupsHost = new StackPanel { Spacing = 12 };
        var detailsHost = new StackPanel { Spacing = 10 };
        var detailsScrollViewer = new ScrollViewer
        {
            Content = detailsHost,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var detailsCard = BuildDialogSurfaceCard(detailsScrollViewer, new Thickness(12));
        detailsCard.Visibility = Visibility.Collapsed;
        var detailsColumn = new ColumnDefinition { Width = new GridLength(0) };

        var contentGrid = new Grid { ColumnSpacing = 14 };
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        contentGrid.ColumnDefinitions.Add(detailsColumn);

        var listScroll = new ScrollViewer
        {
            Content = groupsHost,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetColumn(listScroll, 0);
        Grid.SetColumn(detailsCard, 1);
        contentGrid.Children.Add(listScroll);
        contentGrid.Children.Add(detailsCard);

        var filterGrid = new Grid { ColumnSpacing = 10, RowSpacing = 10 };
        filterGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        filterGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        filterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(searchBox, 0);
        Grid.SetColumn(ingestionCategoryButton, 1);
        Grid.SetColumn(summaryCategoryButton, 2);
        Grid.SetColumn(dateFieldCombo, 3);
        Grid.SetColumn(datePresetCombo, 4);
        Grid.SetColumn(sortDirectionCombo, 5);
        Grid.SetColumn(refreshButton, 6);
        Grid.SetRow(dateFromPicker, 1);
        Grid.SetRow(dateToPicker, 1);
        Grid.SetColumn(dateFromPicker, 3);
        Grid.SetColumn(dateToPicker, 4);
        filterGrid.Children.Add(searchBox);
        filterGrid.Children.Add(ingestionCategoryButton);
        filterGrid.Children.Add(summaryCategoryButton);
        filterGrid.Children.Add(dateFieldCombo);
        filterGrid.Children.Add(datePresetCombo);
        filterGrid.Children.Add(sortDirectionCombo);
        filterGrid.Children.Add(refreshButton);
        filterGrid.Children.Add(dateFromPicker);
        filterGrid.Children.Add(dateToPicker);

        var page = new Grid
        {
            Padding = new Thickness(18),
            RowSpacing = 12
        };
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var headerGrid = new Grid { ColumnSpacing = 12 };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var header = new StackPanel { Spacing = 6 };
        header.Children.Add(new TextBlock
        {
            Text = ClientUiText.Get("admin.jobs.title", lang),
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            Foreground = UseLightPalette() ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xF5, 0xF7, 0xFB),
            TextWrapping = TextWrapping.WrapWholeWords
        });
        header.Children.Add(summaryText);
        var headerActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        headerActions.Children.Add(capabilityAKpiButton);
        headerActions.Children.Add(capabilityBQualityButton);
        headerActions.Children.Add(autoRefreshToggle);

        Grid.SetColumn(header, 0);
        Grid.SetColumn(headerActions, 1);
        headerGrid.Children.Add(header);
        headerGrid.Children.Add(headerActions);

        Grid.SetRow(headerGrid, 0);
        Grid.SetRow(metricsHost, 1);
        Grid.SetRow(filterGrid, 2);
        var helpBanner = BuildDialogInfoBanner(ClientUiText.Get("admin.jobs.help.body", lang));
        Grid.SetRow(helpBanner, 3);
        Grid.SetRow(contentGrid, 4);

        var footer = new Grid { ColumnSpacing = 10 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(selectionText, 0);
        Grid.SetColumn(deleteSelectionButton, 1);
        Grid.SetColumn(purgeButton, 2);
        footer.Children.Add(selectionText);
        footer.Children.Add(deleteSelectionButton);
        footer.Children.Add(purgeButton);
        Grid.SetRow(footer, 5);

        page.Children.Add(headerGrid);
        page.Children.Add(metricsHost);
        page.Children.Add(filterGrid);
        page.Children.Add(helpBanner);
        page.Children.Add(contentGrid);
        page.Children.Add(footer);

        _adminConsoleContentHost!.Content = page;

        var context = new AdminJobsOverlayContext
        {
            Shell = new Border(),
            LifecycleToken = AdminConsoleToken,
            SummaryText = summaryText,
            SelectionText = selectionText,
            SearchBox = searchBox,
            DateFieldCombo = dateFieldCombo,
            DatePresetCombo = datePresetCombo,
            SortDirectionCombo = sortDirectionCombo,
            DateFromPicker = dateFromPicker,
            DateToPicker = dateToPicker,
            TypeCombo = typeCombo,
            IngestionCategoryButton = ingestionCategoryButton,
            SummaryCategoryButton = summaryCategoryButton,
            StatusCombo = statusCombo,
            AutoRefreshToggle = autoRefreshToggle,
            CapabilityAKpiButton = capabilityAKpiButton,
            CapabilityBQualityButton = capabilityBQualityButton,
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

        searchBox.TextChanged += (_, __) => RenderAdminJobsOverlay(context);
        ingestionCategoryButton.Click += async (_, __) =>
        {
            context.IncludeIngestionCategory = !context.IncludeIngestionCategory;
            context.SelectedJobId = null;
            UpdateAdminConsoleJobsCategoryButtonState(context);
            await RefreshAdminJobsOverlayAsync(context, context.LifecycleToken).ConfigureAwait(true);
        };
        summaryCategoryButton.Click += async (_, __) =>
        {
            context.IncludeSummaryCategory = !context.IncludeSummaryCategory;
            context.SelectedJobId = null;
            UpdateAdminConsoleJobsCategoryButtonState(context);
            await RefreshAdminJobsOverlayAsync(context, context.LifecycleToken).ConfigureAwait(true);
        };
        dateFieldCombo.SelectionChanged += async (_, __) => await RefreshAdminJobsOverlayAsync(context, context.LifecycleToken).ConfigureAwait(true);
        datePresetCombo.SelectionChanged += async (_, __) => await RefreshAdminJobsOverlayAsync(context, context.LifecycleToken).ConfigureAwait(true);
        sortDirectionCombo.SelectionChanged += async (_, __) => await RefreshAdminJobsOverlayAsync(context, context.LifecycleToken).ConfigureAwait(true);
        dateFromPicker.DateChanged += async (_, __) => await RefreshAdminJobsOverlayAsync(context, context.LifecycleToken).ConfigureAwait(true);
        dateToPicker.DateChanged += async (_, __) => await RefreshAdminJobsOverlayAsync(context, context.LifecycleToken).ConfigureAwait(true);
        refreshButton.Click += async (_, __) => await RefreshAdminJobsOverlayAsync(context, context.LifecycleToken).ConfigureAwait(true);
        autoRefreshToggle.Toggled += (_, __) =>
        {
            UpdateAdminJobsRefreshControls(context);
            UpdateAdminConsoleJobsRefreshTimer();
        };
        capabilityAKpiButton.Click += async (_, __) => await SelectAdminConsoleSectionAsync(AdminConsoleSection.Index).ConfigureAwait(true);
        capabilityBQualityButton.Click += async (_, __) => await SelectAdminConsoleSectionAsync(AdminConsoleSection.Summaries).ConfigureAwait(true);
        deleteSelectionButton.Click += async (_, __) => await DeleteSelectedAdminJobsAsync(context).ConfigureAwait(true);
        purgeButton.Flyout = BuildAdminJobsPurgeFlyout(context);

        return context;
    }

    private ComboBox BuildAdminConsoleCombo(params (string Label, string Tag)[] items)
    {
        var combo = new ComboBox
        {
            MinWidth = 132,
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        foreach (var item in items)
            combo.Items.Add(new ComboBoxItem { Content = item.Label, Tag = item.Tag });
        combo.SelectedIndex = 0;
        return combo;
    }

    private static string? GetAdminConsoleComboTag(ComboBox combo)
        => (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    private static string FormatAdminConsoleMetric(int? value, string? unit = null)
    {
        if (value is null)
            return "-";

        var formatted = value.Value.ToString("N0", CultureInfo.CurrentCulture);
        return string.IsNullOrWhiteSpace(unit) ? formatted : $"{formatted} {unit}";
    }

    private static string FormatAdminConsoleScore(double? score)
        => score is null ? "-" : score.Value.ToString("0.000", CultureInfo.CurrentCulture);

    private static string FormatAdminConsoleRetrievalMethodList(JsonElement values, string lang)
    {
        if (values.ValueKind != JsonValueKind.Array)
            return "-";

        var labels = values
            .EnumerateArray()
            .Select(static item => item.GetString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(value => HumanizeAdminConsoleRetrievalMethod(value, lang))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return labels.Length == 0 ? "-" : string.Join(", ", labels);
    }

    private static string? HumanizeAdminConsoleRetrievalMethod(string? value, string lang)
    {
        var normalized = NormalizeAdminConsoleToken(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        if (normalized.Contains("busy", StringComparison.Ordinal)
            || normalized.Contains("throttle", StringComparison.Ordinal)
            || normalized.Contains("timeout", StringComparison.Ordinal))
        {
            return ClientUiText.Get("admin.console.retrieval.method.busy", lang);
        }

        if (normalized.Contains("exact", StringComparison.Ordinal)
            || normalized.Contains("title", StringComparison.Ordinal)
            || normalized.Contains("resolve", StringComparison.Ordinal))
        {
            return ClientUiText.Get("admin.console.retrieval.method.exact", lang);
        }

        if (normalized.Contains("bm25", StringComparison.Ordinal)
            || normalized.Contains("sparse", StringComparison.Ordinal)
            || normalized.Contains("keyword", StringComparison.Ordinal)
            || normalized.Contains("lexical", StringComparison.Ordinal))
        {
            return ClientUiText.Get("admin.console.retrieval.method.keywords", lang);
        }

        if (normalized.Contains("dense", StringComparison.Ordinal)
            || normalized.Contains("vector", StringComparison.Ordinal)
            || normalized.Contains("qdrant", StringComparison.Ordinal)
            || normalized.Contains("semantic", StringComparison.Ordinal))
        {
            return ClientUiText.Get("admin.console.retrieval.method.vector", lang);
        }

        if (normalized.Contains("profile", StringComparison.Ordinal)
            || normalized.Contains("card", StringComparison.Ordinal)
            || normalized.Contains("summary", StringComparison.Ordinal))
        {
            return ClientUiText.Get("admin.console.retrieval.method.profile", lang);
        }

        if (normalized.Contains("linked", StringComparison.Ordinal)
            || normalized.Contains("source", StringComparison.Ordinal))
        {
            return ClientUiText.Get("admin.console.retrieval.method.linked", lang);
        }

        if (normalized.Contains("rrf", StringComparison.Ordinal)
            || normalized.Contains("rank", StringComparison.Ordinal)
            || normalized.Contains("fusion", StringComparison.Ordinal))
        {
            return ClientUiText.Get("admin.console.retrieval.method.fusion", lang);
        }

        if (normalized.Contains("select", StringComparison.Ordinal)
            || normalized.Contains("autocut", StringComparison.Ordinal)
            || normalized.Contains("final", StringComparison.Ordinal))
        {
            return ClientUiText.Get("admin.console.retrieval.method.selection", lang);
        }

        return ClientUiText.Format("admin.console.retrieval.method.unknown", lang, value?.Trim() ?? normalized);
    }

    private static int? SumAdminConsoleMetric(JsonElement metrics, params string[] propertyNames)
    {
        var hasValue = false;
        var total = 0;
        foreach (var propertyName in propertyNames)
        {
            var value = TryGetInt(metrics, propertyName);
            if (value is null)
                continue;

            hasValue = true;
            total += Math.Max(0, value.Value);
        }

        return hasValue ? total : null;
    }

    private static string? HumanizeAdminConsoleGuidanceBehavior(string? value, string lang)
    {
        var normalized = NormalizeAdminConsoleToken(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        if (normalized.Contains("clarification", StringComparison.Ordinal)
            || normalized.Contains("clarify", StringComparison.Ordinal)
            || normalized.Contains("ask", StringComparison.Ordinal))
        {
            return ClientUiText.Get("admin.console.retrieval.guidance.behavior.clarify", lang);
        }

        if (normalized.Contains("caveat", StringComparison.Ordinal)
            || normalized.Contains("qualified", StringComparison.Ordinal))
        {
            return ClientUiText.Get("admin.console.retrieval.guidance.behavior.caveat", lang);
        }

        if (normalized.Contains("answer", StringComparison.Ordinal))
            return ClientUiText.Get("admin.console.retrieval.guidance.behavior.answer", lang);

        return ClientUiText.Get("admin.console.retrieval.guidance.behavior.unknown", lang);
    }

    private static string? HumanizeAdminConsoleResponseShape(string? value, string lang)
    {
        var normalized = NormalizeAdminConsoleToken(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        if (normalized.Contains("no_source", StringComparison.Ordinal)
            || normalized.Contains("no_match", StringComparison.Ordinal))
        {
            return ClientUiText.Get("admin.console.retrieval.guidance.shape.no_source", lang);
        }

        if (normalized.Contains("clarify", StringComparison.Ordinal))
            return ClientUiText.Get("admin.console.retrieval.guidance.shape.clarify", lang);

        if (normalized.Contains("list", StringComparison.Ordinal)
            || normalized.Contains("options", StringComparison.Ordinal)
            || normalized.Contains("compare", StringComparison.Ordinal)
            || normalized.Contains("planning", StringComparison.Ordinal))
        {
            return ClientUiText.Get("admin.console.retrieval.guidance.shape.structured", lang);
        }

        if (normalized.Contains("qualified", StringComparison.Ordinal)
            || normalized.Contains("answer", StringComparison.Ordinal))
        {
            return ClientUiText.Get("admin.console.retrieval.guidance.shape.answer", lang);
        }

        return ClientUiText.Get("admin.console.retrieval.guidance.shape.unknown", lang);
    }

    private static string? HumanizeAdminConsoleGuidanceReason(string? value, string lang)
    {
        var normalized = NormalizeAdminConsoleToken(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        if (normalized.Contains("no_relevant_source", StringComparison.Ordinal))
            return ClientUiText.Get("admin.console.retrieval.guidance.reason.no_source", lang);

        if (normalized.Contains("quality", StringComparison.Ordinal)
            || normalized.Contains("ocr", StringComparison.Ordinal)
            || normalized.Contains("manual_review", StringComparison.Ordinal)
            || normalized.Contains("confidence", StringComparison.Ordinal))
        {
            return ClientUiText.Get("admin.console.retrieval.guidance.reason.quality", lang);
        }

        if (normalized.Contains("scope", StringComparison.Ordinal)
            || normalized.Contains("ambiguous", StringComparison.Ordinal)
            || normalized.Contains("identifier", StringComparison.Ordinal))
        {
            return ClientUiText.Get("admin.console.retrieval.guidance.reason.scope", lang);
        }

        if (normalized.Contains("safety", StringComparison.Ordinal)
            || normalized.Contains("compliance", StringComparison.Ordinal)
            || normalized.Contains("risk", StringComparison.Ordinal))
        {
            return ClientUiText.Get("admin.console.retrieval.guidance.reason.risk", lang);
        }

        if (normalized.Contains("documented", StringComparison.Ordinal)
            || normalized.Contains("relevant", StringComparison.Ordinal))
        {
            return ClientUiText.Get("admin.console.retrieval.guidance.reason.relevant", lang);
        }

        return ClientUiText.Get("admin.console.retrieval.guidance.reason.unknown", lang);
    }

    private static string? HumanizeAdminConsoleContentRole(string? value, string lang)
    {
        var normalized = NormalizeAdminConsoleToken(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        if (normalized.Contains("navigation", StringComparison.Ordinal))
            return ClientUiText.Get("admin.console.retrieval.role.navigation", lang);

        if (normalized.Contains("mixed", StringComparison.Ordinal))
            return ClientUiText.Get("admin.console.retrieval.role.mixed", lang);

        if (normalized.Contains("content", StringComparison.Ordinal)
            || normalized.Contains("body", StringComparison.Ordinal))
        {
            return ClientUiText.Get("admin.console.retrieval.role.content", lang);
        }

        return ClientUiText.Get("admin.console.retrieval.role.unknown", lang);
    }

    private static string HumanizeAdminConsoleRetrievalPhaseName(string? value, string lang)
    {
        var normalized = NormalizeAdminConsoleToken(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return ClientUiText.Get("admin.console.retrieval.diagnostics.phase.unknown", lang);

        if (normalized.Contains("exact", StringComparison.Ordinal))
            return ClientUiText.Get("admin.console.retrieval.phase.exact", lang);
        if (normalized.Contains("sparse", StringComparison.Ordinal) || normalized.Contains("bm25", StringComparison.Ordinal))
            return ClientUiText.Get("admin.console.retrieval.phase.sparse", lang);
        if (normalized.Contains("dense", StringComparison.Ordinal) || normalized.Contains("qdrant", StringComparison.Ordinal))
            return ClientUiText.Get("admin.console.retrieval.phase.dense", lang);
        if (normalized.Contains("profile", StringComparison.Ordinal))
            return ClientUiText.Get("admin.console.retrieval.phase.profile", lang);
        if (normalized.Contains("fusion", StringComparison.Ordinal) || normalized.Contains("rrf", StringComparison.Ordinal))
            return ClientUiText.Get("admin.console.retrieval.phase.fusion", lang);
        if (normalized.Contains("rerank", StringComparison.Ordinal))
            return ClientUiText.Get("admin.console.retrieval.phase.rerank", lang);
        if (normalized.Contains("selection", StringComparison.Ordinal))
            return ClientUiText.Get("admin.console.retrieval.phase.selection", lang);
        if (normalized.Contains("title", StringComparison.Ordinal))
            return ClientUiText.Get("admin.console.retrieval.diagnostics.phase.title", lang);

        return ClientUiText.Get("admin.console.retrieval.diagnostics.phase.unknown", lang);
    }

    private static string? HumanizeAdminConsoleQualityStatus(string? value, string lang)
    {
        var normalized = NormalizeAdminConsoleToken(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        if (normalized.Contains("manual_review", StringComparison.Ordinal)
            || normalized.Contains("probable_ocr_noise", StringComparison.Ordinal)
            || normalized.Contains("text_not_indexed", StringComparison.Ordinal))
        {
            return ClientUiText.Get("admin.console.retrieval.quality.review", lang);
        }

        if (normalized.Contains("failed", StringComparison.Ordinal)
            || normalized.Contains("insufficient", StringComparison.Ordinal)
            || normalized.Contains("missing", StringComparison.Ordinal)
            || normalized.Contains("disabled", StringComparison.Ordinal)
            || normalized.Contains("no_indexable", StringComparison.Ordinal)
            || normalized.Contains("empty_text", StringComparison.Ordinal)
            || normalized.Contains("low_text", StringComparison.Ordinal))
        {
            return ClientUiText.Get("admin.console.retrieval.quality.incomplete", lang);
        }

        if (normalized.Contains("ocr_applied_ok_with_page_warnings", StringComparison.Ordinal)
            || normalized.Contains("with_page_warnings", StringComparison.Ordinal))
        {
            return ClientUiText.Get("admin.console.retrieval.quality.ocr_warning", lang);
        }

        if (normalized.Contains("ocr", StringComparison.Ordinal))
            return ClientUiText.Get("admin.console.retrieval.quality.ocr", lang);

        if (normalized.Contains("ok", StringComparison.Ordinal)
            || normalized.Contains("indexed_by_context", StringComparison.Ordinal)
            || normalized.Contains("low_value_text", StringComparison.Ordinal))
        {
            return ClientUiText.Get("admin.console.retrieval.quality.ok", lang);
        }

        return ClientUiText.Format("admin.console.retrieval.quality.unknown", lang, value?.Trim() ?? normalized);
    }

    private static string NormalizeAdminConsoleToken(string? value)
        => (value ?? string.Empty)
            .Trim()
            .Replace('-', '_')
            .Replace(' ', '_')
            .ToLowerInvariant();

    private static string? TryGetStringFromNested(JsonElement element, string objectName, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(objectName, out var nested)
            || nested.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return TryGetString(nested, propertyName);
    }

    private static string? ShortenAdminConsoleHash(string? hash)
    {
        var trimmed = (hash ?? string.Empty).Trim();
        if (trimmed.Length <= 12)
            return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;

        return trimmed[..12];
    }

    private static string TrimAdminConsoleText(string text, int maxLength)
    {
        var normalized = (text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        normalized = string.Join(" ", normalized.Split('\n').Select(static line => line.Trim()).Where(static line => line.Length > 0));
        if (normalized.Length <= maxLength)
            return normalized;

        return normalized[..Math.Max(0, maxLength - 1)].TrimEnd() + "…";
    }

    private void UpdateAdminConsoleJobsCategoryButtonState(AdminJobsOverlayContext context)
    {
        ApplyAdminConsoleCategoryButtonVisual(context.IngestionCategoryButton, context.IncludeIngestionCategory);
        ApplyAdminConsoleCategoryButtonVisual(context.SummaryCategoryButton, context.IncludeSummaryCategory);
    }

    private void ApplyAdminConsoleCategoryButtonVisual(Button button, bool selected)
    {
        button.BorderThickness = new Thickness(selected ? 2 : 1);
        button.Opacity = selected ? 1d : 0.82d;
        if (selected)
        {
            ApplyInlineButtonStatusAccent(button, "running");
            return;
        }

        var light = UseLightPalette();
        var background = light ? UiBrush(0xF7, 0xFA, 0xFD) : UiBrush(0x14, 0x19, 0x22);
        var border = light ? UiBrush(0xC8, 0xD5, 0xE3) : UiBrush(0x2E, 0x38, 0x45);
        var foreground = light ? UiBrush(0x19, 0x24, 0x33) : UiBrush(0xE6, 0xEC, 0xF4);
        button.Background = background;
        button.BorderBrush = border;
        button.Foreground = foreground;
        button.Resources["ButtonBackgroundPointerOver"] = background;
        button.Resources["ButtonBackgroundPressed"] = background;
        button.Resources["ButtonBorderBrushPointerOver"] = border;
        button.Resources["ButtonBorderBrushPressed"] = border;
        button.Resources["ButtonForegroundPointerOver"] = foreground;
        button.Resources["ButtonForegroundPressed"] = foreground;
    }

    private void UpdateAdminConsoleJobsRefreshTimer()
    {
        StopAdminConsoleJobsRefreshTimer();
        var context = _adminConsoleJobsContext;
        if (_adminConsoleWindow is null || context is null || context.AutoRefreshToggle.IsOn != true)
            return;

        _adminConsoleJobsRefreshTimer = DispatcherQueue.CreateTimer();
        _adminConsoleJobsRefreshTimer.Interval = TimeSpan.FromSeconds(3);
        _adminConsoleJobsRefreshTimer.Tick += async (_, __) =>
        {
            var currentContext = _adminConsoleJobsContext;
            if (currentContext is null
                || !ReferenceEquals(currentContext, context)
                || currentContext.LifecycleToken.IsCancellationRequested)
                return;
            if (currentContext.IsRefreshing)
            {
                currentContext.RefreshPending = true;
                return;
            }
            try
            {
                await RefreshAdminJobsOverlayAsync(currentContext, currentContext.LifecycleToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (currentContext.LifecycleToken.IsCancellationRequested)
            {
            }
        };
        _adminConsoleJobsRefreshTimer.Start();
    }

    private void StopAdminConsoleJobsRefreshTimer()
    {
        try { _adminConsoleJobsRefreshTimer?.Stop(); } catch { }
        _adminConsoleJobsRefreshTimer = null;
    }

    private async Task RequalifyAdminConsoleRuntimeAsync()
    {
        var lang = UiLang;
        var renderVersion = ++_adminConsoleRenderVersion;
        SetAdminConsoleLoading(ClientUiText.Get("admin.runtime.action.requalify.loading", lang));
        try
        {
            var response = await _api.AdminRuntimeRequalifyAsync(null, null, true, AdminConsoleToken).ConfigureAwait(true);
            if (!IsAdminConsoleRenderCurrent(AdminConsoleSection.Overview, renderVersion))
                return;

            Status(BuildAdminRuntimeRequalifySummaryMessage(response, lang));
            await SelectAdminConsoleSectionAsync(AdminConsoleSection.Overview).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (!IsAdminConsoleRenderCurrent(AdminConsoleSection.Overview, renderVersion))
                return;
            ClientLog.Exception("AdminConsole.Requalify", ex);
            SetAdminConsoleError(ClientUiText.Get("admin.runtime.action.requalify.failed", lang), BuildAdminRuntimeActionErrorDetail(ex, lang));
        }
    }

    private async Task PreviewAdminConsoleIndexFixesAsync()
    {
        var lang = UiLang;
        var renderVersion = ++_adminConsoleRenderVersion;
        SetAdminConsoleLoading(ClientUiText.Get("admin.runtime.action.preview_offsets.loading", lang));
        try
        {
            var result = await LoadCapabilityAOffsetBackfillPreviewAsync(lang, AdminConsoleToken).ConfigureAwait(true);
            if (!IsAdminConsoleRenderCurrent(AdminConsoleSection.Index, renderVersion))
                return;

            var body = new StackPanel { Spacing = 14 };
            body.Children.Add(BuildDialogInfoBanner(result.Message, result.Positive));
            var backButton = BuildDialogFooterButton(ClientUiText.Get("admin.console.back_to_index", lang), primary: true);
            backButton.Click += async (_, __) => await SelectAdminConsoleSectionAsync(AdminConsoleSection.Index).ConfigureAwait(true);
            body.Children.Add(backButton);
            SetAdminConsolePage(ClientUiText.Get("admin.runtime.action.preview_offsets", lang), ClientUiText.Get("admin.runtime.kpi_a.title", lang), body);
        }
        catch (Exception ex)
        {
            if (!IsAdminConsoleRenderCurrent(AdminConsoleSection.Index, renderVersion))
                return;
            ClientLog.Exception("AdminConsole.PreviewIndexFixes", ex);
            SetAdminConsoleError(ClientUiText.Get("admin.runtime.action.preview_offsets.failed", lang), BuildAdminRuntimeActionErrorDetail(ex, lang));
        }
    }

    private bool IsAdminConsoleRenderCurrent(AdminConsoleSection section, long renderVersion)
        => _adminConsoleContentHost is not null
           && _adminConsoleActiveSection == section
           && _adminConsoleRenderVersion == renderVersion
           && !AdminConsoleToken.IsCancellationRequested;

    private CancellationToken AdminConsoleToken => _adminConsoleCts?.Token ?? CancellationToken.None;

    private void SetAdminConsoleLoading(string message)
    {
        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(BuildDialogInfoBanner(message));
        SetAdminConsolePage(ClientUiText.Get("admin.console.title", UiLang), string.Empty, body);
    }

    private void SetAdminConsoleError(string title, string detail)
    {
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(BuildDialogInfoBanner(title));
        if (!string.IsNullOrWhiteSpace(detail))
            body.Children.Add(BuildDialogInfoBanner(detail));
        SetAdminConsolePage(title, string.Empty, body);
    }

    private void SetAdminConsolePage(string title, string? subtitle, Panel body)
    {
        if (_adminConsoleContentHost is null)
            return;

        var page = new Grid
        {
            Padding = new Thickness(22),
            RowSpacing = 14
        };
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = BuildAdminConsoleHeader(title, subtitle);
        Grid.SetRow(header, 0);

        var scroll = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroll, 1);

        page.Children.Add(header);
        page.Children.Add(scroll);
        _adminConsoleContentHost.Content = page;
    }

    private FrameworkElement BuildAdminConsoleHeader(string title, string? subtitle)
    {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            Foreground = UseLightPalette() ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xF5, 0xF7, 0xFB),
            TextWrapping = TextWrapping.WrapWholeWords
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            stack.Children.Add(new TextBlock
            {
                Text = subtitle,
                Foreground = UseLightPalette() ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE),
                FontSize = 14,
                TextWrapping = TextWrapping.WrapWholeWords
            });
        }

        return stack;
    }

    private FrameworkElement BuildAdminConsoleMetricsGrid(IReadOnlyList<(string Label, string Value)> metrics)
    {
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        for (var i = 0; i < 4; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var rows = Math.Max(1, (int)Math.Ceiling(metrics.Count / 4d));
        for (var i = 0; i < rows; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var i = 0; i < metrics.Count; i++)
        {
            var card = BuildAdminConsoleMetricTile(metrics[i].Label, metrics[i].Value);
            Grid.SetColumn(card, i % 4);
            Grid.SetRow(card, i / 4);
            grid.Children.Add(card);
        }

        return grid;
    }

    private FrameworkElement BuildAdminConsoleMetricTile(string label, string value)
        => BuildDialogSurfaceCard(new StackPanel
        {
            Spacing = 5,
            Children =
            {
                new TextBlock
                {
                    Text = label,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 12,
                    Foreground = UseLightPalette() ? UiBrush(0x5D, 0x6E, 0x82) : UiBrush(0xA8, 0xB5, 0xC7),
                    TextWrapping = TextWrapping.WrapWholeWords
                },
                new TextBlock
                {
                    Text = value,
                    FontSize = 24,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = UseLightPalette() ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xF5, 0xF7, 0xFB),
                    TextWrapping = TextWrapping.WrapWholeWords
                }
            }
        }, new Thickness(14));

    private FrameworkElement BuildAdminConsoleCapabilityCard(AdminRuntimeOperationalItem item)
    {
        var stack = new StackPanel { Spacing = 8 };
        var title = string.Equals(item.Key, "capability_a.corpus_enrichment", StringComparison.OrdinalIgnoreCase)
            ? ClientUiText.Get("admin.runtime.section.capability_a", UiLang)
            : string.Equals(item.Key, "capability_b.backoffice_generation", StringComparison.OrdinalIgnoreCase)
                ? ClientUiText.Get("admin.runtime.section.capability_b", UiLang)
                : ClientUiText.Get("admin.runtime.section.capability_other", UiLang);
        stack.Children.Add(BuildAdminConsoleSubHeader(title));
        AddAdminConsoleFact(stack, ClientUiText.Get("admin.runtime.field.status", UiLang), ResolveCapabilityStatusLabel(item.Status, UiLang));
        AddAdminConsoleFact(stack, ClientUiText.Get("admin.runtime.field.selected", UiLang), item.Selected ? ClientUiText.Get("admin.jobs.value.yes", UiLang) : ClientUiText.Get("admin.jobs.value.no", UiLang));
        AddAdminConsoleFact(stack, ClientUiText.Get("admin.runtime.fact.backlog", UiLang), item.Summary.CandidateCount.ToString(CultureInfo.InvariantCulture));
        AddAdminConsoleFact(stack, ClientUiText.Get("admin.runtime.fact.ready", UiLang), item.Summary.ReadyToEnqueueCount.ToString(CultureInfo.InvariantCulture));
        if (item.Recommendations.Count > 0)
            AddAdminConsoleFact(stack, ClientUiText.Get("admin.runtime.field.recommendations", UiLang), string.Join(Environment.NewLine, item.Recommendations.Take(3).Select(x => ResolveCapabilityRecommendationLabel(x, UiLang))));
        return BuildDialogSurfaceCard(stack, new Thickness(14));
    }

    private FrameworkElement BuildAdminConsoleQualityReviewPreview(AdminRuntimeCapabilityBQualitySnapshot snapshot, string lang)
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(BuildAdminConsoleSubHeader(ClientUiText.Get("admin.console.summaries.quality_preview", lang)));

        if (snapshot.Items.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = ClientUiText.Get("admin.runtime.quality.empty", lang),
                Foreground = UseLightPalette() ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE),
                TextWrapping = TextWrapping.WrapWholeWords
            });
            return BuildDialogSurfaceCard(stack, new Thickness(14));
        }

        foreach (var item in snapshot.Items.Take(5))
        {
            var itemStack = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
            itemStack.Children.Add(new TextBlock
            {
                Text = item.DocName,
                FontWeight = FontWeights.SemiBold,
                Foreground = UseLightPalette() ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xF5, 0xF7, 0xFB),
                TextWrapping = TextWrapping.WrapWholeWords
            });
            itemStack.Children.Add(new TextBlock
            {
                Text = ClientUiText.Format(
                    "admin.console.summaries.quality_item",
                    lang,
                    item.QualityScore.ToString("0.00", CultureInfo.InvariantCulture),
                    ResolveQualitySeverityLabel(item.Severity, lang),
                    ResolveQualityActionLabel(item.RecommendedAction, lang)),
                Foreground = UseLightPalette() ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE),
                TextWrapping = TextWrapping.WrapWholeWords
            });
            stack.Children.Add(itemStack);
        }

        return BuildDialogSurfaceCard(stack, new Thickness(14));
    }

    private static string ResolveQualitySeverityLabel(string? severity, string lang)
        => (severity ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "critical" => LocalRuntimeText("critique", "critical", "crítico", "crítico", "kritisch", "critico", lang),
            "high" => LocalRuntimeText("élevée", "high", "alta", "alta", "hoch", "alta", lang),
            "medium" => LocalRuntimeText("moyenne", "medium", "media", "média", "mittel", "media", lang),
            "low" => LocalRuntimeText("faible", "low", "baja", "baixa", "niedrig", "bassa", lang),
            _ => LocalRuntimeText("à vérifier", "to review", "por revisar", "a verificar", "zu prüfen", "da verificare", lang)
        };

    private static string ResolveQualityActionLabel(string? action, string lang)
        => (action ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "stabilize_runtime_then_regenerate" => LocalRuntimeText("stabiliser le moteur puis régénérer", "stabilize the engine, then regenerate", "estabilizar el motor y regenerar", "estabilizar o motor e regenerar", "Engine stabilisieren, dann neu generieren", "stabilizzare il motore e rigenerare", lang),
            "regenerate_with_context_review" => LocalRuntimeText("vérifier le contexte puis régénérer", "check context, then regenerate", "revisar el contexto y regenerar", "verificar o contexto e regenerar", "Kontext prüfen, dann neu generieren", "controllare il contesto e rigenerare", lang),
            "regenerate_summary" => LocalRuntimeText("régénérer le résumé", "regenerate the summary", "regenerar el resumen", "regenerar o resumo", "Zusammenfassung neu generieren", "rigenerare il riepilogo", lang),
            "manual_review" => LocalRuntimeText("revue manuelle", "manual review", "revisión manual", "revisão manual", "manuelle Prüfung", "revisione manuale", lang),
            _ => LocalRuntimeText("à vérifier", "to review", "por revisar", "a verificar", "zu prüfen", "da verificare", lang)
        };

    private TextBlock BuildAdminConsoleSubHeader(string text)
        => new()
        {
            Text = text,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = UseLightPalette() ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xF5, 0xF7, 0xFB),
            TextWrapping = TextWrapping.WrapWholeWords
        };

    private void AddAdminConsoleFact(Panel host, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var labelBlock = new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold,
            Foreground = UseLightPalette() ? UiBrush(0x5D, 0x6E, 0x82) : UiBrush(0xA8, 0xB5, 0xC7),
            TextWrapping = TextWrapping.WrapWholeWords
        };
        var valueBlock = new TextBlock
        {
            Text = value,
            Foreground = UseLightPalette() ? UiBrush(0x19, 0x24, 0x33) : UiBrush(0xF2, 0xF5, 0xFA),
            TextWrapping = TextWrapping.WrapWholeWords
        };
        Grid.SetColumn(labelBlock, 0);
        Grid.SetColumn(valueBlock, 1);
        row.Children.Add(labelBlock);
        row.Children.Add(valueBlock);
        host.Children.Add(row);
    }

    private static string FormatAdminConsoleTimestamp(DateTimeOffset? value)
        => value?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "-";
}
