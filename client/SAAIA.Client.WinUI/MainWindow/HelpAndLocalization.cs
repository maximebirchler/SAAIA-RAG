using System.Net;
using System.Net.Http;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using SAAIA.Client.WinUI.Localization;
using SAAIA.Client.WinUI.Services.ToolAgent;

namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow
{
    private enum HelpGuidedMode
    {
        None,
        CategoryDocuments,
        CategoryStats,
        DocumentSearch,
        ReindexDocument
    }

    private sealed record HelpCategory(string CategoryRef, string CategoryPath, string DisplayName, int DocumentCount, string SearchText);
    private sealed record HelpDocument(string? DocId, string DocPath, string DocName, string CategoryPath);

    private string UiLang => ClientUiText.NormalizeLanguage(_appSettings.UiLanguage);

    private void ApplyUiLanguage()
    {
        var lang = UiLang;

        TrySoftUi("ApplyUiLanguage.Tooltip.ChatsToggleButton", () => ToolTipService.SetToolTip(ChatsToggleButton, ClientUiText.Get("header.chats", lang)));
        TrySoftUi("ApplyUiLanguage.Tooltip.SetupButton", () => ToolTipService.SetToolTip(SetupButton, ClientUiText.Get("button.setup", lang)));
        TrySoftUi("ApplyUiLanguage.Tooltip.HeaderHelpButton", () => ToolTipService.SetToolTip(HeaderHelpButton, ClientUiText.Get("header.help", lang)));
        TrySoftUi("ApplyUiLanguage.Tooltip.HeaderSettingsButton", () => ToolTipService.SetToolTip(HeaderSettingsButton, ClientUiText.Get("header.settings", lang)));
        TrySoftUi("ApplyUiLanguage.ChatsHeaderText", () => ChatsHeaderText.Text = ClientUiText.Get("panel.chats", lang));
        TrySoftUi("ApplyUiLanguage.NewChatButton", () => NewChatButton.Content = ClientUiText.Get("button.new", lang));
        TrySoftUi("ApplyUiLanguage.JumpBottomButton", () => JumpBottomButton.Content = ClientUiText.Get("button.jump_bottom", lang));
        TrySoftUi("ApplyUiLanguage.TypingText", () => TypingText.Text = ClientUiText.Get("typing", lang));
        TrySoftUi("ApplyUiLanguage.InputBox.PlaceholderText", () => InputBox.PlaceholderText = ClientUiText.Get("input.placeholder", lang));
        TrySoftUi("ApplyUiLanguage.ConnectButton", () => ConnectButton.Content = ClientUiText.Get("button.connect", lang));
        TrySoftUi("ApplyUiLanguage.UserSettingsButton", () => UserSettingsButton.Content = ClientUiText.Get("header.settings", lang));
        TrySoftUi("ApplyUiLanguage.ApplyLocalizedDefaultSessionTitles", ApplyLocalizedDefaultSessionTitles);

        UpdateUiState(_isGenerating);
    }

    private async void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var xamlRoot = await GetDialogXamlRootAsync().ConfigureAwait(true);
            if (xamlRoot is null)
                return;

            var lang = UiLang;
            var canRun = !_isGenerating && _agent is not null && !string.IsNullOrWhiteSpace(_sessionId);
            var isAdmin = _api.HasAdminKey;
            List<HelpCategory> categories = new();
            var categoriesLoaded = false;
            var categoriesLoadStarted = false;
            Exception? categoriesLoadError = null;

            if (_activeHelpOverlay is not null)
            {
                TrySoftUi("HelpButton_Click.CloseActiveOverlay", _activeHelpOverlay.Close);
                _activeHelpOverlay = null;
            }

            OverlayDialogSession? overlay = null;

            var root = new StackPanel { Spacing = 16, MaxWidth = 860 };
            var closeButton = BuildDialogFooterButton(ClientUiText.Get("dialog.close", lang));
            closeButton.Click += (_, __) => overlay?.Close();

            var quickPanel = new StackPanel { Spacing = 8 };
            quickPanel.Children.Add(CreateActionButton(ClientUiText.Get("cmd.catalog.categories", lang), null, canRun, async () =>
            {
                overlay?.Close();
                await TryExecuteHelpDirectCommandAsync(DirectCommandCatalog.CatalogCategoriesList, new { }, ClientUiText.BuildPromptCategories(lang)).ConfigureAwait(true);
            }));
            quickPanel.Children.Add(CreateActionButton(ClientUiText.Get("cmd.catalog.stats", lang), null, canRun, async () =>
            {
                overlay?.Close();
                await TryExecuteHelpDirectCommandAsync(DirectCommandCatalog.CatalogStatsView, new { }, ClientUiText.BuildPromptCatalogStats(lang)).ConfigureAwait(true);
            }));
            quickPanel.Children.Add(CreateActionButton(ClientUiText.Get("cmd.catalog.tree", lang), null, canRun, async () =>
            {
                overlay?.Close();
                await TryExecuteHelpDirectCommandAsync(DirectCommandCatalog.CatalogTreeView, new { depth = 12, format = "markdown" }, ClientUiText.BuildPromptCatalogTree(lang)).ConfigureAwait(true);
            }));
            root.Children.Add(CreateSectionCard(ClientUiText.Get("help.section.quick", lang), new UIElement[] { quickPanel }));

            var guidedButtons = new StackPanel { Spacing = 8 };
            var modeDescription = new TextBlock
            {
                Text = ClientUiText.Get("help.mode.none", lang),
                Opacity = 0.84,
                TextWrapping = TextWrapping.WrapWholeWords
            };
            var searchBox = new TextBox
            {
                Visibility = Visibility.Collapsed,
                PlaceholderText = ClientUiText.Get("help.search.placeholder.category", lang),
                MinWidth = 320
            };
            var searchButton = BuildDialogFooterButton(ClientUiText.Get("button.search", lang), primary: true);
            searchButton.Visibility = Visibility.Collapsed;
            searchButton.HorizontalAlignment = HorizontalAlignment.Left;
            searchButton.MinWidth = 140;
            var helperText = new TextBlock
            {
                Opacity = 0.9,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.WrapWholeWords
            };
            var searchAnchor = new Border { Padding = new Thickness(0, 1, 0, 0) };
            var resultsPanel = new StackPanel { Spacing = 8 };
            HelpGuidedMode currentMode = HelpGuidedMode.None;

            async Task EnsureCategoriesLoadedAsync()
            {
                if (categoriesLoaded || categoriesLoadStarted)
                    return;

                categoriesLoadStarted = true;
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var snapshot = await _api.CatalogSnapshotAsync(cts.Token).ConfigureAwait(true);
                    categories = ParseCategories(snapshot);
                    categoriesLoaded = true;
                }
                catch (Exception ex)
                {
                    categoriesLoadError = ex;
                }
            }

            void RenderCategoryResults()
            {
                resultsPanel.Children.Clear();
                if (!categoriesLoaded)
                {
                    resultsPanel.Children.Add(new TextBlock
                    {
                        Text = categoriesLoadError is null ? ClientUiText.Get("help.loading", lang) : categoriesLoadError.Message,
                        Opacity = 0.72,
                        TextWrapping = TextWrapping.WrapWholeWords
                    });
                    return;
                }

                if (categories.Count == 0)
                {
                    resultsPanel.Children.Add(new TextBlock
                    {
                        Text = ClientUiText.Get("help.none.categories", lang),
                        Opacity = 0.72,
                        TextWrapping = TextWrapping.WrapWholeWords
                    });
                    return;
                }

                var query = (searchBox.Text ?? string.Empty).Trim();
                var hits = FilterCategories(categories, query).Take(10).ToList();
                if (hits.Count == 0)
                {
                    resultsPanel.Children.Add(new TextBlock
                    {
                        Text = ClientUiText.Get("help.search.no_results", lang),
                        Opacity = 0.72,
                        TextWrapping = TextWrapping.WrapWholeWords
                    });
                    return;
                }

                foreach (var category in hits)
                {
                    var categoryLabel = GetCategoryLabel(category);
                    var subtitle = string.IsNullOrWhiteSpace(category.CategoryPath)
                        ? ClientUiText.Format("help.meta.documents", lang, category.DocumentCount)
                        : $"{category.CategoryPath} • {ClientUiText.Format("help.meta.documents", lang, category.DocumentCount)}";

                    resultsPanel.Children.Add(CreateActionButton(
                        categoryLabel,
                        subtitle,
                        canRun,
                        async () =>
                        {
                            var prompt = currentMode switch
                            {
                                HelpGuidedMode.CategoryStats => ClientUiText.BuildPromptCategoryStats(lang, categoryLabel),
                                _ => ClientUiText.BuildPromptCategoryDocuments(lang, categoryLabel)
                            };
                            var commandId = currentMode == HelpGuidedMode.CategoryStats
                                ? DirectCommandCatalog.CatalogStatsView
                                : DirectCommandCatalog.CatalogDocumentsListByCategory;

                            overlay?.Close();
                            await TryExecuteHelpDirectCommandAsync(commandId, new { categoryRef = category.CategoryRef }, prompt).ConfigureAwait(true);
                        }));
                }
            }

            async Task SearchDocumentsAsync()
            {
                resultsPanel.Children.Clear();
                var query = (searchBox.Text ?? string.Empty).Trim();
                if (query.Length < 2)
                {
                    resultsPanel.Children.Add(new TextBlock
                    {
                        Text = ClientUiText.Get("help.search.type_more", lang),
                        Opacity = 0.72,
                        TextWrapping = TextWrapping.WrapWholeWords
                    });
                    return;
                }

                resultsPanel.Children.Add(new TextBlock
                {
                    Text = ClientUiText.Get("help.search.loading", lang),
                    Opacity = 0.72,
                    TextWrapping = TextWrapping.WrapWholeWords
                });

                try
                {
                    var json = await _api.DocumentsSearchAsync(q: query, categoryPath: null, categoryRef: null, limit: 10, offset: 0, ct: CancellationToken.None).ConfigureAwait(true);
                    var docs = ParseDocuments(json);
                    resultsPanel.Children.Clear();

                    if (docs.Count == 0)
                    {
                        resultsPanel.Children.Add(new TextBlock
                        {
                            Text = ClientUiText.Get("help.none.documents", lang),
                            Opacity = 0.72,
                            TextWrapping = TextWrapping.WrapWholeWords
                        });
                        return;
                    }

                    foreach (var doc in docs)
                    {
                        var docTitle = string.IsNullOrWhiteSpace(doc.DocName) ? doc.DocPath : doc.DocName;
                        var exactFileName = GetExactDocumentFileLabel(doc);
                        var docRef = string.IsNullOrWhiteSpace(doc.DocPath) ? docTitle : doc.DocPath;
                        var subtitle = string.IsNullOrWhiteSpace(doc.CategoryPath) ? docRef : $"{doc.CategoryPath} • {docRef}";
                        var prompt = currentMode == HelpGuidedMode.ReindexDocument
                            ? ClientUiText.BuildPromptAdminReindex(lang, exactFileName)
                            : ClientUiText.BuildPromptSearchDocuments(lang, docTitle);
                        var displayPrompt = currentMode == HelpGuidedMode.ReindexDocument
                            ? ClientUiText.BuildPromptAdminReindexDisplay(lang, exactFileName)
                            : prompt;
                        var buttonTitle = currentMode == HelpGuidedMode.ReindexDocument ? exactFileName : docTitle;

                        resultsPanel.Children.Add(CreateActionButton(
                            buttonTitle,
                            subtitle,
                            canRun,
                            async () =>
                            {
                                overlay?.Close();
                                var commandId = currentMode == HelpGuidedMode.ReindexDocument
                                    ? DirectCommandCatalog.AdminIngestionReindexDocument
                                    : DirectCommandCatalog.CatalogDocumentsSearch;
                                object args = currentMode == HelpGuidedMode.ReindexDocument
                                    ? new
                                    {
                                        documentRef = doc.DocPath,
                                        docPath = doc.DocPath,
                                        docId = doc.DocId,
                                        docName = exactFileName,
                                        displayName = exactFileName
                                    }
                                    : new { query = docTitle };

                                await TryExecuteHelpDirectCommandAsync(commandId, args, displayPrompt).ConfigureAwait(true);
                            }));
                    }
                }
                catch (Exception ex)
                {
                    resultsPanel.Children.Clear();
                    resultsPanel.Children.Add(new TextBlock
                    {
                        Text = ex.Message,
                        Opacity = 0.72,
                        TextWrapping = TextWrapping.WrapWholeWords
                    });
                }
            }

            async Task ScrollSearchRegionIntoViewAsync()
            {
                await Task.Delay(30).ConfigureAwait(true);
                searchAnchor.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.08, AnimationDesired = true });
                searchBox.Focus(FocusState.Programmatic);
            }

            async Task ActivateModeAsync(HelpGuidedMode mode)
            {
                currentMode = mode;
                resultsPanel.Children.Clear();
                searchBox.Text = string.Empty;

                switch (mode)
                {
                    case HelpGuidedMode.CategoryDocuments:
                        modeDescription.Text = ClientUiText.Get("help.mode.category.documents", lang);
                        searchBox.Visibility = Visibility.Visible;
                        searchButton.Visibility = Visibility.Collapsed;
                        searchBox.PlaceholderText = ClientUiText.Get("help.search.placeholder.category", lang);
                        helperText.Text = ClientUiText.Get("help.search.hint.category", lang) + Environment.NewLine + ClientUiText.Get("help.search.next_step.category", lang);
                        await EnsureCategoriesLoadedAsync().ConfigureAwait(true);
                        RenderCategoryResults();
                        await ScrollSearchRegionIntoViewAsync().ConfigureAwait(true);
                        break;

                    case HelpGuidedMode.CategoryStats:
                        modeDescription.Text = ClientUiText.Get("help.mode.category.stats", lang);
                        searchBox.Visibility = Visibility.Visible;
                        searchButton.Visibility = Visibility.Collapsed;
                        searchBox.PlaceholderText = ClientUiText.Get("help.search.placeholder.category", lang);
                        helperText.Text = ClientUiText.Get("help.search.hint.category", lang) + Environment.NewLine + ClientUiText.Get("help.search.next_step.category", lang);
                        await EnsureCategoriesLoadedAsync().ConfigureAwait(true);
                        RenderCategoryResults();
                        await ScrollSearchRegionIntoViewAsync().ConfigureAwait(true);
                        break;

                    case HelpGuidedMode.DocumentSearch:
                        modeDescription.Text = ClientUiText.Get("help.mode.document.search", lang);
                        searchBox.Visibility = Visibility.Visible;
                        searchButton.Visibility = Visibility.Visible;
                        searchBox.PlaceholderText = ClientUiText.Get("help.search.placeholder.document", lang);
                        helperText.Text = ClientUiText.Get("help.search.hint.document", lang) + Environment.NewLine + ClientUiText.Get("help.search.next_step.document", lang);
                        resultsPanel.Children.Add(new TextBlock
                        {
                            Text = ClientUiText.Get("help.search.type_more", lang),
                            Opacity = 0.72,
                            TextWrapping = TextWrapping.WrapWholeWords
                        });
                        await ScrollSearchRegionIntoViewAsync().ConfigureAwait(true);
                        break;

                    case HelpGuidedMode.ReindexDocument:
                        modeDescription.Text = ClientUiText.Get("help.mode.document.reindex", lang);
                        searchBox.Visibility = Visibility.Visible;
                        searchButton.Visibility = Visibility.Visible;
                        searchBox.PlaceholderText = ClientUiText.Get("help.search.placeholder.document", lang);
                        helperText.Text = ClientUiText.Get("help.search.hint.document", lang) + Environment.NewLine + ClientUiText.Get("help.search.next_step.document", lang);
                        resultsPanel.Children.Add(new TextBlock
                        {
                            Text = ClientUiText.Get("help.search.type_more", lang),
                            Opacity = 0.72,
                            TextWrapping = TextWrapping.WrapWholeWords
                        });
                        await ScrollSearchRegionIntoViewAsync().ConfigureAwait(true);
                        break;

                    default:
                        modeDescription.Text = ClientUiText.Get("help.mode.none", lang);
                        searchBox.Visibility = Visibility.Collapsed;
                        searchButton.Visibility = Visibility.Collapsed;
                        helperText.Text = string.Empty;
                        break;
                }
            }

            guidedButtons.Children.Add(CreateActionButton(
                ClientUiText.Get("cmd.guided.document_search", lang),
                null,
                true,
                async () => await ActivateModeAsync(HelpGuidedMode.DocumentSearch).ConfigureAwait(true)));

            guidedButtons.Children.Add(CreateActionButton(
                ClientUiText.Get("cmd.guided.documents_by_category", lang),
                ClientUiText.Get("help.search.top_categories", lang),
                true,
                async () => await ActivateModeAsync(HelpGuidedMode.CategoryDocuments).ConfigureAwait(true)));

            guidedButtons.Children.Add(CreateActionButton(
                ClientUiText.Get("cmd.guided.category_stats", lang),
                ClientUiText.Get("help.search.top_categories", lang),
                true,
                async () => await ActivateModeAsync(HelpGuidedMode.CategoryStats).ConfigureAwait(true)));

            if (isAdmin)
            {
                guidedButtons.Children.Add(CreateActionButton(
                    ClientUiText.Get("cmd.guided.reindex", lang),
                    null,
                    true,
                    async () => await ActivateModeAsync(HelpGuidedMode.ReindexDocument).ConfigureAwait(true)));
            }

            searchBox.TextChanged += (_, __) =>
            {
                if (currentMode is HelpGuidedMode.CategoryDocuments or HelpGuidedMode.CategoryStats)
                    RenderCategoryResults();
            };

            searchBox.KeyDown += async (_, args) =>
            {
                if (args.Key != Windows.System.VirtualKey.Enter)
                    return;
                if (currentMode is HelpGuidedMode.DocumentSearch or HelpGuidedMode.ReindexDocument)
                {
                    args.Handled = true;
                    await SearchDocumentsAsync().ConfigureAwait(true);
                }
            };
            searchButton.Click += async (_, __) => await SearchDocumentsAsync().ConfigureAwait(true);

            var guidedCardChildren = new List<UIElement>
            {
                guidedButtons,
                modeDescription,
                searchAnchor,
                searchBox,
                searchButton,
                helperText,
                BuildSectionHeader(ClientUiText.Get("help.section.results", lang)),
                resultsPanel
            };
            root.Children.Add(CreateSectionCard(ClientUiText.Get("help.section.guided", lang), guidedCardChildren));

            if (isAdmin)
            {
                var adminPanel = new StackPanel { Spacing = 8 };
                adminPanel.Children.Add(CreateActionButton(ClientUiText.Get("cmd.summary.missing.count", lang), null, canRun, async () =>
                {
                    overlay?.Close();
                    await TryExecuteHelpDirectCommandAsync(DirectCommandCatalog.CatalogSummariesMissingCount, new { }, ClientUiText.BuildPromptSummaryMissingCount(lang)).ConfigureAwait(true);
                }));
                adminPanel.Children.Add(CreateActionButton(ClientUiText.Get("cmd.summary.missing.list", lang), null, canRun, async () =>
                {
                    overlay?.Close();
                    await TryExecuteHelpDirectCommandAsync(DirectCommandCatalog.CatalogSummariesMissingList, new { pageSize = 100 }, ClientUiText.BuildPromptSummaryMissingList(lang)).ConfigureAwait(true);
                }));
                adminPanel.Children.Add(CreateActionButton(ClientUiText.Get("cmd.summary.present.count", lang), null, canRun, async () =>
                {
                    overlay?.Close();
                    await TryExecuteHelpDirectCommandAsync(DirectCommandCatalog.CatalogSummariesPresentCount, new { }, ClientUiText.BuildPromptSummaryPresentCount(lang)).ConfigureAwait(true);
                }));
                adminPanel.Children.Add(CreateActionButton(ClientUiText.Get("cmd.summary.present.list", lang), null, canRun, async () =>
                {
                    overlay?.Close();
                    await TryExecuteHelpDirectCommandAsync(DirectCommandCatalog.CatalogSummariesPresentList, new { pageSize = 100 }, ClientUiText.BuildPromptSummaryPresentList(lang)).ConfigureAwait(true);
                }));
                adminPanel.Children.Add(CreateActionButton(ClientUiText.Get("cmd.admin.rescan", lang), null, canRun, async () =>
                {
                    overlay?.Close();
                    await TryExecuteHelpDirectCommandAsync(DirectCommandCatalog.AdminCatalogRescan, new { }, ClientUiText.BuildPromptAdminRescan(lang)).ConfigureAwait(true);
                }));
                root.Children.Add(CreateSectionCard(ClientUiText.Get("help.section.admin", lang), new UIElement[] { adminPanel }));
            }

            var dialogSize = GetDialogMaxSize(900, 760, horizontalMargin: 56, verticalMargin: 88);
            var shell = BuildScrollableDialogShell(
                ClientUiText.Get("header.help", lang),
                ClientUiText.Get("help.title", lang),
                !canRun
                    ? (_isGenerating ? ClientUiText.Get("help.subtitle.busy", lang) : ClientUiText.Get("help.subtitle.disconnected", lang))
                    : ClientUiText.Get("help.subtitle.ready", lang),
                new UIElement[] { root },
                BuildDialogFooter(closeButton),
                dialogSize.Width,
                dialogSize.Height);
            overlay = ShowOverlayDialog(
                shell,
                resizeHandler: _ =>
                {
                    var size = GetDialogMaxSize(900, 760, horizontalMargin: 56, verticalMargin: 88);
                    shell.MaxWidth = size.Width;
                    shell.MaxHeight = size.Height;
                });

            _activeHelpOverlay = overlay;
            try
            {
                await overlay.Completion;
            }
            finally
            {
                if (ReferenceEquals(_activeHelpOverlay, overlay))
                    _activeHelpOverlay = null;
            }
        }
        catch (Exception ex)
        {
            Status(ClientUiText.Get("status.help_send_failed", UiLang) + ex.Message);
        }
    }

    private async Task<bool> TryExecuteHelpDirectCommandAsync(string commandId, object args, string displayText)
    {
        if (_isGenerating)
        {
            Status(ClientUiText.Get("status.help_busy", UiLang));
            return false;
        }

        if (_agent is null || string.IsNullOrWhiteSpace(_sessionId))
        {
            Status(ClientUiText.Get("status.help_connect_first", UiLang));
            return false;
        }

        ChatMessageItem? assistantMsg = null;

        try
        {
            _autoFollow = true;
            _userScrolledUp = false;
            UpdateJumpButton();
            UpdateUiState(isGenerating: true);
            InputBox.Text = string.Empty;
            _pendingOutboundWireText = null;
            _pendingOutboundDisplayText = null;

            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            assistantMsg = new ChatMessageItem
            {
                Role = "assistant",
                Content = string.Empty,
                CreatedAt = DateTime.UtcNow,
                StatusNote = null,
                ProgressText = ClientUiText.Get("help.loading", UiLang)
            };
            _messages.Add(assistantMsg);
            ScrollToBottom(force: true);

            SetTyping(true);
            SourcesCards.Items = new List<SourceCard>();
            SourcesBox.Text = string.Empty;

            var result = await _agent.ExecuteDirectCommandAsync(
                new DirectCommandRequest
                {
                    CommandId = commandId,
                    ArgsJson = JsonSerializer.Serialize(args),
                    DisplayText = displayText,
                    Language = UiLang
                },
                _cts.Token).ConfigureAwait(true);

            assistantMsg.ProgressText = null;
            assistantMsg.Content = string.IsNullOrWhiteSpace(result.FinalAnswer)
                ? "⚠️ Réponse vide."
                : DecodeEscapedUiText(result.FinalAnswer);
            assistantMsg.StatusNote = null;

            var pretty = result.SourcesPayload is null
                ? string.Empty
                : JsonSerializer.Serialize(result.SourcesPayload, new JsonSerializerOptions { WriteIndented = true });

            assistantMsg.SourcesJson = pretty;
            SourcesCards.Items = SourceCardParser.Parse(pretty);
            SourcesBox.Text = pretty;

            if (result.TrackedJob is not null && !string.IsNullOrWhiteSpace(result.TrackedJob.JobId))
                assistantMsg.TrackingMeta = BuildTrackedJobMeta(result.TrackedJob, isTerminal: false);

            var persistedAssistantMsg = await _api.AddMessageAsync(_sessionId!, "assistant", assistantMsg.Content, result.SourcesPayload, CancellationToken.None, assistantMsg.StatusNote, assistantMsg.ProgressText, assistantMsg.TrackingMeta);
            if (!string.IsNullOrWhiteSpace(persistedAssistantMsg?.MessageId))
                assistantMsg.MessageId = persistedAssistantMsg!.MessageId;

            if (result.TrackedJob is not null && !string.IsNullOrWhiteSpace(result.TrackedJob.JobId))
                StartDirectCommandJobTracker(result.TrackedJob, assistantMsg, _sessionId);

            try
            {
                await RefreshSessionsAsync(preferSessionId: _sessionId, CancellationToken.None);
            }
            catch
            {
            }

            if (_autoFollow && !_userScrolledUp)
                ScrollToBottom(force: true);

            ClearStatus();
            return true;
        }
        catch (OperationCanceledException)
        {
            SetTyping(false);
            UpdateJumpButton();
            MarkInterrupted(assistantMsg);
            return false;
        }
        catch (Exception ex)
        {
            EnsureAssistantMessageHasFailureText(assistantMsg);
            Status(ClientUiText.Get("status.help_send_failed", UiLang) + ex.Message);
            return false;
        }
        finally
        {
            UpdateUiState(isGenerating: false);
            SetTyping(false);
            UpdateJumpButton();
        }
    }

    private Border CreateSectionCard(string? title, IEnumerable<UIElement> body)
    {
        var light = UseLightPalette();
        var stack = new StackPanel { Spacing = 12 };
        if (!string.IsNullOrWhiteSpace(title))
        {
            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                Foreground = light ? UiBrush(0x3E, 0x4E, 0x61) : UiBrush(0xD1, 0xD9, 0xE6),
                Opacity = 0.94,
                TextWrapping = TextWrapping.WrapWholeWords
            });
        }

        foreach (var child in body)
            stack.Children.Add(child);

        return BuildDialogSurfaceCard(stack, new Thickness(16, 14, 16, 14));
    }

    private TextBlock BuildSectionHeader(string text)
        => new()
        {
            Text = text,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = UseLightPalette() ? UiBrush(0x5D, 0x6E, 0x82) : UiBrush(0xA8, 0xB5, 0xC7),
            CharacterSpacing = 40
        };

    private Button CreateActionButton(string title, string? subtitle, bool canRun, Func<Task> onClick)
    {
        var light = UseLightPalette();
        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(new TextBlock
        {
            Text = title,
            TextWrapping = TextWrapping.WrapWholeWords,
            FontWeight = FontWeights.SemiBold
        });

        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            content.Children.Add(new TextBlock
            {
                Text = subtitle,
                TextWrapping = TextWrapping.WrapWholeWords,
                Opacity = 0.78,
                FontSize = 12
            });
        }

        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(16, 14, 16, 14),
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            Background = light ? UiBrush(0xF7, 0xFA, 0xFD) : UiBrush(0x16, 0x1D, 0x27),
            BorderBrush = light ? UiBrush(0xC8, 0xD3, 0xDF) : UiBrush(0x2B, 0x35, 0x41),
            Foreground = light ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xF5, 0xF7, 0xFB),
            IsEnabled = canRun,
            UseSystemFocusVisuals = false,
            Content = content
        };

        button.Resources["ButtonBackgroundPointerOver"] = light ? UiBrush(0xEC, 0xF1, 0xF6) : UiBrush(0x1C, 0x24, 0x30);
        button.Resources["ButtonBackgroundPressed"] = light ? UiBrush(0xE1, 0xE8, 0xF0) : UiBrush(0x22, 0x2B, 0x38);
        button.Resources["ButtonBackgroundDisabled"] = light ? UiBrush(0xF1, 0xF4, 0xF8) : UiBrush(0x12, 0x15, 0x1B);
        button.Resources["ButtonBorderBrushPointerOver"] = light ? UiBrush(0xB8, 0xC8, 0xDA) : UiBrush(0x39, 0x45, 0x57);
        button.Resources["ButtonBorderBrushPressed"] = light ? UiBrush(0xA6, 0xB9, 0xCD) : UiBrush(0x4A, 0x59, 0x70);
        button.Resources["ButtonBorderBrushDisabled"] = light ? UiBrush(0xD4, 0xDC, 0xE6) : UiBrush(0x22, 0x26, 0x2E);
        button.Resources["ButtonForegroundPointerOver"] = button.Foreground;
        button.Resources["ButtonForegroundPressed"] = button.Foreground;
        button.Resources["ButtonForegroundDisabled"] = light ? UiBrush(0x84, 0x92, 0xA3) : UiBrush(0x7A, 0x84, 0x92);

        button.Click += async (_, __) => await onClick().ConfigureAwait(true);
        return button;
    }

    private static bool HasAny(HashSet<string> values, params string[] ids)
        => ids.Any(values.Contains);

    private static string GetExactDocumentFileLabel(HelpDocument doc)
    {
        var path = (doc.DocPath ?? string.Empty).Trim();
        var fileName = Path.GetFileName(path.Replace('\\', '/'));
        if (!string.IsNullOrWhiteSpace(fileName))
            return fileName;
        if (!string.IsNullOrWhiteSpace(doc.DocName))
            return doc.DocName.Trim();
        return path;
    }

    private static string DecodeEscapedUiText(string value)
    {
        var text = value ?? string.Empty;
        if (text.IndexOf(@"\u", StringComparison.Ordinal) < 0 && text.IndexOf(@"\n", StringComparison.Ordinal) < 0 && text.IndexOf(@"\/", StringComparison.Ordinal) < 0)
            return text;

        try
        {
            var decoded = Regex.Unescape(text);
            return string.IsNullOrWhiteSpace(decoded) ? text : decoded;
        }
        catch
        {
            return text;
        }
    }

    private static string GetCategoryLabel(HelpCategory category)
        => !string.IsNullOrWhiteSpace(category.CategoryPath)
            ? category.CategoryPath
            : (!string.IsNullOrWhiteSpace(category.DisplayName) ? category.DisplayName : category.CategoryRef);

    private static IEnumerable<HelpCategory> FilterCategories(IEnumerable<HelpCategory> categories, string query)
    {
        var list = categories.ToList();
        if (string.IsNullOrWhiteSpace(query))
        {
            return list
                .OrderByDescending(x => x.DocumentCount)
                .ThenBy(x => GetCategoryLabel(x), StringComparer.OrdinalIgnoreCase);
        }

        var q = query.Trim().ToLowerInvariant();
        return list
            .Select(x => new
            {
                Item = x,
                Score = GetCategoryScore(x, q)
            })
            .Where(x => x.Score < int.MaxValue)
            .OrderBy(x => x.Score)
            .ThenByDescending(x => x.Item.DocumentCount)
            .ThenBy(x => GetCategoryLabel(x.Item), StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Item);
    }

    private static int GetCategoryScore(HelpCategory category, string query)
    {
        var label = GetCategoryLabel(category).ToLowerInvariant();
        var path = (category.CategoryPath ?? string.Empty).ToLowerInvariant();
        var search = (category.SearchText ?? string.Empty).ToLowerInvariant();

        if (label.Equals(query, StringComparison.Ordinal)) return 0;
        if (path.Equals(query, StringComparison.Ordinal)) return 1;
        if (label.StartsWith(query, StringComparison.Ordinal)) return 2;
        if (path.StartsWith(query, StringComparison.Ordinal)) return 3;
        if (search.Contains(query, StringComparison.Ordinal)) return 10;
        return int.MaxValue;
    }

    private static HashSet<string> ParseCommandIds(JsonElement root, string propertyName)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Object)
            return result;
        if (!capabilities.TryGetProperty(propertyName, out var commands) || commands.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in commands.EnumerateArray())
        {
            if (TryGetString(item, "commandId") is { Length: > 0 } commandId)
                result.Add(commandId);
        }

        return result;
    }


    private void StartDirectCommandJobTracker(DirectCommandTrackedJob trackedJob, ChatMessageItem assistantMsg, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(trackedJob.JobId))
            return;

        assistantMsg.TrackingMeta ??= BuildTrackedJobMeta(trackedJob, isTerminal: false);

        ActiveDirectCommandTrackerState? existing;
        lock (_directCommandTrackerGate)
        {
            if (_activeDirectCommandTrackers.TryGetValue(trackedJob.JobId, out existing))
            {
                existing.SessionId = sessionId;
                existing.MessageId = assistantMsg.MessageId;
                existing.Message = assistantMsg;
                existing.LastContent ??= assistantMsg.Content;
                existing.LastProgressText ??= assistantMsg.ProgressText;
                existing.LastStatusNote ??= assistantMsg.StatusNote;
                existing.LastPersistedContent ??= assistantMsg.Content;
                existing.LastPersistedProgressText ??= assistantMsg.ProgressText;
                existing.LastPersistedStatusNote ??= assistantMsg.StatusNote;
                existing.LastPersistedTerminal = existing.LastPersistedTerminal || assistantMsg.TrackingMeta?.IsTerminal == true;
            }
            else
            {
                var cts = new CancellationTokenSource();
                var state = new ActiveDirectCommandTrackerState
                {
                    Job = trackedJob,
                    SessionId = sessionId,
                    Cancellation = cts,
                    MessageId = assistantMsg.MessageId,
                    Message = assistantMsg,
                    LastContent = assistantMsg.Content,
                    LastProgressText = assistantMsg.ProgressText,
                    LastStatusNote = assistantMsg.StatusNote,
                    LastPersistedContent = assistantMsg.Content,
                    LastPersistedProgressText = assistantMsg.ProgressText,
                    LastPersistedStatusNote = assistantMsg.StatusNote,
                    LastPersistedTerminal = assistantMsg.TrackingMeta?.IsTerminal == true
                };
                _activeDirectCommandTrackers[trackedJob.JobId] = state;
                _directCommandTrackers.Add(cts);
                existing = state;
            }
        }

        ApplyTrackedJobSnapshotToMessage(existing, assistantMsg);

        if (!existing.IsTerminal && !existing.TrackingLoopStarted && existing.Cancellation is { IsCancellationRequested: false })
        {
            existing.TrackingLoopStarted = true;
            _ = TrackDirectCommandJobAsync(existing);
        }
    }

    private async Task RehydrateTrackedJobsForCurrentSessionAsync()
    {
        if (string.IsNullOrWhiteSpace(_sessionId) || _messages.Count == 0)
            return;

        foreach (var msg in _messages)
        {
            if (!string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                continue;
            if (msg.TrackingMeta?.IsTerminal == true)
                continue;
            if (!TryCreateTrackedJobFromMessage(msg, out var trackedJob))
                continue;

            StartDirectCommandJobTracker(trackedJob, msg, _sessionId);
            await Task.Yield();
        }
    }

    private void RebindDirectCommandTrackersForCurrentSession()
    {
        if (string.IsNullOrWhiteSpace(_sessionId) || _messages.Count == 0)
            return;

        List<ActiveDirectCommandTrackerState> states;
        lock (_directCommandTrackerGate)
        {
            states = _activeDirectCommandTrackers.Values
                .Where(x => !x.IsTerminal && string.Equals(x.SessionId, _sessionId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        foreach (var state in states)
        {
            var msg = FindTrackedJobMessageForCurrentSession(state.MessageId, state.Job.DisplayLabel);
            if (msg is null)
                continue;
            state.Message = msg;
            state.MessageId = msg.MessageId;
            ApplyTrackedJobSnapshotToMessage(state, msg);
        }
    }

    private ChatMessageItem? FindTrackedJobMessageForCurrentSession(string? messageId, string displayLabel)
    {
        if (_messages.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(messageId))
        {
            var byId = _messages.FirstOrDefault(m => string.Equals(m.MessageId, messageId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
                return byId;
        }

        if (string.IsNullOrWhiteSpace(displayLabel))
            return null;

        for (var i = _messages.Count - 1; i >= 0; i--)
        {
            var msg = _messages[i];
            if (!string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrWhiteSpace(msg.Content))
                continue;
            if (msg.Content.IndexOf(displayLabel, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            if (msg.Content.IndexOf("réindex", StringComparison.OrdinalIgnoreCase) < 0
                && msg.Content.IndexOf("reindex", StringComparison.OrdinalIgnoreCase) < 0
                && msg.Content.IndexOf("index", StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            return msg;
        }

        return null;
    }

    private void ApplyTrackedJobSnapshotToMessage(ActiveDirectCommandTrackerState state, ChatMessageItem assistantMsg)
    {
        if (!string.IsNullOrWhiteSpace(state.LastContent))
            assistantMsg.Content = state.LastContent;
        assistantMsg.ProgressText = state.LastProgressText;
        assistantMsg.StatusNote = state.LastStatusNote;
        if (!string.IsNullOrWhiteSpace(state.MessageId))
            assistantMsg.MessageId = state.MessageId;
        assistantMsg.TrackingMeta = BuildTrackedJobMeta(state.Job, state.IsTerminal);
    }

    private static ChatTrackingMeta BuildTrackedJobMeta(DirectCommandTrackedJob trackedJob, bool isTerminal)
        => new()
        {
            Kind = "admin.ingestion.reindex",
            JobId = trackedJob.JobId,
            JobType = string.IsNullOrWhiteSpace(trackedJob.JobType) ? "ingestion" : trackedJob.JobType,
            DisplayLabel = trackedJob.DisplayLabel,
            DocId = trackedJob.DocId,
            DocPath = trackedJob.DocPath,
            IsTerminal = isTerminal
        };

    private static bool TryCreateTrackedJobFromMessage(ChatMessageItem message, out DirectCommandTrackedJob trackedJob)
    {
        trackedJob = null!;
        var meta = message.TrackingMeta;
        if (meta is null)
            return false;
        if (!string.Equals(meta.Kind, "admin.ingestion.reindex", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrWhiteSpace(meta.JobId))
            return false;

        trackedJob = new DirectCommandTrackedJob
        {
            JobId = meta.JobId,
            JobType = string.IsNullOrWhiteSpace(meta.JobType) ? "ingestion" : meta.JobType,
            DisplayLabel = string.IsNullOrWhiteSpace(meta.DisplayLabel) ? (message.Content ?? string.Empty) : meta.DisplayLabel!,
            Status = message.StatusNote,
            DocId = meta.DocId,
            DocPath = meta.DocPath
        };
        return true;
    }
    private async Task<JsonElement> TryLoadTrackedAdminJobSnapshotAsync(string jobId, CancellationToken ct)
    {
        JsonElement direct = default;
        var hasDirect = false;

        try
        {
            direct = await _api.AdminJobGetAsync(jobId, ct).ConfigureAwait(false);
            hasDirect = true;
            if (IsTrackedJobSnapshotInformative(direct))
                return direct;
        }
        catch
        {
        }

        try
        {
            var listed = await _api.AdminJobsListAsync("ingestion", 100, 0, ct).ConfigureAwait(false);
            if (listed.ValueKind == JsonValueKind.Object && listed.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var id = TryGetString(item, "jobId") ?? TryGetString(item, "JobId");
                    if (!string.Equals(id, jobId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!hasDirect)
                        return item.Clone();

                    return ChooseBetterTrackedJobSnapshot(direct, item.Clone());
                }
            }
        }
        catch
        {
            if (!hasDirect)
                throw;
        }

        if (hasDirect)
            return direct;

        throw new InvalidOperationException($"Unable to load admin job snapshot for {jobId}.");
    }

    private static bool IsAdminTrackingAccessUnavailable(HttpRequestException ex)
        => ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private async Task<bool> TryReconcileTrackedJobWithoutAdminAsync(ActiveDirectCommandTrackerState state, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(state.Job.DocId))
            return false;

        try
        {
            var isIndexed = await _api.DocumentsCatalogIsIndexedAsync(state.Job.DocId, ct).ConfigureAwait(false);
            if (!isIndexed)
                return false;

            state.LastContent = DeterministicAgentText.AdminReindexCompleted(UiLang, state.Job.DisplayLabel);
            state.LastProgressText = null;
            state.LastStatusNote = null;
            state.IsTerminal = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static JsonElement ChooseBetterTrackedJobSnapshot(JsonElement direct, JsonElement fallback)
    {
        var directScore = GetTrackedJobSnapshotScore(direct);
        var fallbackScore = GetTrackedJobSnapshotScore(fallback);
        return fallbackScore > directScore ? fallback : direct;
    }

    private static bool IsTrackedJobSnapshotInformative(JsonElement snapshot)
        => GetTrackedJobSnapshotScore(snapshot) >= 3;

    private static int GetTrackedJobSnapshotScore(JsonElement snapshot)
    {
        var score = 0;
        var status = (TryGetString(snapshot, "status") ?? TryGetString(snapshot, "Status") ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(status)) score += 1;
        if (!string.IsNullOrWhiteSpace(TryGetString(snapshot, "progressPhase") ?? TryGetString(snapshot, "ProgressPhase"))) score += 2;
        if (TryGetInt(snapshot, "progressCurrent").HasValue || TryGetInt(snapshot, "ProgressCurrent").HasValue) score += 2;
        if (TryGetInt(snapshot, "progressTotal").HasValue || TryGetInt(snapshot, "ProgressTotal").HasValue) score += 2;
        if (TryGetInt(snapshot, "progressPercent").HasValue || TryGetInt(snapshot, "ProgressPercent").HasValue) score += 3;
        if (TryGetDateTimeOffset(snapshot, "StartedAt").HasValue || TryGetDateTimeOffset(snapshot, "startedAt").HasValue) score += 1;
        return score;
    }

    private async Task TrackDirectCommandJobAsync(ActiveDirectCommandTrackerState state)
    {
        var ct = state.Cancellation.Token;
        var startedAtUtc = state.StartedAtUtc ?? DateTimeOffset.UtcNow;
        state.StartedAtUtc = startedAtUtc;
        var hadTrackingError = false;

        try
        {
            while (!ct.IsCancellationRequested && !state.IsTerminal)
            {
                JsonElement snapshot;
                try
                {
                    if (!_api.HasAdminSessionKey)
                    {
                        if (await TryReconcileTrackedJobWithoutAdminAsync(state, ct).ConfigureAwait(false))
                        {
                            await ApplyTrackedJobStateToUiAndPersistAsync(state, ct).ConfigureAwait(false);
                            break;
                        }

                        state.LastStatusNote = ClientUiText.Get("admin.tracking.reconnect_required", UiLang);
                        await ApplyTrackedJobStateToUiAndPersistAsync(state, ct).ConfigureAwait(false);
                        await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                        continue;
                    }

                    snapshot = await TryLoadTrackedAdminJobSnapshotAsync(state.Job.JobId, ct).ConfigureAwait(false);
                    hadTrackingError = false;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (HttpRequestException ex) when (IsAdminTrackingAccessUnavailable(ex))
                {
                    _api.ClearAdminSessionKey();

                    if (await TryReconcileTrackedJobWithoutAdminAsync(state, ct).ConfigureAwait(false))
                    {
                        await ApplyTrackedJobStateToUiAndPersistAsync(state, ct).ConfigureAwait(false);
                        break;
                    }

                    state.LastStatusNote = ClientUiText.Get("admin.tracking.reconnect_required", UiLang);
                    await ApplyTrackedJobStateToUiAndPersistAsync(state, ct).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                    continue;
                }
                catch
                {
                    hadTrackingError = true;
                    var fallbackElapsed = Math.Max(1, (int)Math.Round((DateTimeOffset.UtcNow - startedAtUtc).TotalSeconds));
                    state.LastContent ??= DeterministicAgentText.AdminReindexRunning(UiLang, state.Job.DisplayLabel);
                    state.LastProgressText ??= DeterministicAgentText.AdminReindexProgressPhase(UiLang, "running", null, null, null, fallbackElapsed);
                    state.LastStatusNote = ClientUiText.Get("help.loading", UiLang);

                    await ApplyTrackedJobStateToUiAndPersistAsync(state, ct).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                    continue;
                }

                var snapshotStartedAt = TryGetDateTimeOffset(snapshot, "StartedAt")
                    ?? TryGetDateTimeOffset(snapshot, "startedAt")
                    ?? TryGetDateTimeOffset(snapshot, "CreatedAt")
                    ?? TryGetDateTimeOffset(snapshot, "createdAt");
                if (snapshotStartedAt.HasValue)
                {
                    state.StartedAtUtc = snapshotStartedAt.Value;
                    startedAtUtc = snapshotStartedAt.Value;
                }

                var status = (TryGetString(snapshot, "status") ?? TryGetString(snapshot, "Status") ?? state.Job.Status ?? "running").Trim().ToLowerInvariant();
                var lastError = TryGetString(snapshot, "lastError") ?? TryGetString(snapshot, "LastError");
                var progressPhase = TryGetString(snapshot, "progressPhase") ?? TryGetString(snapshot, "ProgressPhase");
                var progressCurrent = TryGetInt(snapshot, "progressCurrent") ?? TryGetInt(snapshot, "ProgressCurrent");
                var progressTotal = TryGetInt(snapshot, "progressTotal") ?? TryGetInt(snapshot, "ProgressTotal");
                var progressPercent = TryGetInt(snapshot, "progressPercent") ?? TryGetInt(snapshot, "ProgressPercent");
                if (!progressPercent.HasValue && progressCurrent.HasValue && progressTotal.HasValue && progressTotal.Value > 0)
                    progressPercent = Math.Clamp((int)Math.Round((progressCurrent.Value * 100d) / progressTotal.Value, MidpointRounding.AwayFromZero), 0, 100);
                var displayPercent = GetTrackedJobDisplayPercent(status, progressPhase, progressPercent, progressCurrent, progressTotal);
                var elapsedSeconds = Math.Max(1, (int)Math.Round((DateTimeOffset.UtcNow - startedAtUtc).TotalSeconds));

                if (status is "done" or "completed" or "succeeded" or "success")
                {
                    state.LastContent = DeterministicAgentText.AdminReindexCompleted(UiLang, state.Job.DisplayLabel);
                    state.LastProgressText = null;
                    state.LastStatusNote = null;
                    state.IsTerminal = true;
                }
                else if (status is "failed" or "error" or "canceled" or "cancelled")
                {
                    state.LastContent = DeterministicAgentText.AdminReindexFailed(UiLang, state.Job.DisplayLabel, lastError);
                    state.LastProgressText = null;
                    state.LastStatusNote = null;
                    state.IsTerminal = true;
                }
                else if (status == "queued")
                {
                    state.LastContent = DeterministicAgentText.AdminReindexQueued(UiLang, state.Job.DisplayLabel, state.Job.JobId);
                    state.LastProgressText = DeterministicAgentText.AdminJobQueued(UiLang, state.Job.DisplayLabel, elapsedSeconds);
                    state.LastStatusNote = hadTrackingError ? ClientUiText.Get("help.loading", UiLang) : null;
                }
                else
                {
                    state.LastContent = displayPercent.HasValue
                        ? DeterministicAgentText.AdminReindexRunningWithPercent(UiLang, state.Job.DisplayLabel, displayPercent.Value)
                        : DeterministicAgentText.AdminReindexRunning(UiLang, state.Job.DisplayLabel);
                    state.LastProgressText = DeterministicAgentText.AdminReindexProgressPhase(UiLang, progressPhase, displayPercent, progressCurrent, progressTotal, elapsedSeconds);
                    state.LastStatusNote = hadTrackingError ? ClientUiText.Get("help.loading", UiLang) : null;
                }

                await ApplyTrackedJobStateToUiAndPersistAsync(state, ct).ConfigureAwait(false);

                if (state.IsTerminal)
                    break;

                await Task.Delay(TimeSpan.FromSeconds(status == "queued" ? 1.5 : 2.5), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_directCommandTrackerGate)
            {
                state.TrackingLoopStarted = false;
                _directCommandTrackers.Remove(state.Cancellation);
                if (state.IsTerminal)
                    _activeDirectCommandTrackers.Remove(state.Job.JobId);
            }
            state.Cancellation.Dispose();
        }
    }

    private async Task ApplyTrackedJobStateToUiAndPersistAsync(ActiveDirectCommandTrackerState state, CancellationToken ct)
    {
        await RunOnUiThreadAsync(() =>
        {
            var msg = state.Message;
            if (msg is null && string.Equals(_sessionId, state.SessionId, StringComparison.OrdinalIgnoreCase))
            {
                msg = FindTrackedJobMessageForCurrentSession(state.MessageId, state.Job.DisplayLabel);
                state.Message = msg;
            }
            if (msg is not null)
            {
                ApplyTrackedJobSnapshotToMessage(state, msg);
                if (_autoFollow && !_userScrolledUp)
                    ScrollToBottom(force: false);
            }
        }).ConfigureAwait(false);

        await MaybePersistTrackedJobSnapshotAsync(state, ct).ConfigureAwait(false);
    }

    private async Task MaybePersistTrackedJobSnapshotAsync(ActiveDirectCommandTrackerState state, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(state.MessageId))
            return;

        var content = state.LastContent;
        var statusNote = state.LastStatusNote;
        var progressText = state.LastProgressText;
        var now = DateTimeOffset.UtcNow;

        var changed = !string.Equals(state.LastPersistedContent, content, StringComparison.Ordinal)
            || !string.Equals(state.LastPersistedStatusNote, statusNote, StringComparison.Ordinal)
            || !string.Equals(state.LastPersistedProgressText, progressText, StringComparison.Ordinal)
            || state.LastPersistedTerminal != state.IsTerminal;

        if (!changed && !state.IsTerminal && (now - state.LastPersistedAtUtc) < TimeSpan.FromSeconds(5))
            return;

        var meta = BuildTrackedJobMeta(state.Job, state.IsTerminal);
        try
        {
            await _api.PatchMessageAsync(state.MessageId!, content, statusNote, progressText, meta, ct).ConfigureAwait(false);
            state.LastPersistedAtUtc = now;
            state.LastPersistedContent = content;
            state.LastPersistedStatusNote = statusNote;
            state.LastPersistedProgressText = progressText;
            state.LastPersistedTerminal = state.IsTerminal;
        }
        catch
        {
        }
    }

    private static int? GetTrackedJobDisplayPercent(string status, string? progressPhase, int? progressPercent, int? progressCurrent, int? progressTotal)
    {
        if (progressPercent.HasValue)
            return Math.Clamp(progressPercent.Value, 0, 100);

        if (progressCurrent.HasValue && progressTotal.HasValue && progressTotal.Value > 0)
            return Math.Clamp((int)Math.Round((progressCurrent.Value * 100d) / progressTotal.Value, MidpointRounding.AwayFromZero), 0, 100);

        var phase = (progressPhase ?? string.Empty).Trim().ToLowerInvariant();
        if (status == "queued")
            return 0;

        return phase switch
        {
            "preparing" => 1,
            "extracting" => 3,
            "deleting" => 97,
            "finalizing" => 99,
            _ => null
        };
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;
        if (property.ValueKind != JsonValueKind.String)
            return null;
        return DateTimeOffset.TryParse(property.GetString(), out var value) ? value : null;
    }

    private async Task PersistTrackedJobTerminalSnapshotAsync(ChatMessageItem message, ActiveDirectCommandTrackerState state, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(message.MessageId))
            state.MessageId = message.MessageId;
        state.Message = message;
        state.IsTerminal = true;
        await MaybePersistTrackedJobSnapshotAsync(state, ct).ConfigureAwait(false);
    }

    private Task RunOnUiThreadAsync(Action action)
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                action();
                tcs.TrySetResult(null);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }))
        {
            tcs.TrySetCanceled();
        }

        return tcs.Task;
    }

    private static List<HelpCategory> ParseCategories(JsonElement root)
    {
        var list = new List<HelpCategory>();
        if (!root.TryGetProperty("categories", out var categories) || categories.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in categories.EnumerateArray())
        {
            var categoryRef = TryGetString(item, "categoryRef") ?? string.Empty;
            var categoryPath = TryGetString(item, "categoryPath") ?? string.Empty;
            var displayName = TryGetString(item, "canonicalName")
                ?? TryGetString(item, "displayName")
                ?? categoryPath
                ?? categoryRef
                ?? string.Empty;

            if (string.IsNullOrWhiteSpace(categoryPath))
                categoryPath = !string.IsNullOrWhiteSpace(displayName) ? displayName : categoryRef;
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = !string.IsNullOrWhiteSpace(categoryPath) ? categoryPath : categoryRef;

            var documentCount = TryGetInt(item, "documentCount") ?? TryGetInt(item, "totalDocuments") ?? 0;
            var aliases = ReadStringArray(item, "aliases");
            var safeCategoryRef = categoryRef ?? string.Empty;
            var safeCategoryPath = categoryPath ?? string.Empty;
            var safeDisplayName = displayName ?? string.Empty;
            var searchText = string.Join(" ", new[] { safeCategoryRef, safeCategoryPath, safeDisplayName }.Concat(aliases).Where(x => !string.IsNullOrWhiteSpace(x)));
            list.Add(new HelpCategory(safeCategoryRef, safeCategoryPath, safeDisplayName, documentCount, searchText));
        }

        return list;
    }

    private static List<HelpDocument> ParseDocuments(JsonElement root)
    {
        var list = new List<HelpDocument>();
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in items.EnumerateArray())
        {
            var docId = TryGetString(item, "docId") ?? TryGetString(item, "id");
            var docPath = TryGetString(item, "docPath") ?? string.Empty;
            var docName = TryGetString(item, "docName") ?? TryGetString(item, "canonicalName") ?? docPath;
            var categoryPath = TryGetString(item, "categoryPath") ?? string.Empty;
            list.Add(new HelpDocument(docId, docPath, docName, categoryPath));
        }

        return list;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return null;
        return property.GetString();
    }

    private static int? TryGetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
            return value;
        return null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var result = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                result.Add(item.GetString()!);
        }

        return result;
    }
}
