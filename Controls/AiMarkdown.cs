using System.Buffers;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig.Syntax;

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
    internal struct VnMarkdownBoundarySummary
    {
        public byte HasUnclosedFence;
        public byte FenceChar;
        public byte FenceCount;
        public byte HasUnclosedInlineCode;
        public byte InlineCodeTicks;
        public byte HasUnclosedBold;
        public byte BoldChar;
        public byte HasUnclosedItalic;
        public byte ItalicChar;
        public byte BoldOuter;
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
        out VnMarkdownBoundarySummary outSummary);

    private static readonly FontFamily FallbackBodyFont = VNotch.Services.AppFonts.Get();
    private static readonly MarkdownFlowOptions CachedMediumOptions = CreateAiFlowOptions(FallbackBodyFont, normalBody: false);
    private static readonly MarkdownFlowOptions CachedNormalOptions = CreateAiFlowOptions(FallbackBodyFont, normalBody: true);

    [ThreadStatic]
    private static string? _cachedMarkdownText;

    [ThreadStatic]
    private static MarkdownDocument? _cachedMarkdownDoc;

    internal static int ScanWordSpans(
        ReadOnlySpan<char> text,
        Span<VnMarkdownWordSlice> outSpans,
        out int outWritten) =>
        vn_markdown_scan_word_spans(text, text.Length, outSpans, outSpans.Length, out outWritten);

    internal static VnMarkdownBoundarySummary ScanSpanBoundaries(ReadOnlySpan<char> markdown)
    {
        vn_markdown_scan_span_boundaries(markdown, markdown.Length, out var summary);
        return summary;
    }

    private static MarkdownFlowOptions CreateAiFlowOptions(FontFamily font, bool normalBody) =>
        new()
        {
            BodyFont = font,
            BodyFontWeight = normalBody ? FontWeights.Normal : FontWeights.Medium,
            LinkMode = MarkdownLinkMode.LabelWithUrlSuffix
        };

    private static MarkdownDocument GetOrParseDocument(string markdown)
    {
        if (string.Equals(_cachedMarkdownText, markdown, StringComparison.Ordinal) && _cachedMarkdownDoc != null)
            return _cachedMarkdownDoc;

        var parsed = MarkdownWpfRenderer.Parse(markdown);
        _cachedMarkdownText = markdown;
        _cachedMarkdownDoc = parsed;
        return parsed;
    }

    internal static FlowDocument Render(string markdown, FontFamily? fontFamily = null, bool normalBody = false)
    {
        var font = fontFamily
            ?? (FontFamily?)Application.Current?.TryFindResource("SFProDisplay")
            ?? FallbackBodyFont;

        MarkdownFlowOptions options = ReferenceEquals(font, FallbackBodyFont)
            ? (normalBody ? CachedNormalOptions : CachedMediumOptions)
            : CreateAiFlowOptions(font, normalBody);

        return MarkdownWpfRenderer.RenderFlowDocument(GetOrParseDocument(markdown), options);
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
}
