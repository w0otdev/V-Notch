using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using VNotch.Models;

namespace VNotch.Services;

internal static partial class MediaSourceClassifier
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_media_source_classify_app_id(
        ReadOnlySpan<char> appPtr,
        int appLen,
        out int outPlatform,
        out byte outIsSpotifyPlaying,
        out byte outSetRunning);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_media_source_detect_from_metadata(
        ReadOnlySpan<char> titlePtr,
        int titleLen,
        ReadOnlySpan<char> artistPtr,
        int artistLen,
        ReadOnlySpan<char> albumPtr,
        int albumLen,
        out int outPlatform);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_media_source_detect_from_window_title(
        ReadOnlySpan<char> winTitlePtr,
        int winTitleLen,
        ReadOnlySpan<char> trackLowerPtr,
        int trackLowerLen,
        ReadOnlySpan<char> trackNormPtr,
        int trackNormLen,
        byte hasTrack,
        ReadOnlySpan<char> curTrackPtr,
        int curTrackLen,
        out int outPlatform,
        out byte outMatched,
        Span<char> outBetterTrackBuf,
        int outBetterTrackCap,
        out int outBetterTrackLen);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_media_source_is_junk_title(
        ReadOnlySpan<char> titlePtr,
        int titleLen,
        ReadOnlySpan<char> artistPtr,
        int artistLen,
        out byte outIsJunk);

    public static void ApplyFromAppId(MediaInfo info, string sessionSourceApp)
    {
        if (string.IsNullOrEmpty(sessionSourceApp)) return;

        ReadOnlySpan<char> span = sessionSourceApp.AsSpan();
        vn_media_source_classify_app_id(span, span.Length, out int platVal, out byte isSpotifyPlaying, out byte setRunning);
        var platform = (MediaPlatform)platVal;
        info.MediaSource = platform.ToDisplayString();
        if (isSpotifyPlaying != 0)
            info.IsSpotifyPlaying = true;
        if (setRunning != 0)
        {
            switch (platform)
            {
                case MediaPlatform.Spotify: info.IsSpotifyRunning = true; break;
                case MediaPlatform.YouTube: info.IsYouTubeRunning = true; break;
                case MediaPlatform.Discord: info.IsDiscordRunning = true; break;
                case MediaPlatform.Twitch: info.IsTwitchRunning = true; break;
                case MediaPlatform.Tidal: info.IsTidalRunning = true; break;
                case MediaPlatform.Deezer: info.IsDeezerRunning = true; break;
                case MediaPlatform.AppleMusic: info.IsAppleMusicRunning = true; break;
            }
        }
    }

    public static void RefineFromMetadata(MediaInfo info, string lowerTitle, string lowerArtist, string lowerAlbum)
    {
        if (info.MediaSource != MediaPlatform.Browser.ToDisplayString() && !string.IsNullOrEmpty(info.MediaSource)) return;

        ReadOnlySpan<char> t = (lowerTitle ?? string.Empty).AsSpan();
        ReadOnlySpan<char> a = (lowerArtist ?? string.Empty).AsSpan();
        ReadOnlySpan<char> b = (lowerAlbum ?? string.Empty).AsSpan();

        vn_media_source_detect_from_metadata(t, t.Length, a, a.Length, b, b.Length, out int platVal);
        var detected = (MediaPlatform)platVal;
        if (detected != MediaPlatform.Unknown)
        {
            SetPlatformRunning(info, detected);
        }
    }

    public static void DetectFromWindowTitles(
        MediaInfo info,
        IEnumerable<string> windowTitles,
        string trackTitleLower,
        string trackTitleNormalized,
        bool hasTrack)
    {
        ReadOnlySpan<char> tLower = (trackTitleLower ?? string.Empty).AsSpan();
        ReadOnlySpan<char> tNorm = (trackTitleNormalized ?? string.Empty).AsSpan();
        Span<char> stackBuf = stackalloc char[512];

        foreach (var title in windowTitles)
        {
            if (info.Platform == MediaPlatform.YouTube)
                break;

            if (title == null) continue;
            ReadOnlySpan<char> wSpan = title.AsSpan();
            ReadOnlySpan<char> curTrack = (info.CurrentTrack ?? string.Empty).AsSpan();
            int cap = wSpan.Length + 16;
            Span<char> betterBuf = cap <= 512 ? stackBuf[..cap] : new char[cap];

            vn_media_source_detect_from_window_title(
                wSpan, wSpan.Length,
                tLower, tLower.Length,
                tNorm, tNorm.Length,
                hasTrack ? (byte)1 : (byte)0,
                curTrack, curTrack.Length,
                out int platVal,
                out byte matched,
                betterBuf, betterBuf.Length,
                out int betterLen);

            if (matched != 0)
            {
                SetPlatformRunning(info, (MediaPlatform)platVal);
                if (betterLen > 0)
                {
                    info.CurrentTrack = new string(betterBuf[..betterLen]);
                }
                break;
            }
        }
    }

    internal static void SetPlatformRunning(MediaInfo info, MediaPlatform platform)
    {
        info.IsSpotifyRunning = false;
        info.IsYouTubeRunning = false;
        info.IsDiscordRunning = false;
        info.IsTwitchRunning = false;
        info.IsAppleMusicRunning = false;
        info.IsSoundCloudRunning = false;
        info.IsTidalRunning = false;
        info.IsDeezerRunning = false;
        info.IsBandcampRunning = false;
        info.IsNetflixRunning = false;
        info.IsBilibiliRunning = false;
        info.IsVimeoRunning = false;
        info.IsInstagramRunning = false;
        info.IsTwitterRunning = false;

        info.MediaSource = platform.ToDisplayString();
        switch (platform)
        {
            case MediaPlatform.Spotify: info.IsSpotifyRunning = true; break;
            case MediaPlatform.YouTube: info.IsYouTubeRunning = true; break;
            case MediaPlatform.Discord: info.IsDiscordRunning = true; break;
            case MediaPlatform.Twitch: info.IsTwitchRunning = true; break;
            case MediaPlatform.AppleMusic: info.IsAppleMusicRunning = true; break;
            case MediaPlatform.SoundCloud: info.IsSoundCloudRunning = true; break;
            case MediaPlatform.Tidal: info.IsTidalRunning = true; break;
            case MediaPlatform.Deezer: info.IsDeezerRunning = true; break;
            case MediaPlatform.Bandcamp: info.IsBandcampRunning = true; break;
            case MediaPlatform.Netflix: info.IsNetflixRunning = true; break;
            case MediaPlatform.Bilibili: info.IsBilibiliRunning = true; break;
            case MediaPlatform.Vimeo: info.IsVimeoRunning = true; break;
            case MediaPlatform.Instagram: info.IsInstagramRunning = true; break;
            case MediaPlatform.Twitter: info.IsTwitterRunning = true; break;
            default: break;
        }
    }

    public static bool TryHandleJunkTitle(MediaInfo info, string sessionTitle, string sessionArtist)
    {
        ReadOnlySpan<char> t = (sessionTitle ?? string.Empty).AsSpan();
        ReadOnlySpan<char> a = (sessionArtist ?? string.Empty).AsSpan();
        vn_media_source_is_junk_title(t, t.Length, a, a.Length, out byte isJunk);
        if (isJunk == 0) return false;

        if (info.MediaSource == MediaPlatform.YouTube.ToDisplayString())
        {
            info.CurrentTrack = "";
            info.CurrentArtist = MediaPlatform.YouTube.ToDisplayString();
        }

        return true;
    }
}
