using System.Net.Http;

namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow
{
    private static string FormatAdminLoadErrorForUser(System.Exception ex, string endpoint, string lang)
    {
        if (ex is TaskCanceledException)
        {
            return lang switch
            {
                "en" => "The server did not answer in time. Try again in a moment; if it keeps happening, check the server load.",
                "es" => "El servidor no respondió a tiempo. Inténtalo de nuevo en un momento; si continúa, revisa la carga del servidor.",
                "pt" => "O servidor não respondeu a tempo. Tenta novamente daqui a pouco; se continuar, verifica a carga do servidor.",
                "de" => "Der Server hat nicht rechtzeitig geantwortet. Versuche es gleich erneut; falls es bleibt, prüfe die Serverlast.",
                "it" => "Il server non ha risposto in tempo. Riprova tra poco; se continua, controlla il carico del server.",
                _ => "Le serveur n'a pas répondu à temps. Réessaie dans un instant ; si cela continue, vérifie la charge serveur."
            };
        }

        if (ex is HttpRequestException http && http.StatusCode is { } code)
        {
            return (int)code switch
            {
                401 => lang switch
                {
                    "en" => "The admin session is not accepted by the server. Check the admin/API key in the configuration.",
                    "es" => "El servidor no acepta la sesión de administración. Revisa la clave admin/API en la configuración.",
                    "pt" => "O servidor não aceita a sessão de administração. Verifica a chave admin/API na configuração.",
                    "de" => "Der Server akzeptiert die Admin-Sitzung nicht. Prüfe den Admin/API-Schlüssel in der Konfiguration.",
                    "it" => "Il server non accetta la sessione admin. Controlla la chiave admin/API nella configurazione.",
                    _ => "Le serveur n'accepte pas la session d'administration. Vérifie la clé admin/API dans la configuration."
                },
                403 => lang switch
                {
                    "en" => "The admin key is recognized but not allowed to open this view.",
                    "es" => "La clave admin se reconoce, pero no tiene permiso para abrir esta vista.",
                    "pt" => "A chave admin é reconhecida, mas não tem autorização para abrir esta vista.",
                    "de" => "Der Admin-Schlüssel wurde erkannt, darf diese Ansicht aber nicht öffnen.",
                    "it" => "La chiave admin è riconosciuta, ma non può aprire questa vista.",
                    _ => "La clé admin est reconnue, mais elle n'a pas le droit d'ouvrir cet écran."
                },
                404 => lang switch
                {
                    "en" => "This server version does not provide this admin view yet.",
                    "es" => "Esta versión del servidor todavía no ofrece esta vista de administración.",
                    "pt" => "Esta versão do servidor ainda não fornece esta vista de administração.",
                    "de" => "Diese Serverversion stellt diese Admin-Ansicht noch nicht bereit.",
                    "it" => "Questa versione del server non fornisce ancora questa vista admin.",
                    _ => "Cette version du serveur ne fournit pas encore cet écran d'administration."
                },
                400 => lang switch
                {
                    "en" => "The server refused this action in its current state. Refresh the server state, then try again.",
                    "es" => "El servidor rechazo esta accion en su estado actual. Actualiza el estado del servidor y vuelve a intentarlo.",
                    "pt" => "O servidor recusou esta acao no estado atual. Atualiza o estado do servidor e tenta novamente.",
                    "de" => "Der Server hat diese Aktion im aktuellen Zustand abgelehnt. Serverstatus aktualisieren und erneut versuchen.",
                    "it" => "Il server ha rifiutato questa azione nello stato attuale. Aggiorna lo stato del server e riprova.",
                    _ => "Le serveur refuse cette action dans son etat actuel. Actualise l'etat serveur, puis reessaie."
                },
                500 => lang switch
                {
                    "en" => "The server failed while preparing this view. I kept the technical detail in the logs.",
                    "es" => "El servidor falló al preparar esta vista. El detalle técnico queda en los logs.",
                    "pt" => "O servidor falhou ao preparar esta vista. O detalhe técnico ficou nos logs.",
                    "de" => "Der Server konnte diese Ansicht nicht vorbereiten. Details stehen in den Logs.",
                    "it" => "Il server non è riuscito a preparare questa vista. I dettagli tecnici sono nei log.",
                    _ => "Le serveur a échoué en préparant cet écran. Le détail technique est conservé dans les logs."
                },
                503 => lang switch
                {
                    "en" => "The server is temporarily unavailable. Wait for the current processing to calm down, then refresh.",
                    "es" => "El servidor no está disponible temporalmente. Espera a que baje la actividad y actualiza.",
                    "pt" => "O servidor está temporariamente indisponível. Aguarda que a atividade baixe e atualiza.",
                    "de" => "Der Server ist vorübergehend nicht verfügbar. Warte kurz und aktualisiere danach.",
                    "it" => "Il server è temporaneamente non disponibile. Attendi che l'attività cali e aggiorna.",
                    _ => "Le serveur est temporairement indisponible. Attends que les traitements se calment, puis actualise."
                },
                _ => lang switch
                {
                    "en" => "The server returned an unexpected response. The technical detail is available in the logs.",
                    "es" => "El servidor devolvió una respuesta inesperada. El detalle técnico está en los logs.",
                    "pt" => "O servidor devolveu uma resposta inesperada. O detalhe técnico está nos logs.",
                    "de" => "Der Server hat unerwartet geantwortet. Details stehen in den Logs.",
                    "it" => "Il server ha restituito una risposta inattesa. I dettagli tecnici sono nei log.",
                    _ => "Le serveur a renvoyé une réponse inattendue. Le détail technique est disponible dans les logs."
                }
            };
        }

        return lang switch
        {
            "en" => "This action could not be completed. Check the connection and try again.",
            "es" => "No se pudo completar esta acción. Revisa la conexión e inténtalo de nuevo.",
            "pt" => "Não foi possível concluir esta ação. Verifica a ligação e tenta novamente.",
            "de" => "Diese Aktion konnte nicht abgeschlossen werden. Prüfe die Verbindung und versuche es erneut.",
            "it" => "Non è stato possibile completare questa azione. Controlla la connessione e riprova.",
            _ => "L'action n'a pas pu être terminée. Vérifie la connexion, puis réessaie."
        };
    }

    private static string FormatLocalLlmUserActionError(System.Exception ex, string lang)
    {
        if (ex is UnauthorizedAccessException)
            return lang switch { "en" => "Access denied. Check the file or folder permissions.", "es" => "Acceso denegado. Revisa los permisos del archivo o de la carpeta.", "pt" => "Acesso negado. Verifica as permissões do ficheiro ou da pasta.", "de" => "Zugriff verweigert. Prüfe die Datei- oder Ordnerrechte.", "it" => "Accesso negato. Controlla i permessi del file o della cartella.", _ => "Accès refusé. Vérifie les droits du fichier ou du dossier." };
        if (ex is FileNotFoundException or DirectoryNotFoundException)
            return lang switch { "en" => "The selected file or folder no longer exists.", "es" => "El archivo o la carpeta seleccionados ya no existen.", "pt" => "O ficheiro ou a pasta selecionados já não existem.", "de" => "Die ausgewählte Datei oder der Ordner existiert nicht mehr.", "it" => "Il file o la cartella selezionati non esistono più.", _ => "Le fichier ou le dossier sélectionné n'existe plus." };
        if (ex is IOException)
            return lang switch { "en" => "Windows could not access the file. Close any program using it, then try again.", "es" => "Windows no pudo acceder al archivo. Cierra el programa que lo usa e inténtalo de nuevo.", "pt" => "O Windows não conseguiu aceder ao ficheiro. Fecha o programa que o usa e tenta novamente.", "de" => "Windows konnte nicht auf die Datei zugreifen. Schließe Programme, die sie nutzen, und versuche es erneut.", "it" => "Windows non ha potuto accedere al file. Chiudi il programma che lo usa e riprova.", _ => "Windows n'a pas pu accéder au fichier. Ferme le programme qui l'utilise, puis réessaie." };

        return lang switch
        {
            "en" => "The local assistant action failed. The technical detail was written to the logs.",
            "es" => "La acción del asistente local falló. El detalle técnico se escribió en los logs.",
            "pt" => "A ação do assistente local falhou. O detalhe técnico foi escrito nos logs.",
            "de" => "Die Aktion des lokalen Assistenten ist fehlgeschlagen. Details wurden in die Logs geschrieben.",
            "it" => "L'azione dell'assistente locale non è riuscita. I dettagli tecnici sono stati scritti nei log.",
            _ => "L'action de l'assistant local a échoué. Le détail technique a été écrit dans les logs."
        };
    }

    private static string BuildAdminRuntimeActionErrorDetail(System.Exception ex, string lang)
    {
        if (ex is TaskCanceledException)
        {
            return lang switch
            {
                "en" => "request timed out",
                "es" => "la solicitud ha expirado",
                "pt" => "o pedido excedeu o tempo limite",
                "de" => "Anfrage mit Zeitueberschreitung",
                "it" => "richiesta scaduta",
                _ => "delai d'attente depasse"
            };
        }

        var message = ex.Message ?? string.Empty;
        var lower = message.ToLowerInvariant();
        if (lower.Contains("capability_not_qualified") || lower.Contains("not_qualified"))
            return lang switch { "en" => "this server feature has not been checked yet; run a server recheck first", "es" => "esta funcion del servidor aun no esta validada; lanza primero una revision del servidor", "pt" => "esta funcao do servidor ainda nao foi verificada; executa primeiro uma reverificacao do servidor", "de" => "diese Serverfunktion wurde noch nicht geprueft; zuerst den Server erneut pruefen", "it" => "questa funzione server non e ancora verificata; esegui prima una verifica server", _ => "cette fonction serveur n'est pas encore verifiee ; lance d'abord une verification serveur" };
        if (lower.Contains("capability_not_selected") || lower.Contains("not_selected"))
            return lang switch { "en" => "this server feature is checked but not enabled for execution", "es" => "esta funcion del servidor esta validada pero no activada para ejecucion", "pt" => "esta funcao do servidor esta verificada mas nao ativada para execucao", "de" => "diese Serverfunktion ist geprueft, aber nicht zur Ausfuehrung aktiviert", "it" => "questa funzione server e verificata ma non attivata per l'esecuzione", _ => "cette fonction serveur est verifiee mais pas activee pour l'execution" };
        if (lower.Contains("capability_stale") || lower.Contains("stale"))
            return lang switch { "en" => "the server state is outdated; refresh or repair blocked states before launching", "es" => "el estado del servidor esta obsoleto; actualiza o repara los estados bloqueados antes de lanzar", "pt" => "o estado do servidor esta obsoleto; atualiza ou repara os estados bloqueados antes de lancar", "de" => "der Serverstatus ist veraltet; vor dem Start aktualisieren oder blockierte Zustaende reparieren", "it" => "lo stato del server e obsoleto; aggiorna o ripara gli stati bloccati prima di avviare", _ => "l'etat serveur est obsolete ; actualise ou repare les etats bloques avant de demarrer" };
        if (lower.Contains("documents_root_not_found") || lower.Contains("documents_root_not_configured"))
            return lang switch { "en" => "the server documents folder is not configured or not reachable", "es" => "la carpeta de documentos del servidor no esta configurada o no es accesible", "pt" => "a pasta de documentos do servidor nao esta configurada ou nao esta acessivel", "de" => "der Dokumentenordner des Servers ist nicht konfiguriert oder nicht erreichbar", "it" => "la cartella documenti del server non e configurata o non e raggiungibile", _ => "le dossier documents du serveur n'est pas configure ou n'est pas accessible" };

        ClientLog.Exception("AdminRuntime.Action", ex);
        return FormatAdminLoadErrorForUser(ex, "/admin/runtime/action", lang);
    }

    private Border BuildDialogBadge(string badgeText)
    {
        var light = UseLightPalette();
        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Child = new TextBlock
            {
                Text = (badgeText ?? string.Empty).ToUpperInvariant(),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                CharacterSpacing = 80,
                Foreground = light ? UiBrush(0x5D, 0x6E, 0x82) : UiBrush(0xA8, 0xB5, 0xC7),
                Opacity = 0.9
            }
        };
    }

    private Border BuildDialogHeroCard(string badgeText, string title, string? subtitle = null)
    {
        var light = UseLightPalette();
        var headerStack = new StackPanel { Spacing = 6 };
        headerStack.Children.Add(BuildDialogBadge(badgeText));
        headerStack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Foreground = light ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xF5, 0xF7, 0xFB),
            TextWrapping = TextWrapping.WrapWholeWords
        });

        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            headerStack.Children.Add(new TextBlock
            {
                Text = subtitle,
                Foreground = light ? UiBrush(0x4D, 0x5D, 0x71) : UiBrush(0xC7, 0xD0, 0xDC),
                Opacity = 0.96,
                TextWrapping = TextWrapping.WrapWholeWords
            });
        }

        return new Border
        {
            Padding = new Thickness(0, 0, 0, 2),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Child = headerStack
        };
    }

    private Border BuildDialogSurfaceCard(UIElement child, Thickness? padding = null)
    {
        var light = UseLightPalette();
        return new Border
        {
            CornerRadius = new CornerRadius(15),
            Padding = padding ?? new Thickness(14),
            Background = light ? UiBrush(0xF8, 0xFB, 0xFE) : UiBrush(0x15, 0x1B, 0x24),
            BorderBrush = light ? UiBrush(0xC4, 0xD0, 0xDC) : UiBrush(0x2A, 0x33, 0x3D),
            BorderThickness = new Thickness(1),
            Child = child
        };
    }

    private Border BuildDialogInfoBanner(string text, bool positive = false)
    {
        var light = UseLightPalette();
        var foreground = light ? UiBrush(0x19, 0x24, 0x33) : UiBrush(0xF2, 0xF5, 0xFA);
        var iconForeground = positive
            ? (light ? UiBrush(0x2E, 0x7D, 0x5A) : UiBrush(0x66, 0xD1, 0x9E))
            : (light ? UiBrush(0x5E, 0x7A, 0x97) : UiBrush(0x78, 0xB4, 0xF0));

        return new Border
        {
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14, 12, 14, 12),
            Background = positive
                ? (light ? UiBrush(0xEC, 0xF7, 0xF2) : UiBrush(0x12, 0x24, 0x1E))
                : (light ? UiBrush(0xF4, 0xF7, 0xFB) : UiBrush(0x14, 0x1B, 0x24)),
            BorderBrush = positive
                ? (light ? UiBrush(0xC6, 0xE5, 0xD7) : UiBrush(0x2A, 0x54, 0x43))
                : (light ? UiBrush(0xC9, 0xD4, 0xE1) : UiBrush(0x2B, 0x35, 0x41)),
            BorderThickness = new Thickness(1),
            Child = BuildDialogInfoBannerContent(text, positive ? "\uE73E" : "\uE946", iconForeground, foreground)
        };
    }

    private static Grid BuildDialogInfoBannerContent(string text, string glyph, Brush iconForeground, Brush foreground)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new FontIcon
        {
            Glyph = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            Foreground = iconForeground,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0)
        };
        var textBlock = new TextBlock
        {
            Text = text,
            MinWidth = 0,
            TextWrapping = TextWrapping.WrapWholeWords,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = foreground
        };

        Grid.SetColumn(icon, 0);
        Grid.SetColumn(textBlock, 1);
        grid.Children.Add(icon);
        grid.Children.Add(textBlock);
        return grid;
    }

    private Border BuildDialogNameChip(string text)
    {
        var light = UseLightPalette();
        return new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 10, 14, 10),
            Background = light ? UiBrush(0xE9, 0xF0, 0xF8) : UiBrush(0x17, 0x22, 0x31),
            BorderBrush = light ? UiBrush(0xBD, 0xCD, 0xDF) : UiBrush(0x2E, 0x4A, 0x68),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = light ? UiBrush(0x19, 0x24, 0x33) : UiBrush(0xF5, 0xF8, 0xFC)
            }
        };
    }

    private Border BuildCompactDialogShell(string badgeText, string title, string? subtitle, IEnumerable<UIElement> body, UIElement footer)
    {
        var light = UseLightPalette();
        var stack = new StackPanel { Spacing = 14, MaxWidth = 400 };
        stack.Children.Add(BuildDialogHeroCard(badgeText, title, subtitle));

        foreach (var child in body)
            stack.Children.Add(child);

        stack.Children.Add(footer);

        return new Border
        {
            MaxWidth = 400,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(24),
            Padding = new Thickness(16),
            Background = light ? UiBrush(0xEC, 0xF1, 0xF6) : UiBrush(0x14, 0x19, 0x21),
            BorderBrush = light ? UiBrush(0xB8, 0xC5, 0xD3) : UiBrush(0x2E, 0x36, 0x42),
            BorderThickness = new Thickness(1),
            Child = stack
        };
    }

    private Border BuildDialogShell(string badgeText, string title, string? subtitle, IEnumerable<UIElement> body, UIElement footer)
    {
        var light = UseLightPalette();
        var stack = new StackPanel { Spacing = 16, MaxWidth = 840 };
        stack.Children.Add(BuildDialogHeroCard(badgeText, title, subtitle));
        foreach (var child in body)
            stack.Children.Add(child);
        stack.Children.Add(footer);

        return new Border
        {
            MaxWidth = 840,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(24),
            Padding = new Thickness(16),
            Background = light ? UiBrush(0xEC, 0xF1, 0xF6) : UiBrush(0x14, 0x19, 0x21),
            BorderBrush = light ? UiBrush(0xB8, 0xC5, 0xD3) : UiBrush(0x2E, 0x36, 0x42),
            BorderThickness = new Thickness(1),
            Child = stack
        };
    }

    private Border BuildScrollableDialogShell(string badgeText, string title, string? subtitle, IEnumerable<UIElement> body, UIElement footer, double maxWidth, double maxHeight)
    {
        var light = UseLightPalette();
        var bodyStack = new StackPanel { Spacing = 16, MaxWidth = Math.Max(280, maxWidth - 20) };
        foreach (var child in body)
            bodyStack.Children.Add(child);

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = bodyStack,
            MaxHeight = Math.Max(220, maxHeight - 180)
        };

        var layout = new Grid { RowSpacing = 16 };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var hero = BuildDialogHeroCard(badgeText, title, subtitle);
        var footerElement = footer as FrameworkElement ?? new ContentPresenter { Content = footer };
        Grid.SetRow(hero, 0);
        Grid.SetRow(scroller, 1);
        Grid.SetRow(footerElement, 2);
        layout.Children.Add(hero);
        layout.Children.Add(scroller);
        layout.Children.Add(footerElement);

        return new Border
        {
            MaxWidth = maxWidth,
            MaxHeight = maxHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(24),
            Padding = new Thickness(16),
            Background = light ? UiBrush(0xEC, 0xF1, 0xF6) : UiBrush(0x14, 0x19, 0x21),
            BorderBrush = light ? UiBrush(0xB8, 0xC5, 0xD3) : UiBrush(0x2E, 0x36, 0x42),
            BorderThickness = new Thickness(1),
            Child = layout
        };
    }

    private Grid WrapDialogContent(UIElement content)
    {
        var host = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        if (content is FrameworkElement framework)
        {
            framework.HorizontalAlignment = HorizontalAlignment.Center;
            framework.VerticalAlignment = VerticalAlignment.Center;
            framework.Margin = new Thickness(0);
        }
        else
        {
            var presenter = new ContentPresenter
            {
                Content = content,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0)
            };
            host.Children.Add(presenter);
            return host;
        }

        host.Children.Add((UIElement)content);
        return host;
    }

    private TextBlock BuildDialogFieldLabel(string text)
        => new()
        {
            Text = text,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = UseLightPalette() ? UiBrush(0x5D, 0x6E, 0x82) : UiBrush(0xA8, 0xB5, 0xC7),
            CharacterSpacing = 30
        };

    private Border BuildDialogTextBoxCard(TextBox box)
    {
        box.BorderThickness = new Thickness(0);
        box.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        box.Padding = new Thickness(0);
        box.MinWidth = 0;
        return BuildDialogSurfaceCard(box, new Thickness(14, 12, 14, 12));
    }

    private Border BuildDialogPasswordBoxCard(PasswordBox box)
    {
        box.BorderThickness = new Thickness(0);
        box.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        box.Padding = new Thickness(0);
        box.MinWidth = 0;
        return BuildDialogSurfaceCard(box, new Thickness(14, 12, 14, 12));
    }

    private void ApplyDialogInputChrome(Control control)
    {
        var light = UseLightPalette();
        var background = light ? UiBrush(0xF8, 0xFB, 0xFE) : UiBrush(0x19, 0x1F, 0x29);
        var disabledBackground = light ? UiBrush(0xE8, 0xEE, 0xF5) : UiBrush(0x12, 0x17, 0x20);
        var foreground = light ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xF5, 0xF7, 0xFB);
        var disabledForeground = light ? UiBrush(0x5F, 0x70, 0x84) : UiBrush(0x92, 0x9A, 0xA8);
        var border = light ? UiBrush(0xC5, 0xD0, 0xDD) : UiBrush(0x35, 0x42, 0x50);

        control.Background = background;
        control.Foreground = foreground;
        control.BorderBrush = border;
        control.BorderThickness = new Thickness(1);
        control.Resources["TextControlBackground"] = background;
        control.Resources["TextControlBackgroundPointerOver"] = background;
        control.Resources["TextControlBackgroundFocused"] = background;
        control.Resources["TextControlBackgroundDisabled"] = disabledBackground;
        control.Resources["TextControlForeground"] = foreground;
        control.Resources["TextControlForegroundPointerOver"] = foreground;
        control.Resources["TextControlForegroundFocused"] = foreground;
        control.Resources["TextControlForegroundDisabled"] = disabledForeground;
        control.Resources["ComboBoxBackground"] = background;
        control.Resources["ComboBoxBackgroundPointerOver"] = background;
        control.Resources["ComboBoxBackgroundFocused"] = background;
        control.Resources["ComboBoxBackgroundDisabled"] = disabledBackground;
        control.Resources["ComboBoxForeground"] = foreground;
        control.Resources["ComboBoxForegroundPointerOver"] = foreground;
        control.Resources["ComboBoxForegroundFocused"] = foreground;
        control.Resources["ComboBoxForegroundDisabled"] = disabledForeground;
        control.Resources["CalendarDatePickerBackground"] = background;
        control.Resources["CalendarDatePickerBackgroundPointerOver"] = background;
        control.Resources["CalendarDatePickerForeground"] = foreground;
        control.Resources["CalendarDatePickerForegroundDisabled"] = disabledForeground;
    }

    private Button BuildDialogFooterButton(string text, bool primary = false, bool destructive = false)
    {
        var light = UseLightPalette();
        var background = destructive
            ? (light ? UiBrush(0xC2, 0x4D, 0x56) : UiBrush(0x8E, 0x3A, 0x41))
            : primary ? (light ? UiBrush(0x5E, 0x7A, 0x97) : UiBrush(0x3A, 0x84, 0xD8))
            : (light ? UiBrush(0xF1, 0xF4, 0xF8) : UiBrush(0x19, 0x1D, 0x26));
        var hover = destructive
            ? (light ? UiBrush(0xD7, 0x5A, 0x64) : UiBrush(0xA0, 0x44, 0x4C))
            : primary ? (light ? UiBrush(0x6B, 0x87, 0xA4) : UiBrush(0x4B, 0x95, 0xE6))
            : (light ? UiBrush(0xE7, 0xEE, 0xF7) : UiBrush(0x22, 0x29, 0x33));
        var pressed = destructive
            ? (light ? UiBrush(0xAD, 0x3F, 0x47) : UiBrush(0x74, 0x2D, 0x33))
            : primary ? (light ? UiBrush(0x4F, 0x68, 0x82) : UiBrush(0x2B, 0x6D, 0xB6))
            : (light ? UiBrush(0xDA, 0xE5, 0xF2) : UiBrush(0x17, 0x1B, 0x22));
        var border = destructive
            ? (light ? UiBrush(0xD7, 0x5A, 0x64) : UiBrush(0xA0, 0x44, 0x4C))
            : primary ? (light ? UiBrush(0x6B, 0x87, 0xA4) : UiBrush(0x56, 0xA7, 0xE7))
            : (light ? UiBrush(0xCC, 0xD6, 0xE4) : UiBrush(0x36, 0x38, 0x40));
        var foreground = destructive || primary
            ? UiBrush(0xFF, 0xFF, 0xFF)
            : (light ? UiBrush(0x11, 0x18, 0x27) : UiBrush(0xF5, 0xF7, 0xFB));
        var disabledBackground = light ? UiBrush(0xE2, 0xE8, 0xF0) : UiBrush(0x1B, 0x21, 0x2A);
        var disabledForeground = light ? UiBrush(0x5F, 0x70, 0x84) : UiBrush(0x91, 0x9A, 0xA8);
        var disabledBorder = light ? UiBrush(0xCF, 0xD9, 0xE6) : UiBrush(0x30, 0x38, 0x44);

        var button = new Button
        {
            Content = new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
                // Wrap multi-word labels (e.g. "Réconcilier stale") instead of letting them clip
                // when the footer Grid hands the button a narrow column.
                TextWrapping = TextWrapping.WrapWholeWords
            },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Background = background,
            BorderBrush = border,
            Foreground = foreground,
            UseSystemFocusVisuals = false
        };

        button.Resources["ButtonBackgroundPointerOver"] = hover;
        button.Resources["ButtonBackgroundPressed"] = pressed;
        button.Resources["ButtonBorderBrushPointerOver"] = border;
        button.Resources["ButtonBorderBrushPressed"] = border;
        button.Resources["ButtonForegroundPointerOver"] = foreground;
        button.Resources["ButtonForegroundPressed"] = foreground;
        button.Resources["ButtonBackgroundDisabled"] = disabledBackground;
        button.Resources["ButtonBorderBrushDisabled"] = disabledBorder;
        button.Resources["ButtonForegroundDisabled"] = disabledForeground;
        return button;
    }

    private Grid BuildDialogFooter(params Button[] buttons)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        if (buttons.Length <= 1)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (buttons.Length == 1)
            {
                buttons[0].HorizontalAlignment = HorizontalAlignment.Right;
                buttons[0].MinWidth = 160;
                grid.Children.Add(buttons[0]);
            }
            return grid;
        }

        for (var i = 0; i < buttons.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(buttons[i], i);
            grid.Children.Add(buttons[i]);
        }

        return grid;
    }

    private Button BuildDialogChromeIconButton(string tooltip)
    {
        var button = new Button
        {
            Width = 40,
            Height = 40,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Content = new FontIcon { Glyph = "", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 16 },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Style = (Style?)Application.Current.Resources["AppIconButtonStyle"]
        };
        ToolTipService.SetToolTip(button, tooltip);
        ApplyHeaderButtonChrome(button);
        return button;
    }

    private Border BuildAdminWorkspaceDialogShell(
        string badgeText,
        string title,
        string? subtitle,
        FrameworkElement metrics,
        FrameworkElement toolbar,
        FrameworkElement body,
        FrameworkElement footer,
        FrameworkElement? closeButton,
        double maxWidth,
        double maxHeight)
    {
        var light = UseLightPalette();
        var layout = new Grid { RowSpacing = 14 };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var headerGrid = new Grid { ColumnSpacing = 12 };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var hero = BuildDialogHeroCard(badgeText, title, subtitle);
        Grid.SetColumn(hero, 0);
        headerGrid.Children.Add(hero);
        if (closeButton is not null)
        {
            Grid.SetColumn(closeButton, 1);
            headerGrid.Children.Add(closeButton);
        }

        Grid.SetRow(headerGrid, 0);
        Grid.SetRow(metrics, 1);
        Grid.SetRow(toolbar, 2);
        Grid.SetRow(body, 3);
        Grid.SetRow(footer, 4);
        layout.Children.Add(headerGrid);
        layout.Children.Add(metrics);
        layout.Children.Add(toolbar);
        layout.Children.Add(body);
        layout.Children.Add(footer);

        return new Border
        {
            MaxWidth = maxWidth,
            MaxHeight = maxHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(24),
            Padding = new Thickness(18),
            Background = light ? UiBrush(0xEC, 0xF1, 0xF6) : UiBrush(0x14, 0x19, 0x21),
            BorderBrush = light ? UiBrush(0xB8, 0xC5, 0xD3) : UiBrush(0x2E, 0x36, 0x42),
            BorderThickness = new Thickness(1),
            Child = layout
        };
    }


}
