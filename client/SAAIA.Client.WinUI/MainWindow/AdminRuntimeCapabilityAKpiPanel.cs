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
                var message = ClientUiText.Format(
                    "admin.runtime.action.preview_offsets.failed_detail",
                    lang,
                    BuildAdminRuntimeActionErrorDetail(ex, lang));
                SetStateBanner(message);
                Status(message);
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

        string CapabilityAMetricTitle(string? key)
            => (key ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "capability_a_operation_p95" => LocalRuntimeText("Temps de traitement", "Processing time", "Tiempo de tratamiento", "Tempo de processamento", "Verarbeitungszeit", "Tempo di elaborazione", lang),
                "capability_a_candidate_reads" => LocalRuntimeText("Lectures de candidats", "Candidate reads", "Lecturas de candidatos", "Leituras de candidatos", "Kandidaten-Lesevorgaenge", "Letture candidati", lang),
                "capability_a_candidate_count" => LocalRuntimeText("Documents analyses par controle", "Documents checked per scan", "Documentos analizados por control", "Documentos analisados por controlo", "Dokumente pro Pruefung", "Documenti analizzati per controllo", lang),
                "capability_a_enqueue_requests" => LocalRuntimeText("Demandes de relance", "Rerun requests", "Solicitudes de relanzamiento", "Pedidos de relancamento", "Neustart-Anfragen", "Richieste di rilancio", lang),
                "capability_a_skip_rate" => LocalRuntimeText("Documents ignores temporairement", "Temporarily skipped documents", "Documentos omitidos temporalmente", "Documentos ignorados temporariamente", "Zeitweise uebersprungene Dokumente", "Documenti saltati temporaneamente", lang),
                "capability_a_ready_to_enqueue_rate" => LocalRuntimeText("Documents prets a relancer", "Documents ready to rerun", "Documentos listos para relanzar", "Documentos prontos a relancar", "Dokumente bereit zum Neustart", "Documenti pronti al rilancio", lang),
                "capability_a_offset_backfill_share" => LocalRuntimeText("Index a completer", "Index to complete", "Indice por completar", "Indice a completar", "Index zu ergaenzen", "Indice da completare", lang),
                _ => LocalRuntimeText("Indicateur serveur", "Server indicator", "Indicador servidor", "Indicador servidor", "Server-Indikator", "Indicatore server", lang)
            };

        string CapabilityAMetricBody(AdminRuntimeCapabilityAKpiMetric metric)
            => (metric.Key ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "capability_a_operation_p95" => LocalRuntimeText("Mesure si l'enrichissement de l'index reste assez rapide. Si ce chiffre monte, le serveur met trop de temps a preparer les documents a relancer.", "Checks whether index enrichment stays fast enough. If this rises, the server is taking too long to prepare documents for rerun.", "Mide si el enriquecimiento del indice sigue siendo rapido. Si sube, el servidor tarda demasiado en preparar documentos para relanzar.", "Mede se o enriquecimento do indice continua rapido. Se subir, o servidor demora demasiado a preparar documentos para relancar.", "Prueft, ob die Indexanreicherung schnell genug bleibt. Steigt der Wert, braucht der Server zu lange fuer die Vorbereitung.", "Verifica se l'arricchimento dell'indice resta abbastanza rapido. Se sale, il server impiega troppo a preparare i documenti.", lang),
                "capability_a_candidate_reads" => LocalRuntimeText("Compte les consultations de la liste des documents qui pourraient beneficier d'un nouvel enrichissement.", "Counts reads of the list of documents that may benefit from another enrichment pass.", "Cuenta las consultas de la lista de documentos que podrian beneficiarse de otro enriquecimiento.", "Conta as leituras da lista de documentos que podem beneficiar de novo enriquecimento.", "Zaehlt Abrufe der Dokumentliste, die von einer erneuten Anreicherung profitieren koennte.", "Conta le letture dell'elenco dei documenti che possono beneficiare di un nuovo arricchimento.", lang),
                "capability_a_candidate_count" => LocalRuntimeText("Montre combien de documents sont inspectes a chaque controle. Utile pour voir si la file grossit anormalement.", "Shows how many documents are inspected on each check. Useful to see whether the queue grows abnormally.", "Muestra cuantos documentos se inspeccionan en cada control. Sirve para ver si la cola crece de forma anormal.", "Mostra quantos documentos sao inspecionados em cada controlo. Ajuda a ver se a fila cresce anormalmente.", "Zeigt, wie viele Dokumente pro Pruefung betrachtet werden. Hilft, ungewoehnliches Wachstum zu erkennen.", "Mostra quanti documenti vengono controllati ogni volta. Utile per capire se la coda cresce troppo.", lang),
                "capability_a_enqueue_requests" => LocalRuntimeText("Compte les demandes de relance controlee. Une relance ne demarre que si le document n'est pas deja traite ailleurs.", "Counts controlled rerun requests. A rerun starts only when the document is not already being processed elsewhere.", "Cuenta las solicitudes de relanzamiento controlado. Se inicia solo si el documento no se trata ya en otro lugar.", "Conta os pedidos de relancamento controlado. So inicia se o documento nao estiver ja em processamento.", "Zaehlt kontrollierte Neustart-Anfragen. Ein Neustart erfolgt nur, wenn das Dokument nicht bereits verarbeitet wird.", "Conta le richieste di rilancio controllato. Parte solo se il documento non e gia in elaborazione.", lang),
                "capability_a_skip_rate" => LocalRuntimeText("Indique la part de documents que le serveur protege temporairement pour eviter les doublons, les boucles ou les relances trop rapides.", "Shows the share of documents temporarily protected to avoid duplicates, loops, or too-frequent reruns.", "Indica la parte de documentos protegidos temporalmente para evitar duplicados, bucles o relanzamientos demasiado rapidos.", "Indica a parte de documentos protegidos temporariamente para evitar duplicados, ciclos ou relancamentos demasiado rapidos.", "Zeigt den Anteil temporaer geschuetzter Dokumente, um Duplikate, Schleifen oder zu schnelle Neustarts zu vermeiden.", "Indica la quota di documenti protetti temporaneamente per evitare duplicati, cicli o rilanci troppo rapidi.", lang),
                "capability_a_ready_to_enqueue_rate" => LocalRuntimeText("Indique la part de documents qui peuvent etre relances maintenant sans conflit avec l'ingestion en cours.", "Shows the share of documents that can be rerun now without conflicting with current ingestion.", "Indica la parte de documentos que pueden relanzarse ahora sin conflicto con la ingesta en curso.", "Indica a parte de documentos que podem ser relancados agora sem conflito com a ingestao em curso.", "Zeigt den Anteil der Dokumente, die jetzt ohne Konflikt zur laufenden Ingestion neu gestartet werden koennen.", "Indica la quota di documenti rilanciabili ora senza conflitti con l'ingestione in corso.", lang),
                "capability_a_offset_backfill_share" => LocalRuntimeText("Repere les anciens documents dont certaines positions d'index manquent encore. Les corriger rend les sources plus fiables.", "Finds older documents still missing some index positions. Fixing them makes sources more reliable.", "Detecta documentos antiguos a los que aun les faltan posiciones de indice. Corregirlos hace las fuentes mas fiables.", "Deteta documentos antigos que ainda nao tem todas as posicoes de indice. Corrigir melhora a fiabilidade das fontes.", "Findet alte Dokumente mit fehlenden Indexpositionen. Ihre Korrektur macht Quellen verlaesslicher.", "Individua vecchi documenti con posizioni indice mancanti. Correggerli rende le fonti piu affidabili.", lang),
                _ => LocalRuntimeText("Indicateur technique fourni par le serveur. Il sera detaille dans une prochaine version de l'interface.", "Technical indicator provided by the server. It will be detailed in a later UI version.", "Indicador tecnico proporcionado por el servidor. Se detallara en una version posterior de la interfaz.", "Indicador tecnico fornecido pelo servidor. Sera detalhado numa versao futura da interface.", "Technischer Serverindikator. Er wird in einer spaeteren UI-Version genauer beschrieben.", "Indicatore tecnico fornito dal server. Sara dettagliato in una prossima versione dell'interfaccia.", lang)
            };

        string CapabilityAAlertTitle(string? key)
            => (key ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "capability_a_operation_p95_regression" => LocalRuntimeText("Traitement trop lent", "Processing too slow", "Tratamiento demasiado lento", "Processamento demasiado lento", "Verarbeitung zu langsam", "Elaborazione troppo lenta", lang),
                "capability_a_skip_rate_regression" => LocalRuntimeText("Trop de documents proteges", "Too many protected documents", "Demasiados documentos protegidos", "Demasiados documentos protegidos", "Zu viele geschuetzte Dokumente", "Troppi documenti protetti", lang),
                "capability_a_ready_to_enqueue_rate_regression" => LocalRuntimeText("Pas assez de documents prets", "Not enough documents ready", "No hay suficientes documentos listos", "Poucos documentos prontos", "Zu wenige Dokumente bereit", "Pochi documenti pronti", lang),
                "capability_a_offset_backfill_share_watch" => LocalRuntimeText("Corrections d'index a surveiller", "Index fixes to watch", "Correcciones de indice a vigilar", "Correcoes de indice a vigiar", "Indexkorrekturen beobachten", "Correzioni indice da monitorare", lang),
                _ => LocalRuntimeText("Alerte a verifier", "Alert to review", "Alerta por revisar", "Alerta a rever", "Warnung pruefen", "Avviso da verificare", lang)
            };

        string CapabilityAAlertBody(AdminRuntimeCapabilityAKpiAlert alert)
            => (alert.Key ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "capability_a_operation_p95_regression" => LocalRuntimeText("Le serveur met plus de temps que prevu a preparer l'enrichissement. A verifier avant d'augmenter le volume de relance.", "The server takes longer than expected to prepare enrichment. Check this before increasing rerun volume.", "El servidor tarda mas de lo previsto en preparar el enriquecimiento. Revisalo antes de aumentar el volumen.", "O servidor demora mais do que esperado a preparar o enriquecimento. Verifica antes de aumentar o volume.", "Der Server braucht laenger als erwartet fuer die Vorbereitung. Vor mehr Volumen pruefen.", "Il server impiega piu del previsto a preparare l'arricchimento. Verifica prima di aumentare il volume.", lang),
                "capability_a_skip_rate_regression" => LocalRuntimeText("Beaucoup de documents sont repousses parce qu'ils sont deja en traitement ou proteges par une temporisation.", "Many documents are postponed because they are already being processed or protected by a delay.", "Muchos documentos se posponen porque ya estan en tratamiento o protegidos por una espera.", "Muitos documentos sao adiados porque ja estao em processamento ou protegidos por temporizacao.", "Viele Dokumente werden verschoben, weil sie bereits verarbeitet werden oder geschuetzt sind.", "Molti documenti vengono rimandati perche gia in elaborazione o protetti da una pausa.", lang),
                "capability_a_ready_to_enqueue_rate_regression" => LocalRuntimeText("Peu de documents peuvent etre relances immediatement. Verifie les traitements actifs et les protections anti-relance.", "Few documents can be rerun immediately. Check active processing and retry protection.", "Pocos documentos pueden relanzarse de inmediato. Revisa procesos activos y protecciones.", "Poucos documentos podem ser relancados de imediato. Verifica processamentos ativos e protecoes.", "Nur wenige Dokumente koennen sofort neu gestartet werden. Aktive Verarbeitung und Schutzregeln pruefen.", "Pochi documenti possono essere rilanciati subito. Controlla elaborazioni attive e protezioni.", lang),
                "capability_a_offset_backfill_share_watch" => LocalRuntimeText("Une part importante de l'index doit encore etre completee. Des relances controlees amelioreront la fiabilite des sources.", "A significant part of the index still needs completion. Controlled reruns will improve source reliability.", "Una parte importante del indice debe completarse. Relanzamientos controlados mejoraran la fiabilidad.", "Uma parte importante do indice ainda precisa ser completada. Relancamentos controlados melhoram a fiabilidade.", "Ein relevanter Teil des Index ist noch unvollstaendig. Kontrollierte Neustarts verbessern die Quellen.", "Una parte importante dell'indice va completata. Rilanci controllati migliorano l'affidabilita.", lang),
                _ => LocalRuntimeText("Le serveur demande une verification manuelle de ce point.", "The server asks for a manual check on this point.", "El servidor pide una revision manual de este punto.", "O servidor pede uma verificacao manual deste ponto.", "Der Server empfiehlt eine manuelle Pruefung.", "Il server richiede una verifica manuale.", lang)
            };

        string CapabilityASeverityLabel(string? severity)
            => (severity ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "critical" => LocalRuntimeText("critique", "critical", "critico", "critico", "kritisch", "critico", lang),
                "high" => LocalRuntimeText("elevee", "high", "alta", "alta", "hoch", "alta", lang),
                "medium" => LocalRuntimeText("moyenne", "medium", "media", "media", "mittel", "media", lang),
                "low" => LocalRuntimeText("faible", "low", "baja", "baixa", "niedrig", "bassa", lang),
                "" => LocalRuntimeText("a verifier", "to review", "por revisar", "a rever", "zu pruefen", "da verificare", lang),
                _ => LocalRuntimeText("a verifier", "to review", "por revisar", "a rever", "zu pruefen", "da verificare", lang)
            };

        string CapabilityARecommendation(string text)
        {
            var normalized = (text ?? string.Empty).ToLowerInvariant();
            if (normalized.Contains("active") || normalized.Contains("cooldown") || normalized.Contains("blocked"))
                return LocalRuntimeText("Verifier les traitements deja en cours et les protections anti-relance avant de forcer une nouvelle campagne.", "Check running processing and retry protection before forcing a new campaign.", "Revisa procesos en curso y protecciones antes de forzar una campana nueva.", "Verifica processamentos em curso e protecoes antes de forcar nova campanha.", "Laufende Verarbeitung und Schutzregeln pruefen, bevor eine neue Kampagne erzwungen wird.", "Controlla elaborazioni attive e protezioni prima di forzare una nuova campagna.", lang);
            return LocalRuntimeText("Verifier l'etat serveur avant d'augmenter le volume de relance.", "Check server state before increasing rerun volume.", "Revisa el estado del servidor antes de aumentar el volumen.", "Verifica o estado do servidor antes de aumentar o volume.", "Serverstatus pruefen, bevor das Volumen erhoeht wird.", "Controlla lo stato server prima di aumentare il volume.", lang);
        }

        string CapabilityADashboardPanelLabel(string text)
        {
            var normalized = (text ?? string.Empty).ToLowerInvariant();
            if (normalized.Contains("operation p95"))
                return LocalRuntimeText("Temps de traitement par type d'action et resultat.", "Processing time by action type and result.", "Tiempo de tratamiento por tipo de accion y resultado.", "Tempo de processamento por tipo de acao e resultado.", "Verarbeitungszeit nach Aktion und Ergebnis.", "Tempo di elaborazione per tipo di azione e risultato.", lang);
            if (normalized.Contains("candidate reads"))
                return LocalRuntimeText("Volume de documents inspectes par les controles d'enrichissement.", "Volume of documents inspected by enrichment checks.", "Volumen de documentos inspeccionados por controles de enriquecimiento.", "Volume de documentos inspecionados pelos controlos de enriquecimento.", "Volumen der von Anreicherungspruefungen betrachteten Dokumente.", "Volume di documenti controllati dall'arricchimento.", lang);
            if (normalized.Contains("queued vs skipped"))
                return LocalRuntimeText("Documents relances ou ignores temporairement.", "Documents rerun or temporarily skipped.", "Documentos relanzados u omitidos temporalmente.", "Documentos relancados ou ignorados temporariamente.", "Neu gestartete oder zeitweise uebersprungene Dokumente.", "Documenti rilanciati o saltati temporaneamente.", lang);
            if (normalized.Contains("ready-to-enqueue"))
                return LocalRuntimeText("Part de documents prets a relancer maintenant.", "Share of documents ready to rerun now.", "Parte de documentos listos para relanzar ahora.", "Parte de documentos prontos a relancar agora.", "Anteil der jetzt startbereiten Dokumente.", "Quota di documenti pronti al rilancio.", lang);
            if (normalized.Contains("offset-backfill"))
                return LocalRuntimeText("Part de documents dont l'index doit etre complete.", "Share of documents whose index needs completion.", "Parte de documentos cuyo indice debe completarse.", "Parte de documentos cujo indice precisa ser completado.", "Anteil der Dokumente mit unvollstaendigem Index.", "Quota di documenti con indice da completare.", lang);
            return LocalRuntimeText("Panneau de suivi serveur.", "Server monitoring panel.", "Panel de seguimiento servidor.", "Painel de acompanhamento servidor.", "Server-Monitoring-Panel.", "Pannello di monitoraggio server.", lang);
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
                    LocalRuntimeText(
                        "Ces chiffres servent a decider s'il faut relancer proprement certains documents pour completer l'index. Ils indiquent surtout la vitesse, la part de documents prets et la part encore protegee.",
                        "These numbers help decide whether some documents should be rerun cleanly to complete the index. They mainly show speed, ready share, and protected share.",
                        "Estas cifras ayudan a decidir si algunos documentos deben relanzarse limpiamente para completar el indice. Muestran velocidad, parte lista y parte protegida.",
                        "Estes numeros ajudam a decidir se alguns documentos devem ser relancados para completar o indice. Mostram velocidade, parte pronta e parte protegida.",
                        "Diese Zahlen helfen zu entscheiden, ob Dokumente sauber neu gestartet werden sollen, um den Index zu ergaenzen. Sie zeigen vor allem Tempo, Bereitschaft und Schutzanteil.",
                        "Questi numeri aiutano a decidere se alcuni documenti vanno rilanciati per completare l'indice. Mostrano velocita, quota pronta e quota protetta.",
                        lang)));
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
                    var recommendationBody = string.Join("\n", snapshot.Live.Recommendations.Select(item => "- " + CapabilityARecommendation(item)));
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
                    var metricBody = string.Join(
                        "\n",
                        CapabilityAMetricBody(metric),
                        string.Empty,
                        ClientUiText.Format("admin.runtime.kpi_a.fact.unit", lang, metric.Unit));
                    trackedMetricsHost.Children.Add(BuildTextCard(
                        CapabilityAMetricTitle(metric.Key),
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
                        CapabilityAAlertTitle(alert.Key),
                        CapabilityAAlertBody(alert),
                        tone: ClientUiText.Format("admin.runtime.quality.fact.severity", lang, CapabilityASeverityLabel(alert.Severity))));
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
                    Text = CapabilityADashboardPanelLabel(panel),
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
                trackedMetricsHost.Children.Add(BuildDialogInfoBanner(
                    FormatAdminLoadErrorForUser(ex, "/admin/runtime/capability-a/kpis", lang)));
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
                    BuildDialogInfoBanner(ClientUiText.Get("admin.runtime.kpi_a.help.body", lang)),
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
