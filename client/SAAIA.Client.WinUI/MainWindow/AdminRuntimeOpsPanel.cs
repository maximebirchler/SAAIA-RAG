using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow
{
    private static readonly string[] CapabilityAOffsetBackfillReasons =
    {
        "retrieval_chunk_offsets_missing",
        "exact_match_offsets_missing"
    };

    private OverlayDialogSession? _activeAdminRuntimeOverlay;

    private sealed record AdminRuntimeOperationalSnapshot(
        string Environment,
        DateTimeOffset? GeneratedAt,
        AdminRuntimeOperationalTotals Summary,
        IReadOnlyList<AdminRuntimeOperationalItem> Items);

    private sealed record AdminRuntimeOperationalTotals(
        int CapabilityACandidateCount,
        int CapabilityAReadyToEnqueueCount,
        int CapabilityAOffsetBackfillCandidateCount,
        int CapabilityBBacklogCount,
        int CapabilityBReadyToEnqueueCount,
        int CapabilityBActiveJobCount,
        int? CapabilityBLatestCampaignProgressPercent);

    private sealed record AdminRuntimeOperationalItem(
        string Key,
        string DisplayName,
        string Status,
        string Family,
        bool Implemented,
        bool Qualified,
        bool Selected,
        bool Stale,
        AdminRuntimeOperationalItemSummary Summary,
        IReadOnlyList<string> Recommendations);

    private sealed record AdminRuntimeOperationalItemSummary(
        int CandidateCount,
        int ReadyToEnqueueCount,
        int BlockedByActiveJobCount,
        int BlockedByCooldownCount,
        int ActiveCapabilityJobCount,
        int TotalCampaignCount,
        int ActiveCampaignCount,
        int TerminalCapabilityJobCount,
        int StoredSummaryCount,
        int? OffsetBackfillCandidateCount,
        int? LatestCampaignProgressPercent);

    private async void HeaderRuntimeButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowAdminRuntimeOverlayAsync();
    }

    private async Task ShowAdminRuntimeOverlayAsync()
    {
        if (!_api.HasAdminKey)
        {
            Status(ClientUiText.Get("admin.jobs.no_admin", UiLang));
            return;
        }

        if (_activeAdminRuntimeOverlay is not null)
            return;

        var lang = UiLang;
        var generatedText = new TextBlock
        {
            Foreground = UseLightPalette() ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE),
            TextWrapping = TextWrapping.WrapWholeWords
        };
        var stateHost = new ContentPresenter();
        var metricsGrid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        for (var i = 0; i < 3; i++)
            metricsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metricsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        metricsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var capabilitiesHost = new StackPanel { Spacing = 12 };

        var refreshButton = BuildDialogFooterButton(ClientUiText.Get("admin.runtime.refresh", lang), primary: true);
        var closeButton = BuildDialogFooterButton(ClientUiText.Get("dialog.close", lang));
        var footer = BuildDialogFooter(refreshButton, closeButton);

        OverlayDialogSession? overlay = null;
        using var overlayCts = new CancellationTokenSource();
        var isLoading = false;
        var actionButtons = new List<(Button Button, bool EnabledWhenIdle)>();

        void RegisterActionButton(Button button, bool enabledWhenIdle)
        {
            actionButtons.Add((button, enabledWhenIdle));
            button.IsEnabled = enabledWhenIdle && !isLoading;
        }

        void SyncActionButtons()
        {
            foreach (var (button, enabledWhenIdle) in actionButtons)
                button.IsEnabled = enabledWhenIdle && !isLoading;
        }

        void SetStateBanner(string text, bool positive = false)
        {
            stateHost.Content = BuildDialogInfoBanner(text, positive);
        }

        void SetBusy(bool busy)
        {
            isLoading = busy;
            refreshButton.IsEnabled = !busy;
            closeButton.IsEnabled = !busy;
            SyncActionButtons();
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
                        FontWeight = FontWeights.SemiBold
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

        void RenderMetrics(AdminRuntimeOperationalTotals summary)
        {
            metricsGrid.Children.Clear();
            var tiles = new[]
            {
                BuildMetricTile(ClientUiText.Get("admin.runtime.metric.a_backlog", lang), summary.CapabilityACandidateCount.ToString()),
                BuildMetricTile(ClientUiText.Get("admin.runtime.metric.a_ready", lang), summary.CapabilityAReadyToEnqueueCount.ToString()),
                BuildMetricTile(ClientUiText.Get("admin.runtime.metric.a_offsets", lang), summary.CapabilityAOffsetBackfillCandidateCount.ToString()),
                BuildMetricTile(ClientUiText.Get("admin.runtime.metric.b_backlog", lang), summary.CapabilityBBacklogCount.ToString()),
                BuildMetricTile(ClientUiText.Get("admin.runtime.metric.b_ready", lang), summary.CapabilityBReadyToEnqueueCount.ToString()),
                BuildMetricTile(ClientUiText.Get("admin.runtime.metric.b_active", lang), summary.CapabilityBActiveJobCount.ToString())
            };

            for (var index = 0; index < tiles.Length; index++)
            {
                Grid.SetColumn(tiles[index], index % 3);
                Grid.SetRow(tiles[index], index / 3);
                metricsGrid.Children.Add(tiles[index]);
            }
        }

        async Task PreviewCapabilityAOffsetBackfillAsync(AdminRuntimeOperationalItem item)
        {
            if (isLoading)
                return;

            SetBusy(true);
            SetStateBanner(ClientUiText.Get("admin.runtime.action.preview_offsets.loading", lang));

            try
            {
                var json = await _api.AdminRuntimeCapabilityAEnqueueAsync(
                    CapabilityAOffsetBackfillReasons,
                    dryRun: true,
                    allowUnsafeCandidates: false,
                    maxCandidates: null,
                    overlayCts.Token).ConfigureAwait(true);

                var candidateCount = TryGetInt(json, "candidateCount") ?? 0;
                var plannedCount = TryGetInt(json, "plannedCount") ?? 0;
                var skippedCount = TryGetInt(json, "skippedCount") ?? 0;
                var previewDocs = new List<string>();

                if (TryGetPropertyIgnoreCase(json, "items", out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var previewItem in itemsElement.EnumerateArray())
                    {
                        var reason = TryGetString(previewItem, "reason");
                        var docPath = TryGetString(previewItem, "docPath");
                        if (!string.Equals(reason, "dry_run_preview", StringComparison.OrdinalIgnoreCase)
                            || string.IsNullOrWhiteSpace(docPath))
                        {
                            continue;
                        }

                        previewDocs.Add(docPath!);
                    }
                }

                var examples = previewDocs
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .ToArray();

                var message = plannedCount > 0
                    ? examples.Length > 0
                        ? ClientUiText.Format(
                            "admin.runtime.action.preview_offsets.result",
                            lang,
                            plannedCount,
                            candidateCount,
                            skippedCount,
                            string.Join(", ", examples))
                        : ClientUiText.Format(
                            "admin.runtime.action.preview_offsets.result_short",
                            lang,
                            plannedCount,
                            candidateCount,
                            skippedCount)
                    : ClientUiText.Format(
                        "admin.runtime.action.preview_offsets.empty",
                        lang,
                        candidateCount,
                        skippedCount);

                SetStateBanner(message, positive: plannedCount > 0);
                Status(message);
            }
            catch (Exception ex)
            {
                ClientLog.Exception($"AdminRuntimeOps.PreviewOffsetBackfill[{item.Key}]", ex);
                var message = ClientUiText.Get("admin.runtime.action.preview_offsets.failed", lang);
                SetStateBanner(message);
                Status(message + " " + ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        async Task OpenCapabilityAJobsAsync()
        {
            overlay?.Close();
            await ShowAdminJobsOverlayAsync(launchMode: AdminJobsLaunchMode.CapabilityAEnrichment).ConfigureAwait(true);
        }

        async Task OpenCapabilityBJobsAsync()
        {
            overlay?.Close();
            await ShowAdminJobsOverlayAsync(launchMode: AdminJobsLaunchMode.CapabilityBBackoffice).ConfigureAwait(true);
        }

        FrameworkElement BuildCapabilityCard(AdminRuntimeOperationalItem item)
        {
            var title = string.Equals(item.Key, "capability_a.corpus_enrichment", StringComparison.Ordinal)
                ? ClientUiText.Get("admin.runtime.section.capability_a", lang)
                : string.Equals(item.Key, "capability_b.backoffice_generation", StringComparison.Ordinal)
                    ? ClientUiText.Get("admin.runtime.section.capability_b", lang)
                    : item.DisplayName;
            var yesNo = ClientUiText.Get(item.Selected ? "admin.jobs.value.yes" : "admin.jobs.value.no", lang);

            var stack = new StackPanel { Spacing = 8 };
            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = UseLightPalette() ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xF5, 0xF7, 0xFB)
            });
            stack.Children.Add(new TextBlock
            {
                Text = $"{ClientUiText.Get("admin.runtime.field.status", lang)}: {item.Status} | {ClientUiText.Get("admin.runtime.field.selected", lang)}: {yesNo}",
                Foreground = UseLightPalette() ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE),
                TextWrapping = TextWrapping.WrapWholeWords
            });

            var facts = new StackPanel { Spacing = 3 };
            facts.Children.Add(new TextBlock { Text = $"{ClientUiText.Get("admin.runtime.fact.backlog", lang)}: {item.Summary.CandidateCount}" });
            facts.Children.Add(new TextBlock { Text = $"{ClientUiText.Get("admin.runtime.fact.ready", lang)}: {item.Summary.ReadyToEnqueueCount}" });
            facts.Children.Add(new TextBlock { Text = $"{ClientUiText.Get("admin.runtime.fact.blocked_active", lang)}: {item.Summary.BlockedByActiveJobCount}" });
            facts.Children.Add(new TextBlock { Text = $"{ClientUiText.Get("admin.runtime.fact.blocked_policy", lang)}: {item.Summary.BlockedByCooldownCount}" });
            if (item.Summary.OffsetBackfillCandidateCount is int offsets)
            {
                facts.Children.Add(new TextBlock
                {
                    Text = $"{ClientUiText.Get("admin.runtime.fact.legacy_offsets", lang)}: {offsets}"
                });
            }

            if (item.Summary.ActiveCapabilityJobCount > 0 || item.Summary.TotalCampaignCount > 0)
            {
                facts.Children.Add(new TextBlock
                {
                    Text = ClientUiText.Format(
                        "admin.runtime.fact.active_jobs_campaigns",
                        lang,
                        item.Summary.ActiveCapabilityJobCount,
                        item.Summary.TotalCampaignCount)
                });
            }

            if (item.Summary.LatestCampaignProgressPercent is int progress)
            {
                facts.Children.Add(new TextBlock
                {
                    Text = ClientUiText.Format("admin.runtime.fact.latest_campaign", lang, progress)
                });
            }

            stack.Children.Add(facts);

            if (item.Recommendations.Count > 0)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = ClientUiText.Get("admin.runtime.field.recommendations", lang),
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 4, 0, 0)
                });

                foreach (var recommendation in item.Recommendations.Take(3))
                {
                    stack.Children.Add(new TextBlock
                    {
                        Text = "- " + recommendation,
                        TextWrapping = TextWrapping.WrapWholeWords,
                        Foreground = UseLightPalette() ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE)
                    });
                }
            }

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 6, 0, 0)
            };

            if (string.Equals(item.Key, "capability_a.corpus_enrichment", StringComparison.Ordinal))
            {
                var canPreviewOffsets =
                    item.Implemented
                    && item.Qualified
                    && item.Selected
                    && !item.Stale
                    && item.Summary.OffsetBackfillCandidateCount.GetValueOrDefault() > 0;

                var previewOffsetsButton = BuildDialogInlineButton(
                    ClientUiText.Get("admin.runtime.action.preview_offsets", lang),
                    accentStatus: canPreviewOffsets ? "queued" : null);
                previewOffsetsButton.Click += async (_, __) =>
                    await PreviewCapabilityAOffsetBackfillAsync(item).ConfigureAwait(true);
                RegisterActionButton(previewOffsetsButton, canPreviewOffsets);
                actions.Children.Add(previewOffsetsButton);

                var canOpenJobs =
                    item.Implemented
                    && (item.Selected
                        || item.Summary.ActiveCapabilityJobCount > 0
                        || item.Summary.TotalCampaignCount > 0
                        || item.Summary.CandidateCount > 0
                        || item.Summary.OffsetBackfillCandidateCount.GetValueOrDefault() > 0);

                var openJobsButton = BuildDialogInlineButton(
                    ClientUiText.Get("admin.runtime.action.open_a_jobs", lang),
                    accentStatus: canOpenJobs ? "running" : null);
                openJobsButton.Click += async (_, __) =>
                    await OpenCapabilityAJobsAsync().ConfigureAwait(true);
                RegisterActionButton(openJobsButton, canOpenJobs);
                actions.Children.Add(openJobsButton);
            }

            if (string.Equals(item.Key, "capability_b.backoffice_generation", StringComparison.Ordinal))
            {
                var canOpenJobs =
                    item.Implemented
                    && (item.Selected
                        || item.Summary.ActiveCapabilityJobCount > 0
                        || item.Summary.TotalCampaignCount > 0
                        || item.Summary.CandidateCount > 0);

                var openJobsButton = BuildDialogInlineButton(
                    ClientUiText.Get("admin.runtime.action.open_b_jobs", lang),
                    accentStatus: canOpenJobs ? "running" : null);
                openJobsButton.Click += async (_, __) =>
                    await OpenCapabilityBJobsAsync().ConfigureAwait(true);
                RegisterActionButton(openJobsButton, canOpenJobs);
                actions.Children.Add(openJobsButton);
            }

            if (actions.Children.Count > 0)
                stack.Children.Add(actions);

            return BuildDialogSurfaceCard(stack, new Thickness(16, 14, 16, 14));
        }

        void Render(AdminRuntimeOperationalSnapshot snapshot)
        {
            generatedText.Text = snapshot.GeneratedAt.HasValue
                ? ClientUiText.Format("admin.runtime.generated", lang, snapshot.GeneratedAt.Value.ToLocalTime().ToString("g"))
                : string.Empty;
            SetStateBanner($"{snapshot.Environment} | {ClientUiText.Get("status.ready", lang)}", positive: true);
            RenderMetrics(snapshot.Summary);
            actionButtons.Clear();
            capabilitiesHost.Children.Clear();

            if (snapshot.Items.Count == 0)
            {
                capabilitiesHost.Children.Add(BuildDialogInfoBanner(ClientUiText.Get("admin.runtime.empty", lang)));
                return;
            }

            foreach (var item in snapshot.Items)
                capabilitiesHost.Children.Add(BuildCapabilityCard(item));

            SyncActionButtons();
        }

        async Task LoadAsync()
        {
            if (isLoading)
                return;

            SetBusy(true);
            generatedText.Text = string.Empty;
            SetStateBanner(ClientUiText.Get("admin.runtime.loading", lang));
            metricsGrid.Children.Clear();
            actionButtons.Clear();
            capabilitiesHost.Children.Clear();

            try
            {
                var json = await _api.AdminRuntimeOperationalSummaryAsync(overlayCts.Token).ConfigureAwait(true);
                var snapshot = ParseAdminRuntimeOperationalSnapshot(json);
                Render(snapshot);
            }
            catch (Exception ex)
            {
                ClientLog.Exception("AdminRuntimeOps.Load", ex);
                SetStateBanner(ClientUiText.Get("admin.runtime.load_failed", lang));
                capabilitiesHost.Children.Clear();
                capabilitiesHost.Children.Add(BuildDialogInfoBanner(ex.Message));
            }
            finally
            {
                SetBusy(false);
            }
        }

        refreshButton.Click += async (_, __) => await LoadAsync().ConfigureAwait(true);
        closeButton.Click += (_, __) => overlay?.Close();

        overlay = ShowOverlayDialog(
            BuildCompactDialogShell(
                ClientUiText.Get("help.section.admin", lang),
                ClientUiText.Get("admin.runtime.title", lang),
                ClientUiText.Get("admin.runtime.subtitle", lang),
                new UIElement[]
                {
                    generatedText,
                    stateHost,
                    BuildDialogSurfaceCard(metricsGrid, new Thickness(12)),
                    BuildDialogSurfaceCard(capabilitiesHost, new Thickness(12))
                },
                footer),
            closeOnBackgroundTap: true);

        _activeAdminRuntimeOverlay = overlay;

        try
        {
            await LoadAsync().ConfigureAwait(true);
            await overlay.Completion.ConfigureAwait(true);
        }
        finally
        {
            overlayCts.Cancel();
            if (ReferenceEquals(_activeAdminRuntimeOverlay, overlay))
                _activeAdminRuntimeOverlay = null;
        }
    }

    private static AdminRuntimeOperationalSnapshot ParseAdminRuntimeOperationalSnapshot(JsonElement root)
    {
        var summary = TryGetPropertyIgnoreCase(root, "summary", out var summaryElement)
            ? new AdminRuntimeOperationalTotals(
                CapabilityACandidateCount: TryGetInt(summaryElement, "capabilityACandidateCount") ?? 0,
                CapabilityAReadyToEnqueueCount: TryGetInt(summaryElement, "capabilityAReadyToEnqueueCount") ?? 0,
                CapabilityAOffsetBackfillCandidateCount: TryGetInt(summaryElement, "capabilityAOffsetBackfillCandidateCount") ?? 0,
                CapabilityBBacklogCount: TryGetInt(summaryElement, "capabilityBBacklogCount") ?? 0,
                CapabilityBReadyToEnqueueCount: TryGetInt(summaryElement, "capabilityBReadyToEnqueueCount") ?? 0,
                CapabilityBActiveJobCount: TryGetInt(summaryElement, "capabilityBActiveJobCount") ?? 0,
                CapabilityBLatestCampaignProgressPercent: TryGetInt(summaryElement, "capabilityBLatestCampaignProgressPercent"))
            : new AdminRuntimeOperationalTotals(0, 0, 0, 0, 0, 0, null);

        var items = new List<AdminRuntimeOperationalItem>();
        if (TryGetPropertyIgnoreCase(root, "items", out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemsElement.EnumerateArray())
            {
                if (!TryGetPropertyIgnoreCase(item, "summary", out var itemSummaryElement))
                    continue;

                var recommendations = ReadStringArray(item, "recommendations");
                items.Add(new AdminRuntimeOperationalItem(
                    Key: TryGetString(item, "key") ?? string.Empty,
                    DisplayName: TryGetString(item, "displayName") ?? TryGetString(item, "key") ?? string.Empty,
                    Status: TryGetString(item, "status") ?? string.Empty,
                    Family: TryGetString(item, "family") ?? string.Empty,
                    Implemented: TryGetBool(item, "implemented") ?? false,
                    Qualified: TryGetBool(item, "qualified") ?? false,
                    Selected: TryGetBool(item, "selected") ?? false,
                    Stale: TryGetBool(item, "stale") ?? false,
                    Summary: new AdminRuntimeOperationalItemSummary(
                        CandidateCount: TryGetInt(itemSummaryElement, "candidateCount") ?? 0,
                        ReadyToEnqueueCount: TryGetInt(itemSummaryElement, "readyToEnqueueCount") ?? 0,
                        BlockedByActiveJobCount: TryGetInt(itemSummaryElement, "blockedByActiveJobCount") ?? 0,
                        BlockedByCooldownCount: TryGetInt(itemSummaryElement, "blockedByCooldownCount") ?? 0,
                        ActiveCapabilityJobCount: TryGetInt(itemSummaryElement, "activeCapabilityJobCount") ?? 0,
                        TotalCampaignCount: TryGetInt(itemSummaryElement, "totalCampaignCount") ?? 0,
                        ActiveCampaignCount: TryGetInt(itemSummaryElement, "activeCampaignCount") ?? 0,
                        TerminalCapabilityJobCount: TryGetInt(itemSummaryElement, "terminalCapabilityJobCount") ?? 0,
                        StoredSummaryCount: TryGetInt(itemSummaryElement, "storedSummaryCount") ?? 0,
                        OffsetBackfillCandidateCount: TryGetInt(itemSummaryElement, "offsetBackfillCandidateCount"),
                        LatestCampaignProgressPercent: TryGetInt(itemSummaryElement, "latestCampaignProgressPercent")),
                    Recommendations: recommendations));
            }
        }

        return new AdminRuntimeOperationalSnapshot(
            Environment: TryGetString(root, "environment") ?? string.Empty,
            GeneratedAt: TryGetDateTimeOffset(root, "generatedAt"),
            Summary: summary,
            Items: items);
    }
}
