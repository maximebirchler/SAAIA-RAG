using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

using SAAIA.Client.WinUI.Models;
using SAAIA.Client.WinUI.Services;

using Windows.Graphics;
using Windows.Storage;

namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly ApiClient _api = new();
    private readonly OpenAiLlmClient _llm = new();
    private RagChatAgent? _agent;

    private readonly ObservableCollection<ChatMessageItem> _messages = new();

    private string? _sessionId;
    private string _userId = "";
    private CancellationTokenSource? _cts;
    private bool _isGenerating;

    public MainWindow()
    {
        InitializeComponent();

        TryResize(1200, 780);

        MessagesList.ItemsSource = _messages;

        LoadSettings();
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

    private bool IsConnected => _agent is not null && !string.IsNullOrWhiteSpace(_sessionId);

    private void UpdateUiState(bool isGenerating)
    {
        _isGenerating = isGenerating;

        SendButton.IsEnabled = IsConnected && !_isGenerating;
        CancelButton.IsEnabled = IsConnected && _isGenerating;
        NewChatButton.IsEnabled = IsConnected && !_isGenerating;

        InputBox.IsEnabled = IsConnected && !_isGenerating;
        ConnectButton.IsEnabled = !_isGenerating;
    }

    private void LoadSettings()
    {
        var ls = ApplicationData.Current.LocalSettings;

        ServerUrlBox.Text = ClientDefaults.BackendBaseUrl;
        _userId = SecureLocalStore.GetOrCreateUserId();

        ApiKeyBox.Password = SecureLocalStore.GetServerApiKey() ?? "";

        LlmUrlBox.Text = ClientDefaults.LlmBaseUrl;
        LlmModelBox.Text = ClientDefaults.LlmModel;

        _sessionId = (ls.Values["sessionId"] as string);
    }

    private void SaveSettings()
    {
        var ls = ApplicationData.Current.LocalSettings;

        SecureLocalStore.SetServerApiKey(ApiKeyBox.Password.Trim());

        if (!string.IsNullOrWhiteSpace(_sessionId))
            ls.Values["sessionId"] = _sessionId;
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _userId = SecureLocalStore.GetOrCreateUserId();

            _api.Configure(ClientDefaults.BackendBaseUrl, ApiKeyBox.Password, _userId);
            _llm.Configure(ClientDefaults.LlmBaseUrl, ClientDefaults.LlmModel);
            _agent = new RagChatAgent(_api, _llm);

            SaveSettings();

            Status("Connecting…");

            if (string.IsNullOrWhiteSpace(_sessionId))
            {
                var res = await _api.CreateSessionAsync("New chat", Environment.UserName, CancellationToken.None);
                _sessionId = res.SessionId;
                SaveSettings();
            }

            _messages.Clear();
            var msgs = await _api.ListMessagesAsync(_sessionId!, CancellationToken.None);
            foreach (var m in msgs) _messages.Add(m);

            SourcesCards.Items = new List<SourceCard>();
            SourcesBox.Text = "";

            Status($"Connected. Session: {_sessionId}");
            UpdateUiState(isGenerating: false);
        }
        catch (Exception ex)
        {
            Status("Connect failed: " + ex.Message);
            UpdateUiState(isGenerating: false);
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

    private async void NewChat_Click(object sender, RoutedEventArgs e)
    {
        if (_agent is null)
        {
            Status("Click Connect first.");
            return;
        }

        try
        {
            UpdateUiState(isGenerating: false);
            Status("Creating new chat…");

            var res = await _api.CreateSessionAsync("New chat", Environment.UserName, CancellationToken.None);
            _sessionId = res.SessionId;
            SaveSettings();

            _messages.Clear();
            SourcesCards.Items = new List<SourceCard>();
            SourcesBox.Text = "";

            Status($"New session: {_sessionId}");
            UpdateUiState(isGenerating: false);
        }
        catch (Exception ex)
        {
            Status("New chat failed: " + ex.Message);
            UpdateUiState(isGenerating: false);
        }
    }

    private static void MarkInterrupted(ChatMessageItem? assistantMsg)
    {
        if (assistantMsg is null) return;

        // ✅ On ne met PAS la note dans Content (sinon doublons + layout moche)
        if (string.IsNullOrWhiteSpace(assistantMsg.StatusNote))
            assistantMsg.StatusNote = "Génération interrompue.";
    }

    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(_sessionId))
        {
            Status("Click Connect first.");
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

            SourcesCards.Items = new List<SourceCard>();
            SourcesBox.Text = "";

            Status("Thinking…");

            var (finalAnswer, sourcesObj) = await _agent.RunAsync(
                userText: text,
                category: ClientDefaults.DefaultCategory,
                conversationTail: tailBefore,
                onDelta: token =>
                {
                    DispatcherQueue.TryEnqueue(() => { assistantMsg.Content += token; });
                },
                ct: _cts.Token);

            var wasCancelled = _cts.Token.IsCancellationRequested;

            // ✅ Si pas cancel : on finalise le texte.
            // ✅ Si cancel : on garde le texte déjà streamé (ne pas écraser).
            if (!wasCancelled)
            {
                assistantMsg.Content = string.IsNullOrWhiteSpace(finalAnswer)
                    ? "⚠️ Réponse vide côté LLM. Voir les sources à droite."
                    : finalAnswer;

                assistantMsg.StatusNote = null;
            }
            else
            {
                // Cancel: garder le stream, ajouter juste le label gris.
                MarkInterrupted(assistantMsg);

                // cas rare: cancel avant le moindre delta, mais agent a quand même renvoyé un bout
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

            Status(wasCancelled ? "Cancelled." : "Done.");
        }
        catch (OperationCanceledException)
        {
            // cancel possible pendant la phase RAG (avant streaming)
            MarkInterrupted(assistantMsg);
            Status("Cancelled.");
        }
        catch (Exception ex)
        {
            Status("Send failed: " + ex.Message);
        }
        finally
        {
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
}
