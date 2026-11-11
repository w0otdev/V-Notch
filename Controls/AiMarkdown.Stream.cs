using System.Buffers;
using System.Windows.Documents;

namespace VNotch.Controls;

internal static partial class AiMarkdown
{
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
        if (summary.HasUnclosedFence != 0)
        {
            char fenceChar = summary.FenceChar != 0 ? (char)summary.FenceChar : '`';
            int fenceCount = Math.Max(3, (int)summary.FenceCount);
            return markdown + "\n" + new string(fenceChar, fenceCount);
        }

        if (summary.HasUnclosedInlineCode != 0)
        {
            int ticks = Math.Max(1, (int)summary.InlineCodeTicks);
            string closer = new string('`', ticks);
            return markdown[^1] == '`' ? markdown + " " + closer : markdown + closer;
        }

        if (summary.HasUnclosedBold == 0 && summary.HasUnclosedItalic == 0)
        {
            return markdown;
        }

        string boldCloser = summary.HasUnclosedBold != 0
            ? new string(summary.BoldChar != 0 ? (char)summary.BoldChar : '*', 2)
            : string.Empty;
        string italicCloser = summary.HasUnclosedItalic != 0
            ? new string(summary.ItalicChar != 0 ? (char)summary.ItalicChar : '*', 1)
            : string.Empty;
        string emphasisCloser = summary.BoldOuter != 0
            ? italicCloser + boldCloser
            : boldCloser + italicCloser;

        int trimEnd = markdown.Length;
        while (trimEnd > 0 && char.IsWhiteSpace(markdown[trimEnd - 1]))
        {
            trimEnd--;
        }

        if (trimEnd == 0) return markdown;
        return trimEnd == markdown.Length
            ? markdown + emphasisCloser
            : string.Concat(markdown.AsSpan(0, trimEnd), emphasisCloser, markdown.AsSpan(trimEnd));
    }
}
