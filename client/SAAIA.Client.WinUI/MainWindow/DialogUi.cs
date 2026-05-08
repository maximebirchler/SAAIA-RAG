using System.Net.Http;

namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow
{
    // Turn an HttpRequestException's opaque "Response status code does not indicate success."
    // into a humane "GET /admin/x → HTTP 401 (clé admin invalide ?)" so admin panels show
    // the integrator something actionable instead of the raw .NET message.
    private static string FormatAdminLoadError(System.Exception ex, string endpoint, string lang)
    {
        if (ex is TaskCanceledException)
            return endpoint + " — timeout";

        if (ex is HttpRequestException http && http.StatusCode is { } code)
        {
            var hint = (int)code switch
            {
                401 => lang switch { "en" => "missing or invalid admin key", "es" => "clave admin inválida o ausente", "pt" => "chave admin inválida ou ausente", "de" => "Admin-Key fehlt oder ungueltig", "it" => "chiave admin mancante o non valida", _ => "clé admin invalide ou absente" },
                403 => lang switch { "en" => "admin key not authorized", "es" => "clave admin no autorizada", "pt" => "chave admin não autorizada", "de" => "Admin-Key nicht autorisiert", "it" => "chiave admin non autorizzata", _ => "clé admin non autorisée" },
                404 => lang switch { "en" => "endpoint missing on backend", "es" => "endpoint ausente en backend", "pt" => "endpoint ausente no backend", "de" => "Endpoint fehlt im Backend", "it" => "endpoint assente nel backend", _ => "endpoint absent côté backend" },
                503 => lang switch { "en" => "backend service unavailable", "es" => "servicio backend no disponible", "pt" => "serviço backend indisponível", "de" => "Backend-Dienst nicht verfuegbar", "it" => "servizio backend non disponibile", _ => "service backend indisponible" },
                _ => lang switch { "en" => "see server logs", "es" => "ver logs del servidor", "pt" => "ver logs do servidor", "de" => "Server-Logs ansehen", "it" => "vedi log server", _ => "voir logs serveur" }
            };
            return $"GET {endpoint} → HTTP {(int)code} ({hint})";
        }

        var msg = ex.Message ?? string.Empty;
        if (msg.Length > 160) msg = msg.Substring(0, 157) + "…";
        return $"{endpoint} — {msg}";
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
