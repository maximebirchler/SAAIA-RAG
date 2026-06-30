namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow
{
    private async void SendCancel_Click(object sender, RoutedEventArgs e)
    {
        var hasSession = !string.IsNullOrWhiteSpace(_sessionId);
        var hasText = !string.IsNullOrWhiteSpace(InputBox?.Text);
        var canInvoke = IsConnected && hasSession && ((_isGenerating && !_isCancellingGeneration) || hasText);
        if (!canInvoke)
            return;

        if (_isGenerating && !_isCancellingGeneration)
        {
            CancelGeneration();
            return;
        }

        await SendAsync();
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!Services.StagedOutboundMessageState.ShouldRetainPendingAfterTextChange(
                _pendingOutboundWireText,
                _pendingOutboundDisplayText,
                InputBox?.Text))
        {
            ClearStagedOutboundMessage();
        }

        UpdateSendCancelButtonVisualState();
    }

    private void InputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || e.KeyStatus.IsMenuKeyDown)
            return;

        if (sender is not TextBox tb)
            return;

        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
        var shiftDown = (shift & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

        e.Handled = true;

        if (shiftDown)
        {
            InsertNewLineAtCaret(tb);
            return;
        }

        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await SendAsync();
            }
            catch
            {
                // non bloquant
            }
        });
    }

    private static void InsertNewLineAtCaret(TextBox tb)
    {
        // WinUI TextBox normalizes line breaks internally. Using Environment.NewLine here can
        // desynchronize SelectionStart vs the actual stored text on repeated Shift+Enter presses.
        // A single CR keeps caret math stable and avoids the regression where a second Shift+Enter
        // appears to remove the previous line break.
        const string newline = "\r";

        var current = tb.Text ?? string.Empty;
        var selectionStart = Math.Clamp(tb.SelectionStart, 0, current.Length);
        var selectionLength = Math.Clamp(tb.SelectionLength, 0, current.Length - selectionStart);

        var updated = current.Remove(selectionStart, selectionLength).Insert(selectionStart, newline);
        tb.Text = updated;

        var caret = Math.Clamp(selectionStart + newline.Length, 0, tb.Text?.Length ?? 0);
        tb.SelectionStart = caret;
        tb.SelectionLength = 0;
    }

    private void CancelGeneration()
    {
        if (!_isGenerating) return;

        _isCancellingGeneration = true;
        UpdateSendCancelButtonVisualState();
        Status(LocalRuntimeText("Annulation...", "Cancelling...", "Cancelando...", "A cancelar...", "Abbrechen...", "Annullamento...", UiLang));
        TrySoftUi("CancelGeneration.CancelToken", () => _cts?.Cancel());
    }

    private void MarkInterrupted(ChatMessageItem? assistantMsg)
    {
        if (assistantMsg is null) return;

        assistantMsg.ProgressText = null;

        if (string.IsNullOrWhiteSpace(assistantMsg.Content))
        {
            assistantMsg.Content = "";
            assistantMsg.StatusNote = LocalRuntimeText("Generation interrompue.", "Generation interrupted.", "Generacion interrumpida.", "Geracao interrompida.", "Generierung unterbrochen.", "Generazione interrotta.", UiLang);
            return;
        }

        if (string.IsNullOrWhiteSpace(assistantMsg.StatusNote))
            assistantMsg.StatusNote = LocalRuntimeText("Generation interrompue.", "Generation interrupted.", "Generacion interrumpida.", "Geracao interrompida.", "Generierung unterbrochen.", "Generazione interrotta.", UiLang);
    }

    private static void SetAssistantProgress(ChatMessageItem? assistantMsg, string? progress)
    {
        if (assistantMsg is null) return;
        assistantMsg.ProgressText = string.IsNullOrWhiteSpace(progress) ? null : progress.Trim();
    }

    private static void ClearAssistantProgress(ChatMessageItem? assistantMsg)
        => SetAssistantProgress(assistantMsg, null);

    private static void StampAssistantMessageStart(ChatMessageItem? assistantMsg, ref int replyStarted)
    {
        if (assistantMsg is null)
            return;

        if (System.Threading.Interlocked.CompareExchange(ref replyStarted, 1, 0) != 0)
            return;

        assistantMsg.CreatedAt = DateTime.UtcNow;
    }

    private void EnsureAssistantMessageHasFailureText(ChatMessageItem? assistantMsg, Exception? cause = null)
    {
        if (assistantMsg is null)
            return;

        ClearAssistantProgress(assistantMsg);
        assistantMsg.StatusNote = null;

        if (!string.IsNullOrWhiteSpace(assistantMsg.Content))
            return;

        // Try to recognise the most common root cause (local assistant unreachable / refused)
        // and tell the user something actionable instead of the generic "try again".
        var hint = ClassifyAssistantFailure(cause);
        assistantMsg.Content = hint;
    }

    private string ClassifyAssistantFailure(Exception? ex)
    {
        var lang = UiLang;
        var msg = ex?.Message ?? string.Empty;
        var lower = msg.ToLowerInvariant();

        if (ex is InvalidOperationException
            && (lower.Contains("modele local")
                || lower.Contains("local model")
                || lower.Contains("warmup")
                || lower.Contains("verification de stabilite")
                || lower.Contains("stability check")))
        {
            return msg;
        }

        if (lower.Contains("exceeds the available context size")
            || (lower.Contains("context size") && lower.Contains("exceed"))
            || lower.Contains("context window")
            || lower.Contains("n_ctx"))
        {
            return LocalRuntimeText(
                "Le contexte documentaire est trop volumineux pour l'assistant local actuel. Essaie une question plus ciblée ou utilise un profil avec une fenêtre de contexte plus grande.",
                "The document context is too large for the current local assistant. Try a more focused question or use a profile with a larger context window.",
                "El contexto documental es demasiado grande para el asistente local actual. Prueba con una pregunta más concreta o usa un perfil con una ventana de contexto mayor.",
                "O contexto documental é demasiado grande para o assistente local atual. Tenta uma pergunta mais focada ou usa um perfil com uma janela de contexto maior.",
                "Der Dokumentkontext ist zu gross fuer den aktuellen lokalen Assistenten. Stelle eine gezieltere Frage oder nutze ein Profil mit groesserem Kontextfenster.",
                "Il contesto documentale è troppo grande per l'assistente locale attuale. Prova con una domanda più mirata o usa un profilo con una finestra di contesto più ampia.",
                lang);
        }

        // Network errors talking to the local assistant endpoint (most common: nothing listening on
        // 127.0.0.1:1234 because Docker/llama-server isn't up, or wrong port/host configured).
        if (ex is HttpRequestException || ex is TaskCanceledException
            || lower.Contains("connection refused") || lower.Contains("no connection could be made")
            || lower.Contains("actively refused") || lower.Contains("connection.*timed out")
            || lower.Contains("timed out")
            || lower.Contains("name resolution"))
        {
            return LocalRuntimeText(
                "L'assistant local n'a pas répondu. Vérifie qu'il est démarré et que son URL est correcte dans les paramètres.",
                "The local assistant did not respond. Check that it is running and that its URL is correct in settings.",
                "El asistente local no respondió. Comprueba que esté iniciado y que su URL sea correcta en ajustes.",
                "O assistente local não respondeu. Verifica se está iniciado e se o URL está correto nas definições.",
                "Der lokale Assistent hat nicht geantwortet. Pruefe, ob er laeuft und ob seine URL in den Einstellungen stimmt.",
                "L'assistente locale non ha risposto. Verifica che sia avviato e che l'URL sia corretto nelle impostazioni.",
                lang);
        }

        return LocalRuntimeText(
            "La réponse n'a pas pu être générée. Réessaie.",
            "The reply could not be generated. Try again.",
            "No se pudo generar la respuesta. Vuelve a intentarlo.",
            "Não foi possível gerar a resposta. Tenta novamente.",
            "Die Antwort konnte nicht erzeugt werden. Versuche es erneut.",
            "Non è stato possibile generare la risposta. Riprova.",
            lang);
    }

    private async Task MaybeAutoTitleAsync(string userText)
    {
        // Si le titre est "New chat" (ou vide), on met un titre basé sur la 1ère question
        if (SessionsList.SelectedItem is not ChatSessionItem s) return;

        var currentTitle = (s.Title ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(currentTitle) && !IsDefaultSessionTitle(currentTitle))
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
        _typingPinned = isTyping;
        if (TypingText is not null)
        {
            TypingText.Text = string.Empty;
            TypingText.Visibility = Visibility.Collapsed;
        }
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

        void ScrollNow()
        {
            try
            {
                _isProgrammaticScroll = true;
                MessagesList?.UpdateLayout();
                MessagesScroll?.UpdateLayout();
                MessagesScroll?.ChangeView(null, MessagesScroll.ScrollableHeight, null, true);
            }
            catch
            {
                // non bloquant
            }
            finally
            {
                _isProgrammaticScroll = false;
            }
        }

        ScrollNow();

        try
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ScrollNow);
        }
        catch
        {
            // non bloquant
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

            Status(LocalRuntimeText("Copie.", "Copied.", "Copiado.", "Copiado.", "Kopiert.", "Copiato.", UiLang));
        }
        catch (Exception ex)
        {
            Status(LocalRuntimeText("Echec de la copie : ", "Copy failed: ", "Error al copiar: ", "Falha ao copiar: ", "Kopieren fehlgeschlagen: ", "Copia non riuscita: ", UiLang) + ex.Message);
        }
    }

    private void ApplyResponsiveLayout(double width)
    {
        // UI change: Sources panel is deprecated (sources are now inline in the chat).
        // Keep it permanently hidden to avoid wasting space.
        try
        {
            SourcesCol.Width = new GridLength(0);
            SourcesPanel.Visibility = Visibility.Collapsed;
            SourcesToggleButton.Visibility = Visibility.Collapsed;
        }
        catch
        {
            // non bloquant
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
        try { _dialogOverlayResizeHandler?.Invoke(e.NewSize); } catch { }
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

            var closeButton = BuildDialogFooterButton(ClientUiText.Get("dialog.close", _appSettings.UiLanguage), primary: true);
            var dialogSize = GetDialogMaxSize(920, 720, horizontalMargin: 72, verticalMargin: 96);
            var shell = BuildDialogShell(
                LocalRuntimeText("Sources", "Sources", "Fuentes", "Fontes", "Quellen", "Fonti", UiLang),
                LocalRuntimeText("Sources", "Sources", "Fuentes", "Fontes", "Quellen", "Fonti", UiLang),
                cards.Count == 0 ? LocalRuntimeText("Aucune source disponible pour la selection actuelle.", "No source is available for the current selection.", "No hay fuentes disponibles para la seleccion actual.", "Nenhuma fonte disponivel para a selecao atual.", "Keine Quelle fuer die aktuelle Auswahl verfuegbar.", "Nessuna fonte disponibile per la selezione corrente.", UiLang) : null,
                new UIElement[]
                {
                    BuildDialogSurfaceCard(ctrl, new Thickness(12))
                },
                BuildDialogFooter(closeButton));
            shell.MaxWidth = dialogSize.Width;
            shell.MaxHeight = dialogSize.Height;

            OverlayDialogSession? overlay = null;
            closeButton.Click += (_, __) => overlay?.Close();
            overlay = ShowOverlayDialog(
                shell,
                resizeHandler: _ =>
                {
                    var size = GetDialogMaxSize(920, 720, horizontalMargin: 72, verticalMargin: 96);
                    shell.MaxWidth = size.Width;
                    shell.MaxHeight = size.Height;
                });

            await overlay.Completion;
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

        var text = LinkifiedTextBlock.ToPlainText(msg.Content);
        if (text.Length == 0) return;

        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);

        Status(LocalRuntimeText("Copie.", "Copied.", "Copiado.", "Copiado.", "Kopiert.", "Copiato.", UiLang));
    }

    private void Bubble_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;

        if (fe.FindName("CopyBtn") is Button b)
        {
            ToolTipService.SetToolTip(b, ClientUiText.Get("button.copy", UiLang));
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
            Status(LocalRuntimeText("Selectionne une discussion (ou Nouveau).", "Select a chat (or New).", "Selecciona un chat (o Nuevo).", "Seleciona uma conversa (ou Novo).", "Waehle einen Chat (oder Neu).", "Seleziona una chat (o Nuovo).", UiLang));
            return;
        }
        if (_agent is null)
        {
            Status(LocalRuntimeText("Clique d'abord sur Connecter (agent non pret).", "Click Connect first (agent not ready).", "Haz clic primero en Connect (agente no listo).", "Clica primeiro em Connect (agente nao pronto).", "Klicke zuerst auf Connect (Agent nicht bereit).", "Fai prima clic su Connect (agente non pronto).", UiLang));
            return;
        }

        var stagedResolution = Services.StagedOutboundMessageState.ResolveForSend(
            _pendingOutboundWireText,
            _pendingOutboundDisplayText,
            InputBox.Text);
        var text = stagedResolution.EffectiveWireText;
        var shownText = stagedResolution.EffectiveDisplayText;
        if (text.Length == 0) return;

        ChatMessageItem? assistantMsg = null;

        try
        {
            _autoFollow = true;
            _userScrolledUp = false;
            UpdateJumpButton();

            UpdateUiState(isGenerating: true);
            InputBox.Text = string.Empty;
            ClearStagedOutboundMessage();

            var tailBefore = _messages.ToList();
            Services.ClientLog.Info(
                "Chat send begin: " +
                $"session={_sessionId}|" +
                $"wireChars={text.Length}|" +
                $"displayChars={shownText.Length}|" +
                $"tail={tailBefore.Count}");

            var userMsg = new ChatMessageItem { Role = "user", Content = shownText, CreatedAt = DateTime.UtcNow };
            _messages.Add(userMsg);
            ScrollToBottom(force: true);
            await _api.AddMessageAsync(_sessionId!, "user", shownText, null, CancellationToken.None);

            await MaybeAutoTitleAsync(shownText);

            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            assistantMsg = new ChatMessageItem
            {
                Role = "assistant",
                Content = string.Empty,
                CreatedAt = DateTime.UtcNow,
                StatusNote = null,
                ProgressText = LocalRuntimeText("Je prepare la reponse...", "Preparing the reply...", "Preparando la respuesta...", "A preparar a resposta...", "Antwort wird vorbereitet...", "Preparazione della risposta...", UiLang)
            };
            _messages.Add(assistantMsg);
            ScrollToBottom(force: true);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ScrollToBottom(force: true));

            SourcesCards.Items = new List<SourceCard>();
            SourcesBox.Text = string.Empty;

            SetTyping(true);
            _autoFollow = true;
            _userScrolledUp = false;
            UpdateJumpButton();

            var finalAnswerCommitted = 0;
            var replyStarted = 0;

            if (!await EnsureLocalLlmAwakeForRequestAsync(assistantMsg, _cts.Token))
            {
                var startupFailure = string.IsNullOrWhiteSpace(_lastLocalLlmStartupFailure)
                    ? LocalRuntimeText("Assistant temporairement indisponible : le modele local n'a pas pu demarrer.", "Assistant temporarily unavailable: the local model could not start.", "Asistente temporalmente no disponible: el modelo local no pudo iniciarse.", "Assistente temporariamente indisponivel: nao foi possivel iniciar o modelo local.", "Assistent voruebergehend nicht verfuegbar: das lokale Modell konnte nicht gestartet werden.", "Assistente temporaneamente non disponibile: il modello locale non e riuscito ad avviarsi.", UiLang)
                    : _lastLocalLlmStartupFailure!;
                throw new InvalidOperationException(startupFailure);
            }

            assistantMsg.IsStreaming = true;
            var (finalAnswer, sourcesObj) = await _agent.RunAsync(
                userText: text,
                category: ClientDefaults.DefaultCategory,
                conversationTail: tailBefore,
                onDelta: token =>
                {
                    if (string.IsNullOrEmpty(token) || System.Threading.Volatile.Read(ref finalAnswerCommitted) == 1)
                        return;

                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (System.Threading.Volatile.Read(ref finalAnswerCommitted) == 1)
                            return;

                        StampAssistantMessageStart(assistantMsg, ref replyStarted);
                        assistantMsg.StatusNote = null;
                        ClearAssistantProgress(assistantMsg);
                        assistantMsg.Content += token;

                        if (_autoFollow && !_userScrolledUp)
                            ScrollToBottom(force: true);
                    });
                },
                onPhase: _ => { },
                onProgress: progress =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        SetAssistantProgress(assistantMsg, progress);
                        if (_autoFollow && !_userScrolledUp)
                            ScrollToBottom(force: true);
                    });
                },
                ct: _cts.Token);
            Services.ClientLog.Info(
                "Chat send agent result: " +
                $"cancelled={_cts.Token.IsCancellationRequested}|" +
                $"answerChars={finalAnswer?.Length ?? 0}|" +
                $"sourcesPayload={(sourcesObj is null ? "none" : sourcesObj.GetType().Name)}");

            var wasCancelled = _cts.Token.IsCancellationRequested;

            if (!wasCancelled)
            {
                assistantMsg.IsStreaming = false;
                ClearAssistantProgress(assistantMsg);

                if (!string.IsNullOrWhiteSpace(finalAnswer))
                {
                    StampAssistantMessageStart(assistantMsg, ref replyStarted);
                    System.Threading.Interlocked.Exchange(ref finalAnswerCommitted, 1);
                    assistantMsg.Content = finalAnswer;
                }
                else if (string.IsNullOrWhiteSpace(assistantMsg.Content))
                {
                    assistantMsg.Content = LocalRuntimeText("Réponse vide de l'assistant local. Vérifie les sources affichées.", "Empty reply from the local assistant. Check the displayed sources.", "Respuesta vacía del asistente local. Revisa las fuentes mostradas.", "Resposta vazia do assistente local. Consulta as fontes apresentadas.", "Leere Antwort vom lokalen Assistenten. Pruefe die angezeigten Quellen.", "Risposta vuota dall'assistente locale. Controlla le fonti mostrate.", UiLang);
                }

                assistantMsg.StatusNote = null;
            }
            else
            {
                assistantMsg.IsStreaming = false;
                MarkInterrupted(assistantMsg);
                if (string.IsNullOrWhiteSpace(assistantMsg.Content) && !string.IsNullOrWhiteSpace(finalAnswer))
                {
                    StampAssistantMessageStart(assistantMsg, ref replyStarted);
                    System.Threading.Interlocked.Exchange(ref finalAnswerCommitted, 1);
                    assistantMsg.Content = finalAnswer;
                }
            }

            var pretty = sourcesObj is null
                ? ""
                : System.Text.Json.JsonSerializer.Serialize(
                    sourcesObj,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

            assistantMsg.SourcesJson = pretty;
            SourcesCards.Items = SourceCardParser.Parse(pretty);
            SourcesBox.Text = pretty;

            await _api.AddMessageAsync(_sessionId!, "assistant", assistantMsg.Content, sourcesObj, CancellationToken.None, assistantMsg.StatusNote);

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
        }
        catch (OperationCanceledException)
        {
            SetTyping(false);
            UpdateJumpButton();
            if (assistantMsg is not null)
                assistantMsg.IsStreaming = false;
            MarkInterrupted(assistantMsg);

            try
            {
                if (!string.IsNullOrWhiteSpace(_sessionId) && assistantMsg is not null)
                {
                    await _api.AddMessageAsync(
                        _sessionId!,
                        "assistant",
                        assistantMsg.Content ?? "",
                        assistantMsg.SourcesJson,
                        CancellationToken.None,
                        assistantMsg.StatusNote);

                    try { await RefreshSessionsAsync(preferSessionId: _sessionId, CancellationToken.None); } catch { }
                }
            }
            catch
            {
            }

            if (_autoFollow && !_userScrolledUp)
                ScrollToBottom(force: true);

            ClearStatus();
        }
        catch (Exception ex)
        {
            Services.ClientLog.Exception("Chat.SendAsync", ex);
            if (assistantMsg is not null)
                assistantMsg.IsStreaming = false;
            EnsureAssistantMessageHasFailureText(assistantMsg, ex);
            SetTyping(false);
            UpdateJumpButton();
            Status(LocalRuntimeText("Échec de l'envoi : ", "Send failed: ", "Error al enviar: ", "Falha ao enviar: ", "Senden fehlgeschlagen: ", "Invio non riuscito: ", UiLang) + ex.Message);
        }
        finally
        {
            if (assistantMsg is not null)
                assistantMsg.IsStreaming = false;
            UpdateUiState(isGenerating: false);
            SetTyping(false);
            UpdateJumpButton();
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
