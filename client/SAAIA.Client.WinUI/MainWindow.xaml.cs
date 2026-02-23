using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

using SAAIA.Client.WinUI.Models;
using SAAIA.Client.WinUI.Services;

using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.ApplicationModel.DataTransfer;

namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly ApiClient _api = new();
    private readonly OpenAiLlmClient _llm = new();
    private AppSettings _appSettings = AppSettings.Load();
    private readonly LlamaCppProcessManager _llmProc = new();
    private RagChatAgent? _agent;

    private readonly ObservableCollection<ChatMessageItem> _messages = new();
    private readonly ObservableCollection<ChatSessionItem> _sessions = new();

    private string? _sessionId;
    private string _userId = "";
    private CancellationTokenSource? _cts;
    private bool _isGenerating;
    private bool _isLoadingSession;
    private bool _autoFollow = true;          // si true: on suit le bas pendant streaming
    private bool _userScrolledUp = false;     // si true: on ne force plus le scroll
    private DateTime _lastAutoScroll = DateTime.MinValue;
    private bool _isProgrammaticScroll = false;
    private bool _sourcesCollapsedByWidth;

    public MainWindow()
    {
        InitializeComponent();

        Root.Loaded += (_, __) => UpdateMessagesClip();

        TryResize(1400, 820);

        MessagesList.ItemsSource = _messages;
        SessionsList.ItemsSource = _sessions;

        LoadSettings();
        LoadLocalLlmUiFromSettings();
        UpdateUiState(isGenerating: false);
        Status("Ready.");
    }

    private void TryResize(int width, int height)
    {
        try { AppWindow.Resize(new SizeInt32(width, height)); }
        catch
        {
            Activated += (_, __) =>
            {
                try { AppWindow.Resize(new SizeInt32(width, height)); } catch { }
            };
        }
    }

    private void Status(string s) => StatusText.Text = s;

    private bool IsConnected => _agent is not null;

    private void UpdateUiState(bool isGenerating)
    {
        _isGenerating = isGenerating;

        SendButton.IsEnabled = IsConnected && !_isGenerating && !string.IsNullOrWhiteSpace(_sessionId);
        CancelButton.IsEnabled = IsConnected && _isGenerating;
        InputBox.IsEnabled = IsConnected && !_isGenerating && !string.IsNullOrWhiteSpace(_sessionId);

        SessionsList.IsEnabled = IsConnected && !_isGenerating;
        NewChatButton.IsEnabled = IsConnected && !_isGenerating;

        ConnectButton.IsEnabled = !_isGenerating;
    }

    private void LoadSettings()
    {
        ServerUrlBox.Text = ClientDefaults.BackendBaseUrl;

        _userId = SecureLocalStore.GetOrCreateUserId();
        ApiKeyBox.Password = SecureLocalStore.GetServerApiKey() ?? "";

        _appSettings = AppSettings.Load();
        LlmUrlBox.Text = _appSettings.UseLocalLlm ? _appSettings.LlmBaseUrl : ClientDefaults.LlmBaseUrl;
        LlmModelBox.Text = string.IsNullOrWhiteSpace(_appSettings.ModelId) ? ClientDefaults.LlmModel : _appSettings.ModelId;

        _sessionId = string.IsNullOrWhiteSpace(_appSettings.LastSessionId) ? null : _appSettings.LastSessionId;
    }

    private void SaveSettings()
    {
        SecureLocalStore.SetServerApiKey(ApiKeyBox.Password.Trim());

        // Persist last session id in AppSettings (works both packaged and unpackaged)
        _appSettings ??= AppSettings.Load();
        _appSettings.LastSessionId = _sessionId;
        _appSettings.Save();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _userId = SecureLocalStore.GetOrCreateUserId();

            _api.Configure(ClientDefaults.BackendBaseUrl, ApiKeyBox.Password, _userId);

            _appSettings = AppSettings.Load();
            var llmBaseUrl = _appSettings.UseLocalLlm ? _appSettings.LlmBaseUrl : ClientDefaults.LlmBaseUrl;
            var llmModelId = string.IsNullOrWhiteSpace(_appSettings.ModelId) ? ClientDefaults.LlmModel : _appSettings.ModelId;

            // Auto-start local llama.cpp if enabled (M6.1)
            if (_appSettings.UseLocalLlm && _appSettings.AutoStartOnConnect)
            {
                var started = await EnsureLocalLlmStartedAsync(CancellationToken.None);
                if (!started)
                    Status("Local LLM start failed. See LLM panel for details.");
            }

            _llm.Configure(llmBaseUrl, llmModelId);
            _agent = new RagChatAgent(_api, _llm);

            // reflect in UI
            LlmUrlBox.Text = llmBaseUrl;
            LlmModelBox.Text = llmModelId;

            SaveSettings();

            Status("Connecting…");

            await RefreshSessionsAsync(preferSessionId: _sessionId, CancellationToken.None);

            if (SessionsList.SelectedItem is ChatSessionItem sel)
                await LoadSessionAsync(sel, CancellationToken.None);

            Status($"Connected. Session: {_sessionId}");
            UpdateUiState(isGenerating: false);
            ApplyResponsiveLayout(Root.ActualWidth);

        }
        catch (Exception ex)
        {
            Status("Connect failed: " + ex.Message);
            UpdateUiState(isGenerating: false);
            ApplyResponsiveLayout(Root.ActualWidth);
        }
    }

    private async Task RefreshSessionsAsync(string? preferSessionId, CancellationToken ct)
    {
        var list = await _api.ListSessionsAsync(ct, limit: 200, offset: 0);

        // Si aucune session: on en crée une
        if (list.Count == 0)
        {
            var created = await _api.CreateSessionAsync("New chat", Environment.UserName, ct);
            list.Insert(0, new ChatSessionItem
            {
                SessionId = created.SessionId,
                Title = created.Title,
                ClientUser = created.ClientUser,
                CreatedAtUtc = created.CreatedAtUtc.UtcDateTime,
                UpdatedAtUtc = created.CreatedAtUtc.UtcDateTime,
                LastMessageAtUtc = null
            });
        }

        _sessions.Clear();
        foreach (var s in list) _sessions.Add(s);

        ChatSessionItem? toSelect = null;

        if (!string.IsNullOrWhiteSpace(preferSessionId))
            toSelect = _sessions.FirstOrDefault(x => string.Equals(x.SessionId, preferSessionId, StringComparison.OrdinalIgnoreCase));

        toSelect ??= _sessions.FirstOrDefault();

        if (toSelect is not null)
            SessionsList.SelectedItem = toSelect;
    }

    private async Task LoadSessionAsync(ChatSessionItem session, CancellationToken ct)
    {
        if (_isLoadingSession) return;

        _isLoadingSession = true;
        try
        {
            _sessionId = session.SessionId;
            SaveSettings();

            Status("Loading chat…");

            _messages.Clear();
            var msgs = await _api.ListMessagesAsync(_sessionId!, ct);
            foreach (var m in msgs) _messages.Add(m);

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


            Status($"Loaded. Session: {_sessionId}");
            UpdateUiState(_isGenerating);
        }
        finally
        {
            _isLoadingSession = false;
        }
    }


    private async void SessionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSession) return;
        if (_isGenerating) return;

        if (SessionsList.SelectedItem is ChatSessionItem sel)
        {
            if (!string.Equals(_sessionId, sel.SessionId, StringComparison.OrdinalIgnoreCase))
                await LoadSessionAsync(sel, CancellationToken.None);
        }
    }

    private async void NewChat_Click(object sender, RoutedEventArgs e)
    {
        if (_agent is null)
        {
            Status("Click Connect first.");
            return;
        }

        if (_isGenerating) return;

        try
        {
            Status("Creating new chat…");

            var res = await _api.CreateSessionAsync("New chat", Environment.UserName, CancellationToken.None);

            // refresh pour ordre correct (backend ORDER BY updated_at desc)
            await RefreshSessionsAsync(preferSessionId: res.SessionId, CancellationToken.None);

            if (SessionsList.SelectedItem is ChatSessionItem sel)
                await LoadSessionAsync(sel, CancellationToken.None);

            Status($"New session: {_sessionId}");
        }
        catch (Exception ex)
        {
            Status("New chat failed: " + ex.Message);
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

            var box = new TextBox
            {
                Text = s.DisplayTitle,
                PlaceholderText = "Titre…"
            };

            var dlg = new ContentDialog
            {
                Title = "Rename chat",
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                Content = box,
                XamlRoot = xamlRoot
            };

            var res = await dlg.ShowAsync();
            if (res != ContentDialogResult.Primary) return;

            var newTitle = (box.Text ?? "").Trim();
            if (newTitle.Length == 0) newTitle = "New chat";
            if (newTitle.Length > 120) newTitle = newTitle[..120];

            await _api.UpdateSessionTitleAsync(s.SessionId, newTitle, CancellationToken.None);

            await RefreshSessionsAsync(preferSessionId: s.SessionId, CancellationToken.None);
            Status("Renamed.");
        }
        catch (Exception ex)
        {
            Status("Rename failed: " + ex.Message);
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

            var dlg = new ContentDialog
            {
                Title = "Delete chat?",
                Content = $"Supprimer définitivement: \"{s.DisplayTitle}\" ?",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot
            };

            var res = await dlg.ShowAsync();
            if (res != ContentDialogResult.Primary) return;

            await _api.DeleteSessionAsync(s.SessionId, CancellationToken.None);

            var next = _sessions.FirstOrDefault(x => !string.Equals(x.SessionId, s.SessionId, StringComparison.OrdinalIgnoreCase));
            await RefreshSessionsAsync(preferSessionId: next?.SessionId, CancellationToken.None);

            if (SessionsList.SelectedItem is ChatSessionItem sel)
                await LoadSessionAsync(sel, CancellationToken.None);

            Status("Deleted.");
        }
        catch (Exception ex)
        {
            Status("Delete failed: " + ex.Message);
        }
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync();

    private async void InputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && !e.KeyStatus.IsMenuKeyDown && !e.KeyStatus.IsKeyReleased)
        {
            e.Handled = true;
            await SendAsync();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_isGenerating) return;

        Status("Cancelling…");
        CancelButton.IsEnabled = false;
        try { _cts?.Cancel(); } catch { }
    }

    private static void MarkInterrupted(ChatMessageItem? assistantMsg)
    {
        if (assistantMsg is null) return;

        if (string.IsNullOrWhiteSpace(assistantMsg.Content))
        {
            assistantMsg.Content = ""; // on laisse vide (ou tu peux mettre "…")
            assistantMsg.StatusNote = "Génération interrompue.";
            return;
        }

        if (string.IsNullOrWhiteSpace(assistantMsg.StatusNote))
            assistantMsg.StatusNote = "Génération interrompue.";
    }

    private async Task MaybeAutoTitleAsync(string userText)
    {
        // Si le titre est "New chat" (ou vide), on met un titre basé sur la 1ère question
        if (SessionsList.SelectedItem is not ChatSessionItem s) return;

        var currentTitle = (s.Title ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(currentTitle) && !string.Equals(currentTitle, "New chat", StringComparison.OrdinalIgnoreCase))
            return;

        var title = (userText ?? "").Trim();
        if (title.Length == 0) return;

        // petit nettoyage + coupe
        title = title.Replace("\r", " ").Replace("\n", " ");
        if (title.Length > 60) title = title[..60];

        try
        {
            await _api.UpdateSessionTitleAsync(s.SessionId, title, CancellationToken.None);
            await RefreshSessionsAsync(preferSessionId: s.SessionId, CancellationToken.None);
        }
        catch { /* non bloquant */ }
    }

    private void SetTyping(bool isTyping)
    {
        TypingText.Visibility = isTyping ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateJumpButton()
    {
        JumpBottomButton.Visibility = (_userScrolledUp && _messages.Count > 0) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ScrollToBottom(bool force = false)
    {
        // throttle léger pour éviter un spam de ChangeView pendant streaming
        if (!force)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastAutoScroll).TotalMilliseconds < 120) return;
            _lastAutoScroll = now;
        }

        try
        {
            _isProgrammaticScroll = true;
            MessagesScroll.ChangeView(null, MessagesScroll.ScrollableHeight, null, true);
        }
        catch { }
        finally
        {
            _isProgrammaticScroll = false;
        }

    }

    private void MessagesScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_isProgrammaticScroll) return;

        // Si le user n'est pas à ~20px du bas => il a scroll up
        var distanceFromBottom = MessagesScroll.ScrollableHeight - MessagesScroll.VerticalOffset;

        var nearBottom = distanceFromBottom < 20;
        _userScrolledUp = !nearBottom;

        // si il revient en bas manuellement, on réactive l'autofollow
        if (nearBottom)
            _autoFollow = true;

        UpdateJumpButton();
    }

    private void JumpBottom_Click(object sender, RoutedEventArgs e)
    {
        _autoFollow = true;
        _userScrolledUp = false;
        UpdateJumpButton();
        ScrollToBottom(force: true);
    }

    private void CopyAssistant_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not Button b) return;
            if (b.CommandParameter is not ChatMessageItem m) return;

            var text = (m.Content ?? "").Trim();
            if (text.Length == 0) return;

            var dp = new DataPackage();
            dp.SetText(text);
            Clipboard.SetContent(dp);

            Status("Copied.");
        }
        catch (Exception ex)
        {
            Status("Copy failed: " + ex.Message);
        }
    }

    private void ApplyResponsiveLayout(double width)
    {
        // seuils simples et efficaces
        // - >= 1200 : on montre Sources à droite
        // - < 1200 : on cache Sources pour laisser respirer le chat
        var shouldCollapseSources = width < 1200;

        if (shouldCollapseSources == _sourcesCollapsedByWidth)
            return;

        _sourcesCollapsedByWidth = shouldCollapseSources;

        if (_sourcesCollapsedByWidth)
        {
            // cache la colonne Sources
            SourcesCol.Width = new GridLength(0);
            SourcesPanel.Visibility = Visibility.Collapsed;

            // affiche bouton "Sources" en haut (ouvre un popup)
            SourcesToggleButton.Visibility = Visibility.Visible;
        }
        else
        {
            // restore
            SourcesCol.Width = new GridLength(360);
            SourcesPanel.Visibility = Visibility.Visible;

            SourcesToggleButton.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateMessagesClip()
    {
        try
        {
            if (MessagesPanelBorder is null) return;

            var w = MessagesPanelBorder.ActualWidth;
            var h = MessagesPanelBorder.ActualHeight;

            if (w <= 0 || h <= 0) return;

            MessagesPanelBorder.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, w, h)
            };
        }
        catch
        {
            // non bloquant
        }
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(e.NewSize.Width);
        UpdateMessagesClip();
    }

    private async void SourcesToggle_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var xamlRoot = (Content as FrameworkElement)?.XamlRoot;
            if (xamlRoot is null) return;

            // On prend les sources du message sélectionné.
            // Si rien n’est sélectionné, on tente le dernier message qui a des sources.
            string? json = null;

            if (MessagesList.SelectedItem is ChatMessageItem sel)
                json = sel.SourcesJson;

            if (string.IsNullOrWhiteSpace(json))
                json = _messages.LastOrDefault(m => !string.IsNullOrWhiteSpace(m.SourcesJson))?.SourcesJson;

            var cards = SourceCardParser.Parse(json);

            var ctrl = new SAAIA.Client.WinUI.Controls.SourcesCardsControl
            {
                Items = cards
            };

            var dlg = new ContentDialog
            {
                Title = "Sources",
                Content = ctrl,
                CloseButtonText = "Close",
                XamlRoot = xamlRoot
            };

            await dlg.ShowAsync();
        }
        catch
        {
            // non bloquant
        }
    }

    private void CopyMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;

        // CommandParameter="{Binding}" => ChatMessageItem
        if (b.CommandParameter is not ChatMessageItem msg) return;

        var text = msg.Content ?? "";
        if (text.Length == 0) return;

        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);

        Status("Copied.");
    }

    private void Bubble_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;

        if (fe.FindName("CopyBtn") is Button b)
        {
            b.Opacity = 1;
            b.IsHitTestVisible = true;
        }
    }

    private void Bubble_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;

        if (fe.FindName("CopyBtn") is Button b)
        {
            b.Opacity = 0;
            b.IsHitTestVisible = false;
        }
    }

    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(_sessionId))
        {
            Status("Select a chat (or New).");
            return;
        }
        if (_agent is null)
        {
            Status("Click Connect first (agent not ready).");
            return;
        }

        var text = (InputBox.Text ?? "").Trim();
        if (text.Length == 0) return;

        ChatMessageItem? assistantMsg = null;

        try
        {
            UpdateUiState(isGenerating: true);

            InputBox.Text = "";

            var tailBefore = _messages.ToList();

            var userMsg = new ChatMessageItem { Role = "user", Content = text, CreatedAt = DateTime.UtcNow };
            _messages.Add(userMsg);
            await _api.AddMessageAsync(_sessionId!, "user", text, null, CancellationToken.None);

            await MaybeAutoTitleAsync(text);

            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            assistantMsg = new ChatMessageItem
            {
                Role = "assistant",
                Content = "",
                CreatedAt = DateTime.UtcNow,
                StatusNote = null
            };
            _messages.Add(assistantMsg);
            ScrollToBottom(force: true);

            SourcesCards.Items = new List<SourceCard>();
            SourcesBox.Text = "";

            Status("Thinking…");
            
            SetTyping(true);
            _autoFollow = true;
            _userScrolledUp = false;
            UpdateJumpButton();

            var (finalAnswer, sourcesObj) = await _agent.RunAsync(
                userText: text,
                category: ClientDefaults.DefaultCategory,
                conversationTail: tailBefore,
                onDelta: token =>
                {
                   DispatcherQueue.TryEnqueue(() =>
                    {
                        assistantMsg.Content += token;

                        // Auto-follow seulement si on est en mode follow et que l'user n'a pas scroll up
                        if (_autoFollow && !_userScrolledUp)
                            ScrollToBottom();
                    });
                },
                ct: _cts.Token);

            var wasCancelled = _cts.Token.IsCancellationRequested;

            if (!wasCancelled)
            {
                assistantMsg.Content = string.IsNullOrWhiteSpace(finalAnswer)
                    ? "⚠️ Réponse vide côté LLM. Voir les sources à droite."
                    : finalAnswer;

                assistantMsg.StatusNote = null;
            }
            else
            {
                MarkInterrupted(assistantMsg);
                if (string.IsNullOrWhiteSpace(assistantMsg.Content) && !string.IsNullOrWhiteSpace(finalAnswer))
                    assistantMsg.Content = finalAnswer;
            }

            var pretty = System.Text.Json.JsonSerializer.Serialize(
                sourcesObj,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

            assistantMsg.SourcesJson = pretty;

            SourcesCards.Items = SourceCardParser.Parse(pretty);
            SourcesBox.Text = pretty;

            await _api.AddMessageAsync(_sessionId!, "assistant", assistantMsg.Content, sourcesObj, CancellationToken.None);

            // refresh sidebar order after activity
            await RefreshSessionsAsync(preferSessionId: _sessionId, CancellationToken.None);

            if (_autoFollow && !_userScrolledUp)
                ScrollToBottom(force: true);

            Status(wasCancelled ? "Cancelled." : "Done.");
        }
        catch (OperationCanceledException)
        {
            SetTyping(false);
            UpdateJumpButton();
            MarkInterrupted(assistantMsg);

            if (_autoFollow && !_userScrolledUp)
                ScrollToBottom(force: true);

            Status("Cancelled.");
        }
        catch (Exception ex)
        {
            SetTyping(false);
            UpdateJumpButton();
            Status("Send failed: " + ex.Message);
        }
        finally
        {
            SetTyping(false);
            UpdateJumpButton();
            UpdateUiState(isGenerating: false);
        }
    }

    private void MessagesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MessagesList.SelectedItem is ChatMessageItem m)
        {
            SourcesCards.Items = SourceCardParser.Parse(m.SourcesJson);
            SourcesBox.Text = m.SourcesJson ?? "";
        }
    }

    // =========================
    // Local LLM (llama.cpp) - M6.1
    // =========================

    private void LoadLocalLlmUiFromSettings()
    {
        try
        {
            _appSettings = AppSettings.Load();

            LocalLlmEnabledCheck.IsChecked = _appSettings.UseLocalLlm;
            LocalLlmAutoStartCheck.IsChecked = _appSettings.AutoStartOnConnect;

            LocalLlmExePathBox.Text = _appSettings.LlamaExePath;
            LocalLlmModelPathBox.Text = _appSettings.ModelPath;

            LocalLlmHostBox.Text = _appSettings.Host;
            LocalLlmPortBox.Text = _appSettings.Port.ToString();

            LocalLlmModelIdBox.Text = _appSettings.ModelId;
            LocalLlmExtraArgsBox.Text = _appSettings.ExtraArgs;

            LocalLlmStatusText.Text = _llmProc.IsRunning ? "Running." : "";
            LocalLlmCmdLineBox.Text = _llmProc.LastCommandLine ?? "";
        }
        catch
        {
            // ignore UI init failures
        }
    }

    private AppSettings ReadLocalLlmSettingsFromUi()
    {
        var s = AppSettings.Load();

        s.UseLocalLlm = LocalLlmEnabledCheck.IsChecked == true;
        s.AutoStartOnConnect = LocalLlmAutoStartCheck.IsChecked == true;

        s.LlamaExePath = (LocalLlmExePathBox.Text ?? "").Trim();
        s.ModelPath = (LocalLlmModelPathBox.Text ?? "").Trim();

        s.Host = string.IsNullOrWhiteSpace(LocalLlmHostBox.Text) ? "127.0.0.1" : LocalLlmHostBox.Text.Trim();

        if (int.TryParse((LocalLlmPortBox.Text ?? "").Trim(), out var p) && p > 0) s.Port = p;
        else s.Port = 1234;

        s.ModelId = string.IsNullOrWhiteSpace(LocalLlmModelIdBox.Text) ? ClientDefaults.LlmModel : LocalLlmModelIdBox.Text.Trim();
        s.ExtraArgs = (LocalLlmExtraArgsBox.Text ?? "").Trim();

        return s;
    }

    private async Task<bool> EnsureLocalLlmStartedAsync(CancellationToken ct)
    {
        _appSettings = ReadLocalLlmSettingsFromUi();
        _appSettings.Save();

        LocalLlmStatusText.Text = "Starting llama.cpp…";
        var (ok, msg) = await _llmProc.StartAsync(_appSettings, ct);

        LocalLlmCmdLineBox.Text = _llmProc.LastCommandLine ?? "";
        LocalLlmStatusText.Text = msg;

        // reflect URL/model
        LlmUrlBox.Text = _appSettings.LlmBaseUrl;
        LlmModelBox.Text = _appSettings.ModelId;

        return ok;
    }

    private async void LocalLlmStart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsureLocalLlmStartedAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            LocalLlmStatusText.Text = "Start failed: " + ex.Message;
        }
    }

    private void LocalLlmStop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _llmProc.Stop();
            LocalLlmStatusText.Text = "Stopped.";
        }
        catch (Exception ex)
        {
            LocalLlmStatusText.Text = "Stop failed: " + ex.Message;
        }
    }

    private void LocalLlmSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _appSettings = ReadLocalLlmSettingsFromUi();
            _appSettings.Save();

            // reflect URL/model in top bar
            LlmUrlBox.Text = _appSettings.UseLocalLlm ? _appSettings.LlmBaseUrl : ClientDefaults.LlmBaseUrl;
            LlmModelBox.Text = _appSettings.ModelId;

            LocalLlmStatusText.Text = "Saved.";
        }
        catch (Exception ex)
        {
            LocalLlmStatusText.Text = "Save failed: " + ex.Message;
        }
    }

}
