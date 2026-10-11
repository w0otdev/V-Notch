using System.Net;
using System.Text.RegularExpressions;

namespace VNotch.Services.Translation;

internal static partial class TranslationTextIntegrity
{
    internal sealed record ProtectedText(string Text, Dictionary<string, string> Literals, string Original)
    {
        internal string Restore(string translated, bool allowUnverifiedNumbers = false)
        {
            foreach (var (placeholder, original) in Literals)
            {
                translated = translated.Replace(placeholder, original, StringComparison.Ordinal);
            }
            // Some models emit original values instead of placeholders. Written-number
            // fallback is explicitly requested by the engine; the service flags it for review.
            if (!Verify(Original, translated) && !(allowUnverifiedNumbers && CanPresentUnverifiedNumbers(Original, translated))) throw new TranslationException("translation.literalsChanged");
            return translated;
        }
    }
    internal static ProtectedText Protect(string text, bool protectNumbers = true)
    {
        string prefix = "__VNT_LITERAL";
        while (text.Contains(prefix, StringComparison.Ordinal)) prefix += "X";
        var literals = new Dictionary<string, string>();
        string masked = Facts().Replace(text, match =>
        {
            if (match.Value.StartsWith("&", StringComparison.Ordinal) || IsIdiomaticOne(text, match) || (!protectNumbers && IsNumericFact(text, match))) return match.Value;
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
        if (string.IsNullOrWhiteSpace(translated) || HasUnresolvedPlaceholder(source, translated)) return false;
        source = WebUtility.HtmlDecode(TranslationStructuredText.NormalizeSocialIcons(source));
        translated = WebUtility.HtmlDecode(TranslationStructuredText.NormalizeSocialIcons(translated));
        var original = ObservableFacts(source);
        var result = ObservableFacts(translated);
        return original.SequenceEqual(result, StringComparer.Ordinal);
    }

    // Numeric spelling, currency names, separators, ordinals and dates vary by
    // language. A regex mismatch is a review signal, not proof of a bad translation.
    // Keep exact checks for URLs, email addresses and identifiers.
    internal static bool CanPresentUnverifiedNumbers(string source, string translated)
    {
        if (string.IsNullOrWhiteSpace(translated) || HasUnresolvedPlaceholder(source, translated)) return false;
        return ProtectedFacts(WebUtility.HtmlDecode(TranslationStructuredText.NormalizeSocialIcons(source))).SequenceEqual(ProtectedFacts(WebUtility.HtmlDecode(TranslationStructuredText.NormalizeSocialIcons(translated))), StringComparer.Ordinal);
    }

    private static IEnumerable<string> ProtectedFacts(string text) => Facts().Matches(text)
        .Where(match => !IsNumericFact(text, match))
        .Select(match => match.Value.TrimEnd('.', ',', ';', ':', ')', ']', '}', '!', '?'))
        .Order(StringComparer.Ordinal);

    private static bool IsNumericFact(string text, Match match)
    {
        // The first four alternatives in Facts are links, addresses and IDs.
        char first = match.Value[0];
        if (!char.IsDigit(first) && !"+-−¥$€£₫".Contains(first)) return false;
        if (match.Value.Contains('@') || match.Value.Contains("://", StringComparison.Ordinal)) return false;
        // Numeric hostnames are still links, not quantities.
        if (Regex.IsMatch(match.Value, @"\.[A-Za-z]{2,}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) return false;
        // Preserve embedded IDs such as item12; ordinary units after a number
        // may be translated and are not identifiers.
        return match.Index == 0 || !(char.IsAsciiLetterOrDigit(text[match.Index - 1]) || text[match.Index - 1] == '_');
    }

    private static bool HasUnresolvedPlaceholder(string source, string translated) =>
        Regex.Matches(translated, @"__VNT_\w+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))
            .Any(match => !source.Contains(match.Value, StringComparison.Ordinal));

    private static IEnumerable<string> ObservableFacts(string text) => Facts().Matches(text)
        .Where(match => !IsIdiomaticOne(text, match))
        .Select(match => NormalizeFact(match.Value.TrimEnd('.', ',', ';', ':', ')', ']', '}', '!', '?')))
        .Order(StringComparer.Ordinal);

    // Vietnamese "1 tí/chút" means "a little", not an exact quantity of one.
    // Leave it to the model as prose in both protection and verification. Match
    // only a standalone unsigned 1; amounts, identifiers and other numbers remain facts.
    private static bool IsIdiomaticOne(string text, Match literal) => literal.Value == "1" &&
        IdiomaticOne().Match(text, literal.Index) is { Success: true } idiom && idiom.Index == literal.Index;

    [GeneratedRegex(@"(?<![\p{L}\p{N}_.,/+\-−¥$€£₫])1[ \t]+(?:tí|tý|chút)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex IdiomaticOne();

    internal static string NormalizeFact(string fact) =>
        Regex.Replace(fact, @"(?<=\b\d{1,3})[.,](?=\d{3}(?:\D|$))", "");

    // Natural phrasing comes from the translation model. Do not rewrite facts or
    // remove script-specific separators in a separate paraphrasing pass.
    internal static string Clean(string text) => text.Trim();

    [GeneratedRegex(@"&(?:\#[xX][0-9a-fA-F]+|\#\d+|[A-Za-z][A-Za-z0-9]+);|(?:https?://|www\.)[^\s<>\[\]()*`\u3000-\u303f\uff01-\uff65]+|[\w.+-]+@[\w.-]+\.[\p{L}]{2,}|(?:[\p{L}\p{N}-]+\.)+[A-Za-z]{2,}(?:/[^\s<>\[\]()*`\u3000-\u303f\uff01-\uff65]*)?|[A-Z]{2,}[-_]\d+[A-Z0-9_-]*|(?:[+\-−]?[¥$€£₫]\s*)?[+\-−]?\d+(?:[.,:/-]\d+)*(?:\s*(?:[%¥$€£₫]|元|人民币|USD|VND|EUR|CNY|JPY|GBP))?", RegexOptions.CultureInvariant, 100)]
    private static partial Regex Facts();
}
