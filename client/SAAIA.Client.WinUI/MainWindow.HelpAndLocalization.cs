using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

using SAAIA.Client.WinUI.Services;

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
    private sealed record HelpDocument(string DocPath, string DocName, string CategoryPath);

    private string UiLang => ClientUiText.NormalizeLanguage(_appSettings.UiLanguage);

    private void ApplyUiLanguage()
    {
        var lang = UiLang;

        try { ToolTipService.SetToolTip(ChatsToggleButton, ClientUiText.Get("header.chats", lang)); } catch { }
        try { ToolTipService.SetToolTip(HeaderHelpButton, ClientUiText.Get("header.help", lang)); } catch { }
        try { ToolTipService.SetToolTip(HeaderSettingsButton, ClientUiText.Get("header.settings", lang)); } catch { }
        try { ChatsHeaderText.Text = ClientUiText.Get("panel.chats", lang); } catch { }
        try { NewChatButton.Content = ClientUiText.Get("button.new", lang); } catch { }
        try { JumpBottomButton.Content = ClientUiText.Get("button.jump_bottom", lang); } catch { }
        try { TypingText.Text = ClientUiText.Get("typing", lang); } catch { }
        try { InputBox.PlaceholderText = ClientUiText.Get("input.placeholder", lang); } catch { }
        try { ConnectButton.Content = ClientUiText.Get("button.connect", lang); } catch { }
        try { SetupButton.Content = ClientUiText.Get("button.setup", lang); } catch { }
        try { UserSettingsButton.Content = ClientUiText.Get("header.settings", lang); } catch { }
        try { ApplyLocalizedDefaultSessionTitles(); } catch { }

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
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));

            JsonElement? capabilities = null;
            JsonElement? snapshot = null;

            try { capabilities = await _api.AuthCapabilitiesAsync(cts.Token).ConfigureAwait(true); } catch { }
            try { snapshot = await _api.CatalogSnapshotAsync(cts.Token).ConfigureAwait(true); } catch { }

            var directCommands = capabilities.HasValue ? ParseCommandIds(capabilities.Value, "directCommands") : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var adminCommands = capabilities.HasValue ? ParseCommandIds(capabilities.Value, "adminCommands") : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var categories = snapshot.HasValue ? ParseCategories(snapshot.Value) : new List<HelpCategory>();

            var canRun = !_isGenerating && _agent is not null && !string.IsNullOrWhiteSpace(_sessionId);
            var canRescan = HasAny(adminCommands, "admin.catalog.rescan", "admin.catalog.rescan_now");
            var canReindex = HasAny(adminCommands, "admin.ingestion.reindexDocument", "admin.ingestion.reindex");
            var canSummaryStatus = adminCommands.Contains("catalog.summaries.status");
            var canCategories = directCommands.Contains("catalog.categories.list") || directCommands.Count == 0;
            var canCatalogStats = directCommands.Contains("catalog.stats.view") || directCommands.Count == 0;
            var canSearchDocuments = directCommands.Contains("catalog.documents.listAll") || directCommands.Contains("catalog.documents.listByCategory") || directCommands.Count == 0;
            var canCategoryScoped = directCommands.Contains("catalog.documents.listByCategory") || directCommands.Contains("catalog.stats.view") || directCommands.Count == 0;

            if (_activeHelpDialog is not null)
            {
                try
                {
                    _activeHelpDialog.Hide();
                }
                catch
                {
                }
                _activeHelpDialog = null;
            }

            var dlg = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = ClientUiText.Get("help.title", lang),
                CloseButtonText = ClientUiText.Get("dialog.close", lang),
                DefaultButton = ContentDialogButton.Close
            };
            _activeHelpDialog = dlg;
            dlg.Closed += (_, __) =>
            {
                if (ReferenceEquals(_activeHelpDialog, dlg))
                    _activeHelpDialog = null;
            };

            var root = new StackPanel { Spacing = 12 };
            root.Children.Add(new TextBlock
            {
                Text = !canRun
                    ? (_isGenerating ? ClientUiText.Get("help.subtitle.busy", lang) : ClientUiText.Get("help.subtitle.disconnected", lang))
                    : ClientUiText.Get("help.subtitle.ready", lang),
                Opacity = 0.82,
                TextWrapping = TextWrapping.WrapWholeWords
            });

            if (canCategories || canCatalogStats || canSummaryStatus || canRescan)
            {
                root.Children.Add(BuildSectionHeader(ClientUiText.Get("help.section.quick", lang)));

                if (canCategories)
                {
                    root.Children.Add(CreateActionButton(
                        ClientUiText.Get("cmd.catalog.categories", lang),
                        null,
                        canRun,
                        async () =>
                        {
                            dlg.Hide();
                            await TrySendHelpPromptAsync(ClientUiText.BuildPromptCategories(lang)).ConfigureAwait(true);
                        }));
                }

                if (canCatalogStats)
                {
                    root.Children.Add(CreateActionButton(
                        ClientUiText.Get("cmd.catalog.stats", lang),
                        null,
                        canRun,
                        async () =>
                        {
                            dlg.Hide();
                            await TrySendHelpPromptAsync(ClientUiText.BuildPromptCatalogStats(lang)).ConfigureAwait(true);
                        }));
                }

                if (canSummaryStatus)
                {
                    root.Children.Add(CreateActionButton(
                        ClientUiText.Get("cmd.summary.missing.count", lang),
                        null,
                        canRun,
                        async () =>
                        {
                            dlg.Hide();
                            await TrySendHelpPromptAsync(ClientUiText.BuildPromptSummaryMissingCount(lang)).ConfigureAwait(true);
                        }));

                    root.Children.Add(CreateActionButton(
                        ClientUiText.Get("cmd.summary.missing.list", lang),
                        null,
                        canRun,
                        async () =>
                        {
                            dlg.Hide();
                            await TrySendHelpPromptAsync(ClientUiText.BuildPromptSummaryMissingList(lang)).ConfigureAwait(true);
                        }));

                    root.Children.Add(CreateActionButton(
                        ClientUiText.Get("cmd.summary.present.count", lang),
                        null,
                        canRun,
                        async () =>
                        {
                            dlg.Hide();
                            await TrySendHelpPromptAsync(ClientUiText.BuildPromptSummaryPresentCount(lang)).ConfigureAwait(true);
                        }));

                    root.Children.Add(CreateActionButton(
                        ClientUiText.Get("cmd.summary.present.list", lang),
                        null,
                        canRun,
                        async () =>
                        {
                            dlg.Hide();
                            await TrySendHelpPromptAsync(ClientUiText.BuildPromptSummaryPresentList(lang)).ConfigureAwait(true);
                        }));
                }

                if (canRescan)
                {
                    root.Children.Add(CreateActionButton(
                        ClientUiText.Get("cmd.admin.rescan", lang),
                        null,
                        canRun,
                        async () =>
                        {
                            dlg.Hide();
                            await TrySendHelpPromptAsync(ClientUiText.BuildPromptAdminRescan(lang)).ConfigureAwait(true);
                        }));
                }
            }

            if (canSearchDocuments || canCategoryScoped || canReindex)
            {
                root.Children.Add(BuildSectionHeader(ClientUiText.Get("help.section.guided", lang)));

                var guidedButtons = new StackPanel { Spacing = 8 };
                var modeDescription = new TextBlock
                {
                    Text = ClientUiText.Get("help.mode.none", lang),
                    Opacity = 0.84,
                    TextWrapping = TextWrapping.WrapWholeWords
                };
                var searchBox = new TextBox { Visibility = Visibility.Collapsed };
                var searchButton = new Button
                {
                    Content = ClientUiText.Get("button.search", lang),
                    Visibility = Visibility.Collapsed,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    MinWidth = 120
                };
                var helperText = new TextBlock
                {
                    Opacity = 0.9,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.WrapWholeWords
                };
                var resultsPanel = new StackPanel { Spacing = 8 };

                HelpGuidedMode currentMode = HelpGuidedMode.None;

                void RenderCategoryResultsAsync()
                {
                    resultsPanel.Children.Clear();
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

                                dlg.Hide();
                                await TrySendHelpPromptAsync(prompt).ConfigureAwait(true);
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
                        var json = await _api.DocumentsListAsync(categoryPath: null, categoryRef: null, q: query, limit: 10, offset: 0, ct: CancellationToken.None).ConfigureAwait(true);
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
                            var docRef = string.IsNullOrWhiteSpace(doc.DocPath) ? docTitle : doc.DocPath;
                            var subtitle = string.IsNullOrWhiteSpace(doc.CategoryPath) ? docRef : $"{doc.CategoryPath} • {docRef}";
                            var prompt = currentMode == HelpGuidedMode.ReindexDocument
                                ? ClientUiText.BuildPromptAdminReindex(lang, docTitle)
                                : ClientUiText.BuildPromptSearchDocuments(lang, docTitle);
                            var displayPrompt = currentMode == HelpGuidedMode.ReindexDocument
                                ? ClientUiText.BuildPromptAdminReindexDisplay(lang, docTitle)
                                : prompt;

                            resultsPanel.Children.Add(CreateActionButton(
                                docTitle,
                                subtitle,
                                canRun,
                                async () =>
                                {
                                    dlg.Hide();
                                    await TrySendHelpPromptAsync(prompt, displayPrompt).ConfigureAwait(true);
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

                void ActivateMode(HelpGuidedMode mode)
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
                            helperText.Text = ClientUiText.Get("help.search.hint.category", lang)
                                + Environment.NewLine
                                + ClientUiText.Get("help.search.next_step.category", lang);
                            RenderCategoryResultsAsync();
                            searchBox.Focus(FocusState.Programmatic);
                            searchBox.StartBringIntoView();
                            break;

                        case HelpGuidedMode.CategoryStats:
                            modeDescription.Text = ClientUiText.Get("help.mode.category.stats", lang);
                            searchBox.Visibility = Visibility.Visible;
                            searchButton.Visibility = Visibility.Collapsed;
                            searchBox.PlaceholderText = ClientUiText.Get("help.search.placeholder.category", lang);
                            helperText.Text = ClientUiText.Get("help.search.hint.category", lang)
                                + Environment.NewLine
                                + ClientUiText.Get("help.search.next_step.category", lang);
                            RenderCategoryResultsAsync();
                            searchBox.Focus(FocusState.Programmatic);
                            searchBox.StartBringIntoView();
                            break;

                        case HelpGuidedMode.DocumentSearch:
                            modeDescription.Text = ClientUiText.Get("help.mode.document.search", lang);
                            searchBox.Visibility = Visibility.Visible;
                            searchButton.Visibility = Visibility.Visible;
                            searchBox.PlaceholderText = ClientUiText.Get("help.search.placeholder.document", lang);
                            helperText.Text = ClientUiText.Get("help.search.hint.document", lang)
                                + Environment.NewLine
                                + ClientUiText.Get("help.search.next_step.document", lang);
                            searchBox.Focus(FocusState.Programmatic);
                            searchBox.StartBringIntoView();
                            resultsPanel.Children.Add(new TextBlock
                            {
                                Text = ClientUiText.Get("help.search.type_more", lang),
                                Opacity = 0.72,
                                TextWrapping = TextWrapping.WrapWholeWords
                            });
                            break;

                        case HelpGuidedMode.ReindexDocument:
                            modeDescription.Text = ClientUiText.Get("help.mode.document.reindex", lang);
                            searchBox.Visibility = Visibility.Visible;
                            searchButton.Visibility = Visibility.Visible;
                            searchBox.PlaceholderText = ClientUiText.Get("help.search.placeholder.document", lang);
                            helperText.Text = ClientUiText.Get("help.search.hint.document", lang)
                                + Environment.NewLine
                                + ClientUiText.Get("help.search.next_step.document", lang);
                            searchBox.Focus(FocusState.Programmatic);
                            searchBox.StartBringIntoView();
                            resultsPanel.Children.Add(new TextBlock
                            {
                                Text = ClientUiText.Get("help.search.type_more", lang),
                                Opacity = 0.72,
                                TextWrapping = TextWrapping.WrapWholeWords
                            });
                            break;

                        default:
                            modeDescription.Text = ClientUiText.Get("help.mode.none", lang);
                            searchBox.Visibility = Visibility.Collapsed;
                            searchButton.Visibility = Visibility.Collapsed;
                            helperText.Text = string.Empty;
                            break;
                    }
                }

                if (canSearchDocuments)
                {
                    guidedButtons.Children.Add(CreateActionButton(
                        ClientUiText.Get("cmd.guided.document_search", lang),
                        null,
                        true,
                        () =>
                        {
                            ActivateMode(HelpGuidedMode.DocumentSearch);
                            return Task.CompletedTask;
                        }));
                }

                if (canCategoryScoped)
                {
                    guidedButtons.Children.Add(CreateActionButton(
                        ClientUiText.Get("cmd.guided.documents_by_category", lang),
                        ClientUiText.Get("help.search.top_categories", lang),
                        true,
                        () =>
                        {
                            ActivateMode(HelpGuidedMode.CategoryDocuments);
                            return Task.CompletedTask;
                        }));

                    guidedButtons.Children.Add(CreateActionButton(
                        ClientUiText.Get("cmd.guided.category_stats", lang),
                        ClientUiText.Get("help.search.top_categories", lang),
                        true,
                        () =>
                        {
                            ActivateMode(HelpGuidedMode.CategoryStats);
                            return Task.CompletedTask;
                        }));
                }

                if (canReindex)
                {
                    guidedButtons.Children.Add(CreateActionButton(
                        ClientUiText.Get("cmd.guided.reindex", lang),
                        null,
                        true,
                        () =>
                        {
                            ActivateMode(HelpGuidedMode.ReindexDocument);
                            return Task.CompletedTask;
                        }));
                }

                searchBox.TextChanged += (_, __) =>
                {
                    if (currentMode is HelpGuidedMode.CategoryDocuments or HelpGuidedMode.CategoryStats)
                        RenderCategoryResultsAsync();
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

                root.Children.Add(guidedButtons);
                root.Children.Add(modeDescription);
                root.Children.Add(searchBox);
                root.Children.Add(searchButton);
                root.Children.Add(helperText);
                root.Children.Add(BuildSectionHeader(ClientUiText.Get("help.section.results", lang)));
                root.Children.Add(resultsPanel);
            }

            var dialogSize = GetDialogMaxSize(900, 760, horizontalMargin: 72, verticalMargin: 110);
            dlg.Content = new Border
            {
                MaxWidth = dialogSize.Width,
                MaxHeight = dialogSize.Height,
                Padding = new Thickness(2, 0, 2, 0),
                Child = new ScrollViewer
                {
                    Content = root,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    MaxWidth = dialogSize.Width,
                    MaxHeight = dialogSize.Height
                }
            };

            await dlg.ShowAsync();
        }
        catch (Exception ex)
        {
            Status(ClientUiText.Get("status.help_send_failed", UiLang) + ex.Message);
        }
    }

    private async Task<bool> TrySendHelpPromptAsync(string prompt, string? displayText = null)
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

        try
        {
            StageOutboundMessage(prompt, displayText);
            await SendAsync().ConfigureAwait(true);
            return true;
        }
        catch (Exception ex)
        {
            Status(ClientUiText.Get("status.help_send_failed", UiLang) + ex.Message);
            return false;
        }
    }

    private static TextBlock BuildSectionHeader(string text)
        => new()
        {
            Text = text,
            FontWeight = FontWeights.SemiBold,
            FontSize = 15,
            Margin = new Thickness(0, 6, 0, 0)
        };

    private static Button CreateActionButton(string title, string? subtitle, bool canRun, Func<Task> onClick)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            TextWrapping = TextWrapping.WrapWholeWords,
            FontWeight = FontWeights.SemiBold
        });

        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            stack.Children.Add(new TextBlock
            {
                Text = subtitle,
                TextWrapping = TextWrapping.WrapWholeWords,
                Opacity = 0.74,
                FontSize = 12
            });
        }

        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 10, 12, 10),
            IsEnabled = canRun,
            Content = stack
        };

        button.Click += async (_, __) => await onClick().ConfigureAwait(true);
        return button;
    }

    private static bool HasAny(HashSet<string> values, params string[] ids)
        => ids.Any(values.Contains);

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
            var docPath = TryGetString(item, "docPath") ?? string.Empty;
            var docName = TryGetString(item, "docName") ?? TryGetString(item, "canonicalName") ?? docPath;
            var categoryPath = TryGetString(item, "categoryPath") ?? string.Empty;
            list.Add(new HelpDocument(docPath, docName, categoryPath));
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
