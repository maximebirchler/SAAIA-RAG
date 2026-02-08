using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.UI.Windowing;
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
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();

        // Fixe la taille (WinUI3 ne supporte pas Width/Height sur <Window>)
        TryResize(1200, 780);

        MessagesList.ItemsSource = _messages;

        LoadSettings();
        Status("Ready.");
    }

    private void TryResize(int width, int height)
    {
        try
        {
            AppWindow.Resize(new SizeInt32(width, height));
        }
        catch
        {
            // Sur certains cas, l'AppWindow n'est pas prêt immédiatement.
            // On retente dès que la fenêtre est activée.
            Activated += (_, __) =>
            {
                try { AppWindow.Resize(new SizeInt32(width, height)); } catch { }
            };
        }
    }

    private void Status(string s) => StatusText.Text = s;

    private void LoadSettings()
    {
        var ls = ApplicationData.Current.LocalSettings;

        ServerUrlBox.Text = (ls.Values["serverUrl"] as string) ?? "http://localhost:5122";
        ApiKeyBox.Password = (ls.Values["apiKey"] as string) ?? "";

        LlmUrlBox.Text = (ls.Values["llmUrl"] as string) ?? "http://127.0.0.1:8080/v1";
        LlmModelBox.Text = (ls.Values["llmModel"] as string) ?? "mistral";

        _sessionId = (ls.Values["sessionId"] as string);
    }

    private void SaveSettings()
    {
        var ls = ApplicationData.Current.LocalSettings;

        ls.Values["serverUrl"] = ServerUrlBox.Text.Trim();
        ls.Values["apiKey"] = ApiKeyBox.Password.Trim();

        ls.Values["llmUrl"] = LlmUrlBox.Text.Trim();
        ls.Values["llmModel"] = LlmModelBox.Text.Trim();

        if (!string.IsNullOrWhiteSpace(_sessionId))
            ls.Values["sessionId"] = _sessionId;
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _api.Configure(ServerUrlBox.Text, ApiKeyBox.Password);
            _llm.Configure(LlmUrlBox.Text, LlmModelBox.Text);
            _agent = new RagChatAgent(_api, _llm);

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

            Status($"Connected. Session: {_sessionId}");
        }
        catch (Exception ex)
        {
            Status("Connect failed: " + ex.Message);
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

        try
        {
            InputBox.Text = "";

            // 1) add user message (UI + server)
            var userMsg = new ChatMessageItem { Role = "user", Content = text, CreatedAt = DateTime.UtcNow };
            _messages.Add(userMsg);
            await _api.AddMessageAsync(_sessionId!, "user", text, null, CancellationToken.None);

            // 2) assistant streaming placeholder
            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            var assistantMsg = new ChatMessageItem
            {
                Role = "assistant",
                Content = "",
                CreatedAt = DateTime.UtcNow
            };
            _messages.Add(assistantMsg);

            SourcesBox.Text = "";
            Status("Thinking…");

            // On passe un "tail" pour contexte (derniers messages)
            var tail = _messages.ToList();

            object? sourcesPayload = null;

            var (finalAnswer, sourcesObj) = await _agent.RunAsync(
                userText: text,
                category: "general",
                conversationTail: tail,
                onDelta: token =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        assistantMsg.Content += token;
                    });
                },
                ct: _cts.Token);

            sourcesPayload = sourcesObj;

            assistantMsg.Content = finalAnswer;

            var pretty = System.Text.Json.JsonSerializer.Serialize(
                sourcesPayload,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

            assistantMsg.SourcesJson = pretty;
            SourcesBox.Text = pretty;

            await _api.AddMessageAsync(_sessionId!, "assistant", assistantMsg.Content, sourcesPayload, CancellationToken.None);

            Status("Done.");
        }
        catch (Exception ex)
        {
            Status("Send failed: " + ex.Message);
        }
    }

    private void MessagesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MessagesList.SelectedItem is ChatMessageItem m)
            SourcesBox.Text = m.SourcesJson ?? "";
    }
}
