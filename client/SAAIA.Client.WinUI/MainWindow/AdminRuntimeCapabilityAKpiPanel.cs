using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow
{
    private OverlayDialogSession? _activeCapabilityAKpiOverlay;

    private sealed record AdminRuntimeCapabilityAKpiSnapshot(
        string Environment,
        DateTimeOffset? GeneratedAt,
        AdminRuntimeCapabilityAKpiPolicy Policy,
        AdminRuntimeCapabilityALiveSnapshot Live,
        IReadOnlyList<AdminRuntimeCapabilityAKpiMetric> Metrics,
        IReadOnlyList<AdminRuntimeCapabilityAKpiAlert> Alerts,
        IReadOnlyList<string> DashboardPanels);

    private sealed record AdminRuntimeCapabilityAKpiPolicy(
        int ObservationWindowMinutes,
        double OperationP95TargetMs,
        double SkipRateTargetPercent,
        double ReadyToEnqueueRateTargetPercent,
        double OffsetBackfillShareTargetPercent,
        string? Notes);

    private sealed record AdminRuntimeCapabilityAKpiMetric(
        string Key,
        string Instrument,
        string Aggregation,
        string Unit,
        string Description,
        IReadOnlyList<string> Tags);

    private sealed record AdminRuntimeCapabilityAKpiAlert(
        string Key,
        string Severity,
        string Condition,
        string RecommendedAction);

    private sealed record AdminRuntimeCapabilityALiveSnapshot(
        int BacklogCount,
        int ReadyCount,
        int OffsetBackfillCount,
        double ReadyRatePercent,
        double OffsetBackfillSharePercent,
        bool IsAvailable,
        string? StatusMessage,
        IReadOnlyList<string> Recommendations);

    private async Task ShowCapabilityAKpiOverlayAsync()
    {
        if (!_api.HasAdminKey)
        {
            Status(ClientUiText.Get("admin.jobs.no_admin", UiLang));
            return;
        }

        if (_activeCapabilityAKpiOverlay is not null)
            return;

        var lang = UiLang;
        var generatedText = new TextBlock
        {
            Foreground = UseLightPalette() ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE),
            TextWrapping = TextWrapping.WrapWholeWords
        };
        var stateHost = new ContentPresenter();

        var metricsGrid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        for (var i = 0; i < 4; i++)
            metricsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metricsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var liveGrid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        for (var i = 0; i < 4; i++)
            liveGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        liveGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var notesHost = new StackPanel { Spacing = 10 };
        var trackedMetricsHost = new StackPanel { Spacing = 12 };
        var alertsHost = new StackPanel { Spacing = 12 };
        var panelsHost = new StackPanel { Spacing = 8 };
        var listScroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 420,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    trackedMetricsHost,
                    alertsHost,
                    panelsHost
                }
            }
        };

        var refreshButton = BuildDialogFooterButton(ClientUiText.Get("admin.runtime.kpi_a.refresh", lang), primary: true);
        var previewOffsetsButton = BuildDialogFooterButton(ClientUiText.Get("admin.runtime.action.preview_offsets", lang));
        var openJobsButton = BuildDialogFooterButton(ClientUiText.Get("admin.runtime.action.open_a_jobs", lang));
        var closeButton = BuildDialogFooterButton(ClientUiText.Get("dialog.close", lang));
        var footer = BuildDialogFooter(refreshButton, previewOffsetsButton, openJobsButton, closeButton);

        OverlayDialogSession? overlay = null;
        using var overlayCts = new CancellationTokenSource();
        var isLoading = false;
        AdminRuntimeCapabilityAKpiSnapshot? currentSnapshot = null;

        void SetStateBanner(string text, bool positive = false)
            => stateHost.Content = BuildDialogInfoBanner(text, positive);

        void SetBusy(bool busy)
        {
            isLoading = busy;
            refreshButton.IsEnabled = !busy;
            previewOffsetsButton.IsEnabled = !busy && currentSnapshot?.Live.OffsetBackfillCount > 0;
            openJobsButton.IsEnabled = !busy;
            closeButton.IsEnabled = !busy;
        }

        async Task PreviewOffsetBackfillAsync()
        {
            if (isLoading)
                return;

            SetBusy(true);
            SetStateBanner(ClientUiText.Get("admin.runtime.action.preview_offsets.loading", lang));

            try
            {
                var preview = await LoadCapabilityAOffsetBackfillPreviewAsync(lang, overlayCts.Token).ConfigureAwait(true);
                SetStateBanner(preview.Message, positive: preview.Positive);
                Status(preview.Message);
            }
            catch (Exception ex)
            {
                ClientLog.Exception("AdminRuntimeCapabilityAKpi.PreviewOffsets", ex);
                var message = ClientUiText.Get("admin.runtime.action.preview_offsets.failed", lang);
                SetStateBanner(message);
                Status(message + " " + ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        FrameworkElement BuildMetricTile(string label, string value)
        {
            var valueBrush = UseLightPalette() ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xF5, 0xF7, 0xFB);
            var labelBrush = UseLightPalette() ? UiBrush(0x5D, 0x6E, 0x82) : UiBrush(0xA8, 0xB5, 0xC7);
            return BuildDialogSurfaceCard(new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock
                    {
                        Text = label,
                        Foreground = labelBrush,
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        TextWrapping = TextWrapping.WrapWholeWords
                    },
                    new TextBlock
                    {
                        Text = value,
                        Foreground = valueBrush,
                        FontSize = 24,
                        FontWeight = FontWeights.SemiBold
                    }
                }
            }, new Thickness(14));
        }

        FrameworkElement BuildTextCard(string title, string body, string? tone = null)
        {
            var stack = new StackPanel { Spacing = 6 };
            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontWeight = FontWeights.SemiBold,
                Foreground = UseLightPalette() ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xF5, 0xF7, 0xFB),
                TextWrapping = TextWrapping.WrapWholeWords
            });
            stack.Children.Add(new TextBlock
            {
                Text = body,
                Foreground = UseLightPalette() ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE),
                TextWrapping = TextWrapping.WrapWholeWords
            });

            if (!string.IsNullOrWhiteSpace(tone))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = tone,
                    Foreground = UseLightPalette() ? UiBrush(0x7A, 0x4B, 0x12) : UiBrush(0xF3, 0xC4, 0x83),
                    FontSize = 12,
                    TextWrapping = TextWrapping.WrapWholeWords
                });
            }

            return BuildDialogSurfaceCard(stack, new Thickness(14));
        }

        void RenderPolicy(AdminRuntimeCapabilityAKpiPolicy policy)
        {
            metricsGrid.Children.Clear();
            var tiles = new[]
            {
                BuildMetricTile(
                    ClientUiText.Get("admin.runtime.kpi_a.metric.operation_p95", lang),
                    policy.OperationP95TargetMs.ToString("0", CultureInfo.InvariantCulture) + " ms"),
                BuildMetricTile(
                    ClientUiText.Get("admin.runtime.kpi_a.metric.skip_rate", lang),
                    policy.SkipRateTargetPercent.ToString("0.##", CultureInfo.InvariantCulture) + "%"),
                BuildMetricTile(
                    ClientUiText.Get("admin.runtime.kpi_a.metric.ready_rate", lang),
                    policy.ReadyToEnqueueRateTargetPercent.ToString("0.##", CultureInfo.InvariantCulture) + "%"),
                BuildMetricTile(
                    ClientUiText.Get("admin.runtime.kpi_a.metric.offset_backfill", lang),
                    policy.OffsetBackfillShareTargetPercent.ToString("0.##", CultureInfo.InvariantCulture) + "%")
            };

            for (var index = 0; index < tiles.Length; index++)
            {
                Grid.SetColumn(tiles[index], index);
                Grid.SetRow(tiles[index], 0);
                metricsGrid.Children.Add(tiles[index]);
            }
        }

        void RenderLive(AdminRuntimeCapabilityALiveSnapshot live)
        {
            liveGrid.Children.Clear();
            var tiles = new[]
            {
                BuildMetricTile(
                    ClientUiText.Get("admin.runtime.kpi_a.metric.current_backlog", lang),
                    live.BacklogCount.ToString(CultureInfo.InvariantCulture)),
                BuildMetricTile(
                    ClientUiText.Get("admin.runtime.kpi_a.metric.current_ready", lang),
                    live.ReadyCount.ToString(CultureInfo.InvariantCulture)),
                BuildMetricTile(
                    ClientUiText.Get("admin.runtime.kpi_a.metric.current_ready_rate", lang),
                    live.ReadyRatePercent.ToString("0.##", CultureInfo.InvariantCulture) + "%"),
                BuildMetricTile(
                    ClientUiText.Get("admin.runtime.kpi_a.metric.current_offset_share", lang),
                    live.OffsetBackfillSharePercent.ToString("0.##", CultureInfo.InvariantCulture) + "%")
            };

            for (var index = 0; index < tiles.Length; index++)
            {
                Grid.SetColumn(tiles[index], index);
                Grid.SetRow(tiles[index], 0);
                liveGrid.Children.Add(tiles[index]);
            }
        }

        void Render(AdminRuntimeCapabilityAKpiSnapshot snapshot)
        {
            currentSnapshot = snapshot;
            generatedText.Text = snapshot.GeneratedAt.HasValue
                ? ClientUiText.Format("admin.runtime.generated", lang, snapshot.GeneratedAt.Value.ToLocalTime().ToString("g"))
                : string.Empty;
            SetStateBanner(
                ClientUiText.Format(
                    "admin.runtime.kpi_a.state_ready",
                    lang,
                    snapshot.Environment,
                    snapshot.Policy.ObservationWindowMinutes.ToString(CultureInfo.InvariantCulture)),
                positive: true);

            RenderPolicy(snapshot.Policy);
            RenderLive(snapshot.Live);

            notesHost.Children.Clear();
            notesHost.Children.Add(new TextBlock
            {
                Text = ClientUiText.Get("admin.runtime.kpi_a.section.current", lang),
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = UseLightPalette() ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xF5, 0xF7, 0xFB)
            });
            if (snapshot.Live.IsAvailable)
            {
                notesHost.Children.Add(BuildDialogSurfaceCard(liveGrid, new Thickness(12)));
            }
            else
            {
                notesHost.Children.Add(BuildDialogInfoBanner(
                    snapshot.Live.StatusMessage ?? ClientUiText.Get("admin.runtime.kpi_a.live_unavailable", lang)));
            }

            if (!string.IsNullOrWhiteSpace(snapshot.Policy.Notes))
            {
                notesHost.Children.Add(BuildTextCard(
                    ClientUiText.Get("admin.runtime.kpi_a.section.notes", lang),
                    snapshot.Policy.Notes));
            }

            if (snapshot.Live.IsAvailable)
            {
                var readyDelta = snapshot.Live.ReadyRatePercent - snapshot.Policy.ReadyToEnqueueRateTargetPercent;
                var offsetDelta = snapshot.Live.OffsetBackfillSharePercent - snapshot.Policy.OffsetBackfillShareTargetPercent;
                var comparisonBody = string.Join(
                    "\n",
                    ClientUiText.Format(
                        "admin.runtime.kpi_a.compare.ready_rate",
                        lang,
                        snapshot.Live.ReadyRatePercent.ToString("0.##", CultureInfo.InvariantCulture),
                        snapshot.Policy.ReadyToEnqueueRateTargetPercent.ToString("0.##", CultureInfo.InvariantCulture),
                        readyDelta >= 0
                            ? ClientUiText.Get("admin.runtime.kpi_a.compare.on_target", lang)
                            : ClientUiText.Get("admin.runtime.kpi_a.compare.watch", lang)),
                    ClientUiText.Format(
                        "admin.runtime.kpi_a.compare.offset_share",
                        lang,
                        snapshot.Live.OffsetBackfillSharePercent.ToString("0.##", CultureInfo.InvariantCulture),
                        snapshot.Policy.OffsetBackfillShareTargetPercent.ToString("0.##", CultureInfo.InvariantCulture),
                        offsetDelta <= 0
                            ? ClientUiText.Get("admin.runtime.kpi_a.compare.on_target", lang)
                            : ClientUiText.Get("admin.runtime.kpi_a.compare.watch", lang)));
                notesHost.Children.Add(BuildTextCard(
                    ClientUiText.Get("admin.runtime.kpi_a.section.compare", lang),
                    comparisonBody));

                if (snapshot.Live.Recommendations.Count > 0)
                {
                    var recommendationBody = string.Join("\n", snapshot.Live.Recommendations.Select(static item => "- " + item));
                    notesHost.Children.Add(BuildTextCard(
                        ClientUiText.Get("admin.runtime.field.recommendations", lang),
                        recommendationBody));
                }
            }

            trackedMetricsHost.Children.Clear();
            trackedMetricsHost.Children.Add(new TextBlock
            {
                Text = ClientUiText.Get("admin.runtime.kpi_a.section.metrics", lang),
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = UseLightPalette() ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xF5, 0xF7, 0xFB)
            });
            if (snapshot.Metrics.Count == 0)
            {
                trackedMetricsHost.Children.Add(BuildDialogInfoBanner(ClientUiText.Get("admin.runtime.kpi_a.empty_metrics", lang)));
            }
            else
            {
                foreach (var metric in snapshot.Metrics)
                {
                    var tags = metric.Tags.Count > 0
                        ? string.Join(", ", metric.Tags)
                        : "n/a";
                    var metricBody = string.Join(
                        "\n",
                        metric.Description,
                        string.Empty,
                        ClientUiText.Format("admin.runtime.kpi_a.fact.instrument", lang, metric.Instrument),
                        ClientUiText.Format("admin.runtime.kpi_a.fact.aggregation", lang, metric.Aggregation),
                        ClientUiText.Format("admin.runtime.kpi_a.fact.unit", lang, metric.Unit),
                        ClientUiText.Format("admin.runtime.kpi_a.fact.tags", lang, tags));
                    trackedMetricsHost.Children.Add(BuildTextCard(
                        metric.Key,
                        metricBody));
                }
            }

            alertsHost.Children.Clear();
            alertsHost.Children.Add(new TextBlock
            {
                Text = ClientUiText.Get("admin.runtime.kpi_a.section.alerts", lang),
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = UseLightPalette() ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xF5, 0xF7, 0xFB)
            });
            if (snapshot.Alerts.Count == 0)
            {
                alertsHost.Children.Add(BuildDialogInfoBanner(ClientUiText.Get("admin.runtime.kpi_a.empty_alerts", lang), positive: true));
            }
            else
            {
                foreach (var alert in snapshot.Alerts)
                {
                    alertsHost.Children.Add(BuildTextCard(
                        alert.Key,
                        $"{alert.Condition}\n\n{alert.RecommendedAction}",
                        tone: ClientUiText.Format("admin.runtime.quality.fact.severity", lang, alert.Severity)));
                }
            }

            panelsHost.Children.Clear();
            panelsHost.Children.Add(new TextBlock
            {
                Text = ClientUiText.Get("admin.runtime.kpi_a.section.panels", lang),
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = UseLightPalette() ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xF5, 0xF7, 0xFB)
            });

            foreach (var panel in snapshot.DashboardPanels)
            {
                panelsHost.Children.Add(BuildDialogSurfaceCard(new TextBlock
                {
                    Text = panel,
                    TextWrapping = TextWrapping.WrapWholeWords,
                    Foreground = UseLightPalette() ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE)
                }, new Thickness(14)));
            }

            previewOffsetsButton.IsEnabled = !isLoading && snapshot.Live.OffsetBackfillCount > 0;
        }

        async Task LoadAsync()
        {
            if (isLoading)
                return;

            SetBusy(true);
            generatedText.Text = string.Empty;
            metricsGrid.Children.Clear();
            liveGrid.Children.Clear();
            notesHost.Children.Clear();
            trackedMetricsHost.Children.Clear();
            alertsHost.Children.Clear();
            panelsHost.Children.Clear();
            SetStateBanner(ClientUiText.Get("admin.runtime.kpi_a.loading", lang));

            try
            {
                var json = await _api.AdminRuntimeCapabilityAKpisAsync(overlayCts.Token).ConfigureAwait(true);
                JsonElement? operationalJson = null;
                string? liveUnavailableMessage = null;

                try
                {
                    operationalJson = await _api.AdminRuntimeOperationalSummaryAsync(overlayCts.Token).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    ClientLog.Exception("AdminRuntimeCapabilityAKpi.Live", ex);
                    liveUnavailableMessage = ClientUiText.Get("admin.runtime.kpi_a.live_unavailable", lang);
                }

                var snapshot = ParseAdminRuntimeCapabilityAKpiSnapshot(json, operationalJson, liveUnavailableMessage);
                Render(snapshot);
            }
            catch (Exception ex)
            {
                ClientLog.Exception("AdminRuntimeCapabilityAKpi.Load", ex);
                SetStateBanner(ClientUiText.Get("admin.runtime.kpi_a.load_failed", lang));
                trackedMetricsHost.Children.Add(BuildDialogInfoBanner(ex.Message));
            }
            finally
            {
                SetBusy(false);
            }
        }

        refreshButton.Click += async (_, __) => await LoadAsync().ConfigureAwait(true);
        previewOffsetsButton.Click += async (_, __) => await PreviewOffsetBackfillAsync().ConfigureAwait(true);
        openJobsButton.Click += async (_, __) =>
        {
            overlay?.Close();
            await ShowAdminJobsOverlayAsync(launchMode: AdminJobsLaunchMode.CapabilityAEnrichment).ConfigureAwait(true);
        };
        closeButton.Click += (_, __) => overlay?.Close();

        overlay = ShowOverlayDialog(
            BuildScrollableDialogShell(
                ClientUiText.Get("help.section.admin", lang),
                ClientUiText.Get("admin.runtime.kpi_a.title", lang),
                ClientUiText.Get("admin.runtime.kpi_a.subtitle", lang),
                new UIElement[]
                {
                    generatedText,
                    stateHost,
                    BuildDialogSurfaceCard(metricsGrid, new Thickness(12)),
                    BuildDialogSurfaceCard(notesHost, new Thickness(12)),
                    BuildDialogSurfaceCard(listScroller, new Thickness(12))
                },
                footer,
                maxWidth: 980,
                maxHeight: 760),
            closeOnBackgroundTap: true);

        _activeCapabilityAKpiOverlay = overlay;

        try
        {
            await LoadAsync().ConfigureAwait(true);
            await overlay.Completion.ConfigureAwait(true);
        }
        finally
        {
            overlayCts.Cancel();
            if (ReferenceEquals(_activeCapabilityAKpiOverlay, overlay))
                _activeCapabilityAKpiOverlay = null;
        }
    }

    private static AdminRuntimeCapabilityAKpiSnapshot ParseAdminRuntimeCapabilityAKpiSnapshot(
        JsonElement root,
        JsonElement? operationalRoot,
        string? liveUnavailableMessage)
    {
        JsonElement policyElement = default;
        _ = TryGetPropertyIgnoreCase(root, "policy", out policyElement);

        var metrics = new List<AdminRuntimeCapabilityAKpiMetric>();
        if (TryGetPropertyIgnoreCase(root, "metrics", out var metricsElement) && metricsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var metric in metricsElement.EnumerateArray())
            {
                metrics.Add(new AdminRuntimeCapabilityAKpiMetric(
                    Key: TryGetString(metric, "key") ?? string.Empty,
                    Instrument: TryGetString(metric, "instrument") ?? string.Empty,
                    Aggregation: TryGetString(metric, "aggregation") ?? string.Empty,
                    Unit: TryGetString(metric, "unit") ?? string.Empty,
                    Description: TryGetString(metric, "description") ?? string.Empty,
                    Tags: ReadStringArray(metric, "tags")));
            }
        }

        var alerts = new List<AdminRuntimeCapabilityAKpiAlert>();
        if (TryGetPropertyIgnoreCase(root, "alerts", out var alertsElement) && alertsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var alert in alertsElement.EnumerateArray())
            {
                alerts.Add(new AdminRuntimeCapabilityAKpiAlert(
                    Key: TryGetString(alert, "key") ?? string.Empty,
                    Severity: TryGetString(alert, "severity") ?? string.Empty,
                    Condition: TryGetString(alert, "condition") ?? string.Empty,
                    RecommendedAction: TryGetString(alert, "recommendedAction") ?? string.Empty));
            }
        }

        var liveSnapshot = BuildLiveSnapshot(operationalRoot, liveUnavailableMessage);

        return new AdminRuntimeCapabilityAKpiSnapshot(
            Environment: TryGetString(root, "environment") ?? string.Empty,
            GeneratedAt: TryGetDateTimeOffset(root, "generatedAt"),
            Policy: new AdminRuntimeCapabilityAKpiPolicy(
                ObservationWindowMinutes: policyElement.ValueKind == JsonValueKind.Object ? TryGetInt(policyElement, "observationWindowMinutes") ?? 0 : 0,
                OperationP95TargetMs: policyElement.ValueKind == JsonValueKind.Object ? TryGetDouble(policyElement, "operationP95TargetMs") ?? 0d : 0d,
                SkipRateTargetPercent: policyElement.ValueKind == JsonValueKind.Object ? TryGetDouble(policyElement, "skipRateTargetPercent") ?? 0d : 0d,
                ReadyToEnqueueRateTargetPercent: policyElement.ValueKind == JsonValueKind.Object ? TryGetDouble(policyElement, "readyToEnqueueRateTargetPercent") ?? 0d : 0d,
                OffsetBackfillShareTargetPercent: policyElement.ValueKind == JsonValueKind.Object ? TryGetDouble(policyElement, "offsetBackfillShareTargetPercent") ?? 0d : 0d,
                Notes: policyElement.ValueKind == JsonValueKind.Object ? TryGetString(policyElement, "notes") : null),
            Live: liveSnapshot,
            Metrics: metrics,
            Alerts: alerts,
            DashboardPanels: ReadStringArray(root, "dashboardPanels"));
    }

    private static AdminRuntimeCapabilityALiveSnapshot BuildLiveSnapshot(JsonElement? operationalRoot, string? liveUnavailableMessage)
    {
        if (!operationalRoot.HasValue)
        {
            return new AdminRuntimeCapabilityALiveSnapshot(
                BacklogCount: 0,
                ReadyCount: 0,
                OffsetBackfillCount: 0,
                ReadyRatePercent: 0d,
                OffsetBackfillSharePercent: 0d,
                IsAvailable: false,
                StatusMessage: liveUnavailableMessage,
                Recommendations: Array.Empty<string>());
        }

        var operationalSnapshot = ParseAdminRuntimeOperationalSnapshot(operationalRoot.Value);
        var capabilityAItem = operationalSnapshot.Items.FirstOrDefault(static item =>
            string.Equals(item.Key, "capability_a.corpus_enrichment", StringComparison.Ordinal));
        var backlogCount = operationalSnapshot.Summary.CapabilityACandidateCount;
        var readyCount = operationalSnapshot.Summary.CapabilityAReadyToEnqueueCount;
        var offsetBackfillCount = operationalSnapshot.Summary.CapabilityAOffsetBackfillCandidateCount;
        var readyRatePercent = backlogCount > 0
            ? readyCount * 100d / backlogCount
            : 0d;
        var offsetBackfillSharePercent = backlogCount > 0
            ? offsetBackfillCount * 100d / backlogCount
            : 0d;

        return new AdminRuntimeCapabilityALiveSnapshot(
            BacklogCount: backlogCount,
            ReadyCount: readyCount,
            OffsetBackfillCount: offsetBackfillCount,
            ReadyRatePercent: readyRatePercent,
            OffsetBackfillSharePercent: offsetBackfillSharePercent,
            IsAvailable: true,
            StatusMessage: null,
            Recommendations: capabilityAItem?.Recommendations ?? Array.Empty<string>());
    }
}
