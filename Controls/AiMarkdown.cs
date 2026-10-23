using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdTable = Markdig.Extensions.Tables.Table;
using WpfBlock = System.Windows.Documents.Block;

namespace VNotch.Controls;

internal static partial class AiMarkdown
{
    private static readonly Uri FontsBaseUri = VNotch.Services.AppFonts.BaseUri;
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().UseEmphasisExtras().Build();
    private static readonly FontFamily FallbackBodyFont = new(FontsBaseUri, "./#SF Pro Display, Segoe UI Emoji, Segoe UI, Arial");
    private static readonly FontFamily CodeBlockFont = new("Consolas, SF Mono, Menlo, monospace");
    private static readonly SolidColorBrush BodyForegroundBrush = CreateFrozenBrush(Color.FromRgb(200, 200, 200));
    private static readonly SolidColorBrush CodeForegroundBrush = CreateFrozenBrush(Color.FromRgb(226, 232, 240));
    private static readonly SolidColorBrush CodeBackgroundBrush = CreateFrozenBrush(Color.FromArgb(200, 20, 20, 26));
    private static readonly SolidColorBrush CodeBorderBrush = CreateFrozenBrush(Color.FromArgb(40, 255, 255, 255));
    private static readonly SolidColorBrush TableCellBorderBrush = CreateFrozenBrush(Color.FromArgb(50, 255, 255, 255));
    private static readonly SolidColorBrush InlineCodeForegroundBrush = CreateFrozenBrush(Color.FromRgb(225, 225, 225));
    private static readonly SolidColorBrush InlineCodeBackgroundBrush = CreateFrozenBrush(Color.FromArgb(36, 255, 255, 255));
    private static readonly SolidColorBrush QuoteBorderBrush = CreateFrozenBrush(Color.FromRgb(85, 85, 85));
    private static readonly SolidColorBrush QuoteForegroundBrush = CreateFrozenBrush(Color.FromArgb(215, 255, 255, 255));

    [ThreadStatic]
    private static string? _cachedMarkdownText;

    [ThreadStatic]
    private static MarkdownDocument? _cachedMarkdownDoc;

