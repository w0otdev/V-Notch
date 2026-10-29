using System.Text;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace VNotch.Services.Translation;

internal static partial class TranslationTextIntegrity
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_translation_normalize_fact(
        ReadOnlySpan<char> factPtr,
        int factLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_translation_has_unresolved_placeholder(
        ReadOnlySpan<char> sourcePtr,
        int sourceLen,
        ReadOnlySpan<char> translatedPtr,
        int translatedLen,
        out byte outResult);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_translation_clean_text(
        ReadOnlySpan<char> textPtr,
        int textLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

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
            var inspection = Inspect(Original, translated);
            if (!inspection.LiteralsPreserved && !(allowUnverifiedNumbers && inspection.ProtectedFactsPreserved)) throw new TranslationException("translation.literalsChanged");
            return translated;
        }
    }

    internal static ProtectedText Protect(string text, bool protectNumbers = true)
    {
        var prefixBuilder = new StringBuilder("__VNT_LITERAL");
        while (text.Contains(prefixBuilder.ToString(), StringComparison.Ordinal)) prefixBuilder.Append('X');
        string prefix = prefixBuilder.ToString();
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
        char first = match.Value[0];
        if (!char.IsDigit(first) && first is not ('+' or '-' or '−') &&
            char.GetUnicodeCategory(first) != UnicodeCategory.CurrencySymbol) return false;
        if (match.Value.Contains('@') || match.Value.Contains("://", StringComparison.Ordinal)) return false;
        if (match.Value.Contains('.') && NumericHostSuffix().IsMatch(match.Value)) return false;
        return match.Index == 0 || !(char.IsAsciiLetterOrDigit(text[match.Index - 1]) || text[match.Index - 1] == '_');
    }

    private static bool HasUnresolvedPlaceholder(string source, string translated)
    {
        ReadOnlySpan<char> s = (source ?? string.Empty).AsSpan();
        ReadOnlySpan<char> t = (translated ?? string.Empty).AsSpan();
        vn_translation_has_unresolved_placeholder(s, s.Length, t, t.Length, out byte res);
        return res != 0;
    }

    private static bool IsIdiomaticOne(string text, Match literal) => literal.Value == "1" &&
        IdiomaticOne().Match(text, literal.Index) is { Success: true } idiom && idiom.Index == literal.Index;

    [GeneratedRegex(@"(?<![\p{L}\p{N}\p{Sc}_.,/+\-−])1[ \t]+(?:tí|tý|chút)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex IdiomaticOne();

    internal static string NormalizeFact(string fact)
    {
        if (string.IsNullOrEmpty(fact)) return string.Empty;
        ReadOnlySpan<char> span = fact.AsSpan();
        int cap = span.Length + 4;
        Span<char> buf = cap <= 256 ? stackalloc char[cap] : new char[cap];
        vn_translation_normalize_fact(span, span.Length, buf, buf.Length, out int len);
        return len > 0 ? new string(buf[..len]) : string.Empty;
    }

    [GeneratedRegex(@"\.[A-Za-z]{2,}", RegexOptions.CultureInvariant, 100)]
    private static partial Regex NumericHostSuffix();

    internal static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        ReadOnlySpan<char> span = text.AsSpan();
        int cap = span.Length;
        Span<char> buf = cap <= 1024 ? stackalloc char[cap] : new char[cap];
        vn_translation_clean_text(span, span.Length, buf, buf.Length, out int len);
        return len > 0 ? new string(buf[..len]) : string.Empty;
    }

    [GeneratedRegex(@"&(?:\#[xX][0-9a-fA-F]+|\#\d+|[A-Za-z][A-Za-z0-9]+);|(?:https?://|www\.)[^\s<>\[\]()*`\u3000-\u303f\uff01-\uff65]+|[\w.+-]+@[\w.-]+\.[\p{L}]{2,}|(?:[\p{L}\p{N}-]+\.)+[A-Za-z]{2,}(?:/[^\s<>\[\]()*`\u3000-\u303f\uff01-\uff65]*)?|[A-Z]{2,}[-_]\d+[A-Z0-9_-]*|(?:[+\-−]?\p{Sc}\s*)?[+\-−]?\d+(?:[.,:/\u066B\u066C-]\d+)*(?:\s*(?:[%\p{Sc}]|元|人民币|USD|VND|EUR|CNY|JPY|GBP))?", RegexOptions.CultureInvariant, 100)]
    private static partial Regex Facts();
}
