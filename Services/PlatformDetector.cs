using System.Runtime.InteropServices;

namespace VNotch.Services;

public static partial class PlatformDetector
{
    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_platform_detect_from_app_id(char* appPtr, int appLen, out int outPlatform);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_platform_is_browser_app(char* appPtr, int appLen, out byte outIsBrowser);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_platform_detect_single_window_title(char* titlePtr, int titleLen, out int outImmediate, out int outFallback);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_platform_extract_title_from_window(char* titlePtr, int titleLen, char* fallbackPtr, int fallbackLen, char* outBuf, int outCap, out int outLen);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_platform_parse_spotify_title(char* titlePtr, int titleLen, char* outArtistBuf, int outArtistCap, out int outArtistLen, char* outTrackBuf, int outTrackCap, out int outTrackLen);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_platform_parse_soundcloud_title(char* titlePtr, int titleLen, char* outArtistBuf, int outArtistCap, out int outArtistLen, char* outTrackBuf, int outTrackCap, out int outTrackLen);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_platform_normalize_for_loose_match(char* valPtr, int valLen, char* outBuf, int outCap, out int outLen);

    public static unsafe MediaPlatform DetectFromAppId(string? sourceAppId)
    {
        if (string.IsNullOrEmpty(sourceAppId))
            return MediaPlatform.Unknown;

        fixed (char* ptr = sourceAppId)
        {
            _ = vn_platform_detect_from_app_id(ptr, sourceAppId.Length, out int p);
            return (MediaPlatform)p;
        }
    }

    public static unsafe bool IsBrowserApp(string? sourceAppId)
    {
        if (string.IsNullOrEmpty(sourceAppId))
            return false;

        fixed (char* ptr = sourceAppId)
        {
            _ = vn_platform_is_browser_app(ptr, sourceAppId.Length, out byte b);
            return b != 0;
        }
    }

    public static unsafe MediaPlatform DetectFromWindowTitles(IEnumerable<string> windowTitles)
    {
        MediaPlatform fallback = MediaPlatform.Unknown;

        foreach (var title in windowTitles)
        {
            if (string.IsNullOrEmpty(title))
                continue;

            fixed (char* ptr = title)
            {
                _ = vn_platform_detect_single_window_title(ptr, title.Length, out int imm, out int fb);
                if ((MediaPlatform)imm != MediaPlatform.Unknown)
                    return (MediaPlatform)imm;
                if (fallback == MediaPlatform.Unknown && (MediaPlatform)fb != MediaPlatform.Unknown)
                    fallback = (MediaPlatform)fb;
            }
        }

        return fallback;
    }

    public static string DetectPlatformHint(IEnumerable<string> windowTitles)
    {
        var platform = DetectFromWindowTitles(windowTitles);
        return platform.ToDisplayString();
    }

