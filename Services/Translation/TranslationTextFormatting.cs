namespace VNotch.Services.Translation;

internal static class TranslationTextFormatting
{
    // Only change paragraph separators; never rewrite translated words or punctuation.
    // Preserve internal line breaks within Markdown fenced code blocks.
    internal static string Format(string text)
    {
        var paragraphs = new List<string>();
        var currentCodeBlock = new List<string>();
        bool inCodeFence = false;

        foreach (ReadOnlySpan<char> rawLine in text.AsSpan().EnumerateLines())
        {
            ReadOnlySpan<char> trimmed = rawLine.Trim();
            bool isFence = trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal);

            if (isFence)
            {
                string line = rawLine.ToString();
                if (inCodeFence)
                {
                    currentCodeBlock.Add(line);
                    paragraphs.Add(string.Join("\n", currentCodeBlock));
                    currentCodeBlock.Clear();
                    inCodeFence = false;
                }
                else
                {
                    inCodeFence = true;
                    currentCodeBlock.Clear();
                    currentCodeBlock.Add(line);
                }
                continue;
            }

            if (inCodeFence)
            {
                currentCodeBlock.Add(rawLine.ToString());
            }
            else if (!trimmed.IsEmpty)
            {
                paragraphs.Add(rawLine.ToString());
            }
        }

        if (currentCodeBlock.Count > 0)
        {
            paragraphs.Add(string.Join("\n", currentCodeBlock));
        }

        return string.Join("\n\n", paragraphs);
    }
}
