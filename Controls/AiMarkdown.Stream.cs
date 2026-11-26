using System;
using System.Runtime.InteropServices;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using VNotch.Interop;
using MdInline = Markdig.Syntax.Inlines.Inline;

namespace VNotch.Controls;

internal static partial class AiMarkdown
{
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_markdown_reveal_prefix(ReadOnlySpan<char> text, int length, int words, out int end);

    internal static int RevealPrefixLength(ReadOnlySpan<char> text, int words)
    {
        if (text.IsEmpty || words >= text.Length) return text.Length;
        NativeResult.RequireSuccess(vn_markdown_reveal_prefix(text, text.Length, Math.Max(1, words), out int end),
            nameof(vn_markdown_reveal_prefix));
        return end;
    }

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_ai_markdown_close_stream_spans(
        ReadOnlySpan<char> md,
        int len,
        in VnMarkdownBoundarySummary summary,
        out int outInsertPos,
        Span<char> outCloser,
        int cap,
        out int outLen);
    internal sealed class StreamRenderer(bool uniformBold = false)
    {
        internal FlowDocument Document { get; } = Render("", uniformBold: uniformBold);
        private int _revealed, _committed, _stableBlocks;
        private Paragraph? _paragraph;
        private readonly List<(MdInline Source, Span View)> _inlines = [];

        internal bool Advance(string markdown, int words, double fadeMilliseconds)
        {
            int previous = _revealed;
            _revealed += RevealPrefixLength(markdown.AsSpan(previous), words);
            if (_revealed == previous) return _revealed == markdown.Length;
            string tail = markdown[_committed.._revealed];
            string balanced = CloseSpans(tail);
            var parsed = MarkdownWpfRenderer.Parse(balanced);
            // Only the final Markdown block can still change as more words arrive.
            // Preserve completed paragraphs, lists, tables and code blocks in WPF.
            int stable = Math.Max(0, parsed.Count - 1);
            int commit = stable > 0 ? Math.Min(tail.Length, parsed[stable - 1].Span.End + 1) : 0;
            if (parsed.Count == 1 && parsed[0] is ParagraphBlock { Inline: { } inlines } && UpdateParagraph(inlines, fadeMilliseconds))
                return _revealed == markdown.Length;
            _paragraph = null;
            _inlines.Clear();
            var rendered = RenderParsed(parsed, null, false, uniformBold);
            if (rendered.Blocks.LastBlock is { } last) FadeLastWord(last.ContentStart, last.ContentEnd, fadeMilliseconds);
            while (Document.Blocks.Count > _stableBlocks) Document.Blocks.Remove(Document.Blocks.LastBlock);
            while (rendered.Blocks.FirstBlock is { } block)
            {
                rendered.Blocks.Remove(block);
                Document.Blocks.Add(block);
            }
            _stableBlocks += stable;
            _committed += commit;
            return _revealed == markdown.Length;
        }
        // A closing delimiter may reinterpret earlier tokens. Reuse the WPF
        // prefix while its parsed text and formatting still match.
        private bool UpdateParagraph(ContainerInline content, double fade)
        {
            foreach (var node in content)
                if (node is not LiteralInline && !(node is EmphasisInline em && em.All(child => child is LiteralInline))) return false;
            if (_paragraph == null)
            {
                while (Document.Blocks.Count > _stableBlocks) Document.Blocks.Remove(Document.Blocks.LastBlock);
                _paragraph = new Paragraph { Margin = new System.Windows.Thickness(0, 0, 0, 8) };
                Document.Blocks.Add(_paragraph);
            }
            int index = 0;
            bool changed = false;
            foreach (var node in content)
            {
                if (index < _inlines.Count && SameInline(_inlines[index].Source, node))
                {
                    // Keep only the current parse tree. Old inline nodes also own
                    // their parent document and its entire source string.
                    _inlines[index] = (node, _inlines[index].View);
                    index++;
                    continue;
                }
                if (index == _inlines.Count - 1 && node is LiteralInline next &&
                    _inlines[index].Source is LiteralInline old && next.Content.AsSpan().StartsWith(old.Content.AsSpan(), StringComparison.Ordinal))
                {
                    var view = _inlines[index].View;
                    view.Inlines.Add(new Run(next.Content.AsSpan()[old.Content.Length..].ToString()));
                    _inlines[index++] = (next, view);
                }
                else
                {
                    RemoveSuffix(index);
                    var view = new Span();
                    MarkdownWpfRenderer.AddStreamInline(view.Inlines, node, uniformBold ? CachedBoldOptions : CachedMediumOptions);
                    _paragraph.Inlines.Add(view);
                    _inlines.Add((node, view));
                    index++;
                }
                changed = true;
            }
            RemoveSuffix(index);
            if (changed) FadeLastWord(_paragraph.ContentStart, _paragraph.ContentEnd, fade);
            return true;
        }

        private void RemoveSuffix(int keep)
        {
            for (int i = _inlines.Count - 1; i >= keep; i--)
            {
                _paragraph!.Inlines.Remove(_inlines[i].View);
                _inlines.RemoveAt(i);
            }
        }

        private static bool SameInline(MdInline previous, MdInline next)
        {
            if (previous is LiteralInline a && next is LiteralInline b) return a.Content.AsSpan().SequenceEqual(b.Content.AsSpan());
            if (previous is not EmphasisInline left || next is not EmphasisInline right ||
                left.DelimiterChar != right.DelimiterChar || left.DelimiterCount != right.DelimiterCount) return false;
            var x = left.FirstChild;
            var y = right.FirstChild;
            while (x != null && y != null && SameInline(x, y)) { x = x.NextSibling; y = y.NextSibling; }
            return x == null && y == null;
        }

        private static void FadeLastWord(TextPointer start, TextPointer end, double milliseconds)
        {
            if (milliseconds <= 0) return;
            for (var p = end; p != null && p.CompareTo(start) > 0; p = p.GetNextContextPosition(LogicalDirection.Backward))
            {
                if (p.GetPointerContext(LogicalDirection.Backward) != TextPointerContext.Text) continue;
                string text = p.GetTextInRun(LogicalDirection.Backward);
                int last = text.Length;
                while (last > 0 && char.IsWhiteSpace(text[last - 1])) last--;
                if (last == 0) continue;
                int first = last - 1;
                while (first > 0 && !char.IsWhiteSpace(text[first - 1])) first--;
                var ink = ((p.Parent as TextElement)?.Foreground as SolidColorBrush ?? SpotlightChatPalette.Primary).Clone();
                _ = new Span(p.GetPositionAtOffset(first - text.Length), p.GetPositionAtOffset(last - text.Length)) { Foreground = ink };
                ink.BeginAnimation(Brush.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(milliseconds))
                    { FillBehavior = FillBehavior.Stop });
                return;
            }
        }
    }

    internal static string CloseSpans(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return markdown;

        var summary = ScanSpanBoundaries(markdown);
        Span<char> closer = stackalloc char[264];
        if (vn_ai_markdown_close_stream_spans(
                markdown, markdown.Length, in summary,
                out int insertPos, closer, closer.Length, out int closerLen) != 0 ||
            closerLen <= 0)
        {
            return markdown;
        }

        return insertPos == markdown.Length
            ? string.Concat(markdown, closer[..closerLen])
            : string.Concat(markdown.AsSpan(0, insertPos), closer[..closerLen], markdown.AsSpan(insertPos));
    }
}
