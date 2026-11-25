using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace TURZX.WinUsbDisplay.Studio;

/// <summary>按 Studio 文档面板样式将 Markdown 转为 WPF 文档。</summary>
internal static partial class MarkdownPreview
{
    /// <summary>文档强调内容使用的画刷。</summary>
    private static readonly Brush Accent = BrushOf("#A8BFFF");
    /// <summary>文档面板区域的背景画刷。</summary>
    private static readonly Brush Panel = BrushOf("#202C44");
    /// <summary>文档分隔线使用的画刷。</summary>
    private static readonly Brush Rule = BrushOf("#394861");
    /// <summary>内联代码和代码块使用的等宽字体。</summary>
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Microsoft YaHei UI");

    /// <summary>将 Markdown 标题、段落和代码等内容呈现为流文档。</summary>
    internal static FlowDocument Render(string markdown, Action<string> openLink)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(20, 12, 20, 18),
            FontSize = 14,
            LineHeight = 23,
            Foreground = BrushOf("#E1E9FC"),
            ColumnWidth = double.PositiveInfinity
        };
        string[] lines = markdown.ReplaceLineEndings("\n").Split('\n');
        for (int index = 0; index < lines.Length;)
        {
            string line = lines[index++];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                var code = new StringBuilder();
                while (index < lines.Length && !lines[index].StartsWith("```", StringComparison.Ordinal))
                {
                    if (code.Length > 0)
                        code.Append('\n');
                    code.Append(lines[index++]);
                }
                if (index < lines.Length)
                    index++;
                document.Blocks.Add(new Paragraph(new Run(code.ToString()))
                {
                    FontFamily = Mono, FontSize = 12, Background = Panel, Padding = new Thickness(12),
                    Margin = new Thickness(0, 6, 0, 12)
                });
                continue;
            }

            if (line.StartsWith('|') && index < lines.Length && TableDivider().IsMatch(lines[index]))
            {
                index++;
                string[] headers = Cells(line);
                var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 6, 0, 14) };
                double[] widths = headers.Length switch
                {
                    4 => [0.55, 1.75, 3.45, 1.1],
                    3 => [1, 2, 2],
                    2 => [1, 3],
                    _ => Enumerable.Repeat(1d, headers.Length).ToArray()
                };
                foreach (double width in widths)
                    table.Columns.Add(new TableColumn { Width = new GridLength(width, GridUnitType.Star) });
                var rows = new TableRowGroup();
                rows.Rows.Add(CreateRow(headers, headers.Length, true, openLink));
                while (index < lines.Length && lines[index].StartsWith('|'))
                    rows.Rows.Add(CreateRow(Cells(lines[index++]), headers.Length, false, openLink));
                table.RowGroups.Add(rows);
                document.Blocks.Add(table);
                continue;
            }

            int heading = line.TakeWhile(character => character == '#').Count();
            if (heading is > 0 and <= 6 && heading < line.Length && line[heading] == ' ')
            {
                var title = ParagraphOf(line[(heading + 1)..], openLink);
                title.FontSize = heading == 1 ? 26 : heading == 2 ? 20 : 16;
                title.FontWeight = FontWeights.SemiBold;
                title.LineHeight = title.FontSize + 9;
                title.Foreground = heading == 1 ? Brushes.White : Accent;
                title.Margin = new Thickness(0, heading == 1 ? 4 : 15, 0, 6);
                document.Blocks.Add(title);
                continue;
            }

            var item = ListItemPattern().Match(line);
            if (item.Success)
            {
                bool ordered = item.Groups[1].Value.EndsWith('.');
                var list = new List
                {
                    MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(0, 4, 0, 10), Padding = new Thickness(23, 0, 0, 0)
                };
                do
                {
                    list.ListItems.Add(new ListItem(ParagraphOf(item.Groups[2].Value, openLink)));
                    if (index >= lines.Length)
                        break;
                    item = ListItemPattern().Match(lines[index]);
                    if (!item.Success || item.Groups[1].Value.EndsWith('.') != ordered)
                        break;
                    index++;
                } while (true);
                document.Blocks.Add(list);
                continue;
            }

            document.Blocks.Add(ParagraphOf(line, openLink));
        }
        return document;
    }

    /// <summary>在源文本中查找下一处匹配。</summary>
    internal static bool FindNext(RichTextBox viewer, string query)
    {
        var text = new StringBuilder();
        var runs = new List<(int Offset, int Length, TextPointer Position)>();
        int start = -1;
        for (TextPointer? position = viewer.Document.ContentStart; position is not null;)
        {
            var context = position.GetPointerContext(LogicalDirection.Forward);
            if (context == TextPointerContext.Text)
            {
                string run = position.GetTextInRun(LogicalDirection.Forward);
                if (start < 0 && position.CompareTo(viewer.Selection.End) >= 0)
                    start = text.Length;
                else if (start < 0 && position.GetPositionAtOffset(run.Length) is { } end &&
                    end.CompareTo(viewer.Selection.End) >= 0)
                    start = text.Length + position.GetOffsetToPosition(viewer.Selection.End);
                runs.Add((text.Length, run.Length, position));
                text.Append(run);
            }
            else if (context == TextPointerContext.ElementEnd && position.GetAdjacentElement(LogicalDirection.Forward) is Paragraph)
                text.Append('\n');

            position = position.GetNextContextPosition(LogicalDirection.Forward);
        }

        string value = text.ToString();
        int index = value.IndexOf(query, start < 0 ? value.Length : start, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            index = value.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return false;

        TextPointer? first = Locate(index);
        TextPointer? last = Locate(index + query.Length - 1)?.GetPositionAtOffset(1);
        if (first is null || last is null)
            return false;
        viewer.Focus();
        viewer.Selection.Select(first, last);
        first.Paragraph?.BringIntoView();
        return true;

        // 将匹配文本中的字符偏移映射到 WPF 文档位置。
        TextPointer? Locate(int offset)
        {
            foreach (var run in runs)
                if (offset >= run.Offset && offset < run.Offset + run.Length)
                    return run.Position.GetPositionAtOffset(offset - run.Offset);
            return null;
        }
    }

    /// <summary>将 Markdown 表格的单行转换为 WPF 表格行。</summary>
    private static TableRow CreateRow(string[] values, int columns, bool header, Action<string> openLink)
    {
        var row = new TableRow();
        for (int index = 0; index < columns; index++)
        {
            var paragraph = ParagraphOf(index < values.Length ? values[index] : "", openLink);
            paragraph.FontSize = 12;
            paragraph.LineHeight = 19;
            paragraph.Margin = new Thickness(0);
            row.Cells.Add(new TableCell(paragraph)
            {
                Padding = new Thickness(7, 5, 7, 5), BorderBrush = Rule, BorderThickness = new Thickness(0, 0, 1, 1),
                Background = header ? Panel : Brushes.Transparent,
                FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal
            });
        }
        return row;
    }

    /// <summary>从一段文本构造支持内联格式的文档段落。</summary>
    private static Paragraph ParagraphOf(string text, Action<string> openLink)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 2, 0, 7) };
        int offset = 0;
        foreach (Match match in InlinePattern().Matches(text))
        {
            paragraph.Inlines.Add(new Run(text[offset..match.Index]));
            if (match.Groups[1].Success)
                paragraph.Inlines.Add(new Run(match.Groups[1].Value) { FontFamily = Mono, Foreground = Accent });
            else if (match.Groups[2].Success)
                paragraph.Inlines.Add(new Bold(new Run(match.Groups[2].Value)));
            else
            {
                var link = new Hyperlink(new Run(match.Groups[3].Value)) { Foreground = Accent };
                link.Click += (_, _) => openLink(match.Groups[4].Value);
                paragraph.Inlines.Add(link);
            }
            offset = match.Index + match.Length;
        }
        paragraph.Inlines.Add(new Run(text[offset..]));
        return paragraph;
    }

    /// <summary>按 Markdown 表格语法拆分一行的单元格。</summary>
    private static string[] Cells(string line) => line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();

    /// <summary>从样式指定的颜色值创建画刷。</summary>
    private static Brush BrushOf(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    /// <summary>匹配 Markdown 表格的表头分隔行。</summary>
    [GeneratedRegex(@"^\s*\|(?:\s*:?-{3,}:?\s*\|)+\s*$")]
    private static partial Regex TableDivider();

    /// <summary>匹配 Markdown 列表项。</summary>
    [GeneratedRegex(@"^\s*([-*]|\d+\.)\s+(.+)$")]
    private static partial Regex ListItemPattern();

    /// <summary>匹配正文中支持的内联标记。</summary>
    [GeneratedRegex(@"\x60([^\x60]+)\x60|\*\*([^*]+)\*\*|\[([^\]]+)\]\(([^)]+)\)")]
    private static partial Regex InlinePattern();
}