    public static bool HasReliableWindowMatch(string windowTitle, MediaPlatform platform)
    {
        if (string.IsNullOrWhiteSpace(windowTitle))
            return false;

        string platformName = platform.ToDisplayString();
        return !string.IsNullOrEmpty(platformName) &&
               windowTitle.Contains(platformName, StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasReliableWindowMatch(IEnumerable<string> windowTitles, string track, string platformName)
    {
        if (string.IsNullOrWhiteSpace(track) || string.IsNullOrWhiteSpace(platformName))
            return false;

        string normalizedTrack = NormalizeForLooseMatch(track);
        if (string.IsNullOrEmpty(normalizedTrack))
            return false;

        foreach (var title in windowTitles)
        {
            if (string.IsNullOrEmpty(title) || !title.Contains(platformName, StringComparison.OrdinalIgnoreCase))
                continue;

            string normalizedTitle = NormalizeForLooseMatch(title);
            if (normalizedTitle.Contains(normalizedTrack, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public static string ExtractTitleFromWindow(string windowTitle, MediaPlatform platform)
    {
        return ExtractTitleFromWindow(windowTitle, platform.ToDisplayString());
    }

    public static unsafe string ExtractTitleFromWindow(string windowTitle, string platformFallback)
    {
        string t = windowTitle ?? string.Empty;
        string fb = platformFallback ?? string.Empty;
        int cap = Math.Max(64, Math.Max(t.Length, fb.Length) + 16);
        Span<char> buf = cap <= 512 ? stackalloc char[cap] : new char[cap];
        fixed (char* pT = t)
        fixed (char* pFb = fb)
        fixed (char* pOut = buf)
        {
            _ = vn_platform_extract_title_from_window(pT, t.Length, pFb, fb.Length, pOut, cap, out int outLen);
            return outLen > 0 ? new string(buf.Slice(0, outLen)) : string.Empty;
        }
    }

    public static unsafe (string Artist, string Track) ParseSpotifyTitle(string title)
    {
        string t = title ?? string.Empty;
        int cap = Math.Max(32, t.Length + 8);
        Span<char> artistBuf = cap <= 512 ? stackalloc char[cap] : new char[cap];
        Span<char> trackBuf = cap <= 512 ? stackalloc char[cap] : new char[cap];
        fixed (char* pT = t)
        fixed (char* pA = artistBuf)
        fixed (char* pTr = trackBuf)
        {
            _ = vn_platform_parse_spotify_title(pT, t.Length, pA, cap, out int aLen, pTr, cap, out int trLen);
            return (aLen > 0 ? new string(artistBuf.Slice(0, aLen)) : string.Empty,
                    trLen > 0 ? new string(trackBuf.Slice(0, trLen)) : string.Empty);
        }
    }

    public static unsafe (string Artist, string Track) ParseSoundCloudTitle(string extractedTitle)
    {
        string t = extractedTitle ?? string.Empty;
        int cap = Math.Max(32, t.Length + 8);
        Span<char> artistBuf = cap <= 512 ? stackalloc char[cap] : new char[cap];
        Span<char> trackBuf = cap <= 512 ? stackalloc char[cap] : new char[cap];
        fixed (char* pT = t)
        fixed (char* pA = artistBuf)
        fixed (char* pTr = trackBuf)
        {
            _ = vn_platform_parse_soundcloud_title(pT, t.Length, pA, cap, out int aLen, pTr, cap, out int trLen);
            return (aLen > 0 ? new string(artistBuf.Slice(0, aLen)) : string.Empty,
                    trLen > 0 ? new string(trackBuf.Slice(0, trLen)) : string.Empty);
        }
    }

    public static unsafe string NormalizeForLooseMatch(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        int cap = value.Length * 4 + 16;
        Span<char> buf = cap <= 512 ? stackalloc char[cap] : new char[cap];
        fixed (char* pIn = value)
        fixed (char* pOut = buf)
        {
            int rc = vn_platform_normalize_for_loose_match(pIn, value.Length, pOut, cap, out int outLen);
            if (rc == -2 && outLen > cap) { char[] larger = new char[outLen]; fixed (char* pL = larger) rc = vn_platform_normalize_for_loose_match(pIn, value.Length, pL, outLen, out outLen); return rc == 0 && outLen > 0 ? new string(larger, 0, outLen) : string.Empty; }
            if (rc != 0) outLen = 0;
            return outLen > 0 ? new string(buf.Slice(0, outLen)) : string.Empty;
        }
    }
}

public enum MediaPlatform
{
    Unknown,
    Spotify,
    YouTube,
    SoundCloud,
    AppleMusic,
    Browser,
    Instagram = 8, // Preserve existing platform IDs; 6 and 7 are retired.
    Twitter,
    Twitch,
    Discord,
    Netflix,
    Tidal,
    Deezer,
    Bandcamp,
    Bilibili,
    Vimeo
}

public static partial class MediaPlatformExtensions
{
    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_platform_parse(char* valPtr, int valLen, out int outPlatform);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_platform_to_display_string(int platform, char* outBuf, int outCap, out int outLen);

    public static unsafe string ToDisplayString(this MediaPlatform platform)
    {
        char* buf = stackalloc char[32];
        _ = vn_platform_to_display_string((int)platform, buf, 32, out int len);
        return len > 0 ? new string(buf, 0, len) : string.Empty;
    }

    public static unsafe MediaPlatform ParsePlatform(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return MediaPlatform.Unknown;

        fixed (char* ptr = value)
        {
            _ = vn_platform_parse(ptr, value.Length, out int p);
            return (MediaPlatform)p;
        }
    }
}
