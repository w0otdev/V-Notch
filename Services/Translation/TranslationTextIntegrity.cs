using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace VNotch.Services.Translation;

internal static partial class TranslationTextIntegrity
{
    internal readonly record struct Inspection(bool LiteralsPreserved, bool ProtectedFactsPreserved);
    private readonly record struct FactSet(List<string>? Observable, List<string>? Protected);

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
            var inspection = Inspect(Original, translated);
            if (!inspection.LiteralsPreserved && !(allowUnverifiedNumbers && inspection.ProtectedFactsPreserved)) throw new TranslationException("translation.literalsChanged");
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
    internal static bool Verify(string source, string translated) => Inspect(source, translated).LiteralsPreserved;

    internal static Inspection Inspect(string source, string translated)
    {
        if (string.IsNullOrWhiteSpace(translated) || HasUnresolvedPlaceholder(source, translated)) return default;
        source = WebUtility.HtmlDecode(TranslationStructuredText.NormalizeSocialIcons(source));
        translated = WebUtility.HtmlDecode(TranslationStructuredText.NormalizeSocialIcons(translated));
        var original = ReadFacts(source);
        var result = ReadFacts(translated);
        return new(SameFacts(original.Observable, result.Observable), SameFacts(original.Protected, result.Protected));
    }

    // Numeric spelling, currency names, separators, ordinals and dates vary by
    // language. A regex mismatch is a review signal, not proof of a bad translation.
    // Keep exact checks for URLs, email addresses and identifiers.
    internal static bool CanPresentUnverifiedNumbers(string source, string translated) => Inspect(source, translated).ProtectedFactsPreserved;

    private static FactSet ReadFacts(string text)
    {
        List<string>? observable = null, protectedFacts = null;
        foreach (Match match in Facts().Matches(text))
        {
            bool numeric = IsNumericFact(text, match);
            string value = match.Value.TrimEnd('.', ',', ';', ':', ')', ']', '}', '!', '?');
            if (!IsIdiomaticOne(text, match))
                (observable ??= new()).Add(numeric ? NormalizeFact(value) : value);
            // Link, address and identifier comparisons remain exact. Keep the same
            // strings for fallback checks instead of normalizing/parsing again.
            if (!numeric) (protectedFacts ??= new()).Add(value);
        }
        observable?.Sort(StringComparer.Ordinal);
        protectedFacts?.Sort(StringComparer.Ordinal);
        return new(observable, protectedFacts);
    }

    private static bool SameFacts(List<string>? original, List<string>? result)
    {
        if (original == null || result == null) return original == result;
        if (original.Count != result.Count) return false;
        for (int i = 0; i < original.Count; i++)
            if (!string.Equals(original[i], result[i], StringComparison.Ordinal)) return false;
        return true;
    }

    private static bool IsNumericFact(string text, Match match)
    {
        // The first four alternatives in Facts are links, addresses and IDs.
        char first = match.Value[0];
        if (!char.IsDigit(first) && first is not ('+' or '-' or '−') &&
            char.GetUnicodeCategory(first) != UnicodeCategory.CurrencySymbol) return false;
        if (match.Value.Contains('@') || match.Value.Contains("://", StringComparison.Ordinal)) return false;
        // Numeric hostnames are still links, not quantities.
        if (match.Value.Contains('.') && NumericHostSuffix().IsMatch(match.Value)) return false;
        // Preserve embedded IDs such as item12; ordinary units after a number
        // may be translated and are not identifiers.
        return match.Index == 0 || !(char.IsAsciiLetterOrDigit(text[match.Index - 1]) || text[match.Index - 1] == '_');
    }

    private static bool HasUnresolvedPlaceholder(string source, string translated)
    {
        if (!translated.Contains("__VNT_", StringComparison.Ordinal)) return false;
        foreach (Match match in UnresolvedPlaceholder().Matches(translated))
            if (!source.Contains(match.Value, StringComparison.Ordinal)) return true;
        return false;
    }

    // Vietnamese "1 tí/chút" means "a little", not an exact quantity of one.
    // Leave it to the model as prose in both protection and verification. Match
    // only a standalone unsigned 1; amounts, identifiers and other numbers remain facts.
    private static bool IsIdiomaticOne(string text, Match literal) => literal.Value == "1" &&
        IdiomaticOne().Match(text, literal.Index) is { Success: true } idiom && idiom.Index == literal.Index;

    [GeneratedRegex(@"(?<![\p{L}\p{N}\p{Sc}_.,/+\-−])1[ \t]+(?:tí|tý|chút)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex IdiomaticOne();

    internal static string NormalizeFact(string fact)
    {
        // Compare decimal values across writing systems without rewriting the
        // displayed translation, identifiers, currency symbols or signs.
        StringBuilder? canonical = null;
        for (int i = 0; i < fact.Length; i++)
        {
            char c = fact[i];
            int digit = c > 127 ? CharUnicodeInfo.GetDecimalDigitValue(c) : -1;
            char normalized = digit >= 0 ? (char)('0' + digit) : c switch
            {
                '\u066B' => '.', // Arabic decimal separator
                '\u066C' => ',', // Arabic thousands separator
                _ => c
            };
            if (canonical == null && normalized == c) continue;
            canonical ??= new StringBuilder(fact.Length).Append(fact.AsSpan(0, i));
            canonical.Append(normalized);
        }
        return GroupingSeparators().Replace(canonical?.ToString() ?? fact, "");
    }

    [GeneratedRegex(@"\.[A-Za-z]{2,}", RegexOptions.CultureInvariant, 100)]
    private static partial Regex NumericHostSuffix();

    [GeneratedRegex(@"__VNT_\w+", RegexOptions.CultureInvariant, 100)]
    private static partial Regex UnresolvedPlaceholder();

    [GeneratedRegex(@"(?<=\b\d{1,3})[.,](?=\d{3}(?:\D|$))")]
    private static partial Regex GroupingSeparators();

    // Natural phrasing comes from the translation model. Do not rewrite facts or
    // remove script-specific separators in a separate paraphrasing pass.
    internal static string Clean(string text) => text.Trim();

    [GeneratedRegex(@"&(?:\#[xX][0-9a-fA-F]+|\#\d+|[A-Za-z][A-Za-z0-9]+);|(?:https?://|www\.)[^\s<>\[\]()*`\u3000-\u303f\uff01-\uff65]+|[\w.+-]+@[\w.-]+\.[\p{L}]{2,}|(?:[\p{L}\p{N}-]+\.)+[A-Za-z]{2,}(?:/[^\s<>\[\]()*`\u3000-\u303f\uff01-\uff65]*)?|[A-Z]{2,}[-_]\d+[A-Z0-9_-]*|(?:[+\-−]?\p{Sc}\s*)?[+\-−]?\d+(?:[.,:/\u066B\u066C-]\d+)*(?:\s*(?:[%\p{Sc}]|元|人民币|USD|VND|EUR|CNY|JPY|GBP))?", RegexOptions.CultureInvariant, 100)]
    private static partial Regex Facts();
}
