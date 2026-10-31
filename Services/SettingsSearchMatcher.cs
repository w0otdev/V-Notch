using System.Runtime.InteropServices;

namespace VNotch.Services;

internal static partial class SettingsSearchMatcher
{
    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_settings_search_normalize(
        char* inputPtr, int inputLen, char* outBuf, int outCap, out int outLen);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_settings_search_get_normalized_score(
        char* normSourcePtr, int normSourceLen, char* normQueryPtr, int normQueryLen, byte allowFuzzy, out int outScore);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_settings_search_score_row(
        char* normTitlePtr, int normTitleLen,
        char* normContentPtr, int normContentLen,
        char* normCombinedPtr, int normCombinedLen,
        char* normQueryPtr, int normQueryLen,
        byte allowFuzzy, out int outScore);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_settings_search_is_match(
        char* sourcePtr, int sourceLen, char* queryPtr, int queryLen, out byte outIsMatch);

    public static unsafe string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        int cap = Math.Max(32, input.Length * 4 + 8);
        char[] buf = new char[cap];
        fixed (char* pIn = input)
        fixed (char* pOut = buf)
        {
            int rc = vn_settings_search_normalize(pIn, input.Length, pOut, cap, out int outLen);
            if (rc == -2 && outLen > cap) { char[] larger = new char[outLen]; fixed (char* pL = larger) rc = vn_settings_search_normalize(pIn, input.Length, pL, outLen, out outLen); return rc == 0 && outLen > 0 ? new string(larger, 0, outLen) : string.Empty; }
            if (rc != 0) outLen = 0;
            return outLen > 0 ? new string(buf, 0, outLen) : string.Empty;
        }
    }

    public static unsafe bool IsMatch(string sourceText, string query)
    {
        string s = sourceText ?? string.Empty;
        string q = query ?? string.Empty;
        fixed (char* pS = s)
        fixed (char* pQ = q)
        {
            _ = vn_settings_search_is_match(pS, s.Length, pQ, q.Length, out byte isMatch);
            return isMatch != 0;
        }
    }

    public static bool IsNormalizedMatch(string normalizedSource, string normalizedQuery) =>
        GetNormalizedMatchScore(normalizedSource, normalizedQuery) > 0;

    public static unsafe int GetNormalizedMatchScore(string normalizedSource, string normalizedQuery, bool allowFuzzy = true)
    {
        if (string.IsNullOrEmpty(normalizedSource) || string.IsNullOrEmpty(normalizedQuery))
            return 0;

        fixed (char* pS = normalizedSource)
        fixed (char* pQ = normalizedQuery)
        {
            _ = vn_settings_search_get_normalized_score(
                pS, normalizedSource.Length,
                pQ, normalizedQuery.Length,
                allowFuzzy ? (byte)1 : (byte)0,
                out int score);
            return score;
        }
    }

    public static unsafe int GetRowMatchScore(
        string normalizedTitle,
        string normalizedContent,
        string normalizedCombined,
        string normalizedQuery,
        bool allowFuzzy = true)
    {
        if (string.IsNullOrEmpty(normalizedQuery))
            return 0;

        string t = normalizedTitle ?? string.Empty;
        string c = normalizedContent ?? string.Empty;
        string cb = normalizedCombined ?? string.Empty;

        fixed (char* pT = t)
        fixed (char* pC = c)
        fixed (char* pCb = cb)
        fixed (char* pQ = normalizedQuery)
        {
            _ = vn_settings_search_score_row(
                pT, t.Length,
                pC, c.Length,
                pCb, cb.Length,
                pQ, normalizedQuery.Length,
                allowFuzzy ? (byte)1 : (byte)0,
                out int score);
            return score;
        }
    }
}
