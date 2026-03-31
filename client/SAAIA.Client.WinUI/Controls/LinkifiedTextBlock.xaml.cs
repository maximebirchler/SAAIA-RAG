using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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
