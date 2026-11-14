using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace VNotch.Services.Translation;

internal static partial class TranslationTextIntegrity
{
    private const string NativeDll = "VNotch.Native.dll";

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnTextFactSpan
    {
        public int offset;
        public int length;
        public int trimmed_length;
        public byte is_numeric;
        public byte is_idiomatic_one;
        public byte is_entity;
        public byte reserved;
    }

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

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_translation_integrity_extract_facts(
        ReadOnlySpan<char> textPtr,
        int textLen,
        Span<VnTextFactSpan> outSpans,
        int spanCap,
        out int outCount);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_translation_integrity_inspect(
        ReadOnlySpan<char> sourcePtr,
        int sourceLen,
        ReadOnlySpan<char> translatedPtr,
        int translatedLen,
        out byte outLiteralsPreserved,
        out byte outProtectedPreserved);

    internal readonly record struct Inspection(bool LiteralsPreserved, bool ProtectedFactsPreserved);

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
        var literals = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(text)) return new(text, literals, text);

        ReadOnlySpan<char> textSpan = text.AsSpan();
        Span<VnTextFactSpan> stackSpans = stackalloc VnTextFactSpan[32];
        VnTextFactSpan[]? heapSpans = null;
        int rc = vn_translation_integrity_extract_facts(textSpan, textSpan.Length, stackSpans, stackSpans.Length, out int count);
        if (rc == -2 && count > stackSpans.Length)
        {
            heapSpans = new VnTextFactSpan[count];
            rc = vn_translation_integrity_extract_facts(textSpan, textSpan.Length, heapSpans, heapSpans.Length, out count);
        }
        if (rc != 0 || count <= 0) return new(text, literals, text);

        ReadOnlySpan<VnTextFactSpan> spans = heapSpans != null ? heapSpans.AsSpan(0, count) : stackSpans[..count];

        var prefixBuilder = new StringBuilder("__VNT_LITERAL");
        while (text.Contains(prefixBuilder.ToString(), StringComparison.Ordinal)) prefixBuilder.Append('X');
        string prefix = prefixBuilder.ToString();

        var sb = new StringBuilder(text.Length + count * 16);
        int cursor = 0;
        for (int i = 0; i < spans.Length; i++)
        {
            VnTextFactSpan s = spans[i];
            if (s.offset > cursor)
                sb.Append(textSpan.Slice(cursor, s.offset - cursor));

            if (s.is_entity != 0 || s.is_idiomatic_one != 0 || (!protectNumbers && s.is_numeric != 0))
            {
                sb.Append(textSpan.Slice(s.offset, s.length));
            }
            else
            {
                string original = text.Substring(s.offset, s.trimmed_length);
                string placeholder = $"{prefix}{literals.Count}__";
                literals.Add(placeholder, original);
                sb.Append(placeholder);
                int suffixLen = s.length - s.trimmed_length;
                if (suffixLen > 0)
                    sb.Append(textSpan.Slice(s.offset + s.trimmed_length, suffixLen));
            }
            cursor = s.offset + s.length;
        }
        if (cursor < textSpan.Length)
            sb.Append(textSpan[cursor..]);

        return new(sb.ToString(), literals, text);
    }

    internal static bool Verify(string source, string translated) => Inspect(source, translated).LiteralsPreserved;

    internal static Inspection Inspect(string source, string translated)
    {
        if (string.IsNullOrWhiteSpace(translated) || HasUnresolvedPlaceholder(source, translated)) return default;
        source = WebUtility.HtmlDecode(TranslationStructuredText.NormalizeSocialIcons(source));
        translated = WebUtility.HtmlDecode(TranslationStructuredText.NormalizeSocialIcons(translated));
        ReadOnlySpan<char> srcSpan = source.AsSpan();
        ReadOnlySpan<char> dstSpan = translated.AsSpan();
        if (vn_translation_integrity_inspect(srcSpan, srcSpan.Length, dstSpan, dstSpan.Length, out byte literalsPreserved, out byte protectedPreserved) != 0)
            return default;
        return new(literalsPreserved != 0, protectedPreserved != 0);
    }

    internal static bool CanPresentUnverifiedNumbers(string source, string translated) => Inspect(source, translated).ProtectedFactsPreserved;

    private static bool HasUnresolvedPlaceholder(string source, string translated)
    {
        ReadOnlySpan<char> s = (source ?? string.Empty).AsSpan();
        ReadOnlySpan<char> t = (translated ?? string.Empty).AsSpan();
        vn_translation_has_unresolved_placeholder(s, s.Length, t, t.Length, out byte res);
        return res != 0;
    }

    internal static string NormalizeFact(string fact)
    {
        if (string.IsNullOrEmpty(fact)) return string.Empty;
        ReadOnlySpan<char> span = fact.AsSpan();
        int cap = span.Length + 4;
        Span<char> buf = cap <= 256 ? stackalloc char[cap] : new char[cap];
        vn_translation_normalize_fact(span, span.Length, buf, buf.Length, out int len);
        return len > 0 ? new string(buf[..len]) : string.Empty;
    }

    internal static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        ReadOnlySpan<char> span = text.AsSpan();
        int cap = span.Length;
        Span<char> buf = cap <= 1024 ? stackalloc char[cap] : new char[cap];
        vn_translation_clean_text(span, span.Length, buf, buf.Length, out int len);
        return len > 0 ? new string(buf[..len]) : string.Empty;
    }
}
