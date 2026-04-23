using System;
using System.Linq;
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
            LocalLlmStatusText.Text = LocalRuntimeText(
                "Diagnostic runtime impossible : ",
                "Runtime diagnostics failed: ",
                "No se pudo abrir el diagnostico runtime: ",
                "Nao foi possivel abrir o diagnostico runtime: ",
                "Runtime-Diagnose konnte nicht geoeffnet werden: ",
                "Impossibile aprire la diagnostica runtime: ",
                UiLang) + ex.Message;
        }
    }

    private async Task ShowLocalLlmRuntimeDiagnosticsAsync()
    {
        var lang = UiLang;

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

        static string SafeBuild(string? value, string fallback)
            => string.IsNullOrWhiteSpace(value) ? fallback : value;

        string FormatEvent(RuntimeEventLogItem item)
            => LocalRuntimeText(
                $"{item.At.ToLocalTime():g} | {ResolveRuntimeEventLabel(item.EventKind, lang)} | build {SafeBuild(item.Build, "-")} | prec. {SafeBuild(item.PreviousBuild, "-")}",
                $"{item.At.ToLocalTime():g} | {ResolveRuntimeEventLabel(item.EventKind, lang)} | build {SafeBuild(item.Build, "-")} | prev {SafeBuild(item.PreviousBuild, "-")}",
                $"{item.At.ToLocalTime():g} | {ResolveRuntimeEventLabel(item.EventKind, lang)} | build {SafeBuild(item.Build, "-")} | ant. {SafeBuild(item.PreviousBuild, "-")}",
                $"{item.At.ToLocalTime():g} | {ResolveRuntimeEventLabel(item.EventKind, lang)} | build {SafeBuild(item.Build, "-")} | ant. {SafeBuild(item.PreviousBuild, "-")}",
                $"{item.At.ToLocalTime():g} | {ResolveRuntimeEventLabel(item.EventKind, lang)} | Build {SafeBuild(item.Build, "-")} | vorher {SafeBuild(item.PreviousBuild, "-")}",
                $"{item.At.ToLocalTime():g} | {ResolveRuntimeEventLabel(item.EventKind, lang)} | build {SafeBuild(item.Build, "-")} | prec. {SafeBuild(item.PreviousBuild, "-")}",
                lang);

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
        var refreshButton = BuildDialogFooterButton(ClientUiText.Get("button.refresh", lang));
        var closeButton = BuildDialogFooterButton(ClientUiText.Get("dialog.close", _appSettings.UiLanguage), primary: true);
        var dialogSize = GetDialogMaxSize(900, 760, horizontalMargin: 72, verticalMargin: 96);
        var shell = BuildScrollableDialogShell(
            "Runtime",
            LocalRuntimeText("Diagnostic runtime local", "Local runtime diagnostics", "Diagnostico runtime local", "Diagnostico runtime local", "Lokale Runtime-Diagnose", "Diagnostica runtime locale", lang),
            LocalRuntimeText("Etat du runtime actif, compatibilite modele/runtime et dernier warmup.", "Status of the active runtime, model/runtime compatibility, and latest warmup.", "Estado del runtime activo, compatibilidad modelo/runtime y ultimo warmup.", "Estado do runtime ativo, compatibilidade modelo/runtime e ultimo warmup.", "Status der aktiven Runtime, Modell/Runtime-Kompatibilitaet und letzter Warmup.", "Stato del runtime attivo, compatibilita modello/runtime e ultimo warmup.", lang),
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
                bannerText = LocalRuntimeText(
                    $"Le modele courant requiert un runtime plus recent ({SafeBuild(diagnostics.RequiredBuild, "inconnu")}).",
                    $"The current model requires a newer runtime ({SafeBuild(diagnostics.RequiredBuild, "unknown")}).",
                    $"El modelo actual requiere un runtime mas reciente ({SafeBuild(diagnostics.RequiredBuild, "desconocido")}).",
                    $"O modelo atual requer um runtime mais recente ({SafeBuild(diagnostics.RequiredBuild, "desconhecido")}).",
                    $"Das aktuelle Modell erfordert eine neuere Runtime ({SafeBuild(diagnostics.RequiredBuild, "unbekannt")}).",
                    $"Il modello corrente richiede un runtime piu recente ({SafeBuild(diagnostics.RequiredBuild, "sconosciuto")}).",
                    lang);
                bannerHost.Content = BuildDialogInfoBanner(bannerText);
                actionButton.Content = LocalRuntimeText("Mettre a niveau", "Upgrade", "Actualizar", "Atualizar", "Aktualisieren", "Aggiorna", lang);
                actionButton.Visibility = Visibility.Visible;
            }
            else if (string.Equals(diagnostics.ActiveState, "pending_qualification", StringComparison.OrdinalIgnoreCase))
            {
                bannerText = LocalRuntimeText(
                    $"Le runtime actif ({SafeBuild(diagnostics.ActiveBuild, "inconnu")}) attend encore sa qualification warmup.",
                    $"The active runtime ({SafeBuild(diagnostics.ActiveBuild, "unknown")}) is still waiting for warmup qualification.",
                    $"El runtime activo ({SafeBuild(diagnostics.ActiveBuild, "desconocido")}) sigue esperando su cualificacion warmup.",
                    $"O runtime ativo ({SafeBuild(diagnostics.ActiveBuild, "desconhecido")}) ainda aguarda a qualificacao warmup.",
                    $"Die aktive Runtime ({SafeBuild(diagnostics.ActiveBuild, "unbekannt")}) wartet noch auf die Warmup-Qualifizierung.",
                    $"Il runtime attivo ({SafeBuild(diagnostics.ActiveBuild, "sconosciuto")}) e ancora in attesa della qualificazione warmup.",
                    lang);
                bannerHost.Content = BuildDialogInfoBanner(bannerText);
                actionButton.Content = LocalRuntimeText("Demarrer et qualifier", "Start and qualify", "Iniciar y cualificar", "Iniciar e qualificar", "Starten und qualifizieren", "Avvia e qualifica", lang);
                actionButton.Visibility = Visibility.Visible;
            }
            else if (diagnostics.LatestWarmupStatus is WarmupGateStatus.FailBlock or WarmupGateStatus.FailFallback)
            {
                bannerText = LocalRuntimeText(
                    "Le dernier warmup a echoue. Le runtime local doit etre reverifie.",
                    "The latest warmup failed. The local runtime needs to be checked again.",
                    "El ultimo warmup fallo. El runtime local debe verificarse de nuevo.",
                    "O ultimo warmup falhou. O runtime local precisa de nova verificacao.",
                    "Der letzte Warmup ist fehlgeschlagen. Die lokale Runtime muss erneut geprueft werden.",
                    "L'ultimo warmup non e riuscito. Il runtime locale deve essere verificato di nuovo.",
                    lang);
                bannerHost.Content = BuildDialogInfoBanner(bannerText);
                actionButton.Content = LocalRuntimeText("Relancer qualification", "Retry qualification", "Relanzar cualificacion", "Relancar qualificacao", "Qualifizierung erneut starten", "Rilancia qualificazione", lang);
                actionButton.Visibility = Visibility.Visible;
            }
            else
            {
                bannerText = LocalRuntimeText(
                    "Le runtime local est compatible avec le modele courant.",
                    "The local runtime is compatible with the current model.",
                    "El runtime local es compatible con el modelo actual.",
                    "O runtime local e compativel com o modelo atual.",
                    "Die lokale Runtime ist mit dem aktuellen Modell kompatibel.",
                    "Il runtime locale e compatibile con il modello corrente.",
                    lang);
                bannerHost.Content = BuildDialogInfoBanner(bannerText, positive: true);
                actionButton.Visibility = Visibility.Collapsed;
            }

            metricsGrid.Children.Clear();
            var metricTiles = new[]
            {
                BuildMetricTile(LocalRuntimeText("Runtime", "Runtime", "Runtime", "Runtime", "Runtime", "Runtime", lang), diagnostics.RuntimeLabel),
                BuildMetricTile(LocalRuntimeText("Build actif", "Active build", "Build activo", "Build ativo", "Aktiver Build", "Build attivo", lang), SafeBuild(diagnostics.ActiveBuild, LocalRuntimeText("inconnu", "unknown", "desconocido", "desconhecido", "unbekannt", "sconosciuto", lang))),
                BuildMetricTile(LocalRuntimeText("Build requis", "Required build", "Build requerido", "Build necessario", "Erforderlicher Build", "Build richiesto", lang), SafeBuild(diagnostics.RequiredBuild, LocalRuntimeText("aucun", "none", "ninguno", "nenhum", "keiner", "nessuno", lang))),
                BuildMetricTile(LocalRuntimeText("Etat", "State", "Estado", "Estado", "Status", "Stato", lang), ResolveRuntimeStateLabel(diagnostics.ActiveState, lang)),
                BuildMetricTile(LocalRuntimeText("Build precedent", "Previous build", "Build anterior", "Build anterior", "Vorheriger Build", "Build precedente", lang), SafeBuild(diagnostics.PreviousBuild, "-")),
                BuildMetricTile(LocalRuntimeText("Warmup", "Warmup", "Warmup", "Warmup", "Warmup", "Warmup", lang), ResolveWarmupStateLabel(diagnostics.LatestWarmupStatus, lang))
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
                BuildField(LocalRuntimeText("Modele", "Model", "Modelo", "Modelo", "Modell", "Modello", lang), diagnostics.ModelId),
                BuildField(LocalRuntimeText("Famille GGUF", "GGUF family", "Familia GGUF", "Familia GGUF", "GGUF-Familie", "Famiglia GGUF", lang), diagnostics.ModelFamily),
                BuildField(LocalRuntimeText("Profil qualifie", "Qualified profile", "Perfil cualificado", "Perfil qualificado", "Qualifiziertes Profil", "Profilo qualificato", lang), diagnostics.QualifiedProfileId),
                BuildField(LocalRuntimeText("Policy flash-attn", "Flash-attn policy", "Politica flash-attn", "Politica flash-attn", "Flash-attn-Regel", "Policy flash-attn", lang), ResolveFlashAttnPolicyLabel(diagnostics.ForcedFlashAttn, lang)),
                BuildField(LocalRuntimeText("Actif depuis", "Active since", "Activo desde", "Ativo desde", "Aktiv seit", "Attivo da", lang), FormatTimestamp(diagnostics.ActivatedAtUtc)),
                BuildField(LocalRuntimeText("Qualifie le", "Qualified at", "Cualificado el", "Qualificado em", "Qualifiziert am", "Qualificato il", lang), FormatTimestamp(diagnostics.QualifiedAtUtc)),
                BuildField(LocalRuntimeText("Exe actif", "Active exe", "Exe activo", "Exe ativo", "Aktive Exe", "Exe attivo", lang), diagnostics.ActiveExePath),
                BuildField(LocalRuntimeText("Manifest runtime", "Runtime manifest", "Manifest runtime", "Manifest runtime", "Runtime-Manifest", "Manifest runtime", lang), diagnostics.ActiveManifestPath),
                BuildField(LocalRuntimeText("Derniere raison warmup", "Latest warmup reason", "Ultima razon warmup", "Ultima razao warmup", "Letzter Warmup-Grund", "Ultimo motivo warmup", lang), diagnostics.LatestWarmupReason),
                BuildField(LocalRuntimeText("Compatibilite", "Compatibility", "Compatibilidad", "Compatibilidade", "Kompatibilitaet", "Compatibilita", lang), diagnostics.CompatibilityReason),
                BuildField(
                    LocalRuntimeText("Historique runtime", "Runtime history", "Historial runtime", "Historico runtime", "Runtime-Verlauf", "Storico runtime", lang),
                    diagnostics.RecentEvents.Count == 0
                        ? LocalRuntimeText("Aucun evenement runtime recent.", "No recent runtime event.", "No hay eventos runtime recientes.", "Nenhum evento runtime recente.", "Keine aktuellen Runtime-Ereignisse.", "Nessun evento runtime recente.", lang)
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
                Title = LocalRuntimeText("Confirmer la mise a niveau runtime", "Confirm runtime upgrade", "Confirmar actualizacion runtime", "Confirmar atualizacao runtime", "Runtime-Aktualisierung bestaetigen", "Conferma aggiornamento runtime", lang),
                Content = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = LocalRuntimeText(
                                $"Le modele courant demande un runtime {SafeBuild(currentDiagnostics.RequiredBuild, "plus recent")}.",
                                $"The current model requires runtime {SafeBuild(currentDiagnostics.RequiredBuild, "newer")}.",
                                $"El modelo actual requiere el runtime {SafeBuild(currentDiagnostics.RequiredBuild, "mas reciente")}.",
                                $"O modelo atual requer o runtime {SafeBuild(currentDiagnostics.RequiredBuild, "mais recente")}.",
                                $"Das aktuelle Modell erfordert Runtime {SafeBuild(currentDiagnostics.RequiredBuild, "neuere")}.",
                                $"Il modello corrente richiede il runtime {SafeBuild(currentDiagnostics.RequiredBuild, "piu recente")}.",
                                lang),
                            TextWrapping = TextWrapping.WrapWholeWords
                        },
                        new TextBlock
                        {
                            Text = LocalRuntimeText(
                                $"Runtime actuel : {SafeBuild(currentDiagnostics.ActiveBuild, "inconnu")}",
                                $"Current runtime: {SafeBuild(currentDiagnostics.ActiveBuild, "unknown")}",
                                $"Runtime actual: {SafeBuild(currentDiagnostics.ActiveBuild, "desconocido")}",
                                $"Runtime atual: {SafeBuild(currentDiagnostics.ActiveBuild, "desconhecido")}",
                                $"Aktuelle Runtime: {SafeBuild(currentDiagnostics.ActiveBuild, "unbekannt")}",
                                $"Runtime attivo: {SafeBuild(currentDiagnostics.ActiveBuild, "sconosciuto")}",
                                lang),
                            TextWrapping = TextWrapping.WrapWholeWords,
                            Opacity = 0.82
                        },
                        new TextBlock
                        {
                            Text = LocalRuntimeText(
                                "Le runtime sera telecharge dans un dossier versionne, puis devra etre qualifie par warmup avant usage nominal.",
                                "The runtime will be downloaded into a versioned folder and must pass warmup qualification before normal use.",
                                "El runtime se descargara en una carpeta versionada y debera pasar la cualificacion warmup antes del uso normal.",
                                "O runtime sera transferido para uma pasta versionada e tera de passar pela qualificacao warmup antes do uso normal.",
                                "Die Runtime wird in einen versionierten Ordner geladen und muss vor dem regulaeren Einsatz die Warmup-Qualifizierung bestehen.",
                                "Il runtime verra scaricato in una cartella versionata e dovra superare la qualificazione warmup prima dell'uso normale.",
                                lang),
                            TextWrapping = TextWrapping.WrapWholeWords,
                            Opacity = 0.82
                        }
                    }
                },
                PrimaryButtonText = LocalRuntimeText("Mettre a niveau", "Upgrade", "Actualizar", "Atualizar", "Aktualisieren", "Aggiorna", lang),
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
                ShowProgress(LocalRuntimeText("Preparation de la mise a niveau runtime...", "Preparing runtime upgrade...", "Preparando actualizacion runtime...", "A preparar atualizacao runtime...", "Runtime-Aktualisierung wird vorbereitet...", "Preparazione aggiornamento runtime...", lang));
                var progress = new Progress<DownloadManager.ProgressInfo>(p =>
                {
                    var label = p.Stage switch
                    {
                        "resolve" => LocalRuntimeText("Resolution de la release runtime...", "Resolving runtime release...", "Resolviendo la release runtime...", "A resolver a release runtime...", "Runtime-Release wird aufgeloest...", "Risoluzione release runtime...", lang),
                        "verify" => LocalRuntimeText($"Verification : {p.Id}", $"Verifying: {p.Id}", $"Verificando: {p.Id}", $"A verificar: {p.Id}", $"Pruefung: {p.Id}", $"Verifica: {p.Id}", lang),
                        "download" => LocalRuntimeText($"Telechargement : {p.Id}", $"Downloading: {p.Id}", $"Descargando: {p.Id}", $"A transferir: {p.Id}", $"Download: {p.Id}", $"Download: {p.Id}", lang),
                        "extract" => LocalRuntimeText("Extraction du runtime...", "Extracting runtime...", "Extrayendo runtime...", "A extrair o runtime...", "Runtime wird entpackt...", "Estrazione runtime...", lang),
                        "done" => LocalRuntimeText($"OK : {p.Id}", $"Done: {p.Id}", $"OK: {p.Id}", $"OK: {p.Id}", $"OK: {p.Id}", $"OK: {p.Id}", lang),
                        _ => LocalRuntimeText($"{p.Stage} : {p.Id}", $"{p.Stage}: {p.Id}", $"{p.Stage}: {p.Id}", $"{p.Stage}: {p.Id}", $"{p.Stage}: {p.Id}", $"{p.Stage}: {p.Id}", lang)
                    };
                    ShowProgress(label, p);
                });

                var (ok, msg, _) = await _llmBootstrapper.EnsureAsync(settings, force: false, progress, CancellationToken.None).ConfigureAwait(true);
                _appSettings = AppSettings.Load();
                LoadLocalLlmUiFromSettings();

                if (!ok)
                {
                    bannerHost.Content = BuildDialogInfoBanner(LocalRuntimeText(
                        $"Mise a niveau runtime impossible : {msg}",
                        $"Runtime upgrade failed: {msg}",
                        $"No se pudo actualizar el runtime: {msg}",
                        $"Nao foi possivel atualizar o runtime: {msg}",
                        $"Runtime-Aktualisierung fehlgeschlagen: {msg}",
                        $"Aggiornamento runtime non riuscito: {msg}",
                        lang));
                    return;
                }

                bannerHost.Content = BuildDialogInfoBanner(
                    LocalRuntimeText(
                        "Mise a niveau terminee. Qualification warmup requise avant usage nominal.",
                        "Upgrade completed. Warmup qualification is required before normal use.",
                        "Actualizacion completada. Se requiere cualificacion warmup antes del uso normal.",
                        "Atualizacao concluida. A qualificacao warmup e necessaria antes do uso normal.",
                        "Aktualisierung abgeschlossen. Vor dem regulaeren Einsatz ist eine Warmup-Qualifizierung erforderlich.",
                        "Aggiornamento completato. E richiesta la qualificazione warmup prima dell'uso normale.",
                        lang),
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
                ShowProgress(LocalRuntimeText("Demarrage du runtime et qualification warmup...", "Starting runtime and running warmup qualification...", "Iniciando runtime y ejecutando la cualificacion warmup...", "A iniciar o runtime e a executar a qualificacao warmup...", "Runtime wird gestartet und Warmup-Qualifizierung ausgefuehrt...", "Avvio del runtime e qualificazione warmup in corso...", lang));
                var ok = _llmProc.IsRunning
                    ? await RunLocalLlmWarmupQualificationAsync(ReadLocalLlmSettingsFromUi(), null, CancellationToken.None).ConfigureAwait(true)
                    : await EnsureLocalLlmStartedAsync(CancellationToken.None).ConfigureAwait(true);

                _appSettings = AppSettings.Load();
                LoadLocalLlmUiFromSettings();

                bannerHost.Content = ok
                    ? BuildDialogInfoBanner(LocalRuntimeText("Qualification runtime terminee.", "Runtime qualification completed.", "Cualificacion runtime completada.", "Qualificacao runtime concluida.", "Runtime-Qualifizierung abgeschlossen.", "Qualificazione runtime completata.", lang), positive: true)
                    : BuildDialogInfoBanner(LocalRuntimeText("La qualification runtime a echoue. Le rollback a ete applique si un runtime sain etait disponible.", "Runtime qualification failed. Rollback was applied if a healthy runtime was available.", "La cualificacion runtime fallo. Se aplico rollback si habia un runtime sano disponible.", "A qualificacao runtime falhou. O rollback foi aplicado se havia um runtime saudavel disponivel.", "Die Runtime-Qualifizierung ist fehlgeschlagen. Ein Rollback wurde angewendet, falls eine gesunde Runtime verfuegbar war.", "La qualificazione runtime non e riuscita. Il rollback e stato applicato se era disponibile un runtime sano.", lang));

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

    private static string ResolveRuntimeStateLabel(string? state, string? language)
        => state switch
        {
            "qualified" => LocalRuntimeText("qualifie", "qualified", "cualificado", "qualificado", "qualifiziert", "qualificato", language),
            "pending_qualification" => LocalRuntimeText("qualification en attente", "qualification pending", "cualificacion pendiente", "qualificacao pendente", "Qualifizierung ausstehend", "qualificazione in attesa", language),
            null or "" => LocalRuntimeText("legacy / non suivi", "legacy / not tracked", "legacy / no seguido", "legacy / nao monitorizado", "Legacy / nicht verfolgt", "legacy / non tracciato", language),
            _ => state
        };

    private static string ResolveWarmupStateLabel(WarmupGateStatus? status, string? language)
        => status switch
        {
            WarmupGateStatus.Pass => LocalRuntimeText("pass", "pass", "pass", "pass", "pass", "pass", language),
            WarmupGateStatus.PassDegraded => LocalRuntimeText("degrade", "degraded", "degradado", "degradado", "degradiert", "degradato", language),
            WarmupGateStatus.FailBlock => LocalRuntimeText("blocage", "blocked", "bloqueado", "bloqueado", "blockiert", "bloccato", language),
            WarmupGateStatus.FailFallback => LocalRuntimeText("fallback", "fallback", "fallback", "fallback", "fallback", "fallback", language),
            null => LocalRuntimeText("aucun", "none", "ninguno", "nenhum", "keiner", "nessuno", language),
            _ => status?.ToString() ?? LocalRuntimeText("aucun", "none", "ninguno", "nenhum", "keiner", "nessuno", language)
        };

    private static string ResolveFlashAttnPolicyLabel(bool? forcedFlashAttn, string? language)
        => forcedFlashAttn switch
        {
            true => LocalRuntimeText("force on", "forced on", "forzado on", "forcado on", "erzwingt on", "forzato on", language),
            false => LocalRuntimeText("force off", "forced off", "forzado off", "forcado off", "erzwingt off", "forzato off", language),
            null => LocalRuntimeText("auto / aucune surcharge", "auto / no override", "auto / sin override", "auto / sem override", "auto / kein override", "auto / nessun override", language)
        };

    private static string ResolveRuntimeEventLabel(string? eventKind, string? language)
        => eventKind switch
        {
            "runtime_installed" => LocalRuntimeText("runtime installe", "runtime installed", "runtime instalado", "runtime instalado", "Runtime installiert", "runtime installato", language),
            "runtime_upgrade_activated" => LocalRuntimeText("upgrade runtime active", "runtime upgrade activated", "actualizacion runtime activada", "upgrade runtime ativado", "Runtime-Upgrade aktiviert", "upgrade runtime attivato", language),
            "runtime_qualified" => LocalRuntimeText("runtime qualifie", "runtime qualified", "runtime cualificado", "runtime qualificado", "Runtime qualifiziert", "runtime qualificato", language),
            "runtime_rollback_applied" => LocalRuntimeText("rollback runtime applique", "runtime rollback applied", "rollback runtime aplicado", "rollback runtime aplicado", "Runtime-Rollback angewendet", "rollback runtime applicato", language),
            "runtime_upgrade_confirmed" => LocalRuntimeText("upgrade runtime confirme", "runtime upgrade confirmed", "actualizacion runtime confirmada", "upgrade runtime confirmado", "Runtime-Upgrade bestaetigt", "upgrade runtime confermato", language),
            _ => eventKind ?? "-"
        };

    private static string LocalRuntimeText(string fr, string en, string es, string pt, string de, string it, string? language)
        => ClientUiText.NormalizeLanguage(language) switch
        {
            "en" => en,
            "es" => es,
            "pt" => pt,
            "de" => de,
            "it" => it,
            _ => fr
        };
}
