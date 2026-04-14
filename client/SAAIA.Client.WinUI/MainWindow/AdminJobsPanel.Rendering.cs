using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow
{
    private void RenderAdminJobsOverlay(AdminJobsOverlayContext context)
    {
        var visibleItems = ApplyAdminJobsFilters(context);
        if (!string.IsNullOrWhiteSpace(context.SelectedJobId)
            && !visibleItems.Any(item => string.Equals(item.JobId, context.SelectedJobId, StringComparison.OrdinalIgnoreCase)))
        {
            context.SelectedJobId = null;
        }

        var renderSignature = BuildAdminJobsRenderSignature(visibleItems);
        var shouldRebuildGroups =
            !string.Equals(context.LastVisibleRenderSignature, renderSignature, StringComparison.Ordinal)
            || !string.Equals(context.SelectedJobId, context.LastRenderedSelectedJobId, StringComparison.OrdinalIgnoreCase);
        RenderAdminJobsMetrics(context, context.Items, visibleItems);
        if (shouldRebuildGroups)
        {
            RenderAdminJobsGroups(context, visibleItems);
            context.LastVisibleRenderSignature = renderSignature;
            context.LastRenderedSelectedJobId = context.SelectedJobId;
        }
        RenderAdminJobsDetails(context);
        UpdateAdminJobsSelectionState(context, visibleItems);
        var loadedCount = (context.IncludeIngestionCategory || context.IncludeSummaryCategory) ? context.Items.Count : 0;
        context.SummaryText.Text = ClientUiText.Format("admin.jobs.visible_summary", UiLang, visibleItems.Count, loadedCount);
    }

    private List<AdminJobListItem> ApplyAdminJobsFilters(AdminJobsOverlayContext context)
    {
        if (HasInvalidAdminJobsDateRange(context))
            return new List<AdminJobListItem>();

        var term = (context.SearchBox.Text ?? string.Empty).Trim();
        IEnumerable<AdminJobListItem> items = context.Items;
        if (!string.IsNullOrWhiteSpace(term))
        {
            items = items.Where(item =>
                item.JobId.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(item.DocPath) && item.DocPath.Contains(term, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(item.LastError) && item.LastError.Contains(term, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(item.JobType) && item.JobType.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        if (context.IncludeIngestionCategory || context.IncludeSummaryCategory)
        {
            items = items.Where(item =>
                (context.IncludeIngestionCategory && string.Equals(item.Type, "ingestion", StringComparison.OrdinalIgnoreCase))
                || (context.IncludeSummaryCategory && string.Equals(item.Type, "summary", StringComparison.OrdinalIgnoreCase)));
        }
        else
        {
            items = Enumerable.Empty<AdminJobListItem>();
        }

        if (context.DateFrom.HasValue || context.DateTo.HasValue)
        {
            items = items.Where(item =>
            {
                var timestamp = GetAdminJobSortTimestamp(item, context.DateField);
                if (!timestamp.HasValue)
                    return false;
                if (context.DateFrom.HasValue && timestamp.Value < context.DateFrom.Value)
                    return false;
                if (context.DateTo.HasValue && timestamp.Value >= context.DateTo.Value)
                    return false;
                return true;
            });
        }

        if (context.SelectedMetricFilters.Count > 0)
        {
            items = items.Where(item => context.SelectedMetricFilters.Any(filterTag => MatchesAdminJobsStatusFilter(item, filterTag)));
        }
        else
        {
            items = Enumerable.Empty<AdminJobListItem>();
        }

        return SortAdminJobs(items, context).ToList();
    }

    private IEnumerable<AdminJobListItem> SortAdminJobs(IEnumerable<AdminJobListItem> items, AdminJobsOverlayContext context)
    {
        var sortDirection = NormalizeAdminJobsSortDirection(context.SortDirection);
        var ascending = string.Equals(sortDirection, "asc", StringComparison.OrdinalIgnoreCase);

        var ordered = items.OrderBy(item => GetAdminJobSortTimestamp(item, context.DateField).HasValue ? 0 : 1);
        if (ascending)
        {
            return ordered
                .ThenBy(item => GetAdminJobSortTimestamp(item, context.DateField) ?? DateTimeOffset.MaxValue)
                .ThenBy(item => item.FinishedAt ?? DateTimeOffset.MaxValue)
                .ThenBy(item => item.StartedAt ?? DateTimeOffset.MaxValue)
                .ThenBy(item => item.CreatedAt ?? DateTimeOffset.MaxValue)
                .ThenBy(item => item.JobId, StringComparer.OrdinalIgnoreCase);
        }

        return ordered
            .ThenByDescending(item => GetAdminJobSortTimestamp(item, context.DateField) ?? DateTimeOffset.MinValue)
            .ThenByDescending(item => item.FinishedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(item => item.StartedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(item => item.CreatedAt ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.JobId, StringComparer.OrdinalIgnoreCase);
    }

    private static DateTimeOffset? GetAdminJobSortTimestamp(AdminJobListItem item, string? dateField)
    {
        var normalized = NormalizeAdminJobsDateField(dateField);
        return normalized switch
        {
            "created" => item.CreatedAt ?? item.StartedAt ?? item.FinishedAt,
            "started" => item.StartedAt ?? item.CreatedAt ?? item.FinishedAt,
            _ => item.FinishedAt ?? item.StartedAt ?? item.CreatedAt
        };
    }

    private void RenderAdminJobsMetrics(AdminJobsOverlayContext context, IReadOnlyList<AdminJobListItem> allItems, IReadOnlyList<AdminJobListItem> visibleItems)
    {
        context.MetricsHost.Children.Clear();
        var metricsSource = (context.IncludeIngestionCategory || context.IncludeSummaryCategory)
            ? allItems.Where(item =>
                (context.IncludeIngestionCategory && string.Equals(item.Type, "ingestion", StringComparison.OrdinalIgnoreCase))
                || (context.IncludeSummaryCategory && string.Equals(item.Type, "summary", StringComparison.OrdinalIgnoreCase)))
                .ToList()
            : new List<AdminJobListItem>();

        var grid = new Grid { ColumnSpacing = 10 };
        for (var i = 0; i < 6; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var metrics = new (string Title, int Value, string Accent, string FilterTag)[]
        {
            (ClientUiText.Get("admin.jobs.metric.queued", UiLang), metricsSource.Count(x => x.IsQueued), "queued", "queued"),
            (ClientUiText.Get("admin.jobs.metric.running", UiLang), metricsSource.Count(x => x.IsRunning), "running", "running"),
            (ClientUiText.Get("admin.jobs.metric.paused", UiLang), metricsSource.Count(x => x.IsPaused), "paused", "paused"),
            (ClientUiText.Get("admin.jobs.metric.failed", UiLang), metricsSource.Count(x => x.IsFailed), "failed", "failed"),
            (ClientUiText.Get("admin.jobs.metric.canceled", UiLang), metricsSource.Count(x => x.IsCanceled), "canceled", "canceled"),
            (ClientUiText.Get("admin.jobs.metric.done", UiLang), metricsSource.Count(x => string.Equals(NormalizeTrackedJobStatus(x.Status), "done", StringComparison.OrdinalIgnoreCase)), "done", "done")
        };

        for (var i = 0; i < metrics.Length; i++)
        {
            var metric = metrics[i];
            var isSelected = context.SelectedMetricFilters.Contains(metric.FilterTag);
            var card = BuildAdminMetricCard(metric.Title, metric.Value, metric.Accent, isSelected);
            card.Tapped += (_, __) =>
            {
                ApplyAdminJobsStatusFilter(context, metric.FilterTag);
                RenderAdminJobsOverlay(context);
            };
            Grid.SetColumn(card, i);
            grid.Children.Add(card);
        }

        context.MetricsHost.Children.Add(grid);
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
              .Append(item.DocumentStatus).Append('|')
              .Append(item.DocumentIngestionVersion?.ToString(CultureInfo.InvariantCulture) ?? "-").Append('|')
              .Append(item.DocumentIndexedVersion?.ToString(CultureInfo.InvariantCulture) ?? "-").Append('|')
              .Append(item.DocumentAutoIngestPaused?.ToString() ?? "-").Append('|')
              .Append(item.DocumentAutoIngestPauseReason).Append('|')
              .Append(item.LastError).Append(';');
        }

        return sb.ToString();
    }

    private static bool MatchesAdminJobsStatusFilter(AdminJobListItem item, string filterTag)
    {
        var normalized = (filterTag ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "active" => !item.IsTerminal && !item.IsPaused,
            "queued" => item.IsQueued,
            "running" => item.IsRunning,
            "cancel_requested" => string.Equals(NormalizeTrackedJobStatus(item.Status), "cancel_requested", StringComparison.OrdinalIgnoreCase),
            "paused" => item.IsPaused,
            "done" => string.Equals(NormalizeTrackedJobStatus(item.Status), "done", StringComparison.OrdinalIgnoreCase),
            "failed" => string.Equals(NormalizeTrackedJobStatus(item.Status), "failed", StringComparison.OrdinalIgnoreCase),
            "canceled" => string.Equals(NormalizeTrackedJobStatus(item.Status), "canceled", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static void ApplyAdminJobsStatusFilter(AdminJobsOverlayContext context, string filterTag)
    {
        context.HasMetricFilterInteraction = true;
        if (context.SelectedMetricFilters.Contains(filterTag))
            context.SelectedMetricFilters.Remove(filterTag);
        else
            context.SelectedMetricFilters.Add(filterTag);

        for (var i = 0; i < context.StatusCombo.Items.Count; i++)
        {
            if (context.StatusCombo.Items[i] is ComboBoxItem cbi
                && string.Equals((cbi.Tag as string) ?? string.Empty, "all", StringComparison.OrdinalIgnoreCase))
            {
                context.StatusCombo.SelectedIndex = i;
                return;
            }
        }
    }

    private Border BuildAdminMetricCard(string title, int value, string accentStatus, bool isSelected)
    {
        var light = UseLightPalette();
        return new Border
        {
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12, 10, 12, 10),
            Background = light ? UiBrush(0xF7, 0xFA, 0xFD) : UiBrush(0x11, 0x16, 0x1E),
            BorderBrush = GetAdminJobStatusBorder(accentStatus, light),
            BorderThickness = new Thickness(isSelected ? 3 : 1),
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        Foreground = light ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE),
                        TextWrapping = TextWrapping.WrapWholeWords,
                        FontSize = 11,
                        CharacterSpacing = 10
                    },
                    new TextBlock
                    {
                        Text = value.ToString(CultureInfo.InvariantCulture),
                        FontSize = 22,
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

        var queued = visibleItems.Where(item => item.IsQueued).ToList();
        var running = visibleItems.Where(item => item.IsRunning).ToList();
        var paused = visibleItems.Where(item => item.IsPaused).ToList();
        var failed = visibleItems.Where(item => item.IsFailed).ToList();
        var canceled = visibleItems.Where(item => item.IsCanceled).ToList();
        var done = visibleItems.Where(item => item.IsTerminal && !item.IsFailed && !item.IsCanceled).ToList();

        if (queued.Count > 0)
            context.GroupsHost.Children.Add(BuildAdminJobsGroupSection(ClientUiText.Get("admin.jobs.metric.queued", UiLang), queued, context));
        if (running.Count > 0)
            context.GroupsHost.Children.Add(BuildAdminJobsGroupSection(ClientUiText.Get("admin.jobs.metric.running", UiLang), running, context));
        if (paused.Count > 0)
            context.GroupsHost.Children.Add(BuildAdminJobsGroupSection(ClientUiText.Get("admin.jobs.metric.paused", UiLang), paused, context));
        if (failed.Count > 0)
            context.GroupsHost.Children.Add(BuildAdminJobsGroupSection(ClientUiText.Get("admin.jobs.metric.failed", UiLang), failed, context));
        if (canceled.Count > 0)
            context.GroupsHost.Children.Add(BuildAdminJobsGroupSection(ClientUiText.Get("admin.jobs.metric.canceled", UiLang), canceled, context));
        if (done.Count > 0)
        {
            var take = Math.Max(50, context.HistoryTake);
            var shown = done.Take(take).ToList();
            context.GroupsHost.Children.Add(BuildAdminJobsGroupSection(ClientUiText.Get("admin.jobs.metric.done", UiLang), shown, context));
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

        var progressBar = BuildAdminJobProgressBar(item);
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


        var normalizedStatus = NormalizeTrackedJobStatus(item.Status);
        var isInitialIngestion = string.Equals(item.JobType, "upsert", StringComparison.OrdinalIgnoreCase)
                                 && (item.DocumentIndexedVersion ?? 0) <= 0;
        var isPauseTransitionPending = IsAdminPauseTransitionPending(item);
        var isCancelRequested = string.Equals(normalizedStatus, "cancel_requested", StringComparison.OrdinalIgnoreCase)
                                || isPauseTransitionPending;

        if (CanResumeAdminJob(item, isCancelRequested))
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
            if (isPauseTransitionPending)
            {
                var pendingButton = BuildDialogInlineButton(
                    ClientUiText.Get(isInitialIngestion ? "admin.jobs.pause" : "admin.jobs.cancel", UiLang) + "...",
                    destructive: !isInitialIngestion,
                    accentStatus: isInitialIngestion ? "paused" : null);
                pendingButton.IsEnabled = false;
                actions.Children.Add(pendingButton);
            }
            else if (!item.IsPaused)
            {
                var cancelButton = BuildDialogInlineButton(
                    ClientUiText.Get(isInitialIngestion ? "admin.jobs.pause" : "admin.jobs.cancel", UiLang),
                    destructive: !isInitialIngestion,
                    accentStatus: isInitialIngestion ? "paused" : null);
                cancelButton.Click += async (_, __) =>
                {
                    try
                    {
                        cancelButton.IsEnabled = false;
                        var optimisticRequestedAction = isInitialIngestion ? "pause" : "cancel";
                        ApplyOptimisticAdminJobAction(context, item, optimisticRequestedAction);
                        var response = isInitialIngestion
                            ? await _api.AdminJobsPauseAsync(item.JobId, CancellationToken.None).ConfigureAwait(true)
                            : await _api.AdminJobsCancelAsync(item.JobId, CancellationToken.None).ConfigureAwait(true);

                        var result = (TryGetString(response, "result") ?? string.Empty).Trim().ToLowerInvariant();
                        var status = NormalizeTrackedJobStatus(TryGetString(response, "status") ?? string.Empty);
                        var requestedAction = (TryGetString(response, "requestedAction") ?? string.Empty).Trim().ToLowerInvariant();
                        var cancelRequested = (TryGetInt(response, "runningCancelRequested") ?? 0) > 0 || status == "cancel_requested" || result == "cancel_requested";
                        var canceled = TryGetPropertyIgnoreCase(response, "canceled", out var canceledEl)
                            && canceledEl.ValueKind is JsonValueKind.True or JsonValueKind.False
                            && canceledEl.GetBoolean();

                        if (cancelRequested && requestedAction == "pause")
                        {
                            Status(ClientUiText.Get("admin.jobs.pause", UiLang) + "...");
                            status = NormalizeTrackedJobStatus(
                                await WaitForAdminJobCancellationSettlementAsync(context, item.JobId, requestedAction, CancellationToken.None).ConfigureAwait(true)
                                ?? status);
                            cancelRequested = status == "cancel_requested";
                        }
                        else if (cancelRequested)
                        {
                            Status(ClientUiText.Get("admin.jobs.cancel_requested", UiLang));
                            status = NormalizeTrackedJobStatus(
                                await WaitForAdminJobCancellationSettlementAsync(context, item.JobId, requestedAction, CancellationToken.None).ConfigureAwait(true)
                                ?? status);
                            cancelRequested = status == "cancel_requested";
                        }
                        else
                        {
                            await RefreshAdminJobsOverlayAsync(context, CancellationToken.None).ConfigureAwait(true);
                        }

                        if (cancelRequested && requestedAction == "pause")
                            Status(ClientUiText.Get("admin.jobs.pause", UiLang) + "...");
                        else if (cancelRequested)
                            Status(ClientUiText.Get("admin.jobs.cancel_requested", UiLang));
                        else if (status == "paused" || result == "paused")
                            Status(ClientUiText.Get("admin.jobs.pause_done", UiLang));
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
                        await RefreshAdminJobsOverlayAsync(context, CancellationToken.None).ConfigureAwait(true);
                        Status(ClientUiText.Get("admin.jobs.refresh_failed", UiLang) + ex.Message);
                    }
                    finally
                    {
                        cancelButton.IsEnabled = true;
                    }
                };
                actions.Children.Add(cancelButton);
            }
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
                RenderAdminJobsOverlay(context);
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

    private async Task PurgeAdminJobsHistoryAsync(AdminJobsOverlayContext context, string scope)
    {
        try
        {
            context.DeleteSelectionButton.IsEnabled = false;
            context.PurgeButton.IsEnabled = false;

            var response = await _api.AdminJobsPurgeAsync(scope, null, CancellationToken.None).ConfigureAwait(true);
            var deleted = TryGetInt(response, "deleted") ?? TryGetInt(response, "Deleted") ?? 0;

            if (deleted > 0)
            {
                await RefreshAdminJobsOverlayAsync(context, CancellationToken.None).ConfigureAwait(true);
                Status(ClientUiText.Format("admin.jobs.delete_done", UiLang, deleted));
            }
            else
            {
                Status(ClientUiText.Get("admin.jobs.delete_nothing", UiLang));
            }
        }
        catch (Exception ex)
        {
            ClientLog.Exception("AdminJobs.PurgeHistory", ex);
            Status(ClientUiText.Get("admin.jobs.delete_failed", UiLang) + ex.Message);
        }
        finally
        {
            context.DeleteSelectionButton.IsEnabled = context.SelectedTerminalJobIds.Count > 0;
            context.PurgeButton.IsEnabled = true;
        }
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

    private Button BuildDialogInlineButton(string text, bool destructive = false, string? accentStatus = null)
    {
        var button = BuildDialogFooterButton(text, destructive: destructive);
        button.MinWidth = 0;
        button.Padding = new Thickness(12, 8, 12, 8);
        button.CornerRadius = new CornerRadius(12);
        if (!string.IsNullOrWhiteSpace(accentStatus))
            ApplyInlineButtonStatusAccent(button, accentStatus!);
        return button;
    }

    private void ApplyInlineButtonStatusAccent(Button button, string status)
    {
        var light = UseLightPalette();
        var background = GetAdminJobStatusBackground(status, light);
        var border = GetAdminJobStatusBorder(status, light);
        var foreground = GetAdminJobStatusForeground(status, light);

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

}