    [System.Text.RegularExpressions.GeneratedRegex(@"(?m)^\s*```")]
    private static partial System.Text.RegularExpressions.Regex FenceRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?s)```.*?```|`[^`]*`")]
    private static partial System.Text.RegularExpressions.Regex CodeSpanRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<!\\)\*\*")]
    private static partial System.Text.RegularExpressions.Regex BoldMarkerRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\S+\s*")]
    private static partial System.Text.RegularExpressions.Regex WordSpanRegex();

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static MarkdownDocument GetOrParseDocument(string markdown)
    {
        if (string.Equals(_cachedMarkdownText, markdown, StringComparison.Ordinal) && _cachedMarkdownDoc != null)
            return _cachedMarkdownDoc;

        var parsed = Markdown.Parse(markdown, Pipeline);
        _cachedMarkdownText = markdown;
        _cachedMarkdownDoc = parsed;
        return parsed;
    }

    internal static FlowDocument Render(string markdown, FontFamily? fontFamily = null, bool normalBody = false)
    {
        var font = fontFamily
            ?? (FontFamily?)Application.Current?.TryFindResource("SFProDisplay")
            ?? FallbackBodyFont;

        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontSize = 13.5,
            FontWeight = normalBody ? FontWeights.Normal : FontWeights.Bold,
            FontFamily = font,
            LineHeight = 22,
            Foreground = BodyForegroundBrush,
            Background = Brushes.Transparent
        };
        foreach (var block in GetOrParseDocument(markdown)) doc.Blocks.Add(RenderBlock(block, font, normalBody));
        return doc;
    }

    private static WpfBlock RenderBlock(Markdig.Syntax.Block block, FontFamily font, bool normalBody = false)
    {
        if (block is CodeBlock code)
            return new Paragraph(new Run(code.Lines.ToString()))
            {
                FontFamily = CodeBlockFont,
                FontSize = 12.5,
                FontWeight = FontWeights.Normal,
                Foreground = CodeForegroundBrush,
                Background = CodeBackgroundBrush,
                BorderBrush = CodeBorderBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 6, 0, 10)
            };
        if (block is ListBlock list)
        {
            var result = new System.Windows.Documents.List
            {
                FontFamily = font,
                FontWeight = normalBody ? FontWeights.Normal : FontWeights.Bold,
                MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                Margin = new Thickness(0, 4, 0, 8),
                Padding = new Thickness(22, 0, 0, 0)
            };
            if (list.IsOrdered && int.TryParse(list.OrderedStart, out int start) && start > 0) result.StartIndex = start;
            foreach (var item in list.OfType<ListItemBlock>())
            {
                var li = new ListItem { FontFamily = font, FontWeight = normalBody ? FontWeights.Normal : FontWeights.Bold };
                foreach (var child in item) li.Blocks.Add(RenderBlock(child, font, normalBody));
                result.ListItems.Add(li);
            }
            return result;
        }
        if (block is MdTable table)
        {
            var result = new System.Windows.Documents.Table { FontFamily = font, FontWeight = normalBody ? FontWeights.Normal : FontWeights.Bold, CellSpacing = 0, Margin = new Thickness(0, 6, 0, 10) };
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
                        BorderBrush = TableCellBorderBrush,
                        BorderThickness = new Thickness(0.5),
                        FontWeight = normalBody ? FontWeights.Normal : FontWeights.Bold
                    };
                    foreach (var child in cell) target.Blocks.Add(RenderBlock(child, font, normalBody));
                    rendered.Cells.Add(target);
                }
                rows.Rows.Add(rendered);
            }
            return result;
        }
        if (block is ThematicBreakBlock)
            return new Paragraph
            {
                BorderBrush = CodeBorderBrush,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Margin = new Thickness(0, 8, 0, 8)
            };
        if (block is ContainerBlock container)
        {
            var section = new Section { FontFamily = font, FontWeight = normalBody ? FontWeights.Normal : FontWeights.Bold };
            if (block is QuoteBlock)
            {
                section.BorderBrush = QuoteBorderBrush;
                section.BorderThickness = new Thickness(3, 0, 0, 0);
                section.Padding = new Thickness(12, 4, 4, 4);
                section.Foreground = QuoteForegroundBrush;
            }
            foreach (var child in container) section.Blocks.Add(RenderBlock(child, font, normalBody));
            return section;
        }
        var p = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, 8),
            FontFamily = font,
            FontWeight = normalBody ? FontWeights.Normal : FontWeights.Bold
        };
        if (block is HeadingBlock heading)
        {
            p.FontSize = Math.Max(14, 20 - (heading.Level - 1) * 2);
            p.FontWeight = FontWeights.Bold;
            p.Foreground = BodyForegroundBrush;
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
        var fenceCount = FenceRegex().Count(markdown);
        if (fenceCount % 2 != 0) markdown += "\n```";
        else
        {
            string outsideCode = CodeSpanRegex().Replace(markdown, "");
            if (BoldMarkerRegex().Count(outsideCode) % 2 != 0)
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
                foreach (var word in WordSpanRegex().EnumerateMatches(text.AsSpan()))
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
                case HtmlEntityInline entity: target.Add(new Run(entity.Transcoded.ToString())); break;
                case AutolinkInline autolink: target.Add(new Run(autolink.Url)); break;
                case CodeInline code:
                    target.Add(new Run(code.Content)
                    {
                        FontFamily = CodeBlockFont,
                        FontSize = 12,
                        Foreground = InlineCodeForegroundBrush,
                        Background = InlineCodeBackgroundBrush
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
                    var label = new Span { Foreground = BodyForegroundBrush };
                    AddInlines(label.Inlines, link);
                    if (!string.IsNullOrEmpty(link.Url) && !VNotch.Services.Translation.TranslationStructuredText.IsSocialIcon(link)) label.Inlines.Add(new Run(" (" + link.Url + ")"));
                    target.Add(label); break;
                case ContainerInline nested: AddInlines(target, nested); break;
            }
        }
    }
}
