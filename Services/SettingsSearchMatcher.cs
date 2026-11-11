using System;
using System.Runtime.InteropServices;

namespace VNotch.Services;

internal static partial class SettingsSearchMatcher
{
    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_settings_search_get_normalized_score(
        char* normSourcePtr, int normSourceLen, char* normQueryPtr, int normQueryLen, byte allowFuzzy, out int outScore);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_settings_search_is_match(
        char* sourcePtr, int sourceLen, char* queryPtr, int queryLen, out byte outIsMatch);

    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        int cap = Math.Max(32, input.Length * 4 + 8);
        char[] buf = new char[cap];
        int rc = VNotchNative.vn_settings_search_normalize(input.AsSpan(), input.Length, buf, cap, out int outLen);
        if (rc == VNotchNative.VN_ERR_BUFFER_TOO_SMALL && outLen > cap)
        {
            char[] larger = new char[outLen];
            rc = VNotchNative.vn_settings_search_normalize(input.AsSpan(), input.Length, larger, outLen, out outLen);
            return rc == VNotchNative.VN_OK && outLen > 0 ? new string(larger, 0, outLen) : string.Empty;
        }
        return rc == VNotchNative.VN_OK && outLen > 0 ? new string(buf, 0, outLen) : string.Empty;
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

    public static int GetRowMatchScore(
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

        _ = VNotchNative.vn_settings_search_score_row(
            t.AsSpan(), t.Length,
            c.AsSpan(), c.Length,
            cb.AsSpan(), cb.Length,
            normalizedQuery.AsSpan(), normalizedQuery.Length,
            allowFuzzy ? (byte)1 : (byte)0,
            out int score);
        return score;
    }
}
