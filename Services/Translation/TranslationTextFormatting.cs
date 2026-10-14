namespace VNotch.Services.Translation;

internal static class TranslationTextFormatting
{
    // Only change paragraph separators; never rewrite translated words or punctuation.
    // Preserve internal line breaks within Markdown fenced code blocks.
    internal static string Format(string text)
    {
        var paragraphs = new List<string>();
        var currentBlock = new List<string>();
        char fenceChar = '\0';
        int fenceLength = 0;
        int lastKind = 0; // 1 = table, 2 = list, 3 = blockquote

        void FlushGroup()
        {
            if (currentBlock.Count > 0)
            {
                paragraphs.Add(string.Join("\n", currentBlock));
                currentBlock.Clear();
            }
            lastKind = 0;
        }

        foreach (ReadOnlySpan<char> rawLine in text.AsSpan().EnumerateLines())
        {
            ReadOnlySpan<char> trimmed = rawLine.Trim();

            if (fenceLength > 0)
            {
                string line = rawLine.ToString();
                currentBlock.Add(line);
                if (IsClosingFence(trimmed, fenceChar, fenceLength))
                {
                    paragraphs.Add(string.Join("\n", currentBlock));
                    currentBlock.Clear();
                    fenceChar = '\0';
                    fenceLength = 0;
                }
                continue;
            }

            if (TryGetOpeningFence(trimmed, out char openChar, out int openLen))
            {
                FlushGroup();
                fenceChar = openChar;
                fenceLength = openLen;
                currentBlock.Add(rawLine.ToString());
                continue;
            }

            if (trimmed.IsEmpty)
            {
                FlushGroup();
                continue;
            }

            int kind = ClassifyBlockLine(trimmed);
            if (kind != 0)
            {
                if (lastKind != 0 && lastKind != kind) FlushGroup();
                lastKind = kind;
                currentBlock.Add(rawLine.ToString());
            }
            else
            {
                FlushGroup();
                paragraphs.Add(rawLine.ToString());
            }
        }

        FlushGroup();
        return string.Join("\n\n", paragraphs);
    }

    private static bool TryGetOpeningFence(ReadOnlySpan<char> trimmed, out char fenceChar, out int fenceLength)
    {
        fenceChar = '\0';
        fenceLength = 0;
        if (trimmed.Length < 3) return false;
        char first = trimmed[0];
        if (first is not ('`' or '~')) return false;
        int len = 0;
        while (len < trimmed.Length && trimmed[len] == first) len++;
        if (len < 3) return false;
        if (first == '`' && trimmed[len..].Contains('`')) return false;
        fenceChar = first;
        fenceLength = len;
        return true;
    }

    private static bool IsClosingFence(ReadOnlySpan<char> trimmed, char fenceChar, int minLength)
    {
        if (trimmed.Length < minLength) return false;
        int len = 0;
        while (len < trimmed.Length && trimmed[len] == fenceChar) len++;
        return len >= minLength && trimmed[len..].IsWhiteSpace();
    }

    private static int ClassifyBlockLine(ReadOnlySpan<char> trimmed)
    {
        if (trimmed.Length > 0 && trimmed[0] == '|') return 1;
        if (trimmed.Length > 0 && trimmed[0] == '>') return 3;
        if (IsListItem(trimmed)) return 2;
        return 0;
    }

    private static bool IsListItem(ReadOnlySpan<char> trimmed)
    {
        if (trimmed.Length >= 2 && trimmed[0] is '-' or '*' or '+' && trimmed[1] is ' ' or '\t')
            return true;
        int digits = 0;
        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits])) digits++;
        return digits > 0 && digits + 1 < trimmed.Length && trimmed[digits] is '.' or ')' && trimmed[digits + 1] is ' ' or '\t';
    }
}
