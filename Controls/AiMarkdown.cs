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
    private static readonly MarkdownFlowOptions CachedBoldOptions = CreateAiFlowOptions(FallbackBodyFont, normalBody: false, uniformBold: true);

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

    private static MarkdownFlowOptions CreateAiFlowOptions(FontFamily font, bool normalBody, bool uniformBold = false)
    {
        var defaults = MarkdownFlowOptions.Default;
        return new()
        {
            BodyFont = font,
            BodyFontWeight = uniformBold ? FontWeights.Bold : normalBody ? FontWeights.Normal : FontWeights.Medium,
            CodeFont = uniformBold ? font : defaults.CodeFont,
            CodeFontWeight = uniformBold ? FontWeights.Bold : defaults.CodeFontWeight,
            BodyForeground = SpotlightChatPalette.Primary,
            CodeForeground = SpotlightChatPalette.Primary,
            CodeBackground = SpotlightChatPalette.Surface,
            InlineCodeForeground = SpotlightChatPalette.Primary,
            InlineCodeBackground = SpotlightChatPalette.Surface,
            QuoteForeground = SpotlightChatPalette.Secondary,
            LinkForeground = SpotlightChatPalette.Link,
            LinkMode = MarkdownLinkMode.LabelWithUrlSuffix
        };
    }

    private static MarkdownDocument GetOrParseDocument(string markdown)
    {
        if (string.Equals(_cachedMarkdownText, markdown, StringComparison.Ordinal) && _cachedMarkdownDoc != null)
            return _cachedMarkdownDoc;

        var parsed = MarkdownWpfRenderer.Parse(markdown);
        _cachedMarkdownText = markdown;
        _cachedMarkdownDoc = parsed;
        return parsed;
    }

    internal static string ToPlainTextPreview(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;

        // Saved previews may end inside emphasis or inline code after truncation.
        string text = Markdig.Markdown.ToPlainText(CloseSpans(markdown), MarkdownWpfRenderer.Pipeline);
        return string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    internal static FlowDocument Render(string markdown, FontFamily? fontFamily = null, bool normalBody = false,
        bool uniformBold = false)
    {
        var font = fontFamily
            ?? (FontFamily?)Application.Current?.TryFindResource("SFProDisplay")
            ?? FallbackBodyFont;

        MarkdownFlowOptions options = ReferenceEquals(font, FallbackBodyFont)
            ? (uniformBold ? CachedBoldOptions : normalBody ? CachedNormalOptions : CachedMediumOptions)
            : CreateAiFlowOptions(font, normalBody, uniformBold);

        return MarkdownWpfRenderer.RenderFlowDocument(GetOrParseDocument(markdown), options);
    }
}
