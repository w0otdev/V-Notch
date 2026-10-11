using System.Globalization;
using System.Text;

namespace VNotch.Services.Translation;

internal static class TranslationInput
{
    internal const int MaxCharacters = 2000;

    internal static string Prepare(string text)
    {
        string prefix = Prefix(text, MaxCharacters);
        if (string.IsNullOrWhiteSpace(prefix)) throw new TranslationException("translation.noSelection");
        if ((!prefix.EnumerateRunes().Any(Rune.IsLetterOrDigit) && !TranslationStructuredText.IsOnlyIcons(prefix)) || IsKeyboardMash(prefix.Trim()))
            throw new TranslationException("translation.invalidSelection");
        return prefix;
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
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            int next = elements.ElementIndex + elements.GetTextElement().Length;
            if (next > limit) break;
            end = next;
        }
        return text[..end];
    }

    internal static string FitTokens(string text, Func<string, int> count, int limit)
    {
        if (count(text) <= limit) return text;
        var boundaries = StringInfo.ParseCombiningCharacters(text);
        int low = 0, high = boundaries.Length;
        while (low + 1 < high)
        {
            int middle = (low + high) / 2;
            if (count(text[..boundaries[middle]]) <= limit) low = middle;
            else high = middle;
        }
        string prefix = text[..boundaries[low]];
        return Prepare(prefix);
    }
}
