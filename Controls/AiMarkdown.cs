using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdTable = Markdig.Extensions.Tables.Table;
using WpfBlock = System.Windows.Documents.Block;

namespace VNotch.Controls;

internal static class AiMarkdown
{
    private static readonly Uri FontsBaseUri = new("pack://application:,,,/V-Notch;component/Fonts/");
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().UseEmphasisExtras().Build();

    internal static FlowDocument Render(string markdown, FontFamily? fontFamily = null)
    {
        var font = fontFamily
            ?? (FontFamily?)Application.Current?.TryFindResource("SFProDisplay")
            ?? new FontFamily(FontsBaseUri, "./#SF Pro Display, Segoe UI, Arial");

        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontSize = 13.5,
            FontWeight = FontWeights.Bold,
            FontFamily = font,
            LineHeight = 22,
            Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
            Background = Brushes.Transparent
        };
        foreach (var block in Markdown.Parse(markdown, Pipeline)) doc.Blocks.Add(RenderBlock(block, font));
        return doc;
    }

    private static WpfBlock RenderBlock(Markdig.Syntax.Block block, FontFamily font)
    {
        if (block is CodeBlock code)
            return new Paragraph(new Run(code.Lines.ToString()))
            {
                FontFamily = new FontFamily("Consolas, SF Mono, Menlo, monospace"),
                FontSize = 12.5,
                FontWeight = FontWeights.Normal,
                Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                Background = new SolidColorBrush(Color.FromArgb(200, 20, 20, 26)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 6, 0, 10)
            };
        if (block is ListBlock list)
        {
            var result = new System.Windows.Documents.List
            {
                FontFamily = font,
                FontWeight = FontWeights.Bold,
                MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                Margin = new Thickness(0, 4, 0, 8),
                Padding = new Thickness(22, 0, 0, 0)
            };
            if (list.IsOrdered && int.TryParse(list.OrderedStart, out int start) && start > 0) result.StartIndex = start;
            foreach (var item in list.OfType<ListItemBlock>())
            {
                var li = new ListItem { FontFamily = font, FontWeight = FontWeights.Bold };
                foreach (var child in item) li.Blocks.Add(RenderBlock(child, font));
                result.ListItems.Add(li);
            }
            return result;
        }
        if (block is MdTable table)
        {
            var result = new System.Windows.Documents.Table { FontFamily = font, FontWeight = FontWeights.Bold, CellSpacing = 0, Margin = new Thickness(0, 6, 0, 10) };
            var rows = new TableRowGroup();
            result.RowGroups.Add(rows);
            foreach (var row in table.OfType<Markdig.Extensions.Tables.TableRow>())
            {
                var rendered = new System.Windows.Documents.TableRow();
                foreach (var cell in row.OfType<Markdig.Extensions.Tables.TableCell>())
                {
                    var target = new System.Windows.Documents.TableCell
                    {
                        FontFamily = font,
                        Padding = new Thickness(6),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
                        BorderThickness = new Thickness(0.5),
                        FontWeight = FontWeights.Bold
                    };
                    foreach (var child in cell) target.Blocks.Add(RenderBlock(child, font));
                    rendered.Cells.Add(target);
                }
                rows.Rows.Add(rendered);
            }
            return result;
        }
        if (block is ThematicBreakBlock)
            return new Paragraph
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Margin = new Thickness(0, 8, 0, 8)
            };
        if (block is ContainerBlock container)
        {
            var section = new Section { FontFamily = font, FontWeight = FontWeights.Bold };
            if (block is QuoteBlock)
            {
                section.BorderBrush = new SolidColorBrush(Color.FromRgb(85, 85, 85));
                section.BorderThickness = new Thickness(3, 0, 0, 0);
                section.Padding = new Thickness(12, 4, 4, 4);
                section.Foreground = new SolidColorBrush(Color.FromArgb(215, 255, 255, 255));
            }
            foreach (var child in container) section.Blocks.Add(RenderBlock(child, font));
            return section;
        }
        var p = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, 8),
            FontFamily = font,
            FontWeight = FontWeights.Bold
        };
        if (block is HeadingBlock heading)
        {
            p.FontSize = Math.Max(14, 20 - (heading.Level - 1) * 2);
            p.FontWeight = FontWeights.Bold;
            p.Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200));
            p.Margin = new Thickness(0, 10, 0, 4);
        }
        if (block is LeafBlock leaf && leaf.Inline != null) AddInlines(p.Inlines, leaf.Inline);
        return p;
    }

    // Parse first, then trim the formatted document: Markdown delimiters never become printed text.
    internal static FlowDocument RenderWords(string markdown, int wordLimit, out int shown,
        out bool caughtUp, double fadeMilliseconds, List<double>? revealTimes = null, double nowMilliseconds = 0)
    {
        // Temporarily close common unfinished spans while the next network chunk is pending.
        // These closers are presentation-only and never change the saved response.
        var fenceCount = System.Text.RegularExpressions.Regex.Matches(markdown, @"(?m)^\s*```").Count;
        if (fenceCount % 2 != 0) markdown += "\n```";
        else
        {
            string outsideCode = System.Text.RegularExpressions.Regex.Replace(markdown,
                @"(?s)```.*?```|`[^`]*`", "");
            if (System.Text.RegularExpressions.Regex.Matches(outsideCode, @"(?<!\\)\*\*").Count % 2 != 0)
                markdown += "**";
        }
        var doc = Render(markdown);
        shown = 0;
        TextPointer? lastStart = null;
        TextPointer? cutoff = null;
        var fadingWords = new List<(TextPointer Start, TextPointer End, double Progress)>();
        var cursor = doc.ContentStart;
        caughtUp = true;
        while (cursor != null && cursor.CompareTo(doc.ContentEnd) < 0)
        {
            if (cursor.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                string text = cursor.GetTextInRun(LogicalDirection.Forward);
                foreach (System.Text.RegularExpressions.Match word in System.Text.RegularExpressions.Regex.Matches(text, @"\S+\s*"))
                {
                    if (shown == wordLimit) { caughtUp = false; break; }
                    lastStart = cursor.GetPositionAtOffset(word.Index);
                    cutoff = cursor.GetPositionAtOffset(word.Index + word.Length);
                    if (revealTimes != null)
                    {
                        if (shown >= revealTimes.Count) revealTimes.Add(nowMilliseconds);
                        double progress = Math.Clamp((nowMilliseconds - revealTimes[shown]) / fadeMilliseconds, 0, 1);
                        if (progress < 1 && lastStart != null && cutoff != null)
                            fadingWords.Add((lastStart, cutoff, progress));
                    }
                    shown++;
                }
                if (!caughtUp) break;
            }
            cursor = cursor.GetNextContextPosition(LogicalDirection.Forward);
        }
        if (cutoff != null && lastStart != null)
        {
            if (!caughtUp) new TextRange(cutoff, doc.ContentEnd).Text = "";
            if (revealTimes == null) fadingWords.Add((lastStart, cutoff, 0));
            // Rebuilt documents resume each word's own fade instead of restarting or dropping it.
            foreach (var word in fadingWords)
            {
                var range = new TextRange(word.Start, word.End);
                var foreground = range.GetPropertyValue(TextElement.ForegroundProperty) as SolidColorBrush;
                // Explicit inline boundaries prevent WPF from merging same-colored
                // words into a shared Run and fading the whole sentence.
                var ink = new SolidColorBrush(foreground?.Color ?? VNotch.Services.UiPalette.PrimaryColor)
                { Opacity = 1 };
                _ = new Span(word.Start, word.End) { Foreground = ink };
                ink.BeginAnimation(Brush.OpacityProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(word.Progress, 1,
                        TimeSpan.FromMilliseconds(fadeMilliseconds * (1 - word.Progress)))
                    { FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop });
            }
        }

        return doc;
    }

    private static void AddInlines(InlineCollection target, ContainerInline container)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal: target.Add(new Run(literal.Content.ToString())); break;
                case CodeInline code:
                    target.Add(new Run(code.Content)
                    {
                        FontFamily = new FontFamily("Consolas, SF Mono, Menlo, monospace"),
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Color.FromRgb(225, 225, 225)),
                        Background = new SolidColorBrush(Color.FromArgb(36, 255, 255, 255))
                    });
                    break;
                case LineBreakInline: target.Add(new LineBreak()); break;
                case EmphasisInline emphasis:
                    var span = new Span();
                    if (emphasis.DelimiterChar == '~') span.TextDecorations = TextDecorations.Strikethrough;
                    else if (emphasis.DelimiterCount >= 2) span.FontWeight = FontWeights.Bold;
                    else span.FontStyle = FontStyles.Italic;
                    AddInlines(span.Inlines, emphasis); target.Add(span); break;
                case LinkInline link:
                    // Render labels without fetching remote images or executing generated links.
                    var label = new Span { Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)) };
                    AddInlines(label.Inlines, link);
                    if (!string.IsNullOrEmpty(link.Url)) label.Inlines.Add(new Run(" (" + link.Url + ")"));
                    target.Add(label); break;
                case ContainerInline nested: AddInlines(target, nested); break;
            }
        }
    }
}
