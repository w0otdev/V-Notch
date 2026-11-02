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
            foreach (var word in WordSpanRegex().EnumerateMatches(markdown.AsSpan(_revealed)))
            {
                _revealed = previous + word.Index + word.Length;
                if (--remaining == 0) break;
            }
            if (remaining > 0) _revealed = markdown.Length;
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

    private static string CloseSpans(string markdown)
    {
        if (FenceRegex().Count(markdown) % 2 != 0) return markdown + "\n```";
        string outsideCode = CodeSpanRegex().Replace(markdown, "");
        return BoldMarkerRegex().Count(outsideCode) % 2 != 0 ? markdown + "**" : markdown;
    }
}
