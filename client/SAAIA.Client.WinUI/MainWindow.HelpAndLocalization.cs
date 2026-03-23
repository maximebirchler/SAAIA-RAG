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
            var canRun = !_isGenerating && _agent is not null && !string.IsNullOrWhiteSpace(_sessionId);
            var isAdmin = _api.HasAdminKey;
            List<HelpCategory> categories = new();
            var categoriesLoaded = false;
            var categoriesLoadStarted = false;
            Exception? categoriesLoadError = null;

            if (_activeHelpDialog is not null)
            {
                try { _activeHelpDialog.Hide(); } catch { }
                _activeHelpDialog = null;
            }

            var dlg = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = string.Empty,
                CloseButtonText = ClientUiText.Get("dialog.close", lang),
                DefaultButton = ContentDialogButton.Close
            };
            _activeHelpDialog = dlg;
            dlg.Closed += (_, __) =>
            {
                if (ReferenceEquals(_activeHelpDialog, dlg))
                    _activeHelpDialog = null;
            };

            var contentScroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            var root = new StackPanel { Spacing = 14, MaxWidth = 860 };
            var hero = CreateSectionCard(null, new UIElement[]
            {
                new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = ClientUiText.Get("help.title", lang),
                            FontSize = 22,
                            FontWeight = FontWeights.SemiBold
                        },
                        new TextBlock
                        {
                            Text = !canRun
                                ? (_isGenerating ? ClientUiText.Get("help.subtitle.busy", lang) : ClientUiText.Get("help.subtitle.disconnected", lang))
                                : ClientUiText.Get("help.subtitle.ready", lang),
                            Opacity = 0.82,
                            TextWrapping = TextWrapping.WrapWholeWords
                        }
                    }
                }
            });
            root.Children.Add(hero);

            var quickPanel = new StackPanel { Spacing = 8 };
            quickPanel.Children.Add(CreateActionButton(ClientUiText.Get("cmd.catalog.categories", lang), null, canRun, async () =>
            {
                dlg.Hide();
                await TrySendHelpPromptAsync(ClientUiText.BuildPromptCategories(lang)).ConfigureAwait(true);
            }));
            quickPanel.Children.Add(CreateActionButton(ClientUiText.Get("cmd.catalog.stats", lang), null, canRun, async () =>
            {
                dlg.Hide();
                await TrySendHelpPromptAsync(ClientUiText.BuildPromptCatalogStats(lang)).ConfigureAwait(true);
            }));
            quickPanel.Children.Add(CreateActionButton(ClientUiText.Get("cmd.catalog.tree", lang), null, canRun, async () =>
            {
                dlg.Hide();
                await TrySendHelpPromptAsync(ClientUiText.BuildPromptCatalogTree(lang)).ConfigureAwait(true);
            }));
            root.Children.Add(CreateSectionCard(ClientUiText.Get("help.section.quick", lang), new UIElement[] { quickPanel }));

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
                MinWidth = 120,
                CornerRadius = new CornerRadius(12)
            };
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
                    dlg.Hide();
                    await TrySendHelpPromptAsync(ClientUiText.BuildPromptSummaryMissingCount(lang)).ConfigureAwait(true);
                }));
                adminPanel.Children.Add(CreateActionButton(ClientUiText.Get("cmd.summary.missing.list", lang), null, canRun, async () =>
                {
                    dlg.Hide();
                    await TrySendHelpPromptAsync(ClientUiText.BuildPromptSummaryMissingList(lang)).ConfigureAwait(true);
                }));
                adminPanel.Children.Add(CreateActionButton(ClientUiText.Get("cmd.summary.present.count", lang), null, canRun, async () =>
                {
                    dlg.Hide();
                    await TrySendHelpPromptAsync(ClientUiText.BuildPromptSummaryPresentCount(lang)).ConfigureAwait(true);
                }));
                adminPanel.Children.Add(CreateActionButton(ClientUiText.Get("cmd.summary.present.list", lang), null, canRun, async () =>
                {
                    dlg.Hide();
                    await TrySendHelpPromptAsync(ClientUiText.BuildPromptSummaryPresentList(lang)).ConfigureAwait(true);
                }));
                adminPanel.Children.Add(CreateActionButton(ClientUiText.Get("cmd.admin.rescan", lang), null, canRun, async () =>
                {
                    dlg.Hide();
                    await TrySendHelpPromptAsync(ClientUiText.BuildPromptAdminRescan(lang)).ConfigureAwait(true);
                }));
                root.Children.Add(CreateSectionCard(ClientUiText.Get("help.section.admin", lang), new UIElement[] { adminPanel }));
            }

            var dialogSize = GetDialogMaxSize(900, 760, horizontalMargin: 56, verticalMargin: 88);
            contentScroller.Content = root;
            contentScroller.MaxWidth = dialogSize.Width;
            contentScroller.MaxHeight = dialogSize.Height;
            dlg.Content = new Border
            {
                MaxWidth = dialogSize.Width,
                MaxHeight = dialogSize.Height,
                Padding = new Thickness(2, 0, 2, 0),
                Child = contentScroller
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

    private static Border CreateSectionCard(string? title, IEnumerable<UIElement> body)
    {
        var stack = new StackPanel { Spacing = 10 };
        if (!string.IsNullOrWhiteSpace(title))
            stack.Children.Add(BuildSectionHeader(title));
        foreach (var child in body)
            stack.Children.Add(child);
        return new Border
        {
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(16, 16, 16, 16),
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x11, 0x11, 0x11)),
            BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x27, 0x27, 0x27)),
            BorderThickness = new Thickness(1),
            Child = stack
        };
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
        var stack = new StackPanel { Spacing = 4 };
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
            Padding = new Thickness(14, 12, 14, 12),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x22, 0x22, 0x22)),
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
