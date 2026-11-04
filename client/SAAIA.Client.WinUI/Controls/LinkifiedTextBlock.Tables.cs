using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SAAIA.Client.Core.Presentation;

namespace SAAIA.Client.WinUI.Controls;

public sealed partial class LinkifiedTextBlock
{
    private void RenderDocumentLines(IReadOnlyList<string> lines, Brush foreground, double fontSize,
        bool singleSourceBlock, IReadOnlyDictionary<int, MarkdownTable> tables)
    {
        for (var index = 0; index < lines.Count;)
        {
            if (tables.TryGetValue(index, out var table))
            {
                LinesPanel.Children.Add(BuildTable(table, foreground, fontSize));
                index += table.ConsumedLineCount;
            }
            else RenderLine(lines[index++], foreground, fontSize, singleSourceBlock);
        }
    }

    private FrameworkElement BuildTable(MarkdownTable table, Brush foreground, double fontSize)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 8) };
        for (var column = 0; column < table.Headers.Count; column++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(column == 0 ? 130 : 180) });
        for (var row = 0; row <= table.Rows.Count; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var values = row == 0 ? table.Headers : table.Rows[row - 1];
            for (var column = 0; column < values.Count; column++)
            {
                var text = BuildRichTextElement(values[column], foreground, fontSize, InlineLinkAlphaMultiplier);
                if (row == 0 && text is RichTextBlock header) header.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                var cell = new Border
                {
                    Child = text, Padding = new Thickness(8), BorderThickness = new Thickness(0.5),
                    BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0x55, 0x88, 0x88, 0x88)),
                    Background = row == 0 ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(0x14, 0x88, 0x88, 0x88)) : null
                };
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                grid.Children.Add(cell);
            }
        }
        return new ScrollViewer
        {
            Content = grid, HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
    }
}
