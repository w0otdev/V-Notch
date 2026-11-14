using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Windows.Documents;

namespace VNotch.Controls;

internal static partial class AiMarkdown
{
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_ai_markdown_close_stream_spans(
        ReadOnlySpan<char> md,
        int len,
        in VnMarkdownBoundarySummary summary,
        out int outInsertPos,
        Span<char> outCloser,
        int cap,
        out int outLen);
    internal sealed class StreamRenderer
    {
        internal FlowDocument Document { get; } = Render("");
        private int _revealed, _committed, _stableBlocks;

        internal bool Advance(string markdown, int words, double fadeMilliseconds)
        {
            int previous = _revealed, remaining = Math.Max(1, words);
            ReadOnlySpan<char> unrevealed = markdown.AsSpan(previous);
            if (!unrevealed.IsEmpty)
            {
                Span<VnMarkdownWordSlice> stackSlices = stackalloc VnMarkdownWordSlice[128];
                _ = vn_markdown_scan_word_spans(unrevealed, unrevealed.Length, stackSlices, stackSlices.Length, out int totalWords);
                if (totalWords <= remaining)
                {
                    _revealed = markdown.Length;
                }
                else if (remaining <= stackSlices.Length)
                {
                    var target = stackSlices[remaining - 1];
                    _revealed = previous + target.Start + target.Length;
                }
                else
                {
                    var rented = ArrayPool<VnMarkdownWordSlice>.Shared.Rent(remaining);
                    try
                    {
                        _ = vn_markdown_scan_word_spans(unrevealed, unrevealed.Length, rented, remaining, out _);
                        var target = rented[remaining - 1];
                        _revealed = previous + target.Start + target.Length;
                    }
                    finally
                    {
                        ArrayPool<VnMarkdownWordSlice>.Shared.Return(rented);
                    }
                }
            }
            if (_revealed == previous) return _revealed == markdown.Length;
            string tail = markdown[_committed.._revealed];
            string balanced = CloseSpans(tail);
            var parsed = GetOrParseDocument(balanced);
            // Only the final Markdown block can still change as more words arrive.
            // Preserve completed paragraphs, lists, tables and code blocks in WPF.
            int stable = Math.Max(0, parsed.Count - 1);
            int commit = stable > 0 ? Math.Min(tail.Length, parsed[stable - 1].Span.End + 1) : 0;
            var rendered = RenderWords(tail, int.MaxValue, out _, out _, fadeMilliseconds);
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
