using System.Runtime.InteropServices;

namespace VNotch.Services;

internal static partial class MediaHeuristics
{
    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_media_heuristics_build_track_identity(
        char* trackPtr, int trackLen, char* artistPtr, int artistLen, char* outBuf, int outCap, out int outLen);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_media_heuristics_build_source_override_key(
        char* sessionKeyPtr, int sessionKeyLen, char* appIdPtr, int appIdLen, char* outBuf, int outCap, out int outLen);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_media_heuristics_is_track_compatible_with_window_title(
        char* trackPtr, int trackLen, char* titlePtr, int titleLen, out byte outCompatible);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_media_heuristics_is_ignored_source_app(
        char* appIdPtr, int appIdLen, out byte outIgnored);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_media_heuristics_is_likely_soundcloud_placeholder_url(
        char* urlPtr, int urlLen, out byte outIsPlaceholder);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_media_heuristics_normalize_track_for_comparison(
        char* textPtr, int textLen, char* outBuf, int outCap, out int outLen);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_media_heuristics_extract_core_track_name(
        char* trackPtr, int trackLen, char* outBuf, int outCap, out int outLen);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_media_heuristics_spotify_title_contains_track(
        char* titlePtr, int titleLen, char* trackPtr, int trackLen, out byte outContains);

    public static unsafe string BuildTrackIdentity(string track, string artist)
    {
        string t = track ?? string.Empty;
        string a = artist ?? string.Empty;
        int cap = (t.Length + a.Length) * 4 + 16;
        Span<char> buf = cap <= 512 ? stackalloc char[cap] : new char[cap];
        fixed (char* pT = t)
        fixed (char* pA = a)
        fixed (char* pOut = buf)
        {
            int rc = vn_media_heuristics_build_track_identity(pT, t.Length, pA, a.Length, pOut, cap, out int len);
            if (rc == -2 && len > cap) { char[] larger = new char[len]; fixed (char* pL = larger) rc = vn_media_heuristics_build_track_identity(pT, t.Length, pA, a.Length, pL, len, out len); return rc == 0 && len > 0 ? new string(larger, 0, len) : "|"; }
            if (rc != 0) len = 0;
            return len > 0 ? new string(buf.Slice(0, len)) : "|";
        }
    }

    public static unsafe string BuildSourceOverrideKey(string sessionInstanceKey, string sourceAppId)
    {
        string k = sessionInstanceKey ?? string.Empty;
        string a = sourceAppId ?? string.Empty;
        int cap = Math.Max(16, Math.Max(k.Length, a.Length) + 8);
        Span<char> buf = cap <= 512 ? stackalloc char[cap] : new char[cap];
        fixed (char* pK = k)
        fixed (char* pA = a)
        fixed (char* pOut = buf)
        {
            int rc = vn_media_heuristics_build_source_override_key(pK, k.Length, pA, a.Length, pOut, cap, out int len);
            if (rc == -2 && len > cap) { char[] larger = new char[len]; fixed (char* pL = larger) rc = vn_media_heuristics_build_source_override_key(pK, k.Length, pA, a.Length, pL, len, out len); return rc == 0 && len > 0 ? new string(larger, 0, len) : string.Empty; }
            if (rc != 0) len = 0;
            return len > 0 ? new string(buf.Slice(0, len)) : string.Empty;
        }
    }

    public static unsafe bool IsTrackCompatibleWithWindowTitle(string track, string windowTitle)
    {
        if (string.IsNullOrWhiteSpace(track) || string.IsNullOrWhiteSpace(windowTitle))
            return false;

        fixed (char* pT = track)
        fixed (char* pW = windowTitle)
        {
            _ = vn_media_heuristics_is_track_compatible_with_window_title(pT, track.Length, pW, windowTitle.Length, out byte comp);
            return comp != 0;
        }
    }

    public static unsafe bool IsIgnoredSourceApp(string sourceAppId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId))
            return false;

        fixed (char* pA = sourceAppId)
        {
            _ = vn_media_heuristics_is_ignored_source_app(pA, sourceAppId.Length, out byte ign);
            return ign != 0;
        }
    }

    public static unsafe bool IsLikelySoundCloudPlaceholderArtworkUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return true;

        fixed (char* pU = url)
        {
            _ = vn_media_heuristics_is_likely_soundcloud_placeholder_url(pU, url.Length, out byte ph);
            return ph != 0;
        }
    }

    public static unsafe string NormalizeTrackForComparison(string text)
    {
        string s = text ?? string.Empty;
        int cap = Math.Max(32, s.Length * 3 + 8);
        Span<char> buf = cap <= 512 ? stackalloc char[cap] : new char[cap];
        fixed (char* pIn = s)
        fixed (char* pOut = buf)
        {
            int rc = vn_media_heuristics_normalize_track_for_comparison(pIn, s.Length, pOut, cap, out int len);
            if (rc == -2 && len > cap) { char[] larger = new char[len]; fixed (char* pL = larger) rc = vn_media_heuristics_normalize_track_for_comparison(pIn, s.Length, pL, len, out len); return rc == 0 && len > 0 ? new string(larger, 0, len) : string.Empty; }
            if (rc != 0) len = 0;
            return len > 0 ? new string(buf.Slice(0, len)) : string.Empty;
        }
    }

    public static unsafe string ExtractCoreTrackName(string track)
    {
        string s = track ?? string.Empty;
        int cap = Math.Max(16, s.Length + 8);
        Span<char> buf = cap <= 512 ? stackalloc char[cap] : new char[cap];
        fixed (char* pIn = s)
        fixed (char* pOut = buf)
        {
            int rc = vn_media_heuristics_extract_core_track_name(pIn, s.Length, pOut, cap, out int len);
            if (rc == -2 && len > cap) { char[] larger = new char[len]; fixed (char* pL = larger) rc = vn_media_heuristics_extract_core_track_name(pIn, s.Length, pL, len, out len); return rc == 0 && len > 0 ? new string(larger, 0, len) : string.Empty; }
            if (rc != 0) len = 0;
            return len > 0 ? new string(buf.Slice(0, len)) : string.Empty;
        }
    }

    public static unsafe bool SpotifyTitleContainsTrack(string spotifyWindowTitle, string smtcTrack)
    {
        if (string.IsNullOrEmpty(spotifyWindowTitle) || string.IsNullOrEmpty(smtcTrack))
            return false;

        fixed (char* pW = spotifyWindowTitle)
        fixed (char* pT = smtcTrack)
        {
            _ = vn_media_heuristics_spotify_title_contains_track(pW, spotifyWindowTitle.Length, pT, smtcTrack.Length, out byte c);
            return c != 0;
        }
    }
}
