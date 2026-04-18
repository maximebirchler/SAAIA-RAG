namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow
{
    private ChatSessionItem? FindSessionById(string? sessionId)
        => string.IsNullOrWhiteSpace(sessionId)
            ? null
            : _sessions.FirstOrDefault(x => string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));

    private void ApplySessionContainerSelectionState(string? sessionId)
    {
        var light = UseLightPalette();
        foreach (var session in _sessions)
        {
            var isCurrent = !string.IsNullOrWhiteSpace(sessionId)
                && string.Equals(session.SessionId, sessionId, StringComparison.OrdinalIgnoreCase);
            session.ApplySelectionVisualState(isCurrent, light);
        }
    }

    private void SyncSessionSelectionVisual(string? preferredSessionId = null)
    {
        if (SessionsList is null)
            return;

        var target = FindSessionById(preferredSessionId ?? _sessionId);
        var targetSessionId = target?.SessionId;

        ApplySessionContainerSelectionState(targetSessionId);

        _suppressSessionSelectionChanged = true;
        try
        {
            SessionsList.SelectedItem = null;
            if (target is not null)
            {
                SessionsList.SelectedItem = target;
                TrySoftUi("SyncSessionSelectionVisual.ScrollIntoView", () => SessionsList.ScrollIntoView(target));
            }
            TrySoftUi("SyncSessionSelectionVisual.UpdateLayout.Initial", () => SessionsList.UpdateLayout());
        }
        finally
        {
            _suppressSessionSelectionChanged = false;
        }

        TrySoftUi("SyncSessionSelectionVisual.DispatcherQueue", () =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                TrySoftUi("SyncSessionSelectionVisual.DispatcherQueue.ApplySelectionState", () => ApplySessionContainerSelectionState(targetSessionId));
                TrySoftUi("SyncSessionSelectionVisual.DispatcherQueue.SelectedItem", () =>
                {
                    if (target is not null)
                        SessionsList.SelectedItem = target;
                });
                TrySoftUi("SyncSessionSelectionVisual.UpdateLayout.Deferred", () => SessionsList.UpdateLayout());
            });
        });
    }

    private string GetDefaultSessionTitle()
        => ClientUiText.Get("chat.default_title", _appSettings.UiLanguage);

    private static bool IsDefaultSessionTitle(string? title)
    {
        var value = (title ?? string.Empty).Trim();
        if (value.Length == 0)
            return true;

        foreach (var language in ClientUiText.SupportedLanguageCodes())
        {
            if (string.Equals(value, ClientUiText.Get("chat.default_title", language), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return string.Equals(value, "New chat", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyLocalizedDefaultSessionTitles()
    {
        var localizedDefault = GetDefaultSessionTitle();
        foreach (var session in _sessions)
        {
            if (IsDefaultSessionTitle(session.Title))
                session.Title = localizedDefault;
        }
    }

    private void SessionMenu_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout flyout)
            return;

        if (flyout.Items.Count > 0 && flyout.Items[0] is MenuFlyoutItem rename)
            rename.Text = ClientUiText.Get("session.menu.rename", _appSettings.UiLanguage);
        if (flyout.Items.Count > 1 && flyout.Items[1] is MenuFlyoutItem delete)
            delete.Text = ClientUiText.Get("session.menu.delete", _appSettings.UiLanguage);
    }

    private async Task LoadSessionAsync(ChatSessionItem session, CancellationToken ct)
    {
        if (_isLoadingSession) return;

        _isLoadingSession = true;
        try
        {
            _sessionId = session.SessionId;
            SaveSettings();
            SyncSessionSelectionVisual(_sessionId);
            try
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    try { SyncSessionSelectionVisual(_sessionId); } catch { }
                });
            }
            catch { }

            Status(ClientUiText.Get("status.loading_chat", _appSettings.UiLanguage));

            _agent?.ResetConversationState();
            ClearStagedOutboundMessage();
            _messages.Clear();
            var msgs = await _api.ListMessagesAsync(_sessionId!, ct);
            await PreRefreshTrackedMessagesAsync(msgs, _sessionId, ct);
            foreach (var m in msgs) _messages.Add(m);
            await RehydrateTrackedJobsForCurrentSessionAsync(refreshBeforeLoop: false);
            RebindDirectCommandTrackersForCurrentSession();

            // reset
            SourcesCards.Items = new List<SourceCard>();
            SourcesBox.Text = "";

            // ✅ toujours reset auto-follow au chargement
            _autoFollow = true;
            _userScrolledUp = false;
            UpdateJumpButton();

            if (_messages.Count > 0)
            {
                var lastWithSources = _messages.LastOrDefault(m => !string.IsNullOrWhiteSpace(m.SourcesJson));
                MessagesList.SelectedItem = lastWithSources ?? _messages[^1];
                ScrollToBottom(force: true);
            }
            else
            {
                ScrollToBottom(force: true);
            }


            Status(ClientUiText.Format("status.loaded_session", _appSettings.UiLanguage, _sessionId ?? string.Empty));
            SyncSessionSelectionVisual(_sessionId);
            UpdateUiState(_isGenerating);
        }
        finally
        {
            _isLoadingSession = false;
        }
    }


    private async void SessionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSessionSelectionChanged) return;
        if (_isLoadingSession) return;
        if (_isGenerating) return;

        if (SessionsList.SelectedItem is ChatSessionItem sel)
        {
            if (!string.Equals(_sessionId, sel.SessionId, StringComparison.OrdinalIgnoreCase))
            {
                _sessionId = sel.SessionId;
                SyncSessionSelectionVisual(sel.SessionId);
                await LoadSessionAsync(sel, CancellationToken.None);
            }
        }
    }

    private async void NewChat_Click(object sender, RoutedEventArgs e)
    {
        if (_agent is null)
        {
            Status(ClientUiText.Get("status.connect_first", _appSettings.UiLanguage));
            return;
        }

        if (_isGenerating) return;

        try
        {
            Status(ClientUiText.Get("status.creating_chat", _appSettings.UiLanguage));

            var res = await _api.CreateSessionAsync(GetDefaultSessionTitle(), Environment.UserName, CancellationToken.None);

            // refresh pour ordre correct (backend ORDER BY updated_at desc)
            await RefreshSessionsAsync(preferSessionId: res.SessionId, CancellationToken.None);

            var selectedNewSession = FindSessionById(res.SessionId ?? _sessionId);
            if (selectedNewSession is not null)
                await LoadSessionAsync(selectedNewSession, CancellationToken.None);

            Status(ClientUiText.Format("status.new_session", _appSettings.UiLanguage, _sessionId ?? string.Empty));
        }
        catch (Exception ex)
        {
            Status(ClientUiText.Get("status.new_chat_failed", _appSettings.UiLanguage) + ex.Message);
        }
        finally
        {
            UpdateUiState(isGenerating: false);
        }
    }

    private async void SessionRename_Click(object sender, RoutedEventArgs e)
    {
        if (_isGenerating) return;

        if (sender is not MenuFlyoutItem mi) return;
        if (mi.CommandParameter is not ChatSessionItem s) return;

        try
        {
            var xamlRoot = (Content as FrameworkElement)?.XamlRoot;
            if (xamlRoot is null) return;

            var lang = _appSettings.UiLanguage;
            var box = new TextBox
            {
                Text = s.DisplayTitle,
                PlaceholderText = ClientUiText.Get("session.rename.placeholder", lang),
                MaxLength = 120,
                MinWidth = 340,
                TextWrapping = TextWrapping.NoWrap
            };

            var saveButton = BuildDialogFooterButton(ClientUiText.Get("session.rename.save", lang), primary: true);
            var closeButton = BuildDialogFooterButton(ClientUiText.Get("dialog.close", lang));
            var confirmed = false;

            OverlayDialogSession? overlay = null;
            saveButton.Click += (_, __) =>
            {
                confirmed = true;
                overlay?.Close();
            };
            closeButton.Click += (_, __) => overlay?.Close();

            overlay = ShowOverlayDialog(BuildCompactDialogShell(
                ClientUiText.Get("panel.chats", lang),
                ClientUiText.Get("session.rename.title", lang),
                null,
                new UIElement[]
                {
                    BuildDialogTextBoxCard(box)
                },
                BuildDialogFooter(saveButton, closeButton)));

            await overlay.Completion;
            if (!confirmed) return;

            var newTitle = (box.Text ?? string.Empty).Trim();
            if (newTitle.Length == 0) newTitle = GetDefaultSessionTitle();
            if (newTitle.Length > 120) newTitle = newTitle[..120];

            await _api.UpdateSessionTitleAsync(s.SessionId, newTitle, CancellationToken.None);

            await RefreshSessionsAsync(preferSessionId: s.SessionId, CancellationToken.None);
            SyncSessionSelectionVisual(s.SessionId);
            Status(ClientUiText.Get("session.rename.done", lang));
        }
        catch (Exception ex)
        {
            Status(ClientUiText.Get("session.rename.failed", _appSettings.UiLanguage) + ex.Message);
        }
    }

    private async void SessionDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_isGenerating) return;

        if (sender is not MenuFlyoutItem mi) return;
        if (mi.CommandParameter is not ChatSessionItem s) return;

        try
        {
            var xamlRoot = (Content as FrameworkElement)?.XamlRoot;
            if (xamlRoot is null) return;

            var lang = _appSettings.UiLanguage;
            var deleteButton = BuildDialogFooterButton(ClientUiText.Get("session.delete.confirm_button", lang), destructive: true);
            var closeButton = BuildDialogFooterButton(ClientUiText.Get("dialog.close", lang));
            var confirmed = false;

            OverlayDialogSession? overlay = null;
            deleteButton.Click += (_, __) =>
            {
                confirmed = true;
                overlay?.Close();
            };
            closeButton.Click += (_, __) => overlay?.Close();

            overlay = ShowOverlayDialog(BuildCompactDialogShell(
                ClientUiText.Get("panel.chats", lang),
                ClientUiText.Get("session.delete.title", lang),
                null,
                new UIElement[]
                {
                    BuildDialogNameChip(s.DisplayTitle)
                },
                BuildDialogFooter(deleteButton, closeButton)));

            await overlay.Completion;
            if (!confirmed) return;

            await _api.DeleteSessionAsync(s.SessionId, CancellationToken.None);

            var next = _sessions.FirstOrDefault(x => !string.Equals(x.SessionId, s.SessionId, StringComparison.OrdinalIgnoreCase));
            await RefreshSessionsAsync(preferSessionId: next?.SessionId, CancellationToken.None);
            var selectedAfterDelete = FindSessionById(next?.SessionId ?? _sessionId);
            SyncSessionSelectionVisual(selectedAfterDelete?.SessionId ?? _sessionId);

            if (selectedAfterDelete is not null)
                await LoadSessionAsync(selectedAfterDelete, CancellationToken.None);

            Status(ClientUiText.Get("session.delete.done", lang));
        }
        catch (Exception ex)
        {
            Status(ClientUiText.Get("session.delete.failed", _appSettings.UiLanguage) + ex.Message);
        }
    }

    private void ChatsToggle_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _chatsCollapsed = !_chatsCollapsed;

            ChatsCol.Width = _chatsCollapsed ? new GridLength(0) : new GridLength(280);
            ChatsPanel.Visibility = _chatsCollapsed ? Visibility.Collapsed : Visibility.Visible;

            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    MessagesPanelBorder?.UpdateLayout();
                    UpdateMessagesClip();
                }
                catch
                {
                    // non bloquant
                }
            });
        }
        catch
        {
            // non bloquant
        }
    }

}
