namespace VNotch.Services.Translation;

internal static class TranslationTextFormatting
{
    // Only change paragraph separators; never rewrite translated words or punctuation.
    internal static string Format(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        return string.Join("\n\n", lines.Where(line => !string.IsNullOrWhiteSpace(line)));
    }
}
