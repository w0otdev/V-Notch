using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace VNotch.Services.Translation;

internal static partial class TranslationInput
{
    private const string NativeDll = "VNotch.Native.dll";
    internal const int MaxCharacters = 2000;

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_translation_input_sanitize(
        ReadOnlySpan<char> textPtr,
        int textLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_translation_input_is_keyboard_mash(
        ReadOnlySpan<char> textPtr,
        int textLen,
        out byte outResult);

    internal static string Prepare(string text)
    {
        string sanitized = Sanitize(text);
        string prefix = Prefix(sanitized, MaxCharacters);
        if (string.IsNullOrWhiteSpace(prefix)) throw new TranslationException("translation.noSelection");
        if ((!prefix.EnumerateRunes().Any(r => Rune.IsLetterOrDigit(r) || Rune.IsSymbol(r)) && !TranslationStructuredText.IsOnlyIcons(prefix)) || IsKeyboardMash(prefix.Trim()))
            throw new TranslationException("translation.invalidSelection");
        return prefix;
    }

    internal static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (text.IndexOfAny(['\0', '\uFFFC']) < 0) return text;
        ReadOnlySpan<char> span = text.AsSpan();
        int cap = span.Length;
        Span<char> buf = cap <= 1024 ? stackalloc char[cap] : new char[cap];
        vn_translation_input_sanitize(span, span.Length, buf, buf.Length, out int len);
        return len > 0 ? new string(buf[..len]) : "";
    }

    private static bool IsKeyboardMash(string text)
    {
        ReadOnlySpan<char> span = text.AsSpan();
        vn_translation_input_is_keyboard_mash(span, span.Length, out byte res);
        return res != 0;
    }

    // Keep surrogate pairs, combining marks and emoji sequences together.
    internal static string Prefix(string text, int limit)
    {
        if (text.Length <= limit) return text;
        int end = 0;
        int index = 0;
        while (index < text.Length)
        {
            int next = index + StringInfo.GetNextTextElementLength(text, index);
            if (next > limit) break;
            end = next;
            index = next;
        }
        return text[..end];
    }

    internal static string FitTokens(string text, Func<string, int> count, int limit) =>
        FitTokensWithCount(text, count, limit).Text;

    internal static (string Text, int TokenCount) FitTokensWithCount(string text, Func<string, int> count, int limit)
    {
        int tokens = count(text);
        if (tokens <= limit) return (text, tokens);
        var boundaries = StringInfo.ParseCombiningCharacters(text);
        int low = 0, high = boundaries.Length, lowTokens = 0;
        while (low + 1 < high)
        {
            int middle = (low + high) / 2;
            int middleTokens = count(text[..boundaries[middle]]);
            if (middleTokens <= limit) { low = middle; lowTokens = middleTokens; }
            else high = middle;
        }
        string prefix = text[..boundaries[low]];
        string prepared = Prepare(prefix);
        return (prepared, prepared == prefix ? lowTokens : count(prepared));
    }
}
