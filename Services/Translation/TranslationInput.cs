using System.Globalization;
using System.Text;

namespace VNotch.Services.Translation;

internal static class TranslationInput
{
    internal const int MaxCharacters = 2000;

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
        return string.Concat(text.Where(c => c is not ('\0' or '\uFFFC')));
    }

    private static bool IsKeyboardMash(string text)
    {
        if (text.Length < 7 || text.Length > 10) return false;
        // Reject whole keyboard walks, without rejecting words such as QWERTY.
        return text.ToLowerInvariant() is "qwertyuiop" or "asdfghjkl" or "zxcvbnm" or "poiuytrewq" or "lkjhgfdsa" or "mnbvcxz";
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
        // Sanitation can change a direct caller's text. Only carry the measured
        // count when it still describes the exact string being returned.
        return (prepared, prepared == prefix ? lowTokens : count(prepared));
    }
}
