using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

using Windows.System;

using SAAIA.Client.WinUI.Services;

namespace SAAIA.Client.WinUI.Controls;

public sealed partial class LinkifiedTextBlock : UserControl
{
    // Token: [[open|<docPath>|<page>|<label>]]
    // Example: [[open|ATEX/EN 1127-1.pdf|12|EN 1127-1 (ATEX) p.12]]
    private static readonly Regex TokenRegex = new(
        "\\[\\[open\\|(?<path>[^|]+)\\|(?<page>[^|]+)\\|(?<label>[^\\]]+)\\]\\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BulletLineRegex = new(
        @"^(?<indent>\s*)-\s+(?<rest>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex NumberLineRegex = new(
        @"^(?<indent>\s*)(?<num>\d{1,4})\.\s+(?<rest>.*)$",
        RegexOptions.Compiled);

    // ---------------- Visual tuning ----------------
    // Edit these values if you want tighter/looser tree spacing.
    private static readonly string[] TreeBullets = new[] { "•", "◦", "▪", "–" };
    private const int IndentPerLevelPx = 14;
    private const int BulletMarkerWidthPx = 14;
    private const int NumberMarkerWidthPx = 22;
    private const int MarkerGapPx = 2;

    private const double DocLinkNormalAlphaMultiplier = 0.90;
    private const double DocLinkHoverAlphaMultiplier = 1.00;
    private const double DocLinkPressedAlphaMultiplier = 0.82;
    private const double InlineLinkAlphaMultiplier = 0.85;

