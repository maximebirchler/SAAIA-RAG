using System;
using System.Text.RegularExpressions;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace SAAIA.Client.WinUI.Controls;

public sealed partial class LinkifiedTextBlock
{
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
        _ = linkAlphaMultiplier;
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

    private static SolidColorBrush CreateBrush(byte a, byte r, byte g, byte b)
        => new(global::Windows.UI.Color.FromArgb(a, r, g, b));

    private static Brush CreateDocLinkNormalBrush() => CreateBrush(0xFF, 0x8A, 0xCC, 0xFF);
    private static Brush CreateDocLinkHoverBrush() => CreateBrush(0xFF, 0xBF, 0xE3, 0xFF);
    private static Brush CreateDocLinkPressedBrush() => CreateBrush(0xFF, 0x6E, 0xB8, 0xF0);
    private static Brush CreateDocLinkHoverBackground() => CreateBrush(0x32, 0x8A, 0xCC, 0xFF);
    private static Brush CreateDocLinkPressedBackground() => CreateBrush(0x46, 0x6E, 0xB8, 0xF0);
    private static Brush CreateDocLinkHoverBorderBrush() => CreateBrush(0x7A, 0x8A, 0xCC, 0xFF);
}
