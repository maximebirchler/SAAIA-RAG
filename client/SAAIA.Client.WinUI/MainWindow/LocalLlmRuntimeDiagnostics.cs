using System;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SAAIA.Client.WinUI.Services;

namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow
{
    private async void LocalLlmRuntimeDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ShowLocalLlmRuntimeDiagnosticsAsync();
        }
        catch (Exception ex)
        {
            LocalLlmStatusText.Text = "Diagnostic runtime impossible: " + ex.Message;
        }
    }

    private async Task ShowLocalLlmRuntimeDiagnosticsAsync()
    {
        FrameworkElement BuildMetricTile(string label, string value)
            => BuildDialogSurfaceCard(new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock
                    {
                        Text = label,
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = UseLightPalette() ? UiBrush(0x5D, 0x6E, 0x82) : UiBrush(0xA8, 0xB5, 0xC7)
                    },
                    new TextBlock
                    {
                        Text = value,
                        FontSize = 22,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = UseLightPalette() ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xF5, 0xF7, 0xFB),
                        TextWrapping = TextWrapping.WrapWholeWords
                    }
                }
            }, new Thickness(14));

        FrameworkElement BuildField(string label, string? value)
            => BuildDialogSurfaceCard(new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    BuildDialogFieldLabel(label),
                    new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(value) ? "-" : value,
                        TextWrapping = TextWrapping.WrapWholeWords,
                        IsTextSelectionEnabled = true,
                        Foreground = UseLightPalette() ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xF5, 0xF7, 0xFB)
                    }
                }
            }, new Thickness(14));

        static string FormatTimestamp(DateTimeOffset? value)
            => value?.ToLocalTime().ToString("g") ?? "-";

        static string FormatEvent(RuntimeEventLogItem item)
        {
            var stamp = item.At.ToLocalTime().ToString("g");
            var build = string.IsNullOrWhiteSpace(item.Build) ? "-" : item.Build;
            var previous = string.IsNullOrWhiteSpace(item.PreviousBuild) ? string.Empty : $" (prec. {item.PreviousBuild})";
            return $"{stamp} - {item.EventKind} - {build}{previous}";
        }

        string bannerText;
        var metricsGrid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        for (var i = 0; i < 3; i++)
            metricsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metricsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        metricsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var detailsGrid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        detailsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        detailsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 6; i++)
            detailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var bannerHost = new ContentPresenter();
        var progressBar = new ProgressBar
        {
            IsIndeterminate = false,
            Height = 6,
            Minimum = 0,
            Maximum = 1,
            Visibility = Visibility.Collapsed
        };
        var progressText = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            Opacity = 0.84,
            TextWrapping = TextWrapping.WrapWholeWords
        };

        var actionButton = BuildDialogFooterButton("Action", primary: true);
        var refreshButton = BuildDialogFooterButton("Actualiser");
        var closeButton = BuildDialogFooterButton(ClientUiText.Get("dialog.close", _appSettings.UiLanguage), primary: true);
        var dialogSize = GetDialogMaxSize(900, 760, horizontalMargin: 72, verticalMargin: 96);
        var shell = BuildScrollableDialogShell(
            "Runtime",
            "Diagnostic runtime local",
            "Etat du runtime actif, compatibilite modele/runtime et dernier warmup.",
            new UIElement[]
            {
                bannerHost,
                progressBar,
                progressText,
                BuildDialogSurfaceCard(metricsGrid, new Thickness(12)),
                BuildDialogSurfaceCard(detailsGrid, new Thickness(12))
            },
            BuildDialogFooter(actionButton, refreshButton, closeButton),
            dialogSize.Width,
            dialogSize.Height);

        OverlayDialogSession? overlay = null;
        var isBusy = false;
        LocalLlmRuntimeDiagnostics? currentDiagnostics = null;

        void SetBusy(bool busy)
        {
            isBusy = busy;
            actionButton.IsEnabled = !busy;
            refreshButton.IsEnabled = !busy;
            closeButton.IsEnabled = !busy;
        }

        void ShowProgress(string text, DownloadManager.ProgressInfo? progress = null)
        {
            progressText.Text = text;
            progressText.Visibility = Visibility.Visible;
            progressBar.Visibility = Visibility.Visible;

            if (progress?.TotalBytes is long total && total > 0)
            {
                progressBar.IsIndeterminate = false;
                progressBar.Maximum = total;
                progressBar.Value = Math.Min(total, Math.Max(0, progress.DownloadedBytes));
            }
            else
            {
                progressBar.IsIndeterminate = true;
                progressBar.Value = 0;
            }
        }

        void HideProgress()
        {
            progressBar.Visibility = Visibility.Collapsed;
            progressBar.IsIndeterminate = false;
            progressBar.Value = 0;
            progressText.Visibility = Visibility.Collapsed;
            progressText.Text = string.Empty;
        }

        void RenderDiagnostics(LocalLlmRuntimeDiagnostics diagnostics)
        {
            currentDiagnostics = diagnostics;

            if (diagnostics.UpgradeRequired)
            {
                bannerText = $"Le modele courant requiert un runtime plus recent ({diagnostics.RequiredBuild ?? "inconnu"}).";
                bannerHost.Content = BuildDialogInfoBanner(bannerText);
                actionButton.Content = "Mettre a niveau";
                actionButton.Visibility = Visibility.Visible;
            }
            else if (string.Equals(diagnostics.ActiveState, "pending_qualification", StringComparison.OrdinalIgnoreCase))
            {
                bannerText = $"Le runtime actif ({diagnostics.ActiveBuild ?? "inconnu"}) attend encore sa qualification warmup.";
                bannerHost.Content = BuildDialogInfoBanner(bannerText);
                actionButton.Content = "Demarrer et qualifier";
                actionButton.Visibility = Visibility.Visible;
            }
            else if (diagnostics.LatestWarmupStatus is WarmupGateStatus.FailBlock or WarmupGateStatus.FailFallback)
            {
                bannerText = "Le dernier warmup a echoue. Le runtime local doit etre reverifie.";
                bannerHost.Content = BuildDialogInfoBanner(bannerText);
                actionButton.Content = "Relancer qualification";
                actionButton.Visibility = Visibility.Visible;
            }
            else
            {
                bannerText = "Le runtime local est compatible avec le modele courant.";
                bannerHost.Content = BuildDialogInfoBanner(bannerText, positive: true);
                actionButton.Visibility = Visibility.Collapsed;
            }

            metricsGrid.Children.Clear();
            var metricTiles = new[]
            {
                BuildMetricTile("Runtime", diagnostics.RuntimeLabel),
                BuildMetricTile("Build actif", diagnostics.ActiveBuild ?? "inconnu"),
                BuildMetricTile("Build requis", diagnostics.RequiredBuild ?? "aucun"),
                BuildMetricTile("Etat", ResolveRuntimeStateLabel(diagnostics.ActiveState)),
                BuildMetricTile("Build precedent", diagnostics.PreviousBuild ?? "-"),
                BuildMetricTile("Warmup", ResolveWarmupStateLabel(diagnostics.LatestWarmupStatus))
            };
            for (var index = 0; index < metricTiles.Length; index++)
            {
                Grid.SetColumn(metricTiles[index], index % 3);
                Grid.SetRow(metricTiles[index], index / 3);
                metricsGrid.Children.Add(metricTiles[index]);
            }

            detailsGrid.Children.Clear();
            var detailCards = new[]
            {
                BuildField("Modele", diagnostics.ModelId),
                BuildField("Famille GGUF", diagnostics.ModelFamily),
                BuildField("Profil qualifie", diagnostics.QualifiedProfileId),
                BuildField("Policy flash-attn", ResolveFlashAttnPolicyLabel(diagnostics.ForcedFlashAttn)),
                BuildField("Active depuis", FormatTimestamp(diagnostics.ActivatedAtUtc)),
                BuildField("Qualifie le", FormatTimestamp(diagnostics.QualifiedAtUtc)),
                BuildField("Exe actif", diagnostics.ActiveExePath),
                BuildField("Manifest runtime", diagnostics.ActiveManifestPath),
                BuildField("Derniere raison warmup", diagnostics.LatestWarmupReason),
                BuildField("Compatibilite", diagnostics.CompatibilityReason),
                BuildField(
                    "Historique runtime",
                    diagnostics.RecentEvents.Count == 0
                        ? "Aucun evenement runtime recent."
                        : string.Join(Environment.NewLine, diagnostics.RecentEvents.Select(FormatEvent)))
            };
            for (var index = 0; index < detailCards.Length; index++)
            {
                Grid.SetColumn(detailCards[index], index % 2);
                Grid.SetRow(detailCards[index], index / 2);
                detailsGrid.Children.Add(detailCards[index]);
            }
        }

        async Task RefreshDiagnosticsAsync()
        {
            var settings = ReadLocalLlmSettingsFromUi();
            var diagnostics = await LocalLlmRuntimeDiagnosticsService.EvaluateAsync(settings).ConfigureAwait(true);
            RenderDiagnostics(diagnostics);
        }

        async Task UpgradeRuntimeAsync()
        {
            if (currentDiagnostics is null)
                return;

            var confirm = new ContentDialog
            {
                Title = "Confirmer la mise a niveau runtime",
                Content = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"Le modele courant demande un runtime {currentDiagnostics.RequiredBuild ?? "plus recent"}.",
                            TextWrapping = TextWrapping.WrapWholeWords
                        },
                        new TextBlock
                        {
                            Text = $"Runtime actuel : {currentDiagnostics.ActiveBuild ?? "inconnu"}",
                            TextWrapping = TextWrapping.WrapWholeWords,
                            Opacity = 0.82
                        },
                        new TextBlock
                        {
                            Text = "Le runtime sera telecharge dans un dossier versionne, puis devra etre qualifie par warmup avant usage nominal.",
                            TextWrapping = TextWrapping.WrapWholeWords,
                            Opacity = 0.82
                        }
                    }
                },
                PrimaryButtonText = "Mettre a niveau",
                CloseButtonText = ClientUiText.Get("dialog.close", _appSettings.UiLanguage),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Root.XamlRoot
            };
            ConfigureDialogChrome(confirm);
            var confirmed = await confirm.ShowAsync();
            if (confirmed != ContentDialogResult.Primary)
                return;

            await RuntimeEventLogStore.AppendAsync(new RuntimeEventLogItem(
                At: DateTimeOffset.UtcNow,
                RuntimeId: currentDiagnostics.RuntimeId,
                EventKind: "runtime_upgrade_confirmed",
                Build: currentDiagnostics.RequiredBuild,
                PreviousBuild: currentDiagnostics.ActiveBuild,
                ModelId: currentDiagnostics.ModelId,
                Detail: currentDiagnostics.CompatibilityReason));

            SetBusy(true);
            try
            {
                var settings = ReadLocalLlmSettingsFromUi();
                ShowProgress("Preparation de la mise a niveau runtime...");
                var progress = new Progress<DownloadManager.ProgressInfo>(p =>
                {
                    var label = p.Stage switch
                    {
                        "resolve" => "Resolution de la release runtime...",
                        "verify" => $"Verification : {p.Id}",
                        "download" => $"Telechargement : {p.Id}",
                        "extract" => "Extraction du runtime...",
                        "done" => $"OK : {p.Id}",
                        _ => $"{p.Stage} : {p.Id}"
                    };
                    ShowProgress(label, p);
                });

                var (ok, msg, _) = await _llmBootstrapper.EnsureAsync(settings, force: false, progress, CancellationToken.None).ConfigureAwait(true);
                _appSettings = AppSettings.Load();
                LoadLocalLlmUiFromSettings();

                if (!ok)
                {
                    bannerHost.Content = BuildDialogInfoBanner("Mise a niveau runtime impossible : " + msg);
                    return;
                }

                bannerHost.Content = BuildDialogInfoBanner(
                    "Mise a niveau terminee. Qualification warmup requise avant usage nominal.",
                    positive: true);
                await RefreshDiagnosticsAsync().ConfigureAwait(true);
            }
            finally
            {
                HideProgress();
                SetBusy(false);
            }
        }

        async Task RunQualificationAsync()
        {
            SetBusy(true);
            try
            {
                ShowProgress("Demarrage du runtime et qualification warmup...");
                var ok = _llmProc.IsRunning
                    ? await RunLocalLlmWarmupQualificationAsync(ReadLocalLlmSettingsFromUi(), null, CancellationToken.None).ConfigureAwait(true)
                    : await EnsureLocalLlmStartedAsync(CancellationToken.None).ConfigureAwait(true);

                _appSettings = AppSettings.Load();
                LoadLocalLlmUiFromSettings();

                bannerHost.Content = ok
                    ? BuildDialogInfoBanner("Qualification runtime terminee.", positive: true)
                    : BuildDialogInfoBanner("La qualification runtime a echoue. Le rollback a ete applique si un runtime sain etait disponible.");

                await RefreshDiagnosticsAsync().ConfigureAwait(true);
            }
            finally
            {
                HideProgress();
                SetBusy(false);
            }
        }

        closeButton.Click += (_, _) => overlay?.Close();
        refreshButton.Click += async (_, _) =>
        {
            if (isBusy)
                return;
            await RefreshDiagnosticsAsync().ConfigureAwait(true);
        };
        actionButton.Click += async (_, _) =>
        {
            if (isBusy || currentDiagnostics is null)
                return;

            if (currentDiagnostics.UpgradeRequired)
            {
                await UpgradeRuntimeAsync().ConfigureAwait(true);
                return;
            }

            await RunQualificationAsync().ConfigureAwait(true);
        };

        overlay = ShowOverlayDialog(
            shell,
            resizeHandler: _ =>
            {
                var size = GetDialogMaxSize(900, 760, horizontalMargin: 72, verticalMargin: 96);
                shell.MaxWidth = size.Width;
                shell.MaxHeight = size.Height;
            });

        await RefreshDiagnosticsAsync().ConfigureAwait(true);
        await overlay.Completion;
    }

    private static string ResolveRuntimeStateLabel(string? state)
        => state switch
        {
            "qualified" => "qualifie",
            "pending_qualification" => "qualification en attente",
            null or "" => "legacy / non suivi",
            _ => state
        };

    private static string ResolveWarmupStateLabel(WarmupGateStatus? status)
        => status switch
        {
            WarmupGateStatus.Pass => "pass",
            WarmupGateStatus.PassDegraded => "degrade",
            WarmupGateStatus.FailBlock => "blocage",
            WarmupGateStatus.FailFallback => "fallback",
            null => "aucun",
            _ => status?.ToString() ?? "aucun"
        };

    private static string ResolveFlashAttnPolicyLabel(bool? forcedFlashAttn)
        => forcedFlashAttn switch
        {
            true => "force on",
            false => "force off",
            null => "auto / aucune surcharge"
        };
}
