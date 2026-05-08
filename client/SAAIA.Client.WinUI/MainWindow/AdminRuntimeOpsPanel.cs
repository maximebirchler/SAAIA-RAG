using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SAAIA.Client.WinUI.Services;

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
        int? CapabilityBLatestCampaignProgressPercent,
        AdminRuntimeCapabilityBIdleScheduler? CapabilityBIdleScheduler);

    private sealed record AdminRuntimeCapabilityBIdleScheduler(
        bool Enabled,
        bool RequiresIngestionIdle,
        bool AutoEnqueueEnabled,
        bool IsIdle,
        string State,
        string Reason,
        int ActiveIngestionJobs,
        DateTimeOffset? LastIngestionActivityAt,
        int RequiredIdleSeconds,
        int? IdleForSeconds,
        int AutoEnqueueBatchSize,
        int RunningJobLeaseTimeoutSeconds);

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
        IReadOnlyDictionary<string, int> ReasonCounts,
        int? OffsetBackfillCandidateCount,
        int? LatestCampaignProgressPercent,
        string? LatestCampaignId,
        string? LatestCampaignStatus,
        DateTimeOffset? LatestCampaignOccurredAt,
        IReadOnlyDictionary<string, int> LlmFailureCounts);

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
        var blacklistHost = new ContentPresenter();
        var runtimeHost = new ContentPresenter();
        var metricsGrid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        for (var i = 0; i < 3; i++)
            metricsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metricsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        metricsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        metricsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var capabilitiesHost = new StackPanel { Spacing = 12 };

        var reconcileStaleButton = BuildDialogFooterButton(GetAdminRuntimeReconcileStaleButtonLabel(lang));
        var requalifyButton = BuildDialogFooterButton(ClientUiText.Get("admin.runtime.requalify", lang));
        var refreshButton = BuildDialogFooterButton(ClientUiText.Get("admin.runtime.refresh", lang), primary: true);
        var closeButton = BuildDialogFooterButton(ClientUiText.Get("dialog.close", lang));
        var footer = BuildDialogFooter(reconcileStaleButton, requalifyButton, refreshButton, closeButton);

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

        void SetBlacklistBanner(string text, bool positive = false)
        {
            blacklistHost.Content = BuildDialogInfoBanner(text, positive);
        }

        void SetRuntimeSummary(LocalLlmRuntimeDiagnostics diagnostics)
        {
            static string FormatTimestamp(DateTimeOffset? value)
                => value?.ToLocalTime().ToString("g") ?? "-";

            var summary = LocalRuntimeText(
                $"{diagnostics.RuntimeLabel} | build {diagnostics.ActiveBuild ?? "inconnu"}",
                $"{diagnostics.RuntimeLabel} | build {diagnostics.ActiveBuild ?? "unknown"}",
                $"{diagnostics.RuntimeLabel} | build {diagnostics.ActiveBuild ?? "desconocido"}",
                $"{diagnostics.RuntimeLabel} | build {diagnostics.ActiveBuild ?? "desconhecido"}",
                $"{diagnostics.RuntimeLabel} | Build {diagnostics.ActiveBuild ?? "unbekannt"}",
                $"{diagnostics.RuntimeLabel} | build {diagnostics.ActiveBuild ?? "sconosciuto"}",
                lang);
            if (!string.IsNullOrWhiteSpace(diagnostics.RequiredBuild))
                summary += " | " + LocalRuntimeText(
                    $"requis {diagnostics.RequiredBuild}",
                    $"required {diagnostics.RequiredBuild}",
                    $"requerido {diagnostics.RequiredBuild}",
                    $"requerido {diagnostics.RequiredBuild}",
                    $"erforderlich {diagnostics.RequiredBuild}",
                    $"richiesto {diagnostics.RequiredBuild}",
                    lang);

            var details = new StackPanel { Spacing = 6 };
            details.Children.Add(new TextBlock
            {
                Text = summary,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = UseLightPalette() ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xF5, 0xF7, 0xFB),
                TextWrapping = TextWrapping.WrapWholeWords
            });
            details.Children.Add(new TextBlock
            {
                Text = LocalRuntimeText(
                    $"Etat {ResolveRuntimeStateLabel(diagnostics.ActiveState, lang)} | Warmup {ResolveWarmupStateLabel(diagnostics.LatestWarmupStatus, lang)} | Flash-attn {ResolveFlashAttnPolicyLabel(diagnostics.ForcedFlashAttn, lang)}",
                    $"State {ResolveRuntimeStateLabel(diagnostics.ActiveState, lang)} | Warmup {ResolveWarmupStateLabel(diagnostics.LatestWarmupStatus, lang)} | Flash-attn {ResolveFlashAttnPolicyLabel(diagnostics.ForcedFlashAttn, lang)}",
                    $"Estado {ResolveRuntimeStateLabel(diagnostics.ActiveState, lang)} | Warmup {ResolveWarmupStateLabel(diagnostics.LatestWarmupStatus, lang)} | Flash-attn {ResolveFlashAttnPolicyLabel(diagnostics.ForcedFlashAttn, lang)}",
                    $"Estado {ResolveRuntimeStateLabel(diagnostics.ActiveState, lang)} | Warmup {ResolveWarmupStateLabel(diagnostics.LatestWarmupStatus, lang)} | Flash-attn {ResolveFlashAttnPolicyLabel(diagnostics.ForcedFlashAttn, lang)}",
                    $"Status {ResolveRuntimeStateLabel(diagnostics.ActiveState, lang)} | Warmup {ResolveWarmupStateLabel(diagnostics.LatestWarmupStatus, lang)} | Flash-attn {ResolveFlashAttnPolicyLabel(diagnostics.ForcedFlashAttn, lang)}",
                    $"Stato {ResolveRuntimeStateLabel(diagnostics.ActiveState, lang)} | Warmup {ResolveWarmupStateLabel(diagnostics.LatestWarmupStatus, lang)} | Flash-attn {ResolveFlashAttnPolicyLabel(diagnostics.ForcedFlashAttn, lang)}",
                    lang),
                Foreground = UseLightPalette() ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE),
                TextWrapping = TextWrapping.WrapWholeWords
            });

            if (!string.IsNullOrWhiteSpace(diagnostics.PreviousBuild)
                || !string.IsNullOrWhiteSpace(diagnostics.ModelId))
            {
                details.Children.Add(new TextBlock
                {
                    Text = LocalRuntimeText(
                        $"Modele {diagnostics.ModelId ?? "-"} | precedent {diagnostics.PreviousBuild ?? "-"} | profil {diagnostics.QualifiedProfileId ?? "-"}",
                        $"Model {diagnostics.ModelId ?? "-"} | previous {diagnostics.PreviousBuild ?? "-"} | profile {diagnostics.QualifiedProfileId ?? "-"}",
                        $"Modelo {diagnostics.ModelId ?? "-"} | anterior {diagnostics.PreviousBuild ?? "-"} | perfil {diagnostics.QualifiedProfileId ?? "-"}",
                        $"Modelo {diagnostics.ModelId ?? "-"} | anterior {diagnostics.PreviousBuild ?? "-"} | perfil {diagnostics.QualifiedProfileId ?? "-"}",
                        $"Modell {diagnostics.ModelId ?? "-"} | vorher {diagnostics.PreviousBuild ?? "-"} | Profil {diagnostics.QualifiedProfileId ?? "-"}",
                        $"Modello {diagnostics.ModelId ?? "-"} | precedente {diagnostics.PreviousBuild ?? "-"} | profilo {diagnostics.QualifiedProfileId ?? "-"}",
                        lang),
                    Foreground = UseLightPalette() ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE),
                    TextWrapping = TextWrapping.WrapWholeWords
                });
            }

            details.Children.Add(new TextBlock
            {
                Text = LocalRuntimeText(
                    $"Actif depuis {FormatTimestamp(diagnostics.ActivatedAtUtc)} | qualifie le {FormatTimestamp(diagnostics.QualifiedAtUtc)}",
                    $"Active since {FormatTimestamp(diagnostics.ActivatedAtUtc)} | qualified at {FormatTimestamp(diagnostics.QualifiedAtUtc)}",
                    $"Activo desde {FormatTimestamp(diagnostics.ActivatedAtUtc)} | cualificado el {FormatTimestamp(diagnostics.QualifiedAtUtc)}",
                    $"Ativo desde {FormatTimestamp(diagnostics.ActivatedAtUtc)} | qualificado em {FormatTimestamp(diagnostics.QualifiedAtUtc)}",
                    $"Aktiv seit {FormatTimestamp(diagnostics.ActivatedAtUtc)} | qualifiziert am {FormatTimestamp(diagnostics.QualifiedAtUtc)}",
                    $"Attivo da {FormatTimestamp(diagnostics.ActivatedAtUtc)} | qualificato il {FormatTimestamp(diagnostics.QualifiedAtUtc)}",
                    lang),
                Foreground = UseLightPalette() ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE),
                TextWrapping = TextWrapping.WrapWholeWords
            });

            if (diagnostics.RecentEvents.Count > 0)
            {
                details.Children.Add(new TextBlock
                {
                    Text = LocalRuntimeText("Evenements runtime recents :", "Recent runtime events:", "Eventos runtime recientes:", "Eventos runtime recentes:", "Aktuelle Runtime-Ereignisse:", "Eventi runtime recenti:", lang),
                    FontWeight = FontWeights.SemiBold,
                    Foreground = UseLightPalette() ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE),
                    TextWrapping = TextWrapping.WrapWholeWords
                });

                foreach (var item in diagnostics.RecentEvents.Take(3))
                {
                    details.Children.Add(new TextBlock
                    {
                        Text = LocalRuntimeText(
                            $"{item.At.ToLocalTime():g} | {ResolveRuntimeEventLabel(item.EventKind, lang)} | build {item.Build ?? "-"} | prec. {item.PreviousBuild ?? "-"}",
                            $"{item.At.ToLocalTime():g} | {ResolveRuntimeEventLabel(item.EventKind, lang)} | build {item.Build ?? "-"} | prev {item.PreviousBuild ?? "-"}",
                            $"{item.At.ToLocalTime():g} | {ResolveRuntimeEventLabel(item.EventKind, lang)} | build {item.Build ?? "-"} | ant. {item.PreviousBuild ?? "-"}",
                            $"{item.At.ToLocalTime():g} | {ResolveRuntimeEventLabel(item.EventKind, lang)} | build {item.Build ?? "-"} | ant. {item.PreviousBuild ?? "-"}",
                            $"{item.At.ToLocalTime():g} | {ResolveRuntimeEventLabel(item.EventKind, lang)} | Build {item.Build ?? "-"} | vorher {item.PreviousBuild ?? "-"}",
                            $"{item.At.ToLocalTime():g} | {ResolveRuntimeEventLabel(item.EventKind, lang)} | build {item.Build ?? "-"} | prec. {item.PreviousBuild ?? "-"}",
                            lang),
                        Foreground = UseLightPalette() ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE),
                        TextWrapping = TextWrapping.WrapWholeWords
                    });
                }
            }
            else
            {
                details.Children.Add(new TextBlock
                {
                    Text = LocalRuntimeText("Evenements runtime recents : aucun", "Recent runtime events: none", "Eventos runtime recientes: ninguno", "Eventos runtime recentes: nenhum", "Aktuelle Runtime-Ereignisse: keine", "Eventi runtime recenti: nessuno", lang),
                    Foreground = UseLightPalette() ? UiBrush(0x4B, 0x5D, 0x71) : UiBrush(0xC7, 0xD1, 0xDE),
                    TextWrapping = TextWrapping.WrapWholeWords
                });
            }

            runtimeHost.Content = BuildDialogSurfaceCard(details, new Thickness(12));
        }

        async Task RefreshLocalRuntimeSummaryAsync()
        {
            try
            {
                var diagnostics = await LocalLlmRuntimeDiagnosticsService.EvaluateAsync(AppSettings.Load(), ct: overlayCts.Token).ConfigureAwait(true);
                SetRuntimeSummary(diagnostics);
            }
            catch (Exception ex)
            {
                ClientLog.Exception("AdminRuntimeOps.LocalRuntimeSummary", ex);
                runtimeHost.Content = BuildDialogInfoBanner(LocalRuntimeText("Diagnostic runtime local indisponible.", "Local runtime diagnostics unavailable.", "Diagnostico runtime local no disponible.", "Diagnostico runtime local indisponivel.", "Lokale Runtime-Diagnose nicht verfuegbar.", "Diagnostica runtime locale non disponibile.", lang));
            }
        }

        void SetBusy(bool busy)
        {
            isLoading = busy;
            reconcileStaleButton.IsEnabled = !busy;
            requalifyButton.IsEnabled = !busy;
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
            var tiles = new List<FrameworkElement>
            {
                BuildMetricTile(ClientUiText.Get("admin.runtime.metric.a_backlog", lang), summary.CapabilityACandidateCount.ToString()),
                BuildMetricTile(ClientUiText.Get("admin.runtime.metric.a_ready", lang), summary.CapabilityAReadyToEnqueueCount.ToString()),
                BuildMetricTile(ClientUiText.Get("admin.runtime.metric.a_offsets", lang), summary.CapabilityAOffsetBackfillCandidateCount.ToString()),
                BuildMetricTile(ClientUiText.Get("admin.runtime.metric.b_backlog", lang), summary.CapabilityBBacklogCount.ToString()),
                BuildMetricTile(ClientUiText.Get("admin.runtime.metric.b_ready", lang), summary.CapabilityBReadyToEnqueueCount.ToString()),
                BuildMetricTile(ClientUiText.Get("admin.runtime.metric.b_active", lang), summary.CapabilityBActiveJobCount.ToString())
            };
            if (summary.CapabilityBLatestCampaignProgressPercent is int latestCampaignProgress)
            {
                tiles.Add(BuildMetricTile(
                    LocalRuntimeText("Campagne B", "B campaign", "Campana B", "Campanha B", "B-Kampagne", "Campagna B", lang),
                    latestCampaignProgress + "%"));
            }

            for (var index = 0; index < tiles.Count; index++)
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
                var preview = await LoadCapabilityAOffsetBackfillPreviewAsync(lang, overlayCts.Token).ConfigureAwait(true);
                SetStateBanner(preview.Message, positive: preview.Positive);
                Status(preview.Message);
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

        async Task OpenCapabilityAKpisAsync()
        {
            await ShowCapabilityAKpiOverlayAsync().ConfigureAwait(true);
        }

        async Task OpenCapabilityBJobsAsync()
        {
            overlay?.Close();
            await ShowAdminJobsOverlayAsync(launchMode: AdminJobsLaunchMode.CapabilityBBackoffice).ConfigureAwait(true);
        }

        async Task OpenCapabilityBQualityReviewAsync()
        {
            await ShowCapabilityBQualityReviewOverlayAsync().ConfigureAwait(true);
        }

        async Task RequalifyRuntimeAsync()
        {
            if (isLoading)
                return;

            SetBusy(true);
            SetStateBanner(GetAdminRuntimeRequalifyLoadingMessage(lang));

            try
            {
                var response = await _api.AdminRuntimeRequalifyAsync(
                    capabilityKey: null,
                    profileKey: null,
                    selectWhenQualified: true,
                    overlayCts.Token).ConfigureAwait(true);
                var message = BuildAdminRuntimeRequalifySummaryMessage(response, lang);
                SetStateBanner(message, positive: true);
                Status(message);
                var snapshot = ParseAdminRuntimeOperationalSnapshot(
                    await _api.AdminRuntimeOperationalSummaryAsync(overlayCts.Token).ConfigureAwait(true));
                Render(snapshot);
            }
            catch (Exception ex)
            {
                ClientLog.Exception("AdminRuntimeOps.Requalify", ex);
                var message = GetAdminRuntimeRequalifyFailedMessage(lang);
                SetStateBanner(message);
                Status(message + " " + ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        async Task ReconcileStaleRuntimeAsync()
        {
            if (isLoading)
                return;

            SetBusy(true);
            SetStateBanner(GetAdminRuntimeReconcileStaleLoadingMessage(lang));

            try
            {
                var response = await _api.AdminRuntimeReconcileStaleAsync(
                    capabilityKey: null,
                    overlayCts.Token).ConfigureAwait(true);
                var message = BuildAdminRuntimeReconcileStaleSummaryMessage(response, lang);
                SetStateBanner(message, positive: true);
                Status(message);
                var snapshot = ParseAdminRuntimeOperationalSnapshot(
                    await _api.AdminRuntimeOperationalSummaryAsync(overlayCts.Token).ConfigureAwait(true));
                Render(snapshot);
            }
            catch (Exception ex)
            {
                ClientLog.Exception("AdminRuntimeOps.ReconcileStale", ex);
                var message = GetAdminRuntimeReconcileStaleFailedMessage(lang);
                SetStateBanner(message);
                Status(message + " " + ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        async Task RefreshBlacklistBannerAsync()
        {
            try
            {
                var read = await GovernanceArtifactStore.ReadAsync<BlacklistArtifact>(
                    GovernanceArtifactStore.BlacklistFile,
                    ct: overlayCts.Token).ConfigureAwait(true);
                var activeCount = read.Value?.Items.Count(rule =>
                    rule.Active
                    && (rule.ExpiresAt is null || rule.ExpiresAt > DateTimeOffset.UtcNow)) ?? 0;
                SetBlacklistBanner(
                    BuildAdminRuntimeBlacklistSummaryMessage(read, lang),
                    positive: read.Status == GovernanceArtifactReadStatus.Ok && activeCount == 0);
            }
            catch (Exception ex)
            {
                ClientLog.Exception("AdminRuntimeOps.Blacklist", ex);
                SetBlacklistBanner(GetAdminRuntimeBlacklistReadFailedMessage(lang));
            }
        }

        FrameworkElement BuildCapabilityCard(AdminRuntimeOperationalItem item)
        {
            var isCapabilityB = string.Equals(item.Key, "capability_b.backoffice_generation", StringComparison.Ordinal);
            var title = string.Equals(item.Key, "capability_a.corpus_enrichment", StringComparison.Ordinal)
                ? ClientUiText.Get("admin.runtime.section.capability_a", lang)
                : isCapabilityB
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

            if (isCapabilityB)
            {
                var campaignOperationalText = BuildCapabilityBCampaignOperationalText(item.Summary, lang);
                if (!string.IsNullOrWhiteSpace(campaignOperationalText))
                {
                    facts.Children.Add(new TextBlock
                    {
                        Text = campaignOperationalText,
                        TextWrapping = TextWrapping.WrapWholeWords
                    });
                }
            }

            var latestCampaignText = BuildLatestCampaignText(item.Summary, lang);
            if (!string.IsNullOrWhiteSpace(latestCampaignText))
            {
                facts.Children.Add(new TextBlock
                {
                    Text = latestCampaignText,
                    TextWrapping = TextWrapping.WrapWholeWords
                });
            }

            if (isCapabilityB)
            {
                var reasonCountsText = BuildCapabilityBReasonCountsText(item.Summary.ReasonCounts, lang);
                if (!string.IsNullOrWhiteSpace(reasonCountsText))
                {
                    facts.Children.Add(new TextBlock
                    {
                        Text = reasonCountsText,
                        TextWrapping = TextWrapping.WrapWholeWords
                    });
                }

                var llmFailureCountsText = BuildLlmFailureCountsText(item.Summary.LlmFailureCounts, lang);
                if (!string.IsNullOrWhiteSpace(llmFailureCountsText))
                {
                    facts.Children.Add(new TextBlock
                    {
                        Text = llmFailureCountsText,
                        TextWrapping = TextWrapping.WrapWholeWords
                    });
                }
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
                var openKpisButton = BuildDialogInlineButton(
                    ClientUiText.Get("admin.runtime.action.open_a_kpis", lang),
                    accentStatus: item.Implemented ? "running" : null);
                openKpisButton.Click += async (_, __) =>
                    await OpenCapabilityAKpisAsync().ConfigureAwait(true);
                RegisterActionButton(openKpisButton, item.Implemented);
                actions.Children.Add(openKpisButton);

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

            if (isCapabilityB)
            {
                var openQualityButton = BuildDialogInlineButton(
                    ClientUiText.Get("admin.runtime.action.open_b_quality", lang),
                    accentStatus: item.Implemented ? "running" : null);
                openQualityButton.Click += async (_, __) =>
                    await OpenCapabilityBQualityReviewAsync().ConfigureAwait(true);
                RegisterActionButton(openQualityButton, item.Implemented);
                actions.Children.Add(openQualityButton);

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
            var schedulerText = BuildCapabilityBIdleSchedulerText(snapshot.Summary.CapabilityBIdleScheduler, lang);
            SetStateBanner(
                string.IsNullOrWhiteSpace(schedulerText)
                    ? $"{snapshot.Environment} | {ClientUiText.Get("status.ready", lang)}"
                    : $"{snapshot.Environment} | {ClientUiText.Get("status.ready", lang)} | {schedulerText}",
                positive: snapshot.Summary.CapabilityBIdleScheduler?.IsIdle != false);
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
            SetBlacklistBanner(GetAdminRuntimeBlacklistLoadingMessage(lang));

            try
            {
                var json = await _api.AdminRuntimeOperationalSummaryAsync(overlayCts.Token).ConfigureAwait(true);
                var snapshot = ParseAdminRuntimeOperationalSnapshot(json);
                Render(snapshot);
                await RefreshLocalRuntimeSummaryAsync().ConfigureAwait(true);
                await RefreshBlacklistBannerAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                ClientLog.Exception("AdminRuntimeOps.Load", ex);
                SetStateBanner(ClientUiText.Get("admin.runtime.load_failed", lang));
                runtimeHost.Content = BuildDialogInfoBanner(LocalRuntimeText("Diagnostic runtime local indisponible.", "Local runtime diagnostics unavailable.", "Diagnostico runtime local no disponible.", "Diagnostico runtime local indisponivel.", "Lokale Runtime-Diagnose nicht verfuegbar.", "Diagnostica runtime locale non disponibile.", lang));
                capabilitiesHost.Children.Clear();
                capabilitiesHost.Children.Add(BuildDialogInfoBanner(FormatAdminLoadError(ex, "/admin/runtime/operational-summary", lang)));
            }
            finally
            {
                SetBusy(false);
            }
        }

        reconcileStaleButton.Click += async (_, __) => await ReconcileStaleRuntimeAsync().ConfigureAwait(true);
        requalifyButton.Click += async (_, __) => await RequalifyRuntimeAsync().ConfigureAwait(true);
        refreshButton.Click += async (_, __) => await LoadAsync().ConfigureAwait(true);
        closeButton.Click += (_, __) => overlay?.Close();

        var dialogSize = GetDialogMaxSize(980, 760, horizontalMargin: 72, verticalMargin: 96);
        overlay = ShowOverlayDialog(
            BuildScrollableDialogShell(
                ClientUiText.Get("help.section.admin", lang),
                ClientUiText.Get("admin.runtime.title", lang),
                ClientUiText.Get("admin.runtime.subtitle", lang),
                new UIElement[]
                {
                    generatedText,
                    stateHost,
                    blacklistHost,
                    runtimeHost,
                    BuildDialogSurfaceCard(metricsGrid, new Thickness(12)),
                    BuildDialogSurfaceCard(capabilitiesHost, new Thickness(12))
                },
                footer,
                dialogSize.Width,
                dialogSize.Height),
            // Admin panel — must dismiss only via the explicit "Fermer" button so an
            // accidental click outside doesn't close mid-action. Other admin overlays
            // already follow this pattern.
            closeOnBackgroundTap: false);

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
                CapabilityBLatestCampaignProgressPercent: TryGetInt(summaryElement, "capabilityBLatestCampaignProgressPercent"),
                CapabilityBIdleScheduler: ParseCapabilityBIdleScheduler(summaryElement))
            : new AdminRuntimeOperationalTotals(0, 0, 0, 0, 0, 0, null, null);

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
                        ReasonCounts: ReadIntDictionary(itemSummaryElement, "reasonCounts"),
                        OffsetBackfillCandidateCount: TryGetInt(itemSummaryElement, "offsetBackfillCandidateCount"),
                        LatestCampaignProgressPercent: TryGetInt(itemSummaryElement, "latestCampaignProgressPercent"),
                        LatestCampaignId: TryGetString(itemSummaryElement, "latestCampaignId"),
                        LatestCampaignStatus: TryGetString(itemSummaryElement, "latestCampaignStatus"),
                        LatestCampaignOccurredAt: TryGetDateTimeOffset(itemSummaryElement, "latestCampaignOccurredAt"),
                        LlmFailureCounts: ReadIntDictionary(itemSummaryElement, "llmFailureCounts")),
                    Recommendations: recommendations));
            }
        }

        return new AdminRuntimeOperationalSnapshot(
            Environment: TryGetString(root, "environment") ?? string.Empty,
            GeneratedAt: TryGetDateTimeOffset(root, "generatedAt"),
            Summary: summary,
            Items: items);
    }

    private static IReadOnlyDictionary<string, int> ReadIntDictionary(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var property) || property.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in property.EnumerateObject())
        {
            if (item.Value.ValueKind == JsonValueKind.Number && item.Value.TryGetInt32(out var numericValue))
            {
                result[item.Name] = numericValue;
                continue;
            }

            if (item.Value.ValueKind == JsonValueKind.String && int.TryParse(item.Value.GetString(), out var stringValue))
                result[item.Name] = stringValue;
        }

        return result;
    }

    private static string BuildCapabilityBCampaignOperationalText(AdminRuntimeOperationalItemSummary summary, string lang)
    {
        if (summary.ActiveCampaignCount <= 0
            && summary.TerminalCapabilityJobCount <= 0
            && summary.StoredSummaryCount <= 0)
        {
            return string.Empty;
        }

        return LocalRuntimeText(
            $"Campagnes actives: {summary.ActiveCampaignCount} | Jobs termines: {summary.TerminalCapabilityJobCount} | resumes stockes: {summary.StoredSummaryCount}",
            $"Active campaigns: {summary.ActiveCampaignCount} | Terminal jobs: {summary.TerminalCapabilityJobCount} | Stored summaries: {summary.StoredSummaryCount}",
            $"Campanas activas: {summary.ActiveCampaignCount} | Jobs terminales: {summary.TerminalCapabilityJobCount} | resumenes guardados: {summary.StoredSummaryCount}",
            $"Campanhas ativas: {summary.ActiveCampaignCount} | Jobs terminais: {summary.TerminalCapabilityJobCount} | resumos guardados: {summary.StoredSummaryCount}",
            $"Aktive Kampagnen: {summary.ActiveCampaignCount} | Terminale Jobs: {summary.TerminalCapabilityJobCount} | Gespeicherte Zusammenfassungen: {summary.StoredSummaryCount}",
            $"Campagne attive: {summary.ActiveCampaignCount} | Job terminali: {summary.TerminalCapabilityJobCount} | riepiloghi salvati: {summary.StoredSummaryCount}",
            lang);
    }

    private static string BuildLatestCampaignText(AdminRuntimeOperationalItemSummary summary, string lang)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(summary.LatestCampaignStatus))
        {
            parts.Add(LocalRuntimeText(
                $"statut {ResolveCampaignStatusLabel(summary.LatestCampaignStatus, lang)}",
                $"status {ResolveCampaignStatusLabel(summary.LatestCampaignStatus, lang)}",
                $"estado {ResolveCampaignStatusLabel(summary.LatestCampaignStatus, lang)}",
                $"estado {ResolveCampaignStatusLabel(summary.LatestCampaignStatus, lang)}",
                $"Status {ResolveCampaignStatusLabel(summary.LatestCampaignStatus, lang)}",
                $"stato {ResolveCampaignStatusLabel(summary.LatestCampaignStatus, lang)}",
                lang));
        }

        if (summary.LatestCampaignProgressPercent is int progress)
        {
            parts.Add(LocalRuntimeText(
                $"progression {progress}%",
                $"progress {progress}%",
                $"progreso {progress}%",
                $"progresso {progress}%",
                $"Fortschritt {progress}%",
                $"avanzamento {progress}%",
                lang));
        }

        if (summary.LatestCampaignOccurredAt is DateTimeOffset occurredAt)
        {
            var occurredAtText = occurredAt.ToLocalTime().ToString("g");
            parts.Add(LocalRuntimeText(
                $"le {occurredAtText}",
                $"at {occurredAtText}",
                $"el {occurredAtText}",
                $"em {occurredAtText}",
                $"am {occurredAtText}",
                $"il {occurredAtText}",
                lang));
        }

        if (!string.IsNullOrWhiteSpace(summary.LatestCampaignId))
        {
            parts.Add("id " + ShortenCampaignId(summary.LatestCampaignId));
        }

        if (parts.Count == 0)
            return string.Empty;

        var detail = string.Join(" | ", parts);
        return LocalRuntimeText(
            $"Derniere campagne: {detail}",
            $"Latest campaign: {detail}",
            $"Ultima campana: {detail}",
            $"Ultima campanha: {detail}",
            $"Letzte Kampagne: {detail}",
            $"Ultima campagna: {detail}",
            lang);
    }

    private static string BuildCapabilityBReasonCountsText(IReadOnlyDictionary<string, int> reasonCounts, string lang)
    {
        var detail = string.Join(", ", reasonCounts
            .Where(static item => item.Value > 0)
            .OrderByDescending(static item => item.Value)
            .ThenBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .Select(item => $"{ResolveCapabilityBReasonLabel(item.Key, lang)}: {item.Value}"));

        if (string.IsNullOrWhiteSpace(detail))
            return string.Empty;

        return LocalRuntimeText(
            $"Raisons principales: {detail}",
            $"Top reasons: {detail}",
            $"Motivos principales: {detail}",
            $"Principais motivos: {detail}",
            $"Hauptgruende: {detail}",
            $"Motivi principali: {detail}",
            lang);
    }

    private static string BuildLlmFailureCountsText(IReadOnlyDictionary<string, int> failureCounts, string lang)
    {
        var detail = string.Join(", ", failureCounts
            .Where(static item => item.Value > 0)
            .OrderByDescending(static item => item.Value)
            .ThenBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .Select(item => $"{ResolveLlmFailureCategoryLabel(item.Key, lang)}: {item.Value}"));

        if (string.IsNullOrWhiteSpace(detail))
            return string.Empty;

        return LocalRuntimeText(
            $"Echecs LLM serveur: {detail}",
            $"Server LLM failures: {detail}",
            $"Fallos LLM servidor: {detail}",
            $"Falhas LLM servidor: {detail}",
            $"Server-LLM-Fehler: {detail}",
            $"Errori LLM server: {detail}",
            lang);
    }

    private static string ResolveLlmFailureCategoryLabel(string? category, string lang)
        => category switch
        {
            "queue" => LocalRuntimeText("file pleine", "queue full", "cola llena", "fila cheia", "Warteschlange voll", "coda piena", lang),
            "timeout" => LocalRuntimeText("timeout", "timeout", "timeout", "timeout", "Timeout", "timeout", lang),
            "http" => LocalRuntimeText("HTTP/runtime", "HTTP/runtime", "HTTP/runtime", "HTTP/runtime", "HTTP/Runtime", "HTTP/runtime", lang),
            "transport" => LocalRuntimeText("transport", "transport", "transporte", "transporte", "Transport", "trasporto", lang),
            "empty" => LocalRuntimeText("reponse vide", "empty response", "respuesta vacia", "resposta vazia", "Leere Antwort", "risposta vuota", lang),
            "configuration" => LocalRuntimeText("configuration", "configuration", "configuracion", "configuracao", "Konfiguration", "configurazione", lang),
            "quality" => LocalRuntimeText("qualite sortie", "output quality", "calidad salida", "qualidade saida", "Ausgabequalitat", "qualita output", lang),
            "exception" => LocalRuntimeText("exception", "exception", "excepcion", "excecao", "Exception", "eccezione", lang),
            _ => string.IsNullOrWhiteSpace(category) ? LocalRuntimeText("inconnu", "unknown", "desconocido", "desconhecido", "Unbekannt", "sconosciuto", lang) : category
        };

    private static string ResolveCampaignStatusLabel(string? status, string? lang)
        => status switch
        {
            "dry_run" => LocalRuntimeText("dry-run", "dry run", "dry-run", "dry-run", "Dry-Run", "dry-run", lang),
            "executed" => LocalRuntimeText("executee", "executed", "ejecutada", "executada", "ausgefuehrt", "eseguita", lang),
            null or "" => "-",
            _ => HumanizeRuntimeIdentifier(status)
        };

    private static string ResolveCapabilityBReasonLabel(string reason, string? lang)
        => reason switch
        {
            "summary_missing" => LocalRuntimeText("resume manquant", "summary missing", "resumen ausente", "resumo ausente", "Zusammenfassung fehlt", "riepilogo mancante", lang),
            "summary_stale" => LocalRuntimeText("resume obsolete", "summary stale", "resumen obsoleto", "resumo obsoleto", "Zusammenfassung veraltet", "riepilogo obsoleto", lang),
            "active_summary_job_exists" => LocalRuntimeText("job resume actif", "active summary job", "job resumen activo", "job resumo ativo", "aktiver Zusammenfassungsjob", "job riepilogo attivo", lang),
            "recent_summary_job_failure" => LocalRuntimeText("echec recent", "recent failure", "fallo reciente", "falha recente", "letzter Fehler", "errore recente", lang),
            "recent_summary_job_cancellation" => LocalRuntimeText("annulation recente", "recent cancellation", "cancelacion reciente", "cancelamento recente", "letzter Abbruch", "annullamento recente", lang),
            _ => HumanizeRuntimeIdentifier(reason)
        };

    private static string HumanizeRuntimeIdentifier(string value)
    {
        var cleaned = value.Trim();
        if (cleaned.Length == 0)
            return "-";

        return cleaned.Replace('_', ' ').Replace('-', ' ');
    }

    private static string ShortenCampaignId(string campaignId)
    {
        var trimmed = campaignId.Trim();
        return trimmed.Length <= 8 ? trimmed : trimmed[..8];
    }

    private static AdminRuntimeCapabilityBIdleScheduler? ParseCapabilityBIdleScheduler(JsonElement summaryElement)
    {
        if (!TryGetPropertyIgnoreCase(summaryElement, "capabilityBIdleScheduler", out var scheduler)
            || scheduler.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new AdminRuntimeCapabilityBIdleScheduler(
            Enabled: TryGetBool(scheduler, "enabled") ?? false,
            RequiresIngestionIdle: TryGetBool(scheduler, "requiresIngestionIdle") ?? false,
            AutoEnqueueEnabled: TryGetBool(scheduler, "autoEnqueueEnabled") ?? false,
            IsIdle: TryGetBool(scheduler, "isIdle") ?? false,
            State: TryGetString(scheduler, "state") ?? string.Empty,
            Reason: TryGetString(scheduler, "reason") ?? string.Empty,
            ActiveIngestionJobs: TryGetInt(scheduler, "activeIngestionJobs") ?? 0,
            LastIngestionActivityAt: TryGetDateTimeOffset(scheduler, "lastIngestionActivityAt"),
            RequiredIdleSeconds: TryGetInt(scheduler, "requiredIdleSeconds") ?? 0,
            IdleForSeconds: TryGetInt(scheduler, "idleForSeconds"),
            AutoEnqueueBatchSize: TryGetInt(scheduler, "autoEnqueueBatchSize") ?? 0,
            RunningJobLeaseTimeoutSeconds: TryGetInt(scheduler, "runningJobLeaseTimeoutSeconds") ?? 0);
    }

    private string BuildCapabilityBIdleSchedulerText(AdminRuntimeCapabilityBIdleScheduler? scheduler, string lang)
    {
        if (scheduler is null)
            return string.Empty;

        if (!scheduler.Enabled)
        {
            return LocalRuntimeText(
                "Resumes serveur: worker desactive",
                "Server summaries: worker disabled",
                "Resumenes servidor: worker desactivado",
                "Resumos servidor: worker desativado",
                "Server-Zusammenfassungen: Worker deaktiviert",
                "Riassunti server: worker disattivato",
                lang);
        }

        if (!scheduler.AutoEnqueueEnabled)
        {
            return LocalRuntimeText(
                "Resumes serveur: file auto desactivee",
                "Server summaries: auto queue disabled",
                "Resumenes servidor: cola automatica desactivada",
                "Resumos servidor: fila automatica desativada",
                "Server-Zusammenfassungen: Auto-Warteschlange deaktiviert",
                "Riassunti server: coda automatica disattivata",
                lang);
        }

        if (!scheduler.RequiresIngestionIdle)
        {
            return LocalRuntimeText(
                "Resumes serveur: execution libre",
                "Server summaries: unrestricted execution",
                "Resumenes servidor: ejecucion libre",
                "Resumos servidor: execucao livre",
                "Server-Zusammenfassungen: freie Ausfuehrung",
                "Riassunti server: esecuzione libera",
                lang);
        }

        if (scheduler.IsIdle)
        {
            return LocalRuntimeText(
                $"Resumes serveur: ingestion inactive, lot max {scheduler.AutoEnqueueBatchSize}, lease {scheduler.RunningJobLeaseTimeoutSeconds}s",
                $"Server summaries: ingestion idle, max batch {scheduler.AutoEnqueueBatchSize}, lease {scheduler.RunningJobLeaseTimeoutSeconds}s",
                $"Resumenes servidor: ingestion inactiva, lote max {scheduler.AutoEnqueueBatchSize}, lease {scheduler.RunningJobLeaseTimeoutSeconds}s",
                $"Resumos servidor: ingestao inativa, lote max {scheduler.AutoEnqueueBatchSize}, lease {scheduler.RunningJobLeaseTimeoutSeconds}s",
                $"Server-Zusammenfassungen: Ingestion inaktiv, max. Batch {scheduler.AutoEnqueueBatchSize}, Lease {scheduler.RunningJobLeaseTimeoutSeconds}s",
                $"Riassunti server: ingestione inattiva, batch max {scheduler.AutoEnqueueBatchSize}, lease {scheduler.RunningJobLeaseTimeoutSeconds}s",
                lang);
        }

        if (scheduler.ActiveIngestionJobs > 0)
        {
            return LocalRuntimeText(
                $"Resumes serveur en attente: {scheduler.ActiveIngestionJobs} ingestion(s) active(s)",
                $"Server summaries waiting: {scheduler.ActiveIngestionJobs} active ingestion job(s)",
                $"Resumenes servidor en espera: {scheduler.ActiveIngestionJobs} ingestion(es) activa(s)",
                $"Resumos servidor aguardando: {scheduler.ActiveIngestionJobs} ingestao(oes) ativa(s)",
                $"Server-Zusammenfassungen warten: {scheduler.ActiveIngestionJobs} aktive Ingestion(s)",
                $"Riassunti server in attesa: {scheduler.ActiveIngestionJobs} ingestione/i attiva/e",
                lang);
        }

        var idleFor = scheduler.IdleForSeconds.GetValueOrDefault();
        return LocalRuntimeText(
            $"Resumes serveur en attente: idle ingestion {idleFor}s/{scheduler.RequiredIdleSeconds}s",
            $"Server summaries waiting: ingestion idle {idleFor}s/{scheduler.RequiredIdleSeconds}s",
            $"Resumenes servidor en espera: ingestion inactiva {idleFor}s/{scheduler.RequiredIdleSeconds}s",
            $"Resumos servidor aguardando: ingestao inativa {idleFor}s/{scheduler.RequiredIdleSeconds}s",
            $"Server-Zusammenfassungen warten: Ingestion inaktiv {idleFor}s/{scheduler.RequiredIdleSeconds}s",
            $"Riassunti server in attesa: ingestione inattiva {idleFor}s/{scheduler.RequiredIdleSeconds}s",
            lang);
    }

    private static string BuildAdminRuntimeRequalifySummaryMessage(JsonElement root, string lang)
    {
        var itemCount = 0;
        var qualifiedCount = 0;
        var selectedCount = 0;
        var warmupCount = 0;

        if (TryGetPropertyIgnoreCase(root, "items", out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemsElement.EnumerateArray())
            {
                itemCount++;
                if (TryGetBool(item, "qualified") == true)
                    qualifiedCount++;
                if (TryGetBool(item, "selected") == true)
                    selectedCount++;
            }
        }

        if (TryGetPropertyIgnoreCase(root, "warmupResults", out var warmupsElement) && warmupsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var _ in warmupsElement.EnumerateArray())
                warmupCount++;
        }

        return lang switch
        {
            "en" => $"Requalification completed: {itemCount} capability(ies), {qualifiedCount} qualified, {selectedCount} selected, {warmupCount} warmup result(s).",
            "de" => $"Neuqualifizierung abgeschlossen: {itemCount} Faehigkeit(en), {qualifiedCount} qualifiziert, {selectedCount} ausgewaehlt, {warmupCount} Warmup-Ergebnis(se).",
            "es" => $"Recalificacion completada: {itemCount} capacidad(es), {qualifiedCount} cualificada(s), {selectedCount} seleccionada(s), {warmupCount} resultado(s) de warmup.",
            "it" => $"Ririqualificazione completata: {itemCount} capacita, {qualifiedCount} qualificata/e, {selectedCount} selezionata/e, {warmupCount} risultato/i warmup.",
            "pt" => $"Requalificacao concluida: {itemCount} capacidade(s), {qualifiedCount} qualificada(s), {selectedCount} selecionada(s), {warmupCount} resultado(s) de warmup.",
            _ => $"Requalification terminee : {itemCount} capacite(s), {qualifiedCount} qualifiee(s), {selectedCount} selectionnee(s), {warmupCount} warmup result(s)."
        };
    }

    private static string GetAdminRuntimeRequalifyLoadingMessage(string lang)
        => lang switch
        {
            "en" => "Runtime requalification in progress...",
            "de" => "Runtime-Neuqualifizierung laeuft...",
            "es" => "Recalificacion runtime en curso...",
            "it" => "Ririqualificazione runtime in corso...",
            "pt" => "Requalificacao runtime em curso...",
            _ => "Requalification runtime en cours..."
        };

    private static string GetAdminRuntimeRequalifyFailedMessage(string lang)
        => lang switch
        {
            "en" => "Could not start runtime requalification.",
            "de" => "Runtime-Neuqualifizierung konnte nicht gestartet werden.",
            "es" => "No se pudo iniciar la recalificacion runtime.",
            "it" => "Impossibile avviare la ririqualificazione runtime.",
            "pt" => "Nao foi possivel iniciar a requalificacao runtime.",
            _ => "Impossible de lancer la requalification runtime."
        };

    private static string BuildAdminRuntimeReconcileStaleSummaryMessage(JsonElement root, string lang)
    {
        var updatedCount = TryGetInt(root, "updatedCount") ?? 0;
        var itemCount = 0;
        var staleRemaining = 0;

        if (TryGetPropertyIgnoreCase(root, "items", out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemsElement.EnumerateArray())
            {
                itemCount++;
                if (TryGetBool(item, "stale") == true)
                    staleRemaining++;
            }
        }

        return lang switch
        {
            "en" => $"Stale runtime reconciliation completed: {updatedCount} updated, {itemCount} state item(s), {staleRemaining} stale remaining.",
            "de" => $"Stale-Runtime-Abgleich abgeschlossen: {updatedCount} aktualisiert, {itemCount} Statuseintrag(e), {staleRemaining} weiter stale.",
            "es" => $"Reconciliacion stale completada: {updatedCount} actualizado(s), {itemCount} estado(s), {staleRemaining} stale restante(s).",
            "it" => $"Riconciliazione stale completata: {updatedCount} aggiornato/i, {itemCount} stato/i, {staleRemaining} stale residuo/i.",
            "pt" => $"Reconciliacao stale concluida: {updatedCount} atualizado(s), {itemCount} estado(s), {staleRemaining} stale restante(s).",
            _ => $"Reconciliation stale terminee : {updatedCount} mis a jour, {itemCount} etat(s), {staleRemaining} stale restant(s)."
        };
    }

    private static string GetAdminRuntimeReconcileStaleButtonLabel(string lang)
        => lang switch
        {
            "en" => "Reconcile stale",
            "de" => "Stale abgleichen",
            "es" => "Reconciliar stale",
            "it" => "Riconcilia stale",
            "pt" => "Reconciliar stale",
            _ => "Reconcile stale"
        };

    private static string GetAdminRuntimeReconcileStaleLoadingMessage(string lang)
        => lang switch
        {
            "en" => "Stale runtime reconciliation in progress...",
            "de" => "Stale-Runtime-Abgleich laeuft...",
            "es" => "Reconciliacion stale en curso...",
            "it" => "Riconciliazione stale in corso...",
            "pt" => "Reconciliacao stale em curso...",
            _ => "Reconciliation stale en cours..."
        };

    private static string GetAdminRuntimeReconcileStaleFailedMessage(string lang)
        => lang switch
        {
            "en" => "Could not start stale runtime reconciliation.",
            "de" => "Stale-Runtime-Abgleich konnte nicht gestartet werden.",
            "es" => "No se pudo iniciar la reconciliacion stale.",
            "it" => "Impossibile avviare la riconciliazione stale.",
            "pt" => "Nao foi possivel iniciar a reconciliacao stale.",
            _ => "Impossible de lancer la reconciliation stale."
        };

    private static string BuildAdminRuntimeBlacklistSummaryMessage(
        GovernanceArtifactReadResult<BlacklistArtifact> read,
        string lang)
    {
        if (read.Status == GovernanceArtifactReadStatus.Missing)
            return GetAdminRuntimeBlacklistMissingMessage(lang);

        if (read.Status != GovernanceArtifactReadStatus.Ok || read.Value is null)
            return GetAdminRuntimeBlacklistInvalidMessage(read.Status, lang);

        var now = DateTimeOffset.UtcNow;
        var activeRules = read.Value.Items
            .Where(rule => rule.Active && (rule.ExpiresAt is null || rule.ExpiresAt > now))
            .ToArray();

        if (activeRules.Length == 0)
            return lang switch
            {
                "en" => "Local blacklist: 0 active rule. Read-only.",
                "de" => "Lokale Blacklist: 0 aktive Regel. Nur Lesen.",
                "es" => "Blacklist local: 0 regla activa. Solo lectura.",
                "it" => "Blacklist locale: 0 regole attive. Sola lettura.",
                "pt" => "Blacklist local: 0 regra ativa. Somente leitura.",
                _ => "Blacklist locale : 0 regle active. Lecture seule."
            };

        var preview = string.Join(", ", activeRules
            .Take(3)
            .Select(rule => string.IsNullOrWhiteSpace(rule.RuleId) ? "rule" : rule.RuleId.Trim()));
        var suffix = activeRules.Length > 3 ? $" +{activeRules.Length - 3}" : string.Empty;

        return lang switch
        {
            "en" => $"Local blacklist: {activeRules.Length} active rule(s) ({preview}{suffix}). Read-only.",
            "de" => $"Lokale Blacklist: {activeRules.Length} aktive Regel(n) ({preview}{suffix}). Nur Lesen.",
            "es" => $"Blacklist local: {activeRules.Length} regla(s) activa(s) ({preview}{suffix}). Solo lectura.",
            "it" => $"Blacklist locale: {activeRules.Length} regola/e attiva/e ({preview}{suffix}). Sola lettura.",
            "pt" => $"Blacklist local: {activeRules.Length} regra(s) ativa(s) ({preview}{suffix}). Somente leitura.",
            _ => $"Blacklist locale : {activeRules.Length} regle(s) active(s) ({preview}{suffix}). Lecture seule."
        };
    }

    private static string GetAdminRuntimeBlacklistLoadingMessage(string lang)
        => lang switch
        {
            "en" => "Local blacklist: loading...",
            "de" => "Lokale Blacklist: wird geladen...",
            "es" => "Blacklist local: cargando...",
            "it" => "Blacklist locale: caricamento...",
            "pt" => "Blacklist local: carregando...",
            _ => "Blacklist locale : chargement..."
        };

    private static string GetAdminRuntimeBlacklistMissingMessage(string lang)
        => lang switch
        {
            "en" => "Local blacklist: artifact missing. Read-only.",
            "de" => "Lokale Blacklist: Artefakt fehlt. Nur Lesen.",
            "es" => "Blacklist local: artefacto ausente. Solo lectura.",
            "it" => "Blacklist locale: artefatto assente. Sola lettura.",
            "pt" => "Blacklist local: artefato ausente. Somente leitura.",
            _ => "Blacklist locale : artefact absent. Lecture seule."
        };

    private static string GetAdminRuntimeBlacklistInvalidMessage(GovernanceArtifactReadStatus status, string lang)
        => lang switch
        {
            "en" => $"Local blacklist: unreadable ({status}). Read-only.",
            "de" => $"Lokale Blacklist: nicht lesbar ({status}). Nur Lesen.",
            "es" => $"Blacklist local: no legible ({status}). Solo lectura.",
            "it" => $"Blacklist locale: non leggibile ({status}). Sola lettura.",
            "pt" => $"Blacklist local: ilegivel ({status}). Somente leitura.",
            _ => $"Blacklist locale : non lisible ({status}). Lecture seule."
        };

    private static string GetAdminRuntimeBlacklistReadFailedMessage(string lang)
        => lang switch
        {
            "en" => "Local blacklist: read failed.",
            "de" => "Lokale Blacklist: Lesen fehlgeschlagen.",
            "es" => "Blacklist local: error de lectura.",
            "it" => "Blacklist locale: lettura non riuscita.",
            "pt" => "Blacklist local: falha de leitura.",
            _ => "Blacklist locale : lecture impossible."
        };
}
