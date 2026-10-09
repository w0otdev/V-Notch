using System.Text.RegularExpressions;

namespace VNotch.Services.Translation;

internal static partial class TranslationTextIntegrity
{
    internal sealed record ProtectedText(string Text, Dictionary<string, string> Literals, string Original)
    {
        internal string Restore(string translated)
        {
            bool missingPlaceholder = false;
            foreach (var (placeholder, original) in Literals)
            {
                if (!translated.Contains(placeholder, StringComparison.Ordinal)) missingPlaceholder = true;
                translated = translated.Replace(placeholder, original, StringComparison.Ordinal);
            }
            // Some models emit a literal's original value instead of its placeholder. Accept
            // that only when the complete multiset of observable literals still matches.
            if (missingPlaceholder && !Verify(Original, translated)) throw new TranslationException("translation.failed");
            return translated;
        }
    }
    internal static ProtectedText Protect(string text)
    {
        string prefix = "__VNT_LITERAL";
        while (text.Contains(prefix, StringComparison.Ordinal)) prefix += "X";
        var literals = new Dictionary<string, string>();
        string masked = Facts().Replace(text, match =>
        {
            string original = match.Value.TrimEnd('.', ',', ';', ':', ')', ']', '}', '!', '?');
            string placeholder = $"{prefix}{literals.Count}__";
            literals.Add(placeholder, original);
            return placeholder + match.Value[original.Length..];
        });
        return new(masked, literals, text);
    }
    // Compare only observable literals. Semantic accuracy still depends on the model.
    internal static bool Verify(string source, string translated)
    {
        if (string.IsNullOrWhiteSpace(translated)) return false;
        var original = Facts().Matches(source).Select(x => x.Value.TrimEnd('.', ',', ';', ':', ')', ']', '}', '!', '?')).Order(StringComparer.Ordinal).ToArray();
        var result = Facts().Matches(translated).Select(x => x.Value.TrimEnd('.', ',', ';', ':', ')', ']', '}', '!', '?')).Order(StringComparer.Ordinal).ToArray();
        return original.SequenceEqual(result, StringComparer.Ordinal);
    }

    // Natural phrasing comes from the translation model. Do not rewrite facts or
    // remove script-specific separators in a separate paraphrasing pass.
    internal static string Clean(string text) => text.Trim();

    [GeneratedRegex(@"(?:https?://|www\.)[^\s<>\u3000-\u303f\uff01-\uff65]+|[\w.+-]+@[\w.-]+\.[\p{L}]{2,}|(?:[\p{L}\p{N}-]+\.)+[A-Za-z]{2,}(?:/[^\s<>\u3000-\u303f\uff01-\uff65]*)?|[A-Z]{2,}[-_]\d+[A-Z0-9_-]*|(?:[+\-−]?[¥$€£₫]\s*)?[+\-−]?\d+(?:[.,:/-]\d+)*(?:\s*(?:[%¥$€£₫]|元|人民币|USD|VND|EUR|CNY|JPY|GBP))?", RegexOptions.CultureInvariant, 100)]
    private static partial Regex Facts();
}
