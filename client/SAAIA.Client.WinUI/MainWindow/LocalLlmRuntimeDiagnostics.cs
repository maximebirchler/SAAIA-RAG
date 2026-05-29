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
            ClientLog.Exception("LocalLlmRuntimeDiagnostics.Open", ex);
            LocalLlmStatusText.Text = LocalRuntimeText(
                "Impossible d'ouvrir le diagnostic de l'assistant local. Le detail technique est dans les logs.",
                "Could not open local assistant diagnostics. Technical detail is in the logs.",
                "No se pudo abrir el diagnostico del asistente local. El detalle tecnico esta en los logs.",
                "Nao foi possivel abrir o diagnostico do assistente local. O detalhe tecnico esta nos logs.",
                "Diagnose des lokalen Assistenten konnte nicht geoeffnet werden. Details stehen in den Logs.",
                "Impossibile aprire la diagnostica dell'assistente locale. I dettagli tecnici sono nei log.",
                UiLang);
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
                $"{item.At.ToLocalTime():g} | {ResolveRuntimeEventLabel(item.EventKind, lang)} | version {SafeBuild(item.Build, "-")} | ancienne version {SafeBuild(item.PreviousBuild, "-")}",
                $"{item.At.ToLocalTime():g} | {ResolveRuntimeEventLabel(item.EventKind, lang)} | version {SafeBuild(item.Build, "-")} | previous version {SafeBuild(item.PreviousBuild, "-")}",
                $"{item.At.ToLocalTime():g} | {ResolveRuntimeEventLabel(item.EventKind, lang)} | version {SafeBuild(item.Build, "-")} | version anterior {SafeBuild(item.PreviousBuild, "-")}",
                $"{item.At.ToLocalTime():g} | {ResolveRuntimeEventLabel(item.EventKind, lang)} | versao {SafeBuild(item.Build, "-")} | versao anterior {SafeBuild(item.PreviousBuild, "-")}",
                $"{item.At.ToLocalTime():g} | {ResolveRuntimeEventLabel(item.EventKind, lang)} | Version {SafeBuild(item.Build, "-")} | vorherige Version {SafeBuild(item.PreviousBuild, "-")}",
                $"{item.At.ToLocalTime():g} | {ResolveRuntimeEventLabel(item.EventKind, lang)} | versione {SafeBuild(item.Build, "-")} | versione precedente {SafeBuild(item.PreviousBuild, "-")}",
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
            LocalRuntimeText("Assistant local", "Local assistant", "Asistente local", "Assistente local", "Lokaler Assistent", "Assistente locale", lang),
            LocalRuntimeText("Diagnostic de l'assistant local", "Local assistant diagnostics", "Diagnostico del asistente local", "Diagnostico do assistente local", "Diagnose des lokalen Assistenten", "Diagnostica assistente locale", lang),
            LocalRuntimeText("Etat de l'assistant local, version du moteur et derniere verification de demarrage.", "Local assistant status, engine version, and latest startup check.", "Estado del asistente local, version del motor y ultima verificacion de arranque.", "Estado do assistente local, versao do motor e ultima verificacao de arranque.", "Status des lokalen Assistenten, Engine-Version und letzte Startpruefung.", "Stato dell'assistente locale, versione del motore e ultima verifica di avvio.", lang),
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
                    $"Le modele courant demande une version plus recente du moteur local ({SafeBuild(diagnostics.RequiredBuild, "inconnue")}).",
                    $"The current model needs a newer local engine version ({SafeBuild(diagnostics.RequiredBuild, "unknown")}).",
                    $"El modelo actual necesita una version mas reciente del motor local ({SafeBuild(diagnostics.RequiredBuild, "desconocida")}).",
                    $"O modelo atual precisa de uma versao mais recente do motor local ({SafeBuild(diagnostics.RequiredBuild, "desconhecida")}).",
                    $"Das aktuelle Modell benoetigt eine neuere Version der lokalen Engine ({SafeBuild(diagnostics.RequiredBuild, "unbekannt")}).",
                    $"Il modello corrente richiede una versione piu recente del motore locale ({SafeBuild(diagnostics.RequiredBuild, "sconosciuta")}).",
                    lang);
                bannerHost.Content = BuildDialogInfoBanner(bannerText);
                actionButton.Content = LocalRuntimeText("Mettre a niveau", "Upgrade", "Actualizar", "Atualizar", "Aktualisieren", "Aggiorna", lang);
                actionButton.Visibility = Visibility.Visible;
            }
            else if (string.Equals(diagnostics.ActiveState, "pending_qualification", StringComparison.OrdinalIgnoreCase))
            {
                bannerText = LocalRuntimeText(
                    $"Le moteur local ({SafeBuild(diagnostics.ActiveBuild, "inconnu")}) attend encore sa verification de demarrage.",
                    $"The local engine ({SafeBuild(diagnostics.ActiveBuild, "unknown")}) is still waiting for its startup check.",
                    $"El motor local ({SafeBuild(diagnostics.ActiveBuild, "desconocido")}) sigue esperando su verificacion de arranque.",
                    $"O motor local ({SafeBuild(diagnostics.ActiveBuild, "desconhecido")}) ainda aguarda a verificacao de arranque.",
                    $"Die lokale Engine ({SafeBuild(diagnostics.ActiveBuild, "unbekannt")}) wartet noch auf die Startpruefung.",
                    $"Il motore locale ({SafeBuild(diagnostics.ActiveBuild, "sconosciuto")}) attende ancora la verifica di avvio.",
                    lang);
                bannerHost.Content = BuildDialogInfoBanner(bannerText);
                actionButton.Content = LocalRuntimeText("Demarrer et verifier", "Start and check", "Iniciar y verificar", "Iniciar e verificar", "Starten und pruefen", "Avvia e verifica", lang);
                actionButton.Visibility = Visibility.Visible;
            }
            else if (diagnostics.LatestWarmupStatus is WarmupGateStatus.FailBlock or WarmupGateStatus.FailFallback)
            {
                bannerText = LocalRuntimeText(
                    "La derniere verification de demarrage a echoue. Le moteur local doit etre reverifie.",
                    "The latest startup check failed. The local engine needs to be checked again.",
                    "La ultima verificacion de arranque fallo. El motor local debe revisarse de nuevo.",
                    "A ultima verificacao de arranque falhou. O motor local precisa de nova verificacao.",
                    "Die letzte Startpruefung ist fehlgeschlagen. Die lokale Engine muss erneut geprueft werden.",
                    "L'ultima verifica di avvio non e riuscita. Il motore locale deve essere ricontrollato.",
                    lang);
                bannerHost.Content = BuildDialogInfoBanner(bannerText);
                actionButton.Content = LocalRuntimeText("Relancer la verification", "Retry check", "Relanzar verificacion", "Relancar verificacao", "Pruefung erneut starten", "Rilancia verifica", lang);
                actionButton.Visibility = Visibility.Visible;
            }
            else
            {
                bannerText = LocalRuntimeText(
                    "L'assistant local est pret avec le modele courant.",
                    "The local assistant is ready with the current model.",
                    "El asistente local esta listo con el modelo actual.",
                    "O assistente local esta pronto com o modelo atual.",
                    "Der lokale Assistent ist mit dem aktuellen Modell bereit.",
                    "L'assistente locale e pronto con il modello corrente.",
                    lang);
                bannerHost.Content = BuildDialogInfoBanner(bannerText, positive: true);
                actionButton.Visibility = Visibility.Collapsed;
            }

            metricsGrid.Children.Clear();
            var metricTiles = new[]
            {
                BuildMetricTile(LocalRuntimeText("Moteur local", "Local engine", "Motor local", "Motor local", "Lokale Engine", "Motore locale", lang), diagnostics.RuntimeLabel),
                BuildMetricTile(LocalRuntimeText("Version active", "Active version", "Version activa", "Versao ativa", "Aktive Version", "Versione attiva", lang), SafeBuild(diagnostics.ActiveBuild, LocalRuntimeText("inconnu", "unknown", "desconocido", "desconhecido", "unbekannt", "sconosciuto", lang))),
                BuildMetricTile(LocalRuntimeText("Version minimale", "Minimum version", "Version minima", "Versao minima", "Mindestversion", "Versione minima", lang), SafeBuild(diagnostics.RequiredBuild, LocalRuntimeText("aucun", "none", "ninguno", "nenhum", "keiner", "nessuno", lang))),
                BuildMetricTile(LocalRuntimeText("Etat", "State", "Estado", "Estado", "Status", "Stato", lang), ResolveRuntimeStateLabel(diagnostics.ActiveState, lang)),
                BuildMetricTile(LocalRuntimeText("Version precedente", "Previous version", "Version anterior", "Versao anterior", "Vorherige Version", "Versione precedente", lang), SafeBuild(diagnostics.PreviousBuild, "-")),
                BuildMetricTile(LocalRuntimeText("Verification", "Check", "Verificacion", "Verificacao", "Pruefung", "Verifica", lang), ResolveWarmupStateLabel(diagnostics.LatestWarmupStatus, lang))
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
                BuildField(LocalRuntimeText("Profil valide", "Approved profile", "Perfil validado", "Perfil validado", "Freigegebenes Profil", "Profilo validato", lang), diagnostics.QualifiedProfileId),
                BuildField(LocalRuntimeText("Acceleration GPU", "GPU acceleration", "Aceleracion GPU", "Aceleracao GPU", "GPU-Beschleunigung", "Accelerazione GPU", lang), ResolveFlashAttnPolicyLabel(diagnostics.ForcedFlashAttn, lang)),
                BuildField(LocalRuntimeText("Actif depuis", "Active since", "Activo desde", "Ativo desde", "Aktiv seit", "Attivo da", lang), FormatTimestamp(diagnostics.ActivatedAtUtc)),
                BuildField(LocalRuntimeText("Verifie le", "Checked at", "Verificado el", "Verificado em", "Geprueft am", "Verificato il", lang), FormatTimestamp(diagnostics.QualifiedAtUtc)),
                BuildField(LocalRuntimeText("Programme utilise", "Program used", "Programa usado", "Programa usado", "Verwendetes Programm", "Programma usato", lang), diagnostics.ActiveExePath),
                BuildField(LocalRuntimeText("Configuration du moteur local", "Local engine configuration", "Configuracion del motor local", "Configuracao do motor local", "Konfiguration der lokalen Engine", "Configurazione del motore locale", lang), diagnostics.ActiveManifestPath),
                BuildField(LocalRuntimeText("Derniere raison de verification", "Latest check reason", "Ultimo motivo de verificacion", "Ultima razao de verificacao", "Letzter Pruefgrund", "Ultimo motivo verifica", lang), diagnostics.LatestWarmupReason),
                BuildField(LocalRuntimeText("Compatibilite", "Compatibility", "Compatibilidad", "Compatibilidade", "Kompatibilitaet", "Compatibilita", lang), diagnostics.CompatibilityReason),
                BuildField(
                    LocalRuntimeText("Historique de l'assistant local", "Local assistant history", "Historial del asistente local", "Historico do assistente local", "Verlauf des lokalen Assistenten", "Storico dell'assistente locale", lang),
                    diagnostics.RecentEvents.Count == 0
                        ? LocalRuntimeText("Aucun evenement recent de l'assistant local.", "No recent local assistant event.", "No hay eventos recientes del asistente local.", "Nenhum evento recente do assistente local.", "Keine aktuellen Ereignisse des lokalen Assistenten.", "Nessun evento recente dell'assistente locale.", lang)
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
                Title = LocalRuntimeText("Confirmer la mise a jour du moteur local", "Confirm local engine update", "Confirmar actualizacion del motor local", "Confirmar atualizacao do motor local", "Aktualisierung der lokalen Engine bestaetigen", "Conferma aggiornamento del motore locale", lang),
                Content = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = LocalRuntimeText(
                                $"Le modele courant demande le moteur local {SafeBuild(currentDiagnostics.RequiredBuild, "plus recent")}.",
                                $"The current model needs local engine {SafeBuild(currentDiagnostics.RequiredBuild, "newer")}.",
                                $"El modelo actual necesita el motor local {SafeBuild(currentDiagnostics.RequiredBuild, "mas reciente")}.",
                                $"O modelo atual precisa do motor local {SafeBuild(currentDiagnostics.RequiredBuild, "mais recente")}.",
                                $"Das aktuelle Modell benoetigt die lokale Engine {SafeBuild(currentDiagnostics.RequiredBuild, "neuere")}.",
                                $"Il modello corrente richiede il motore locale {SafeBuild(currentDiagnostics.RequiredBuild, "piu recente")}.",
                                lang),
                            TextWrapping = TextWrapping.WrapWholeWords
                        },
                        new TextBlock
                        {
                            Text = LocalRuntimeText(
                                $"Moteur actuel : {SafeBuild(currentDiagnostics.ActiveBuild, "inconnu")}",
                                $"Current engine: {SafeBuild(currentDiagnostics.ActiveBuild, "unknown")}",
                                $"Motor actual: {SafeBuild(currentDiagnostics.ActiveBuild, "desconocido")}",
                                $"Motor atual: {SafeBuild(currentDiagnostics.ActiveBuild, "desconhecido")}",
                                $"Aktuelle Engine: {SafeBuild(currentDiagnostics.ActiveBuild, "unbekannt")}",
                                $"Motore attivo: {SafeBuild(currentDiagnostics.ActiveBuild, "sconosciuto")}",
                                lang),
                            TextWrapping = TextWrapping.WrapWholeWords,
                            Opacity = 0.82
                        },
                        new TextBlock
                        {
                            Text = LocalRuntimeText(
                                "Le moteur local sera telecharge dans un dossier versionne, puis verifie avant utilisation.",
                                "The local engine will be downloaded into a versioned folder, then checked before use.",
                                "El motor local se descargara en una carpeta versionada y se comprobara antes de usarlo.",
                                "O motor local sera transferido para uma pasta versionada e verificado antes da utilizacao.",
                                "Die lokale Engine wird in einen versionierten Ordner geladen und vor der Nutzung geprueft.",
                                "Il motore locale verra scaricato in una cartella versionata e verificato prima dell'uso.",
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
                ShowProgress(LocalRuntimeText("Preparation de la mise a jour de l'assistant local...", "Preparing local assistant update...", "Preparando actualizacion del asistente local...", "A preparar atualizacao do assistente local...", "Aktualisierung des lokalen Assistenten wird vorbereitet...", "Preparazione aggiornamento assistente locale...", lang));
                var progress = new Progress<DownloadManager.ProgressInfo>(p =>
                {
                    var label = p.Stage switch
                    {
                        "resolve" => LocalRuntimeText("Recherche de la version compatible...", "Finding the compatible version...", "Buscando la version compatible...", "A procurar a versao compativel...", "Kompatible Version wird gesucht...", "Ricerca della versione compatibile...", lang),
                        "verify" => LocalRuntimeText($"Verification : {p.Id}", $"Verifying: {p.Id}", $"Verificando: {p.Id}", $"A verificar: {p.Id}", $"Pruefung: {p.Id}", $"Verifica: {p.Id}", lang),
                        "download" => LocalRuntimeText($"Telechargement : {p.Id}", $"Downloading: {p.Id}", $"Descargando: {p.Id}", $"A transferir: {p.Id}", $"Download: {p.Id}", $"Download: {p.Id}", lang),
                        "extract" => LocalRuntimeText("Installation du moteur local...", "Installing the local engine...", "Instalando el motor local...", "A instalar o motor local...", "Lokale Engine wird installiert...", "Installazione motore locale...", lang),
                        "done" => LocalRuntimeText($"OK : {p.Id}", $"Done: {p.Id}", $"OK: {p.Id}", $"OK: {p.Id}", $"OK: {p.Id}", $"OK: {p.Id}", lang),
                        _ => LocalRuntimeText("Etape technique en cours...", "Technical step in progress...", "Etapa tecnica en curso...", "Etapa tecnica em curso...", "Technischer Schritt laeuft...", "Passaggio tecnico in corso...", lang)
                    };
                    ShowProgress(label, p);
                });

                var (ok, msg, _) = await _llmBootstrapper.EnsureAsync(settings, force: false, progress, CancellationToken.None).ConfigureAwait(true);
                _appSettings = AppSettings.Load();
                LoadLocalLlmUiFromSettings();

                if (!ok)
                {
                    ClientLog.Warn($"[LocalLlmDiagnostics] Local assistant update failed: {msg}");
                    bannerHost.Content = BuildDialogInfoBanner(LocalRuntimeText(
                        "La mise a jour de l'assistant local n'a pas pu etre preparee. Verifie la connexion et l'espace disque, puis reessaie.",
                        "The local assistant update could not be prepared. Check the connection and disk space, then try again.",
                        "No se pudo preparar la actualizacion del asistente local. Revisa la conexion y el espacio en disco, e intentalo de nuevo.",
                        "Nao foi possivel preparar a atualizacao do assistente local. Verifica a ligacao e o espaco em disco, e tenta novamente.",
                        "Die Aktualisierung des lokalen Assistenten konnte nicht vorbereitet werden. Verbindung und Speicherplatz pruefen, dann erneut versuchen.",
                        "Non e stato possibile preparare l'aggiornamento dell'assistente locale. Controlla connessione e spazio disco, poi riprova.",
                        lang));
                    return;
                }

                bannerHost.Content = BuildDialogInfoBanner(
                    LocalRuntimeText(
                        "Mise a jour terminee. Une verification de demarrage est requise avant usage normal.",
                        "Update completed. A startup check is required before normal use.",
                        "Actualizacion completada. Se requiere una verificacion de arranque antes del uso normal.",
                        "Atualizacao concluida. E necessaria uma verificacao de arranque antes do uso normal.",
                        "Aktualisierung abgeschlossen. Vor dem normalen Einsatz ist eine Startpruefung erforderlich.",
                        "Aggiornamento completato. E richiesta una verifica di avvio prima dell'uso normale.",
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
                ShowProgress(LocalRuntimeText("Demarrage du moteur local et verification...", "Starting local engine and running checks...", "Iniciando motor local y verificaciones...", "A iniciar o motor local e verificacoes...", "Lokale Engine wird gestartet und geprueft...", "Avvio del motore locale e verifiche in corso...", lang));
                var ok = _llmProc.IsRunning
                    ? await RunLocalLlmWarmupQualificationAsync(ReadLocalLlmSettingsFromUi(), null, CancellationToken.None).ConfigureAwait(true)
                    : await EnsureLocalLlmStartedAsync(CancellationToken.None).ConfigureAwait(true);

                _appSettings = AppSettings.Load();
                LoadLocalLlmUiFromSettings();

                bannerHost.Content = ok
                    ? BuildDialogInfoBanner(LocalRuntimeText("Verification du moteur local terminee.", "Local engine check completed.", "Comprobacion del motor local completada.", "Verificacao do motor local concluida.", "Pruefung der lokalen Engine abgeschlossen.", "Verifica motore locale completata.", lang), positive: true)
                    : BuildDialogInfoBanner(LocalRuntimeText("La verification du moteur local a echoue. Un retour a la derniere version stable a ete applique si possible.", "The local engine check failed. The last stable version was restored when possible.", "La comprobacion del motor local fallo. Se restauro la ultima version estable cuando fue posible.", "A verificacao do motor local falhou. A ultima versao estavel foi restaurada quando possivel.", "Die Pruefung der lokalen Engine ist fehlgeschlagen. Wenn moeglich wurde die letzte stabile Version wiederhergestellt.", "La verifica del motore locale non e riuscita. Se possibile e stata ripristinata l'ultima versione stabile.", lang));

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
            _ => LocalRuntimeText("etat non reconnu", "unknown state", "estado no reconocido", "estado nao reconhecido", "unbekannter Status", "stato non riconosciuto", language)
        };

    private static string ResolveWarmupStateLabel(WarmupGateStatus? status, string? language)
        => status switch
        {
            WarmupGateStatus.Pass => LocalRuntimeText("validee", "passed", "validada", "validada", "bestanden", "validata", language),
            WarmupGateStatus.PassDegraded => LocalRuntimeText("validee avec reserve", "passed with warning", "validada con aviso", "validada com aviso", "mit Warnung bestanden", "validata con avviso", language),
            WarmupGateStatus.FailBlock => LocalRuntimeText("blocage", "blocked", "bloqueado", "bloqueado", "blockiert", "bloccato", language),
            WarmupGateStatus.FailFallback => LocalRuntimeText("mode de secours", "safe fallback mode", "modo de respaldo", "modo de contingencia", "Ausweichmodus", "modalita di ripiego", language),
            null => LocalRuntimeText("aucun", "none", "ninguno", "nenhum", "keiner", "nessuno", language),
            _ => LocalRuntimeText("etat non reconnu", "unknown state", "estado no reconocido", "estado nao reconhecido", "unbekannter Status", "stato non riconosciuto", language)
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
            "runtime_installed" => LocalRuntimeText("moteur local installe", "local engine installed", "motor local instalado", "motor local instalado", "lokale Engine installiert", "motore locale installato", language),
            "runtime_upgrade_activated" => LocalRuntimeText("mise a jour activee", "update activated", "actualizacion activada", "atualizacao ativada", "Aktualisierung aktiviert", "aggiornamento attivato", language),
            "runtime_qualified" => LocalRuntimeText("moteur local valide", "local engine approved", "motor local validado", "motor local validado", "lokale Engine freigegeben", "motore locale validato", language),
            "runtime_rollback_applied" => LocalRuntimeText("retour a la version stable", "restored stable version", "vuelta a la version estable", "regresso a versao estavel", "stabile Version wiederhergestellt", "ripristino versione stabile", language),
            "runtime_upgrade_confirmed" => LocalRuntimeText("mise a jour confirmee", "update confirmed", "actualizacion confirmada", "atualizacao confirmada", "Aktualisierung bestaetigt", "aggiornamento confermato", language),
            _ => LocalRuntimeText("evenement non classe", "unclassified event", "evento no clasificado", "evento nao classificado", "nicht klassifiziertes Ereignis", "evento non classificato", language)
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
