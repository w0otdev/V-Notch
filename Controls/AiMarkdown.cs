using System.Buffers;
using System.Runtime.InteropServices;
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
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct VnMarkdownWordSlice
    {
        public int Start;
        public int Length;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct VnMarkdownSpanBoundarySummary
    {
        public byte HasUnclosedFence;
        public byte HasUnclosedInlineCode;
        public byte HasUnclosedBold;
        public byte HasUnclosedItalic;
        public int FenceCount;
        public int InlineCodeCount;
        public int BoldMarkerCount;
        public int ItalicMarkerCount;
    }

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_markdown_scan_word_spans(
        ReadOnlySpan<char> textPtr,
        int textLen,
        Span<VnMarkdownWordSlice> outSpans,
        int spanCap,
        out int outWritten);

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_markdown_scan_span_boundaries(
        ReadOnlySpan<char> textPtr,
        int textLen,
        out VnMarkdownSpanBoundarySummary outSummary,
        Span<VnMarkdownWordSlice> outCodeSpans,
        int codeSpanCap,
        out int outCodeSpanCount);

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

    internal static VnMarkdownSpanBoundarySummary ScanSpanBoundaries(ReadOnlySpan<char> markdown)
    {
        vn_markdown_scan_span_boundaries(markdown, markdown.Length, out var summary, Span<VnMarkdownWordSlice>.Empty, 0, out _);
        return summary;
    }

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
            if (list.IsOrdered &&
                ushort.TryParse(list.OrderedStart, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ushort orderedNumber) &&
                orderedNumber > 0)
            {
                result.StartIndex = orderedNumber;
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is not ListItemBlock item) continue;
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
            for (int r = 0; r < table.Count; r++)
            {
                if (table[r] is not Markdig.Extensions.Tables.TableRow row) continue;
                var rendered = new System.Windows.Documents.TableRow();
                for (int c = 0; c < row.Count; c++)
                {
                    if (row[c] is not Markdig.Extensions.Tables.TableCell cell) continue;
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
        markdown = CloseSpans(markdown);
        var doc = Render(markdown);
        shown = 0;
        TextPointer? lastStart = null;
        TextPointer? cutoff = null;
        var fadingWords = new List<(TextPointer Start, TextPointer End, double Progress)>();
        var cursor = doc.ContentStart;
        caughtUp = true;
        Span<VnMarkdownWordSlice> stackWordSlices = stackalloc VnMarkdownWordSlice[128];

        while (cursor != null && cursor.CompareTo(doc.ContentEnd) < 0)
        {
            if (cursor.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                string text = cursor.GetTextInRun(LogicalDirection.Forward);
                ReadOnlySpan<char> textSpan = text.AsSpan();
                VnMarkdownWordSlice[]? rentedSlices = null;
                Span<VnMarkdownWordSlice> wordSlices = stackWordSlices;
                int rc = vn_markdown_scan_word_spans(textSpan, textSpan.Length, wordSlices, wordSlices.Length, out int wordCount);
                if (rc == -2 && wordCount > wordSlices.Length)
                {
                    rentedSlices = ArrayPool<VnMarkdownWordSlice>.Shared.Rent(wordCount);
                    wordSlices = rentedSlices.AsSpan(0, wordCount);
                    vn_markdown_scan_word_spans(textSpan, textSpan.Length, wordSlices, wordSlices.Length, out wordCount);
                }

                try
                {
                    for (int i = 0; i < wordCount; i++)
                    {
                        var word = wordSlices[i];
                        if (shown == wordLimit) { caughtUp = false; break; }
                        lastStart = cursor.GetPositionAtOffset(word.Start);
                        cutoff = cursor.GetPositionAtOffset(word.Start + word.Length);
                        if (revealTimes != null)
                        {
                            if (shown >= revealTimes.Count) revealTimes.Add(nowMilliseconds);
                            double progress = Math.Clamp((nowMilliseconds - revealTimes[shown]) / fadeMilliseconds, 0, 1);
                            if (progress < 1 && lastStart != null && cutoff != null)
                                fadingWords.Add((lastStart, cutoff, progress));
                        }
                        shown++;
                    }
                }
                finally
                {
                    if (rentedSlices != null)
                    {
                        ArrayPool<VnMarkdownWordSlice>.Shared.Return(rentedSlices);
                    }
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
