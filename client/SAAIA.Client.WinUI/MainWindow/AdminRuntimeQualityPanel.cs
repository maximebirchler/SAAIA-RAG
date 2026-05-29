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
                    var jobLabel = jobId.HasValue ? ShortJobId(jobId.Value) : ClientUiText.Get("ui.not_available", lang);
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
                Status(ClientUiText.Get("admin.runtime.quality.regenerate_failed", lang) + BuildAdminRuntimeActionErrorDetail(ex, lang));
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
                    summary.LowestQualityScore.HasValue ? summary.LowestQualityScore.Value.ToString("0.00", CultureInfo.InvariantCulture) : ClientUiText.Get("ui.not_available", lang))
            };

            for (var index = 0; index < tiles.Length; index++)
            {
                Grid.SetColumn(tiles[index], index);
                Grid.SetRow(tiles[index], 0);
                metricsGrid.Children.Add(tiles[index]);
            }
        }

        string QualitySeverityLabel(string? severity)
            => (severity ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "critical" => LocalRuntimeText("critique", "critical", "critico", "critico", "kritisch", "critico", lang),
                "high" => LocalRuntimeText("eleve", "high", "alto", "alto", "hoch", "alto", lang),
                "medium" => LocalRuntimeText("moyen", "medium", "medio", "medio", "mittel", "medio", lang),
                "low" => LocalRuntimeText("faible", "low", "bajo", "baixo", "niedrig", "basso", lang),
                "" => LocalRuntimeText("inconnu", "unknown", "desconocido", "desconhecido", "unbekannt", "sconosciuto", lang),
                _ => LocalRuntimeText("a verifier", "to review", "por revisar", "a rever", "zu pruefen", "da verificare", lang)
            };

        string QualityActionLabel(string? action)
            => (action ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "stabilize_runtime_then_regenerate" => LocalRuntimeText("stabiliser le moteur serveur puis regenerer", "stabilize the server engine, then regenerate", "estabilizar el motor servidor y regenerar", "estabilizar o motor servidor e regenerar", "Server-Engine stabilisieren, dann neu generieren", "stabilizzare il motore server, poi rigenerare", lang),
                "regenerate_with_context_review" => LocalRuntimeText("verifier le contexte puis regenerer", "check context, then regenerate", "revisar el contexto y regenerar", "verificar o contexto e regenerar", "Kontext pruefen, dann neu generieren", "controllare il contesto e rigenerare", lang),
                "regenerate_summary" => LocalRuntimeText("regenerer le resume", "regenerate the summary", "regenerar el resumen", "regenerar o resumo", "Zusammenfassung neu generieren", "rigenerare il riepilogo", lang),
                "manual_review" => LocalRuntimeText("revue manuelle", "manual review", "revision manual", "revisao manual", "manuelle Pruefung", "revisione manuale", lang),
                "" => LocalRuntimeText("a confirmer", "to confirm", "por confirmar", "a confirmar", "zu bestaetigen", "da confermare", lang),
                _ => LocalRuntimeText("a verifier", "to review", "por revisar", "a rever", "zu pruefen", "da verificare", lang)
            };

        string QualityStrategyLabel(string? strategy)
            => (strategy ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "about" => LocalRuntimeText("resume court du document", "short document summary", "resumen corto del documento", "resumo curto do documento", "kurze Dokumentzusammenfassung", "riepilogo breve del documento", lang),
                "summary" => LocalRuntimeText("resume documentaire", "document summary", "resumen documental", "resumo documental", "Dokumentzusammenfassung", "riepilogo documentale", lang),
                "store" => LocalRuntimeText("resume stocke", "stored summary", "resumen almacenado", "resumo armazenado", "gespeicherte Zusammenfassung", "riepilogo archiviato", lang),
                "unknown" or "" => LocalRuntimeText("strategie inconnue", "unknown strategy", "estrategia desconocida", "estrategia desconhecida", "unbekannte Strategie", "strategia sconosciuta", lang),
                _ => LocalRuntimeText("strategie a verifier", "strategy to review", "estrategia por revisar", "estrategia a rever", "Strategie pruefen", "strategia da verificare", lang)
            };

        string QualityRuntimeStatusLabel(string? status)
            => (status ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "runtime_available" or "ok" => LocalRuntimeText("moteur serveur disponible", "server engine available", "motor servidor disponible", "motor servidor disponivel", "Server-Engine verfuegbar", "motore server disponibile", lang),
                "runtime_unavailable" => LocalRuntimeText("moteur serveur indisponible", "server engine unavailable", "motor servidor no disponible", "motor servidor indisponivel", "Server-Engine nicht verfuegbar", "motore server non disponibile", lang),
                "fallback" or "fallback_used" => LocalRuntimeText("resume de secours utilise", "backup summary used", "resumen de respaldo usado", "resumo de contingencia usado", "Ersatz-Zusammenfassung genutzt", "riepilogo di riserva usato", lang),
                "unknown" or "" => LocalRuntimeText("etat a verifier", "state to review", "estado por revisar", "estado a rever", "Status pruefen", "stato da verificare", lang),
                _ => LocalRuntimeText("etat a verifier", "state to review", "estado por revisar", "estado a rever", "Status pruefen", "stato da verificare", lang)
            };

        string QualityFallbackReasonLabel(string? reason)
            => (reason ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "runtime_unavailable" => LocalRuntimeText("moteur serveur indisponible", "server engine unavailable", "motor servidor no disponible", "motor servidor indisponivel", "Server-Engine nicht verfuegbar", "motore server non disponibile", lang),
                "timeout" => LocalRuntimeText("delai depasse", "timeout", "tiempo agotado", "tempo excedido", "Zeitueberschreitung", "timeout", lang),
                "empty_response" or "empty" => LocalRuntimeText("reponse vide", "empty response", "respuesta vacia", "resposta vazia", "leere Antwort", "risposta vuota", lang),
                "quality_too_low" => LocalRuntimeText("qualite trop faible", "quality too low", "calidad demasiado baja", "qualidade demasiado baixa", "Qualitaet zu niedrig", "qualita troppo bassa", lang),
                "unknown" or "" => LocalRuntimeText("raison a verifier", "reason to review", "motivo por revisar", "razao a rever", "Grund pruefen", "motivo da verificare", lang),
                _ => LocalRuntimeText("raison a verifier", "reason to review", "motivo por revisar", "razao a rever", "Grund pruefen", "motivo da verificare", lang)
            };

        string QualityRecommendationLabel(string? recommendation)
        {
            var normalized = (recommendation ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized.Length == 0)
                return string.Empty;

            if (normalized.Contains("deterministic fallback", StringComparison.OrdinalIgnoreCase) || normalized.Contains("fallback", StringComparison.OrdinalIgnoreCase))
                return LocalRuntimeText("Verifier pourquoi un resume de secours a ete utilise au lieu du moteur serveur.", "Check why a backup summary was used instead of the server engine.", "Revisar por que se uso un resumen de respaldo en lugar del motor servidor.", "Verificar porque foi usado um resumo de contingencia em vez do motor servidor.", "Pruefen, warum eine Ersatz-Zusammenfassung statt der Server-Engine verwendet wurde.", "Verificare perche e stato usato un riepilogo di riserva invece del motore server.", lang);
            if (normalized.Contains("section coverage", StringComparison.OrdinalIgnoreCase))
                return LocalRuntimeText("Regenerer avec un meilleur contexte : le resume ne reprend pas assez les sections du document.", "Regenerate with better context: the summary does not reflect enough document sections.", "Regenerar con mejor contexto: el resumen no refleja suficientes secciones del documento.", "Regenerar com melhor contexto: o resumo nao reflete secoes suficientes do documento.", "Mit besserem Kontext neu generieren: die Zusammenfassung spiegelt zu wenige Dokumentabschnitte wider.", "Rigenerare con un contesto migliore: il riepilogo non riflette abbastanza sezioni del documento.", lang);
            if (normalized.Contains("keyword", StringComparison.OrdinalIgnoreCase) || normalized.Contains("excerpt", StringComparison.OrdinalIgnoreCase))
                return LocalRuntimeText("Regenerer avec plus d'extraits utiles : trop peu de mots importants sont repris.", "Regenerate with more useful excerpts: too few important terms are reflected.", "Regenerar con mas extractos utiles: aparecen pocos terminos importantes.", "Regenerar com mais excertos uteis: aparecem poucos termos importantes.", "Mit mehr relevanten Auszuegen neu generieren: zu wenige wichtige Begriffe werden abgedeckt.", "Rigenerare con piu estratti utili: pochi termini importanti sono ripresi.", lang);
            if (normalized.Contains("structure", StringComparison.OrdinalIgnoreCase))
                return LocalRuntimeText("Regenerer le resume : la structure attendue n'est pas respectee.", "Regenerate the summary: the expected structure is not respected.", "Regenerar el resumen: no respeta la estructura esperada.", "Regenerar o resumo: a estrutura esperada nao foi respeitada.", "Zusammenfassung neu generieren: die erwartete Struktur wird nicht eingehalten.", "Rigenerare il riepilogo: la struttura prevista non e rispettata.", lang);
            if (normalized.Contains("length", StringComparison.OrdinalIgnoreCase))
                return LocalRuntimeText("Regenerer le resume : il est trop court ou trop long.", "Regenerate the summary: it is too short or too long.", "Regenerar el resumen: es demasiado corto o demasiado largo.", "Regenerar o resumo: esta demasiado curto ou demasiado longo.", "Zusammenfassung neu generieren: sie ist zu kurz oder zu lang.", "Rigenerare il riepilogo: e troppo corto o troppo lungo.", lang);
            if (normalized.Contains("no low-quality", StringComparison.OrdinalIgnoreCase))
                return LocalRuntimeText("Aucun resume faible n'est actuellement detecte.", "No weak summary is currently detected.", "No se detecta ningun resumen debil.", "Nenhum resumo fraco foi detetado.", "Aktuell wurde keine schwache Zusammenfassung erkannt.", "Nessun riepilogo debole rilevato.", lang);
            if (normalized.Contains("runtime availability", StringComparison.OrdinalIgnoreCase))
                return LocalRuntimeText("Verifier d'abord la disponibilite du moteur serveur.", "Check server engine availability first.", "Comprobar primero la disponibilidad del motor servidor.", "Verificar primeiro a disponibilidade do motor servidor.", "Zuerst die Verfuegbarkeit der Server-Engine pruefen.", "Verificare prima la disponibilita del motore server.", lang);
            if (normalized.Contains("worst summaries", StringComparison.OrdinalIgnoreCase))
                return LocalRuntimeText("Traiter d'abord les resumes avec les scores les plus faibles.", "Handle the lowest-scoring summaries first.", "Tratar primero los resumenes con peor puntuacion.", "Tratar primeiro os resumos com pior pontuacao.", "Zuerst die am schlechtesten bewerteten Zusammenfassungen behandeln.", "Gestire prima i riepiloghi con punteggio piu basso.", lang);
            if (normalized.Contains("prompt drift", StringComparison.OrdinalIgnoreCase) || normalized.Contains("retrieval context", StringComparison.OrdinalIgnoreCase))
                return LocalRuntimeText("Verifier le prompt et le contexte de recuperation des documents concernes.", "Check the prompt and retrieval context for the affected documents.", "Revisar el prompt y el contexto de recuperacion de los documentos afectados.", "Verificar o prompt e o contexto de recuperacao dos documentos afetados.", "Prompt und Suchkontext der betroffenen Dokumente pruefen.", "Controllare prompt e contesto di recupero dei documenti interessati.", lang);

            return LocalRuntimeText(
                "Revoir ce resume : le serveur a signale un point qualite non classe.",
                "Review this summary: the server reported an unclassified quality signal.",
                "Revisar este resumen: el servidor senalo una alerta de calidad no clasificada.",
                "Rever este resumo: o servidor sinalizou um ponto de qualidade sem categoria.",
                "Diese Zusammenfassung pruefen: der Server meldete ein nicht klassifiziertes Qualitaetssignal.",
                "Rivedere questo riepilogo: il server ha segnalato un punto qualita non classificato.",
                lang);
        }

        FrameworkElement BuildDistributionCard(string title, IReadOnlyList<AdminRuntimeNamedCountItem> items, Func<string, string> labelFormatter)
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
                        Text = $"{labelFormatter(item.Key)}: {item.Count}",
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
                Text = ClientUiText.Format("admin.runtime.quality.fact.severity", lang, QualitySeverityLabel(item.Severity))
            });
            facts.Children.Add(new TextBlock
            {
                Text = ClientUiText.Format("admin.runtime.quality.fact.recommended_action", lang, QualityActionLabel(item.RecommendedAction)),
                TextWrapping = TextWrapping.WrapWholeWords
            });
            facts.Children.Add(new TextBlock
            {
                Text = ClientUiText.Format("admin.runtime.quality.fact.strategy", lang, QualityStrategyLabel(item.Strategy))
            });
            facts.Children.Add(new TextBlock
            {
                Text = ClientUiText.Format("admin.runtime.quality.fact.runtime_status", lang, QualityRuntimeStatusLabel(item.RuntimeCapabilityStatus))
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
                    Text = ClientUiText.Format("admin.runtime.quality.fact.fallback", lang, QualityFallbackReasonLabel(item.FallbackReason))
                });
            }

            if (item.Signals.SectionCoverageScore.HasValue || item.Signals.KeywordCoverageScore.HasValue)
            {
                var sectionCoverage = item.Signals.SectionCoverageScore?.ToString("0.00", CultureInfo.InvariantCulture) ?? ClientUiText.Get("ui.not_available", lang);
                var keywordCoverage = item.Signals.KeywordCoverageScore?.ToString("0.00", CultureInfo.InvariantCulture) ?? ClientUiText.Get("ui.not_available", lang);
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
                    var recommendationText = QualityRecommendationLabel(recommendation);
                    if (string.IsNullOrWhiteSpace(recommendationText))
                        continue;

                    stack.Children.Add(new TextBlock
                    {
                        Text = "- " + recommendationText,
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
                snapshot.Summary.StrategyCounts,
                QualityStrategyLabel));
            summaryHost.Children.Add(BuildDistributionCard(
                ClientUiText.Get("admin.runtime.quality.section.statuses", lang),
                snapshot.Summary.RuntimeStatusCounts,
                QualityRuntimeStatusLabel));

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
                    var recommendationText = QualityRecommendationLabel(recommendation);
                    if (string.IsNullOrWhiteSpace(recommendationText))
                        continue;

                    recommendationStack.Children.Add(new TextBlock
                    {
                        Text = "- " + recommendationText,
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
                itemsHost.Children.Add(BuildDialogInfoBanner(FormatAdminLoadErrorForUser(
                    ex,
                    "/admin/runtime/capabilities/capability_b.backoffice_generation/quality-review",
                    lang)));
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
                    BuildDialogInfoBanner(ClientUiText.Get("admin.runtime.quality.help.body", lang)),
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
