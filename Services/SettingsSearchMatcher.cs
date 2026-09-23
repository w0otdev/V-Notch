using System.Globalization;
using System.Text;

namespace VNotch.Services;

internal static class SettingsSearchMatcher
{
    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        string decomposed = input.Normalize(NormalizationForm.FormKD);
        var result = new StringBuilder(decomposed.Length);
        bool previousWasSeparator = true;

        foreach (char character in decomposed)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category is UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            char normalizedCharacter = character switch
            {
                'đ' or 'Đ' => 'd',
                _ => char.ToLowerInvariant(character)
            };

            if (char.IsLetterOrDigit(normalizedCharacter))
            {
                result.Append(normalizedCharacter);
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator)
            {
                result.Append(' ');
                previousWasSeparator = true;
            }
        }

        if (result.Length > 0 && result[^1] == ' ')
        {
            result.Length--;
        }

        return result.ToString();
    }

    public static bool IsMatch(string sourceText, string query) =>
        IsNormalizedMatch(Normalize(sourceText), Normalize(query));

    public static bool IsNormalizedMatch(string normalizedSource, string normalizedQuery) =>
        GetNormalizedMatchScore(normalizedSource, normalizedQuery) > 0;

    // Zero means unrelated. Exact words rank above prefixes, then minor typos.
    public static int GetNormalizedMatchScore(string normalizedSource, string normalizedQuery, bool allowFuzzy = true)
    {
        if (normalizedSource.Length == 0 || normalizedQuery.Length == 0)
        {
            return 0;
        }

        string[] sourceWords = normalizedSource.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] queryWords = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (queryWords.Length == 0) return 0;

        int weakestMatch = 3;
        int exactMatches = 0;
        int fuzzyMatches = 0;
        foreach (string queryWord in queryWords)
        {
            int quality = GetWordMatchQuality(sourceWords, queryWord, allowFuzzy);
            // Never let several approximate words turn an unrelated query into a result.
            if (quality == 0 || (quality == 1 && ++fuzzyMatches > 1)) return 0;
            if (quality == 3) exactMatches++;
            weakestMatch = Math.Min(weakestMatch, quality);
        }

        return weakestMatch * 100 + 50 * exactMatches / queryWords.Length;
    }

    private static int GetWordMatchQuality(string[] sourceWords, string queryWord, bool allowFuzzy)
    {
        int bestQuality = 0;
        foreach (string sourceWord in sourceWords)
        {
            if (sourceWord == queryWord) return 3;

            // These scripts commonly omit spaces between words; preserve literal search.
            if (queryWord.All(IsUnsegmentedScriptCharacter)
                && sourceWord.Contains(queryWord, StringComparison.Ordinal))
            {
                bestQuality = Math.Max(bestQuality, 2);
                continue;
            }

            // Short keywords (e.g. "on" or "ai") must be whole words.
            if (queryWord.Length >= 3 && sourceWord.StartsWith(queryWord, StringComparison.Ordinal))
            {
                bestQuality = Math.Max(bestQuality, 2);
                continue;
            }

            if (allowFuzzy && bestQuality == 0
                && queryWord.Length >= 5 && sourceWord.Length >= 5
                && queryWord[0] == sourceWord[0]
                && Math.Abs(queryWord.Length - sourceWord.Length) <= 1
                && CalculateLevenshteinDistance(queryWord, sourceWord) == 1)
            {
                bestQuality = 1;
            }
        }

        return bestQuality;
    }

    private static bool IsUnsegmentedScriptCharacter(char character) => character is
        >= '\u3040' and <= '\u30ff' // Japanese
        or >= '\u3400' and <= '\u9fff' // Han
        or >= '\u1100' and <= '\u11ff' // Decomposed Hangul (FormKD)
        or >= '\u0e00' and <= '\u0eff' // Thai and Lao
        or >= '\u1000' and <= '\u109f' // Myanmar
        or >= '\u1780' and <= '\u17ff'; // Khmer

    private static int CalculateLevenshteinDistance(string source, string target)
    {
        if (source.Length == 0) return target.Length;
        if (target.Length == 0) return source.Length;

        int[] previousRow = new int[target.Length + 1];
        int[] currentRow = new int[target.Length + 1];

        for (int column = 0; column < previousRow.Length; column++)
        {
            previousRow[column] = column;
        }

        for (int row = 0; row < source.Length; row++)
        {
            currentRow[0] = row + 1;
            for (int column = 0; column < target.Length; column++)
            {
                int substitutionCost = source[row] == target[column] ? 0 : 1;
                currentRow[column + 1] = Math.Min(
                    Math.Min(currentRow[column] + 1, previousRow[column + 1] + 1),
                    previousRow[column] + substitutionCost);
            }

            (previousRow, currentRow) = (currentRow, previousRow);
        }

        return previousRow[target.Length];
    }
}