    public LinkifiedTextBlock()
    {
        InitializeComponent();
        Loaded += (_, _) => Render();
    }

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(LinkifiedTextBlock),
        new PropertyMetadata(string.Empty, (d, _) => ((LinkifiedTextBlock)d).Render()));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty TextForegroundProperty = DependencyProperty.Register(
        nameof(TextForeground),
        typeof(Brush),
        typeof(LinkifiedTextBlock),
        new PropertyMetadata(null, (d, _) => ((LinkifiedTextBlock)d).Render()));

    public Brush? TextForeground
    {
        get => (Brush?)GetValue(TextForegroundProperty);
        set => SetValue(TextForegroundProperty, value);
    }

    public static readonly DependencyProperty TextFontSizeProperty = DependencyProperty.Register(
        nameof(TextFontSize),
        typeof(double),
        typeof(LinkifiedTextBlock),
        new PropertyMetadata(0.0, (d, _) => ((LinkifiedTextBlock)d).Render()));

    public double TextFontSize
    {
        get => (double)GetValue(TextFontSizeProperty);
        set => SetValue(TextFontSizeProperty, value);
    }

    private void Render()
    {
        if (Rtb is null || LinesPanel is null) return;

        var text = Text ?? string.Empty;
        var fg = TextForeground ?? new SolidColorBrush(Microsoft.UI.Colors.White);
        var fs = TextFontSize > 0 ? TextFontSize : 14;

        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        var lines = normalized.Split('\n');
        var useLineRenderer = lines.Any(IsListOrTreeLine);
        var singleSourceBlock = IsSingleNumberedSourceBlock(lines);

        if (useLineRenderer)
        {
            Rtb.Visibility = Visibility.Collapsed;
            LinesPanel.Visibility = Visibility.Visible;

            Rtb.Blocks.Clear();
            LinesPanel.Children.Clear();

            foreach (var line in lines)
                RenderLine(line, fg, fs, singleSourceBlock);

            return;
        }

        LinesPanel.Visibility = Visibility.Collapsed;
        Rtb.Visibility = Visibility.Visible;
        LinesPanel.Children.Clear();

        RenderRichTextInto(Rtb, text, fg, fs, InlineLinkAlphaMultiplier);
    }

    private static bool IsListOrTreeLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        return BulletLineRegex.IsMatch(line) || NumberLineRegex.IsMatch(line);
    }

    private static bool IsSingleNumberedSourceBlock(string[] lines)
    {
        var numberedCount = lines.Count(x => !string.IsNullOrWhiteSpace(x) && NumberLineRegex.IsMatch(x));
        if (numberedCount != 1) return false;

        return lines.Any(x =>
            !string.IsNullOrWhiteSpace(x) &&
            x.TrimStart().StartsWith("Source", StringComparison.OrdinalIgnoreCase));
    }

    private void RenderLine(string line, Brush fg, double fs, bool singleSourceBlock)
    {
        if (LinesPanel is null) return;

        if (string.IsNullOrWhiteSpace(line))
        {
            LinesPanel.Children.Add(new Border { Height = 6, Opacity = 0 });
            return;
        }

        if (TryParseListLine(line, singleSourceBlock, out var level, out var marker, out var rest, out var isNumber))
        {
            var grid = new Grid
            {
                Margin = new Thickness(level * IndentPerLevelPx, 0, 0, 2),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(isNumber ? NumberMarkerWidthPx : BulletMarkerWidthPx)
            });
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });

            var markerTb = new TextBlock
            {
                Text = marker,
                Foreground = fg,
                FontSize = fs,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, MarkerGapPx, 0),
                IsTextSelectionEnabled = false
            };
            Grid.SetColumn(markerTb, 0);
            grid.Children.Add(markerTb);

            var content = BuildListLineContent(rest, fg, fs);
            Grid.SetColumn(content, 1);
            grid.Children.Add(content);

            LinesPanel.Children.Add(grid);
            return;
        }

        var elem = BuildRichTextElement(line, fg, fs, InlineLinkAlphaMultiplier);
        elem.Margin = new Thickness(0, 0, 0, 2);
        LinesPanel.Children.Add(elem);
    }

    private static bool TryParseListLine(string line, bool singleSourceBlock, out int level, out string marker, out string rest, out bool isNumber)
    {
        level = 0;
        marker = string.Empty;
        rest = line;
        isNumber = false;

        var mNum = NumberLineRegex.Match(line);
        if (mNum.Success)
        {
            var indent = mNum.Groups["indent"].Value ?? string.Empty;
            level = Math.Max(0, indent.Length / 2);
            rest = (mNum.Groups["rest"].Value ?? string.Empty).Trim();

            if (singleSourceBlock)
            {
                marker = TreeBullets[0];
                isNumber = false;
            }
            else
            {
                marker = (mNum.Groups["num"].Value ?? string.Empty).Trim() + ".";
                isNumber = true;
            }

            return true;
        }

        var mBul = BulletLineRegex.Match(line);
        if (mBul.Success)
        {
            var indent = mBul.Groups["indent"].Value ?? string.Empty;
            level = Math.Max(0, indent.Length / 2);
            rest = (mBul.Groups["rest"].Value ?? string.Empty).Trim();
            marker = TreeBullets[Math.Min(level, TreeBullets.Length - 1)];
            isNumber = false;
            return true;
        }

        return false;
    }

    private FrameworkElement BuildListLineContent(string rest, Brush fg, double fs)
    {
        if (TryParseSingleOpenToken(rest, out var path, out var page, out var label))
            return MakeDocLinkButton(path, page, label, fg, fs);

        if (TokenRegex.IsMatch(rest))
            return BuildRichTextElement(rest, fg, fs, InlineLinkAlphaMultiplier);

        return new TextBlock
        {
            Text = rest,
            Foreground = fg,
            FontSize = fs,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = false
        };
    }

    private static bool TryParseSingleOpenToken(string s, out string docPath, out int page, out string label)
    {
        docPath = string.Empty;
        page = 1;
        label = string.Empty;

        if (string.IsNullOrWhiteSpace(s)) return false;
        var trimmed = s.Trim();

        var m = TokenRegex.Match(trimmed);
        if (!m.Success) return false;
        if (m.Index != 0 || m.Length != trimmed.Length) return false;

        docPath = (m.Groups["path"].Value ?? string.Empty).Trim();
        var pageRaw = (m.Groups["page"].Value ?? "1").Trim();
        _ = int.TryParse(pageRaw, out page);
        if (page <= 0) page = 1;
        label = (m.Groups["label"].Value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(label)) label = docPath;

        return !string.IsNullOrWhiteSpace(docPath);
    }

    private FrameworkElement MakeDocLinkButton(string docPath, int page, string label, Brush baseFg, double fs)
    {
        _ = baseFg;
        var normalBrush = CreateDocLinkNormalBrush();
        var hoverBrush = CreateDocLinkHoverBrush();
        var pressedBrush = CreateDocLinkPressedBrush();
        var hoverBackground = CreateDocLinkHoverBackground();
        var pressedBackground = CreateDocLinkPressedBackground();
        var hoverBorderBrush = CreateDocLinkHoverBorderBrush();
        var transparentBackground = CreateBrush(0x00, 0x00, 0x00, 0x00);

        var tb = new TextBlock
        {
            Text = label,
            Foreground = normalBrush,
            FontSize = fs,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = false
        };

        var btn = new HandButton
        {
            Padding = new Thickness(2, 0, 2, 0),
            Background = transparentBackground,
            BorderBrush = transparentBackground,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Top,
            IsTabStop = false,
            Content = tb
        };

        void ApplyNormalState()
        {
            tb.Foreground = normalBrush;
            tb.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            tb.Opacity = 1.0;
            btn.Background = transparentBackground;
            btn.BorderBrush = transparentBackground;
        }

        void ApplyHoverState()
        {
            tb.Foreground = hoverBrush;
            tb.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            tb.Opacity = 1.0;
            btn.Background = hoverBackground;
            btn.BorderBrush = hoverBorderBrush;
        }

        void ApplyPressedState()
        {
            tb.Foreground = pressedBrush;
            tb.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            tb.Opacity = 1.0;
            btn.Background = pressedBackground;
            btn.BorderBrush = hoverBorderBrush;
        }

        ApplyNormalState();

        btn.PointerEntered += (_, _) => ApplyHoverState();
        btn.PointerExited += (_, _) => ApplyNormalState();
        btn.PointerPressed += (_, _) => ApplyPressedState();
        btn.PointerReleased += (_, _) => ApplyHoverState();
        btn.PointerCanceled += (_, _) => ApplyNormalState();
        btn.Click += async (_, _) => await OpenAsync(docPath, page);

        return btn;
    }

    private static FrameworkElement BuildRichTextElement(string text, Brush fg, double fs, double linkAlphaMultiplier)
    {
        var rtb = new RichTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = false
        };

        RenderRichTextInto(rtb, text, fg, fs, linkAlphaMultiplier);
        return rtb;
    }

    private static void RenderRichTextInto(RichTextBlock rtb, string text, Brush fg, double fs, double linkAlphaMultiplier)
    {
        rtb.Blocks.Clear();

        var p = new Paragraph();
        var linkFg = CreateDocLinkNormalBrush();
        var source = text ?? string.Empty;
        var last = 0;

        foreach (Match m in TokenRegex.Matches(source))
        {
            if (m.Index > last)
                p.Inlines.Add(MakeRun(source.Substring(last, m.Index - last), fg, fs));

            var path = (m.Groups["path"].Value ?? string.Empty).Trim();
            var pageRaw = (m.Groups["page"].Value ?? "1").Trim();
            _ = int.TryParse(pageRaw, out var page);
            if (page <= 0) page = 1;

            var label = (m.Groups["label"].Value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(label)) label = path;

            var link = new Hyperlink
            {
                UnderlineStyle = UnderlineStyle.Single,
                Foreground = linkFg
            };
            link.Inlines.Add(MakeRun(label, linkFg, fs));
            link.Click += async (_, _) => await OpenAsync(path, page);
            p.Inlines.Add(link);

            last = m.Index + m.Length;
        }

        if (last < source.Length)
            p.Inlines.Add(MakeRun(source.Substring(last), fg, fs));

        rtb.Blocks.Add(p);
    }

    private static Run MakeRun(string s, Brush fg, double fs)
        => new() { Text = s, Foreground = fg, FontSize = fs };

    private static Brush MakeAlphaBrush(Brush baseFg, double alphaMultiplier)
    {
        if (baseFg is SolidColorBrush sb)
        {
            var c = sb.Color;
            var a = (byte)Math.Clamp((int)Math.Round(c.A * alphaMultiplier), 0, 255);
            return new SolidColorBrush(global::Windows.UI.Color.FromArgb(a, c.R, c.G, c.B));
        }

        return baseFg;
    }

    private static SolidColorBrush CreateBrush(byte a, byte r, byte g, byte b)
        => new(global::Windows.UI.Color.FromArgb(a, r, g, b));

    private static Brush CreateDocLinkNormalBrush() => CreateBrush(0xFF, 0x8A, 0xCC, 0xFF);
    private static Brush CreateDocLinkHoverBrush() => CreateBrush(0xFF, 0xBF, 0xE3, 0xFF);
    private static Brush CreateDocLinkPressedBrush() => CreateBrush(0xFF, 0x6E, 0xB8, 0xF0);
    private static Brush CreateDocLinkHoverBackground() => CreateBrush(0x32, 0x8A, 0xCC, 0xFF);
    private static Brush CreateDocLinkPressedBackground() => CreateBrush(0x46, 0x6E, 0xB8, 0xF0);
    private static Brush CreateDocLinkHoverBorderBrush() => CreateBrush(0x7A, 0x8A, 0xCC, 0xFF);

    private static async Task OpenAsync(string docPath, int page)
    {
        try
        {
            var abs = DocumentPathResolver.Resolve(docPath);
            if (string.IsNullOrWhiteSpace(abs)) return;

            var fileUri = new Uri(new Uri("file:///"), abs.Replace('\\', '/'));
            var withPage = new Uri(fileUri.ToString() + $"#page={page}");
            await Launcher.LaunchUriAsync(withPage);
        }
        catch
        {
            // silent (UX)
        }
    }

    /// <summary>
    /// Converts a tokenized message (containing [[open|..]] tokens) to the plain text
    /// that the user actually sees (labels only).
    /// Used for Copy-to-clipboard.
    /// </summary>
    public static string ToPlainText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var s = TokenRegex.Replace(raw, m =>
        {
            var label = (m.Groups["label"].Value ?? string.Empty).Trim();
            if (label.Length == 0)
                label = (m.Groups["path"].Value ?? string.Empty).Trim();
            return label;
        });

        s = s.Replace("\r\n", "\n").Replace("\r", "\n");
        return s;
    }

    private sealed class HandButton : Button
    {
        public HandButton()
        {
            try
            {
                ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
            }
            catch
            {
                // Cursor customization may fail on some environments; ignore.
            }
        }
    }
}
