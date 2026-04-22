using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow
{
    private OverlayDialogSession? _activeCapabilityBQualityOverlay;

    private sealed record AdminRuntimeCapabilityBQualitySnapshot(
        string Environment,
        DateTimeOffset? GeneratedAt,
        double QualityThreshold,
        AdminRuntimeCapabilityBQualitySummary Summary,
        IReadOnlyList<AdminRuntimeCapabilityBQualityItem> Items);

    private sealed record AdminRuntimeCapabilityBQualitySummary(
        int TotalLowQualitySummaries,
        int FallbackSummaryCount,
        int LiveLlmSummaryCount,
        int RuntimeUnavailableCount,
        double? LowestQualityScore,
        DateTimeOffset? LatestUpdatedAt,
        IReadOnlyList<AdminRuntimeNamedCountItem> StrategyCounts,
        IReadOnlyList<AdminRuntimeNamedCountItem> RuntimeStatusCounts,
        IReadOnlyList<string> Recommendations);

    private sealed record AdminRuntimeCapabilityBQualityItem(
        Guid? DocId,
        string DocPath,
        string DocName,
        string? Category,
        string Level,
        double QualityScore,
        string Severity,
        string RecommendedAction,
        string? Strategy,
        bool FallbackUsed,
        string? FallbackReason,
        string? RuntimeCapabilityStatus,
        int SummaryLength,
        DateTimeOffset? UpdatedAt,
        AdminRuntimeCapabilityBQualitySignals Signals,
        IReadOnlyList<string> Recommendations);

    private sealed record AdminRuntimeCapabilityBQualitySignals(
        int? LineCount,
        double? LengthScore,
        double? StructureScore,
        double? SectionCoverageScore,
        double? KeywordCoverageScore);

    private sealed record AdminRuntimeNamedCountItem(
        string Key,
        int Count);

    private async Task ShowCapabilityBQualityReviewOverlayAsync()
    {
        if (!_api.HasAdminKey)
        {
            Status(ClientUiText.Get("admin.jobs.no_admin", UiLang));
            return;
        }

        if (_activeCapabilityBQualityOverlay is not null)
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

        var summaryHost = new StackPanel { Spacing = 10 };
        var itemsHost = new StackPanel { Spacing = 12 };
        var listScroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 420,
            Content = itemsHost
        };

        var refreshButton = BuildDialogFooterButton(ClientUiText.Get("admin.runtime.quality.refresh", lang), primary: true);
        var openJobsButton = BuildDialogFooterButton(ClientUiText.Get("admin.runtime.action.open_b_jobs", lang));
        var closeButton = BuildDialogFooterButton(ClientUiText.Get("dialog.close", lang));
        var footer = BuildDialogFooter(refreshButton, openJobsButton, closeButton);

        OverlayDialogSession? overlay = null;
        using var overlayCts = new CancellationTokenSource();
        var isLoading = false;

        void SetStateBanner(string text, bool positive = false)
            => stateHost.Content = BuildDialogInfoBanner(text, positive);

        void SetBusy(bool busy)
        {
            isLoading = busy;
            refreshButton.IsEnabled = !busy;
            openJobsButton.IsEnabled = !busy;
            closeButton.IsEnabled = !busy;
        }

        async Task RegenerateQualityItemAsync(AdminRuntimeCapabilityBQualityItem item, Button actionButton)
        {
            if (isLoading)
                return;

            var docIds = item.DocId.HasValue
                ? new[] { item.DocId.Value }
                : null;
            var docPaths = !item.DocId.HasValue && !string.IsNullOrWhiteSpace(item.DocPath)
                ? new[] { item.DocPath }
                : null;
            if (docIds is null && docPaths is null)
            {
                Status(ClientUiText.Get("admin.runtime.quality.regenerate_unavailable", lang));
                return;
            }

            try
            {
                SetBusy(true);
                actionButton.IsEnabled = false;
                Status(ClientUiText.Get("admin.runtime.quality.regenerating", lang));
                var response = await _api.AdminRuntimeCapabilityBEnqueueAsync(
                        docIds,
                        docPaths,
                        dryRun: false,
                        force: true,
                        maxCandidates: 1,
                        overlayCts.Token)
                    .ConfigureAwait(true);
                var queuedCount = TryGetInt(response, "queuedCount") ?? TryGetInt(response, "QueuedCount") ?? 0;
                var candidateCount = TryGetInt(response, "candidateCount") ?? TryGetInt(response, "CandidateCount") ?? 0;
                var skippedCount = TryGetInt(response, "skippedCount") ?? TryGetInt(response, "SkippedCount") ?? 0;
                var jobId = TryGetFirstQueuedJobId(response);
                if (queuedCount > 0)
                {
                    var jobLabel = jobId.HasValue ? ShortJobId(jobId.Value) : "n/a";
                    Status(ClientUiText.Format("admin.runtime.quality.regenerate_done_detailed", lang, queuedCount, candidateCount, skippedCount, jobLabel));
                }
                else
                {
                    Status(ClientUiText.Format("admin.runtime.quality.regenerate_none", lang, candidateCount, skippedCount));
                }

                SetBusy(false);
                await LoadAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                ClientLog.Exception("AdminRuntimeQuality.Regenerate", ex);
                Status(ClientUiText.Get("admin.runtime.quality.regenerate_failed", lang) + ex.Message);
            }
            finally
            {
                SetBusy(false);
                actionButton.IsEnabled = true;
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

        void RenderMetrics(AdminRuntimeCapabilityBQualitySummary summary)
        {
            metricsGrid.Children.Clear();
            var tiles = new[]
            {
                BuildMetricTile(ClientUiText.Get("admin.runtime.quality.metric.low_count", lang), summary.TotalLowQualitySummaries.ToString(CultureInfo.InvariantCulture)),
                BuildMetricTile(ClientUiText.Get("admin.runtime.quality.metric.fallbacks", lang), summary.FallbackSummaryCount.ToString(CultureInfo.InvariantCulture)),
                BuildMetricTile(ClientUiText.Get("admin.runtime.quality.metric.runtime_unavailable", lang), summary.RuntimeUnavailableCount.ToString(CultureInfo.InvariantCulture)),
                BuildMetricTile(
                    ClientUiText.Get("admin.runtime.quality.metric.lowest_score", lang),
                    summary.LowestQualityScore.HasValue ? summary.LowestQualityScore.Value.ToString("0.00", CultureInfo.InvariantCulture) : "n/a")
            };

            for (var index = 0; index < tiles.Length; index++)
            {
                Grid.SetColumn(tiles[index], index);
                Grid.SetRow(tiles[index], 0);
                metricsGrid.Children.Add(tiles[index]);
            }
        }

        FrameworkElement BuildDistributionCard(string title, IReadOnlyList<AdminRuntimeNamedCountItem> items)
        {
            var stack = new StackPanel { Spacing = 6 };
            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontWeight = FontWeights.SemiBold,
                Foreground = UseLightPalette() ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xF5, 0xF7, 0xFB)
            });

            if (items.Count == 0)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = ClientUiText.Get("admin.runtime.quality.empty_distribution", lang),
                    Foreground = UseLightPalette() ? UiBrush(0x5D, 0x6E, 0x82) : UiBrush(0xA8, 0xB5, 0xC7)
                });
            }
            else
            {
                foreach (var item in items.Take(4))
                {
                    stack.Children.Add(new TextBlock
                    {
                        Text = $"{item.Key}: {item.Count}",
                        Foreground = UseLightPalette() ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE),
                        TextWrapping = TextWrapping.WrapWholeWords
                    });
                }
            }

            return BuildDialogSurfaceCard(stack, new Thickness(14));
        }

        FrameworkElement BuildQualityItemCard(AdminRuntimeCapabilityBQualityItem item)
        {
            var stack = new StackPanel { Spacing = 8 };
            stack.Children.Add(new TextBlock
            {
                Text = item.DocName,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = UseLightPalette() ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xF5, 0xF7, 0xFB),
                TextWrapping = TextWrapping.WrapWholeWords
            });
            stack.Children.Add(new TextBlock
            {
                Text = item.DocPath,
                Foreground = UseLightPalette() ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE),
                TextWrapping = TextWrapping.WrapWholeWords
            });

            var facts = new StackPanel { Spacing = 3 };
            facts.Children.Add(new TextBlock
            {
                Text = ClientUiText.Format("admin.runtime.quality.fact.score", lang, item.QualityScore.ToString("0.00", CultureInfo.InvariantCulture))
            });
            facts.Children.Add(new TextBlock
            {
                Text = ClientUiText.Format("admin.runtime.quality.fact.severity", lang, item.Severity)
            });
            facts.Children.Add(new TextBlock
            {
                Text = ClientUiText.Format("admin.runtime.quality.fact.recommended_action", lang, item.RecommendedAction),
                TextWrapping = TextWrapping.WrapWholeWords
            });
            facts.Children.Add(new TextBlock
            {
                Text = ClientUiText.Format("admin.runtime.quality.fact.strategy", lang, item.Strategy ?? "unknown")
            });
            facts.Children.Add(new TextBlock
            {
                Text = ClientUiText.Format("admin.runtime.quality.fact.runtime_status", lang, item.RuntimeCapabilityStatus ?? "unknown")
            });
            facts.Children.Add(new TextBlock
            {
                Text = ClientUiText.Format("admin.runtime.quality.fact.summary_length", lang, item.SummaryLength.ToString(CultureInfo.InvariantCulture))
            });
            if (item.UpdatedAt.HasValue)
            {
                facts.Children.Add(new TextBlock
                {
                    Text = ClientUiText.Format("admin.runtime.quality.fact.updated", lang, item.UpdatedAt.Value.ToLocalTime().ToString("g"))
                });
            }

            if (item.FallbackUsed)
            {
                facts.Children.Add(new TextBlock
                {
                    Text = ClientUiText.Format("admin.runtime.quality.fact.fallback", lang, item.FallbackReason ?? "unknown")
                });
            }

            if (item.Signals.SectionCoverageScore.HasValue || item.Signals.KeywordCoverageScore.HasValue)
            {
                var sectionCoverage = item.Signals.SectionCoverageScore?.ToString("0.00", CultureInfo.InvariantCulture) ?? "n/a";
                var keywordCoverage = item.Signals.KeywordCoverageScore?.ToString("0.00", CultureInfo.InvariantCulture) ?? "n/a";
                facts.Children.Add(new TextBlock
                {
                    Text = ClientUiText.Format("admin.runtime.quality.fact.coverage", lang, sectionCoverage, keywordCoverage),
                    TextWrapping = TextWrapping.WrapWholeWords
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
                Margin = new Thickness(0, 4, 0, 0)
            };
            var regenerateButton = BuildDialogInlineButton(
                ClientUiText.Get("admin.runtime.quality.action.regenerate", lang),
                accentStatus: "running");
            regenerateButton.IsEnabled = item.DocId.HasValue || !string.IsNullOrWhiteSpace(item.DocPath);
            regenerateButton.Click += async (_, __) =>
                await RegenerateQualityItemAsync(item, regenerateButton).ConfigureAwait(true);
            var openJobsInlineButton = BuildDialogInlineButton(ClientUiText.Get("admin.runtime.action.open_b_jobs", lang));
            openJobsInlineButton.Click += async (_, __) =>
            {
                overlay?.Close();
                await ShowAdminJobsOverlayAsync(launchMode: AdminJobsLaunchMode.CapabilityBBackoffice).ConfigureAwait(true);
            };
            actions.Children.Add(regenerateButton);
            actions.Children.Add(openJobsInlineButton);
            stack.Children.Add(actions);

            return BuildDialogSurfaceCard(stack, new Thickness(16, 14, 16, 14));
        }

        void Render(AdminRuntimeCapabilityBQualitySnapshot snapshot)
        {
            generatedText.Text = snapshot.GeneratedAt.HasValue
                ? ClientUiText.Format("admin.runtime.generated", lang, snapshot.GeneratedAt.Value.ToLocalTime().ToString("g"))
                : string.Empty;
            SetStateBanner(
                ClientUiText.Format(
                    "admin.runtime.quality.state_ready",
                    lang,
                    snapshot.Environment,
                    snapshot.QualityThreshold.ToString("0.00", CultureInfo.InvariantCulture)),
                positive: true);
            RenderMetrics(snapshot.Summary);

            summaryHost.Children.Clear();
            summaryHost.Children.Add(BuildDistributionCard(
                ClientUiText.Get("admin.runtime.quality.section.strategies", lang),
                snapshot.Summary.StrategyCounts));
            summaryHost.Children.Add(BuildDistributionCard(
                ClientUiText.Get("admin.runtime.quality.section.statuses", lang),
                snapshot.Summary.RuntimeStatusCounts));

            if (snapshot.Summary.Recommendations.Count > 0)
            {
                var recommendationStack = new StackPanel { Spacing = 6 };
                recommendationStack.Children.Add(new TextBlock
                {
                    Text = ClientUiText.Get("admin.runtime.field.recommendations", lang),
                    FontWeight = FontWeights.SemiBold,
                    Foreground = UseLightPalette() ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xF5, 0xF7, 0xFB)
                });
                foreach (var recommendation in snapshot.Summary.Recommendations.Take(3))
                {
                    recommendationStack.Children.Add(new TextBlock
                    {
                        Text = "- " + recommendation,
                        TextWrapping = TextWrapping.WrapWholeWords,
                        Foreground = UseLightPalette() ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE)
                    });
                }

                summaryHost.Children.Add(BuildDialogSurfaceCard(recommendationStack, new Thickness(14)));
            }

            itemsHost.Children.Clear();
            if (snapshot.Items.Count == 0)
            {
                itemsHost.Children.Add(BuildDialogInfoBanner(ClientUiText.Get("admin.runtime.quality.empty", lang), positive: true));
                return;
            }

            foreach (var item in snapshot.Items)
                itemsHost.Children.Add(BuildQualityItemCard(item));
        }

        async Task LoadAsync()
        {
            if (isLoading)
                return;

            SetBusy(true);
            generatedText.Text = string.Empty;
            metricsGrid.Children.Clear();
            summaryHost.Children.Clear();
            itemsHost.Children.Clear();
            SetStateBanner(ClientUiText.Get("admin.runtime.quality.loading", lang));

            try
            {
                var summaryJson = await _api.AdminRuntimeCapabilityBQualityReviewSummaryAsync(overlayCts.Token).ConfigureAwait(true);
                var reviewJson = await _api.AdminRuntimeCapabilityBQualityReviewAsync(50, overlayCts.Token).ConfigureAwait(true);
                var snapshot = ParseAdminRuntimeCapabilityBQualitySnapshot(summaryJson, reviewJson);
                Render(snapshot);
            }
            catch (Exception ex)
            {
                ClientLog.Exception("AdminRuntimeQuality.Load", ex);
                SetStateBanner(ClientUiText.Get("admin.runtime.quality.load_failed", lang));
                itemsHost.Children.Add(BuildDialogInfoBanner(ex.Message));
            }
            finally
            {
                SetBusy(false);
            }
        }

        refreshButton.Click += async (_, __) => await LoadAsync().ConfigureAwait(true);
        openJobsButton.Click += async (_, __) =>
        {
            overlay?.Close();
            await ShowAdminJobsOverlayAsync(launchMode: AdminJobsLaunchMode.CapabilityBBackoffice).ConfigureAwait(true);
        };
        closeButton.Click += (_, __) => overlay?.Close();

        overlay = ShowOverlayDialog(
            BuildScrollableDialogShell(
                ClientUiText.Get("help.section.admin", lang),
                ClientUiText.Get("admin.runtime.quality.title", lang),
                ClientUiText.Get("admin.runtime.quality.subtitle", lang),
                new UIElement[]
                {
                    generatedText,
                    stateHost,
                    BuildDialogSurfaceCard(metricsGrid, new Thickness(12)),
                    BuildDialogSurfaceCard(summaryHost, new Thickness(12)),
                    BuildDialogSurfaceCard(listScroller, new Thickness(12))
                },
                footer,
                maxWidth: 980,
                maxHeight: 760),
            closeOnBackgroundTap: true);

        _activeCapabilityBQualityOverlay = overlay;

        try
        {
            await LoadAsync().ConfigureAwait(true);
            await overlay.Completion.ConfigureAwait(true);
        }
        finally
        {
            overlayCts.Cancel();
            if (ReferenceEquals(_activeCapabilityBQualityOverlay, overlay))
                _activeCapabilityBQualityOverlay = null;
        }
    }

    private static AdminRuntimeCapabilityBQualitySnapshot ParseAdminRuntimeCapabilityBQualitySnapshot(JsonElement summaryRoot, JsonElement reviewRoot)
    {
        var summaryElement = TryGetPropertyIgnoreCase(summaryRoot, "summary", out var nestedSummary) ? nestedSummary : default;
        var recommendations = summaryElement.ValueKind == JsonValueKind.Object
            ? ReadStringArray(summaryElement, "recommendations")
            : Array.Empty<string>();

        var snapshotSummary = new AdminRuntimeCapabilityBQualitySummary(
            TotalLowQualitySummaries: summaryElement.ValueKind == JsonValueKind.Object ? TryGetInt(summaryElement, "totalLowQualitySummaries") ?? 0 : 0,
            FallbackSummaryCount: summaryElement.ValueKind == JsonValueKind.Object ? TryGetInt(summaryElement, "fallbackSummaryCount") ?? 0 : 0,
            LiveLlmSummaryCount: summaryElement.ValueKind == JsonValueKind.Object ? TryGetInt(summaryElement, "liveLlmSummaryCount") ?? 0 : 0,
            RuntimeUnavailableCount: summaryElement.ValueKind == JsonValueKind.Object ? TryGetInt(summaryElement, "runtimeUnavailableCount") ?? 0 : 0,
            LowestQualityScore: summaryElement.ValueKind == JsonValueKind.Object ? TryGetDouble(summaryElement, "lowestQualityScore") : null,
            LatestUpdatedAt: summaryElement.ValueKind == JsonValueKind.Object ? TryGetDateTimeOffset(summaryElement, "latestUpdatedAt") : null,
            StrategyCounts: summaryElement.ValueKind == JsonValueKind.Object ? ParseNamedCounts(summaryElement, "strategyCounts") : Array.Empty<AdminRuntimeNamedCountItem>(),
            RuntimeStatusCounts: summaryElement.ValueKind == JsonValueKind.Object ? ParseNamedCounts(summaryElement, "runtimeStatusCounts") : Array.Empty<AdminRuntimeNamedCountItem>(),
            Recommendations: recommendations);

        var items = new List<AdminRuntimeCapabilityBQualityItem>();
        if (TryGetPropertyIgnoreCase(reviewRoot, "items", out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemsElement.EnumerateArray())
            {
                JsonElement signalsElement = default;
                _ = TryGetPropertyIgnoreCase(item, "signals", out signalsElement);

                items.Add(new AdminRuntimeCapabilityBQualityItem(
                    DocId: TryGetGuid(item, "docId"),
                    DocPath: TryGetString(item, "docPath") ?? string.Empty,
                    DocName: TryGetString(item, "docName") ?? TryGetString(item, "docPath") ?? string.Empty,
                    Category: TryGetString(item, "category"),
                    Level: TryGetString(item, "level") ?? string.Empty,
                    QualityScore: TryGetDouble(item, "qualityScore") ?? 0d,
                    Severity: TryGetString(item, "severity") ?? "unknown",
                    RecommendedAction: TryGetString(item, "recommendedAction") ?? "manual_review",
                    Strategy: TryGetString(item, "strategy"),
                    FallbackUsed: TryGetBool(item, "fallbackUsed") ?? false,
                    FallbackReason: TryGetString(item, "fallbackReason"),
                    RuntimeCapabilityStatus: TryGetString(item, "runtimeCapabilityStatus"),
                    SummaryLength: TryGetInt(item, "summaryLength") ?? 0,
                    UpdatedAt: TryGetDateTimeOffset(item, "updatedAt"),
                    Signals: new AdminRuntimeCapabilityBQualitySignals(
                        LineCount: signalsElement.ValueKind == JsonValueKind.Object ? TryGetInt(signalsElement, "lineCount") : null,
                        LengthScore: signalsElement.ValueKind == JsonValueKind.Object ? TryGetDouble(signalsElement, "lengthScore") : null,
                        StructureScore: signalsElement.ValueKind == JsonValueKind.Object ? TryGetDouble(signalsElement, "structureScore") : null,
                        SectionCoverageScore: signalsElement.ValueKind == JsonValueKind.Object ? TryGetDouble(signalsElement, "sectionCoverageScore") : null,
                        KeywordCoverageScore: signalsElement.ValueKind == JsonValueKind.Object ? TryGetDouble(signalsElement, "keywordCoverageScore") : null),
                    Recommendations: ReadStringArray(item, "recommendations")));
            }
        }

        return new AdminRuntimeCapabilityBQualitySnapshot(
            Environment: TryGetString(summaryRoot, "environment") ?? TryGetString(reviewRoot, "environment") ?? string.Empty,
            GeneratedAt: TryGetDateTimeOffset(summaryRoot, "generatedAt") ?? TryGetDateTimeOffset(reviewRoot, "generatedAt"),
            QualityThreshold: TryGetDouble(summaryRoot, "qualityThreshold") ?? TryGetDouble(reviewRoot, "qualityThreshold") ?? 0d,
            Summary: snapshotSummary,
            Items: items);
    }

    private static IReadOnlyList<AdminRuntimeNamedCountItem> ParseNamedCounts(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
            return Array.Empty<AdminRuntimeNamedCountItem>();

        var items = new List<AdminRuntimeNamedCountItem>();
        foreach (var item in property.EnumerateArray())
        {
            items.Add(new AdminRuntimeNamedCountItem(
                TryGetString(item, "key") ?? string.Empty,
                TryGetInt(item, "count") ?? 0));
        }

        return items;
    }

    private static Guid? TryGetGuid(JsonElement element, string propertyName)
    {
        var raw = TryGetString(element, propertyName);
        return Guid.TryParse(raw, out var value) ? value : null;
    }

    private static Guid? TryGetFirstQueuedJobId(JsonElement element)
    {
        if (!TryGetPropertyIgnoreCase(element, "items", out var items) || items.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in items.EnumerateArray())
        {
            if (TryGetBool(item, "queued") == true && TryGetGuid(item, "jobId") is { } jobId)
                return jobId;
        }

        return null;
    }

    private static string ShortJobId(Guid jobId)
        => jobId.ToString("N")[..8];

    private static double? TryGetDouble(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var property))
            return null;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var value))
            return value;
        if (property.ValueKind == JsonValueKind.String && double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return value;
        return null;
    }
}
