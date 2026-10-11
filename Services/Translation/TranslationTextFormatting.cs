namespace VNotch.Services.Translation;

internal static class TranslationTextFormatting
{
    // Only change paragraph separators; never rewrite translated words or punctuation.
    // Preserve internal line breaks within Markdown fenced code blocks.
    internal static string Format(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraphs = new List<string>();
        var currentCodeBlock = new List<string>();
        bool inCodeFence = false;

        foreach (var line in lines)
        {
            string trimmed = line.Trim();
            bool isFence = trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal);

            if (isFence)
            {
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
                currentCodeBlock.Add(line);
            }
            else if (!string.IsNullOrWhiteSpace(line))
            {
                paragraphs.Add(line);
            }
        }

        if (currentCodeBlock.Count > 0)
        {
            paragraphs.Add(string.Join("\n", currentCodeBlock));
        }

        return string.Join("\n\n", paragraphs);
    }
}
